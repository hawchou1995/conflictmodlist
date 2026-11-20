using System.Runtime.CompilerServices;
using CellMenu;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Attributes;
using SNetwork;
using Steamworks;
using TMPro;
using UnityEngine;

namespace TeamModVersions;

// 注册面约定：il2cpp 侧只应看到 Unity 需要的消息回调（Update / OnGUI），其余纯托管 helper 一律 [HideFromIl2Cpp]。
// 原因：Il2CppInterop 的 ClassInjector 会把本类型每个「声明实例方法」的参数/返回类型丢进 Il2CppClassPointerStore 取原生
// class 指针；插件程序集里的自有值类型（如 ScreenPoint）不是 il2cpp 类型，取回 IntPtr.Zero，ConvertMethodInfo 用它拼方法
// 签名 → 加载期（Plugin.Load → AddComponent 里的 RegisterTypeInIl2Cpp，早于 PatchAll）抛 NullReferenceException，
// 整个插件不加载。这些 helper 只被 C# 侧调用（Plugin、Harmony 补丁、驱动自身），隐藏后 managed 调用与 Unity 消息都不受影响。
// 复核：tests/EntryRegistrationGate 用本地真实 ClassInjector.IsMethodEligible + Il2CppClassPointerStore 逐方法查。
public sealed class TeamVersionsDriver : MonoBehaviour
{
    public TeamVersionsDriver(IntPtr pointer) : base(pointer) { }
    private readonly LobbyInventory inventory = new();
    private readonly DriverLifetime lifetime = new();
    private CM_PageLoadout? page;
    private readonly LobbyButtons buttons = new();
    private readonly TeamNetwork network = new();
    private readonly LobbyNetworkHud hud = new();
    private Action<ulong>? choose;
    private CM_PageLoadout? popupPage;
    private ulong selected;
    private bool networkError;
    private Camera? fallbackCamera;
    private bool cameraChecked;
    private PressSource pressSource = PressSource.None;   // 主按键实际来源（页面 / 物理），只用于日志
    private PressOwnership ownedPress;                    // 关表那一次按压的归属（松开 / 换页 / 超时解除）
    private bool nameError;
    private float next, nextHud;
    private bool active, open, syncError, uiError, matches, confirmed;
    private bool? sessionMatches;
    private Vector2 scroll;
    private Font? font;
    private bool fontTried;
    private GUIStyle? text, heading, nameRun, green, red, gray, close;
    private PlayerColumn[] columns = Array.Empty<PlayerColumn>();
    private PanelModel model = PanelModel.Blank;
    private Dictionary<ulong, NameRun[]> headerNames = new();   // 玩家 ID → 大厅原生名字（颜色分段）
    private NameRun[][] headerRuns = Array.Empty<NameRun[]>();  // 与 columns 同序，绘制时直接取
    private ulong headerLocalId;
    private readonly ClosePressGate closePress = new();   // 关闭按压边沿：开表 / 关闭 / 换页后都要重新按下
    private readonly WheelRouter wheel = new();           // 大厅 ScrollInput 优先、物理滚轮兜底，同帧只算一次
    private readonly RailDrag railDrag = new();
    private readonly ClosePressGate panelPress = new();
    private bool panelHeld, frameDown;
    private int inputFrame = -1, wheelFrame = -1;
    private CursorSample cursor = CursorSample.Invalid("未采样");
    private double inputLogAt;
    private int inputLogs;
    private bool cursorError, buttonError, scrollError, inputError;

    private const float CloseWidth = 64f, CloseHeight = 29f, CloseMarginRight = 18f, CloseMarginTop = 13f;

    [HideFromIl2Cpp]
    internal void EnableMouseActions() => lifetime.EnableMouseActions();

    [HideFromIl2Cpp]
    internal void SetPage(CM_PageLoadout target, bool value)
    {
        if (!lifetime.Running) return;
        if (!value && page != target) return;
        if (page == target && active == value) return;
        if (page != target || !value)
        {
            Close(); ownedPress.Clear(); RemoveButton();
            hud.Clear(); fallbackCamera = null; cameraChecked = false;
            selected = 0;
        }
        page = target;
        active = value;
        ResetInput();
        nextHud = 0;
        next = 0;
    }

    [HideFromIl2Cpp]
    internal void SetPopupVisible(CM_PageLoadout target, bool visible)
    {
        if (!lifetime.Running) return;
        if (visible) popupPage = target;
        else if (popupPage == target) popupPage = null;
        if (page != target) return;
        nextHud = 0;
        if (visible) { Close(); ownedPress.Clear(); RemoveButton(); hud.Clear(); }
        next = 0;
    }

    [HideFromIl2Cpp]
    internal void Shutdown()
    {
        if (!lifetime.BeginShutdown()) return;
        enabled = false;
        active = false;
        try { inventory.Withdraw(); } catch (Exception e) { Plugin.Logger.LogDebug(e.Message); }
        inventory.Reset();
        try { network.Reset(); } catch (Exception e) { Plugin.Logger.LogDebug(e.Message); }
        hud.Clear();
        Close();
        ownedPress.Clear();
        RemoveButton();
        if (font != null) Destroy(font);
    }

    [HideFromIl2Cpp]
    private void RemoveButton() => buttons.Clear();

