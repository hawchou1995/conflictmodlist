using TeamModVersions;

int passes = 0;
void Check(bool result, string message)
{
    if (!result) throw new Exception(message);
    Console.WriteLine($"PASS {++passes}: {message}");
}
ModEntry M(string id, string name, string version = "1.0.0") => new(id, name, version);
PlayerColumn[] team = {
    new(1, "A", new[] { M("m1","MatchA"), M("m2","MatchZ"), M("v","X","1.10.0"), M("s","Z"), M("same.a","A"), M("build","Build","1.0.0+one"), M("unknown","Zed", "manual") }),
    new(2, "B", new[] { M("m1","MatchA"), M("m2","MatchZ"), M("v","X","1.9.0"), M("s","Z"), M("same.b","A"), M("build","Build","1.0.0+two"), M("unknown","Zed", "manual") }),
    new(3, "C", new[] { M("m1","MatchA"), M("m2","MatchZ"), M("v","X","1.10.0"), M("build","Build","1.0.0+one"), M("unknown","Zed", "manual") }),
};
var hidden = Matrix.Build(team);
var shown = Matrix.Build(team, showMatches: true);
Check(hidden.Matches.Length == 0 && hidden.Differences.All(x => x.Id != "m1" && x.Id != "m2"), "matches hidden by default");
Check(shown.Matches.Select(x => x.Id).SequenceEqual(new[] { "m2", "m1" }), "optional matches independently sorted Z-A");
Check(shown.Differences.Select(x => x.Id).SequenceEqual(new[] { "unknown", "v", "build", "s", "same.a", "same.b" }), "differences sorted by count, then name Z-A, then ID");
Check(shown.Differences.Select(x => x.Id).SequenceEqual(hidden.Differences.Select(x => x.Id)), "toggle preserves difference order");
var compare = shown.Differences.Single(x => x.Id == "v");
Check(compare.Cells.Select(c => c.Kind).SequenceEqual(new[] { CellKind.Highest, CellKind.Lower, CellKind.Highest }), "numeric comparison and tied highest");
Check(shown.Differences.Single(x => x.Id == "s").Cells[2].Kind == CellKind.NotReported, "synced but missing is not reported");
Check(shown.Differences.Single(x => x.Id == "unknown").Cells.All(x => x.Kind == CellKind.UnknownVersion), "unparseable versions cannot be matches");
var missing = Matrix.Build(team.Append(new(4, "No plugin", null)).ToArray(), showMatches: true);
Check(missing.Matches.Length == 0 && missing.Differences.All(x => x.Cells[3].Kind == CellKind.NotSynced), "unsynced host or peer prevents matches");
Check(Matrix.Build(team, showMatches: true, rosterConfirmed: false).Matches.Length == 0, "unconfirmed roster prevents hiding");
Check(ModVersion.Parse("1.0.0-rc.10")!.CompareTo(ModVersion.Parse("1.0.0-rc.2")) > 0 && ModVersion.Parse("1.0.0")!.CompareTo(ModVersion.Parse("1.0.0-rc.10")) > 0, "prerelease comparison");

var random = new Random(203);
var items = Enumerable.Range(0, 150).Select(i => M($"test.{i}", "插件 " + string.Concat(Enumerable.Range(0, 32).Select(_ => (char)random.Next('a', 'z' + 1))), "1.10.0")).ToArray();
var wire = InventoryWire.Encode(items);
Check(wire.Parts.Length > 1 && InventoryWire.TryDecode(wire.Header, i => wire.Parts[i], out var result) && result!.SequenceEqual(items), "multipart Unicode round trip");
Check(!InventoryWire.TryDecode(wire.Header, i => i == 0 ? "" : wire.Parts[i], out _), "missing part rejected");
Check(!InventoryWire.TryDecode(wire.Header, i => i == 0 ? (wire.Parts[i][0] == 'a' ? "b" : "a") + wire.Parts[i][1..] : wire.Parts[i], out _), "digest mismatch rejected");
var empty = InventoryWire.Encode(Array.Empty<ModEntry>());
Check(InventoryWire.TryDecode(empty.Header, i => empty.Parts[i], out var noMods) && noMods!.Length == 0, "valid empty inventory is not unsynced");

