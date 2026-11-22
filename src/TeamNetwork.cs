using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SNetwork;
using Steamworks;
using System.Net.NetworkInformation;

namespace TeamModVersions;

// Managed driver-owned service. No MonoBehaviour, IL2CPP class registration or game RPC registration.
internal sealed class TeamNetwork
{
    private readonly SteamIO io = new();
    private readonly NetworkService service;
    private float retryAt;
    internal TeamNetwork() => service = new(io);
    internal void Tick(float now, bool panelOpen)
    {
        if (now < retryAt) return;
        try { service.Tick(now,panelOpen); }
        catch (Exception e) { retryAt = now+5; Reset(); Plugin.Logger.LogDebug($"网络采样暂不可用：{e.GetType().Name}"); }
    }
    internal void Reset()
    {
        try { service.Reset(); } catch (Exception) { /* Steam may already be shut down. Member data expires. */ }
        io.Release();
    }
    internal string[] Lines(ulong selected, Func<ulong,string> name) => service.Lines(selected,name);
    // Compact single-slot text for the read-only HUD. Pure snapshot formatter: no Steam call, no I/O.
    internal string SlotText(ulong id, bool local, bool bot) => service.SlotText(id,local,bot);

    private sealed class SteamIO : INetworkIO
    {
        // Dedicated non-game channel. Do not accept or open sessions, change relay settings or touch game queues.
        private const int Channel = 0x544d5601;
        private readonly Dictionary<ulong,(int GamePing,int RouteMs)> estimates = new();
        private Il2CppStructArray<byte>? buffer;
        private Harmony? counters;
        private bool unavailable;
        // Two independent passive payload meters, plus one host meter. The Steam meter sees the legacy P2P
        // calls the game makes for its own transport (SNet_Core_STEAM::SendBytes and the message thread),
        // the SNet meter counts the same payload at the game's own send/receive entry points, and the host
        // meter counts this machine's interfaces. Only one is reported at a time, and a published report
        // only ever carries the game's own numbers: the host meter is not P2P and never leaves the process.
        private static long steamRx, steamTx, gameRx, gameTx;
        private readonly PayloadMeter meter = new();
        private NetworkScope meterScope;
        private static NetworkTotal SteamBytes() => new(Interlocked.Read(ref steamRx),Interlocked.Read(ref steamTx),NetworkSource.Steam);
        private static NetworkTotal GameBytes() => new(Interlocked.Read(ref gameRx),Interlocked.Read(ref gameTx),NetworkSource.Game);
        private static NetworkInterface[]? interfaces;
        private static long interfacesAt;
        private string? interfaceSet;
        private NetworkSource announced;
        private static bool hostAnnounced;
        private readonly RouteMarkerPublication routePublication = new();
        private ulong routeLobby;
        private bool routeMetadataWarned;
        private static NetworkScope Current()
        {
            if (!SNet.IsInLobby || SNet.CoreType != SNet_CoreType.Steam || SNet.Lobby?.Identifier is not {} id ||
                SNet.SessionHub is not {} hub || SNet.LocalPlayer is not {} self) return default;
            ulong steam = SteamUser.GetSteamID().m_SteamID;
            return steam != 0 && self.Lookup == steam ? new(id.ID,unchecked((uint)hub.SessionID),steam) : default;
        }
        public NetworkRoster Roster()
        {
            estimates.Clear();
            var scope = Current();
            if (scope.Lobby == 0) { Release(); return default; }
            var lobby = new CSteamID(scope.Lobby);
            int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
            if (n is < 1 or > NetworkWire.Members) { Release(); return default; }
            var ids = new ulong[n];
            for (int i=0;i<n;i++) ids[i] = SteamMatchmaking.GetLobbyMemberByIndex(lobby,i).m_SteamID;
            if (!ids.Contains(scope.Self) || ids.Contains(0UL) || ids.Distinct().Count() != n) { Release(); return default; }
            if (scope != meterScope) { meter.Reset(SteamBytes(),GameBytes()); meterScope = scope; announced=NetworkSource.None; }
            PublishRoute(scope);
            var players = SNet.LobbyPlayers;
            if (players != null && players.Count <= 32)
                for (int i=0;i<players.Count;i++)
                    if (players[i] is {} p && !p.IsBot && !p.IsLocal)
                    {
                        ulong author = p.Lookup;
                        int member = Array.IndexOf(ids,author);
                        if (member >= 0 && author != scope.Self)
                            estimates[author] = (p.Ping,ReadRoute(lobby,p,author,member+1));
                    }
            return new(scope,ids,SteamUtils.GetServerRealTime());
        }
        private void PublishRoute(NetworkScope scope)
        {
            if (routeLobby != scope.Lobby)
            {
                routeLobby = scope.Lobby;
                routePublication.Reset();
                SteamRouteEstimate.BeginLobby();
            }
            try
            {
                var lobby = new CSteamID(scope.Lobby);
                string? marker = SteamRouteEstimate.Local();
                var result = routePublication.Sync(Environment.TickCount64,marker,
                    () => SteamMatchmaking.GetLobbyMemberData(lobby,new CSteamID(scope.Self),SteamRouteEstimate.Key),
                    value => SteamMatchmaking.SetLobbyMemberData(lobby,SteamRouteEstimate.Key,value));
                if (result == RouteMarkerPublication.Result.Visible)
                    Plugin.Logger.LogInfo($"[Steam 路由估值] 本机大厅标记已回读（{marker!.Length} 字符）；尚未确认队友收到");
                else if (result == RouteMarkerPublication.Result.Missing)
                    Plugin.Logger.LogInfo($"[Steam 路由估值] 本机大厅标记尚未确认{(marker == null ? "撤回" : "发布")}，将继续限频检查和重试");
            }
            catch (Exception e) { WarnRouteMetadata(e); }
        }
        private int ReadRoute(CSteamID lobby, SNet_Player player, ulong author, int member)
        {
            string? marker = null;
            try { marker = SteamMatchmaking.GetLobbyMemberData(lobby,new CSteamID(author),SteamRouteEstimate.Key); }
            catch (Exception e) { WarnRouteMetadata(e); }
            return SteamRouteEstimate.Read(player,author,member,marker);
        }
        private void WarnRouteMetadata(Exception e)
        {
            if (routeMetadataWarned) return;
            routeMetadataWarned = true;
            Plugin.Logger.LogWarning($"Steam 路由位置的大厅数据读写失败：{e.GetType().Name}；原往返读数不受影响。");
        }
        public int RouteEstimate(ulong peer) => estimates.TryGetValue(peer,out var reading) ? reading.RouteMs : -1;
        public (int Route,int Estimate) Inspect(ulong peer, bool compatible)
        {
            if (!estimates.TryGetValue(peer,out var reading)) return (0,-1);
            int ping = reading.GamePing;
            // Joining unconfirmed peers must not construct a SteamNetworkingIdentity or query Messages.
            if (!compatible) return NetworkLink.CachedEstimate(ping);
            if (!SteamNetworking.GetP2PSessionState(new CSteamID(peer),out var state) ||
                state.m_bConnectionActive == 0 || state.m_eP2PSessionError != 0) return NetworkLink.CachedEstimate(ping);
            // Legacy state has neither an HSteamNetConnection nor RTT/loss/rates. Never read its address fields.
            return (state.m_bUsingRelay == 0 ? 1 : 2,ping);
        }
        public string? Read(NetworkScope scope,ulong author) => SteamMatchmaking.GetLobbyMemberData(new CSteamID(scope.Lobby),new CSteamID(author),NetworkWire.Key);
        public void Write(NetworkScope scope,string text)
        {
            if (scope.Lobby != 0 && Current() == scope) SteamMatchmaking.SetLobbyMemberData(new CSteamID(scope.Lobby),NetworkWire.Key,text);
        }
        public ulong NewEpoch()
        {
            ulong n;
            do { n = BitConverter.ToUInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)); } while (n == 0);
            return n;
        }
        public bool Send(ulong peer,byte[] bytes) => SteamNetworking.SendP2PPacket(new CSteamID(peer),bytes,(uint)bytes.Length,EP2PSend.k_EP2PSendUnreliableNoDelay,Channel);
        public bool Receive(out ulong peer,out byte[] bytes)
        {
            peer = 0; bytes = Array.Empty<byte>();
            // Oversized packets must not be interpreted as valid truncated pulses. No allocation from packet size.
            buffer ??= new Il2CppStructArray<byte>(NetworkWire.PulseSize+1);
            if (!SteamNetworking.ReadP2PPacket(buffer,(uint)buffer.Length,out uint n,out var sender,Channel)) return false;
            if (n == NetworkWire.PulseSize) { peer = sender.m_SteamID; bytes = new byte[n]; for (int i=0;i<bytes.Length;i++) bytes[i]=buffer[i]; }
            return true;
        }
        public NetworkTotal PayloadTotals()
        {
            if (unavailable) return new(-1,-1,NetworkSource.None);
            if (counters == null && !Install()) return new(-1,-1,NetworkSource.None);
            var total = meter.Sample(SteamBytes(),GameBytes());
            if (total.Source != announced)
            {
                announced = total.Source;
                if (total.Source != NetworkSource.None)
                    Plugin.Logger.LogInfo($"网络统计来源：{(total.Source == NetworkSource.Steam ? "Steam P2P 载荷" : "游戏传输载荷")}");
            }
            return total;
        }
        // Host interface totals: every process on this machine, no packet capture, no privilege, no new
        // traffic. The interface list is cached for 30 s so a 1 Hz sample never rescans the whole machine.
        public NetworkTotal HostTotals()
        {
            long now = Environment.TickCount64;
            var nics = interfaces;
            if (nics == null || now-interfacesAt >= 30_000)
            {
                interfacesAt = now;
                try { nics = NetworkInterface.GetAllNetworkInterfaces(); } catch (Exception) { nics = Array.Empty<NetworkInterface>(); }
                interfaces = nics;
            }
            long rx = 0, tx = 0; bool any = false;
            var activeIds = new List<string>();
            foreach (var nic in nics)
            {
                try
                {
                    if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel || nic.OperationalStatus != OperationalStatus.Up) continue;
                    var s = nic.GetIPStatistics(); var r=s.BytesReceived; var t=s.BytesSent;
                    if(r<0 || t<0)continue;
                    activeIds.Add(nic.Id); rx+=r; tx+=t; any=true;
                }
                catch (Exception) { /* adapter vanished between enumeration and read */ }
            }
            activeIds.Sort(StringComparer.Ordinal);
            var set=string.Join(";",activeIds);
            if (interfaceSet!=set) { interfaceSet=set; return new(-1,-1,any ? NetworkSource.Host : NetworkSource.None); }
            if (!any) return new(-1,-1,NetworkSource.None);
            if (!hostAnnounced) { hostAnnounced = true; Plugin.Logger.LogInfo("网络统计来源：主机网卡（非 P2P，整机全部进程）"); }
            return new(rx,tx,NetworkSource.Host);
        }
        private bool Install()
        {
            var harmony = new Harmony(Plugin.Id+".network.bytes");
            bool any=false;
            void Try(System.Reflection.MethodInfo? target,string method)
            {
                try { any |= Count(harmony,target,method); }
                catch (Exception e) { Plugin.Logger.LogWarning($"流量计数 {method} 不可用：{e.GetType().Name}"); }
            }
            Try(AccessTools.Method(typeof(SteamNetworking),nameof(SteamNetworking.ReadP2PPacket)),nameof(AfterRead));
            Try(AccessTools.Method(typeof(SteamNetworking),nameof(SteamNetworking.SendP2PPacket)),nameof(AfterSend));
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(SNet_Core_STEAM)).Where(m => m.Name == nameof(SNet_Core_STEAM.SendBytes)))
                Try(m,nameof(AfterGameSend));
            Try(AccessTools.Method(typeof(SNet_Replication),nameof(SNet_Replication.RecieveBytes)),nameof(AfterGameReceive));
            counters=harmony; unavailable=!any;
            return any;
        }
        private static bool Count(Harmony harmony, System.Reflection.MethodInfo? game, string after)
        {
            if (game == null) return false;
            var patch = AccessTools.Method(typeof(SteamIO),after);
            return patch != null && harmony.Patch(game,postfix:new HarmonyMethod(patch)) != null;
        }
        private static void AfterRead(bool __result,uint pcubMsgSize,int nChannel)
        { if (__result && nChannel!=Channel && pcubMsgSize>0) Interlocked.Add(ref steamRx,pcubMsgSize); }
        private static void AfterSend(bool __result,uint cubData,int nChannel)
        { if (__result && nChannel!=Channel && cubData>0) Interlocked.Add(ref steamTx,cubData); }
        // SNet never carries this mod's probe, so no channel filter is needed. Each SendBytes overload has
        // its own SteamNetworking call site, so nothing is counted twice by these three postfixes.
        private static void AfterGameSend(Il2CppStructArray<byte>? bytes)
        { if (bytes!=null && bytes.Length>0) Interlocked.Add(ref gameTx,bytes.Length); }
        private static void AfterGameReceive(uint size)
        { if (size>0) Interlocked.Add(ref gameRx,size); }
        internal void Release()
        {
            if (counters!=null) { try { counters.UnpatchSelf(); } finally { counters=null; } }
            meterScope=default; announced=NetworkSource.None;
            if (routePublication.HasPendingOrVisibleData)
            {
                try
                {
                    var lobby = Current().Lobby;
                    if (lobby == routeLobby)
                        SteamMatchmaking.SetLobbyMemberData(new CSteamID(lobby),SteamRouteEstimate.Key,RouteMarkerPublication.Withdrawn);
                }
                catch (Exception e) { WarnRouteMetadata(e); }
            }
            estimates.Clear(); buffer = null; routePublication.Reset(); routeLobby = 0; SteamRouteEstimate.Reset();
        }
    }
}
