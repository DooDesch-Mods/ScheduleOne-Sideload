<#
.SYNOPSIS
  Checks that a co-op client joins a host when both run a RELEASED Sideload.dll, once per version given. One PASS
  or FAIL line per check, exit 1 when any check failed.

.DESCRIPTION
  The test stack always carries a Debug build of Sideload. A report about "version X breaks the join" is about the
  DLL players download, so this check takes Sideload.dll from the GitHub release of each version and puts it into
  both slots of the co-op lease before the games start.

  Per version: duo.ps1 builds a fresh pair (host in the world, client in the menu), both games are stopped, the
  released DLL goes into both slots, the host is started again with its lobby, and the client joins it once.
  A fresh host per version matters: a second join into a host that already had a client is a different case.

  Checks per version:
    - host and client log the released version
    - the host reaches its world
    - the client reaches the host's world
    - neither log holds an exception or error line after the join started

.EXAMPLE
  pwsh -File tests/coop-join-release.ps1 -Versions 1.36.7,1.36.5 -Mods Personify,PropHunt
#>

[CmdletBinding()]
param(
    # Released Sideload versions to check, each on a fresh pair.
    [string[]]$Versions = @('1.36.7'),

    # More mods for both slots, by catalog name. Side Hustle and LocalLobby are always in (duo.ps1).
    [string[]]$Mods = @(),

    [string]$Repo = 'DooDesch-Mods/ScheduleOne-Sideload',

    [int]$BootSeconds = 240,
    [int]$JoinSeconds = 300
)

$ErrorActionPreference = 'Stop'
$Versions = @($Versions | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Mods = @($Mods | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$duo = Join-Path $root 'Workspace\tools\session\duo.ps1'
$slotsPy = Join-Path $root 'Workspace\tools\slots\slots.py'
$cache = Join-Path $root '.s1mcp\sideload-releases'

$results = [System.Collections.Generic.List[object]]::new()
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
    Write-Host ("{0}  {1}{2}" -f ($ok ? 'PASS' : 'FAIL'), $name, ($detail ? "  - $detail" : '')) -ForegroundColor ($ok ? 'Green' : 'Red')
    return $ok
}
function Info([string]$t) { Write-Host "INFO  $t" }

function Health([int]$port) {
    try { return Invoke-RestMethod -Uri "http://127.0.0.1:$port/health" -TimeoutSec 3 }
    catch { return $null }   # no answer is the expected state while a game boots; the caller reports the time-out
}

function RunningIn([string]$slotRoot) {
    @(Get-CimInstance Win32_Process -Filter "Name='Schedule I.exe'" -ErrorAction SilentlyContinue |
      Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($slotRoot, [StringComparison]::OrdinalIgnoreCase) })
}

function StopIn([string]$slotRoot) {
    foreach ($p in @(RunningIn $slotRoot)) { Stop-Process -Id $p.ProcessId -Force; Info "stopped pid $($p.ProcessId) in $slotRoot" }
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and (RunningIn $slotRoot).Count -gt 0) { Start-Sleep -Milliseconds 300 }
    if ((RunningIn $slotRoot).Count -gt 0) { throw "a game in $slotRoot is still running after 20 s" }
}

function WaitWorld([int]$port, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $h = Health $port
        if ($h -and $h.scene -eq 'Main' -and -not $h.loading) { return $h }
        Start-Sleep -Seconds 3
    }
    return $null
}

function ReleasedDll([string]$version) {
    $dir = Join-Path $cache $version
    $dll = Join-Path $dir 'Sideload.dll'
    if (-not (Test-Path $dll)) {
        New-Item -ItemType Directory -Force $dir | Out-Null
        & gh release download "v$version" -R $Repo -p Sideload.dll -D $dir --clobber
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dll)) { throw "could not download Sideload.dll of v$version from $Repo" }
    }
    return $dll
}

# Lines after $fromLine that say something went wrong. The S1API notice about trace logging is a setting, not a fault.
function Faults([string]$log, [int]$fromLine) {
    @(Get-Content -LiteralPath $log | Select-Object -Skip $fromLine |
      Where-Object { $_ -match 'Exception|\[ERROR\]' -and $_ -notmatch 'Exception trace logging is enabled' })
}