var clientA = new LobbyInventory();
var clientB = new LobbyInventory();
SNetwork.SNet.SetLobby(100, 1, 2, 3);
SNetwork.SNet.PlayAs(2, "1.10.0");
var aBefore = clientA.Sample(0);
Check(aBefore.Single(x => x.Id == 1).Mods is null && aBefore.Single(x => x.Id == 3).Mods is null, "first reporting guest does not guess host or other guest data");
SNetwork.SNet.PlayAs(3, "1.9.0");
var bAfter = clientB.Sample(0);
SNetwork.SNet.PlayAs(2, "1.10.0");
var aAfter = clientA.Sample(1);
Check(bAfter.Single(x => x.Id == 2).Mods!.Single().Version == "1.10.0" &&
      aAfter.Single(x => x.Id == 3).Mods!.Single().Version == "1.9.0" &&
      aAfter.Single(x => x.Id == 1).Mods is null && bAfter.Single(x => x.Id == 1).Mods is null,
      "guest-to-guest exchange without reporting host (simulated Steam)");
var host = new LobbyInventory();
SNetwork.SNet.PlayAs(1, "1.10.0");
Check(host.Sample(2).Count(x => x.Mods is not null) == 3, "reporting host sees all reporting members (simulated Steam)");
SNetwork.SNet.SetLobby(200, 1, 2, 3);
SNetwork.SNet.PlayAs(2, "1.10.0");
Check(clientA.Sample(3).Count(x => x.Mods is not null) == 1, "lobby change clears prior session inventory");

// ---- 单真人 + BOT：必须直接读到本机真实已加载插件（旧视图会把这些全隐藏） ----
var soloPlugins = new[] { ("mod.aaa", "A 模组", "1.2.3"), ("mod.bbb", "B 模组", "2.0.0"), ("mod.ccc", "C 模组", "0.9.0") };
SNetwork.SNet.SetLobby(300, 5);
SNetwork.SNet.PlayAs(5, soloPlugins);
SNetwork.SNet.SetBots(3);
var soloInventory = new LobbyInventory();
var soloColumns = soloInventory.Sample(0);
Check(soloColumns.Length == 1 && soloColumns[0].Mods!.Length == 3, "单真人 + 3 BOT：只给 1 个真人列，且带本机 3 个已加载插件");
Check(soloInventory.RosterConfirmed, "单真人 + 3 BOT：BOT 不在 Steam 名单里，名单判定为已确认");
var soloOld = Matrix.Build(soloColumns, showMatches: false, rosterConfirmed: true);
Check(soloOld.Differences.Length == 0 && soloOld.Matches.Length == 0, "旧视图（ShowMatchingMods=false）在单真人下把本机插件归为一致项并隐藏 —— 空面板的根因");
var soloModel = PanelModel.Create(soloColumns, showMatches: false, rosterConfirmed: true, notice: soloInventory.Notice, syncError: false);
Check(soloModel.Solo && soloModel.Lines.Count(l => l.Kind == PanelLineKind.Row) == 3, "单真人视图：忽略 ShowMatchingMods，直接列出本机 3 个插件");
Check(!soloModel.CollectionEmpty && !soloModel.FilterEmpty, "单真人视图既不是采集为空也不是筛选为空");
var soloRows = soloModel.Lines.Where(l => l.Kind == PanelLineKind.Row).ToDictionary(l => l.Row!.Name, l => l.Row!.Cells.Single().Text);
Check(soloRows.Count == 3 &&
      soloRows["A 模组"] == "1.2.3" && soloRows["B 模组"] == "2.0.0" && soloRows["C 模组"] == "0.9.0",
    "单真人视图逐个插件对上真实名称与版本号");

