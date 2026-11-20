#if LAYOUT_GATE
// From repo root: dotnet run --project tests/LayoutGate -- [--preview docs]
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using TeamModVersions;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
string? output = args switch { [] => null, ["--preview", var dir] => dir, _ => throw new ArgumentException("[--preview DIRECTORY]") };
static void Check(bool ok, string test) { if (!ok) throw new InvalidDataException(test); }
var empty = Array.Empty<ButtonBox>();
ButtonBox local = new(12, 269, 106, 309);
Check(ButtonLayout.Place(0, false, null, local, 500, empty) == null, "zero ID retained");
Check(ButtonLayout.Place(1, true, null, local, 500, empty) == null, "invented local geometry");
Check(ButtonLayout.Place(2, false, null, null, 500, empty) == null, "invented remote geometry");
Check(ButtonLayout.Place(2, false, null, local, 500, empty)?.Rect == ButtonLayout.Place(1, true, local, null, 500, empty)?.Rect, "fallback in wrong space");
Check(ButtonLayout.Place(2, false, local, null, 500, new ButtonBox[] { new(105, 245, 500, 290) }) == null, "unsafe fully blocked row");
var tight = ButtonLayout.Place(2, false, local, null, 173, empty);
Check(tight?.Rect.Width == 63 && tight?.Rect.Right == 173, "does not fit compactly at viewport edge");

// Compare the interval search against an independent pixel-grid clearance oracle. Integer fixtures
// have integer gap endpoints, so every feasible minimum-width interval has a grid-aligned start.
var rng = new Random(7302);
for (int trial = 0; trial < 512; ++trial)
{
    var blocks = Enumerable.Range(0, 8).Select(_ => { int x = rng.Next(90, 450), y = rng.Next(230, 311); return new ButtonBox(x, y, x + rng.Next(1, 80), y + 25); }).ToArray();
    float limit = rng.Next(150, 480);
    var result = ButtonLayout.Place(1, true, local, null, limit, blocks);
    int expected = Enumerable.Range(110, 400).FirstOrDefault(x => x + 60 <= limit && blocks.All(b =>
        b.Right + 4 <= x || b.Left - 4 >= x + 60 || b.Top + 4 <= 269 || b.Bottom - 4 >= 297), -1);
    Check(result?.Rect.Left == (expected < 0 ? (float?)null : expected), $"first safe gap #{trial}");
    if (result is {} p) Check(p.Rect.Right <= limit && blocks.All(b => !b.Intersects(p.Rect)), $"overlap #{trial}");
}

foreach (int w in new[] {1280,1920,2560,3440})
{
    float pitch=w/4f;
    for(int i=0;i<4;i++)
    {
        float x=pitch*i+8;
        float buttonLeft=x+118, buttonTop=455;
        var native=new HudBounds(buttonLeft,buttonTop,buttonLeft+92,buttonTop+28);
        var cap=SlotHudLayout.Above(native,150,78,w,720);
        var bot=SlotHudLayout.Above(native,115,40,w,720);
        Check(cap.Valid && bot.Valid && cap.Left==buttonLeft && cap.Bottom==bot.Bottom && cap.Bottom==buttonTop-2 && cap.Top<bot.Top,"different heights with exact button clearance");
    }
}
var tightCard=SlotHudLayout.Place(996,435,155,68,1000,700);
Check(tightCard.Valid && tightCard.Left==996 && tightCard.Right==1000 && tightCard.Bottom==435,"screen edge narrows width without moving the left edge");
Check(!SlotHudLayout.Place(float.NaN,435,155,68,1000,700).Valid,"no invented screen-space anchor");
Console.WriteLine("PASS per-seat status geometry: 4 resolutions x 4 slots, BOT without button, right edge");

