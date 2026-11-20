using System;
using CellMenu;
using Il2CppInterop.Runtime;
using TMPro;
using Steamworks;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace TeamModVersions;

// Managed owner; never injected into Unity and never searches a HUD/global canvas.
internal sealed class LobbyButtons
{
    private sealed record Entry(CM_PlayerLobbyBar Bar, Transform Parent, ulong Player, CM_Item Item,
        RectTransform Rect, BoxCollider2D Hit, SpriteRenderer Plate);
    private readonly Dictionary<int, Entry> entries = new();
    // Clothes geometry per pillar, in that pillar's own units, kept across the frames where the game hides
    // that button (in-level Esc). Keyed by native pointer; dropped with the page, on resize, off the roster.
    private readonly Dictionary<IntPtr, Memory> remembered = new();
    private CM_PageLoadout? owner;
    private Action<ulong>? select;
    private Sprite? square;

    // The BOT has no CM_Item. Mirror the actual local chip in the BOT pillar's own space.
    private Entry? localChip;
    internal bool CompareRect(int column, CM_PlayerLobbyBar bar, bool bot, out Transform? owner, out Rect bounds, out float z)
    {
        owner = null; bounds = default; z = 0;
        var e = bot ? localChip : entries.GetValueOrDefault(column);
        if (e == null || e.Rect == null || !e.Rect.gameObject.activeInHierarchy) return false;
        bounds = e.Rect.rect;
        if (!bot) { if (e.Bar != bar) return false; owner = e.Rect; }
        else
        {
            var root = bar.m_hasPlayerRoot;
            if (root == null || !root.activeInHierarchy) return false;
            var p = e.Rect.localPosition;
            owner = root.transform; bounds = new Rect(bounds.x + p.x, bounds.y + p.y, bounds.width, bounds.height); z = p.z;
        }
        return true;
    }

    internal void Update(CM_PageLoadout page, Action<ulong> choose)
    {
        if (owner != page) Clear();
        if (page == null || !page.m_isActive || !page.gameObject.activeInHierarchy || page.m_playerLobbyBars == null)
        { Clear(); return; }
        owner = page;
        select = choose;
        try { Reconcile(page); }
        catch { Clear(); throw; } // A failed read/build must not leave an obsolete clickable entry.
    }

    internal void Clear()
    {
        select = null;
        owner = null;
        localChip = null;
        foreach (var entry in entries.Values) Retire(entry.Item);
        entries.Clear();
        remembered.Clear();
        if (square != null) Object.Destroy(square);
        square = null;
    }

    private static void Retire(CM_Item? item)
    {
        if (item == null) return;
        item.gameObject.SetActive(false); // Destroy is deferred; disable the native hit this frame.
        item.OnBtnPressCallback = null;
        Object.Destroy(item.gameObject);
    }

    private static ulong Player(CM_PlayerLobbyBar bar)
    {
        var p = bar.m_player;
        if (p == null || p.IsBot) return 0;
        if (p.IsLocal)
        {
            ulong steam = SteamUser.GetSteamID().m_SteamID;
            if (steam != 0) return steam;
        }
        return p.Lookup;
    }

    private void Press(int slot, Entry expected)
    {
        // Re-read the actual slot on press. A roster swap can precede the driver's next refresh.
        if (!entries.TryGetValue(slot, out var live) || !ReferenceEquals(live, expected) || owner == null ||
            !owner.m_isActive || live.Item == null || !live.Item.gameObject.activeInHierarchy) return;
        var bars = owner.m_playerLobbyBars;
        if (bars == null || slot >= bars.Length) return;
        var bar = bars[slot];
        if (bar != null && bar == live.Bar && bar.m_hasPlayerRoot != null && bar.m_hasPlayerRoot.transform == live.Parent &&
            Player(bar) == live.Player && (!bar.m_loadoutHidden || Drawn(bar, bar.m_hasPlayerRoot.transform))) select?.Invoke(live.Player);
    }

    private readonly record struct Anchor(ButtonBox Box, float Z);
    private sealed record Seat(CM_PlayerLobbyBar Bar, Transform Parent, ulong Player, bool Local, Anchor? Clothes,
        Anchor? Remembered, Anchor[] Gear);
    // Remembered clothes geometry is only valid for the pillar and the screen it was measured on.
    private sealed record Memory(IntPtr Root, int ScreenW, int ScreenH, Anchor Box);