// ---- 多人：保留差异视角；一致项隐藏时必须给出“怎么看到全部” ----
var sameTeam = new[]
{
    new PlayerColumn(1, "A", new[] { M("m1", "Alpha"), M("m2", "Beta") }),
    new PlayerColumn(2, "B", new[] { M("m1", "Alpha"), M("m2", "Beta") }),
    new PlayerColumn(3, "C", new[] { M("m1", "Alpha"), M("m2", "Beta") }),
};
var multiOld = Matrix.Build(sameTeam, showMatches: false, rosterConfirmed: true);
Check(multiOld.Differences.Length == 0 && multiOld.Matches.Length == 0, "旧视图：全队一致时差异 0 且一致项被隐藏 —— 面板空壳");
var filtered = PanelModel.Create(sameTeam, showMatches: false, rosterConfirmed: true, notice: null, syncError: false);
Check(!filtered.Solo && filtered.FilterEmpty && filtered.Lines.All(l => l.Kind != PanelLineKind.Row), "多人默认视图：只显示差异，并把“有一致项但被隐藏”标为筛选为空");
Check(filtered.Lines.Any(l => l.Kind == PanelLineKind.Hint && l.Text.Contains("按 F")), "筛选为空时给出按键提示，而不是让人以为没有数据");
var allRows = PanelModel.Create(sameTeam, showMatches: true, rosterConfirmed: true, notice: null, syncError: false);
Check(allRows.Lines.Count(l => l.Kind == PanelLineKind.Row) == 2 &&
      allRows.Lines.Any(l => l.Kind == PanelLineKind.Header && l.Text.StartsWith("全队版本一致", StringComparison.Ordinal)),
    "同一会话切到“全部”能看到全部清单（不改配置文件）");
Check(!allRows.FilterEmpty && allRows.ShowAll, "“全部”视图不再报筛选为空");

// ---- 采集为空 与 筛选为空 必须区分开 ----
var emptyLocal = new[] { new PlayerColumn(1, "独狼", Array.Empty<ModEntry>()) };
var emptyModel = PanelModel.Create(emptyLocal, showMatches: false, rosterConfirmed: true, notice: null, syncError: false);
Check(emptyModel.CollectionEmpty && !emptyModel.FilterEmpty, "一个插件都没有 = 采集为空，不是筛选为空");
Check(emptyModel.Lines.Any(l => l.Kind == PanelLineKind.Hint && l.Text.Contains("采集为空", StringComparison.Ordinal)), "采集为空有独立提示文案");
var noPlugins = PanelModel.Create(new[] { new PlayerColumn(1, "A", Array.Empty<ModEntry>()), new PlayerColumn(2, "B", Array.Empty<ModEntry>()) },
    showMatches: false, rosterConfirmed: true, notice: null, syncError: false);
Check(noPlugins.CollectionEmpty && noPlugins.Lines.Any(l => l.Kind == PanelLineKind.Hint && l.Text.Contains("采集为空", StringComparison.Ordinal)),
    "多人但所有人都没有插件，也是采集为空");

// ---- 多人缺数据标记不能被新视图冲掉 ----
var unsynced = PanelModel.Create(new[]
{
    new PlayerColumn(1, "A", new[] { M("x", "X") }),
    new PlayerColumn(2, "B", null),
    new PlayerColumn(3, "C", new[] { M("x", "X") }),
}, showMatches: true, rosterConfirmed: false, notice: null, syncError: false);
Check(unsynced.Sections.Differences[0].Cells[1].Kind == CellKind.NotSynced, "多人未同步列仍是“未同步”，不会被当成一致");
Check(unsynced.Lines.Any(l => l.Kind == PanelLineKind.Row && l.Row!.Cells.Any(c => c.Kind == CellKind.NotSynced)),
    "“未同步”标记真的进了要渲染的行数据");
Check(unsynced.Lines.Any(l => l.Kind == PanelLineKind.Row && l.Row!.Cells.Any(c => c.Kind == CellKind.NotReported)) == false,
    "没报告和没同步没有混为一谈");

// ---- 清单必须能滚/翻到底：IMGUI 鼠标不可假设可用 ----
var manyMods = Enumerable.Range(0, 40).Select(i => M($"many.{i:D2}", $"插件 {i:D2}", "1.0.0")).ToArray();
var scrollModel = PanelModel.Create(new[] { new PlayerColumn(1, "本机", manyMods) }, showMatches: false, rosterConfirmed: true, notice: null, syncError: false);
const float viewport = 510f;
Check(scrollModel.Lines.Length == 41, "40 个插件 = 1 行标题 + 40 行，全部可寻址");
Check(scrollModel.MaxScroll(viewport) > 0f, "内容高于视口时必须可滚动");
var top = scrollModel.Scroll(0f, -PanelModel.RowHeight, viewport);
Check(top == 0f, "在顶部继续上滚不越界");
var bottom = 0f;
for (int i = 0; i < 40; i++) bottom = scrollModel.Scroll(bottom, scrollModel.PageStep(viewport), viewport);
Check(bottom == scrollModel.MaxScroll(viewport), "连续翻页后停在底部且不越界");
Check(scrollModel.LastVisible(bottom, viewport) == scrollModel.Lines.Length - 1, "翻到底时最后一行可见（清单读得到底）");
Check(scrollModel.LastVisible(0f, viewport) < scrollModel.Lines.Length - 1, "顶部时最后一行不可见（对照，证明上一条不是恒真）");
Check(scrollModel.MaxScroll(5000f) == 0f, "视口比内容高时不需要滚动");