    // 面板状态复位：只翻状态与输入（原生命中区已移除，没有别的要收）。
    [HideFromIl2Cpp]
    private void Close()
    {
        if (panelHeld || railDrag.Held) ownedPress.Arm(Time.realtimeSinceStartup);
        open = false;
        ResetInput();
    }

    private void Update()
    {
        if (!lifetime.Running) return;
        // 鼠标必须每帧采一次：按压边沿与物理滚轮都是“这一帧”的读数，放进下面 1 Hz 的刷新节流会漏掉绝大多数点击 / 滚轮。
        // 只在页面活跃时处理；面板未显示 / 换页 / 失焦 / 卸载由 HandleMouse 内部复位（面板外不拦不抢）。
        if (active && page != null)
        {
            try { HandleMouse(page); inputError = false; }
            catch (Exception e)
            {
                ResetInput(); ownedPress.Clear();
                if (!inputError) Plugin.Logger.LogWarning($"读取大厅光标 / 主按键失败，鼠标点「关闭」暂不可用（Esc 仍可关）：{e}");
                inputError = true;
            }
        }
        float now = Time.realtimeSinceStartup;
        bool loadout = LoadoutVisible();
        // The backend throttles its roster/sample/publish work itself. Pulse draining needs
        // the frame clock even when the slower inventory and HUD text refresh are not due.
        network.Tick(now, loadout);
        if (now < next) return;
        next = now + 1;
        try
        {
            var current = inventory.Sample(now);
            bool showMatches = sessionMatches ?? Plugin.ShowMatches.Value, rosterOk = inventory.RosterConfirmed;
            if (syncError || showMatches != matches || rosterOk != confirmed || !columns.SequenceEqual(current))
            {
                model = PanelModel.Create(current, showMatches, rosterOk, inventory.Notice, syncError: false);
                columns = current; matches = showMatches; confirmed = rosterOk;
            }
            syncError = false;
        }
        catch (Exception e)
        {
            if (!syncError) Plugin.Logger.LogWarning($"刷新大厅模组清单失败：{e}");
            syncError = true;
            columns = Array.Empty<PlayerColumn>();
            model = PanelModel.Failure();
        }
        if (loadout && page != null)
        {
            if (open)
            {
                // 名字只在表打开时读：1 Hz 读原生 TMP，失败就退回昵称原文（不清空已显示的表）。
                try { ResolveNames(page); nameError = false; }
                catch (Exception e)
                {
                    if (!nameError) Plugin.Logger.LogWarning($"读取大厅玩家名失败，表头回退到昵称原文：{e}");
                    nameError = true;
                    headerNames = new Dictionary<ulong, NameRun[]>();
                    headerRuns = Array.Empty<NameRun[]>();
                }
            }
        }
    }

    // Native slot activation happens during the page update. Prepare the chip before the menu paints;
    // only membership/gear transitions and the normal data tick require a full obstacle pass.
    private void LateUpdate()
    {
        if (!lifetime.Running || page == null || !LoadoutVisible()) return;
        float now = Time.realtimeSinceStartup;
        bool due = now >= nextHud, changed;
        if (due) nextHud = now + .5f;
        try { changed = hud.Refresh(page, network, due); networkError = false; }
        catch (Exception e)
        {
            hud.Clear(); if (!networkError) Plugin.Logger.LogWarning($"刷新装备栏网络状态失败：{e}");
            networkError = true; return;
        }
        if (due || changed)
        {
            try { PlaceButton(page); uiError = false; }
            catch (Exception e)
            {
                RemoveButton(); if (!uiError) Plugin.Logger.LogWarning($"创建大厅按钮失败：{e}");
                uiError = true;
            }
        }
        hud.Present(buttons, PointerCamera(page));
    }

    [HideFromIl2Cpp]
    private void PlaceButton(CM_PageLoadout current) => buttons.Update(current, choose ??= Toggle);

    /// <summary>
    /// 取大厅每个真人玩家“名字控件”的原生渲染结果：本地用本机 Steam ID、远端用 Lookup 与 PlayerColumn.Id 对应
    /// （不按下标，也不改大厅名字/网络字段）。没有 bar、读不到的人都交给绘制侧回退。
    /// </summary>
    [HideFromIl2Cpp]
    private void ResolveNames(CM_PageLoadout current)
    {
        var names = new Dictionary<ulong, NameRun[]>();
        ulong localId = LocalId();
        var bars = current.m_playerLobbyBars;
        if (bars != null)
        {
            for (int i = 0; i < bars.Length && names.Count < 16; i++)
            {
                var bar = bars[i];
                if (bar == null) continue;
                SNet_Player? player;
                try { player = bar.m_player; }
                catch (Exception e) { Plugin.Logger.LogDebug(e.Message); continue; }
                if (player is null || player.IsBot) continue;
                ulong sid = player.IsLocal ? localId : player.Lookup;
                if (sid == 0) continue;
                NameRun[]? runs;
                try { runs = ReadNativeName(bar.m_nickText); }
                catch (Exception e) { Plugin.Logger.LogDebug($"读大厅名字失败：{e.Message}"); continue; }
                if (runs is { Length: > 0 }) names[sid] = runs;
            }
        }
        headerNames = names;
        headerLocalId = localId;
        var runsByColumn = new NameRun[columns.Length][];
        for (int p = 0; p < columns.Length; p++)
        {
            var column = columns[p];
            runsByColumn[p] = NameMarkup.Resolve(column.Id, localId, names, column.Name);
        }
        headerRuns = runsByColumn;
    }