    private void Reconcile(CM_PageLoadout page)
    {
        var bars = page.m_playerLobbyBars;
        var seats = new Seat?[bars.Length];
        var obstacles = new List<Bounds>(); // World coordinates only during collection; includes neighbouring names/controls.
        Anchor? template = null;
        var seen = new HashSet<IntPtr>();
        for (int i = 0; i < bars.Length; ++i)
        {
            var bar = bars[i];
            if (bar == null || bar.m_hasPlayerRoot == null) continue;
            var parent = bar.m_hasPlayerRoot.transform;
            var clothes = ReadAnchor(parent, bar.m_clothesButton);
            var gear = Gear(parent, bar, obstacles);
            // The game drops this pillar's clothes button in-level while keeping the board, the names and
            // the weapon column drawn. Judge by what is drawn, not by the flag alone, or the in-level Esc
            // view loses every real player's chip.
            bool drawn = clothes != null || gear.Length > 0 || NameDrawn(bar);
            ulong player = parent.gameObject.activeInHierarchy && (!bar.m_loadoutHidden || drawn) ? Player(bar) : 0;
            bool local = player != 0 && bar.m_player.IsLocal;
            var memory = Remember(bar, parent, clothes, seen);
            seats[i] = new(bar, parent, player, local, clothes, memory, gear);
            if (local) template = clothes ?? memory;
            // Do not use the bounds of the whole pillar (it contains the 3D prisoner and backdrop).
            if (bar.m_clothesButton != null) Protect(bar.m_clothesButton.transform, obstacles);
            if (bar.m_boosterImplantSlotHolder != null) Protect(bar.m_boosterImplantSlotHolder.transform, obstacles);
            if (bar.m_newClothesIcon != null) Protect(bar.m_newClothesIcon.transform, obstacles);
            Protect(parent.Find($"UI_ModListButton_{i}"), obstacles);
            AddLabel(bar.m_nickText, obstacles);
            AddLabel(bar.m_weaponsHeaderText, obstacles);
            AddLabel(bar.m_boosterImplantsHeaderText, obstacles);
            AddLabel(bar.m_statusText, obstacles);
        }
        foreach (var gone in new List<IntPtr>(remembered.Keys).FindAll(k => !seen.Contains(k))) remembered.Remove(gone);

        var camera = page.m_guiLayer?.m_menuCamera?.m_camera;
        var keep = new HashSet<int>();
        var used = new HashSet<ulong>();
        var boxes = new List<ButtonBox>(obstacles.Count);
        localChip = null;
        var gearBoxes = new List<ButtonBox>(8);
        for (int i = 0; i < seats.Length; ++i)
        {
            if (seats[i] is not { Player: not 0 } seat || Template(seat.Bar) == null || used.Contains(seat.Player)) continue;
            gearBoxes.Clear();
            // Do not treat a half-built lobby as the cold in-level equipment page. During lobby
            // intro only the real clothes row (or its same-page template) can define a final chip.
            if (seat.Bar.m_loadoutHidden || seat.Bar.m_introDone)
                foreach (var gear in seat.Gear) gearBoxes.Add(gear.Box);
            if (ButtonLayout.Anchor(seat.Local, seat.Clothes?.Box, seat.Remembered?.Box, gearBoxes, template?.Box) is not { } anchor)
                continue;
            float z = seat.Clothes == null && seat.Remembered == null && template is {} tpl && tpl.Box == anchor ? tpl.Z : Depth(seat, anchor);
            var parent = seat.Parent;
            var toParent = parent.worldToLocalMatrix;
            boxes.Clear();
            foreach (var world in obstacles) boxes.Add(Project(world, toParent));
            // Neighbor pitch, in this parent's units (including empty columns). Mirror it at the last seat.
            float next = float.PositiveInfinity, previous = float.PositiveInfinity;
            foreach (var other in seats)
            {
                if (other == null || other.Parent == parent) continue;
                float dx = parent.InverseTransformPoint(other.Parent.position).x;
                if (dx > 1) next = Math.Min(next, dx);
                if (dx < -1) previous = Math.Min(previous, -dx);
            }
            float right = anchor.Left + (float.IsFinite(next) ? next : previous) - ButtonLayout.Gap;
            if (camera != null)
            {
                var v = camera.WorldToViewportPoint(parent.TransformPoint(new Vector3(anchor.Right, anchor.Bottom, z)));
                if (v.z <= 0 || v.y < 0 || v.y >= 1) continue;
                var edge = camera.ViewportToWorldPoint(new Vector3(1, v.y, v.z));
                right = Math.Min(right, parent.InverseTransformPoint(edge).x - ButtonLayout.Gap);
            }
            // The anchor is already resolved (live clothes, remembered geometry or drawn equipment), so the
            // planner only has to find the first free gap on that row inside the pillar.
            var plan = ButtonLayout.Place(seat.Player, seat.Local, anchor, null, right, boxes);
            if (plan is not { } chip) continue;
            if (!entries.TryGetValue(i, out var e) || e.Item == null || e.Bar != seat.Bar || e.Parent != parent || e.Player != chip.Player)
            {
                if (e != null) Retire(e.Item);
                entries[i] = e = Build(page, i, seat);
            }
            Apply(e, chip.Rect, z);
            if (seat.Local) localChip = e;
            obstacles.Add(e.Plate.bounds); // Also reserve this chip if another, differently anchored bar encroaches.
            keep.Add(i);
            used.Add(chip.Player);
        }
        foreach (int i in entries.Keys.Where(i => !keep.Contains(i)).ToArray())
        { Retire(entries[i].Item); entries.Remove(i); }
    }

