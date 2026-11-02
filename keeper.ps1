# Keeps everything alive with no human or AI around: starts PvZ if it's not running, then runs the watchdog
# (bot relaunch / hang restart / Discord services). Started at logon from the Startup folder (cbot_keeper.cmd).
# Honours C:\cbot\hold.flag (PvZ parked: screen used by another game). Single instance via a named mutex.
$mutex = New-Object Threading.Mutex($false, 'Global\cbot_keeper'); if (-not $mutex.WaitOne(0)) { exit }
$game = 'C:\Users\User\Downloads\Modded Plants VS Zombies\Release\LawnProject.exe'
$log = 'C:\cbot\keeper.log'
function Log($m) { Add-Content $log ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $m) }
Log "keeper start"
Start-Sleep 20   # after a reboot, let the desktop settle
while ($true) {
  try {
    if (-not (Test-Path C:\cbot\hold.flag) -and -not (Get-Process LawnProject -EA SilentlyContinue)) {
      Start-Process $game -WorkingDirectory (Split-Path $game); Log "game was not running -> started it"; Start-Sleep 25
    }
    $w = & C:\cbot\watch.ps1 2>&1 | Out-String
    $w -split "`r?`n" | ? { $_ -match 'RELAUNCHED|HUNG|STUCK|WARNING' } | % { Log $_ }
  } catch { Log "error: $_" }
  Start-Sleep 90
}