    /// <summary>
    /// 一个大厅名字控件的真实显示：优先逐字符（ci.index 指原始字符串，按 stringLength 切片，保留代理对与字面尖括号；
    /// 单个码元时用渲染后字符，所以大厅大写/缩写会被保留），并用 GetParsedText 做一致性校验；
    /// 校验失败或没有排版数据时退回 TMP 原文解析；再没有才由调用方用原始昵称。
    /// </summary>
    private static NameRun[]? ReadNativeName(TMP_Text? label)
    {
        if (label == null) return null;
        string? raw = null;
        try { raw = label.text; }
        catch (Exception e) { Plugin.Logger.LogDebug(e.Message); }
        try
        {
            if (!string.IsNullOrEmpty(raw))
            {
                var info = label.textInfo;
                int count = info?.characterCount ?? 0;
                var chars = info?.characterInfo;
                if (count > 0 && chars != null && count <= chars.Length)
                {
                    var pieces = new List<NameChar>(count);
                    bool trusted = true;
                    for (int i = 0; i < count && trusted; i++)
                    {
                        var ci = chars[i];
                        if (ci.elementType != TMP_TextElementType.Character || !ci.isVisible) continue;
                        int index = ci.index, length = ci.stringLength;
                        if (length <= 0) length = 1;
                        if (index < 0 || index + length > raw!.Length) { trusted = false; break; }
                        string slice = raw.Substring(index, length);
                        if (length == 1 && ci.character != '\0' && ci.character != slice[0]) slice = ci.character.ToString();
                        pieces.Add(new NameChar(slice, new Rgb(ci.color.r, ci.color.g, ci.color.b)));
                    }
                    if (trusted && pieces.Count > 0)
                    {
                        var runs = NameMarkup.FromRendered(pieces);
                        string parsed = label.GetParsedText() ?? string.Empty;
                        if (parsed.Length == 0 || string.Equals(NameMarkup.Plain(runs), parsed, StringComparison.OrdinalIgnoreCase)) return runs;
                    }
                }
            }
        }
        catch (Exception e) { Plugin.Logger.LogDebug($"读大厅名字排版结果失败，改用原文：{e.Message}"); }
        return string.IsNullOrEmpty(raw) ? null : NameMarkup.Parse(raw);
    }

    private static ulong LocalId()
    {
        try { ulong steam = SteamUser.GetSteamID().m_SteamID; if (steam != 0) return steam; }
        catch (Exception e) { Plugin.Logger.LogDebug(e.Message); }
        try { return SNet.LocalPlayer?.Lookup ?? 0; }
        catch (Exception e) { Plugin.Logger.LogDebug(e.Message); return 0; }
    }

    [HideFromIl2Cpp]
    private void Toggle(ulong player)
    {
        if (!LoadoutVisible() || player == 0) return;
        if (open && selected == player) { Close(); return; }
        selected = player;
        open = true;
        ResetInput(); // The bar press must be released before the panel accepts a press.
        scroll = Vector2.zero;
        next = 0;
    }

    private static Vector2 PanelSize() => new(Mathf.Min(1180f, Screen.width - 44f),
        Mathf.Min(660f, Screen.height - 44f));

    private static Rect PanelRect(Vector2 size) => new((Screen.width - size.x) * .5f,
        (Screen.height - size.y) * .5f, size.x, size.y);

    // “关闭”在面板本地坐标里的矩形：OnGUI 就用它画，命中则用 CloseButtonRect 换算到屏幕，两者同源同尺寸。
    private static Rect CloseLocalRect(float panelWidth) => new(panelWidth - CloseMarginRight - CloseWidth, CloseMarginTop, CloseWidth, CloseHeight);

    private static Rect CloseButtonRect(Rect panel)
    {
        var local = CloseLocalRect(panel.width);
        return new Rect(panel.x + local.x, panel.y + local.y, local.width, local.height);
    }

    // 清单视口（面板本地坐标）：BeginGroup 用它裁切，滚轮命中用 ListScreenRect 换算到屏幕。
    private static Rect ListLocalRect(Vector2 panel) => new(14f, 113f, panel.x - 28f, ViewportHeight(panel.y));

    private static ScreenRect ListScreenRect(Vector2 panel)
    {
        var screen = PanelRect(panel);
        var local = ListLocalRect(panel);
        return PanelHit.Offset(new ScreenRect(screen.x, screen.y, screen.width, screen.height), local.x, local.y, local.width, local.height);
    }

    private static ScreenRect ToScreenRect(Rect rect) => new(rect.x, rect.y, rect.width, rect.height);

    private static float ViewportHeight(float panelHeight) => panelHeight - 150f;

    // 简体中文优先：按可读性排序，Unity 取第一个系统可用的字体。
    private static readonly string[] ReadableFonts =
    {
        "Microsoft YaHei UI", "Microsoft YaHei", "Source Han Sans SC", "Noto Sans CJK SC", "DengXian", "SimHei",
    };

