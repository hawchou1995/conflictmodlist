#if LAYOUT_GATE
// 局内 Esc 专项：同一个装备视图里游戏只藏了「服装」按钮，看板 / 昵称 / 武器栏照画，旧逻辑因此没有任何
// 锚点（本机既不能用模板、远端模板也一起没了），真人栏的「模组对照」整条消失。
// 普通大厅优先服装行（本栏、缓存、远端可借模板），只有缺少服装行时才用装备替代。
// 全部是纯几何，不启动游戏、不读截图位置。
// 接线：在 tests/LayoutGate/Program.cs 末尾 PASS 那一行之前加  EscAnchorGate.Run();
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TeamModVersions;

internal static class EscAnchorGate
{
    private static void Check(bool ok, string test)
    {
        if (!ok) throw new InvalidDataException(test);
    }

    private static ButtonBox Need(ButtonBox? box, string test) =>
        box ?? throw new InvalidDataException(test);

    private static LobbyButtonPlan Need(LobbyButtonPlan? plan, string test) =>
        plan ?? throw new InvalidDataException(test);

    internal static void Run()
    {
        var none = Array.Empty<ButtonBox>();

        // 1) 没有装备也没有服装按钮：不能凭空造锚点（否则就是一块看不见的命中区）。
        Check(ButtonLayout.StandIn(none) == null, "stand-in invented without equipment");
        Check(ButtonLayout.StandIn(new[] { new ButtonBox(0, 0, 0, 0) }) == null, "stand-in accepted an empty rect");

        // 2) 替代锚点 = 这一栏实际画出来的最底一排里最左的那格（同高取左，避免读到截图里猜的位置）。
        ButtonBox top = new(12, 320, 120, 360), mid = new(12, 285, 120, 325);
        ButtonBox low = new(12, 250, 120, 290), lowRight = new(140, 250, 248, 290);
        Check(ButtonLayout.StandIn(new[] { top, mid, low, lowRight }) == low, "stand-in is not the bottom-left slot");
        Check(ButtonLayout.StandIn(new[] { top, new ButtonBox(0, 0, 0, 0) }) == top, "invalid slot was not skipped");

        // 图标出现不应把使用本机模板的远端栏移到另一行。
        var clothes = new ButtonBox(12, 269, 106, 309);
        var kept = new ButtonBox(30, 200, 130, 240);
        var gear = new[] { new ButtonBox(12, 150, 100, 190) };
        Check(ButtonLayout.Anchor(true, clothes, kept, gear, null) == clothes, "live clothes not preferred (local)");
        Check(ButtonLayout.Anchor(false, clothes, kept, gear, null) == clothes, "live clothes not preferred (remote)");
        Check(ButtonLayout.Anchor(true, null, kept, gear, null) == kept, "remembered geometry not reused for its own pillar");
        Check(ButtonLayout.Anchor(false, null, null, gear, clothes) == clothes, "remote chip jumped to a new row as weapons appeared");
        Check(ButtonLayout.Anchor(false, null, null, none, clothes) == clothes, "remote template fallback lost");
        Check(ButtonLayout.Anchor(true, null, null, none, clothes) == null, "local pillar borrowed another pillar's geometry");
        Check(ButtonLayout.Anchor(true, null, null, none, null) == null, "anchor invented from nothing");
        Check(ButtonLayout.Anchor(false, null, null, none, null) == null, "plan built without any anchor");

        // 4) 局内 Esc、冷启动（无缓存）、服装按钮隐藏：真人栏仍然落出一个贴着最底装备右侧的按钮，
        //    不越栏、不压装备/昵称，且和「有服装按钮」时同一套最小宽度与间隙规则。
        var esc = new[] { new ButtonBox(12, 315, 120, 355), new ButtonBox(12, 280, 120, 320), new ButtonBox(12, 245, 120, 285) };
        var occupied = new List<ButtonBox>(esc)
        {
            new(12, 420, 120, 448),   // 武器栏表头
            new(12, 600, 240, 640),   // 昵称
        };
        var anchor = Need(ButtonLayout.Anchor(true, null, null, esc, null), "in-level Esc has no anchor");
        Check(anchor == esc[2], "in-level Esc anchor is not the bottom gear slot");
        var plan = Need(ButtonLayout.Place(76561198000000001, true, anchor, null, 240, occupied), "in-level Esc has no chip");
        Check(plan.Player == 76561198000000001, "in-level Esc chip lost its player id");
        Check(plan.Rect.Bottom == 245 && plan.Rect.Left == 124, "in-level Esc chip is not on the bottom gear row");
        Check(plan.Rect.Width >= ButtonLayout.MinWidth && plan.Rect.Right <= 240, "in-level Esc chip broke the width / pillar edge");
        Check(occupied.All(b => !b.Intersects(plan.Rect)), "in-level Esc chip covers gear or the nickname");

        // 5) 同一条锚点在本机与远端两栏各出一枚，互不重叠（真人栏都要，BOT 栏不进来）。
        var remote = Need(ButtonLayout.Anchor(false, null, null, esc, anchor), "remote in-level Esc has no anchor");
        var localChip = Need(ButtonLayout.Place(1, true, anchor, null, 240, occupied), "local chip missing");
        var remoteChip = Need(ButtonLayout.Place(2, false, remote, null, 240, occupied), "remote chip missing");
        Check(localChip.Rect == remoteChip.Rect, "same pillar geometry produced two different rows");

        // 6) 缓存命中同一套落位：缓存锚点与实时服装锚点的结果完全一致（切表/回大厅位置不跳）。
        var cached = Need(ButtonLayout.Anchor(true, null, clothes, none, null), "cached anchor lost");
        var live = Need(ButtonLayout.Anchor(true, clothes, null, none, null), "live anchor lost");
        Check(ButtonLayout.Place(1, true, cached, null, 500, none)?.Rect == ButtonLayout.Place(1, true, live, null, 500, none)?.Rect,
            "cached and live clothes geometry disagree");

        // 7) 没有装备可依（例如装备被彻底隐藏）时宁可不留按钮，也不留一块压在空处的命中区。
        Check(ButtonLayout.Place(1, true, null, null, 500, none) == null, "chip placed without an anchor");
        Check(ButtonLayout.Anchor(true, null, null, none, null) is null && ButtonLayout.StandIn(none) is null,
            "hidden gear still produced a stand-in");

        Console.WriteLine("PASS 局内 Esc 服装按钮缺失：缓存模板 / 实际装备替代锚点、本机与远端同一落位、无锚点不留命中区");
    }
}
#endif
