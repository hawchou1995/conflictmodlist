namespace TeamModVersions;

// What a passive counter can actually see. None means it cannot observe this machine's game
// transport at all, so it carries no numbers. Steam and Game are this game's own payload;
// Host is this machine's interface counters, which cover every local process, so its reading is
// never a peer measurement and may never be published or shown as P2P.
internal enum NetworkSource { None, Steam, Game, Host }
// Cumulative bytes from one source, tagged with that source so a reading always states its origin.
internal readonly record struct NetworkTotal(long Rx, long Tx, NetworkSource Source);

// Select a single payload layer per window. A callback on one direction cannot validate its sibling.
internal sealed class PayloadMeter
{
    private NetworkTotal steamBase,gameBase,lastSteam,lastGame;
    private NetworkSource source;
    internal void Reset(NetworkTotal steam,NetworkTotal game)
    { steamBase=lastSteam=steam; gameBase=lastGame=game; source=NetworkSource.None; }
    internal NetworkTotal Sample(NetworkTotal steam,NetworkTotal game)
    {
        bool steamMoves=steam.Rx>lastSteam.Rx || steam.Tx>lastSteam.Tx;
        bool gameMoves=game.Rx>lastGame.Rx || game.Tx>lastGame.Tx;
        if (steamMoves && (!gameMoves || source!=NetworkSource.Game)) source=NetworkSource.Steam;
        else if (gameMoves) source=NetworkSource.Game;
        lastSteam=steam; lastGame=game;
        if(source==NetworkSource.None) return new(-1,-1,source);
        var (n,b)=source==NetworkSource.Steam ? (steam,steamBase) : (game,gameBase);
        return new(n.Rx>b.Rx ? n.Rx : -1,n.Tx>b.Tx ? n.Tx : -1,source);
    }
}
internal interface INetworkIO
{
    NetworkRoster Roster();
    (int Route, int Estimate) Inspect(ulong peer, bool compatible); // active legacy route for cooperating clients; otherwise cached game Ping only
    // Cumulative game payload bytes. Source == None means the counter cannot see the game transport
    // at all, so its numbers must never be presented as a measured zero.
    NetworkTotal PayloadTotals();
    // Host interface byte totals, a separate labelled fallback for when the process counter is blind.
    // Source == None when no interface can be read. Never a P2P measurement.
    NetworkTotal HostTotals();
    string? Read(NetworkScope scope, ulong author);
    void Write(NetworkScope scope, string text);
    bool Send(ulong peer, byte[] bytes);
    bool Receive(out ulong peer, out byte[] bytes);
    ulong NewEpoch();
}
internal sealed class NetworkService
{
    private sealed class Peer
    {
        internal uint Watermark;
        internal string? Accepted;
        internal NetworkReport? Report;
        internal double ReportAt, Expires;
        internal ulong Epoch;
        internal NetworkLink Link = new();
        internal NetworkEdge Edge;
        internal int PreviousNative = -1;
    }
    private readonly INetworkIO io;
    private readonly Dictionary<ulong,Peer> peers = new();
    private NetworkScope scope;
    private ulong epoch;
    private double now, nextSample, hardFloor, nextPublish, nextDrain, sampled, totalsAt, window;
    private uint published, sampledServerTime, sampleSeq;
    private bool draining, previousOpen, observable, host;
    private NetworkSource source;
    private (long Rx,long Tx) totals = (-1,-1);
    private (long Rx,long Tx) hostTotals = (-1,-1);
    private double hostAt;
    private int rx = -1, tx = -1, hostRx = -1, hostTx = -1;
    internal NetworkService(INetworkIO io) => this.io = io;
    internal int CacheCount => peers.Count;
    internal int MaxHistory => peers.Values.Select(p => p.Link.History).DefaultIfEmpty().Max();

