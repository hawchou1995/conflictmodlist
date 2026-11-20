namespace TeamModVersions;

// Projected native UI in screen pixels, top-left origin.
internal readonly record struct HudBounds(float Left, float Top, float Right, float Bottom)
{
    internal bool Valid => float.IsFinite(Left) && float.IsFinite(Top) && float.IsFinite(Right) && float.IsFinite(Bottom) && Right > Left && Bottom > Top;
    internal HudBounds Union(HudBounds b) => !b.Valid ? this : !Valid ? b : new(Math.Min(Left,b.Left),Math.Min(Top,b.Top),Math.Max(Right,b.Right),Math.Max(Bottom,b.Bottom));
}
internal static class SlotHudLayout
{
    internal static HudBounds Above(HudBounds chip,float width,float height,int screenW,int screenH) =>
        chip.Valid && chip.Top>2 && chip.Top<=screenH ? Place(chip.Left,chip.Top-2,width,height,screenW,screenH) : default;
    // Preserve left and bottom. Narrow a card at the physical screen edge, never push it over gear.
    internal static HudBounds Place(float left,float baseline,float width,float height,int screenW,int screenH)
    {
        if (!float.IsFinite(left) || !float.IsFinite(baseline) || left<0 || left>=screenW || height<=0 || width<=0 || screenH<=0) return default;
        float bottom=Math.Clamp(baseline,1,screenH);
        return new(left,Math.Max(0,bottom-height),Math.Min(screenW,left+width),bottom);
    }
}
