<#
  A page that replaces the row around its focused input must not leave the game's typing flag stuck: Escape has to
  close the app afterwards.

  Needs a running Debug build in a loaded save, with tests/focuslost-app installed as a file-only app (sideloadesc
  is a DEBUG console command). -Install copies the app into the slot; the game reads it at start-up, so install
  before the start. Reports PASS or FAIL per check and exits 1 when one failed.

    pwsh -File tests/focus-lost.ps1 -Slot mptest3 -Install     # before the game starts
    pwsh -File tests/focus-lost.ps1 -Slot mptest3
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$s1 = Join-Path $root 'Workspace/tools/bridge/s1.py'
$App = 'sideload-focuslost'
$failed = 0

function Check($name, [bool]$ok, $detail) {
    if ($ok) { Write-Host "PASS  $name" } else { Write-Host "FAIL  $name - $detail"; $script:failed++ }
}

if ($Install) {
    $folder = @{ mptest = '_MPTest'; mptest2 = '_MPTest2'; mptest3 = '_MPTest3'; mptest4 = '_MPTest4'
                 mptest5 = '_MPTest5'; mptest6 = '_MPTest6' }[$Slot]
    if (-not $folder) { Write-Host "FAIL  no test copy known for slot '$Slot'"; exit 1 }
    $target = Join-Path $root "$folder/Schedule I/UserData/Sideload/apps/$App"
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'focuslost-app/*') $target -Force
    Write-Host "installed $App into $target"
    exit 0
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

function Eval($code) { ((& python -I $s1 --slot $Slot eval $App $code) -join "`n").Trim() }

# Read from the log rather than from run's output, which carries every line the game wrote meanwhile. Comparing
# with the line before keeps an older result from passing this run.
function LastLine($grep) { ((& python -I $s1 --slot $Slot log --grep $grep -n 1) -join "`n").Trim() }

Check "$App opens" (OpenApp) 'sideload_open_app did not report onScreen'

$redrawsBefore = Eval "document.getElementById('status').textContent"
$field = Eval "document.querySelector('#list input').id"
& python -I $s1 --slot $Slot click $App "#$field" | Out-Null
Start-Sleep -Milliseconds 500
$active = Eval "document.activeElement ? document.activeElement.id : 'none'"
Check 'the field has the caret after a click' ($active -match [regex]::Escape($field)) "activeElement is $active"

$releasedBefore = LastLine 'field with the caret left the page'
$replaced = Eval 'redraw()'
Check 'the page replaced the focused row' ($replaced -match 'replaced row-') $replaced
Start-Sleep -Milliseconds 500

$released = LastLine 'field with the caret left the page'
Check 'Sideload released the keyboard' ($released -ne $releasedBefore -and $released -match 'keyboard released') `
      "no new release line (last: $released)"

$before = LastLine 'sideloadesc:'
& python -I $s1 --slot $Slot run sideloadesc --until 'sideloadesc: (PASS|FAIL)' | Out-Null
$out = LastLine 'sideloadesc: '
if ($out -eq $before) { $out = "no new sideloadesc line in the log (last: $before)" }
Write-Host "      $out"
Check 'Escape closes the app afterwards' ($out -match "sideloadesc: PASS closed '$App'") $out

if ($failed) { Write-Host "$failed check(s) failed."; exit 1 }
Write-Host 'all checks passed.'
