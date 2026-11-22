using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using CellMenu;
using HarmonyLib;
using UnityEngine;

namespace TeamModVersions;

[BepInPlugin(Id, ModName, Version)]
public sealed class Plugin : BasePlugin
{
    internal const string ModName = "conflictmodlist";
    internal const string Id = "hawchow." + ModName;
    internal const string Version = "1.1.15";
    internal static ManualLogSource Logger = null!;
    internal static ConfigEntry<bool> ShowMatches = null!;
    internal static TeamVersionsDriver? Driver;
    private Harmony? patches;

    public override void Load()
    {
        Logger = Log;
        ShowMatches = Config.Bind("Display", "ShowMatchingMods", false,
            "默认隐藏全队已加载且版本一致的模组。开启后，在版本差异表末尾单独显示这些模组。此项只影响本机显示。");
        Driver = AddComponent<TeamVersionsDriver>();
        patches = new Harmony(Id);
        patches.PatchAll(typeof(Plugin).Assembly);
        if (PatchLobbyPress(patches)) Driver.EnableMouseActions();
        Logger.LogInfo($"{ModName} 已加载，通过 Steam 大厅同步模组版本。");
    }

    // Keep input ownership optional on game versions without this dispatch point.
    private bool PatchLobbyPress(Harmony harmony)
    {
        try
        {
            var target = AccessTools.Method(typeof(CM_PageBase), "UpdateButtonPress", Type.EmptyTypes);
            if (target == null)
            {
                Logger.LogWarning("大厅没有 CM_PageBase.UpdateButtonPress：面板按压无法拦截，仍可使用键盘。");
                return false;
            }
            return harmony.Patch(target,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(LobbyPressPatch), nameof(LobbyPressPatch.Prefix)))) != null;
        }
        catch (Exception e)
        {
            Logger.LogWarning($"挂大厅按压前缀失败：面板按压无法拦截，仍可使用键盘：{e.Message}");
            return false;
        }
    }


    public override bool Unload()
    {
        var driver = Driver;
        Driver = null;
        try { if (driver != null) driver.Shutdown(); }
        finally
        {
            try { patches?.UnpatchSelf(); }
            finally { if (driver != null) UnityEngine.Object.Destroy(driver); }
        }
        return true;
    }
}

[HarmonyPatch(typeof(CM_PageLoadout), nameof(CM_PageLoadout.SetPageActive))]
internal static class PageActive
{
    [HarmonyPostfix]
    private static void After(CM_PageLoadout __instance, bool __0) => Plugin.Driver?.SetPage(__instance, __0);
}

[HarmonyPatch(typeof(CM_PageLoadout), nameof(CM_PageLoadout.SetPopupVisible), new Type[] { typeof(bool) })]
internal static class PagePopup
{
    [HarmonyPostfix]
    private static void After(CM_PageLoadout __instance, bool __0) => Plugin.Driver?.SetPopupVisible(__instance, __0);
}

// Sample before native dispatch when it runs first. Suppress only a panel-owned down/hold, never release.
internal static class LobbyPressPatch
{
    internal static bool Prefix(CM_PageBase __instance)
    {
        try { return !(Plugin.Driver?.SuppressLobbyPress(__instance) ?? false); }
        catch (Exception e) { Plugin.Logger.LogDebug(e.Message); return true; }
    }
}
