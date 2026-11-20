namespace TeamModVersions;

/// <summary>
/// 一帧的大厅光标读数，已经换算到 IMGUI 坐标系（左上原点、像素）。Valid=false 表示这一帧没有可信读数，
/// 调用方必须按“没有命中”处理，不能沿用上一帧的坐标（无陈旧命中）。
/// </summary>
internal readonly record struct CursorSample(bool Valid, float X, float Y, string Source)
{
    internal static CursorSample Invalid(string source) => new(false, 0f, 0f, source);
}

/// <summary>
/// 屏幕点换算：大厅光标来自菜单相机的 <c>WorldToScreenPoint</c>（左下原点、y 向上），IMGUI 用左上原点、y 向下。
/// 全工程只在这里翻一次 Y —— 不叠加画布侧或相机侧的其它翻转，也不按 RenderMode 猜比例。
/// 参考案（GTFOMapShapes）实测同一约定：<c>screen.y</c> 直接参与 <c>Screen.height - y</c>。
/// </summary>
internal static class PointerMath
{
    internal static bool TryGui(float screenX, float screenYFromBottom, float depth, float screenHeight,
        out float guiX, out float guiY, out string note)
    {
        guiX = guiY = 0f;
        if (!float.IsFinite(screenHeight) || screenHeight <= 0f) { note = "屏幕高度不可用"; return false; }
        if (!float.IsFinite(screenX) || !float.IsFinite(screenYFromBottom)) { note = "光标投影不是有限数"; return false; }
        // 相机后面的点 WorldToScreenPoint 会给出镜像坐标，不能当命中点。
        if (!float.IsFinite(depth) || depth <= 0f) { note = "光标在菜单相机后面（z<=0）"; return false; }
        guiX = screenX;
        guiY = screenHeight - screenYFromBottom;   // 唯一一次 Y 翻转
        note = string.Empty;
        return true;
    }
}

/// <summary>屏幕坐标命中判定：渲染矩形与命中矩形是同一个 <see cref="ScreenRect"/>。</summary>
internal static class PanelHit
{
    internal static bool Inside(ScreenRect rect, float x, float y)
        => rect.Width > 0f && rect.Height > 0f &&
           x >= rect.X && x < rect.X + rect.Width &&
           y >= rect.Y && y < rect.Y + rect.Height;

    /// <summary>面板本地矩形 → 屏幕矩形（与绘制时 PanelRect + 本地偏移完全一致）。</summary>
    internal static ScreenRect Offset(ScreenRect panel, float x, float y, float width, float height)
        => new(panel.X + x, panel.Y + y, width, height);
}

/// <summary>
/// 关闭按压闸门：一次物理按压只关一次。规则：
///   1) 必须先看到一次「松开」才认可按下（开表那一下按住的键不会立刻把表关掉）；
///   2) 只认「按下」的那一刻光标是否落在关闭矩形内（按在窗口外再拖进来不算，按住不放不重复关）；
///   3) <see cref="Reset"/> 后重新需要一次松开（关闭 / 换页 / 失焦 / 卸载都复位）。
/// 面板隐藏、光标读不到、主按键读不到时调用方直接把 inside 传 false 或复位，不会用陈旧坐标。
/// </summary>
internal sealed class ClosePressGate
{
    private bool down, armed;

    /// <summary>上一帧看到的物理按键状态（用于确认边沿，不用于命中）。</summary>
    internal bool Down => down;

    /// <summary>本次会话已认过的关闭次数（供调试日志与测试断言）。</summary>
    internal int Closes { get; private set; }

    internal void Reset()
    {
        down = false;
        armed = false;
    }

    /// <summary>每帧调用一次；返回 true 表示这一帧要关闭面板（最多一次）。</summary>
    internal bool Sample(bool buttonDown, bool insideClose)
    {
        bool rising = buttonDown && !down;
        down = buttonDown;
        if (!buttonDown)
        {
            armed = true;   // 见过松开：下一次按下才是有效按压
            return false;
        }
        if (!rising || !armed || !insideClose) return false;
        armed = false;      // 同一次按压不再重复触发，需要先松开
        Closes++;
        return true;
    }
}

/// <summary>主按键读数来源：任一来源可用即用；都为 None 时只复位按压边沿，不影响滚轮。</summary>
internal enum PressSource { None, Page, Physical }

// Unity destruction is deferred: stopping is terminal even if a late native callback arrives.
internal sealed class DriverLifetime
{
    internal bool Running { get; private set; } = true;
    internal bool MouseActions { get; private set; }
    internal void EnableMouseActions() { if (Running) MouseActions = true; }
    internal bool BeginShutdown()
    {
        if (!Running) return false;
        Running = MouseActions = false;
        return true;
    }
}

