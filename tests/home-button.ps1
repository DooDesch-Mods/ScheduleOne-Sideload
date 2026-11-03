<#
  The phone's home button closes a Sideload app, and the app opens again afterwards.

  Needs a running Debug build in a loaded save (sideloadhome is a DEBUG console command). Reports PASS or FAIL per
  check and exits 1 when one failed.

    pwsh -File tests/home-button.ps1 -Slot mptest4 -App npccards
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [string]$App = 'npccards'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$s1 = Join-Path $root 'Workspace/tools/bridge/s1.py'
$failed = 0

function Check($name, [bool]$ok, $detail) {
    if ($ok) { Write-Host "PASS  $name" } else { Write-Host "FAIL  $name - $detail"; $script:failed++ }
}

$row = (& python (Join-Path $root 'Workspace/tools/slots/slots.py')) | Where-Object { $_ -match "^$Slot\s+(\d+)\s" } | Select-Object -Last 1
if (-not $row) { Write-Host "FAIL  slot '$Slot' not found in slots.py"; exit 1 }
$port = [int]([regex]::Match($row, "^$Slot\s+(\d+)").Groups[1].Value)

function OpenApp {
    $body = @{ id = [guid]::NewGuid().ToString(); command = 'sideload_open_app'; args = @{ appId = $App; open = $true }; timeoutMs = 8000 }
    try {
        $reply = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$port/command" -ContentType 'application/json' `
                                   -Body ($body | ConvertTo-Json -Depth 6 -Compress) -TimeoutSec 15
    } catch {
        Write-Host "      bridge did not answer: $($_.Exception.Message)"
        return $false
    }
    if (-not $reply.ok) { Write-Host "      bridge error: $($reply.error.code) $($reply.error.message)"; return $false }
    return [bool]$reply.result.onScreen
}

Check "$App opens" (OpenApp) 'sideload_open_app did not report onScreen'

$out = (& python -I $s1 --slot $Slot run sideloadhome) -join "`n"
Check 'home button closes the app' ($out -match "sideloadhome: PASS closed '$App'") ($out.Trim())
Check 'no app is active afterwards' ($out -match 'active app now none') ($out.Trim())

Check "$App opens again" (OpenApp) 'sideload_open_app did not report onScreen after the home button'

if ($failed) { Write-Host "$failed check(s) failed."; exit 1 }
Write-Host 'all checks passed.'
