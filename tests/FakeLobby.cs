namespace BepInEx.Unity.IL2CPP
{
    internal sealed class IL2CPPChainloader
    {
        internal static readonly IL2CPPChainloader Instance = new();
        internal Dictionary<string, PluginInfo> Plugins { get; } = new();
    }
    internal sealed record Metadata(string GUID, string Name, Version Version);
    internal sealed record PluginInfo(Metadata Metadata);
}

namespace SNetwork
{
    internal sealed record LobbyIdentifier(ulong ID);
    internal sealed record GameLobby(LobbyIdentifier Identifier);
    internal sealed record GamePlayer(ulong Lookup, string NickName, int Slot, bool IsBot)
    {
        internal bool IsLocal => !IsBot && Lookup != 0 && Lookup == SNet.LocalId;
        internal int PlayerSlotIndex() => Slot;
    }
    internal static class SNet
    {
        internal static ulong LocalId;
        internal static bool IsInLobby => Lobby is not null;
        internal static GameLobby? Lobby;
        internal static List<GamePlayer> LobbyPlayers = new();
        internal static GamePlayer? LocalPlayer => LobbyPlayers.Find(p => p.IsLocal);
        internal static void SetLobby(ulong id, params ulong[] members)
        {
            Lobby = new(new(id));
            Members = members.ToList();
            LobbyPlayers = members.Select((m, i) => new GamePlayer(m, "P" + m, i, false)).ToList();
        }
        // Steam 大厅名单只算真人；BOT 是游戏内 SNet 玩家，不在大厅成员列表里（和实机一致）。
        internal static List<ulong> Members = new();
        internal static void SetBots(int count)
            => LobbyPlayers.AddRange(Enumerable.Range(0, count).Select(i => new GamePlayer(0, "BOT " + (i + 1), LobbyPlayers.Count + i, true)));
        internal static void Rename(ulong id, string nickname)
        {
            int index = LobbyPlayers.FindIndex(p => !p.IsBot && p.Lookup == id);
            if (index >= 0) LobbyPlayers[index] = LobbyPlayers[index] with { NickName = nickname };
        }
        internal static void PlayAs(ulong id, string version)
        {
            LocalId = id;
            var plugins = BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins;
            plugins.Clear();
            plugins.Add("test.mod", new(new("test.mod", "Test Mod", Version.Parse(version))));
        }
        internal static void PlayAs(ulong id, params (string Guid, string Name, string Version)[] plugins)
        {
            LocalId = id;
            var map = BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins;
            map.Clear();
            foreach (var (guid, name, version) in plugins)
                map[guid] = new(new(guid, name, Version.Parse(version)));
        }
    }
}

namespace Steamworks
{
    using SNetwork;
    internal readonly record struct CSteamID(ulong m_SteamID);
    internal static class SteamUser { internal static CSteamID GetSteamID() => new(SNet.LocalId); }
    internal static class SteamFriends { internal static string GetFriendPersonaName(CSteamID id) => "P" + id.m_SteamID; }
    internal static class SteamMatchmaking
    {
        private static readonly Dictionary<(ulong Lobby, ulong Member, string Key), string> Data = new();
        private static void RequireLobby(CSteamID lobby)
        {
            if (lobby.m_SteamID != SNet.Lobby?.Identifier.ID || SNet.LocalPlayer is null)
                throw new InvalidOperationException("Not a lobby member.");
        }
        internal static int GetNumLobbyMembers(CSteamID lobby) { RequireLobby(lobby); return SNet.Members.Count; }
        internal static CSteamID GetLobbyMemberByIndex(CSteamID lobby, int index) { RequireLobby(lobby); return new(SNet.Members[index]); }
        internal static string GetLobbyMemberData(CSteamID lobby, CSteamID member, string key)
        {
            RequireLobby(lobby);
            return Data.GetValueOrDefault((lobby.m_SteamID, member.m_SteamID, key), "");
        }
        internal static void SetLobbyMemberData(CSteamID lobby, string key, string value)
        {
            RequireLobby(lobby);
            Data[(lobby.m_SteamID, SNet.LocalId, key)] = value;
        }
    }
}

namespace TeamModVersions
{
    internal static class Plugin { internal static readonly TestLog Logger = new(); }
    internal sealed class TestLog { internal void LogWarning(object message) => Console.WriteLine(message); }
}
