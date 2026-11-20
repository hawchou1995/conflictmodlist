namespace TeamModVersions;

// Adapt NetworkService.Lines' observer-oriented text to one compact column. The local-rate
// preamble is repeated for every observer; never attribute it to a remote reporter. Keep the
// backend's values, units (in the shared legend), age and unknowns; do not estimate from text.
internal static class HudCard
{
    internal const int Rows = 5; // source/age + game payload + at most three directed edges
    internal const string Legend = "队伍网络  |  进程 P2P 载荷 KiB/s（不含探针）  |  往返 / 波动 ms：探针报往返上限，应用波动含轮询  |  探针缺收 / 缺发 %  |  — 未知";
    internal static int Fill(string[] lines, bool local, string[,] cells, int card)
    {
        int row = 2;
        cells[card, 0] = "来源未就绪";
        cells[card, 1] = "载荷  收 —   发 —";
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("来源：", StringComparison.Ordinal)) cells[card, 0] = line[3..];
            else if ((local && line.StartsWith("接收 ", StringComparison.Ordinal)) ||
                     (!local && line.StartsWith("该端接收 ", StringComparison.Ordinal)))
                cells[card, 1] = "载荷  " + line.Replace("该端", "").Replace("接收", "收").Replace("发送", "发")
                    .Replace(" KiB/s", "").Replace("未知", "—");
            else if (line.StartsWith("→ ", StringComparison.Ordinal) && row < Rows)
            {
                string pathAndRtt = line.Replace("（", " ").Replace("） ", "    ").Replace(" ms", "");
                string rest = "/—    —/—";
                if (i + 1 < lines.Length && (lines[i + 1].StartsWith("抖动 ", StringComparison.Ordinal) || lines[i+1].StartsWith("应用波动 ",StringComparison.Ordinal)))
                    rest = lines[++i].Replace("应用波动 ", "/应用 ").Replace("抖动 ", "/").Replace(" ms", "").Replace("　缺收 ", "    ")
                        .Replace(" / 缺发 ", "/").Replace("%", "").Replace("未知", "—");
                cells[card, row++] = pathAndRtt + rest;
            }
        }
        if (local && row == 2) cells[card, row++] = "无其他真人链路";
        return row;
    }
}
