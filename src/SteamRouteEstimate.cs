using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SNetwork;
using Steamworks;

namespace TeamModVersions;

// Local Steam relay estimate of this machine's route to one teammate. It is a prediction computed here
// from a ping location the peer never had to send to the player: either the location the game already
// caches for that peer, or the location the peer publishes itself in lobby member data. Nothing here is
// transmitted as a measurement, written to the peer, or used in place of a measured round trip, and no
// value exists for the local player or for BOTs.
//
// Two sources exist because they fail independently. The game-side cache is populated by SNet's own ping
// location exchange (SNet_Core_STEAM::OnPingLocation) and is the cheaper source, but a session can run a
// whole expedition with that cache empty. The lobby exchange only needs the peer to run this build, and it
// carries the peer's own location text, so it still answers when the game-side cache is silent.
internal static class SteamRouteEstimate
{
    // Lobby member data key. Same channel as the version sync, one small string per member.
    internal const string Key = "tmv.ping";

    // sizeof(SteamNetworkPingLocation_t.m_data). A location of any other length is a truncated or
    // misread structure, never a usable marker.
    internal const int MarkerSize = 512;

    private const int TextSize = 1024; // k_cchMaxSteamNetworkingPingLocationString, including NUL
    private const int PlausibleMaxMs = 10000;
    private const long LocalFreshMs = 30000;
    private const long LocalRetryMs = 15000;
    // Diagnostic threshold only; Steam can become ready later.
    private const long RelayStallMs = 60000;

    private static readonly byte[] local = new byte[MarkerSize];
    private static readonly byte[] peer = new byte[MarkerSize];
    private static readonly byte[] text = new byte[TextSize];
    private static readonly HashSet<string> reported = new();
    private static bool unavailable;
    private static bool publishUnavailable;
    private static bool warned;
    private static bool relayRequested;
    private static long relaySince;
    private static IntPtr utils, steamLibrary;
    private static bool resolverRegistered;
    private static long utilsDue;
    private static string? localText;
    private static long localDue;

    // A candidate marker plus the words needed to explain why it is missing. Key is stable for the whole
    // session so one peer can report each distinct outcome exactly once.
    private readonly record struct Marker(byte[]? Bytes, string Key, string Text);

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_SteamNetworkingUtils_SteamAPI_v004", ExactSpelling = true)]
    private static extern IntPtr SteamNetworkingUtils();

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess", ExactSpelling = true)]
    private static extern void InitRelayNetworkAccess(IntPtr self);

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamNetworkingUtils_GetLocalPingLocation", ExactSpelling = true)]
    private static extern float GetLocalPingLocation(IntPtr self, [Out] byte[] location);

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamNetworkingUtils_ConvertPingLocationToString", ExactSpelling = true)]
    private static extern void ConvertPingLocationToString(IntPtr self, [In] byte[] location, [Out] byte[] buffer, int size);

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamNetworkingUtils_ParsePingLocationString", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool ParsePingLocationString(IntPtr self, [MarshalAs(UnmanagedType.LPStr)] string value, [Out] byte[] location);

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamNetworkingUtils_EstimatePingTimeFromLocalHost", ExactSpelling = true)]
    private static extern int EstimatePingTimeFromLocalHost(IntPtr self, [In] byte[] location);

