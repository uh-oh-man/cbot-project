# Low-token status check for overnight watching (~5 lines). Relaunches the bot if it died, restarts it if it hung,
# keeps the Discord services up. "STUCK?" = no real game progress for $StuckMin minutes (report only, no action).
# Progress = level, wave, vase round + vases left, number of RESULT lines. Log chatter does NOT count as progress.
# Hung = run.ps1 is alive (for > $HungMin) but bot.log has not been written for $HungMin minutes -> C:\cbot\restart.ps1 (detached).
# Windows PowerShell 5.1. Every command-line match excludes $PID and is anchored on "-File <exact path>".
param([int]$StuckMin = 6, [double]$HungMin = 2)
$state = "C:\cbot\watch_state.txt"; $botLog = "C:\cbot\bot.log"; $evLog = "C:\cbot\events.log"; $index = "C:\cbot\replays\index.txt"
$now = Get-Date; $out = New-Object System.Collections.Generic.List[string]

function Read-Shared([string]$path) {   # read a file the bot may be appending to, without a sharing violation
  if (-not (Test-Path -LiteralPath $path)) { return }
  try {
    $fs = New-Object IO.FileStream($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
    $sr = New-Object IO.StreamReader($fs)
    try { $txt = $sr.ReadToEnd() } finally { $sr.Dispose() }
    if ($txt.Length -gt 0) { $txt.TrimEnd("`r", "`n") -split "`r?`n" }
  } catch { }
}
function Get-Tail([string]$path, [int]$n) { Get-Content -LiteralPath $path -Tail $n -ErrorAction SilentlyContinue }
# Exact "-File <path>" match (the path may be quoted), never this process.
function Find-Script([object[]]$procs, [string]$path) {
  $rx = '-File\s+"?' + [regex]::Escape($path) + '"?(\s|$)'
  @($procs | ? { $_.ProcessId -ne $PID -and $_.CommandLine -and $_.CommandLine -match $rx })
}
function Start-Hidden([string]$file, [string[]]$extra) {
  $a = @('-NoProfile', '-WindowStyle', 'Hidden', '-File', $file) + $extra
  Start-Process powershell -ArgumentList $a -WindowStyle Hidden
}

$prev = @{}; if (Test-Path $state) { Get-Content $state | % { $k, $v = $_ -split '=', 2; if ($k) { $prev[$k] = $v } } }
function PrevDate([string]$k) { $d = [datetime]::MinValue; if ($prev[$k] -and [datetime]::TryParse($prev[$k], [ref]$d)) { $d } else { [datetime]::MinValue } }
$lastRestart = PrevDate 'restartAt'
$held = Test-Path C:\cbot\hold.flag   # hold.flag = PvZ parked (screen used by Buckshot): no relaunch, no restart, no STUCK

$procs = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue)
$bot = Find-Script $procs 'C:\cbot\run.ps1'
# A restart in flight (restart.ps1 running, or launched < 3 min ago) briefly has no run.ps1: do not launch a second bot.
$restarting = (Find-Script $procs 'C:\cbot\restart.ps1').Count -gt 0 -or ($now - $lastRestart).TotalMinutes -lt 3

# ---- Bot: relaunch if gone, restart if hung (bot.log stale) ----
$logAge = if (Test-Path $botLog) { ($now - (Get-Item $botLog).LastWriteTime).TotalMinutes } else { 1e9 }
$botAge = 0
if ($bot.Count -gt 0) { try { $botAge = ($now - ($bot | % { $_.CreationDate } | sort | select -First 1)).TotalMinutes } catch { $botAge = 0 } }
if ($bot.Count -gt 1) { $out.Add("WARNING: $($bot.Count) run.ps1 processes (pid $(($bot | % { $_.ProcessId }) -join ','))") }
if (-not $held -and -not $restarting) {
  if ($bot.Count -eq 0) {
    Start-Hidden 'C:\cbot\run.ps1' @('-Secs', '86400'); $out.Add("RELAUNCHED bot (was not running)")
  } elseif ($logAge -gt $HungMin -and $botAge -gt $HungMin) {
    Start-Hidden 'C:\cbot\restart.ps1' @(); $lastRestart = $now
    $out.Add(("HUNG: bot.log silent {0:0.0} min while run.ps1 (pid {1}) is up -> restart.ps1 launched" -f $logAge, (($bot | % { $_.ProcessId }) -join ',')))
  }
}

# ---- Discord services: restart whichever died (skipped while discord\stop.flag is present) ----
if (-not (Test-Path C:\cbot\discord\stop.flag)) {
  foreach ($svc in 'relay', 'presence', 'gifcam', 'ideabot') {
    if ($svc -eq 'ideabot' -and -not (Test-Path C:\cbot\discord\ideabot_read.txt)) { continue }   # not set up yet: leave it alone
    $f = "C:\cbot\discord\$svc.ps1"
    if (-not (Test-Path $f)) { continue }
    if ((Find-Script $procs $f).Count -eq 0) { Start-Hidden $f @(); $out.Add("RELAUNCHED discord $svc") }
  }
}