    // 不能调用 Font.CreateDynamicFontFromOSFont：该 interop 桩内部实例化 Il2CppReferenceArray<System.String>，
    // 违反 where T : Il2CppObjectBase 约束，JIT 那个桩时必抛 TypeLoadException（整帧不可执行，日志里每帧刷屏）。
    // 这里走 Unity 自己的等价路径：new Font() + Font.Internal_CreateDynamicFont(Font, Il2CppStringArray, int)。
    // NoInlining 让这条路径的类型加载/JIT 失败只落在这一个调用点上，由调用方的 try/catch 兜成“退回默认字体”。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Font? CreateReadableFont()
    {
        var created = new Font();
        try
        {
            Font.Internal_CreateDynamicFont(created, new Il2CppStringArray(ReadableFonts), 18);
            return created;
        }
        catch
        {
            try { Destroy(created); } catch (Exception cleanup) { Plugin.Logger.LogDebug(cleanup.Message); }
            throw;
        }
    }

    // 面板是否真的画出来了：OnGUI 与按压 / 滚轮命中必须共用同一条判定（见 PanelAnchor.PanelShown）。
    [HideFromIl2Cpp]
    private bool LoadoutVisible()
    {
        if (!lifetime.Running || !active || page == null || popupPage == page) return false;
        try { return page.m_isActive && page.gameObject.activeInHierarchy; }
        catch (Exception e) { Plugin.Logger.LogDebug(e.Message); return false; }
    }

    [HideFromIl2Cpp]
    private bool PanelVisible()
    {
        var size = PanelSize();
        return PanelAnchor.PanelShown(open, active, LoadoutVisible(), size.x, size.y);
    }

    private void OnGUI()
    {
        if (!LoadoutVisible()) return;
        var size = PanelSize();
        bool shown = open && PanelVisible();
        if (shown && HandleKeys(size)) return;
        if (Event.current.type != EventType.Repaint) return;
        if (text is null) BuildStyles();
        int original = GUI.depth;
        GUI.depth = -100;
        try
        {
            var panel = PanelRect(size);
            if (shown)
            {
                GUI.BeginGroup(panel);
                try { Table(size.x, size.y); }
                finally { GUI.EndGroup(); }
            }
            // Last paint, strictly clipped to one of our surfaces. Everywhere else use the native cursor.
            DrawPanelCursor(shown && PanelHit.Inside(ToScreenRect(panel), cursor.X, cursor.Y) ? panel : default);
        }
        finally { GUI.depth = original; }
    }

    // IMGUI 鼠标不可假设可用：滚动/翻页全部有键位，Esc 与页切换保持原样（只拦自己用到的键）。
    [HideFromIl2Cpp]
    private bool HandleKeys(Vector2 size)
    {
        var e = Event.current;
        if (e.type != EventType.KeyDown) return false;
        float viewport = ViewportHeight(size.y);
        switch (e.keyCode)
        {
            case KeyCode.Escape: Close(); e.Use(); return true;
            case KeyCode.F:
                sessionMatches = !(sessionMatches ?? Plugin.ShowMatches.Value); scroll = Vector2.zero; next = 0; e.Use(); return false;
            case KeyCode.UpArrow: scroll = new Vector2(scroll.x, model.Scroll(scroll.y, -PanelModel.RowHeight, viewport)); e.Use(); return false;
            case KeyCode.DownArrow: scroll = new Vector2(scroll.x, model.Scroll(scroll.y, PanelModel.RowHeight, viewport)); e.Use(); return false;
            case KeyCode.PageUp: scroll = new Vector2(scroll.x, model.Scroll(scroll.y, -model.PageStep(viewport), viewport)); e.Use(); return false;
            case KeyCode.PageDown: scroll = new Vector2(scroll.x, model.Scroll(scroll.y, model.PageStep(viewport), viewport)); e.Use(); return false;
            case KeyCode.Home: scroll = Vector2.zero; e.Use(); return false;
            case KeyCode.End: scroll = new Vector2(scroll.x, model.MaxScroll(viewport)); e.Use(); return false;
            default: return false;
        }
    }

    // ---------------------------------------------------------------- 鼠标：大厅自己的光标 + 主按键