    internal void Reset()
    {
        var previous = scope; bool withdrew = published != 0;
        Clear(); nextSample = 0;
        if (withdrew) io.Write(previous,"-"); // adapter verifies still a member of precisely that session
    }
    private void Clear()
    {
        scope = default; epoch = 0; peers.Clear(); draining = false; published = 0;
        nextPublish = 0; totals = (-1,-1); rx = tx = -1;
        observable = false; window = 0; sampleSeq = 0;
        // A new session must not subtract meters from another session or another source.
        source = NetworkSource.None; host = false; hostTotals = (-1,-1); hostAt = 0; hostRx = hostTx = -1;
    }
    internal void Tick(float time, bool open)
    {
        if (!float.IsFinite(time) || time < 0) return;
        if (time < now) return; // monotonic driver clock: a bad frame cannot trigger a second query
        now = time;
        if (open && !previousOpen) nextSample = hardFloor;
        previousOpen = open;
        if (now >= nextSample && now >= hardFloor)
        {
            hardFloor = now+1; nextSample = now+(open ? 1 : 5); // never catch up, including after Reset or a hitch
            var r = io.Roster();
            if (r.Scope.Self == 0 || r.Scope.Lobby == 0 || r.ServerTime == 0 || r.Members is not {Length: >= 1 and <= NetworkWire.Members} ||
                !r.Members.Contains(r.Scope.Self) || r.Members.Contains(0UL) || r.Members.Distinct().Count() != r.Members.Length)
            { Clear(); return; }
            if (scope != r.Scope) { Clear(); scope = r.Scope; epoch = io.NewEpoch(); }
            foreach (ulong gone in peers.Keys.Where(id => !r.Members.Contains(id)).ToArray()) peers.Remove(gone);
            draining = false;
            foreach (ulong id in r.Members)
            {
                if (id == scope.Self) continue;
                if (!peers.TryGetValue(id,out var p)) peers.Add(id,p = new Peer { Watermark = r.ServerTime });
                Refresh(r,id,p);
                bool compatible = p.Report != null && now <= p.Expires;
                var (route, estimate) = io.Inspect(id,compatible);
                p.Link.Route(route);
                if (route is 1 or 2 && p.Report != null && now <= p.Expires)
                {
                    draining = true;
                    var pulse = p.Link.Next(scope,epoch,p.Epoch,now) with { Issued = r.ServerTime };
                    if (io.Send(id,NetworkWire.Encode(pulse))) p.Link.Sent(pulse.Seq,now);
                }
                else p.Link.Pause();
                if (route == 5 && estimate is >= 0 and <= 10000)
                {
                    // One prior snapshot only. No pulse history survives Pause() on this path.
                    p.Edge = new(id,5,estimate,p.PreviousNative < 0 || now-sampled > NetworkWire.Ttl ? -1 : Math.Abs(estimate-p.PreviousNative),-1,-1);
                    p.PreviousNative = estimate;
                }
                else
                {
                    p.PreviousNative = -1;
                    p.Edge = route == 6 && estimate is > 0 and <= 10000 ? new(id,6,estimate,-1,-1,-1) : p.Link.Edge(id,route,estimate,now);
                }
            }
            var next = io.PayloadTotals();
            // Totals from two different meters never subtract: a source change drops the baseline.
            if (next.Source != source)
            {
                source = next.Source; totals = (-1,-1); rx = tx = -1;
                hostTotals = (-1,-1); hostAt = 0; hostRx = hostTx = -1;
            }
            observable = source != NetworkSource.None;
            // The rate is a differential over the window between two successful samples. An unobservable
            // source carries neither totals nor a delta, so both directions stay unknown instead of 0.
            int Rate(long before,long after,double since) => since > 0 && now > since && before >= 0 && after >= before &&
                (after-before)/(now-since) <= NetworkWire.MaxRate ? after==before ? 0 : Math.Max(1,(int)Math.Round((after-before)/(now-since))) : -1;
            rx = observable ? Rate(totals.Rx,next.Rx,totalsAt) : -1;
            tx = observable ? Rate(totals.Tx,next.Tx,totalsAt) : -1;
            window = sampleSeq > 0 && now > sampled ? now-sampled : 0; // measurement window this sample covers
            totals = observable ? (next.Rx,next.Tx) : (-1,-1);
            totalsAt = sampled = now;
            sampleSeq++;
            // The process counter is blind here, so fall back to this machine's interfaces: a different meter
            // with its own baseline. It never feeds rx/tx and is never published as a peer measurement.
            var hostNext = source == NetworkSource.None ? io.HostTotals() : default;
            host = hostNext.Source == NetworkSource.Host;
            if (host)
            {
                hostRx = Rate(hostTotals.Rx,hostNext.Rx,hostAt);
                hostTx = Rate(hostTotals.Tx,hostNext.Tx,hostAt);
                hostTotals = (hostNext.Rx,hostNext.Tx); hostAt = now;
            }
            else { hostTotals=(-1,-1); hostAt=0; hostRx=hostTx=-1; }
            sampledServerTime = r.ServerTime;
            if (peers.Count > 0 && now >= nextPublish && r.ServerTime > published)
            {
                nextPublish = now+(open ? 4 : 5); published = r.ServerTime;
                io.Write(scope,NetworkWire.Encode(scope,new(epoch,published,rx,tx,peers.Values.Select(p => p.Edge.Route is 3 or 4 or 6 ? new NetworkEdge(p.Edge.Peer,0,-1,-1,-1,-1) : p.Edge).ToArray())));
            }
            else if (peers.Count == 0 && published != 0) { io.Write(scope,"-"); published = 0; }
        }
        if (!draining || now < nextDrain) return;
        nextDrain = now+(open ? 0.05 : 0.20);
        double serverNow = sampledServerTime + (now-sampled);
        for (int i=0; i<4 && io.Receive(out ulong author,out var bytes); ++i)
            if (peers.TryGetValue(author,out var p) && p.Edge.Route is >= 1 and <= 4 && p.Report != null && now <= p.Expires && NetworkWire.Decode(bytes,out var msg) &&
                msg.Issued > 0 && serverNow-msg.Issued is >= -1 and <= NetworkWire.Ttl &&
                msg.Lobby == scope.Lobby && msg.Session == scope.Session && msg.ToEpoch == epoch && msg.FromEpoch == p.Epoch)
                p.Link.Receive(msg,now);
    }