    // Clothes geometry for this pillar: fresh when the button is readable, otherwise the copy kept from a
    // frame where it was. A pillar that never showed the button (cold start in-level) simply has none and
    // falls back to the equipment it draws.
    private Anchor? Remember(CM_PlayerLobbyBar bar, Transform parent, Anchor? clothes, HashSet<IntPtr> seen)
    {
        IntPtr id = bar.Pointer;
        if (id == IntPtr.Zero) return clothes;
        seen.Add(id);
        if (clothes is { } live)
        {
            remembered[id] = new Memory(parent.Pointer, Screen.width, Screen.height, live);
            return live;
        }
        return remembered.TryGetValue(id, out var old) && old.Root == parent.Pointer &&
            old.ScreenW == Screen.width && old.ScreenH == Screen.height ? old.Box : null;
    }

    // The equipment this pillar is really drawing: the stand-in for a hidden clothes button, and the same
    // controls the chip must never cover.
    private static Anchor[] Gear(Transform parent, CM_PlayerLobbyBar bar, List<Bounds> obstacles)
    {
        var slots = bar.GetComponentsInChildren<CM_InventorySlotItem>(false);
        var gear = new List<Anchor>(slots.Length);
        foreach (var slot in slots)
        {
            if (slot == null) continue;
            Protect(slot.transform, obstacles);
            // Intro cards can reveal the HUD, but must not define its geometry.
            if (slot.m_inIntro) continue;
            if (Measure(parent, slot) is { } box) gear.Add(box);
        }
        return gear.ToArray();
    }

    // Same preference as the drawn tile: visible frame, then the click target, then the layout rect.
    private static Anchor? Measure(Transform parent, CM_InventorySlotItem slot)
    {
        Transform? source = null;
        var bounds = default(Bounds);
        var frame = slot.m_edge;
        if (frame != null && frame.enabled && frame.gameObject.activeInHierarchy)
        {
            var world = frame.bounds; // Renderer.bounds is already in world space.
            var native = Project(world, parent.worldToLocalMatrix);
            if (native.Valid) return new(native, parent.InverseTransformPoint(world.center).z);
        }
        // A zero-size frame can still have a valid native collider or rect.
        {
            var hit = slot.m_slotCollider?.TryCast<BoxCollider2D>() ?? slot.m_collider;
            if (hit != null && hit.size.x > 0 && hit.size.y > 0)
            {
                source = hit.transform;
                bounds = new Bounds(hit.offset, hit.size);
            }
            else
            {
                var rt = slot.RectTrans;
                if (rt == null) return null;
                source = rt;
                bounds = new Bounds(rt.rect.center, rt.rect.size);
            }
        }
        if (source == null) return null;
        var m = parent.worldToLocalMatrix * source.localToWorldMatrix;
        var box = Project(bounds, m);
        return box.Valid ? new(box, m.MultiplyPoint3x4(bounds.center).z) : null;
    }