foreach (var (width, height, suffix) in new[] { (1920, 1080, "16x9"), (3440, 1440, "ultrawide") })
{
    const int H = 900;
    float W = width * H / (float)height, pitch = (W - 64) / 4;
    XNamespace svg = "http://www.w3.org/2000/svg";
    var root = new XElement(svg + "svg", new XAttribute("viewBox", $"0 0 {W} {H}"), new XAttribute("width", width), new XAttribute("height", height),
        new XAttribute("font-family", "Segoe UI, Microsoft YaHei, sans-serif"), new XElement(svg + "title", $"{width} × {height} · four-player compare layout fixture"));
    XElement Group(string id) => new(svg + "g", new XAttribute("id", id));
    XElement Box(ButtonBox b, string fill) => new(svg + "rect", new XAttribute("x", b.Left), new XAttribute("y", H - b.Top),
        new XAttribute("width", b.Width), new XAttribute("height", b.Height), new XAttribute("fill", fill));
    XElement Text(float x, float y, string s, int size = 15, string fill = "#ced2d6") => new(svg + "text", new XAttribute("x", x),
        new XAttribute("y", H-y), new XAttribute("font-size", size), new XAttribute("fill", fill), s);
    root.Add(Box(new(0, 0, W, H), "#17191b"), Text(32, 824, "装备大厅  /  玩家栏", 34, "#f2f3ef"),
        Text(32, 784, $"{width} × {height}   /   离线布局样例（非游戏截图）"));
    var lobby = Group("lobby"); root.Add(lobby);
    var chips = new List<ButtonBox>();
    var names = new[] { "LOCAL_Scout_With_A_Very_Long_Nickname", "玩家二号_特别长的昵称及后缀_0123456789", "RemoteEngineer_With_A_Long_Suffix", "Very_Long_Nickname_Operator_Four" };
    for (int i = 0; i < 4; ++i)
    {
        float ox = 32 + pitch*i, right = pitch - 14;
        var pillar = Group($"player-{i+1}"); pillar.SetAttributeValue("transform", $"translate({ox} 0)"); lobby.Add(pillar);
        ButtonBox? own = i switch { 0 => local, 2 => new(18, 282, 133, 317), _ => null };
        var a = own ?? local;
        ButtonBox gear = new(12, 335, right-8, 439), name = new(12, 626, right-8, 676);
        ButtonBox localia = i == 1 ? new(a.Right + 3, a.Bottom, a.Right + 72, a.Bottom+28) : new(12, 540, 132, 568);
        var protectedRects = new[] { a, gear, name, localia };
        var plan = ButtonLayout.Place((ulong)i+1, i==0, own, local, right, protectedRects) ?? throw new Exception($"missing slot {i}");
        var r = plan.Rect;
        Check(plan.Player == (ulong)i+1 && r.Bottom == a.Bottom && r.Left >= a.Right+4 && r.Right <= right && r.Width >= 60, "identity / alignment / pillar bounds");
        Check(protectedRects.All(b => !b.Intersects(r)), "over native control or nickname");
        if (i==1) Check(r.Left == localia.Right+4, "Localia collision not resolved");
        var world = new ButtonBox(ox+r.Left, r.Bottom, ox+r.Right, r.Top);
        Check(chips.All(b => !b.Intersects(world)), "cross-pillar overlap"); chips.Add(world);
        pillar.Add(Box(new(0, 170, right, 710), "#212528"), Text(12, 680, $"0{i+1}   /   {(i==0 ? "LOCAL" : "REMOTE")}", 13, "#9ea8af"));
        var clip = new XElement(svg+"clipPath", new XAttribute("id", $"nick-{i}"), Box(name, "white"));
        var nickname = Text(name.Left, name.Bottom+10, names[i], 22, "#eef0ed"); nickname.SetAttributeValue("clip-path", $"url(#nick-{i})");
        pillar.Add(clip, nickname, Box(gear, "#383f43"), Text(gear.Left+8, gear.Bottom+40, "装备  /  EQUIPMENT"));
        pillar.Add(Box(localia, "#47504b"), Text(localia.Left+6, localia.Bottom+7, "Localia"));
        if (own != null) pillar.Add(Box(a, "#515659"), Text(a.Left+6, a.Bottom+10, "服装"));
        else { var guide = Box(a, "none"); guide.SetAttributeValue("stroke", "#768187"); guide.SetAttributeValue("stroke-dasharray", "3 4"); pillar.Add(guide, Text(a.Left+5, a.Bottom+10, "模板位置", 12)); }
        var button = Group($"compare-{plan.Player}"); button.SetAttributeValue("data-hit", "compare");
        var caption = Text((r.Left+r.Right)/2, (r.Bottom+r.Top)/2-5, ButtonLayout.Caption, 15, "#ffd44c"); caption.SetAttributeValue("text-anchor", "middle");
        button.Add(Box(r, "#2b2d2f"), caption); pillar.Add(button);
    }
    var hud = Group("hud"); hud.Add(Text(32, 92, "HUD  /  独立父节点 · 此区域没有大厅命中框", 16, "#929b9e")); root.Add(hud);
    var hits = root.Descendants().Where(e => e.Attribute("data-hit")!=null).ToArray();
    Check(hits.Length == 4 && hits.All(e => (string?)e.Attribute("data-hit")=="compare" && e.Value=="模组对照" && e.Ancestors().Contains(lobby)) && !hud.Descendants().Any(e => e.Attribute("data-hit")!=null), "extra/network/HUD hitbox");
    if (output != null) { Directory.CreateDirectory(output); new XDocument(root).Save(Path.Combine(output, $"layout-preview-{suffix}.svg")); }
    Console.WriteLine($"PASS {width}x{height}: 4 compare regions, remote templates, long names, Localia collision, 0 network/HUD regions");
}
EscAnchorGate.Run();
Console.WriteLine("PASS blocked, narrow, absent template, zero ID, 512 interval fixtures");
#endif