    private void Refresh(NetworkRoster r, ulong id, Peer p)
    {
        string? text = io.Read(scope,id);
        if (p.Report != null && p.Accepted == text && now <= p.Expires && r.ServerTime >= p.Report.Issued &&
            r.ServerTime-p.Report.Issued <= NetworkWire.Ttl && p.Report.Edges.All(e => r.Members.Contains(e.Peer))) return;
        if (NetworkWire.Decode(text,r,id,out var report) && report.Issued > p.Watermark)
        {
            if (report.Epoch != p.Epoch) { p.Link = new(); p.Epoch = report.Epoch; }
            p.Watermark = report.Issued; p.Report = report; p.Accepted = text;
            p.ReportAt = now-(r.ServerTime-report.Issued); p.Expires = p.ReportAt+NetworkWire.Ttl;
        }
        else { p.Report = null; p.Accepted = null; } // never renew a stale or replayed value
    }

    // Age text keeps sub-second resolution and every sample carries a monotonic sequence number and its
    // window: a 1 Hz sampler must never read as a permanent whole "0 seconds", and a reader can tell one
    // fresh sample from another. Only the elapsed time is shown, never an assumed sampling period.
    private static string Num(double value, string format) => value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
    private string Ago(double age) => age < 100 ? $"{Num(age,"0.00")} 秒前" : age < 6000 ? $"{Num(age/60,"0.0")} 分前" : $"{Num(age/3600,"0.0")} 时前";
    private string Stamp() => $"采样 #{sampleSeq}（窗 {Num(window,"0.0")} 秒）· {Ago(now-sampled)}";

    private static string Ms(int n) => n < 0 ? "未知" : $"{n} ms";
    private static string Pct(int n) => n < 0 ? "未知" : Num(n/10.0,"0.#")+"%";
    private static string RoundTrip(NetworkEdge e) => e.Route == 0 || e.Rtt < 0 ? "往返 未知" : e.Route is 1 or 2
        ? $"往返上限 {e.Rtt+1} ms" // polling adds delay; +1 covers rounding the echoed hold and the residual
        : $"往返估值 {Ms(e.Rtt)}";
    private string ProbeNote(Peer peer, NetworkEdge e)
    {
        if (peer.Report == null || now > peer.Expires)
            return peer.Expires > 0 && now > peer.Expires ? "队友报告已过期" : "无近期兼容报告";
        if (e.Peer == 0 && peer.Edge.Route != 0) return "探针读数已过期";
        if (e.Route is 0 or 6) return "已收报告·探针不可用";
        return e.Route is 3 or 4 || (e.Route is 1 or 2 && e.Rtt < 0) ? "等待探针回声" : "";
    }

