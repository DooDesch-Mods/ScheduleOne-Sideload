<#
.SYNOPSIS
  A page whose min-size is above its max-size still lays out (Sideload#123). Reports PASS or FAIL per check.

.DESCRIPTION
  Personify's item picker is `height: 760px` inside `max-height: 86%`. Its editor surface counts in device pixels,
  so on a window under about 884 px tall the min (760) came out above the max, Math.Clamp threw, and the whole page
  stopped rendering: the hair and clothing pickers were empty.

  Boots a test slot into the main menu with Personify, Side Hustle and Personnel, adds tests/resize-probe as a
  plugin and starts again with -KeepSlot, has the probe switch the window to -Width x -Height windowed, opens the
  editor on a throwaway project and opens the hair picker. Resizing from outside (SetWindowPos) does not work:
  Unity keeps its backbuffer and the page still sees the old size. Unity's Screenmanager values in the shared
  registry key (HKCU\Software\TVGS\Schedule I, read by every install) are put back afterwards. Checks:
    - the page sees the small viewport,
    - no "rebuilding the page failed" line after the picker opened,
    - the dialog fits the viewport (capped at 86%) and lists hair rows,
    - the button row ends inside the dialog (Personify#79: the dialog must declare flex-direction to shrink its list).
  The throwaway project is deleted at the end and the game is stopped. Personify from #79 on sets `min-height: 0`
  on its dialogs, so it no longer reaches the min-over-max case on an unfixed Sideload; the headless
  MinOverMaxTests in Workspace/Tests/Sideload.Tests keep that case covered.

    pwsh -File tests/min-over-max.ps1 -Slot mptest3                                   # the stack's Sideload
    pwsh -File tests/min-over-max.ps1 -Slot mptest3 -Sideload _wt-Sideload-min-over-max   # a worktree's Sideload
    pwsh -File tests/min-over-max.ps1 -Slot mptest3 -Personify _wt-Personify-dialog-fit   # a worktree's Personify
  -NoBoot uses the game already running in the slot (main menu, Debug Side Hustle, ResizeProbe loaded).
#>
[CmdletBinding()]
param(
    [ValidateSet('mptest', 'mptest2', 'mptest3', 'mptest4', 'mptest5', 'mptest6')]
    [string]$Slot = 'mptest',
    # A Sideload checkout or worktree folder to build into the slot instead of the stack's Sideload.
    [string]$Sideload = '',
    # The Personify checkout or worktree folder to build into the slot.
    [string]$Personify = 'Personify',
    [int]$Width = 1280,
    [int]$Height = 720,
    [switch]$NoBoot
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$session = Join-Path $root 'Workspace\tools\session\session.ps1'
$s1 = Join-Path $root 'Workspace\tools\bridge\s1.py'
$app = 'personify-editor'
$project = 'MinOverMax Check'
$failLine = 'rebuilding the page failed'

$results = [System.Collections.Generic.List[object]]::new()
function Check([string]$name, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Check = $name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Detail = $detail })
    Write-Host ("{0}  {1}  {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

function Eval([string]$js) {
    $out = (& python -I $s1 --slot $Slot eval $app $js 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { Write-Warning "[test] eval failed ($LASTEXITCODE): $out" }
    return $out
}

function EvalJson([string]$js) {
    $out = Eval $js
    try { return $out | ConvertFrom-Json }
    catch { Write-Warning "[test] eval answer is not JSON: $out"; return $null }
}

# How many failed rebuilds the slot's log holds so far, so a line from before this step cannot count.
function FailCount {
    $lines = @(& python -I $s1 --slot $Slot log --grep $failLine -n 500 2>&1 | Where-Object { "$_" -match $failLine })
    return $lines.Count
}

function SlotGame {
    $slotRoot = [string]((& python -I (Join-Path $root 'Workspace\tools\slots\slots.py') table | ConvertFrom-Json).$Slot.root)
    return @(Get-CimInstance Win32_Process -Filter "Name = 'Schedule I.exe'" |
             Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($slotRoot, 'OrdinalIgnoreCase') })
}

$slotRoot = [string]((& python -I (Join-Path $root 'Workspace\tools\slots\slots.py') table | ConvertFrom-Json).$Slot.root)
$regKey = 'HKCU:\Software\TVGS\Schedule I'

function ScreenPrefs {
    $p = Get-ItemProperty -Path $regKey -ErrorAction SilentlyContinue
    $out = @{}
    if ($p) { foreach ($n in $p.PSObject.Properties.Name) { if ($n -like 'Screenmanager*') { $out[$n] = $p.$n } } }
    return $out
}

# Puts back every Screenmanager value the run changed, and removes the ones it added.
function RestoreScreenPrefs([hashtable]$before) {
    $after = ScreenPrefs
    $changed = 0
    foreach ($n in $after.Keys) {
        if (-not $before.ContainsKey($n)) { Remove-ItemProperty -Path $regKey -Name $n; $changed++ }
        elseif ("$($before[$n])" -ne "$($after[$n])") { Set-ItemProperty -Path $regKey -Name $n -Value $before[$n]; $changed++ }
    }
    Write-Host "[test] registry: $changed Screenmanager value(s) put back"
}

function StartSlot([string[]]$extra) {
    $argv = @('-NoProfile', '-File', $session, '-Slot', $Slot, '-Save', '0', '-NoApp', '-Purpose', 'min-over-max') + $extra
    Write-Host "[test] session.ps1 $($argv[3..($argv.Count - 1)] -join ' ')"
    & pwsh @argv
    if ($LASTEXITCODE -ne 0) { throw "session.ps1 failed with exit code $LASTEXITCODE" }
}

# s1.py log --grep is a plain substring match.
function WaitForLog([string]$text, [int]$seconds) {
    $until = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $until) {
        $line = (& python -I $s1 --slot $Slot log --grep $text -n 1 2>&1 | Out-String).Trim()
        if ($line.Contains($text)) { return $line }
        Start-Sleep -Milliseconds 500
    }
    return ''
}

$prefsBefore = ScreenPrefs
try {
    if (-not $NoBoot) {
        $probe = Join-Path $PSScriptRoot 'resize-probe\ResizeProbe.csproj'
        $log = & dotnet build $probe -c Release -v q --nologo 2>&1
        if ($LASTEXITCODE -ne 0) { throw "building the resize probe failed: $($log -join "`n")" }
        $probeDll = Join-Path $PSScriptRoot 'resize-probe\bin\Release\net6.0\ResizeProbe.dll'

        # The stack rebuild wipes Plugins, so the probe goes in after it, and the second start keeps the slot.
        $builds = if ($Sideload) { "$Personify,$Sideload" } else { $Personify }
        StartSlot @('-Build', $builds, '-Mods', 'SideHustle,Personnel')
        foreach ($p in SlotGame) { Stop-Process -Id $p.ProcessId -Force }
        Copy-Item $probeDll (Join-Path $slotRoot 'Plugins') -Force
        StartSlot @('-KeepSlot')
    }

    Set-Content -Path (Join-Path $slotRoot 'UserData\resize-probe.txt') -Value "$Width $Height" -NoNewline
    $resized = WaitForLog '[resizeprobe] requested' 15
    Check 'window resized' ([bool]$resized) $(if ($resized) { $resized } else { 'no [resizeprobe] line - is the probe loaded?' })
    Start-Sleep -Seconds 2

    $null = @(& python -I $s1 --slot $Slot run shstart doodesch.personify 2>&1)
    $screen = Eval 'ui.screen'
    Check 'editor open' ($screen -eq 'start' -or $screen -eq 'editor') "ui.screen = $screen"

    $vp = EvalJson 'JSON.stringify({w: innerWidth, h: innerHeight})'
    Check 'page sees the small viewport' ($vp -and [int]$vp.h -le $Height -and [int]$vp.h -gt 0) "innerWidth x innerHeight = $($vp.w) x $($vp.h)"

    $null = Eval "JSON.stringify(call('projects.delete', '$project'))"
    $made = EvalJson "JSON.stringify(call('projects.create', {name: '$project', author: 'Check'}))"
    Check 'project created' ([bool]$made.ok) "folder $($made.folder)"
    $null = Eval "openProject('$project'); 'ok'"
    $added = EvalJson "JSON.stringify(call('npc.add', {name: 'Hal Picker', role: 'customer'}))"
    $null = Eval "openProject('$project'); setActive(npcs()[0].id); 'ok'"
    $hair = [int](Eval "catalog('hair').length")
    Check 'hair catalog loaded' ($hair -gt 0) "$hair hair style(s), npc.add ok = $($added.ok)"

    $failsBefore = FailCount
    $null = Eval "pickDialog('Style', asItems(catalog('hair')), {allowNone: true}, () => {}); 'ok'"
    Start-Sleep -Seconds 1
    $failsAfter = FailCount
    Check 'no failed rebuild after the picker opened' ($failsAfter -eq $failsBefore) "'$failLine' lines: $failsBefore -> $failsAfter"

    $dlg = EvalJson @'
JSON.stringify((() => {
  const d = document.querySelector('.scrim .dialog');
  if (!d) return { open: false };
  const r = d.getBoundingClientRect();
  const rows = Array.from(d.querySelectorAll('.pr')).filter(x => x.getBoundingClientRect().height > 0);
  const acts = d.querySelector('.dacts');
  const a = acts ? acts.getBoundingClientRect() : null;
  return { open: true, top: r.top, h: r.height, w: r.width, rows: rows.length, first: rows.length ? rows[0].textContent : '',
           actsBottom: a ? a.bottom : -1 };
})())
'@
    # A failed rebuild leaves the dialog in the document at 0 x 0, so open means laid out with a size.
    Check 'picker dialog open' ($dlg.open -and [double]$dlg.h -gt 0 -and [double]$dlg.w -gt 0) "dialog $($dlg.w) x $($dlg.h) at top $($dlg.top)"
    Check 'dialog fits the viewport' ($dlg.open -and [double]$dlg.h -gt 0 -and [double]$dlg.h -le [double]$vp.h -and [double]$dlg.top -ge 0) "height $($dlg.h) of $($vp.h)"
    Check 'hair rows listed' ([int]$dlg.rows -gt 0) "$($dlg.rows) visible row(s), first '$($dlg.first)'"
    $dlgBottom = [double]$dlg.top + [double]$dlg.h
    Check 'button row inside the dialog' ([double]$dlg.actsBottom -gt 0 -and [double]$dlg.actsBottom -le $dlgBottom + 0.5) "button row ends at $($dlg.actsBottom), dialog at $dlgBottom"

    $shot = (& python -I $s1 --slot $Slot shot 2>&1 | Out-String).Trim()
    Write-Host "[test] screenshot: $shot"
} finally {
    try { $null = Eval "closeDialog(); JSON.stringify(call('projects.delete', '$project'))"; $null = Eval "goStart(); 'ok'" }
    catch { Write-Warning "[test] deleting the throwaway project failed: $_" }
    if (-not $NoBoot) {
        foreach ($p in SlotGame) { Write-Host "[test] stopping pid $($p.ProcessId) in $Slot"; Stop-Process -Id $p.ProcessId -Force }
        Start-Sleep -Seconds 2
        RestoreScreenPrefs $prefsBefore
    }
}

$failed = @($results | Where-Object Result -eq 'FAIL').Count
Write-Host ("[test] {0} of {1} checks passed" -f ($results.Count - $failed), $results.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })
