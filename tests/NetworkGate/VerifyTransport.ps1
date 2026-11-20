# Read-only. Resolve native call edges from this build's IL2CPP interop xref cache;
# the generated C# bodies alone only show il2cpp_runtime_invoke trampolines.
param([Parameter(Mandatory=$true)][string]$GtfoRoot)
$ErrorActionPreference = 'Stop'
$interop = Join-Path $GtfoRoot 'BepInEx\interop'
Add-Type -Path (Join-Path $GtfoRoot 'BepInEx\core\Mono.Cecil.dll')
$map = [IO.BinaryReader]::new([IO.File]::OpenRead((Join-Path $interop 'MethodAddressToToken.db')))
$xrefs = [IO.BinaryReader]::new([IO.File]::OpenRead((Join-Path $interop 'MethodXrefScanCache.db')))
$snet = [Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $interop 'SNet_ASM.dll'))
$steam = [Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $interop 'SteamWorks_ASM.dll'))
try {
    $null=$map.ReadInt32(); $null=$map.ReadInt32()
    $assemblies=$map.ReadInt32(); $count=$map.ReadInt32(); $offset=$map.ReadInt32()
    $names=for($i=0;$i -lt $assemblies;$i++){ $map.ReadString().Split(',')[0] }
    $map.BaseStream.Position=$offset
    [long[]]$addresses=for($i=0;$i -lt $count;$i++){ $map.ReadInt64() }
    function Assert-Call($method,$type,$name) {
        $a=$method.CustomAttributes | Where-Object {$_.AttributeType.Name -eq 'CachedScanResultsAttribute'}
        $fields=@{}; $a.Fields | ForEach-Object {$fields[$_.Name]=$_.Argument.Value}
        for($i=$fields.XrefRangeStart;$i -lt $fields.XrefRangeEnd;$i++) {
            $xrefs.BaseStream.Position=16+24*$i
            $j=[array]::BinarySearch($addresses,$xrefs.ReadInt64()); if($j -lt 0){continue}
            $map.BaseStream.Position=$offset+8*$count+8*$j
            $token=$map.ReadInt32(); if($names[$map.ReadInt32()] -ne 'SteamWorks_ASM'){continue}
            $callee=$steam.LookupToken($token)
            if($callee.DeclaringType.FullName -eq $type -and $callee.Name -eq $name){
                Write-Output "PASS $($method.DeclaringType.Name)::$($method.Name) -> ${type}::$name"; return
            }
        }
        throw "Missing native edge: $($method.FullName) -> ${type}::$name"
    }
    $core=$snet.GetType('SNetwork.SNet_Core_STEAM')
    $send=@($core.Methods | Where-Object Name -eq 'SendBytes'); if($send.Count -ne 3){throw 'Unexpected SendBytes overloads'}
    foreach($m in $send){ Assert-Call $m 'Steamworks.SteamNetworking' 'SendP2PPacket' }
    $thread=$core.NestedTypes | Where-Object Name -eq 'SteamMessagesThreadClass'
    Assert-Call ($thread.Methods | Where-Object Name -eq 'ProcessMessages') 'Steamworks.SteamNetworking' 'ReadP2PPacket'
    $update=$core.Methods | Where-Object Name -eq 'UpdateConnectionStatus'
    Assert-Call $update 'Steamworks.SteamNetworking' 'GetP2PSessionState'
    Assert-Call $update 'Steamworks.SteamNetworkingUtils' 'EstimatePingTimeFromLocalHost'
} finally {
    $map.Dispose(); $xrefs.Dispose(); $snet.Dispose(); $steam.Dispose()
}