function Finish {
    $failed = @($results | Where-Object { -not $_.Ok })
    Write-Host ''
    Write-Host ("===== {0} PASS, {1} FAIL" -f ($results.Count - $failed.Count), $failed.Count)
    foreach ($f in $failed) { Write-Host "FAIL  $($f.Name)  - $($f.Detail)" }
    exit ($failed.Count -gt 0 ? 1 : 0)
}

foreach ($version in $Versions) {
    $dll = ReleasedDll $version
    $sha = (Get-FileHash $dll).Hash.Substring(0, 12)
    Info "Sideload $version release DLL $sha"

    $duoArgs = @('-NoProfile', '-File', $duo)
    if ($Mods.Count -gt 0) { $duoArgs += @('-Mods', ($Mods -join ',')) }
    & pwsh @duoArgs | Where-Object { $_ -notmatch '^\[stack\]' } | ForEach-Object { Write-Host $_ }
    if (-not (Check "[$version] duo.ps1 built the pair" ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE")) { continue }

    $mine = (& python -I $slotsPy --json mine) -join "`n" | ConvertFrom-Json
    $names = @($mine.lease.slots)
    $hostSlot = $mine.lease.table.($names[0]); $clientSlot = $mine.lease.table.($names[1])
    $hostRoot = [string]$hostSlot.root; $clientRoot = [string]$clientSlot.root
    $hostLog = Join-Path $hostRoot 'MelonLoader\Latest.log'; $clientLog = Join-Path $clientRoot 'MelonLoader\Latest.log'

    StopIn $hostRoot; StopIn $clientRoot
    foreach ($r in @($hostRoot, $clientRoot)) { Copy-Item -LiteralPath $dll -Destination (Join-Path $r 'Mods\Sideload.dll') -Force }

    # --host makes LocalLobby create the lobby before the world loads, the order vanilla writes "ready" in.
    Start-Process -FilePath (Join-Path $hostRoot 'Schedule I.exe') -ArgumentList @('--host', '--autoload', '--saveslot', '4') -WorkingDirectory $hostRoot
    $h = WaitWorld ([int]$hostSlot.port) $BootSeconds
    if (-not (Check "[$version] host reached its world" ($null -ne $h) "port $($hostSlot.port), ${BootSeconds}s")) { continue }
    $null = Check "[$version] host runs the released DLL" ([bool](Select-String -LiteralPath $hostLog -SimpleMatch "Sideload v$version" -Quiet)) $hostLog

    & pwsh -NoProfile -File $duo -Rejoin -JoinAttempts 1 -JoinSeconds $JoinSeconds -SettleSeconds 5 | ForEach-Object { Write-Host $_ }
    Info "duo.ps1 -Rejoin exit $LASTEXITCODE (it also fails on lobby membership, which this check does not judge)"

    $c = Health ([int]$clientSlot.port)
    $inWorld = $c -and $c.scene -eq 'Main' -and -not $c.loading
    $null = Check "[$version] client reached the host's world" ([bool]$inWorld) ("client scene '{0}' loading={1}" -f $c.scene, $c.loading)
    $null = Check "[$version] client runs the released DLL" ([bool](Select-String -LiteralPath $clientLog -SimpleMatch "Sideload v$version" -Quiet)) $clientLog

    $joinLine = @(Select-String -LiteralPath $clientLog -SimpleMatch 'Lobby joined' | Select-Object -Last 1)
    $from = if ($joinLine.Count -gt 0) { $joinLine[0].LineNumber } else { 0 }
    $clientFaults = Faults $clientLog $from
    $null = Check "[$version] client log clean after the join" ($clientFaults.Count -eq 0) ($clientFaults | Select-Object -First 2 | Out-String).Trim()
    $hostFaults = Faults $hostLog 0
    $null = Check "[$version] host log clean" ($hostFaults.Count -eq 0) ($hostFaults | Select-Object -First 2 | Out-String).Trim()
}

& pwsh -NoProfile -File $duo -Stop | ForEach-Object { Write-Host $_ }
Finish
