namespace TeamModVersions;

internal enum PanelLineKind { Header, Row, Hint }

internal readonly record struct PanelLine(PanelLineKind Kind, ModRow? Row, string Text);

/// <summary>
/// 面板的“视图模型”：决定列表里到底有哪些行、哪些是筛选掉的一致项、哪些是采集为空，
/// 以及滚动/翻页的边界。放在 shared 里是为了能被离线测试直接驱动（真实生产逻辑，不是字符串替身）。
/// 规则要点：
///   * 只有一个真人（单机 / 1 真人 + 3 BOT）时没有“队友对比”可言，必须直接列本机已加载插件，
///     不跟随 ShowMatchingMods 配置，否则面板只剩“差异 0”的空壳。
///   * 多人时保留差异视角与“未同步/未报告/版本未知”标记，一致项按当前设置隐藏，
///     并提供一次会话内的“全部/差异”视图切换（不改配置文件、不改同步与排序规则）。
///   * 采集为空（一个插件都没读到）与筛选为空（有数据但一致项被隐藏）必须给出不同提示。
/// </summary>
internal sealed class PanelModel
{
    internal const float RowHeight = 28f;
    internal const string FilterHintPrefix = "差异 0：全部 ";
    internal static readonly PanelModel Blank = new(Array.Empty<PlayerColumn>(),
        new(Array.Empty<ModRow>(), Array.Empty<ModRow>()), Array.Empty<PanelLine>(), false, false, false, false, string.Empty);

    private PanelModel(PlayerColumn[] columns, ModSections sections, PanelLine[] lines,
        bool solo, bool showAll, bool collectionEmpty, bool filterEmpty, string status)
    {
        Columns = columns;
        Sections = sections;
        Lines = lines;
        Solo = solo;
        ShowAll = showAll;
        CollectionEmpty = collectionEmpty;
        FilterEmpty = filterEmpty;
        Status = status;
    }

    internal PlayerColumn[] Columns { get; }
    internal ModSections Sections { get; }
    internal PanelLine[] Lines { get; }
    /// <summary>只有一个真人：不做队友比较，直接列本机清单。</summary>
    internal bool Solo { get; }
    /// <summary>本次视图是否包含“版本一致”的行（配置开关或会话内切换的结果）。</summary>
    internal bool ShowAll { get; }
    /// <summary>采集为空：所有列加起来一个插件都没有。</summary>
    internal bool CollectionEmpty { get; }
    /// <summary>筛选为空：有一致项，但当前视图把它们隐藏了。</summary>
    internal bool FilterEmpty { get; }
    internal string Status { get; }
    internal int Rows => Sections.Differences.Length + Sections.Matches.Length;

    internal static PanelModel Create(PlayerColumn[] columns, bool showMatches, bool rosterConfirmed, string? notice, bool syncError)
    {
        bool solo = columns.Length <= 1;
        // 单真人时无论如何都要能看到本机清单；多人时尊重配置与会话内切换。
        var full = Matrix.Build(columns, showMatches: true, rosterConfirmed);
        bool showAll = solo || showMatches;
        int collected = full.Differences.Length + full.Matches.Length;
        bool collectionEmpty = collected == 0;
        int ready = columns.Count(c => c.Mods is not null);
        var lines = new List<PanelLine>();
        var sections = full;

        if (solo)
        {
            lines.Add(new(PanelLineKind.Header, null, $"本机已加载的 BepInEx 插件 · {collected}"));
            if (collectionEmpty)
                lines.Add(new(PanelLineKind.Hint, null, LocalEmptyHint(columns)));
            foreach (var row in full.Differences) lines.Add(new(PanelLineKind.Row, row, row.Name));
            foreach (var row in full.Matches) lines.Add(new(PanelLineKind.Row, row, row.Name));
            sections = new ModSections(full.Differences, full.Matches);
        }
        else
        {
            lines.Add(new(PanelLineKind.Header, null, $"差异 / 待核对 · {full.Differences.Length}"));
            if (collectionEmpty)
                lines.Add(new(PanelLineKind.Hint, null, SyncEmptyHint(ready)));
            else if (full.Differences.Length == 0)
                lines.Add(new(PanelLineKind.Hint, null, showAll
                    ? "已报告的清单里没有待核对项。"
                    : $"{FilterHintPrefix}{full.Matches.Length} 个已加载插件版本一致，当前设置下隐藏。按 F 查看全部清单。"));
            foreach (var row in full.Differences) lines.Add(new(PanelLineKind.Row, row, row.Name));
            if (showAll && full.Matches.Length > 0)
            {
                lines.Add(new(PanelLineKind.Header, null, $"全队版本一致 · {full.Matches.Length}"));
                foreach (var row in full.Matches) lines.Add(new(PanelLineKind.Row, row, row.Name));
            }
            if (!showAll) sections = new ModSections(full.Differences, Array.Empty<ModRow>());
        }

        bool filterEmpty = !solo && !showAll && full.Differences.Length == 0 && full.Matches.Length > 0;
        string status = syncError
            ? "同步暂不可用，正在重试。"
            : notice ?? (solo
                ? $"本机 {collected} 个插件 · 单机 / BOT 模式不比较队友"
                : $"已同步 {ready}/{columns.Length} 人 · 仅比较已加载的 BepInEx 插件" + (filterEmpty ? " · 按 F 查看全部" : string.Empty));
        return new PanelModel(columns, sections, lines.ToArray(), solo, showAll, collectionEmpty, filterEmpty, status);
    }

    internal static PanelModel Failure()
        => new(Array.Empty<PlayerColumn>(), new ModSections(Array.Empty<ModRow>(), Array.Empty<ModRow>()),
            new[] { new PanelLine(PanelLineKind.Hint, null, "读取大厅模组清单失败，正在重试。") },
            false, false, true, false, "同步暂不可用，正在重试。");

    private static string LocalEmptyHint(PlayerColumn[] columns)
        => columns.Length == 0 || columns[0].Mods is null
            ? "读不到本机 BepInEx 插件列表（采集为空）。"
            : "本机没有已加载的 BepInEx 插件（采集为空）。";

    private static string SyncEmptyHint(int ready)
        => ready == 0
            ? "还没有可用的插件清单（采集为空），请等队友同步或检查本机 BepInEx 插件。"
            : "已同步的人的清单里没有任何插件（采集为空）。";

    internal float ContentHeight => Lines.Length * RowHeight;

    internal float MaxScroll(float viewport) => Math.Max(0f, ContentHeight - Math.Max(0f, viewport));

    internal float Scroll(float current, float delta, float viewport)
        => Math.Clamp(current + delta, 0f, MaxScroll(viewport));

    internal float PageStep(float viewport) => Math.Max(RowHeight, viewport - RowHeight);

    /// <summary>滚动到某个位置时，第一行可见行的下标。</summary>
    internal int FirstVisible(float scroll)
        => Lines.Length == 0 ? 0 : Math.Clamp((int)MathF.Floor(Math.Max(0f, scroll) / RowHeight), 0, Lines.Length - 1);

    /// <summary>滚动到最底时，最后一行必须可见（用于证明“翻到底”可达）。</summary>
    internal int LastVisible(float scroll, float viewport)
    {
        if (Lines.Length == 0) return -1;
        int count = Math.Max(1, (int)MathF.Ceiling(Math.Max(0f, viewport) / RowHeight) + 1);
        return Math.Min(Lines.Length - 1, FirstVisible(scroll) + count);
    }
}