    // This machine's own location, cached for 30 s and published by TeamNetwork. Null means Steam has no
    // relay measurement yet, which is also the reason a peer's row cannot leave unknown: without a local
    // location there is nothing to estimate from.
    internal static string? Local()
    {
        if (unavailable || publishUnavailable) return null;
        long now = Environment.TickCount64;
        if (now < localDue) return localText;
        try
        {
            IntPtr self = Interface();
            if (self == IntPtr.Zero)
            {
                localDue = now + LocalRetryMs; localText = null;
                Once("local/interface", "Steam 路由接口未就绪，队友一侧的路由估值会显示未知");
                return null;
            }
            float age = GetLocalPingLocation(self, local);
            if (age < 0f)
            {
                if (!relayRequested) { InitRelayNetworkAccess(self); relayRequested = true; }
                localDue = now + LocalRetryMs; localText = null;
                if (relaySince == 0)
                {
                    relaySince = now;
                    Once("local/pending", "Steam 中继测量尚未返回本机位置，队友一侧的路由估值会显示未知");
                }
                else if (now - relaySince >= RelayStallMs)
                    Once("local/stalled", $"Steam 中继测量超过 {RelayStallMs / 1000} 秒仍无本机位置（返回值 {age:0.##}），将继续重试");
                return null;
            }
            relaySince = 0;
            Array.Clear(text);
            ConvertPingLocationToString(self, local, text, text.Length);
            int n = Array.IndexOf(text, (byte)0);
            if (n <= 0)
            {
                localDue = now + LocalRetryMs; localText = null;
                Once("local/empty", $"Steam 返回的本机位置标记为空（{n} 字符），队友一侧的路由估值会显示未知");
                return null;
            }
            localText = Encoding.ASCII.GetString(text, 0, n);
            localDue = now + LocalFreshMs;
            Once("local/ready", $"本机位置标记已就绪（{n} 字符）");
            return localText;
        }
        catch (EntryPointNotFoundException e) when (utils != IntPtr.Zero)
        {
            publishUnavailable = true;
            Plugin.Logger.LogWarning($"Steam 本机位置发布不可用：{e.Message}；仍尝试读取队友位置。");
            return null;
        }
        catch (Exception e) { Trap(e); return null; }
    }

    // Route to one remote teammate, or -1 when no source can answer. published is that peer's lobby
    // member data under Key; an empty string means the peer either runs another build or has no location.
    internal static int Read(SNet_Player player, string? published)
    {
        if (unavailable) return -1;
        ulong id;
        try { id = player.Lookup; }
        catch (Exception e) { Trap(e); return -1; }
        try
        {
            IntPtr self = Interface();
            if (self == IntPtr.Zero) return Note(id, "interface", "Steam 路由接口未就绪");

            Marker game;
            try { game = FromGame(player, id); }
            catch (Exception e)
            {
                game = new(null, "game/fault", $"位置读取失败（{e.GetType().Name}）");
                if (reported.Add("game/fault")) Plugin.Logger.LogWarning($"Steam 路由游戏缓存读取失败：{e.GetType().Name}；仍尝试队友发布位置。");
            }
            if (game.Bytes != null)
            {
                int gameMs = Judge(id, self, game);
                if (gameMs >= 0) return gameMs;
            }

            Marker lobby = FromLobby(self, published);
            if (lobby.Bytes != null) return Judge(id, self, lobby);

            return Note(id, $"none/{game.Key}/{lobby.Key}",
                $"游戏缓存：{(game.Bytes != null ? "位置被 Steam 拒绝" : game.Text)}；队友发布：{lobby.Text}；游戏往返读数 {player.Ping}");
        }
        catch (Exception e) { Trap(e); return -1; }
    }

    // The location SNet already holds for this peer. pingLocationUpdateTime is a plain float field that
    // reads back even when no location ever arrived, so it separates "the game never received one" from
    // "the game holds one that cannot be read" - the difference between a silent exchange and a broken one.
    private static Marker FromGame(SNet_Player player, ulong id)
    {
        SNet_SteamPlayerData? data = player.PlatformData?.TryCast<SNet_SteamPlayerData>();
        if (data == null) return new(null, "game/no-profile", "游戏内没有该队友的 Steam 档案");
        if (data.SteamID.m_SteamID != id) return new(null, "game/id-mismatch", "游戏内 Steam 档案与队友编号不一致");

        bool arrived = data.pingLocationUpdateTime > 0f;
        Il2CppStructArray<byte>? bytes = data.pingLocation?.m_data;
        if (bytes == null)
            return new(null, arrived ? "game/null-after-update" : "game/never",
                arrived ? "已更新过位置但数据引用为空" : "从未收到过位置（更新时刻 0）");
        if (bytes.Length != MarkerSize)
            return new(null, "game/length", $"位置长度 {bytes.Length}，不是 {MarkerSize}");

        byte[] copy = new byte[MarkerSize];
        bytes.CopyTo(copy, 0);
        if (Array.TrueForAll(copy, b => b == 0)) return new(null, "game/zero", "位置数据全为 0");
        return new(copy, "game", "游戏缓存位置");
    }