    // The game hides this pillar's loadout UI in-level and only drops the clothes button; the board, the
    // names and the weapon column stay. Same rule as Reconcile, re-read on press.
    private static bool Drawn(CM_PlayerLobbyBar bar, Transform parent)
    {
        if (!parent.gameObject.activeInHierarchy) return false;
        if (ReadAnchor(parent, bar.m_clothesButton) != null || NameDrawn(bar) || AnyGear(bar)) return true;
        return false;
    }

    private static bool AnyGear(CM_PlayerLobbyBar bar)
    {
        foreach (var slot in bar.GetComponentsInChildren<CM_InventorySlotItem>(false))
            if (slot != null && !slot.m_inIntro) return true;
        return false;
    }

    private static bool NameDrawn(CM_PlayerLobbyBar bar)
    {
        var label = bar.m_nickText;
        return label != null && label.isActiveAndEnabled;
    }

    // The stand-in reuses the slot's own edges, so its depth is that slot's depth; a remembered anchor keeps
    // its own. Without any of them the chip stays on the pillar's own plane.
    private static float Depth(Seat seat, ButtonBox anchor)
    {
        if (seat.Clothes is { } live && live.Box == anchor) return live.Z;
        if (seat.Remembered is { } kept && kept.Box == anchor) return kept.Z;
        foreach (var gear in seat.Gear)
            if (gear.Box.Left == anchor.Left && gear.Box.Bottom == anchor.Bottom) return gear.Z;
        return 0;
    }

    // The invite button is the cheapest native CM_Item to clone; the in-level Esc view can drop it while the
    // permission / matchmake controls of the same pillar still exist.
    private static CM_Item? Template(CM_PlayerLobbyBar bar) =>
        bar.m_inviteButton ?? bar.m_permissionButton ?? bar.m_matchmakeButton;

    private static Anchor? ReadAnchor(Transform parent, CM_LobbyScrollItem? clothes)
    {
        if (clothes == null || !clothes.gameObject.activeInHierarchy) return null;
        var hit = clothes.m_slotCollider?.TryCast<BoxCollider2D>() ?? clothes.m_collider;
        Transform source;
        Bounds bounds;
        if (hit != null && hit.isActiveAndEnabled && hit.size.x > 0 && hit.size.y > 0)
        {
            source = hit.transform;
            bounds = new Bounds(hit.offset, hit.size);
        }
        else
        {
            var rt = clothes.RectTrans;
            if (rt == null) return null;
            source = rt;
            bounds = new Bounds(rt.rect.center, rt.rect.size);
        }
        var m = parent.worldToLocalMatrix * source.localToWorldMatrix;
        var box = Project(bounds, m);
        return box.Valid ? new(box, m.MultiplyPoint3x4(bounds.center).z) : null;
    }

    // Transform all the extents, not just the pivot (supports nested scale, offset collider and rotation).
    private static ButtonBox Project(Bounds b, Matrix4x4 m)
    {
        var c = m.MultiplyPoint3x4(b.center);
        var e = b.extents;
        float dx = Math.Abs(m.m00 * e.x) + Math.Abs(m.m01 * e.y) + Math.Abs(m.m02 * e.z);
        float dy = Math.Abs(m.m10 * e.x) + Math.Abs(m.m11 * e.y) + Math.Abs(m.m12 * e.z);
        return new(c.x - dx, c.y - dy, c.x + dx, c.y + dy);
    }

    private static void Protect(Transform? control, List<Bounds> obstacles)
    {
        if (control == null || !control.gameObject.activeInHierarchy) return;
        foreach (var c in control.GetComponentsInChildren<Collider2D>(false)) if (c.enabled) obstacles.Add(c.bounds);
        foreach (var r in control.GetComponentsInChildren<Renderer>(false)) if (r.enabled) obstacles.Add(r.bounds);
    }

