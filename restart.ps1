# Hot reload: ask the running bot to save and exit, rebuild, relaunch detached.
param([int]$Secs = 86400, [string]$Mode = "")
# -Mode pro | learn switches the brain (remembered in mode.txt); omitted = keep the current mode.
if ($Mode) { Set-Content C:\cbot\mode.txt $Mode -Encoding ascii }
$bots = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | ? { $_.CommandLine -match 'run\.ps1' -and $_.ProcessId -ne $PID }
if ($bots) {
  New-Item C:\cbot\stop.flag -ItemType File -Force | Out-Null
  for ($i = 0; $i -lt 30 -and (Get-Process -Id $bots.ProcessId -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 300 }
  Get-Process -Id $bots.ProcessId -ErrorAction SilentlyContinue | Stop-Process -Force   # older builds ignore the flag
  Remove-Item C:\cbot\stop.flag -ErrorAction SilentlyContinue
}
. C:\cbot\build.ps1
Start-Process powershell -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-File', 'C:\cbot\run.ps1', '-Secs', $Secs -WindowStyle Hidden
"bot relaunched (mode: " + $(if (Test-Path C:\cbot\mode.txt) { (Get-Content C:\cbot\mode.txt).Trim() } else { "learn" }) + ")"