    // The location this peer published for itself. Only a peer running this build writes it.
    private static Marker FromLobby(IntPtr self, string? published)
    {
        if (string.IsNullOrEmpty(published) || published == "-") return new(null, "lobby/absent", "未发布位置标记");
        if (published.Length >= TextSize)
            return new(null, "lobby/length", $"发布的位置标记长度 {published.Length} 不合规");
        if (published.Any(c => c is < ' ' or > '~') || !ParsePingLocationString(self, published, peer))
            return new(null, "lobby/unparsable", "发布的位置标记无法解析");
        return new(peer, "lobby", "队友发布位置");
    }

    private static int Judge(ulong id, IntPtr self, Marker marker)
    {
        int ms = EstimatePingTimeFromLocalHost(self, marker.Bytes!);
        if (ms < 0 || ms > PlausibleMaxMs)
            return Note(id, $"reject/{marker.Key}", $"{marker.Text}已就位，但 Steam 未给出可用估值（返回 {ms}）");
        return Note(id, $"ok/{marker.Key}", $"{marker.Text} → 本机到该队友 {ms} ms", ms);
    }

    // One line per peer per distinct outcome: keyed on a stable token, so a value that drifts every sample
    // cannot write a line every sample.
    private static int Note(ulong peer, string key, string text, int ms = -1)
    {
        if (reported.Add($"{peer}/{key}"))
            Plugin.Logger.LogInfo($"[Steam 路由估值] 队友 {peer}：{text}");
        return ms;
    }

    private static void Once(string key, string text)
    {
        if (reported.Add(key)) Plugin.Logger.LogInfo($"[Steam 路由估值] {text}");
    }

    // Resolve only this plugin's Steam imports. Unity locates the same DLL under its game data path,
    // which is not necessarily part of the CoreCLR loader's search path under r2modman.
    private static IntPtr ResolveSteam(string name, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != "steam_api64") return IntPtr.Zero;
        if (steamLibrary != IntPtr.Zero) return steamLibrary;
        var path = System.IO.Path.Combine(UnityEngine.Application.dataPath, "Plugins", "x86_64", "steam_api64.dll");
        if (!System.IO.Path.IsPathFullyQualified(path)) throw new DllNotFoundException("GTFO data path unavailable");
        // Retain a reference for the lifetime of the cached P/Invoke bindings.
        return steamLibrary = NativeLibrary.Load(path, assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
    }

    private static IntPtr Interface()
    {
        long now = Environment.TickCount64;
        if (now < utilsDue) return utils;
        utilsDue = now + LocalRetryMs;
        if (!resolverRegistered)
        {
            NativeLibrary.SetDllImportResolver(typeof(SteamRouteEstimate).Assembly, ResolveSteam);
            resolverRegistered = true;
        }
        utils = SteamNetworkingUtils();
        if (utils != IntPtr.Zero) Once("interface/ready", "已绑定 GTFO 自带的 Steam 路由接口");
        return utils;
    }

    private static void Trap(Exception e)
    {
        unavailable = e is EntryPointNotFoundException or BadImageFormatException;
        if (!warned)
        {
            warned = true;
            Plugin.Logger.LogWarning($"Steam 路由估值不可用：{e.GetType().Name}（{e.Message}）；原往返读数不受影响。");
        }
        utils = IntPtr.Zero;
        localText = null;
        localDue = utilsDue = Environment.TickCount64 + RelayStallMs;
    }

    internal static void Reset()
    {
        localText = null;
        localDue = 0;
        relayRequested = false;
        relaySince = 0;
    }
}
