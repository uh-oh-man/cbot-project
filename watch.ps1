# Low-token status check for overnight watching. Relaunches the bot if it died.
# Prints ~5 lines; "STUCK" when nothing has progressed for $StuckMin minutes.
param([int]$StuckMin = 6)
$state = "C:\cbot\watch_state.txt"
$prev = @{}; if (Test-Path $state) { Get-Content $state | % { $k, $v = $_ -split '=', 2; $prev[$k] = $v } }
$alive = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | ? { $_.CommandLine -match 'run\.ps1' }
if (-not $alive -and -not (Test-Path C:\cbot\hold.flag)) {  # hold.flag = PvZ parked (screen used by Buckshot)
  Start-Process powershell -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-File', 'C:\cbot\run.ps1', '-Secs', '86400' -WindowStyle Hidden
  "RELAUNCHED bot (was not running)"
}
# Discord relay + online presence: restart whichever died (skipped while a stop.flag is present).
if (-not (Test-Path C:\cbot\discord\stop.flag)) {
  foreach ($svc in 'relay', 'presence', 'gifcam') {
    $up = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | ? { $_.ProcessId -ne $PID -and $_.CommandLine -match "-File\s+C:\\cbot\\discord\\$svc\.ps1" }
    if (-not $up) { Start-Process powershell -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-File', "C:\cbot\discord\$svc.ps1" -WindowStyle Hidden; "RELAUNCHED discord $svc" }
  }
}
$ev = Get-Content C:\cbot\events.log -ErrorAction SilentlyContinue
$lastLine = [int]($prev['lines']); if ($lastLine -gt $ev.Count) { $lastLine = 0 }
$new = $ev | select -Skip $lastLine
$results = $new | ? { $_ -match 'RESULT|BEATEN' }
$watchdogs = ($new | ? { $_ -match 'WATCHDOG' }).Count
$errs = (Get-Content C:\cbot\bot.log -Tail 400 -ErrorAction SilentlyContinue | ? { $_ -match ' err ' }).Count
$sl = Get-Content C:\cbot\knowledge.txt -ErrorAction SilentlyContinue | ? { $_ -match '^S\|' }
$st = Get-Content C:\cbot\bot.log -Tail 60 -ErrorAction SilentlyContinue | ? { $_ -match 'scene=' } | select -Last 1
$lvl = if ($st -match 'lvl=(\d+)') { [int]$Matches[1] } else { 0 }
$wave = if ($st -match 'wave=(\d+/\d+)') { $Matches[1] } else { '?' }
$sig = "$lvl|$wave|$($ev.Count)"
$since = if ($prev['sig'] -eq $sig) { [datetime]$prev['since'] } else { Get-Date }
$stuckFor = ((Get-Date) - $since).TotalMinutes
$world = [math]::Floor(($lvl - 1) / 10) + 1; $sub = (($lvl - 1) % 10) + 1; $score = [string]($sl -replace '^S\|', 'deaths|wins|streak|best=')
"{0:HH:mm} lvl {1}-{2} wave {3} | {4} | +{5} events, {6} watchdog, {7} errs" -f (Get-Date), $world, $sub, $wave, $score, $new.Count, $watchdogs, $errs
$mi = [array]::FindLastIndex([string[]]$ev, [Predicate[string]]{ param($x) $x -match 'MINE-COUNT-START' }); $mev = if ($mi -ge 0) { $ev[$mi..($ev.Count-1)] } else { $ev }
$mb = ($mev | ? { $_ -match 'LEARN potato mine EXPLODED' }).Count; $me = ($mev | ? { $_ -match 'LEARN potato mine was eaten' }).Count; $ms = ($mev | ? { $_ -match 'LEARN mine at .* not counted' }).Count
"mines: $mb exploded, $me eaten unarmed, $ms skipped (not counted)"
$results | select -Last 4
if ($stuckFor -ge $StuckMin) { "STUCK? no progress for {0:0} min. last events:" -f $stuckFor; $ev | select -Last 4 }
@("lines=$($ev.Count)", "sig=$sig", "since=$($since.ToString('o'))") | Set-Content $state

try { & C:\cbot\report.ps1 } catch { }