// ---- 关闭按钮落点：2560x1440 下面板右上角在屏幕右上象限 ----
const float screenW = 2560f, screenH = 1440f;
var panelW = MathF.Min(1180f, screenW - 44f);
var panelH = MathF.Min(660f, screenH - 56f);
var panel = new ScreenRect((screenW - panelW) * .5f, (screenH - panelH) * .5f, panelW, panelH);
var closeRect = new ScreenRect(panel.X + panel.Width - 18f - 64f, panel.Y + 13f, 64f, 29f);
var closeCenter = PanelAnchor.Center(closeRect, screenH);
Check(MathF.Abs(closeCenter.X - 1820f) < 0.001f, "关闭按钮中心 x = 面板右上（2560 宽）");
Check(MathF.Abs(closeCenter.Y - 1022.5f) < 0.001f, "IMGUI 左上原点翻成屏幕左下原点");
var unitOffset = PanelAnchor.CanvasOffset(closeCenter, screenW, screenH, 1f);
Check(MathF.Abs(unitOffset.X - 540f) < 0.001f && MathF.Abs(unitOffset.Y - 302.5f) < 0.001f, "相对屏幕中心的画布偏移（scaleFactor=1）");
var hdOffset = PanelAnchor.CanvasOffset(closeCenter, screenW, screenH, 4f / 3f);
Check(MathF.Abs(hdOffset.X - 405f) < 0.001f && MathF.Abs(hdOffset.Y - 226.875f) < 0.001f, "高分屏按 Canvas.scaleFactor 折算");
Check(hdOffset.X > 0f && hdOffset.Y > 0f, "关闭落点在屏幕中心右上方 = 面板右上");
var origin = PanelAnchor.CanvasOffset(new ScreenPoint(screenW * .5f, screenH * .5f), screenW, screenH, 4f / 3f);
Check(origin.X == 0f && origin.Y == 0f, "屏幕中心对应画布中心（面板居中的前提）");
Check(PanelAnchor.CanvasOffset(closeCenter, screenW, screenH, 0f).X == unitOffset.X, "scaleFactor 非法时退回 1");

// ---- 表头名字/颜色：大厅 TMP 方言 → 安全颜色分段（richText 关闭，按段画） ----
var quoted = NameMarkup.Parse("<#fa0>\"<#0ff>Demo<#f00>\"");
Check(quoted.Length == 3 &&
      quoted[0].Text == "\"" && quoted[0].Color == new Rgb(0xFF, 0xAA, 0x00) &&
      quoted[1].Text == "Demo" && quoted[1].Color == new Rgb(0x00, 0xFF, 0xFF) &&
      quoted[2].Text == "\"" && quoted[2].Color == new Rgb(0xFF, 0x00, 0x00),
    "短 #RGB 展开：首引号 #FFAA00 / 名字 #00FFFF / 末引号 #FF0000");
Check(NameMarkup.Plain(quoted) == "\"Demo\"", "标签本身不进可见文本");

