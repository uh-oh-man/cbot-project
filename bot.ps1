# Real-time bot: collects sun, plants moves by priority when the packet is lit.
# Usage: bot.ps1 -Secs 180 -Moves "2:75:225","1:155:325",...   (slot:x:y)
param([int]$Secs = 180, [string[]]$Moves = @())
$dll = "C:\cbot\pvz.dll"
if ((Get-Item C:\cbot\pvz.cs).LastWriteTime -gt (Get-Item $dll).LastWriteTime) {
  Remove-Item $dll; Add-Type -Path C:\cbot\pvz.cs -ReferencedAssemblies System.Drawing -OutputAssembly $dll -OutputType Library
}
Add-Type -Path $dll
$log = "C:\cbot\bot.log"; "start $(Get-Date -f T) moves=$($Moves -join ' ')" | Out-File $log
$todo = [Collections.ArrayList]@($Moves)
$max = @{}   # brightest value seen per packet slot = "lit"
$end = (Get-Date).AddSeconds($Secs); $i = 0
while ((Get-Date) -lt $end) {
  try {
    if ([Pvz]::Unpause()) { "unpaused" | Out-File $log -Append }
    foreach ($s in [Pvz]::FindSun()) { $xy = $s -split ','; [Pvz]::Click([int]$xy[0], [int]$xy[1], $false) }
    foreach ($m in @($todo)) {
      $p = $m -split ':'; $slot = [int]$p[0]; $sx = 120 + 58 * ($slot - 1)
      $b = [Pvz]::PacketBrightness($sx)
      if (-not $max[$slot] -or $b -gt $max[$slot]) { $max[$slot] = $b }
      if ($b -lt $max[$slot] * 0.9) { continue }      # packet dim: unaffordable or recharging
      [Pvz]::Click($sx, 45, $false); Start-Sleep -Milliseconds 120
      [Pvz]::Click([int]$p[1], [int]$p[2], $false); Start-Sleep -Milliseconds 250
      if ([Pvz]::PacketBrightness($sx) -lt $b * 0.8) { "planted $m $(Get-Date -f T)" | Out-File $log -Append; $todo.Remove($m) }
      else { [Pvz]::Click(400, 570, $true) }        # drop the held seed
      break
    }
    if (($i++ % 15) -eq 0) { [void][Pvz]::Shot("C:\cbot\g.png", 1.0) }
  } catch { "err $_" | Out-File $log -Append; Start-Sleep 1 }
  Start-Sleep -Milliseconds 200
}
"end left=$($todo -join ' ')" | Out-File $log -Append
