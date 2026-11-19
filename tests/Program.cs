using TeamModVersions;

int passes = 0;
void Check(bool result, string message)
{
    if (!result) throw new Exception(message);
    Console.WriteLine($"PASS {++passes}: {message}");
}
ModEntry M(string id, string name, string version = "1.0.0") => new(id, name, version);
PlayerColumn[] team = {
    new(1, "A", new[] { M("m1","MatchA"), M("m2","MatchZ"), M("v","X","1.10.0"), M("s","Z"), M("same.a","A"), M("build","Build","1.0.0+one"), M("unknown","Zed", "manual") }),
    new(2, "B", new[] { M("m1","MatchA"), M("m2","MatchZ"), M("v","X","1.9.0"), M("s","Z"), M("same.b","A"), M("build","Build","1.0.0+two"), M("unknown","Zed", "manual") }),
    new(3, "C", new[] { M("m1","MatchA"), M("m2","MatchZ"), M("v","X","1.10.0"), M("build","Build","1.0.0+one"), M("unknown","Zed", "manual") }),
};
var hidden = Matrix.Build(team);
var shown = Matrix.Build(team, showMatches: true);
Check(hidden.Matches.Length == 0 && hidden.Differences.All(x => x.Id != "m1" && x.Id != "m2"), "matches hidden by default");
Check(shown.Matches.Select(x => x.Id).SequenceEqual(new[] { "m2", "m1" }), "optional matches independently sorted Z-A");
Check(shown.Differences.Select(x => x.Id).SequenceEqual(new[] { "unknown", "v", "build", "s", "same.a", "same.b" }), "differences sorted by count, then name Z-A, then ID");
Check(shown.Differences.Select(x => x.Id).SequenceEqual(hidden.Differences.Select(x => x.Id)), "toggle preserves difference order");
var compare = shown.Differences.Single(x => x.Id == "v");
Check(compare.Cells.Select(c => c.Kind).SequenceEqual(new[] { CellKind.Highest, CellKind.Lower, CellKind.Highest }), "numeric comparison and tied highest");
Check(shown.Differences.Single(x => x.Id == "s").Cells[2].Kind == CellKind.NotReported, "synced but missing is not reported");
Check(shown.Differences.Single(x => x.Id == "unknown").Cells.All(x => x.Kind == CellKind.UnknownVersion), "unparseable versions cannot be matches");
var missing = Matrix.Build(team.Append(new(4, "No plugin", null)).ToArray(), showMatches: true);
Check(missing.Matches.Length == 0 && missing.Differences.All(x => x.Cells[3].Kind == CellKind.NotSynced), "unsynced host or peer prevents matches");
Check(Matrix.Build(team, showMatches: true, rosterConfirmed: false).Matches.Length == 0, "unconfirmed roster prevents hiding");
Check(ModVersion.Parse("1.0.0-rc.10")!.CompareTo(ModVersion.Parse("1.0.0-rc.2")) > 0 && ModVersion.Parse("1.0.0")!.CompareTo(ModVersion.Parse("1.0.0-rc.10")) > 0, "prerelease comparison");

var random = new Random(203);
var items = Enumerable.Range(0, 150).Select(i => M($"test.{i}", "插件 " + string.Concat(Enumerable.Range(0, 32).Select(_ => (char)random.Next('a', 'z' + 1))), "1.10.0")).ToArray();
var wire = InventoryWire.Encode(items);
Check(wire.Parts.Length > 1 && InventoryWire.TryDecode(wire.Header, i => wire.Parts[i], out var result) && result!.SequenceEqual(items), "multipart Unicode round trip");
Check(!InventoryWire.TryDecode(wire.Header, i => i == 0 ? "" : wire.Parts[i], out _), "missing part rejected");
Check(!InventoryWire.TryDecode(wire.Header, i => i == 0 ? (wire.Parts[i][0] == 'a' ? "b" : "a") + wire.Parts[i][1..] : wire.Parts[i], out _), "digest mismatch rejected");
var empty = InventoryWire.Encode(Array.Empty<ModEntry>());
Check(InventoryWire.TryDecode(empty.Header, i => empty.Parts[i], out var noMods) && noMods!.Length == 0, "valid empty inventory is not unsynced");

var clientA = new LobbyInventory();
var clientB = new LobbyInventory();
SNetwork.SNet.SetLobby(100, 1, 2, 3);
SNetwork.SNet.PlayAs(2, "1.10.0");
var aBefore = clientA.Sample(0);
Check(aBefore.Single(x => x.Id == 1).Mods is null && aBefore.Single(x => x.Id == 3).Mods is null, "first reporting guest does not guess host or other guest data");
SNetwork.SNet.PlayAs(3, "1.9.0");
var bAfter = clientB.Sample(0);
SNetwork.SNet.PlayAs(2, "1.10.0");
var aAfter = clientA.Sample(1);
Check(bAfter.Single(x => x.Id == 2).Mods!.Single().Version == "1.10.0" &&
      aAfter.Single(x => x.Id == 3).Mods!.Single().Version == "1.9.0" &&
      aAfter.Single(x => x.Id == 1).Mods is null && bAfter.Single(x => x.Id == 1).Mods is null,
      "guest-to-guest exchange without reporting host (simulated Steam)");
var host = new LobbyInventory();
SNetwork.SNet.PlayAs(1, "1.10.0");
Check(host.Sample(2).Count(x => x.Mods is not null) == 3, "reporting host sees all reporting members (simulated Steam)");
SNetwork.SNet.SetLobby(200, 1, 2, 3);
SNetwork.SNet.PlayAs(2, "1.10.0");
Check(clientA.Sample(3).Count(x => x.Mods is not null) == 1, "lobby change clears prior session inventory");
