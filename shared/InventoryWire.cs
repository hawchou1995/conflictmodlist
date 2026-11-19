using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TeamModVersions;

internal static class InventoryWire
{
    internal const string HeaderKey = "hc.tmv.v1";
    internal const int PartSize = 1800;
    internal const int MaxParts = 16;
    internal const int MaxEntries = 2048;
    private const int MaxJsonBytes = 512 * 1024;
    private static string Digest(string s) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(s)));
    internal static string PartKey(int i) => HeaderKey + "." + i.ToString(CultureInfo.InvariantCulture);

    internal static (string Header, string[] Parts) Encode(ModEntry[] entries)
    {
        if (!Valid(entries)) throw new InvalidDataException("Invalid plugin inventory.");
        var json = JsonSerializer.SerializeToUtf8Bytes(entries);
        if (json.Length > MaxJsonBytes) throw new InvalidDataException("Plugin inventory exceeds the sync budget.");
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(json);
        string payload = Convert.ToBase64String(compressed.ToArray());
        if (payload.Length > PartSize * MaxParts) throw new InvalidDataException("Plugin inventory exceeds the sync budget.");
        int count = (payload.Length + PartSize - 1) / PartSize;
        var parts = new string[count];
        for (int i = 0; i < count; i++) parts[i] = payload.Substring(i * PartSize, Math.Min(PartSize, payload.Length - i * PartSize));
        return (count.ToString(CultureInfo.InvariantCulture) + ":" + Digest(payload), parts);
    }

    internal static bool TryDecode(string header, Func<int,string> readPart, out ModEntry[]? entries)
    {
        entries = null;
        if (header.Length is < 66 or > 67) return false;
        int sep = header.IndexOf(':');
        if (sep is < 1 or > 2 || header.Length - sep != 65 ||
            !int.TryParse(header.AsSpan(0, sep), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 1 or > MaxParts) return false;
        var combined = new StringBuilder(count * PartSize);
        for (int i = 0; i < count; i++)
        {
            string part = readPart(i);
            if (string.IsNullOrEmpty(part) || part.Length > PartSize || (i + 1 < count && part.Length != PartSize)) return false;
            combined.Append(part);
        }
        var payload = combined.ToString();
        if (!string.Equals(Digest(payload), header[(sep + 1)..], StringComparison.Ordinal)) return false;
        try
        {
            using var source = new MemoryStream(Convert.FromBase64String(payload));
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var json = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (json.Length + read > MaxJsonBytes) return false;
                json.Write(buffer, 0, read);
            }
            var decoded = JsonSerializer.Deserialize<ModEntry[]>(json.ToArray());
            if (decoded is null || !Valid(decoded)) return false;
            entries = decoded;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException or InvalidDataException) { return false; }
    }

    private static bool Valid(ModEntry[] rows)
    {
        if (rows.Length > MaxEntries) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
            if (row is null || string.IsNullOrWhiteSpace(row.Id) || row.Id.Length > 512 || !ids.Add(row.Id) ||
                row.Name is null || row.Name.Length > 512 || row.Version is null || row.Version.Length > 128) return false;
        return true;
    }
}
