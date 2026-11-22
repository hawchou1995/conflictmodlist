namespace TeamModVersions;

// One lobby's own member-data slot. A void setter is not an acknowledgement.
internal sealed class RouteMarkerPublication
{
    internal const string Withdrawn = "-";
    internal enum Result { None, Visible, Missing }

    private long nextCheck;
    private string? requested;
    private Result observed;
    internal bool HasPendingOrVisibleData => requested != null;

    internal Result Sync(long now, string? marker, Func<string?> read, Action<string> write)
    {
        if ((marker == null && requested == null) || now < nextCheck) return Result.None;
        nextCheck = now + 6000;
        string desired = marker ?? Withdrawn;
        string? actual = read();
        bool same = desired == requested;
        Result previous = same ? observed : Result.None;
        if (actual == desired || (marker == null && actual == ""))
        {
            requested = marker;
            observed = marker == null ? Result.None : Result.Visible;
        }
        else
        {
            // The first write can be asynchronous. Only a repeated mismatch or an unreadable key is a stall.
            requested = desired;
            observed = same || actual == null ? Result.Missing : Result.None;
            if (actual != null) write(desired); // null means the lobby/member is no longer answerable
        }
        return observed != previous ? observed : Result.None;
    }

    internal void Reset()
    {
        nextCheck = 0;
        requested = null;
        observed = Result.None;
    }
}
