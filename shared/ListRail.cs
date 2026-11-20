namespace TeamModVersions;

// Track, thumb and hit regions all use the same coordinate system (local when painting,
// screen when interacting). Keeping the grab offset prevents jumps on the first frame.
internal readonly record struct ListRail(ScreenRect Track, ScreenRect Thumb, float Maximum)
{
    internal bool Visible => Maximum > 0 && Track.Height > Thumb.Height;
    internal static ListRail Measure(ScreenRect list, float content, float scroll)
    {
        if (!(content > list.Height && list.Width >= 16 && list.Height > 2)) return default;
        var track = new ScreenRect(list.X + list.Width - 16, list.Y, 16, list.Height);
        float maximum = content - list.Height;
        float height = Math.Clamp(list.Height * (list.Height / content), Math.Min(24, list.Height / 2), list.Height);
        float travel = list.Height - height;
        return new(track, new(track.X, track.Y + travel * Math.Clamp(scroll / maximum, 0, 1), track.Width, height), maximum);
    }
}

internal sealed class RailDrag
{
    private readonly ClosePressGate edge = new();
    private float grab;
    internal bool Held { get; private set; }
    internal void Reset() { edge.Reset(); Held = false; }
    internal bool Sample(ListRail rail, CursorSample cursor, bool down, ref float scroll)
    {
        if (!cursor.Valid || !rail.Visible) { Reset(); return false; }
        bool start = edge.Sample(down, PanelHit.Inside(rail.Track, cursor.X, cursor.Y));
        if (!down) { Held = false; return false; }
        if (start)
        {
            Held = true;
            grab = PanelHit.Inside(rail.Thumb, cursor.X, cursor.Y) ? cursor.Y - rail.Thumb.Y : rail.Thumb.Height / 2;
        }
        if (!Held) return false;
        float travel = rail.Track.Height - rail.Thumb.Height;
        scroll = Math.Clamp((cursor.Y - rail.Track.Y - grab) / travel, 0, 1) * rail.Maximum;
        return true;
    }
}