    private static void AddLabel(TextMeshPro? label, List<Bounds> obstacles)
    {
        if (label != null && label.isActiveAndEnabled && label.renderer.enabled) obstacles.Add(label.renderer.bounds);
    }

    private Entry Build(CM_PageLoadout page, int slot, Seat seat)
    {
        var template = Template(seat.Bar) ?? throw new InvalidOperationException("Lobby bar without a native button template");
        var item = page.m_guiLayer.AddRectComp(template.gameObject, GuiAnchor.MidLeft,
            Vector2.zero, seat.Parent).Cast<CM_Item>();
        try
        {
            item.gameObject.SetActive(false);
            item.name = $"TeamModVersions_Compare_{slot}";
            item.ID = 23870194 + slot;
            item.m_onBtnPress = new UnityEvent(); // Don't run serialized invite actions from the clone.
            item.OnBtnHoverChanged = null;
            item.m_showOnHover = null;
            item.m_alphaSpriteOnHover = item.m_alphaTextOnHover = item.m_clickBlink = false;
            var rect = item.RectTrans;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
            // Keep the native label and make its root the sole input surface.
            var label = item.GetComponent<TextMeshPro>() ?? item.GetComponentInChildren<TextMeshPro>(true);
            if (label == null) throw new InvalidOperationException("Invite template missing TMP label");
            if (label.transform != rect)
            {
                var lr = label.rectTransform; lr.SetParent(rect,false); lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
                lr.offsetMin = lr.offsetMax = Vector2.zero; lr.localRotation = Quaternion.identity; lr.localScale = Vector3.one;
                lr.gameObject.SetActive(true);
            }
            var hit = item.GetComponent<BoxCollider2D>() ?? item.gameObject.AddComponent<BoxCollider2D>();
            foreach (var c in item.GetComponentsInChildren<Collider2D>(true)) c.enabled = c == hit;
            foreach (var r in item.GetComponentsInChildren<Renderer>(true)) r.enabled = r == label.renderer;
            item.m_collider = hit;
            hit.autoTiling = false;
            hit.edgeRadius = 0;
            hit.offset = Vector2.zero;
            item.SetText(ButtonLayout.Caption);
            label.text = ButtonLayout.Caption;
            label.color = new Color32(255, 212, 76, 255);
            label.fontStyle = FontStyles.Bold;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Truncate;
            label.enableAutoSizing = true;
            label.fontSizeMin = 1;
            label.fontSizeMax = 240; // TMP world text uses font units, not UI pixels; auto-fit within the chip.
            label.margin = new Vector4(3, 1, 3, 1);
            label.alignment = TextAlignmentOptions.Center;

            // One opaque square sized from the same plan as the text transform and native collider.
            var plate = new GameObject("ComparePlate");
            plate.transform.SetParent(rect, false);
            plate.layer = item.gameObject.layer;
            var bg = plate.AddComponent<SpriteRenderer>();
            if (square == null) square = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 1, 1), new Vector2(.5f, .5f), 1, 0, SpriteMeshType.FullRect);
            bg.sprite = square;
            bg.color = new Color32(43, 45, 47, 255);
            bg.sortingLayerID = label.renderer.sortingLayerID;
            bg.sortingOrder = label.renderer.sortingOrder;
            label.renderer.sortingOrder = bg.sortingOrder + 1;
            var entry = new Entry(seat.Bar, seat.Parent, seat.Player, item, rect, hit, bg);
            item.OnBtnPressCallback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>((Action<int>)(_ => Press(slot, entry)));
            return entry;
        }
        catch { Retire(item); throw; }
    }

    private static void Apply(Entry e, ButtonBox b, float z)
    {
        var size = new Vector2(b.Width, b.Height);
        e.Rect.sizeDelta = size;
        e.Rect.localPosition = new Vector3(b.Left + size.x / 2, b.Bottom + size.y / 2, z);
        e.Plate.transform.localScale = new Vector3(size.x, size.y, 1);
        e.Hit.size = size;
        if (!e.Item.gameObject.activeSelf) e.Item.gameObject.SetActive(true);
    }
}