// 原生 TMP 已排版字符优先：大厅把名字画成 DEMO（大写），逐字符颜色来自 characterInfo
var rendered = NameMarkup.FromRendered(new[]
{
    new NameChar("\"", new Rgb(0xFF, 0xAA, 0x00)),
    new NameChar("D", new Rgb(0x00, 0xFF, 0xFF)),
    new NameChar("E", new Rgb(0x00, 0xFF, 0xFF)),
    new NameChar("M", new Rgb(0x00, 0xFF, 0xFF)),
    new NameChar("O", new Rgb(0x00, 0xFF, 0xFF)),
    new NameChar("\"", new Rgb(0xFF, 0x00, 0x00)),
});
Check(rendered.Length == 3 && NameMarkup.Plain(rendered) == "\"DEMO\"", "渲染结果保留大厅大写并合并同色段");
Check(rendered[0].Color == new Rgb(0xFF, 0xAA, 0x00) && rendered[1].Color == new Rgb(0x00, 0xFF, 0xFF) && rendered[2].Color == new Rgb(0xFF, 0x00, 0x00),
    "渲染结果逐段颜色与大厅一致");
Check(NameMarkup.FromRendered(new[] { new NameChar("😀", new Rgb(1, 2, 3)), new NameChar("\uD83D", new Rgb(1, 2, 3)), new NameChar("\n", new Rgb(1, 2, 3)) }) is var cleaned &&
      cleaned.Length == 1 && NameMarkup.Plain(cleaned) == "😀", "渲染结果里丢掉孤立代理项与控制字符，Emoji 完整");

// 按 ID 取名：不同身份 / 重排都不串名串色；“（你）”只给本地且不染色
var byId = new Dictionary<ulong, NameRun[]>
{
    [11] = rendered,
    [22] = NameMarkup.Parse("<#0f0>Other"),
};
var mine = NameMarkup.Resolve(11, 11, byId, "不该用到");
var theirs = NameMarkup.Resolve(22, 11, byId, "不该用到");
Check(NameMarkup.Plain(mine) == "\"DEMO\"（你）" && mine[^1].Color is null, "本机名追加未染色的“（你）”");
Check(NameMarkup.Plain(theirs) == "Other" && theirs[^1].Color == new Rgb(0x00, 0xFF, 0x00), "别人的名字不追加“（你）”，末段颜色保持");
var orderA = new[] { 11UL, 22UL }.Select(id => NameMarkup.Plain(NameMarkup.Resolve(id, 11, byId, "x"))).ToArray();
var orderB = new[] { 22UL, 11UL }.Select(id => NameMarkup.Plain(NameMarkup.Resolve(id, 11, byId, "x"))).ToArray();
Check(orderA.SequenceEqual(new[] { "\"DEMO\"（你）", "Other" }) && orderB.SequenceEqual(new[] { "Other", "\"DEMO\"（你）" }),
    "同一批玩家换顺序，名字与颜色都跟着自己的 ID");
Check(NameMarkup.Plain(NameMarkup.Resolve(22, 22, byId, "x")) == "Other（你）", "本机换人后“（你）”跟着本地 ID 走");

// 回退链：没有原生 → 解析 TMP 原文 → 才用原始昵称
var fromText = NameMarkup.Resolve(33, 11, byId, "<#fff>Raw<#f00>Name");
Check(NameMarkup.Plain(fromText) == "RawName" && fromText.Length == 2, "没有原生数据时解析 TMP 原文标签");
var fromNick = NameMarkup.Resolve(44, 11, null, "Plain Name");
Check(NameMarkup.Plain(fromNick) == "Plain Name" && fromNick.Length == 1 && fromNick[0].Color is null, "最后才用原始昵称，且不染色");
Check(NameMarkup.Resolve(55, 11, byId, null).Length == 0, "既没原生也没昵称时给空分段，不抛错");

// 解析边界：嵌套/未闭合/落单闭合/字面尖括号/截断/长短 hex/具名色/中文/Emoji
var nested = NameMarkup.Parse("<color=#FF0000>a<color=#00FF00>b</color>c</color>");
var unclosed = NameMarkup.Parse("<#f00>abc");
Check(NameMarkup.Plain(nested) == "abc" && nested.Select(r => r.Color).SequenceEqual(new Rgb?[] { new Rgb(0xFF, 0, 0), new Rgb(0, 0xFF, 0), new Rgb(0xFF, 0, 0) }),
    "嵌套 <color=…> 与 </color> 正确回退到外层颜色");