    /// <summary>
    /// 每帧一次的大厅实体鼠标处理。输入只用大厅页自己的东西：
    ///   * 光标：<c>CM_PageLoadout.CursorWorldPosition</c> → 菜单相机 <c>WorldToScreenPoint</c> → 唯一一次 Y 翻转；
    ///   * 按键：<c>CM_PageBase.MainButtonStatus</c> 的按下边沿（不是系统鼠标，不做全局捕获）；
    ///   * 滚轮：<c>CM_PageBase.ScrollInput</c> 优先，物理滚轮只在原生没有新读数时兜底。
    /// 命中用的是 OnGUI 画出来的同一块屏幕矩形（<see cref="CloseButtonRect"/> / <see cref="ListLocalRect"/>）；
    /// 只在页面活跃、面板确实显示时处理，面板外不拦不抢；面板隐藏 / 换页 / 失焦 / 卸载都会复位按压与滚轮状态。
    /// </summary>
    [HideFromIl2Cpp]
    private void HandleMouse(CM_PageLoadout current)
    {
        if (!Focused() || !LoadoutVisible()) { ResetInput(); ownedPress.Clear(); return; }
        if (!open && !ownedPress.Armed)
        {
            // Read coordinates only; the HUD never claims a native press, wheel or keyboard event.
            cursor = Cursor.visible ? CursorSample.Invalid("系统光标") : SampleCursor(current);
            return;
        }

        // Either native dispatch or Update can arrive first. Check the live level even on a
        // repeated call: release must reach the game. Project/sample at most once per unchanged frame.
        bool down = false;
        bool havePress = lifetime.MouseActions && TryButton(current, out down);
        if (ownedPress.Armed) ownedPress.Observe(havePress && down, Time.realtimeSinceStartup);
        int frame = Time.frameCount;
        if (frame == inputFrame && frameDown == down && havePress) return;
        inputFrame = frame;
        frameDown = down && havePress;
        if (!frameDown) { ownedPress.Clear(); panelHeld = false; }
        if (!havePress) { closePress.Reset(); panelPress.Reset(); railDrag.Reset(); }
        if (!open) return;
        if (!PanelVisible()) { ResetInput(); ownedPress.Clear(); return; }
        var size = PanelSize();
        var closeRect = ToScreenRect(CloseButtonRect(PanelRect(size)));
        var listRect = ListScreenRect(size);
        cursor = SampleCursor(current);

        // 关闭：读不到主按键只等于“这一下不关表”，不再像以前那样连滚轮一起 return 掉。
        if (havePress && closePress.Sample(down, cursor.Valid && PanelHit.Inside(closeRect, cursor.X, cursor.Y)))
        {
            LogInput($"关闭命中：光标 {cursor.X:0.#},{cursor.Y:0.#} 落在关闭区 "
                + $"{closeRect.X:0.#},{closeRect.Y:0.#},{closeRect.Width:0.#},{closeRect.Height:0.#}；"
                + $"来源 {cursor.Source} / 按键 {pressSource}，屏幕 {Screen.width}x{Screen.height}");
            ownedPress.Arm(Time.realtimeSinceStartup);   // 同一次按压不再落到大厅自己的控件上
            Close();
            return;
        }
        if (havePress && panelPress.Sample(down, cursor.Valid && PanelHit.Inside(ToScreenRect(PanelRect(size)), cursor.X, cursor.Y)))
            panelHeld = true;
        var rail = ListRail.Measure(listRect, model.ContentHeight, scroll.y);
        float position = scroll.y;
        if (havePress && railDrag.Sample(rail, cursor, down, ref position))
        {
            scroll = new Vector2(0, position);
            wheelFrame = frame;
            wheel.Suspend();
            return;
        }
        if (wheelFrame == frame) return;
        wheelFrame = frame;
        float viewport = ViewportHeight(size.y);
        if (cursor.Valid && PanelHit.Inside(listRect, cursor.X, cursor.Y))
        {
            float native = ReadNativeScroll(current);
            float physical = ReadPhysicalWheel();
            float delta = wheel.Sample(native, physical, PanelModel.RowHeight, Mathf.Max(PanelModel.RowHeight, viewport));
            if (delta != 0f)
            {
                float before = scroll.y;
                scroll = new Vector2(0f, model.Scroll(scroll.y, delta, viewport));
                if (scroll.y != before)
                {
                    LogInput($"滚轮：原生 {native:0.###} / 物理 {physical:0.###} → {delta:0.#} 像素"
                        + $"（{wheel.Source}），scrollY {scroll.y:0.#}/{model.MaxScroll(viewport):0.#}");
                }
            }
        }
        // 光标离开清单：只清本帧来源、保留已消费的原生指纹（再进来时同一个旧读数不算新读数）。
        else wheel.Suspend();
    }

    /// <summary>大厅光标：只采页面自己的光标；投影失败或不在相机前方一律按“没有命中”处理（不留陈旧坐标）。</summary>
    [HideFromIl2Cpp]
    private CursorSample SampleCursor(CM_PageLoadout current)
    {
        var camera = PointerCamera(current);
        if (camera == null)
        {
            if (!cursorError)
            {
                cursorError = true;
                Plugin.Logger.LogWarning("拿不到渲染大厅的菜单相机，鼠标点「关闭」暂不可用：面板仍可用 Esc 关闭。");
            }
            return CursorSample.Invalid("没有菜单相机");
        }
        Vector3 world;
        try { world = current.CursorWorldPosition; }
        catch (Exception e)
        {
            if (!cursorError) { cursorError = true; Plugin.Logger.LogWarning($"读大厅光标失败，鼠标点「关闭」暂不可用（Esc 仍可关）：{e.Message}"); }
            return CursorSample.Invalid("光标不可用");
        }
        Vector3 screen;
        try { screen = camera.WorldToScreenPoint(world); }
        catch (Exception e)
        {
            if (!cursorError) { cursorError = true; Plugin.Logger.LogWarning($"大厅光标投影失败，鼠标点「关闭」暂不可用（Esc 仍可关）：{e.Message}"); }
            return CursorSample.Invalid("投影失败");
        }
        cursorError = false;
        const string source = "大厅相机投影";
        return PointerMath.TryGui(screen.x, screen.y, screen.z, Screen.height, out float x, out float y, out string note)
            ? new CursorSample(true, x, y, source)
            : CursorSample.Invalid(note);
    }

