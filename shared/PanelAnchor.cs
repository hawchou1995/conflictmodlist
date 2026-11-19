namespace TeamModVersions;

/// <summary>与引擎无关的屏幕点/矩形，便于离线测试换算逻辑。</summary>
internal readonly record struct ScreenPoint(float X, float Y);

internal readonly record struct ScreenRect(float X, float Y, float Width, float Height);

/// <summary>
/// 把 IMGUI 面板坐标换算成大厅（CellMenu）坐标。两条路都不依赖 RenderMode 假设：
///   1) 屏幕空间画布（Overlay / ScreenSpaceCamera）：画布矩形铺满屏幕、画布中心=屏幕中心，
///      1 画布单位 = scaleFactor 像素 → <see cref="CanvasOffset"/> / <see cref="LocalPerPixel(float)"/>；
///   2) 其它（含 WorldSpace）：由驱动侧用大厅菜单相机把屏幕点投到父平面上，
///      逐像素本地位移再用 <see cref="LocalPerPixel(ScreenPoint, ScreenPoint, float, float)"/> 量出来。
/// </summary>
internal static class PanelAnchor
{
    /// <summary>面板真的画出来了才允许存在关闭命中区：OnGUI 与 PlaceClose 必须共用这一条判定。</summary>
    internal static bool PanelShown(bool open, bool active, bool pageInHierarchy, float width, float height,
        float minWidth = 340f, float minHeight = 220f)
        => open && active && pageInHierarchy && width >= minWidth && height >= minHeight;

    internal static ScreenPoint Center(ScreenRect rect, float screenHeight)
        => new(rect.X + rect.Width * .5f, screenHeight - (rect.Y + rect.Height * .5f));

    internal static ScreenPoint CanvasOffset(ScreenPoint target, float screenWidth, float screenHeight, float scaleFactor)
    {
        float scale = scaleFactor > 0f ? scaleFactor : 1f;
        return new((target.X - screenWidth * .5f) / scale, (target.Y - screenHeight * .5f) / scale);
    }

    /// <summary>画布矩形中心在画布本地坐标里的位置（Canvas 矩形铺满屏幕时这就是屏幕中心）。</summary>
    internal static ScreenPoint CanvasCenter(ScreenRect canvas)
        => new(canvas.X + canvas.Width * .5f, canvas.Y + canvas.Height * .5f);

    /// <summary>屏幕空间画布：1 屏幕像素 = 1/scaleFactor 本地单位。</summary>
    internal static ScreenPoint LocalPerPixel(float scaleFactor)
    {
        float scale = scaleFactor > 0f ? scaleFactor : 1f;
        return new(1f / scale, 1f / scale);
    }

    /// <summary>用实测的两条边（世界/本地位移 + 采样像素数）算出“每屏幕像素多少本地单位”。</summary>
    internal static ScreenPoint LocalPerPixel(ScreenPoint deltaX, ScreenPoint deltaY, float sampleX, float sampleY)
        => new(PerPixel(deltaX.X, sampleX), PerPixel(deltaY.Y, sampleY));

    /// <summary>命中盒像素尺寸 → 本地单位尺寸。</summary>
    internal static ScreenPoint BoxSize(ScreenPoint perPixel, float widthPixels, float heightPixels)
        => new(perPixel.X * widthPixels, perPixel.Y * heightPixels);

    /// <summary>换算比例是否可信（有限、正数、不过分大）；不可信时不改命中盒尺寸，但仍继续摆放。</summary>
    internal static bool IsSane(ScreenPoint perPixel, float maxPerPixel = 4096f)
        => float.IsFinite(perPixel.X) && float.IsFinite(perPixel.Y) &&
           perPixel.X > 0f && perPixel.Y > 0f &&
           perPixel.X <= maxPerPixel && perPixel.Y <= maxPerPixel;

    private static float PerPixel(float delta, float sample) => sample <= 0f || !float.IsFinite(delta) ? 0f : Math.Abs(delta) / sample;
}
