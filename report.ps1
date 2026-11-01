# Builds a tidy progress report (deaths per level / world) from the bot's event log.
$ws = "C:\Users\User\AppData\Local\Claude-Data\claude_pzs8sxrjxfjjc\scratch-workspaces\255952f6-c3d3-44e6-9b5d-1b6405604111\48bbb1d3-f6be-4bb6-9185-acd7de645e4d\scratch-2026-10-31-b92cb2"
$ev = Get-Content C:\cbot\events.log -ErrorAction SilentlyContinue
$deaths = @{}; $beaten = @{}; $first = @{}
foreach ($l in $ev) {
  if ($l -match '^(\S+)\s+RESULT lvl (\d+) DIED') { $n = [int]$Matches[2]; $deaths[$n] = 1 + [int]$deaths[$n]; if (-not $first[$n]) { $first[$n] = $Matches[1] } }
  elseif ($l -match '^(\S+)\s+RESULT lvl (\d+) WON') { $n = [int]$Matches[2]; if (-not $beaten[$n]) { $beaten[$n] = $Matches[1] }; if (-not $first[$n]) { $first[$n] = $Matches[1] } }
}
$st = Get-Content C:\cbot\bot.log -Tail 80 -ErrorAction SilentlyContinue | ? { $_ -match 'scene=' } | select -Last 1
$cur = if ($st -match 'lvl=(\d+)') { [int]$Matches[1] } else { 0 }
$levels = @($deaths.Keys) + @($beaten.Keys) + @($cur) | ? { $_ -gt 0 } | sort -Unique
$out = @("# PvZ bot progress (fresh save `"Claudebot`")", "", "_Updated $(Get-Date -f 'yyyy-MM-dd HH:mm')_", "")
$totalD = ($deaths.Values | Measure-Object -Sum).Sum; if (-not $totalD) { $totalD = 0 }
$out += "**Levels beaten:** $($beaten.Count)   **Total deaths:** $totalD   **Now on:** $(if ($cur) { '{0}-{1}' -f ([math]::Floor(($cur-1)/10)+1), (($cur-1)%10+1) } else { '?' })", ""
foreach ($w in ($levels | % { [math]::Floor(($_ - 1) / 10) + 1 } | sort -Unique)) {
  $inWorld = $levels | ? { [math]::Floor(($_ - 1) / 10) + 1 -eq $w }
  $wd = ($inWorld | % { [int]$deaths[$_] } | Measure-Object -Sum).Sum
  $name = @{1='Day';2='Night';3='Pool';4='Fog';5='Roof'}[[int]$w]
  $out += "## World $w ($name) - $wd death$(if ($wd -ne 1) { 's' })", "", "| Level | Deaths | Result |", "|---|---:|---|"
  foreach ($n in $inWorld) {
    $res = if ($beaten[$n]) { "beaten at $($beaten[$n])" } elseif ($n -eq $cur) { "in progress" } else { "-" }
    $out += "| {0}-{1} | {2} | {3} |" -f $w, (($n-1)%10+1), [int]$deaths[$n], $res
  }
  $out += ""
}
$mi = [array]::FindLastIndex([string[]]$ev, [Predicate[string]]{ param($x) $x -match 'MINE-COUNT-START' }); $mev = if ($mi -ge 0) { $ev[$mi..($ev.Count-1)] } else { $ev }
$mBoom = ($mev | ? { $_ -match 'LEARN potato mine EXPLODED' }).Count
$mEaten = ($mev | ? { $_ -match 'LEARN potato mine was eaten' }).Count
$mSkip = ($mev | ? { $_ -match 'LEARN mine at .* not counted' }).Count
$out += "## Potato mine observations", "", "| Counted as explosion | Counted as eaten unarmed | Skipped (mower / chomper / bomb) |", "|---:|---:|---:|", "| $mBoom | $mEaten | $mSkip |", ""
$warn = Get-Content C:\cbot\sanity.txt -ErrorAction SilentlyContinue
$out += "## Sanity warnings", ""
if ($warn) { $out += ($warn | select -Last 15 | % { "- $_" }) } else { $out += "_None: every learned belief is within its plausible range._" }
$out += ""
$out | Set-Content C:\cbot\progress.md -Encoding utf8
Copy-Item C:\cbot\progress.md (Join-Path $ws "pvz_progress.md") -Force