    /// <summary>
    /// 渲染大厅的菜单相机。参考案实测 <c>m_guiLayer.m_menuCamera.m_camera</c> 才是渲染大厅的那台；
    /// <c>CM_PageBase.Camera</c> 在参考案实测为空，因此只作为后备（后备链还有画布相机、Camera.main）。
    /// 全都取不到时安全降级：不命中、不关闭、不滚动，只写一次警告。
    /// </summary>
    [HideFromIl2Cpp]
    private Camera? PointerCamera(CM_PageLoadout current)
    {
        try { var direct = current.m_guiLayer?.m_menuCamera?.m_camera; if (Usable(direct)) return direct; }
        catch (Exception e) { Plugin.Logger.LogDebug($"取 GuiLayer 菜单相机失败：{e.Message}"); }
        try { var own = CM_PageBase.Camera; if (Usable(own)) return own; }
        catch (Exception e) { Plugin.Logger.LogDebug($"取页面相机失败：{e.Message}"); }
        try
        {
            if (!cameraChecked) { cameraChecked = true; fallbackCamera = Camera.main; }
            if (Usable(fallbackCamera)) return fallbackCamera;
        }
        catch (Exception e) { Plugin.Logger.LogDebug(e.Message); }
        return null;
    }

    [HideFromIl2Cpp]
    private static bool Usable(Camera? camera)
    {
        try { return camera != null && camera.isActiveAndEnabled; }
        catch (Exception e) { Plugin.Logger.LogDebug(e.Message); return false; }
    }

    /// <summary>
    /// 主按键状态：先试大厅页自己的 <c>MainButtonStatus</c>（与游戏自己的按压分发同源），读不到再独立 try 物理主键。
    /// 任一来源可用即用（实际来源记在 <see cref="pressSource"/>）；两个都读不到才返回 false —— 调用方据此只复位
    /// 按压边沿、不跳过滚轮，所以按键读取失败不会把表内其它输入一起卡死。
    /// </summary>
    [HideFromIl2Cpp]
    private bool TryButton(CM_PageLoadout current, out bool down)
    {
        down = false;
        try
        {
            down = current.MainButtonStatus;
            buttonError = false;
            pressSource = PressSource.Page;
            return true;
        }
        catch (Exception e)
        {
            if (!buttonError)
            {
                buttonError = true;
                Plugin.Logger.LogWarning($"读大厅主按键状态失败，改用物理主键（Esc 仍可关）：{e.Message}");
            }
        }
        bool available = ReadPhysicalButton(out down);
        pressSource = available ? PressSource.Physical : PressSource.None;
        return available;
    }

    /// <summary>
    /// 唯一的物理主键读取点（左键是否按住）。只读不捕获：不隐藏 / 不锁定系统光标、不消费事件、不改任何全局输入状态。
    /// 新输入系统下该 API 会抛异常，这里吞掉并按“没按下”返回（于是这一帧不关表、也不拦大厅输入）。
    /// </summary>
    [HideFromIl2Cpp]
    private static bool ReadPhysicalButton(out bool down)
    {
        try { down = Input.GetMouseButton(0); return true; }
        catch (Exception e) { Plugin.Logger.LogDebug(e.Message); down = false; return false; }
    }

    /// <summary>大厅页自己的滚轮读数：优先来源；取不到按 0 处理，交给物理滚轮兜底。</summary>
    [HideFromIl2Cpp]
    private float ReadNativeScroll(CM_PageLoadout current)
    {
        try { float value = current.ScrollInput; scrollError = false; return float.IsFinite(value) ? value : 0f; }
        catch (Exception e)
        {
            if (!scrollError) { scrollError = true; Plugin.Logger.LogDebug($"读大厅滚轮失败，改用物理滚轮：{e.Message}"); }
            return 0f;
        }
    }

    /// <summary>
    /// 唯一的物理滚轮读取点（Unity mouseScrollDelta.y，向下为负），在大厅 ScrollInput 没有新读数时兜底。
    /// 只读不捕获：不隐藏/锁定系统光标、不改任何全局输入状态；调用方保证只在面板可见且光标落在清单内时才到这里。
    /// </summary>
    [HideFromIl2Cpp]
    private static float ReadPhysicalWheel()
    {
        try { var delta = Input.mouseScrollDelta; return float.IsFinite(delta.y) ? delta.y : 0f; }
        catch (Exception e) { Plugin.Logger.LogDebug(e.Message); return 0f; }
    }

    [HideFromIl2Cpp]
    private static bool Focused()
    {
        try { return Application.isFocused; }
        catch (Exception e) { Plugin.Logger.LogDebug(e.Message); return true; }
    }

    [HideFromIl2Cpp]
    private void ResetInput()
    {
        closePress.Reset();
        panelPress.Reset(); railDrag.Reset(); panelHeld = false;
        inputFrame = -1;
        wheel.Reset();
        cursor = CursorSample.Invalid("已复位");
    }

