using System.Globalization;
using System.Text.RegularExpressions;

namespace TeamModVersions;

internal sealed record ModEntry(string Id, string Name, string Version);
internal sealed record PlayerColumn(ulong Id, string Name, ModEntry[]? Mods);
internal enum CellKind { Highest, Lower, UnknownVersion, NotReported, NotSynced }
internal sealed record VersionCell(string Text, CellKind Kind);
internal sealed record ModRow(string Id, string Name, int Players, VersionCell[] Cells);
internal sealed record ModSections(ModRow[] Differences, ModRow[] Matches);

internal static class Matrix
{
    internal static ModSections Build(IReadOnlyList<PlayerColumn> players, bool showMatches = false, bool rosterConfirmed = true)
    {
        var all = new Dictionary<string, ModEntry?[]>(StringComparer.Ordinal);
        for (int p = 0; p < players.Count; p++)
        {
            if (players[p].Mods is not { } list) continue;
            foreach (var mod in list)
            {
                if (string.IsNullOrWhiteSpace(mod.Id)) continue;
                if (!all.TryGetValue(mod.Id, out var cells)) all.Add(mod.Id, cells = new ModEntry?[players.Count]);
                cells[p] = mod;
            }
        }
        var rows = all.Select(entry =>
        {
            var (id, values) = entry;
            var versions = values.Select(v => v is null ? null : ModVersion.Parse(v.Version)).ToArray();
            ModVersion? max = null;
            foreach (var v in versions) if (v is not null && (max is null || v.CompareTo(max) > 0)) max = v;
            var cells = new VersionCell[players.Count];
            for (int p = 0; p < players.Count; p++)
            {
                cells[p] = values[p] is null
                    ? players[p].Mods is null ? new("未同步", CellKind.NotSynced) : new("未报告", CellKind.NotReported)
                    : versions[p] is null ? new("版本未知", CellKind.UnknownVersion)
                    : new(values[p]!.Version, versions[p]!.CompareTo(max!) == 0 ? CellKind.Highest : CellKind.Lower);
            }
            var name = values.Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => x!.Name).OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault() ?? id;
            return new ModRow(id, name, values.Count(x => x is not null), cells);
        }).ToArray();
        bool IsMatch(ModRow row) => rosterConfirmed && players.Count > 0 &&
            row.Players == players.Count && row.Cells.All(cell => cell.Kind == CellKind.Highest &&
                string.Equals(cell.Text, row.Cells[0].Text, StringComparison.Ordinal));
        var differences = Sort(rows.Where(row => !IsMatch(row)));
        var matches = showMatches ? Sort(rows.Where(IsMatch)) : Array.Empty<ModRow>();
        return new(differences, matches);
    }

    private static ModRow[] Sort(IEnumerable<ModRow> rows) => rows.OrderByDescending(row => row.Players)
        .ThenByDescending(row => row.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
}

internal sealed record ModVersion(int Major, int Minor, int Patch, int Revision, string[] Pre) : IComparable<ModVersion>
{
    private static readonly Regex Pattern = new(@"^([0-9]+)\.([0-9]+)(?:\.([0-9]+))?(?:\.([0-9]+))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant);

    internal static ModVersion? Parse(string text)
    {
        if (text.Length is 0 or > 128) return null;
        var match = Pattern.Match(text.Trim());
        if (!match.Success) return null;
        var n = new int[4];
        for (int i = 0; i < 4; i++)
            if (match.Groups[i + 1].Success && !int.TryParse(match.Groups[i + 1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
        return new(n[0], n[1], n[2], n[3], match.Groups[5].Success ? match.Groups[5].Value.Split('.') : Array.Empty<string>());
    }

    public int CompareTo(ModVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c == 0) c = Revision.CompareTo(other.Revision);
        if (c != 0) return c;
        if (Pre.Length == 0 || other.Pre.Length == 0) return (other.Pre.Length == 0 ? 0 : 1) - (Pre.Length == 0 ? 0 : 1);
        for (int i = 0; i < Math.Min(Pre.Length, other.Pre.Length); i++)
        {
            var a = Pre[i]; var b = other.Pre[i];
            bool na = a.All(ch => ch is >= '0' and <= '9'), nb = b.All(ch => ch is >= '0' and <= '9');
            if (na && nb)
            {
                a = a.TrimStart('0'); b = b.TrimStart('0');
                c = a.Length.CompareTo(b.Length);
                if (c == 0) c = string.CompareOrdinal(a, b);
            }
            else c = na != nb ? (na ? -1 : 1) : string.CompareOrdinal(a, b);
            if (c != 0) return c;
        }
        return Pre.Length.CompareTo(other.Pre.Length);
    }
}
