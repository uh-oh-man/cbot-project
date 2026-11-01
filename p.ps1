# Usage: p.ps1 "c 400 300; w 1000; r 100 100; k 27; s"
#   c x y = left click, r x y = right click, k vk = key, w ms = wait, s = screenshot -> C:\cbot\g.png
param([string]$Cmds)
. C:\cbot\build.ps1
foreach ($c in ($Cmds -split ';')) {
  $a = $c.Trim() -split '\s+'
  switch ($a[0]) {
    'c' { [Pvz]::Click([int]$a[1], [int]$a[2], $false) }
    'r' { [Pvz]::Click([int]$a[1], [int]$a[2], $true) }
    'm' { [Pvz]::Move([int]$a[1], [int]$a[2]) }
    'k' { [Pvz]::Key([byte]$a[1]) }
    'u' { if ([Pvz]::Unpause()) { 'unpaused' } }
    'px' { [Pvz]::Pixel([int]$a[1], [int]$a[2]) | % { "$($_.R),$($_.G),$($_.B)" } }
    'w' { Start-Sleep -Milliseconds ([int]$a[1]) }
    's' { [Pvz]::Shot("C:\cbot\g.png", 1.0) }
    'sun' { [Pvz]::FindSun() -join ' ' }
    # auto secs [slot:x:y ...] - collect sun for secs; each pass try the plant moves in order
    # (a move is dropped once a seed was picked and placed; failed picks are cancelled with right-click)
    'auto' {
      $end = (Get-Date).AddSeconds([int]$a[1]); $moves = [Collections.ArrayList]@($a | Select-Object -Skip 2)
      while ((Get-Date) -lt $end) {
        [void][Pvz]::Unpause()
        foreach ($s in [Pvz]::FindSun()) { $xy = $s -split ','; [Pvz]::Click([int]$xy[0], [int]$xy[1], $false) }
        if ($moves.Count) {
          $m = $moves[0] -split ':'; $sx = 120 + 57 * ([int]$m[0] - 1)
          $before = [Pvz]::PacketBrightness($sx)
          [Pvz]::Click($sx, 45, $false); Start-Sleep -Milliseconds 150
          [Pvz]::Click([int]$m[1], [int]$m[2], $false); Start-Sleep -Milliseconds 300
          # A planted seed packet goes dark (recharge); compare brightness to detect success.
          $after = [Pvz]::PacketBrightness($sx)
          if ($after -lt $before * 0.75) { "planted $($moves[0])"; $moves.RemoveAt(0) }
          else { [Pvz]::Click(400, 560, $true) }
        }
        Start-Sleep -Milliseconds 700
      }
      "left: $($moves -join ' ')"
    }
  }
}
