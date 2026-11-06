<#
  A click whose press and release lie frames apart arrives even when the page rebuilds in between, and a page that
  rebuilds under a resting pointer does not hear a second `mouseenter` for the element the pointer never left.

  Uses the self-test app's theme button, wired by the test to add a node on mouseenter or while held, and the DEBUG
  probe `sideloadgesture` (rest, press, hold, release frame by frame through the real EventSystem, with uGUI's own
  click rule). Needs a running Debug build in a loaded save. Reports PASS or FAIL per check and exits 1 when one
  failed.

    pwsh -File tests/press-across-rebuild.ps1 -Slot mptest3
#>
param(
    [Parameter(Mandatory)][string]$Slot
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$s1 = Join-Path $root 'Workspace/tools/bridge/s1.py'
$App = 'sideload-selftest'
$failed = 0

function Check($name, [bool]$ok, $detail) {
    if ($ok) { Write-Host "PASS  $name  $detail" } else { Write-Host "FAIL  $name  $detail"; $script:failed++ }
}

$row = (& python (Join-Path $root 'Workspace/tools/slots/slots.py')) | Where-Object { $_ -match "^$Slot\s+(\d+)\s" } | Select-Object -Last 1
if (-not $row) { Write-Host "FAIL  slot '$Slot' not found in slots.py"; exit 1 }
$port = [int]([regex]::Match($row, "^$Slot\s+(\d+)").Groups[1].Value)

$body = @{ id = [guid]::NewGuid().ToString(); command = 'sideload_open_app'; args = @{ appId = $App; open = $true }; timeoutMs = 8000 }
try {
    $reply = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$port/command" -ContentType 'application/json' `
                               -Body ($body | ConvertTo-Json -Depth 4) -TimeoutSec 15
    Check 'self-test app opens' ([bool]$reply.ok) "$($reply.error)"
} catch {
    Check 'self-test app opens' $false $_.Exception.Message
    exit 1
}

function Eval([string]$js) {
    $out = (& python $s1 --slot $Slot eval $App $js 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { Write-Warning "eval failed ($LASTEXITCODE): $out" }
    return $out
}

# The probe's "released" line: renders and mouseenter counts while hovering and held, what uGUI delivered.
function Gesture([int]$hover, [int]$hold, [string]$selector) {
    $out = @(& python $s1 --slot $Slot run sideloadgesture $App "$hover" "$hold" $selector --until 'gesture on .* done' --max-wait 20000 2>&1 | ForEach-Object { "$_" })
    $line = @($out | Where-Object { $_ -match 'released:' }) | Select-Object -Last 1
    $raised = [bool](@($out | Where-Object { $_ -match 'survived a rebuild' }).Count)
    if (-not $line) { Write-Warning "no probe line for $selector`n$($out -join "`n")" }
    return [pscustomobject]@{
        Line = ($line -replace '^.*released: ', '')
        RendersHover = [int]([regex]::Match("$line", 'renders (\d+) while hovering').Groups[1].Value)
        RendersHeld = [int]([regex]::Match("$line", '(\d+) while held; mouseenter').Groups[1].Value)
        EntersHover = [int]([regex]::Match("$line", 'mouseenter (\d+) while hovering').Groups[1].Value)
        Raised = $raised
    }
}

function Accent { Eval "String(document.body.classList.contains('accent-mode'))" }

# The rebuilds come from the page itself, as in a real app. Mode 'tip': mouseenter on #theme adds a tooltip node
# to the body (the pattern of Personify's icon buttons). Mode 'hold': mousedown adds a node 200 ms later, while the
# button is still held.
function Mode([string]$mode) {
    $js = "(()=>{const b=document.getElementById('theme'); if(!b) return 'missing'; const p=globalThis.__press||(globalThis.__press={mode:'',tip:null});" +
          "if(!p.wired){p.wired=true;" +
          "b.addEventListener('mouseenter',()=>{if(p.mode!=='tip')return; if(p.tip&&p.tip.parentNode)p.tip.parentNode.removeChild(p.tip); const n=document.createElement('div'); n.textContent='tip'; document.body.appendChild(n); p.tip=n;});" +
          "b.addEventListener('mouseleave',()=>{if(p.tip&&p.tip.parentNode)p.tip.parentNode.removeChild(p.tip); p.tip=null;});" +
          "b.addEventListener('mousedown',()=>{if(p.mode!=='hold')return; setTimeout(()=>{const n=document.createElement('div'); n.textContent='held'; document.body.appendChild(n);},200);});}" +
          "p.mode='$mode'; return 'mode '+p.mode;})()"
    $out = Eval $js
    if ($out -ne "mode $mode".Trim()) { Write-Warning "setting probe mode '$mode' failed: $out" }
}

# 1. A long rest on a button whose mouseenter adds a node: one rebuild and one mouseenter, and a quick click lands.
Mode 'tip'
$was = Accent
$g = Gesture 80 6 '#theme'
$now = Accent
Check 'one mouseenter across rebuilds' ($g.RendersHover -ge 1 -and $g.RendersHover -le 3 -and $g.EntersHover -eq 1) $g.Line
Check 'quick click lands' ($now -ne $was) "accent $was -> $now"

# 2. A press held across a rebuild: the click comes on the release.
Mode 'hold'
$was = $now
$g = Gesture 4 80 '#theme'
$now = Accent
Check 'press held across a rebuild clicks' ($g.RendersHeld -ge 1 -and $g.Raised -and $now -ne $was) "accent $was -> $now; raised by Sideload: $($g.Raised); $($g.Line)"

# 3. Exactly one click per gesture, rebuild or not: two gestures bring the accent back.
$start = $now
$null = Gesture 4 80 '#theme'
Mode ''
$null = Gesture 4 6 '#theme'
$end = Accent
Check 'one click per gesture' ($end -eq $start) "accent $start -> $end after two gestures"

Write-Host $(if ($failed -eq 0) { 'all checks passed' } else { "$failed check(s) failed" })
exit $(if ($failed -eq 0) { 0 } else { 1 })
