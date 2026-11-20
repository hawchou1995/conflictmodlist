using System.Buffers.Binary;

namespace TeamModVersions;

internal readonly record struct NetworkScope(ulong Lobby, uint Session, ulong Self);
internal readonly record struct NetworkRoster(NetworkScope Scope, ulong[] Members, uint ServerTime);
// Rtt/Jitter in ms; Rx/Tx are missing unreliable telemetry pulses per mille, NOT game packet loss.
internal readonly record struct NetworkEdge(ulong Peer, int Route, int Rtt, int Jitter, int Rx, int Tx, int Age = 0)
{
    internal bool Valid => Age is >= 0 and <= NetworkWire.Ttl && Route is >= 0 and <= 6 && Rtt is >= -1 and <= 10000 && Jitter is >= -1 and <= 10000 &&
        Rx is >= -1 and <= 1000 && Tx is >= -1 and <= 1000 && (Rtt >= 0 || Jitter == -1) &&
        (Route != 0 || (Rtt, Jitter, Rx, Tx) == (-1, -1, -1, -1)) && (Route is not (3 or 4 or 6) || (Jitter == -1 && Rtt > 0)) &&
        (Route != 5 || Rtt >= 0) && (Route < 5 || (Rx, Tx) == (-1, -1));
}
internal sealed record NetworkReport(ulong Epoch, uint Issued, int InRate, int OutRate, NetworkEdge[] Edges);
internal readonly record struct NetworkPulse(ulong Lobby, uint Session, ulong FromEpoch, ulong ToEpoch, uint Seq, uint Ack, int HoldMs, int Loss, uint Issued = 0);

internal static class NetworkWire
{
    internal const string Key = "tmv.net2";
    internal const int Members = 4, Ttl = 15, MaxRate = 1_000_000_000, PulseSize = 52;
    private const uint ReportMagic = 0x32524e54, PulseMagic = 0x32504e54;
    private const int Header = 44, Row = 18;
    internal static string Encode(NetworkScope scope, NetworkReport report)
    {
        var b = new byte[Header + Row * report.Edges.Length];
        W(b, 0, ReportMagic); W(b, 4, scope.Lobby); W(b, 12, scope.Session); W(b, 16, scope.Self);
        W(b, 24, report.Epoch); W(b, 32, report.Issued); W(b, 36, (uint)report.InRate); W(b, 40, (uint)report.OutRate);
        int i = Header;
        foreach (var e in report.Edges)
        {
            W(b, i, e.Peer); b[i + 8] = (byte)e.Route;
            Short(b, i + 9, e.Rtt); Short(b, i + 11, e.Jitter); Short(b, i + 13, e.Rx); Short(b, i + 15, e.Tx); b[i+17] = (byte)e.Age; i += Row;
        }
        return Convert.ToBase64String(b);
    }

    internal static bool Decode(string? text, NetworkRoster roster, ulong author, out NetworkReport report)
    {
        report = null!;
        if (text == null || text.Length > 132 || text.Length < 60 || !roster.Members.Contains(author) || author == roster.Scope.Self) return false;
        Span<byte> b = stackalloc byte[Header + Row * (Members - 1)];
        if (!Convert.TryFromBase64String(text, b, out int n) || n < Header || (n-Header) % Row != 0 ||
            U(b, 0) != ReportMagic || L(b, 4) != roster.Scope.Lobby || U(b, 12) != roster.Scope.Session || L(b, 16) != author) return false;
        ulong epoch = L(b, 24); uint issued = U(b, 32); int rx = (int)U(b, 36), tx = (int)U(b, 40);
        if (epoch == 0 || issued == 0 || issued > roster.ServerTime || roster.ServerTime - issued > Ttl ||
            rx is < -1 or > MaxRate || tx is < -1 or > MaxRate) return false;
        var edges = new NetworkEdge[(n-Header) / Row];
        for (int j = 0, i = Header; j < edges.Length; ++j, i += Row)
        {
            var e = new NetworkEdge(L(b,i), b[i+8], S(b,i+9), S(b,i+11), S(b,i+13), S(b,i+15), b[i+17]);
            if (!e.Valid || e.Peer == author || !roster.Members.Contains(e.Peer) || edges.Take(j).Any(x => x.Peer == e.Peer)) return false;
            edges[j] = e;
        }
        report = new(epoch, issued, rx, tx, edges); return true;
    }

    internal static byte[] Encode(NetworkPulse p)
    {
        var b = new byte[PulseSize]; W(b,0,PulseMagic); W(b,4,p.Lobby); W(b,12,p.Session); W(b,16,p.FromEpoch); W(b,24,p.ToEpoch);
        W(b,32,p.Seq); W(b,36,p.Ack); W(b,40,(uint)p.HoldMs); W(b,44,(uint)p.Loss); W(b,48,p.Issued); return b;
    }
    internal static bool Decode(ReadOnlySpan<byte> b, out NetworkPulse p)
    {
        p = default;
        if (b.Length != PulseSize || U(b,0) != PulseMagic) return false;
        p = new(L(b,4),U(b,12),L(b,16),L(b,24),U(b,32),U(b,36),(int)U(b,40),(int)U(b,44),U(b,48));
        return p.Seq != 0 && p.HoldMs is >= 0 and <= 10000 && p.Loss is >= -1 and <= 1000 && (p.Ack != 0 || p.HoldMs == 0);
    }
    private static uint U(ReadOnlySpan<byte> b, int i) => BinaryPrimitives.ReadUInt32LittleEndian(b[i..]);
    private static ulong L(ReadOnlySpan<byte> b, int i) => BinaryPrimitives.ReadUInt64LittleEndian(b[i..]);
    private static short S(ReadOnlySpan<byte> b, int i) => BinaryPrimitives.ReadInt16LittleEndian(b[i..]);
    private static void W(byte[] b,int i,uint n) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i),n);
    private static void W(byte[] b,int i,ulong n) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(i),n);
    private static void Short(byte[] b,int i,int n) => BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i),(short)n);
}