Check(NameMarkup.Plain(unclosed) == "abc" && unclosed.Length == 1 && unclosed[0].Color == new Rgb(0xFF, 0, 0), "未闭合颜色一直生效到结尾");
Check(NameMarkup.Plain(NameMarkup.Parse("a</color>b")) == "ab", "落单的 </color> 被忽略");
Check(NameMarkup.Plain(NameMarkup.Parse("5 < 6 and <co")) == "5 < 6 and <co", "普通尖括号与被截断的标签按字面保留");
Check(NameMarkup.Parse("<#1234>abcd")[0].Color == new Rgb(0x11, 0x22, 0x33), "#RGBA 短写（丢 alpha，按位展开）");
Check(NameMarkup.Parse("<#11223344>x")[0].Color == new Rgb(0x11, 0x22, 0x33), "#RRGGBBAA 长写");
Check(NameMarkup.Parse("<color=orange>x")[0].Color == new Rgb(0xFF, 0xA5, 0x00), "<color=具名色> 支持");
Check(NameMarkup.Plain(NameMarkup.Parse("<b>粗</b><size=200>体</size><material=1>x</material>")) == "粗体x", "已知排版标签整条丢掉，中文保留");
Check(NameMarkup.Plain(NameMarkup.Parse("<uppercase>demo</uppercase>")) == "DEMO", "uppercase 标签按大厅大写处理");
Check(NameMarkup.Plain(NameMarkup.Parse("中文😀<#0f0>字")) == "中文😀字", "中文与 Emoji（代理对）不被撕开");
Check(NameMarkup.AppendSuffix(NameMarkup.Parse("<#f00>name"), "（你）").Last().Color is null, "“（你）”不被最后一个颜色段染色");
Check(NameMarkup.Plain(NameMarkup.Truncate(NameMarkup.Parse("abcdef"), 3)) == "abc", "超长名字按 UTF-16 安全截断");
Check(NameMarkup.Plain(NameMarkup.Truncate(NameMarkup.Parse("a😀b"), 2)) == "a", "截断不会切开代理对");

// 生产路径：大厅返回的昵称原样保留（不改网络名），表头把它解析成颜色分段
SNetwork.SNet.SetLobby(400, 7, 8);
SNetwork.SNet.PlayAs(7, ("mod.aaa", "A 模组", "1.2.3"));
SNetwork.SNet.Rename(8, "<#fa0>\"<#0ff>Demo<#f00>\"");
var nameInventory = new LobbyInventory();
var nameColumns = nameInventory.Sample(0);
var peerColumn = nameColumns.Single(c => c.Id == 8);
Check(peerColumn.Name == "<#fa0>\"<#0ff>Demo<#f00>\"", "网络昵称原样保留，没有被改写");
var peerHeader = NameMarkup.Resolve(peerColumn.Id, nameInventory.Sample(0).Single(c => c.Id == 7).Id, null, peerColumn.Name);
Check(NameMarkup.Plain(peerHeader) == "\"Demo\"" &&
      peerHeader[0].Color == new Rgb(0xFF, 0xAA, 0x00) &&
      peerHeader[1].Color == new Rgb(0x00, 0xFF, 0xFF) &&
      peerHeader[2].Color == new Rgb(0xFF, 0x00, 0x00),
    "表头按大厅方言分成三段色，远端名字不带“（你）”");

// 落点换算（相机路径）：用实测位移量出每像素本地单位，再算命中盒
var projected = PanelAnchor.LocalPerPixel(new ScreenPoint(0.75f, 0f), new ScreenPoint(0f, 0.75f), 64f, 29f);
Check(MathF.Abs(projected.X - 0.75f / 64f) < 1e-6f && MathF.Abs(projected.Y - 0.75f / 29f) < 1e-6f, "相机投影量出逐像素本地位移");
var box = PanelAnchor.BoxSize(projected, 64f, 29f);
Check(MathF.Abs(box.X - 0.75f) < 1e-6f && MathF.Abs(box.Y - 0.75f) < 1e-6f, "命中盒按实测比例换回 64×29 像素");
Check(PanelAnchor.IsSane(projected) && !PanelAnchor.IsSane(new ScreenPoint(0f, 0f)) && !PanelAnchor.IsSane(new ScreenPoint(float.NaN, 1f)) &&
      !PanelAnchor.IsSane(new ScreenPoint(1e6f, 1f)), "换算比例的可信性判定");
