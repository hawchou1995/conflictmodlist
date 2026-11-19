using System.Text;

namespace TeamModVersions;

/// <summary>与引擎无关的 8 位颜色（不含 alpha：表头只画不透明文字）。</summary>
internal readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>一段表头文字 + 可选颜色。<c>null</c> = 用 GUIStyle 自己的默认色（大厅的“默认色”不硬编码进表头）。</summary>
internal readonly record struct NameRun(string Text, Rgb? Color);

/// <summary>大厅原生 TMP 已排版的一个可见字符（来自 TMP_CharacterInfo：字符 + 实际颜色）。</summary>
internal readonly record struct NameChar(string Text, Rgb Color);

/// <summary>
/// 大厅玩家名的“安全颜色分段”：
///   * <see cref="FromRendered"/> 优先吃原生 TMP 已排版字符（逐字符实际颜色、大厅改写后的大小写/截断）；
///   * <see cref="Parse"/> 是 TMP 原文（可能带标签）的后备解析，只认 TMP 的一小组标签，其余按字面保留；
///   * <see cref="Resolve"/> 按玩家 ID 取名字（不同身份/重排不串色），本地玩家补“（你）”且不染色。
/// 输出是“文字 + 颜色”结构，不做 IMGUI 富文本：远端昵称永远不进入 richText 通道。
/// </summary>
internal static class NameMarkup
{
    internal const string YouSuffix = "（你）";
    internal const int MaxRuns = 256;
    internal const int MaxChars = 512;

