using BepInEx.Unity.IL2CPP;
using SNetwork;
using Steamworks;

namespace TeamModVersions;

internal sealed class LobbyInventory
{
    private ulong lobbyId;
    private readonly Dictionary<ulong, (string Header, ModEntry[] Mods)> peers = new();
    private ModEntry[]? local;
    private (string Header, string[] Parts)? outgoing;
    private float nextPublish;
    internal bool RosterConfirmed { get; private set; }
    internal string? Notice { get; private set; }

    internal void Reset()
    {
        lobbyId = 0;
        peers.Clear();
        nextPublish = 0;
        RosterConfirmed = false;
    }

    internal void Withdraw()
    {
        if (lobbyId != 0 && SNet.IsInLobby && SNet.Lobby?.Identifier?.ID == lobbyId)
            SteamMatchmaking.SetLobbyMemberData(new CSteamID(lobbyId), InventoryWire.HeaderKey, "-");
    }

    internal PlayerColumn[] Sample(float now)
    {
        RosterConfirmed = false;
        Notice = null;
        var self = SNet.LocalPlayer;
        if (self is null) { Reset(); return Array.Empty<PlayerColumn>(); }
        var plugins = IL2CPPChainloader.Instance.Plugins.Values.Select(p => p.Metadata)
            .Where(m => !string.IsNullOrWhiteSpace(m.GUID))
            .Select(m => new ModEntry(m.GUID, m.Name ?? m.GUID, m.Version?.ToString() ?? string.Empty))
            .OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
        if (local is null || !plugins.SequenceEqual(local))
        {
            local = plugins;
            outgoing = null;
            try { outgoing = InventoryWire.Encode(local); }
            catch (InvalidDataException e) { Plugin.Logger.LogWarning(e.Message); }
            nextPublish = 0;
        }
        string localName = Name(self.NickName, "本机") + "（你）";
        ulong id = SNet.IsInLobby ? SNet.Lobby?.Identifier?.ID ?? 0 : 0;
        if (id == 0)
        {
            Reset(); Notice = "尚未加入 Steam 大厅，仅显示本机。";
            return new PlayerColumn[] { new(self.Lookup, localName, local) };
        }
        if (lobbyId != id) { Reset(); lobbyId = id; }
        var lobby = new CSteamID(id);
        var mySteam = SteamUser.GetSteamID();
        ulong myId = mySteam.m_SteamID;
        int count = SteamMatchmaking.GetNumLobbyMembers(lobby);
        var members = new Dictionary<ulong, CSteamID>();
        for (int i = 0; i < Math.Min(count, 32); i++)
        {
            var member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
            if (member.m_SteamID != 0) members.TryAdd(member.m_SteamID, member);
        }
        if (myId == 0 || !members.ContainsKey(myId))
        {
            peers.Clear(); nextPublish = 0; Notice = "Steam 大厅名单暂不可用，仅显示本机。";
            return new PlayerColumn[] { new(self.Lookup, localName, local) };
        }
        string Read(CSteamID member, string key) => SteamMatchmaking.GetLobbyMemberData(lobby, member, key) ?? string.Empty;
        if (now >= nextPublish)
        {
            nextPublish = now + 6;
            if (outgoing is { } data)
            {
                if (data.Header != Read(mySteam, InventoryWire.HeaderKey) ||
                    data.Parts.Where((part, index) => part != Read(mySteam, InventoryWire.PartKey(index))).Any())
                {
                    for (int i = 0; i < data.Parts.Length; i++)
                        SteamMatchmaking.SetLobbyMemberData(lobby, InventoryWire.PartKey(i), data.Parts[i]);
                    SteamMatchmaking.SetLobbyMemberData(lobby, InventoryWire.HeaderKey, data.Header);
                }
            }
            else if (Read(mySteam, InventoryWire.HeaderKey) != "-")
                SteamMatchmaking.SetLobbyMemberData(lobby, InventoryWire.HeaderKey, "-");
        }
        if (outgoing is null) Notice = "本机清单无法发送；队友看不到你的版本。";

        var players = new Dictionary<ulong, (int Slot, string Name)>();
        var gamePlayers = SNet.LobbyPlayers;
        if (gamePlayers is not null)
            for (int i = 0; i < gamePlayers.Count; i++)
            {
                var p = gamePlayers[i];
                if (p is null || p.IsBot) continue;
                ulong sid = p.IsLocal ? myId : p.Lookup;
                if (sid == 0) continue;
                int slot = p.PlayerSlotIndex();
                players[sid] = (slot < 0 ? int.MaxValue : slot, p.IsLocal ? localName : Name(p.NickName, $"队友 {i + 1}"));
            }
        players.TryAdd(myId, (self.PlayerSlotIndex(), localName));
        RosterConfirmed = gamePlayers is not null && count == members.Count && players.Keys.All(members.ContainsKey);
        foreach (var (sid, member) in members)
            players.TryAdd(sid, (int.MaxValue, Name(SteamFriends.GetFriendPersonaName(member), "队友")));
        foreach (ulong sid in peers.Keys.Where(k => !members.ContainsKey(k)).ToArray()) peers.Remove(sid);

        var result = new List<PlayerColumn>();
        foreach (var (sid, (_, name)) in players.OrderBy(p => p.Value.Slot).ThenBy(p => p.Key))
        {
            if (sid == myId) { result.Add(new(sid, name, local)); continue; }
            if (!members.TryGetValue(sid, out var peer)) { result.Add(new(sid, name, null)); continue; }
            string header = Read(peer, InventoryWire.HeaderKey);
            if (peers.TryGetValue(sid, out var old) && old.Header == header) result.Add(new(sid, name, old.Mods));
            else if (InventoryWire.TryDecode(header, i => Read(peer, InventoryWire.PartKey(i)), out var mods) && mods is not null)
            {
                peers[sid] = (header, mods);
                result.Add(new(sid, name, mods));
            }
            else { peers.Remove(sid); result.Add(new(sid, name, null)); }
        }
        return result.ToArray();
    }

    private static string Name(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