/// <summary>
/// 关表那一次按压的归属：这次按压一旦被用来关表，就在它松开前一直被本插件持有，
/// 让大厅自己的按压分发跳过这一下，避免「同一次按压既关表、又触发背后的大厅控件」。
/// 只认当前页（同一实例）且页面活跃；另有硬超时（<see cref="MaxSeconds"/>）兜底，
/// 读不到松开也不会永久吞输入。别的页、页面失活、超时后一律放行。
/// </summary>
internal struct PressOwnership
{
    internal const double MaxSeconds = 1.0;

    internal bool Armed { get; private set; }
    internal double ArmedAt { get; private set; }

    /// <summary>关表按压成立的那一刻调用一次。</summary>
    internal void Arm(double now)
    {
        Armed = true;
        ArmedAt = double.IsFinite(now) ? now : 0;
    }

    // Only an already owned press can be renewed by a live, same-page sample.
    internal void Observe(bool confirmedDown, double now)
    {
        if (!confirmedDown || !double.IsFinite(now)) Clear();
        else if (Armed) ArmedAt = now;
    }
    internal void Clear()
    {
        Armed = false;
        ArmedAt = 0;
    }

    /// <summary>是否要拦下这一下大厅按压；换页 / 失活 / 超时会自动解除归属并返回 false（放行）。</summary>
    internal bool ShouldSuppress(bool samePage, bool pageActive, double now)
    {
        if (!Armed) return false;
        if (!samePage || !pageActive) { Clear(); return false; }
        if (!double.IsFinite(now) || now - ArmedAt > MaxSeconds) { Clear(); return false; }
        return true;
    }
}
internal enum WheelSource { None, Native, Physical }

/// <summary>
/// 清单滚轮换算与双来源去重。来源优先级：大厅页自己的 <c>ScrollInput</c> 优先，物理滚轮只在原生没有
/// “新的”读数时兜底；同一帧只用其中一个来源，绝不把两者相加（杜绝加倍）。
/// 原生读数按「变化」消费：值没变（一直保持非零）视为已消费，交给物理滚轮兜底，避免每帧重复滚动。
/// 方向：Unity 物理滚轮向下为负，换算成「向列表下方为正」的像素量。
/// </summary>
internal sealed class WheelRouter
{
    /// <summary>一档滚轮滚动的行数。</summary>
    internal const float RowsPerNotch = 3f;
    /// <summary>单帧最多认的档数（防止一次巨大读数把清单甩到底）。</summary>
    internal const float MaxNotches = 5f;

    private float previous;

    internal WheelSource Source { get; private set; }
    internal float ObservedNative { get; private set; }
    internal float ObservedPhysical { get; private set; }

    internal void Reset()
    {
        previous = 0f;
        Source = WheelSource.None;
        ObservedNative = 0f;
        ObservedPhysical = 0f;
    }

    /// <summary>
    /// 光标离开清单时只清“本帧来源”，保留已消费的原生读数指纹：再进列表时同一个未变化的原生读数
    /// 不会被当成新读数、再滚一次。与 <see cref="Reset"/> 的区别就在这条指纹。
    /// </summary>
    internal void Suspend()
    {
        Source = WheelSource.None;
        ObservedNative = 0f;
        ObservedPhysical = 0f;
    }

    /// <summary>返回本帧要加进 scroll.y 的像素量（向下为正）；0 表示这一帧不滚。</summary>
    internal float Sample(float native, float physical, float rowHeight, float maxPixels)
    {
        if (!float.IsFinite(native)) native = 0f;
        if (!float.IsFinite(physical)) physical = 0f;
        ObservedNative = native;
        ObservedPhysical = physical;
        bool fresh = native != 0f && native != previous;
        previous = native;
        Source = WheelSource.None;
        if (fresh)
        {
            Source = WheelSource.Native;
            return Pixels(native, rowHeight, maxPixels);
        }
        if (physical != 0f)
        {
            Source = WheelSource.Physical;
            return Pixels(physical, rowHeight, maxPixels);
        }
        return 0f;
    }

    /// <summary>原始滚轮读数 → 列表滚动像素（正 = 向下翻）。0.1 一档的平台按 10 倍归一。</summary>
    internal static float Pixels(float raw, float rowHeight, float maxPixels)
    {
        if (!float.IsFinite(raw) || raw == 0f) return 0f;
        float notches = MathF.Abs(raw);
        if (notches < 1f) notches *= 10f;
        notches = MathF.Min(notches, MaxNotches);
        float pixels = MathF.Min(notches * rowHeight * RowsPerNotch, MathF.Max(0f, maxPixels));
        return raw < 0f ? pixels : -pixels;
    }
}