    internal string[] Lines(ulong selected, Func<ulong,string> name)
    {
        if (scope.Self == 0) return new[]{"网络：等待有效的 Steam 大厅名单"};
        if (now-sampled > NetworkWire.Ttl) return new[]{$"网络：采样已过期（上次采样 #{sampleSeq} {Ago(now-sampled)}）"};
        if (selected == 0) selected = scope.Self;
        string Name(ulong id)
        {
            string s = name(id) ?? ""; var b = new System.Text.StringBuilder(); bool tag = false;
            foreach (char c in s.Take(128))
            {
                if (c == '<') { tag = true; continue; } if (c == '>') { tag = false; continue; }
                if (!tag && !char.IsControl(c) && !char.IsSurrogate(c) && char.GetUnicodeCategory(c) is not
                    (System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)) b.Append(c);
                if (b.Length >= 12) break;
            }
            return b.Length > 0 ? b.ToString() : id == scope.Self ? "本机" : "队友";
        }
        // 0 means a measured zero, -1 unknown, anything below 1 KiB/s keeps its own label instead of rounding to 0.
        string Rate(int n) => n < 0 ? "未知" : n == 0 ? "0 KiB/s" : n < 1024 ? "<1 KiB/s" : $"{Num(n/1024.0,"0.#")} KiB/s";
        var lines = new List<string>{ $"本机流量（{(source == NetworkSource.Game ? "游戏传输载荷" : "进程 P2P 载荷")}，不含本探针，{Stamp()}）", $"接收 {Rate(rx)}　发送 {Rate(tx)}" };
        // Coverage is stated before any number: a blind process counter is unknown, not idle. When it is
        // blind, this machine's interface delta is offered as a separate, differently labelled reading.
        if (!observable) lines.Add(!host ? "统计范围：尚未确认有效载荷回调，收发未知（不含本探针）"
            : hostRx < 0 || hostTx < 0 ? "统计范围：未确认 P2P 载荷回调；主机网卡（非 P2P，整机全部进程）等待首个样本"
            : $"统计范围：未确认 P2P 载荷回调；主机网卡（非 P2P，整机全部进程）接收 {Rate(hostRx)}　发送 {Rate(hostTx)}");
        bool local = selected == scope.Self;
        if (!local && !peers.ContainsKey(selected)) { lines.Add("所选玩家已离开大厅"); return lines.ToArray(); }
        var observer = local ? null : peers[selected];
        var report = observer != null && now <= observer.Expires ? observer.Report : null;
        lines.Add($"观察者：{Name(selected)}");
        lines.Add(local ? source switch
        {
            NetworkSource.Game => $"来源：本机游戏传输 · {Ago(now-sampled)}",
            NetworkSource.Steam => $"来源：本机 Steam P2P · {Ago(now-sampled)}",
            _ => host ? $"来源：本机主机网卡（非 P2P）· {Ago(now-sampled)}" : $"来源：本机 P2P 计数未确认 · {Ago(now-sampled)}",
        } : report == null ? observer!.Expires > 0 && now > observer.Expires ? "来源：队友报告已过期（无新鲜报告）" : "来源：无新鲜的队友报告" : $"来源：队友大厅报告 · {Ago(now-observer!.ReportAt)}");
        // Distinguishes "nobody else here" from "everybody quiet": an empty link list is a roster fact.
        if (local && peers.Count == 0) lines.Add("同大厅其他真人：无");
        if (!local && report != null) lines.Add($"该端接收 {Rate(report.InRate)}　发送 {Rate(report.OutRate)}");
        var edges = local ? peers.Values.Select(p => p.Edge).ToArray() : report?.Edges ?? Array.Empty<NetworkEdge>();
        foreach (ulong id in peers.Keys.Prepend(scope.Self).Where(id => id != selected))
        {
            var e = Array.Find(edges,x => x.Peer == id);
            if (e.Peer != 0 && now - (local ? sampled : observer!.ReportAt) + e.Age > NetworkWire.Ttl) e = default;
            bool probe = e.Route is >= 1 and <= 4;
            string path = e.Route switch {1 => "直连探针", 2 => "中继探针", 3 => "直连探针·游戏缓存", 4 => "中继探针·游戏缓存", 5 => "消息会话", 6 => "游戏缓存", _ => "不可观测"};
            if (e.Route is 3 or 4 or 6) path += "·采样时间未知";
            string note = local ? ProbeNote(peers[id],e) : "";
            if (note.Length > 0) path += "·"+note;
            lines.Add($"→ {Name(id)}（{path}） {RoundTrip(e)}");
            if (probe || e.Route == 5) lines.Add($"{(probe ? "应用波动" : "抖动")} {(e.Jitter < 0 ? "待采样" : Ms(e.Jitter))}　缺收 {Pct(e.Rx)} / 缺发 {Pct(e.Tx)}");
        }
        lines.Add("探针往返显示上限，应用波动包含轮询；探针缺号不等于游戏丢包。");
        return lines.ToArray();
    }