Check(MathF.Abs(PanelAnchor.LocalPerPixel(2f).X - 0.5f) < 1e-6f, "屏幕空间画布：1 像素 = 1/scaleFactor 本地单位");

// ---- 回归①：大厅 UI 给本地列预拼的“（你）”不能被末尾颜色染到 ----
var prefixed = "<#f00>Red（你）";
var prefixedOld = NameMarkup.Parse(prefixed);
Check(NameMarkup.Plain(prefixedOld) == "Red（你）" && prefixedOld[^1].Color == new Rgb(0xFF, 0, 0),
    "旧路径（直接解析预拼名）会把“（你）”一起染成末尾红色 —— 这就是本次要修的回归");
var prefixedFixed = NameMarkup.Resolve(77, 77, null, prefixed);
Check(NameMarkup.Plain(prefixedFixed) == "Red（你）" && prefixedFixed[^1].Text == NameMarkup.YouSuffix && prefixedFixed[^1].Color is null,
    "本地 + 已预拼 + 末尾红色：剥掉 UI 预拼段后再解析，后缀独立且不染色");
Check(prefixedFixed[..^1].All(r => r.Color == new Rgb(0xFF, 0, 0)), "名字本体的红色保持不变（只挪动后缀）");
var prefixedRendered = NameMarkup.FromRendered(new[] { new NameChar("R", new Rgb(0xFF, 0, 0)), new NameChar("e", new Rgb(0xFF, 0, 0)), new NameChar("d", new Rgb(0xFF, 0, 0)) });
var prefixedWithNative = NameMarkup.Resolve(77, 77, new Dictionary<ulong, NameRun[]> { [77] = prefixedRendered }, prefixed);
Check(NameMarkup.Plain(prefixedWithNative) == "Red（你）" && prefixedWithNative[^1].Color is null, "有原生渲染结果时同样只补一段不染色后缀");

// ---- 回归①b：玩家自己的原名真以“（你）”结尾时不误删（尤其非本机） ----
var remoteOwn = NameMarkup.Resolve(78, 77, null, "张三（你）");
Check(NameMarkup.Plain(remoteOwn) == "张三（你）" && remoteOwn[^1].Color is null, "非本机原名以“（你）”结尾：原样保留，不删也不补");
var mineOwn = NameMarkup.Resolve(77, 77, null, "张三（你）（你）");
Check(NameMarkup.Plain(mineOwn) == "张三（你）（你）", "本机原名自带“（你）”：只剥 UI 预拼的那一段，总长不变");
var remoteNativeOwn = NameMarkup.FromRendered(new[]
{
    new NameChar("你", new Rgb(9, 9, 9)), new NameChar("（", new Rgb(9, 9, 9)),
    new NameChar("你", new Rgb(9, 9, 9)), new NameChar("）", new Rgb(9, 9, 9)),
});
var remoteNativeOwnResolved = NameMarkup.Resolve(78, 77, new Dictionary<ulong, NameRun[]> { [78] = remoteNativeOwn }, "fallback");
Check(NameMarkup.Plain(remoteNativeOwnResolved) == "你（你）", "非本机原生名以“（你）”结尾：按原样显示，不剥也不补");

// ---- 回归③：面板可见性判定（PlaceClose 与 OnGUI 共用，隐藏态不得留命中区） ----
Check(PanelAnchor.PanelShown(true, true, true, 1180f, 660f), "正常打开：面板可见，允许存在关闭命中区");
Check(!PanelAnchor.PanelShown(false, true, true, 1180f, 660f), "未打开：不可见（命中区必须一起藏）");
Check(!PanelAnchor.PanelShown(true, false, true, 1180f, 660f), "页面不活跃：不可见");
Check(!PanelAnchor.PanelShown(true, true, false, 1180f, 660f), "页面不在层级里：不可见");
Check(!PanelAnchor.PanelShown(true, true, true, 339f, 660f) && !PanelAnchor.PanelShown(true, true, true, 1180f, 219f),
    "屏幕太小画不出面板：不可见（不留透明关闭区）");
Check(PanelAnchor.PanelShown(true, true, true, 340f, 220f), "正好达到最小尺寸：可见");
Check(!PanelAnchor.PanelShown(true, true, true, 2560f, 1440f, 3000f, 220f), "阈值可覆盖（宽不够仍不可见）");
