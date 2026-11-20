namespace TeamModVersions;

// One sequence per successful unreliable send. Echo is piggybacked, never an immediate reply.
// Only the sender's local clock is used for RTT; subtract the receiver's *duration*, not its time.
internal sealed class NetworkLink
{
    private readonly (uint Seq, double Time)[] sent = new (uint, double)[12];
    private readonly Queue<(uint Seq, double Time)> arrivals = new();
    private readonly Queue<(int Ms, double Time)> rtts = new();
    private uint txSeq, lastRx, lastAck;
    private double lastArrival;
    private int remoteLoss = -1;
    private int currentRoute;
    internal void Route(int route) { if (currentRoute != route) { Pause(); currentRoute = route; } }
    // No native call on the unconfirmed-peer path. Missing/out-of-range is unknown, never zero RTT.
    internal static (int Route,int Estimate) CachedEstimate(int ping) => ping is > 0 and <= 10_000 ? (6,ping) : (0,-1);
    internal int History => sent.Length + arrivals.Count + rtts.Count; // <= 60 slots total
    internal void Pause()
    {
        arrivals.Clear(); rtts.Clear(); lastAck = txSeq; remoteLoss = -1;
        lastArrival = double.NegativeInfinity; // retain sequence high-water marks, not measurements
    }

    internal NetworkPulse Next(NetworkScope scope, ulong from, ulong to, double now)
    {
        double hold = (now-lastArrival)*1000;
        uint ack = hold is >= 0 and <= 10000 ? lastRx : 0;
        return new(scope.Lobby, scope.Session, from, to, txSeq + 1, ack, ack == 0 ? 0 : (int)Math.Round(hold), ReceiveLoss(now));
    }
    internal void Sent(uint seq, double now) { txSeq = seq; sent[seq % (uint)sent.Length] = (seq, now); }
    internal void Receive(NetworkPulse p, double now)
    {
        if (p.Seq <= lastRx) return; // ignore duplicates/out of order (quality tracks on-time arrivals)
        lastRx = p.Seq; lastArrival = now; remoteLoss = p.Loss;
        Push(arrivals,(p.Seq,now));
        if (p.Ack <= lastAck || p.Ack > txSeq) return;
        var pending = sent[p.Ack % (uint)sent.Length];
        if (pending.Seq != p.Ack) return;
        lastAck = p.Ack;
        double elapsed = now-pending.Time, rtt = elapsed*1000-p.HoldMs;
        if (rtts.Count > 0 && now-rtts.Last().Time > NetworkWire.Ttl) rtts.Clear();
        if (elapsed is >= 0 and <= 12 && rtt is >= -0.5 and <= 10000) Push(rtts,((int)Math.Max(0,Math.Round(rtt)),now));
    }
    internal NetworkEdge Edge(ulong peer, int route, int gameEstimate, double now)
    {
        Trim(rtts,now); Trim(arrivals,now);
        if (route == 0) return new(peer,0,-1,-1,-1,-1);
        int rx = ReceiveLoss(now), tx = now-lastArrival <= NetworkWire.Ttl ? remoteLoss : -1;
        if (rtts.Count == 0 || now-rtts.Last().Time > NetworkWire.Ttl)
            return gameEstimate is > 0 and <= 10000 ? new(peer,route+2,gameEstimate,-1,rx,tx) : new(peer,route,-1,-1,rx,tx);
        int prev=-1, sum=0;
        foreach (var sample in rtts) { if (prev >= 0) sum += Math.Abs(sample.Ms-prev); prev=sample.Ms; }
        return new(peer,route,prev,rtts.Count < 2 ? -1 : (int)Math.Round((double)sum/(rtts.Count-1)),rx,tx,(int)Math.Ceiling(now-rtts.Last().Time));
    }
    private int ReceiveLoss(double now)
    {
        Trim(arrivals,now);
        if (arrivals.Count < 3 || now-lastArrival > NetworkWire.Ttl) return -1;
        double expected = (double)arrivals.Last().Seq-arrivals.Peek().Seq+1;
        return (int)Math.Round(1000*(1-arrivals.Count/expected));
    }
    private static void Push<T>(Queue<T> q,T sample) { if (q.Count == 24) q.Dequeue(); q.Enqueue(sample); }
    private static void Trim<T>(Queue<(T Value,double Time)> q,double now)
    { while (q.Count != 0 && now-q.Peek().Time > 60) q.Dequeue(); }
}