    // Compact per-slot HUD text. Pure read of what Tick already sampled: no roster query, member
    // data read or Steam call, and no SteamID/nick output. Rows stay near twelve ideographs wide.
    // The local slot owns the process P2P counters; a peer slot owns the local->peer direction
    // only, never a third party's perspective and never the peer's self-reported rates.
    internal string SlotText(ulong id, bool local, bool bot)
    {
        if (bot) return "BOT · 网络\n无独立网络连接\n延迟 -- 抖动 -- 丢包 --";
        var lines = new List<string>();
        bool own = local || (id != 0 && id == scope.Self);
        string loss(NetworkEdge e) => e.Rx < 0 && e.Tx < 0 ? "缺收/缺发 未知" : $"缺收 {Pct(e.Rx)}　缺发 {Pct(e.Tx)}";
        // One row per direction: a real sub-KiB/s rate keeps its own B/s unit instead of rounding to a
        // measured 0, and neither row grows past the compact width.
        string flow(int n) => n < 0 ? "未知" : n == 0 ? "0 KiB/s" : n < 1024 ? $"{n} B/s" : $"{Num(n/1024.0, n < 1_048_576 ? "0.#" : "0")} KiB/s";
        bool fresh = scope.Self != 0 && now-sampled <= NetworkWire.Ttl;
        if (own)
        {
            lines.Add(host ? "整机网卡 · 非 P2P" : source==NetworkSource.Game ? "本机 · 游戏载荷" : "本机 · P2P");
            lines.Add($"收 {flow(host ? hostRx : rx)}");
            lines.Add($"发 {flow(host ? hostTx : tx)}");
            lines.Add(scope.Self == 0 ? "大厅名单未就绪" : fresh ? $"采样 #{sampleSeq} · {Ago(now-sampled)}" : $"采样 {Ago(now-sampled)}（过期）");
            // Coverage first, then peer availability. Link metrics belong on the remote seats.
            var notes = new List<string>();
            if (host) notes.Add(hostRx < 0 || hostTx < 0 ? "网卡待采样" : "游戏计数未确认");
            else if (!observable) notes.Add("计数未确认");
            else if (rx < 0 || tx < 0) notes.Add("等待读数");
            if (fresh && peers.Count==0 && Fits(notes,"无共享真人")) notes.Add("无共享真人");
            if (notes.Count > 0) lines.Add(string.Join("·", notes));
            return Join(lines);
        }
        lines.Add("队友");
        if (!fresh) { lines.Add("大厅名单未就绪"); lines.Add("无链路测量"); return Join(lines); }
        if (!peers.TryGetValue(id, out var peer)) { lines.Add("网络名单未确认"); lines.Add("无链路数据"); return Join(lines); }
        var edge = peer.Edge;
        bool stale = edge.Peer != 0 && now-sampled+edge.Age > NetworkWire.Ttl;
        if (edge.Peer == 0 || stale) edge = default; // an expired sample never reads as a measurement
        lines.Add(edge.Route switch
        {
            1 => "本机: 直连探针", 2 => "本机: 中继探针", 3 => "直连探针·缓存时间未知",
            4 => "中继探针·缓存时间未知", 5 => "本机: 消息会话", 6 => "游戏缓存·采样时间未知", _ => "本机: 未测得",
        });
        lines.Add(RoundTrip(edge));
        if (edge.Route is >= 1 and <= 5)
        {
            lines.Add(edge.Jitter < 0 ? "波动 待采样" : $"波动 {Ms(edge.Jitter)}");
            lines.Add(loss(edge)); // Probe gaps can be known before the first usable RTT echo.
        }
        else lines.Add("波动/探针缺号 未知");
        string note = ProbeNote(peer,edge);
        if (note.Length > 0) lines.Add(note);
        return Join(lines);
    }
    private static int Columns(string s)
    {
        int w = 0;
        foreach (char c in s) w += c >= 0x2E80 || c is >= '\u3000' and <= '\u303F' || c is >= '\uFF00' and <= '\uFFEF' ? 2 : 1;
        return w;
    }
    // A second note is dropped rather than wrapped: the card has no seventh row.
    private static bool Fits(List<string> notes,string extra) => Columns(string.Join("·",notes.Append(extra))) <= 24;
    private static string Join(List<string> lines) => string.Join("\n",lines);
}
