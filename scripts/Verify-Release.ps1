# Build and inspect the local release. Reads GtfoRoot; does not install or launch anything.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GtfoRoot)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$bep = Join-Path (Resolve-Path $GtfoRoot).Path 'BepInEx'
$logs = Join-Path $root 'artifacts\verification'
[void][IO.Directory]::CreateDirectory($logs)
function Run([string]$name,[string[]]$arguments) {
    $file = Join-Path $logs ($name + '.log')
    "dotnet $($arguments -join ' ')"
    & dotnet @arguments | Out-File -Encoding utf8 -LiteralPath $file
    $code=$LASTEXITCODE
    Get-Content -Encoding UTF8 -LiteralPath $file -Tail 3
    "[$name] exit=$code"
    if ($code -ne 0) { throw "$name failed. See $file" }
}
$old = $env:DOTNET_ROLL_FORWARD
Push-Location $root
try {
    $env:DOTNET_ROLL_FORWARD = 'LatestMajor'
    Run 'release' @('build','src/TeamModVersions.csproj','-c','Release',"-p:GtfoRoot=$GtfoRoot",'-p:NuGetAudit=false')
    $dll = (Resolve-Path 'src/bin/Release/net6.0/conflictmodlist.dll').Path
    $interop = Join-Path $bep 'interop'; $core = Join-Path $bep 'core'
    $suite=@('OfflineTests','LayoutGate','SlotHudReplay','NetworkGate','FontInteropGate','EntryRegistrationGate','LobbyWiringGate')
    foreach ($name in $suite) {
        $proj=if ($name -eq 'OfflineTests') { 'tests/OfflineTests.csproj' } else { "tests/$name/$name.csproj" }
        $a=@('run','--project',$proj,'-c','Release','-p:NuGetAudit=false')
        if ($name -in @('FontInteropGate','EntryRegistrationGate')) { $a+=@('--',$dll,$interop,$core) }
        elseif ($name -eq 'LobbyWiringGate') { $a+=@('--',$dll) }
        Run $name $a
    }
    Run 'font-selftest' @('run','--no-build','--project','tests/FontInteropGate/FontInteropGate.csproj','-c','Release','--','--selftest',$interop,$core)
    $manifest=Get-Content 'packaging/manifest.json' -Encoding UTF8 -Raw | ConvertFrom-Json
    $av=[Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString(3)
    if ($av -ne $manifest.version_number) { throw 'Manifest and DLL versions disagree' }
    $target=Join-Path $root "artifacts\$av"; [void][IO.Directory]::CreateDirectory($target)
    $dest=Join-Path $target 'conflictmodlist.dll'; Copy-Item -LiteralPath $dll -Destination $dest -Force
    $h=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash -ne $h) { throw 'Copy digest mismatch' }
    "VERIFIED $av $dest SHA256=$h"
} finally { Pop-Location; $env:DOTNET_ROLL_FORWARD=$old }