    // Native dispatch may precede Update. HandleMouse shares its frame/edge guards with both callers.
    [HideFromIl2Cpp]
    internal bool SuppressLobbyPress(CM_PageBase candidate)
    {
        try
        {
            if (!lifetime.MouseActions || (!open && !ownedPress.Armed) || candidate == null || page == null || !SamePage(candidate, page)) return false;
            HandleMouse(page);
            return (open && (panelHeld || railDrag.Held)) || ownedPress.ShouldSuppress(true, active, Time.realtimeSinceStartup);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogDebug(e.Message);
            ResetInput();
            ownedPress.Clear();     // 判定出错就不拦：宁可放过大厅输入
            return false;
        }
    }

    /// <summary>同一个大厅页对象（比较原生指针；取不到指针时退回托管引用比较）。</summary>
    [HideFromIl2Cpp]
    private static bool SamePage(CM_PageBase a, CM_PageLoadout? b)
    {
        if (b == null) return false;
        try { return a.Pointer == b.Pointer; }
        catch (Exception e) { Plugin.Logger.LogDebug(e.Message); return ReferenceEquals(a, b); }
    }

    /// <summary>限频输入日志：只记录真正命中 close / 滚轮的那一下，最多 1 秒 1 行、一局 40 行，不含玩家信息。</summary>
    [HideFromIl2Cpp]
    private void LogInput(string message)
    {
        double now = Time.realtimeSinceStartup;
        if (inputLogs >= 40 || now - inputLogAt < 1.0) return;
        inputLogAt = now;
        inputLogs++;
        Plugin.Logger.LogInfo($"[鼠标] {message}");
    }

    [HideFromIl2Cpp]
    private void BuildStyles()
    {
        var label = new GUIStyle(GUI.skin.label) { fontSize = 16, richText = false, wordWrap = false, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
        var title = new GUIStyle(label) { fontStyle = FontStyle.Bold };
        // 关闭不再用 GUI.Button：GTFO 大厅里 IMGUI 的鼠标事件不可靠，按压由 HandleMouse 采样大厅光标 + 主按键决定，
        // 这里只负责画（居中的标签样式），保证“看到的矩形”就是“命中的矩形”。
        var closeStyle = new GUIStyle(label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        if (!fontTried)
        {
            fontTried = true;
            try { font = CreateReadableFont(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"创建中文字体失败，改用默认字体绘制：{e.GetType().Name}: {e.Message}"); }
        }
        if (font is not null) { label.font = font; title.font = font; closeStyle.font = font; }
        text = label; heading = title; close = closeStyle;
        // 表头玩家名单独一套：richText 恒为 false，颜色由逐段绘制时临时设置，远端昵称不会被当富文本解释。
        nameRun = new GUIStyle(title) { richText = false };
        green = Tint(label, new(.92f, .91f, .85f)); red = Tint(label, Accent); gray = Tint(label, new(.78f, .78f, .74f));
    }

    private static GUIStyle Tint(GUIStyle baseStyle, Color color)
    {
        var style = new GUIStyle(baseStyle);
        style.normal.textColor = color;
        return style;
    }

    [HideFromIl2Cpp]
    private void Table(float w, float h)
    {
        Paint(new Rect(0, 0, w, h), new(.075f, .075f, .07f, 1f));
        Paint(new Rect(0, 0, w, 2), Accent);
        GUI.Label(new Rect(20, 12, w - 110, 31), model.Solo ? "本机模组清单" : "团队模组版本差异", heading!);
        // 关闭：用 CloseLocalRect 画、用 CloseButtonRect 命中（同一组数字）；高亮只看这一帧采到的光标。
        var closeLocal = CloseLocalRect(w);
        bool closeHot = lifetime.MouseActions && cursor.Valid && PanelHit.Inside(ToScreenRect(CloseButtonRect(PanelRect(new Vector2(w, h)))), cursor.X, cursor.Y);
        Paint(closeLocal, closeHot ? new(.42f, .34f, .11f) : new(.20f, .20f, .17f));
        GUI.Label(closeLocal, lifetime.MouseActions ? "关闭" : "Esc", close!);
        GUI.Label(new Rect(20, 48, w - 40, 28), model.Status, gray!);
        float nameW = w * .32f, colW = (w - 54 - nameW) / Math.Max(1, model.Columns.Length);
        Paint(new Rect(14, 80, w - 28, 33), new(.15f, .15f, .13f));
        GUI.Label(new Rect(22, 80, nameW - 6, 33), "模组名称", heading!);
        for (int p = 0; p < model.Columns.Length; p++)
        {
            var column = model.Columns[p];
            var runs = p < headerRuns.Length ? headerRuns[p] : NameMarkup.Resolve(column.Id, headerLocalId, headerNames, column.Name);
            var head = new Rect(22 + nameW + p * colW, 80, Mathf.Max(1f, colW - 8f), 33);
            if (column.Id == selected)
            {
                Paint(new Rect(head.x, head.yMax - 2, head.width, 2), Accent);
                GUI.Label(new Rect(head.x, head.y, 43f, head.height), "所选", red!);
                head.x += 43f; head.width = Mathf.Max(1, head.width - 43f);
            }
            DrawRuns(head, runs, nameRun!);
        }
        var list = ListLocalRect(new Vector2(w, h));
        float viewport = list.height, bodyW = w - 50;
        const float rh = PanelModel.RowHeight;
        // 不用 BeginScrollView：它会自己再消费一次 IMGUI 滚轮，与 HandleMouse 的滚动叠成双倍。
        // 只用 BeginGroup 裁切，滚动量完全来自 Update 里的滚动状态（虚拟化与首末行逻辑不变）。
        scroll = new Vector2(0f, model.Scroll(scroll.y, 0f, viewport));
        GUI.BeginGroup(list);
        try
        {
            int first = model.FirstVisible(scroll.y);
            int last = Math.Min(model.Lines.Length, first + Mathf.CeilToInt(list.height / rh) + 2);
            for (int i = first; i < last; i++)
            {
                var line = model.Lines[i];
                float y = i * rh - scroll.y;
                switch (line.Kind)
                {
                    case PanelLineKind.Header:
                        Paint(new Rect(0, y, bodyW, rh), new(.16f, .16f, .14f));
                        GUI.Label(new Rect(8, y, bodyW - 16, rh), line.Text, heading!);
                        continue;
                    case PanelLineKind.Hint:
                        GUI.Label(new Rect(8, y, bodyW - 16, rh), line.Text, gray!);
                        continue;
                }
                var row = line.Row!;
                if ((i & 1) == 0) Paint(new Rect(0, y, bodyW, rh), new(.105f, .105f, .095f));
                GUI.Label(new Rect(8, y, nameW - 6, rh), Clean(row.Name), text!);
                for (int p = 0; p < row.Cells.Length; p++)
                {
                    var cell = row.Cells[p];
                    var cellRect = new Rect(8 + nameW + p * colW, y, Mathf.Max(1, colW - 8), rh);
                    if (!model.Solo && cell.Kind is CellKind.Highest or CellKind.Lower)
                    {
                        GUI.Label(new Rect(cellRect.x, y, 20, rh), cell.Kind == CellKind.Highest ? "↑" : "↓", cell.Kind == CellKind.Highest ? green! : red!);
                        cellRect.x += 20; cellRect.width = Mathf.Max(1, cellRect.width - 20);
                    }
                    GUI.Label(cellRect, Clean(cell.Text), cell.Kind == CellKind.Highest ? green! : cell.Kind == CellKind.Lower ? red! : gray!);
                }
            }
        }
        finally { GUI.EndGroup(); }
        DrawRail(ListRail.Measure(ToScreenRect(list), model.ContentHeight, scroll.y));
        GUI.Label(new Rect(20, h - 32, w - 40, 25), model.Solo
            ? "Esc 退出 · 滚轮或 ↑↓/PgUp/PgDn/Home/End 滚动 · 只列本机已加载的插件"
            : "↑ 已知最高    ↓ 低于最高    F 全部/差异    滚轮 / 方向键滚动    Esc 关闭", gray!);
    }

    internal static readonly Color Accent = new(.98f, .80f, .25f);

    [HideFromIl2Cpp]
    private void DrawRail(ListRail rail)
    {
        if (!rail.Visible) return;
        var t = rail.Track; var b = rail.Thumb;
        Paint(new Rect(t.X, t.Y, t.Width, t.Height), new(.19f, .19f, .16f));
        Paint(new Rect(b.X + 2, b.Y, b.Width - 4, b.Height), railDrag.Held ? Color.white : Accent);
        Paint(new Rect(b.X + 5, b.Y + b.Height * .5f, b.Width - 10, 2), Color.black);
    }

    [HideFromIl2Cpp]
    private void DrawPanelCursor(Rect panel)
    {
        // Only replace the menu cursor where our opaque sheet actually covers it. The group's
        // clip keeps even the tail inside the sheet. Do not hide, move or lock the game cursor.
        if (!cursor.Valid || Cursor.visible || !PanelHit.Inside(ToScreenRect(panel), cursor.X, cursor.Y)) return;
        float x = cursor.X - panel.x, y = cursor.Y - panel.y;
        GUI.BeginGroup(panel);
        try
        {
            Paint(new Rect(x, y, 4, 15), Color.black);
            Paint(new Rect(x, y, 13, 4), Color.black);
            Paint(new Rect(x, y, 2, 13), Accent);
            Paint(new Rect(x, y, 11, 2), Accent);
        }
        finally { GUI.EndGroup(); }
    }

    /// <summary>
    /// 按段画名字（richText=false）：每段用同一套字体单独量宽、单独画，颜色只作用在该段文字上。
    /// 远端昵称不会进入 IMGUI 富文本通道；超出列宽的段按剩余宽度裁掉。
    /// </summary>
    private static void DrawRuns(Rect rect, NameRun[] runs, GUIStyle style)
    {
        float x = rect.x, limit = rect.x + rect.width;
        var original = style.normal.textColor;
        try
        {
            foreach (var run in runs)
            {
                if (x >= limit) break;
                string text = Clean(run.Text);
                if (text.Length == 0) continue;
                style.normal.textColor = run.Color is { } color ? new Color32(color.R, color.G, color.B, 255) : original;
                var content = new GUIContent(text);
                float width = style.CalcSize(content).x;
                GUI.Label(new Rect(x, rect.y, Mathf.Min(width + 2f, limit - x), rect.height), content, style);
                x += width;
            }
        }
        finally { style.normal.textColor = original; }
    }
    private static string Clean(string s) => s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

    internal static void Paint(Rect rect, Color color)
    {
        var previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }
}