# ---- Progress: level / wave / vase round + left / RESULT count ----
$ev = @(Read-Shared $evLog)
$lastLine = [int]$prev['lines']; if ($lastLine -gt $ev.Count) { $lastLine = 0 }
$new = @($ev | select -Skip $lastLine)
$results = @($ev | ? { $_ -match '\sRESULT lvl \d+' })
$watchdogs = @($new | ? { $_ -match 'WATCHDOG' }).Count
$tail = @(Get-Tail $botLog 400)
$errs = @($tail | ? { $_ -match ' err ' }).Count
$st = $tail | ? { $_ -match 'scene=' } | select -Last 1
$lvl = if ($st -match 'lvl=(\d+)') { [int]$Matches[1] } else { 0 }
$wave = if ($st -match 'wave=(\d+/\d+)') { $Matches[1] } else { '?' }
$idx = Get-Tail $index 1
if ($lvl -eq 0 -and $idx -match 'lvl=(\d+)') { $lvl = [int]$Matches[1] }
# Vasebreaker: round and vases left, from VasePlanner status ("VASE R2 (final): 9 left"), "VASE break r1c7 ... left=16",
# "VASE resumed ... round 2", or the pro panel ("vase round 2 | vases left 7"). Newest line wins.
$vr = '-'; $vl = '-'
foreach ($l in $tail) {
  if ($l -match 'VASE R(\d+)' -or $l -match '(?i)vase round (\d+)' -or $l -match 'VASE resumed.*round (\d+)') { $vr = $Matches[1] }
  if ($l -match 'VASE break .*left=(\d+)' -or $l -match 'VASE R\d+[^:]*: (\d+) left' -or $l -match '(?i)vases left (\d+)') { $vl = $Matches[1] }
}
if ($vr -eq '-' -and $idx -match "lvl=$lvl .*round=(\d+)") { $vr = $Matches[1] }
$sig = "$lvl|$wave|$vr|$vl|$($results.Count)"
$since = if ($prev['sig'] -eq $sig) { PrevDate 'since' } else { $now }
if ($since -eq [datetime]::MinValue) { $since = $now }
$stuckFor = ($now - $since).TotalMinutes

$sl = Get-Content C:\cbot\knowledge.txt -ErrorAction SilentlyContinue | ? { $_ -match '^S\|' } | select -First 1
$score = [string]($sl -replace '^S\|', 'deaths|wins|streak|best=')
$world = [math]::Floor(($lvl - 1) / 10) + 1; $sub = (($lvl - 1) % 10) + 1
$vtxt = if ($vr -ne '-' -or $vl -ne '-') { " vase R$vr left $vl" } else { '' }
$btxt = if ($held) { 'HELD' } elseif ($bot.Count -gt 0) { "up {0:0}m" -f $botAge } else { 'DOWN' }
$ltxt = if ($logAge -lt 1e8) { "{0:0.0}m" -f $logAge } else { 'missing' }
$out.Insert(0, ("{0:HH:mm} bot {1} | lvl {2}-{3} wave {4}{5} | {6} RESULTs | {7} | log {8} old | +{9} ev, {10} wd, {11} errs" -f
  $now, $btxt, $world, $sub, $wave, $vtxt, $results.Count, $score, $ltxt, $new.Count, $watchdogs, $errs))
$mi = if ($ev.Count) { [array]::FindLastIndex([string[]]$ev, [Predicate[string]]{ param($x) $x -match 'MINE-COUNT-START' }) } else { -1 }
$mev = if ($mi -ge 0) { $ev[$mi..($ev.Count - 1)] } else { $ev }
$mb = @($mev | ? { $_ -match 'LEARN potato mine EXPLODED' }).Count; $me = @($mev | ? { $_ -match 'LEARN potato mine was eaten' }).Count; $ms = @($mev | ? { $_ -match 'LEARN mine at .* not counted' }).Count
if ($mb + $me + $ms -gt 0) { $out.Add("mines: $mb exploded, $me eaten unarmed, $ms skipped (not counted)") }
$results | select -Last 2 | % { $out.Add($_) }
if ($stuckFor -ge $StuckMin -and -not $held) {
  $out.Add(("STUCK? no progress ({0}) for {1:0} min. last events:" -f $sig, $stuckFor)); $ev | select -Last 2 | % { $out.Add($_) }
}
$out
$ra = if ($lastRestart -gt [datetime]::MinValue) { $lastRestart.ToString('o') } else { '' }
@("lines=$($ev.Count)", "sig=$sig", "since=$($since.ToString('o'))", "restartAt=$ra") | Set-Content $state -Encoding ascii

try { & C:\cbot\report.ps1 } catch { }
