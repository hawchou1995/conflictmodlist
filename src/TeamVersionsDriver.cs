using System.Runtime.CompilerServices;
using CellMenu;
using Il2CppInterop.Runtime;
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
    private CM_PageLoadout? page;
    private CM_Item? button;       // 开表按钮：大厅原生 CM_Item
    private CM_Item? closeHit;     // 关闭命中区：大厅原生 CM_Item，压在 IMGUI “关闭” 下面
    private Transform? anchor;     // 两个原生物件共用的大厅内父节点（已验证能收到大厅按压）
    private Canvas? canvas;        // 大厅画布（可能为 null / WorldSpace：都不作为禁用原生点击的理由）
    private bool closeError, placeError, nameError;
    private float next;
    private bool active, open, syncError, uiError, matches, confirmed, viewAll;
    private Vector2 scroll;
    private Font? font;
    private bool fontTried;
    private GUIStyle? text, heading, nameRun, green, red, gray, close;
    private PlayerColumn[] columns = Array.Empty<PlayerColumn>();
    private PanelModel model = PanelModel.Blank;
    private Dictionary<ulong, NameRun[]> headerNames = new();   // 玩家 ID → 大厅原生名字（颜色分段）
    private NameRun[][] headerRuns = Array.Empty<NameRun[]>();  // 与 columns 同序，绘制时直接取
    private ulong headerLocalId;

    private const float CloseWidth = 64f, CloseHeight = 29f, CloseMarginRight = 18f, CloseMarginTop = 13f;

    [HideFromIl2Cpp]
    internal void SetPage(CM_PageLoadout target, bool value)
    {
        if (!value && page != target) return;
        if (page != target) { RemoveButton(); page = target; }
        active = value;
        if (!active) Close();
        if (button != null) button.gameObject.SetActive(active);
        next = 0;
    }

    [HideFromIl2Cpp]
    internal void CloseForPage(CM_PageLoadout target) { if (page == target) Close(); }

    [HideFromIl2Cpp]
    internal void Shutdown()
    {
        try { inventory.Withdraw(); } catch (Exception e) { Plugin.Logger.LogDebug(e.Message); }
        inventory.Reset();
        Close();
        RemoveButton();
        if (font != null) Destroy(font);
    }

    [HideFromIl2Cpp]
    private void RemoveButton()
    {
        Close();
        RemoveClose();
        if (button != null) { button.gameObject.SetActive(false); Destroy(button.gameObject); }
        button = null; anchor = null; canvas = null;
    }

    // 关闭 / 销毁命中区都可能碰上父节点已被大厅重建（对象已销毁）：一律吞掉并复位，别让异常冒到 Harmony 补丁里。
    [HideFromIl2Cpp]
    private void Close()
    {
        open = false;
        if (closeHit == null) return;
        try { closeHit.gameObject.SetActive(false); }
        catch (Exception e) { Plugin.Logger.LogDebug($"关闭命中区失活失败：{e.Message}"); RemoveClose(); }
    }

    [HideFromIl2Cpp]
    private void RemoveClose()
    {
        if (closeHit == null) return;
        try { closeHit.gameObject.SetActive(false); Destroy(closeHit.gameObject); }
        catch (Exception e) { Plugin.Logger.LogDebug($"销毁命中区失败：{e.Message}"); }
        closeHit = null;
    }

    private void Update()
    {
        float now = Time.realtimeSinceStartup;
        if (now < next) return;
        next = now + 1;
        try
        {
            var current = inventory.Sample(now);
            bool showMatches = Plugin.ShowMatches.Value || viewAll, rosterOk = inventory.RosterConfirmed;
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
        if (active && page != null)
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
            try { PlaceButton(page); PlaceClose(page); uiError = false; }
            catch (Exception e)
            {
                RemoveButton();
                if (!uiError) Plugin.Logger.LogWarning($"创建大厅按钮失败：{e}");
                uiError = true;
            }
        }
    }

    [HideFromIl2Cpp]
    private void PlaceButton(CM_PageLoadout current)
    {
        var bars = current.m_playerLobbyBars;
        if (bars == null) return;
        for (int i = 0; i < bars.Length; i++)
        {
            var bar = bars[i];
            if (bar == null || bar.m_player is not { IsLocal: true } || bar.m_hasPlayerRoot == null || bar.m_inviteButton == null) continue;
            var parent = bar.m_hasPlayerRoot.transform;
            if (button == null || anchor != parent)
            {
                RemoveButton();
                canvas ??= parent.GetComponentInParent<Canvas>();
                var item = current.m_guiLayer.AddRectComp(bar.m_inviteButton.gameObject, (GuiAnchor)4, Vector2.zero, parent).Cast<CM_Item>();
                button = item; anchor = parent;
                item.gameObject.name = $"hawchow_{Plugin.ModName}_Button";
                item.ID = 23870194;
                item.OnBtnPressCallback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>((Action<int>)Toggle);
                item.transform.localScale = new Vector3(.75f, .75f, .75f);
                item.SetText("<#D5EDFF>模组版本对照");
                var rect = item.transform.TryCast<RectTransform>();
                if (rect != null) rect.offsetMax = new Vector2(520f, rect.offsetMax.y);
                item.gameObject.SetActive(true);
            }
            var oldButton = parent.Find($"UI_ModListButton_{i}");
            button!.transform.localPosition = oldButton == null ? new Vector3(-35f, 540f, 0f) : oldButton.localPosition + new Vector3(0f, 50f, 0f);
            return;
        }
        RemoveButton();
    }

    /// <summary>
    /// 在面板“关闭”按钮的位置放一个大厅原生 CM_Item 作为命中区：大厅自己的虚拟光标 + 射线 +
    /// OnBtnPress 分发就能点到它，不依赖 IMGUI 鼠标、不注入全局原始输入、不抢系统光标。
    /// 落点优先用大厅菜单相机（CM_PageBase.Camera）把屏幕点投到父平面，只有屏幕空间 Overlay 才用
    /// 画布中心 + scaleFactor 的等价换算；WorldSpace 或取不到 Canvas 都不再是禁用原生点击的理由。
    /// 渲染组件全部关掉，视觉仍由 IMGUI 的“关闭”按钮提供；面板收起时立即失活，非面板时不拦截。
    /// </summary>
    [HideFromIl2Cpp]
    private void PlaceClose(CM_PageLoadout current)
    {
        if (!PanelVisible())
        {
            // 面板没真画出来（未打开 / 页面不活跃 / 屏幕太小）：命中区必须一起藏起来，
            // 否则面板不可见时仍有一块原生“关闭”会拦截大厅点击（透明关闭区）。open 状态保留，屏幕恢复后自动重现。
            if (closeHit != null) closeHit.gameObject.SetActive(false);
            return;
        }
        if (button == null || anchor == null) { RemoveClose(); return; }
        var rect = CloseButtonRect(PanelRect(PanelSize()));
        if (closeHit == null)
        {
            try { CreateClose(current); }
            catch (Exception e)
            {
                RemoveClose();
                if (!closeError) Plugin.Logger.LogWarning($"创建大厅关闭命中区失败，退回 IMGUI / Esc：{e}");
                closeError = true;
                return;
            }
        }
        if (closeHit == null) return;
        try { PlaceAt(closeHit, rect); }
        catch (Exception e)
        {
            if (!placeError) Plugin.Logger.LogWarning($"摆放大厅关闭命中区失败，退回 IMGUI / Esc：{e}");
            placeError = true;
            RemoveClose();
            return;
        }
        if (!closeHit!.gameObject.activeSelf) closeHit.gameObject.SetActive(true);
    }

    [HideFromIl2Cpp]
    private void CreateClose(CM_PageLoadout current)
    {
        var item = current.m_guiLayer.AddRectComp(button!.gameObject, (GuiAnchor)4, Vector2.zero, anchor).Cast<CM_Item>();
        item.gameObject.name = $"hawchow_{Plugin.ModName}_Close";
        item.ID = 23870195;
        item.OnBtnPressCallback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>((Action<int>)CloseFromPress);
        item.m_useHover = false;
        item.transform.localScale = Vector3.one;
        HideVisuals(item);
        item.gameObject.SetActive(false);
        closeHit = item;
    }

    // 命中区只需要碰撞盒：把所有 Sprite / TMP 关掉，避免它从面板边缘漏出图像（含 hover 高亮）。
    private static void HideVisuals(CM_Item item)
    {
        try
        {
            foreach (var sprite in item.gameObject.GetComponentsInChildren<SpriteRenderer>(true))
                if (sprite != null) sprite.enabled = false;
            foreach (var label in item.gameObject.GetComponentsInChildren<TextMeshPro>(true))
                if (label != null) label.enabled = false;
        }
        catch (Exception e) { Plugin.Logger.LogDebug($"隐藏原生命中区图像失败：{e.Message}"); }
    }

    /// <summary>
    /// 屏幕点 → 关闭按钮父平面上的位置，并顺带量出“每屏幕像素多少本地单位”用于命中盒尺寸。
    /// 屏幕空间 Overlay 画布走 CanvasOffset 等价换算；其余（ScreenSpaceCamera / WorldSpace / 无 Canvas）
    /// 走菜单相机投影。两条路都用真实 API，不把 RenderMode 假设当修复。
    /// </summary>
    [HideFromIl2Cpp]
    private void PlaceAt(CM_Item item, Rect imgui)
    {
        var rt = item.transform.TryCast<RectTransform>();
        var parent = rt?.parent?.TryCast<RectTransform>();
        if (rt == null || parent == null) throw new InvalidOperationException("关闭命中区没有可用的 RectTransform 父节点。");
        var target = PanelAnchor.Center(new ScreenRect(imgui.x, imgui.y, imgui.width, imgui.height), Screen.height);
        Vector3 world;
        ScreenPoint perPixel;
        if (canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay)
        {
            perPixel = PanelAnchor.LocalPerPixel(canvas.scaleFactor);
            world = OverlayToWorld(parent, PanelAnchor.CanvasOffset(target, Screen.width, Screen.height, canvas.scaleFactor));
        }
        else if (!TryProject(parent, new Vector2(target.X, target.Y), out world, out perPixel))
        {
            // 相机投影失败（相机为空/点不在平面上）：只要还有画布就退回“画布中心 + scaleFactor”的等价换算，
            // 不因为投影失败就把原生点击丢掉。
            if (canvas == null) throw new InvalidOperationException("菜单相机无法把屏幕点投到关闭按钮所在平面，且没有画布可退回。");
            if (!placeError) { placeError = true; Plugin.Logger.LogWarning("菜单相机投影失败，关闭命中区改用画布中心 + scaleFactor 的等价换算。"); }
            perPixel = PanelAnchor.LocalPerPixel(canvas.scaleFactor);
            world = OverlayToWorld(parent, PanelAnchor.CanvasOffset(target, Screen.width, Screen.height, canvas.scaleFactor));
        }
        rt.position = new Vector3(world.x, world.y, rt.position.z);
        ApplyHitBox(item, imgui, perPixel);
    }

    [HideFromIl2Cpp]
    private Vector3 OverlayToWorld(RectTransform parent, ScreenPoint offset)
    {
        var canvasRect = canvas!.transform.TryCast<RectTransform>() ?? parent;
        var center = canvasRect.rect.center;
        return canvasRect.TransformPoint(new Vector3(center.x + offset.X, center.y + offset.Y, 0f));
    }

    /// <summary>用大厅菜单相机把屏幕点投到父平面，并用同一套投影量出逐像素本地位移。</summary>
    [HideFromIl2Cpp]
    private bool TryProject(RectTransform parent, Vector2 screen, out Vector3 world, out ScreenPoint perPixel)
    {
        world = default;
        perPixel = default;
        var camera = MenuCamera();
        if (!RectTransformUtility.ScreenPointToWorldPointInRectangle(parent, screen, camera, out var center)) return false;
        if (!RectTransformUtility.ScreenPointToWorldPointInRectangle(parent, screen + new Vector2(CloseWidth, 0f), camera, out var right)) return false;
        if (!RectTransformUtility.ScreenPointToWorldPointInRectangle(parent, screen + new Vector2(0f, CloseHeight), camera, out var up)) return false;
        var dx = parent.InverseTransformVector(right - center);
        var dy = parent.InverseTransformVector(up - center);
        perPixel = PanelAnchor.LocalPerPixel(new ScreenPoint(dx.x, dx.y), new ScreenPoint(dy.x, dy.y), CloseWidth, CloseHeight);
        world = center;
        return true;
    }

    /// <summary>大厅菜单相机：优先 CM_PageBase.Camera（游戏自己的菜单相机），其次画布相机，最后 Camera.main。</summary>
    [HideFromIl2Cpp]
    private Camera? MenuCamera()
    {
        try { var menu = CM_PageBase.Camera; if (menu != null) return menu; }
        catch (Exception e) { Plugin.Logger.LogDebug($"取大厅菜单相机失败：{e.Message}"); }
        try { if (canvas != null) { var own = canvas.worldCamera; if (own != null) return own; } }
        catch (Exception e) { Plugin.Logger.LogDebug($"取画布相机失败：{e.Message}"); }
        try { return Camera.main; }
        catch (Exception e) { Plugin.Logger.LogDebug(e.Message); return null; }
    }

    // 命中盒尺寸跟着“实测比例”走；比例不可信时保留 prefab 原尺寸，但仍继续摆放（不因此关掉原生点击）。
    [HideFromIl2Cpp]
    private void ApplyHitBox(CM_Item item, Rect imgui, ScreenPoint perPixel)
    {
        if (!PanelAnchor.IsSane(perPixel))
        {
            if (!placeError)
            {
                placeError = true;
                Plugin.Logger.LogWarning($"关闭命中区换算比例不可信（{perPixel.X:0.###}/{perPixel.Y:0.###} 本地单位每像素），保留原碰撞盒大小。");
            }
            return;
        }
        var hit = item.m_collider;
        if (hit == null) return;
        var size = PanelAnchor.BoxSize(perPixel, imgui.width, imgui.height);
        hit.size = new Vector2(size.X, size.Y);
        hit.offset = Vector2.zero;
    }

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
    private void Toggle(int _) { open = !open; if (open) { scroll = Vector2.zero; next = 0; } else Close(); }

    [HideFromIl2Cpp]
    private void CloseFromPress(int _) => Close();

    private static Vector2 PanelSize() => new(Mathf.Min(1180f, Screen.width - 44f), Mathf.Min(660f, Screen.height - 56f));

    private static Rect PanelRect(Vector2 size) => new((Screen.width - size.x) * .5f, (Screen.height - size.y) * .5f, size.x, size.y);

    private static Rect CloseButtonRect(Rect panel) => new(panel.x + panel.width - CloseMarginRight - CloseWidth, panel.y + CloseMarginTop, CloseWidth, CloseHeight);

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

    // 面板是否真的画出来了：PlaceClose 与 OnGUI 必须共用同一条判定（见 PanelAnchor.PanelShown）。
    [HideFromIl2Cpp]
    private bool PanelVisible()
    {
        if (!open || !active || page == null) return false;
        bool alive;
        try { alive = page.gameObject.activeInHierarchy; }
        catch (Exception e) { Plugin.Logger.LogDebug(e.Message); return false; }
        var size = PanelSize();
        return PanelAnchor.PanelShown(open, active, alive, size.x, size.y);
    }

    private void OnGUI()
    {
        if (!PanelVisible()) return;
        var size = PanelSize();
        if (HandleKeys(size)) return;
        if (text is null) BuildStyles();
        int original = GUI.depth;
        GUI.depth = -100;
        GUI.BeginGroup(PanelRect(size));
        try { Table(size.x, size.y); }
        finally { GUI.EndGroup(); GUI.depth = original; }
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
                viewAll = !viewAll; scroll = Vector2.zero; next = 0; e.Use(); return false;
            case KeyCode.UpArrow: scroll = new Vector2(scroll.x, model.Scroll(scroll.y, -PanelModel.RowHeight, viewport)); e.Use(); return false;
            case KeyCode.DownArrow: scroll = new Vector2(scroll.x, model.Scroll(scroll.y, PanelModel.RowHeight, viewport)); e.Use(); return false;
            case KeyCode.PageUp: scroll = new Vector2(scroll.x, model.Scroll(scroll.y, -model.PageStep(viewport), viewport)); e.Use(); return false;
            case KeyCode.PageDown: scroll = new Vector2(scroll.x, model.Scroll(scroll.y, model.PageStep(viewport), viewport)); e.Use(); return false;
            case KeyCode.Home: scroll = Vector2.zero; e.Use(); return false;
            case KeyCode.End: scroll = new Vector2(scroll.x, model.MaxScroll(viewport)); e.Use(); return false;
            default: return false;
        }
    }

    [HideFromIl2Cpp]
    private void BuildStyles()
    {
        var label = new GUIStyle(GUI.skin.label) { fontSize = 16, richText = false, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
        var title = new GUIStyle(label) { fontStyle = FontStyle.Bold };
        var press = new GUIStyle(GUI.skin.button) { fontSize = 16 };
        if (!fontTried)
        {
            fontTried = true;
            try { font = CreateReadableFont(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"创建中文字体失败，改用默认字体绘制：{e.GetType().Name}: {e.Message}"); }
        }
        if (font is not null) { label.font = font; title.font = font; press.font = font; }
        text = label; heading = title; close = press;
        // 表头玩家名单独一套：richText 恒为 false，颜色由逐段绘制时临时设置，远端昵称不会被当富文本解释。
        nameRun = new GUIStyle(title) { richText = false };
        green = Tint(label, new(.40f, .91f, .63f)); red = Tint(label, new(1f, .48f, .47f)); gray = Tint(label, new(.64f, .69f, .74f));
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
        Paint(new Rect(0, 0, w, h), new(.055f, .075f, .10f, .99f));
        Paint(new Rect(0, 0, w, 2), new(.34f, .63f, .80f));
        GUI.Label(new Rect(20, 12, w - 110, 31), model.Solo ? "本机模组清单" : "团队模组版本差异", heading!);
        if (GUI.Button(new Rect(w - CloseMarginRight - CloseWidth, CloseMarginTop, CloseWidth, CloseHeight), "关闭", close!)) Close();
        GUI.Label(new Rect(20, 48, w - 40, 28), model.Status, gray!);
        float nameW = w * .32f, colW = (w - 54 - nameW) / Math.Max(1, model.Columns.Length);
        Paint(new Rect(14, 80, w - 28, 33), new(.13f, .19f, .24f));
        GUI.Label(new Rect(22, 80, nameW - 6, 33), "模组名称", heading!);
        for (int p = 0; p < model.Columns.Length; p++)
        {
            var column = model.Columns[p];
            var runs = p < headerRuns.Length ? headerRuns[p] : NameMarkup.Resolve(column.Id, headerLocalId, headerNames, column.Name);
            DrawRuns(new Rect(22 + nameW + p * colW, 80, Mathf.Max(1f, colW - 8f), 33), runs, nameRun!);
        }
        float viewport = ViewportHeight(h), bodyW = w - 50;
        const float rh = PanelModel.RowHeight;
        scroll = new Vector2(0f, model.Scroll(scroll.y, 0f, viewport));
        scroll = GUI.BeginScrollView(new Rect(14, 113, w - 28, viewport), scroll,
            new Rect(0, 0, bodyW, Mathf.Max(viewport - 1f, model.ContentHeight)));
        try
        {
            int first = model.FirstVisible(scroll.y);
            int last = Math.Min(model.Lines.Length, first + Mathf.CeilToInt(viewport / rh) + 2);
            for (int i = first; i < last; i++)
            {
                var line = model.Lines[i];
                float y = i * rh;
                switch (line.Kind)
                {
                    case PanelLineKind.Header:
                        Paint(new Rect(0, y, bodyW, rh), new(.12f, .17f, .22f));
                        GUI.Label(new Rect(8, y, bodyW - 16, rh), line.Text, heading!);
                        continue;
                    case PanelLineKind.Hint:
                        GUI.Label(new Rect(8, y, bodyW - 16, rh), line.Text, gray!);
                        continue;
                }
                var row = line.Row!;
                if ((i & 1) == 0) Paint(new Rect(0, y, bodyW, rh), new(.10f, .13f, .17f));
                GUI.Label(new Rect(8, y, nameW - 6, rh), Clean(row.Name), text!);
                for (int p = 0; p < row.Cells.Length; p++)
                {
                    var cell = row.Cells[p];
                    GUI.Label(new Rect(8 + nameW + p * colW, y, Mathf.Max(1, colW - 8), rh), Clean(cell.Text),
                        cell.Kind == CellKind.Highest ? green! : cell.Kind == CellKind.Lower ? red! : gray!);
                }
            }
        }
        finally { GUI.EndScrollView(); }
        GUI.Label(new Rect(20, h - 32, w - 40, 25), model.Solo
            ? "Esc 关闭 · F/↑↓/PgUp/PgDn/Home/End 滚动 · 只列本机已加载的插件"
            : "绿：已知最高    红：低于最高    灰：缺数据    F：全部/差异    PgUp/PgDn：滚动", gray!);
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

    private static void Paint(Rect rect, Color color)
    {
        var previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }
}