    // TMP 里常见且“只是排版、不产出可见字符”的标签：整条丢掉（含闭合形式）。
    private static readonly HashSet<string> LayoutTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "b", "i", "u", "s", "size", "sprite", "align", "indent", "line", "margin", "mark", "mspace",
        "pos", "space", "voffset", "cspace", "font", "gradient", "link", "material", "page", "br",
        "nobr", "style", "noparse", "sub", "sup", "strikethrough", "underline", "smallcaps",
    };

    private static readonly Dictionary<string, Rgb> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = new(0xFF, 0xFF, 0xFF), ["black"] = new(0x00, 0x00, 0x00),
        ["red"] = new(0xFF, 0x00, 0x00), ["green"] = new(0x00, 0x80, 0x00), ["blue"] = new(0x00, 0x00, 0xFF),
        ["yellow"] = new(0xFF, 0xFF, 0x00), ["cyan"] = new(0x00, 0xFF, 0xFF), ["aqua"] = new(0x00, 0xFF, 0xFF),
        ["magenta"] = new(0xFF, 0x00, 0xFF), ["fuchsia"] = new(0xFF, 0x00, 0xFF), ["gray"] = new(0x80, 0x80, 0x80),
        ["grey"] = new(0x80, 0x80, 0x80), ["silver"] = new(0xC0, 0xC0, 0xC0), ["maroon"] = new(0x80, 0x00, 0x00),
        ["olive"] = new(0x80, 0x80, 0x00), ["lime"] = new(0x00, 0xFF, 0x00), ["teal"] = new(0x00, 0x80, 0x80),
        ["navy"] = new(0x00, 0x00, 0x80), ["purple"] = new(0x80, 0x00, 0x80), ["orange"] = new(0xFF, 0xA5, 0x00),
        ["brown"] = new(0xA5, 0x2A, 0x2A), ["pink"] = new(0xFF, 0xC0, 0xCB),
    };

    // ---------------------------------------------------------------- 渲染结果优先

    /// <summary>把原生 TMP 的逐字符结果合成安全分段：同色合并，丢掉空串/控制字符/孤立代理项。</summary>
    internal static NameRun[] FromRendered(IReadOnlyList<NameChar> chars)
    {
        var runs = new List<NameRun>();
        var buffer = new StringBuilder();
        Rgb? current = null;
        int written = 0;
        for (int i = 0; i < chars.Count && runs.Count < MaxRuns && written < MaxChars; i++)
        {
            var piece = chars[i];
            string text = piece.Text;
            if (string.IsNullOrEmpty(text) || Droppable(text)) continue;
            if (current is { } have && !have.Equals(piece.Color)) Flush(runs, buffer, current);
            current = piece.Color;
            int room = MaxChars - written;
            if (text.Length > room) text = SafePrefix(text, room);
            if (text.Length == 0) break;
            buffer.Append(text);
            written += text.Length;
        }
        Flush(runs, buffer, current);
        return runs.ToArray();
    }

    // ---------------------------------------------------------------- 原文后备解析

    /// <summary>
    /// 解析 TMP 原文里的一小组标签：颜色（<c>&lt;#rgb&gt;</c>/<c>&lt;#rgba&gt;</c>/<c>&lt;#rrggbb&gt;</c>/<c>&lt;#rrggbbaa&gt;</c>、
    /// <c>&lt;color=…&gt;</c>、<c>&lt;/color&gt;</c>）、大小写（uppercase / lowercase）。其它已知排版标签整条丢掉，
    /// 不认识的 <c>&lt;…&gt;</c> 按字面保留（表头不开 richText，字面尖括号安全）。嵌套/未闭合都不抛错。
    /// </summary>
    internal static NameRun[] Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return Array.Empty<NameRun>();
        var runs = new List<NameRun>();
        var buffer = new StringBuilder();
        var colors = new List<Rgb?>();
        int upper = 0, lower = 0;
        Rgb? buffered = null;
        bool bufferedUpper = false, bufferedLower = false;
        int i = 0;
        while (i < raw.Length && runs.Count < MaxRuns)
        {
            char c = raw[i];
            if (c == '<' && TryReadTag(raw, i, out int next, out string inner))
            {
                ApplyTag(inner, colors, ref upper, ref lower);
                i = next;
                // 颜色/大小写状态变了就先落盘，否则整段会串到最后一种颜色上。
                var now = Current(colors);
                bool upperNow = upper > 0 && lower == 0, lowerNow = lower > 0 && upper == 0;
                if (!Nullable.Equals(now, buffered) || upperNow != bufferedUpper || lowerNow != bufferedLower)
                {
                    Flush(runs, buffer, buffered, bufferedUpper, bufferedLower);
                    buffered = now;
                    bufferedUpper = upperNow;
                    bufferedLower = lowerNow;
                }
                continue;
            }
            if (char.IsHighSurrogate(c) && i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]))
            {
                buffer.Append(c).Append(raw[i + 1]);
                i += 2;
                continue;
            }
            if (char.IsSurrogate(c) || (char.IsControl(c) && c != ' '))
            {
                i++;
                continue;
            }
            buffer.Append(c);
            i++;
        }
        Flush(runs, buffer, buffered, bufferedUpper, bufferedLower);
        return runs.ToArray();
    }

    private static void ApplyTag(string inner, List<Rgb?> colors, ref int upper, ref int lower)
    {
        if (inner.Length == 0) return;
        bool closing = inner[0] == '/';
        string body = closing ? inner[1..] : inner;
        int eq = body.IndexOf('=');
        string name = (eq < 0 ? body : body[..eq]).Trim();
        string value = eq < 0 ? string.Empty : body[(eq + 1)..].Trim();

        if (!closing && name.Length > 1 && name[0] == '#')
        {
            if (TryHex(name[1..], out var direct)) colors.Add(direct);
            return;
        }
        if (name.Equals("color", StringComparison.OrdinalIgnoreCase))
        {
            if (closing || eq < 0)
            {
                if (colors.Count > 0) colors.RemoveAt(colors.Count - 1);
                return;
            }
            if (value.Length > 1 && value[0] == '#' && TryHex(value[1..], out var hex)) { colors.Add(hex); return; }
            if (Named.TryGetValue(value, out var named)) { colors.Add(named); return; }
            colors.Add(null); // 未知颜色名：回到默认色，而不是把整串当标签丢掉
            return;
        }
        if (name.Equals("uppercase", StringComparison.OrdinalIgnoreCase))
        {
            upper = closing ? Math.Max(0, upper - 1) : upper + 1;
            return;
        }
        if (name.Equals("lowercase", StringComparison.OrdinalIgnoreCase))
        {
            lower = closing ? Math.Max(0, lower - 1) : lower + 1;
        }
    }

    /// <summary>3/4/6/8 位 hex：短写按位重复展开（fa0 → FFAA00），4/8 位丢掉 alpha。</summary>
    private static bool TryHex(string hex, out Rgb rgb)
    {
        rgb = default;
        if (hex.Length is not (3 or 4 or 6 or 8)) return false;
        for (int i = 0; i < hex.Length; i++)
            if (!Uri.IsHexDigit(hex[i])) return false;
        if (hex.Length is 3 or 4)
        {
            int r = Convert.ToInt32(hex[0].ToString(), 16), g = Convert.ToInt32(hex[1].ToString(), 16), b = Convert.ToInt32(hex[2].ToString(), 16);
            rgb = new((byte)(r * 17), (byte)(g * 17), (byte)(b * 17));
            return true;
        }
        rgb = new((byte)Convert.ToInt32(hex.Substring(0, 2), 16), (byte)Convert.ToInt32(hex.Substring(2, 2), 16), (byte)Convert.ToInt32(hex.Substring(4, 2), 16));
        return true;
    }

    /// <summary>识别 <c>&lt;…&gt;</c> 是否为我们认识的标签；不认识就当字面文本。</summary>
    private static bool TryReadTag(string raw, int start, out int next, out string inner)
    {
        next = start;
        inner = string.Empty;
        int end = raw.IndexOf('>', start + 1);
        if (end < 0 || end - start > 64) return false;
        string body = raw.Substring(start + 1, end - start - 1);
        if (body.Length == 0) return false;
        bool closing = body[0] == '/';
        string name = closing ? body[1..] : body;
        int eq = name.IndexOf('=');
        string key = (eq < 0 ? name : name[..eq]).Trim();
        if (key.Length == 0) return false;
        bool known = (key.Length > 1 && key[0] == '#') ||
                     key.Equals("color", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("uppercase", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("lowercase", StringComparison.OrdinalIgnoreCase) ||
                     LayoutTags.Contains(key);
        if (!known) return false;
        next = end + 1;
        inner = body;
        return true;
    }

    // ---------------------------------------------------------------- 组合 / 取名

    /// <summary>
    /// 按玩家 ID 取表头分段：优先原生渲染结果，其次昵称原文解析，最后才是 <paramref name="fallbackName"/>。
    /// 本地玩家（与 <paramref name="localId"/> 相等）末尾补“（你）”：该段独立、不染色。
    /// 关键区分：大厅列表（LobbyInventory）给本地列**预拼**了一个“（你）”，它属于 UI 而不是原名内容，
    /// 所以在解析回退名之前先把这一段剥掉再解析，最后作为不染色段补回；这样名字末尾的颜色（例如整段红字）
    /// 不会染到后缀上，而玩家自己的原名真以“（你）”结尾时（尤其非本机）只剥 UI 那一段、原文里的那段保留。
    /// </summary>
    internal static NameRun[] Resolve(ulong id, ulong localId, IReadOnlyDictionary<ulong, NameRun[]>? rendered, string? fallbackName)
    {
        bool local = localId != 0 && id == localId;
        NameRun[]? native = null;
        if (rendered != null && rendered.TryGetValue(id, out var found) && found.Length > 0) native = found;
        string? text = fallbackName;
        if (native is null && local && text is not null && text.EndsWith(YouSuffix, StringComparison.Ordinal))
            text = text[..^YouSuffix.Length];   // 只剥大厅预拼的那一段
        NameRun[] runs = native ?? (!string.IsNullOrEmpty(text) ? Parse(text) : Array.Empty<NameRun>());
        return local ? AppendSuffix(runs, YouSuffix) : runs;
    }

    /// <summary>追加一段不染色文字（例如“（你）”），不改变前面各段颜色。</summary>
    internal static NameRun[] AppendSuffix(NameRun[] runs, string suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return runs;
        var list = new List<NameRun>(runs.Length + 1);
        list.AddRange(runs);
        if (list.Count > 0 && list[^1].Color is null)
            list[^1] = list[^1] with { Text = list[^1].Text + suffix };
        else
            list.Add(new NameRun(suffix, null));
        return list.ToArray();
    }

    internal static string Plain(NameRun[] runs)
    {
        var sb = new StringBuilder();
        foreach (var run in runs) sb.Append(run.Text);
        return sb.ToString();
    }

    /// <summary>按 UTF-16 安全截断（不切开代理对），用于超长名字。</summary>
    internal static NameRun[] Truncate(NameRun[] runs, int maxChars = MaxChars)
    {
        if (maxChars <= 0) return Array.Empty<NameRun>();
        var list = new List<NameRun>(runs.Length);
        int left = maxChars;
        foreach (var run in runs)
        {
            if (left <= 0) break;
            string text = run.Text.Length > left ? SafePrefix(run.Text, left) : run.Text;
            if (text.Length == 0) break;
            list.Add(run with { Text = text });
            left -= text.Length;
        }
        return list.ToArray();
    }

    internal static string SafePrefix(string text, int maxChars)
    {
        if (maxChars <= 0) return string.Empty;
        if (text.Length <= maxChars) return text;
        int cut = maxChars;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut];
    }

    // ---------------------------------------------------------------- 小工具

    private static Rgb? Current(List<Rgb?> colors) => colors.Count == 0 ? null : colors[^1];

    private static void Flush(List<NameRun> runs, StringBuilder buffer, Rgb? color, bool upper = false, bool lower = false)
    {
        if (buffer.Length == 0) return;
        string text = buffer.ToString();
        buffer.Clear();
        if (upper && !lower) text = text.ToUpperInvariant();
        else if (lower && !upper) text = text.ToLowerInvariant();
        if (text.Length == 0 || runs.Count >= MaxRuns) return;
        runs.Add(new NameRun(text, color));
    }

    private static bool Droppable(string text)
    {
        if (text.Length == 2 && char.IsHighSurrogate(text[0]) && char.IsLowSurrogate(text[1])) return false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsSurrogate(c)) return true;
            if (char.IsControl(c) && c != ' ') return true;
        }
        return false;
    }
}
