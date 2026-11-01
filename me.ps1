# My own controls for playing by hand (no bot decisions).
# Usage: me.ps1 "look"  |  "plant 2 1 0; plant 6 3 1"  |  "shovel 2 4"  |  "drop 1 3"  |  "click 400 300"  |  "wait 2000"
#   plant SLOT ROW COL : click seed packet SLOT (1-based), then cell (ROW 0-4, COL 0-8)
#   drop ROW COL       : pick up the nearest dropped seed packet on the lawn and plant it at ROW COL
#   shovel ROW COL     : dig up the plant at ROW COL
# Every call first collects sun on screen and unpauses if needed, then ends with "look".
param([string]$Cmds = "look")
. C:\cbot\build.ps1
function CellX($c) { 40 + $c * 80 + 40 }
function CellY($r) { 80 + $r * 100 + 55 }
function Collect {
  $s = [Mem]::Get()
  if ($s.Paused) { [void][Pvz]::Unpause(); Start-Sleep -Milliseconds 300; $s = [Mem]::Get() }
  foreach ($c in $s.Suns) { if (-not $c.IsPacket -and $c.Type -ge 11 -and $c.Type -le 13) { [Pvz]::PostClick($c.X, $c.Y, $false); Start-Sleep -Milliseconds 20 } }
  foreach ($c in $s.Suns) { if (-not $c.IsPacket -and ($c.Type -lt 11 -or $c.Type -gt 13)) { [Pvz]::PostClick($c.X, $c.Y, $false); Start-Sleep -Milliseconds 20 } }   # coins
}
function Look {
  $s = [Mem]::Get()
  $tf = [Bot]::WavesToFlag($s)
  "LEVEL {0}-{1}  wave {2}/{3}  next flag in {4}  SUN {5}  scene {6}" -f ([int][math]::Floor(($s.Level-1)/10)+1), (($s.Level-1)%10+1), $s.Wave, $s.Waves, $tf, $s.Sun, $s.Scene
  "packets: " + (($s.Packets | % { "{0}={1}`${2}{3}" -f $_.Slot, [Mem]::SeedName($_.Type), $_.Cost, $(if ($_.Ready) { '' } elseif ($_.Cd) { '(cd)' } else { '(-)' }) }) -join '  ')
  $drops = $s.Suns | ? { $_.IsPacket }
  if ($drops) { "dropped packets: " + (($drops | % { [Mem]::SeedName($_.Seed) + "@" + [int](($_.X-40)/80) + "," + [int](($_.Y-80)/100) }) -join ' ') }
  "      c0    c1    c2    c3    c4    c5    c6    c7    c8   | mower | zombies (x, col, hp)"
  for ($r = 0; $r -lt 5; $r++) {
    $line = "r$r  "
    for ($c = 0; $c -lt 9; $c++) {
      $p = $s.Plants | ? { $_.Row -eq $r -and $_.Col -eq $c } | select -First 1
      if ($p) { $line += ([Mem]::SeedName($p.Type)).PadRight(5).Substring(0,5) + " " }
      elseif ($s.Graves.Contains($r*10+$c)) { $line += "RIP   " }
      elseif ($s.Blocked.Contains($r*10+$c)) { $line += "hole  " }
      else { $line += ".     " }
    }
    $mw = @{ -1='gone'; 0='in'; 1='ok'; 2='USED'; 3='squash' }[$s.Mowers[$r]]
    $zs = $s.Zombies | ? { $_.Row -eq $r -and $_.X -lt 800 } | sort X | % { "{0}@{1}(c{2},{3})" -f [Mem]::ZomName($_.Type), [int]$_.X, [math]::Max(0,[int](($_.X+30-40)/80)), $_.Total }
    $line += "| $($mw.PadRight(5)) | " + ($zs -join ' ')
    $line
  }
  $off = ($s.Zombies | ? { $_.X -ge 800 }).Count
  if ($off) { "($off zombies still off-screen)" }
}
Collect
foreach ($cmd in ($Cmds -split ';')) {
  $a = $cmd.Trim() -split '\s+'
  switch ($a[0]) {
    'plant' {
      $s = [Mem]::Get(); $p = $s.Packets[[int]$a[1] - 1]
      [Pvz]::PostClick($p.CX, $p.CY, $false); Start-Sleep -Milliseconds 120
      [Pvz]::PostClick((CellX ([int]$a[3])), (CellY ([int]$a[2])), $false); Start-Sleep -Milliseconds 150
      if ([Mem]::CursorType() -ne 0) { [Pvz]::PostClick(400, 590, $true); "  (plant $($a[1]) at r$($a[2])c$($a[3]) did not take)" }
    }
    'plantw' {   # wait (up to 60s, collecting sun) until the packet is ready and affordable, then plant
      $end = (Get-Date).AddSeconds(60)
      while ((Get-Date) -lt $end) { $s = [Mem]::Get(); $p = $s.Packets[[int]$a[1] - 1]; if ($p.Ready -or $s.Scene -ne 3) { break }; Collect; Start-Sleep -Milliseconds 150 }
      $s = [Mem]::Get(); $p = $s.Packets[[int]$a[1] - 1]
      if ($s.Plants | ? { $_.Row -eq [int]$a[2] -and $_.Col -eq [int]$a[3] }) { "  (r$($a[2])c$($a[3]) already taken)"; break }
      [Pvz]::PostClick($p.CX, $p.CY, $false); Start-Sleep -Milliseconds 100
      [Pvz]::PostClick((CellX ([int]$a[3])), (CellY ([int]$a[2])), $false); Start-Sleep -Milliseconds 120
      if ([Mem]::CursorType() -ne 0) { [Pvz]::PostClick(400, 590, $true); "  (plantw $($a[1]) at r$($a[2])c$($a[3]) did not take)" }
    }
    'drop' {
      $s = [Mem]::Get(); $d = $s.Suns | ? { $_.IsPacket } | select -First 1
      if ($d) { [Pvz]::PostClick($d.X, $d.Y, $false); Start-Sleep -Milliseconds 120; [Pvz]::PostClick((CellX ([int]$a[2])), (CellY ([int]$a[1])), $false); Start-Sleep -Milliseconds 150 } else { "  (no dropped packet)" }
    }
    'shovel' {
      $s = [Mem]::Get(); $sx = ($s.Packets | % { [Mem]::BankX + $_.X + $_.W } | measure -Maximum).Maximum + 42
      [Pvz]::PostClick($sx, 36, $false); Start-Sleep -Milliseconds 120
      [Pvz]::PostClick((CellX ([int]$a[2])), (CellY ([int]$a[1])), $false); Start-Sleep -Milliseconds 150
    }
    'retry' { [Pvz]::Click(390, 366, $false); Start-Sleep 2 }
    'next'  { [Pvz]::Click(400, 520, $false); Start-Sleep 1; [Pvz]::Click(400, 558, $false); Start-Sleep 2 }
    'pick' {   # pick NAME NAME ... : empty the bank, then choose these plants in the seed chooser
      $names = $a | select -Skip 1
      $ib=0; $tc=0; $fl=0
      foreach ($c in ([Mem]::ChooserCards([ref]$ib,[ref]$tc,[ref]$fl) | ? { $_.State -eq 1 })) { [Pvz]::Click($c.X + 25, $c.Y + 35, $false); Start-Sleep -Milliseconds 500 }
      foreach ($n in $names) {
        $c = [Mem]::ChooserCards([ref]$ib,[ref]$tc,[ref]$fl) | ? { [Mem]::SeedName($_.Seed) -eq $n -and $_.State -eq 3 } | select -First 1
        if ($c) { [Pvz]::Click($c.X + 25, $c.Y + 35, $false); Start-Sleep -Milliseconds 600 } else { "  (no $n card)" }
      }
    }
    'rock'  { [Pvz]::Click(232, 565, $false); Start-Sleep 2; if ([Mem]::OpenDialogs() -gt 0) { "  dialog: " + [Mem]::DialogText() } }
    'talk'  { for ($i = 0; $i -lt 25 -and [Mem]::DaveState() -ge 3; $i++) { "  DAVE: " + [Mem]::DaveText(); [Pvz]::Click(400, 300, $false); Start-Sleep -Milliseconds 900 } }
    'click' { [Pvz]::Click([int]$a[1], [int]$a[2], $false) }
    'wait'  { $end = (Get-Date).AddMilliseconds([int]$a[1]); while ((Get-Date) -lt $end) { Collect; Start-Sleep -Milliseconds 400 } }
    'look'  { }
  }
}
Collect
Look
