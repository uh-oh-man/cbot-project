# Hot reload: ask the running bot to save and exit, rebuild, relaunch detached.
param([int]$Secs = 86400, [string]$Mode = "")
# -Mode pro | learn switches the brain (remembered in mode.txt); omitted = keep the current mode.
if ($Mode) { Set-Content C:\cbot\mode.txt $Mode -Encoding ascii }
# Only the bot itself: a powershell started with "-File C:\cbot\run.ps1" (not any command line that merely mentions run.ps1).
$rx = '-File\s+"?' + [regex]::Escape('C:\cbot\run.ps1') + '"?(\s|$)'
$bots = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | ? { $_.ProcessId -ne $PID -and $_.CommandLine -match $rx })
if ($bots.Count -gt 0) {
  $ids = @($bots | % { $_.ProcessId })
  New-Item C:\cbot\stop.flag -ItemType File -Force | Out-Null
  for ($i = 0; $i -lt 30 -and (Get-Process -Id $ids -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 300 }
  Get-Process -Id $ids -ErrorAction SilentlyContinue | Stop-Process -Force   # older builds ignore the flag
  Wait-Process -Id $ids -Timeout 10 -ErrorAction SilentlyContinue           # let it really exit so build.ps1 can replace pvz.dll
  Remove-Item C:\cbot\stop.flag -ErrorAction SilentlyContinue
}
. C:\cbot\build.ps1
Start-Process powershell -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-File', 'C:\cbot\run.ps1', '-Secs', $Secs -WindowStyle Hidden
"bot relaunched (mode: " + $(if (Test-Path C:\cbot\mode.txt) { (Get-Content C:\cbot\mode.txt).Trim() } else { "learn" }) + ")"
