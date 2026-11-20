namespace TeamModVersions;

// Player-root units, +Y up. Left/bottom are edges, not a pivot, anchoredPosition or a screen pixel.
internal readonly record struct ButtonBox(float Left, float Bottom, float Right, float Top)
{
    internal float Width => Right - Left;
    internal float Height => Top - Bottom;
    internal bool Valid => float.IsFinite(Left) && float.IsFinite(Bottom) &&
        float.IsFinite(Right) && float.IsFinite(Top) && Width > 0 && Height > 0;
    internal bool Intersects(ButtonBox b) => Left < b.Right && Right > b.Left && Bottom < b.Top && Top > b.Bottom;
}

// One result is one native item. There is deliberately no second action/rectangle for a status label or HUD.
internal readonly record struct LobbyButtonPlan(ulong Player, ButtonBox Rect);

internal static class ButtonLayout
{
    internal const string Caption = "模组对照";
    internal const float Gap = 4, MinWidth = 60;

    // A hidden remote card borrows *parent-relative* geometry, never the local player's scene position.
    // Keep the bottom edge fixed. Pick the first readable gap on its right; narrow the chip before
    // jumping an obstacle. If there is no safe gap in this pillar, don't leave an invisible hitbox.
    internal static LobbyButtonPlan? Place(ulong player, bool local, ButtonBox? clothes, ButtonBox? template,
        float rightEdge, IReadOnlyList<ButtonBox> occupied)
    {
        if (player == 0 || !float.IsFinite(rightEdge)) return null;
        var anchor = clothes is { Valid: true } ? clothes : local ? null : template;
        if (anchor is not { Valid: true } a) return null;
        float h = Math.Clamp(a.Height * .75f, 20, 28), w = Math.Clamp(3.25f * h, 68, 92);
        float x = a.Right + Gap, y = a.Bottom;
        while (x + MinWidth <= rightEdge)
        {
            float end = rightEdge, next = x;
            foreach (var b in occupied)
            {
                if (!b.Valid || b.Top + Gap <= y || b.Bottom - Gap >= y + h || b.Right + Gap <= x) continue;
                end = Math.Min(end, b.Left - Gap);
                if (b.Left - Gap < x + MinWidth) next = Math.Max(next, b.Right + Gap);
            }
            if (end - x >= MinWidth) return new(player, new(x, y, Math.Min(x + w, end), y + h));
            if (next <= x) break;
            x = next;
        }
        return null;
    }

    // The in-level Esc view keeps the equipment column drawn and only hides the clothes button, so the
    // clothes rect the chip used to sit on is gone. The stand-in is the bottom-left slot this pillar is
    // actually drawing: same left edge, same baseline row, still parent-relative units.
    // Null when the pillar draws no equipment — then there is nothing to sit next to and no hitbox.
    internal static ButtonBox? StandIn(IReadOnlyList<ButtonBox> gear)
    {
        ButtonBox? row = null;
        foreach (var b in gear)
        {
            if (!b.Valid) continue;
            if (row is not { } r || b.Bottom < r.Bottom - .5f || (Math.Abs(b.Bottom - r.Bottom) <= .5f && b.Left < r.Left)) row = b;
        }
        return row;
    }

    // Keep a remote pillar on the clothes row as its weapons appear. Only the cold Esc page
    // without a clothes template uses drawn equipment as a substitute.
    internal static ButtonBox? Anchor(bool local, ButtonBox? clothes, ButtonBox? remembered,
        IReadOnlyList<ButtonBox> gear, ButtonBox? template)
    {
        if (clothes is { Valid: true } live) return live;
        if (remembered is { Valid: true } kept) return kept;
        if (!local && template is { Valid: true } borrowed) return borrowed;
        return StandIn(gear);
    }
}
