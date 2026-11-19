using CellMenu;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace TeamModVersions;

public sealed class TeamVersionsDriver : MonoBehaviour
{
    public TeamVersionsDriver(IntPtr pointer) : base(pointer) { }
    private readonly LobbyInventory inventory = new();
    private CM_PageLoadout? page;
    private CM_Item? button;
    private Transform? anchor;
    private float next;
    private bool active, open, syncError, uiError, matches, confirmed;
    private Vector2 scroll;
    private Font? font;
    private GUIStyle? text, heading, green, red, gray, close;
    private PlayerColumn[] columns = Array.Empty<PlayerColumn>();
    private ModSections sections = new(Array.Empty<ModRow>(), Array.Empty<ModRow>());

    internal void SetPage(CM_PageLoadout target, bool value)
    {
        if (!value && page != target) return;
        if (page != target) { RemoveButton(); page = target; }
        active = value;
        if (!active) open = false;
        if (button != null) button.gameObject.SetActive(active);
        next = 0;
    }

    internal void CloseForPage(CM_PageLoadout target) { if (page == target) open = false; }

    internal void Shutdown()
    {
        try { inventory.Withdraw(); } catch (Exception e) { Plugin.Logger.LogDebug(e.Message); }
        inventory.Reset();
        RemoveButton();
        if (font != null) Destroy(font);
        open = false;
    }

    private void RemoveButton()
    {
        if (button != null) { button.gameObject.SetActive(false); Destroy(button.gameObject); }
        button = null; anchor = null;
    }

    private void Update()
    {
        float now = Time.realtimeSinceStartup;
        if (now < next) return;
        next = now + 1;
        try
        {
            var current = inventory.Sample(now);
            bool showMatches = Plugin.ShowMatches.Value, rosterOk = inventory.RosterConfirmed;
            if (syncError || showMatches != matches || rosterOk != confirmed || !columns.SequenceEqual(current))
            {
                sections = Matrix.Build(current, showMatches, rosterOk);
                columns = current; matches = showMatches; confirmed = rosterOk;
            }
            syncError = false;
        }
        catch (Exception e)
        {
            if (!syncError) Plugin.Logger.LogWarning($"刷新大厅模组清单失败：{e}");
            syncError = true;
            columns = Array.Empty<PlayerColumn>();
            sections = new(Array.Empty<ModRow>(), Array.Empty<ModRow>());
        }
        if (active && page != null)
        {
            try { PlaceButton(page); uiError = false; }
            catch (Exception e)
            {
                RemoveButton();
                if (!uiError) Plugin.Logger.LogWarning($"创建大厅按钮失败：{e}");
                uiError = true;
            }
        }
    }

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
                var item = current.m_guiLayer.AddRectComp(bar.m_inviteButton.gameObject, (GuiAnchor)4, Vector2.zero, parent).Cast<CM_Item>();
                button = item; anchor = parent;
                item.gameObject.name = "hawchow_confilctmodlist_Button";
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

    private void Toggle(int _) { open = !open; if (open) { scroll = Vector2.zero; next = 0; } }

    private void OnGUI()
    {
        if (!open || !active || page == null || !page.gameObject.activeInHierarchy) return;
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
        { open = false; Event.current.Use(); return; }
        if (text is null)
        {
            font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 18);
            text = new GUIStyle(GUI.skin.label) { font = font, fontSize = 16, richText = false, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            heading = new GUIStyle(text) { fontStyle = FontStyle.Bold };
            close = new GUIStyle(GUI.skin.button) { font = font, fontSize = 16 };
            green = Tint(new(.40f, .91f, .63f)); red = Tint(new(1, .48f, .47f)); gray = Tint(new(.64f, .69f, .74f));
        }
        float w = Mathf.Min(1180, Screen.width - 44f), h = Mathf.Min(660, Screen.height - 56f);
        if (w < 340 || h < 220) return;
        int original = GUI.depth;
        GUI.depth = -100;
        GUI.BeginGroup(new Rect((Screen.width - w) * .5f, (Screen.height - h) * .5f, w, h));
        try { Table(w, h); }
        finally { GUI.EndGroup(); GUI.depth = original; }
    }

    private GUIStyle Tint(Color color)
    {
        var style = new GUIStyle(text!);
        style.normal.textColor = color;
        return style;
    }

    private void Table(float w, float h)
    {
        Paint(new Rect(0, 0, w, h), new(.055f, .075f, .10f, .99f));
        Paint(new Rect(0, 0, w, 2), new(.34f, .63f, .80f));
        GUI.Label(new Rect(20, 12, w - 110, 31), "团队模组版本差异", heading!);
        if (GUI.Button(new Rect(w - 82, 13, 64, 29), "关闭", close!)) open = false;
        int ready = columns.Count(c => c.Mods is not null);
        GUI.Label(new Rect(20, 48, w - 40, 28), syncError ? "同步暂不可用，正在重试。" : inventory.Notice ?? $"已同步 {ready}/{columns.Length} 人 · 仅比较已加载的 BepInEx 插件", gray!);
        float nameW = w * .32f, colW = (w - 54 - nameW) / Math.Max(1, columns.Length);
        Paint(new Rect(14, 80, w - 28, 33), new(.13f, .19f, .24f));
        GUI.Label(new Rect(22, 80, nameW - 6, 33), "模组名称", heading!);
        for (int p = 0; p < columns.Length; p++)
            GUI.Label(new Rect(22 + nameW + p * colW, 80, Mathf.Max(1, colW - 8), 33), Clean(columns[p].Name), heading!);
        int diffCount = sections.Differences.Length, matchCount = sections.Matches.Length;
        int diffLines = Math.Max(1, diffCount);
        int lines = 1 + diffLines + (matchCount == 0 ? 0 : 1 + matchCount);
        const float rh = 28;
        float viewport = h - 150, bodyW = w - 50;
        scroll = GUI.BeginScrollView(new Rect(14, 113, w - 28, viewport), scroll,
            new Rect(0, 0, bodyW, Mathf.Max(viewport - 1, lines * rh)));
        try
        {
            int first = Mathf.Clamp(Mathf.FloorToInt(scroll.y / rh), 0, lines - 1);
            for (int i = first; i < Math.Min(lines, first + Mathf.CeilToInt(viewport / rh) + 2); i++)
            {
                float y = i * rh;
                if (i == 0 || i == diffLines + 1)
                {
                    Paint(new Rect(0, y, bodyW, rh), new(.12f, .17f, .22f));
                    GUI.Label(new Rect(8, y, bodyW - 16, rh), i == 0 ? $"差异 / 待核对 · {diffCount}" : $"全队版本一致 · {matchCount}", heading!);
                    continue;
                }
                if (diffCount == 0 && i == 1)
                {
                    GUI.Label(new Rect(8, y, bodyW - 16, rh), ready == 0 ? "暂无可比较的模组。" : "已报告的清单中无待核对项。", gray!);
                    continue;
                }
                var row = i <= diffLines ? sections.Differences[i - 1] : sections.Matches[i - diffLines - 2];
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
        GUI.Label(new Rect(20, h - 32, w - 40, 25), "绿：已知最高    红：低于最高    灰：缺数据    版本一致不代表兼容", gray!);
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
