# analyze.ps1 - offline evaluator for the v2 bot logs. Reads only; never touches the game or the bot.
# Inputs (each optional): replays\index.txt + replays\*.txt, outcomes.txt, vase_contents.txt, pro_record.txt, nemesis.txt, simcal_report.txt
# Usage: powershell -NoProfile -File C:\cbot\v2\analyze.ps1 [-Json] [-Recent 5] [-Window 10] [-Root C:\cbot]
# Windows PowerShell 5.1.
param([switch]$Json, [int]$Recent = 5, [double]$Window = 10, [string]$Root = 'C:\cbot')
$IC = [Globalization.CultureInfo]::InvariantCulture
$Paths = @{
  index = Join-Path $Root 'replays\index.txt'; replays = Join-Path $Root 'replays'; outcomes = Join-Path $Root 'outcomes.txt'
  vases = Join-Path $Root 'vase_contents.txt'; pro = Join-Path $Root 'pro_record.txt'; nemesis = Join-Path $Root 'nemesis.txt'
  simcal = Join-Path $Root 'simcal_report.txt'
}
function Read-Lines([string]$path) {   # shared read: the bot may be appending
  if (-not (Test-Path -LiteralPath $path)) { return }
  try {
    $fs = New-Object IO.FileStream($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
    $sr = New-Object IO.StreamReader($fs)
    try { $t = $sr.ReadToEnd() } finally { $sr.Dispose() }
    if ($t.Length -gt 0) { $t.TrimEnd("`r", "`n") -split "`r?`n" | ? { $_ -ne '' } }
  } catch { }
}
function Kv([string]$line) { $d = @{}; foreach ($t in $line -split ' ') { $e = $t.IndexOf('='); if ($e -gt 0) { $d[$t.Substring(0, $e)] = $t.Substring($e + 1) } }; $d }
function Tally($list, [scriptblock]$key) { $h = @{}; foreach ($x in $list) { $k = [string](& $key $x); $h[$k] = 1 + [int]$h[$k] }; $h }
function TopStr($h, [int]$n = 8) { (($h.GetEnumerator() | sort Value -Descending | select -First $n | % { "$($_.Key) $($_.Value)" }) -join ', ') }
function Pct($a, $b) { if ($b -gt 0) { [int][math]::Round(100.0 * $a / $b) } else { 0 } }
function Num([string]$s) { $v = 0.0; if ([double]::TryParse($s, [Globalization.NumberStyles]::Float, $IC, [ref]$v)) { $v } else { [double]::NaN } }

$src = [ordered]@{}; $Rep = [ordered]@{ generated = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'); root = $Root }
$ReportLines = New-Object System.Collections.Generic.List[string]
function Say([string]$s) { $script:ReportLines.Add($s) }

# ---------------- load ----------------
$idx = @(Read-Lines $Paths.index | % {
  if ($_ -match '^(\S+ \S+) lvl=(-?\d+) result=(\S+) round=(\d+) killer=(\S+) dur=([\d.]+)s lines=(\d+) file=(\S+)\s*(.*)$') {
    $ex = $Matches[9]; $o = [ordered]@{ time = $Matches[1]; lvl = [int]$Matches[2]; result = $Matches[3]; round = [int]$Matches[4]; killer = $Matches[5]
      dur = Num $Matches[6]; lines = [int]$Matches[7]; file = $Matches[8]; extra = $ex; where = '' }
    if ($ex -match 'round (\d+) vases left (\d+)') { $o.round = [int]$Matches[1]; $o.where = "R$($Matches[1]) left $($Matches[2])" }
    elseif ($ex -match 'wave (\d+)/(\d+)') { $o.where = "wave $($Matches[1])/$($Matches[2])" }
    elseif ($ex -match 'vr=(\d+)') { $o.where = "R$($Matches[1])" }
    [pscustomobject]$o
  } })
$src.index = if (Test-Path $Paths.index) { "$($idx.Count) dumps" } else { 'missing' }
$nRep = if (Test-Path $Paths.replays) { @(Get-ChildItem $Paths.replays -Filter '*_lvl*.txt' -ErrorAction SilentlyContinue).Count } else { 0 }
$src.replays = if (Test-Path $Paths.replays) { "$nRep files" } else { 'missing' }

$outc = @(Read-Lines $Paths.outcomes | % {
  if ($_ -match '^(\S+ \S+) lvl(-?\d+) OUTCOME (\S+) (\S+) target=(\S+) row (\d+) col (\d+) round (\d+) t=([\d.]+)(.*)$') {
    [pscustomobject]@{ time = $Matches[1]; lvl = [int]$Matches[2]; plant = $Matches[3]; res = $Matches[4]; target = $Matches[5]; row = [int]$Matches[6]
      col = [int]$Matches[7]; round = [int]$Matches[8]; t = Num $Matches[9]; extra = $Matches[10].Trim() }
  } })
$src.outcomes = if (Test-Path $Paths.outcomes) { "$($outc.Count) judgements" } else { 'missing' }

$vrec = @(Read-Lines $Paths.vases | ? { $_ -like 'V *' } | % { $d = Kv $_; if ($d.lvl -and $d.r) { $d } })
$src.vase_contents = if (Test-Path $Paths.vases) { "$($vrec.Count) round records" } else { 'missing' }

$pro = @(Read-Lines $Paths.pro | % {
  if ($_ -match '^(\S+ \S+) PRO lvl (\d+) (DIED|WON)\s*(.*)$') {
    $w = $Matches[4]; $o = [ordered]@{ time = $Matches[1]; lvl = [int]$Matches[2]; res = $Matches[3]; where = ''; round = 0 }
    if ($w -match 'round (\d+),? vases left (\d+)') { $o.round = [int]$Matches[1]; $o.where = "R$($Matches[1])" }
    elseif ($w -match 'wave (\d+)/(\d+)' -and $Matches[1] -ne '0') { $o.where = "w$($Matches[1])" }   # wave 0 = vase level before round logging
    [pscustomobject]$o
  } })
$src.pro_record = if (Test-Path $Paths.pro) { "$($pro.Count) results" } else { 'missing' }

$nem = @(Read-Lines $Paths.nemesis | % { if ($_ -match '^(\S+ \S+) (\S+) lvl(\d+) (\S+)') { [pscustomobject]@{ time = $Matches[1]; mode = $Matches[2]; lvl = [int]$Matches[3]; killer = $Matches[4] } } })
$src.nemesis = if (Test-Path $Paths.nemesis) { "$($nem.Count) killers" } else { 'missing' }
$sim = @(Read-Lines $Paths.simcal)
$src.simcal_report = if (Test-Path $Paths.simcal) { "$($sim.Count) lines" } else { 'missing' }
$Rep.sources = $src

Say "=== cbot analyze $($Rep.generated) ==="
Say ("sources: " + (($src.GetEnumerator() | % { "$($_.Key)=$($_.Value)" }) -join ' | '))

# ---------------- deaths by level and vase round ----------------
Say ""; Say "## DEATHS BY LEVEL"
$lv = @()
foreach ($g in ($pro | group lvl | sort { [int]$_.Name })) {
  $d = @($g.Group | ? { $_.res -eq 'DIED' }); $w = @($g.Group | ? { $_.res -eq 'WON' })
  $last50 = @($g.Group | select -Last 50); $w50 = @($last50 | ? { $_.res -eq 'WON' }).Count
  $where = Tally ($d | ? { $_.where }) { param($x) $x.where }
  $lv += [pscustomobject][ordered]@{ lvl = [int]$g.Name; deaths = $d.Count; wins = $w.Count; win_pct = (Pct $w.Count $g.Count); last50_win_pct = (Pct $w50 $last50.Count)
    first = $g.Group[0].time; last = $g.Group[-1].time; died_at = $where }
  Say ("lvl {0,-3} {1,4} deaths {2,3} wins ({3}% all, {4}% last {5}) | {6} .. {7}" -f $g.Name, $d.Count, $w.Count, (Pct $w.Count $g.Count), (Pct $w50 $last50.Count), $last50.Count, $g.Group[0].time, $g.Group[-1].time)
  if ($where.Count) {
    $ws = $where.GetEnumerator() | sort { if ($_.Key -match '\d+') { [int]$Matches[0] } else { 0 } } | % { "$($_.Key):$($_.Value)" }
    Say ("   died at: " + ($ws -join ' '))
  }
}
if (-not $pro.Count) { Say "(no pro_record.txt results)" }
$Rep.levels = $lv
# From replay dumps: per level x vase round reached, with result.
$ir = @()
if ($idx.Count) {
  Say "replay dumps by lvl/round (result counts):"
  foreach ($g in ($idx | group lvl, round | sort { $_.Group[0].lvl }, { $_.Group[0].round })) {
    $t = Tally $g.Group { param($x) $x.result }
    $ir += [pscustomobject][ordered]@{ lvl = $g.Group[0].lvl; round = $g.Group[0].round; results = $t; avg_dur = [math]::Round((($g.Group | measure dur -Average).Average), 1) }
    Say ("   lvl {0} R{1}: {2} | avg span {3:0.0} game s" -f $g.Group[0].lvl, $g.Group[0].round, (TopStr $t), (($g.Group | measure dur -Average).Average))
  }
}
$Rep.dumps_by_round = $ir

# ---------------- killers ----------------
Say ""; Say "## TOP KILLERS"
$kAll = Tally $nem { param($x) $x.killer }
$kRecent = Tally ($nem | select -Last 50) { param($x) $x.killer }
$kIdx = Tally ($idx | ? { $_.result -eq 'DIED' -and $_.killer -ne '-' }) { param($x) "$($x.killer)@R$($x.round)" }
if ($nem.Count) {
  Say ("nemesis all ($($nem.Count)): " + (TopStr $kAll))
  Say ("nemesis last 50: " + (TopStr $kRecent))
  foreach ($g in ($nem | group lvl | sort { [int]$_.Name })) { if (@($nem | group lvl).Count -gt 1) { Say ("   lvl $($g.Name): " + (TopStr (Tally $g.Group { param($x) $x.killer }) 5)) } }
}
if ($kIdx.Count) { Say ("replay killers by round: " + (TopStr $kIdx 10)) }
if (-not $nem.Count -and -not $kIdx.Count) { Say "(no killer data)" }
$Rep.killers = [ordered]@{ all = $kAll; last50 = $kRecent; by_round = $kIdx }

# ---------------- one-shot plant outcomes ----------------
Say ""; Say "## PLANT OUTCOMES (hit = HIT_KILL/HIT_DMG/CHARMED/BITE; miss = MISS/EATEN/LOST; WIPED is neutral)"
$hitSet = 'HIT_KILL', 'HIT_DMG', 'CHARMED', 'BITE'; $missSet = 'MISS', 'EATEN', 'LOST'
$po = @()
foreach ($g in ($outc | group plant | sort Count -Descending)) {
  $all = $g.Group; $h = @($all | ? { $hitSet -contains $_.res }).Count; $m = @($all | ? { $missSet -contains $_.res }).Count
  $res = Tally $all { param($x) $x.res }
  $byR = @(); foreach ($rg in ($all | group round | sort { [int]$_.Name })) {
    $rh = @($rg.Group | ? { $hitSet -contains $_.res }).Count; $rm = @($rg.Group | ? { $missSet -contains $_.res }).Count
    $byR += [pscustomobject]@{ round = [int]$rg.Name; n = $rg.Count; hits = $rh; misses = $rm; hit_pct = (Pct $rh ($rh + $rm)) }
  }
  $tg = @{}; foreach ($x in ($all | ? { $_.target -ne '-' })) { if (-not $tg[$x.target]) { $tg[$x.target] = @{} }; $tg[$x.target][$x.res] = 1 + [int]$tg[$x.target][$x.res] }
  $byT = @($tg.GetEnumerator() | sort { -($_.Value.Values | measure -Sum).Sum } | % { [pscustomobject]@{ target = $_.Key; results = $_.Value } })
  $po += [pscustomobject][ordered]@{ plant = $g.Name; n = $all.Count; hits = $h; misses = $m; hit_pct = (Pct $h ($h + $m)); results = $res; by_round = $byR; by_target = $byT }
  Say ("{0,-8} n={1,-4} hit {2,3}% | {3}" -f $g.Name, $all.Count, (Pct $h ($h + $m)), (TopStr $res))
  if ($byR.Count) { Say ("   by round: " + (($byR | % { "R$($_.round) $($_.hit_pct)% of $($_.hits + $_.misses)" + $(if ($_.n -ne $_.hits + $_.misses) { " (+$($_.n - $_.hits - $_.misses) wiped)" }) }) -join ' | ')) }
  if ($byT.Count) { Say ("   targets hit: " + (($byT | select -First 6 | % { $t = $_.results; "$($_.target) " + (($t.GetEnumerator() | sort Value -Descending | % { "$($_.Key -replace 'HIT_','')$($_.Value)" }) -join '/') }) -join ', ')) }
}
if ($outc.Count) {
  Say "   (misses carry no target, so per-target numbers are hit mixes, not rates)"
  $rec = @($outc | select -Last 200); $rh = @($rec | ? { $hitSet -contains $_.res }).Count; $rm = @($rec | ? { $missSet -contains $_.res }).Count
  Say ("   last 200 judgements: hit $(Pct $rh ($rh + $rm))% of $($rh + $rm)")
} else { Say "(no outcomes.txt judgements)" }
$Rep.outcomes = $po

# ---------------- vase rounds ----------------
Say ""; Say "## VASE ROUND CONTENTS (avg per round; full = all vases broken, not a reload)"
$vo = @()
foreach ($g in ($vrec | group { "$($_.lvl)|$($_.r)" } | sort { [int]($_.Name -split '\|')[0] }, { [int]($_.Name -split '\|')[1] })) {
  $all = @($g.Group); $full = @($all | ? { $_.rl -ne '1' -and [int]$_.n -gt 0 -and [int]$_.br -ge [int]$_.n })
  $use = @(if ($full.Count) { $full } else { $all })
  $avg = { param($recs, $key) $recs = @($recs); $sum = @{}; foreach ($r in $recs) { if ($r[$key] -and $r[$key] -ne '-') { foreach ($p in $r[$key] -split ',') { $kv = $p -split ':'; if ($kv.Count -eq 2) { $sum[$kv[0]] = [int]$sum[$kv[0]] + [int]$kv[1] } } } }
    $o = [ordered]@{}; foreach ($k in ($sum.Keys | sort { -$sum[$_] })) { $o[$k] = [math]::Round($sum[$k] / [double]$recs.Count, 1) }; $o }
  $z = & $avg $use 'z'; $s = & $avg $use 's'
  $look = @{ q = @(0, 0, 0); l = @(0, 0, 0); k = @(0, 0, 0) }
  foreach ($r in $all) { if ($r.lk) { foreach ($p in $r.lk -split ',') { $kv = $p -split ':'; if ($kv.Count -eq 2 -and $look.ContainsKey($kv[0])) { $n = $kv[1] -split '/'; for ($i = 0; $i -lt 3 -and $i -lt $n.Count; $i++) { $look[$kv[0]][$i] += [int]$n[$i] } } } } }
  $resT = Tally $all { param($x) $x.res }
  $nAvg = ($use | % { [int]$_.n } | measure -Average).Average
  $lvl, $rnd = $g.Name -split '\|'
  $vo += [pscustomobject][ordered]@{ lvl = [int]$lvl; round = [int]$rnd; records = $all.Count; full = $full.Count; vases = [math]::Round($nAvg, 1); zombies = $z; plants = $s
    look = [ordered]@{ plain_z_p_n = $look.q -join '/'; leaf_z_p_n = $look.l -join '/'; skull_z_p_n = $look.k -join '/' }; ends = $resT }
  Say ("lvl {0} R{1}: {2} recs ({3} full), {4:0.#} vases | Z: {5} | P: {6}" -f $lvl, $rnd, $all.Count, $full.Count, $nAvg,
    (($z.GetEnumerator() | % { "$($_.Key) $($_.Value)" }) -join ', '), (($s.GetEnumerator() | % { "$($_.Key) $($_.Value)" }) -join ', '))
  Say ("   look -> zombie/plant/none: plain {0} leaf {1} skull {2} | ends: {3}" -f ($look.q -join '/'), ($look.l -join '/'), ($look.k -join '/'), (TopStr $resT))
}
if (-not $vrec.Count) { Say "(no vase_contents.txt records)" }
$Rep.vase_rounds = $vo

# ---------------- recent deaths ----------------
Say ""; Say "## LAST $Recent DEATHS (last $Window game s from the replay dumps)"
function Z-Compact([string]$z) {   # "1A,FOOT,2,345,1900,E" -> "FOOT r2 x345 1900E"
  ($z.Trim() -split ' ' | ? { $_ } | % { $f = $_ -split ','; if ($f.Count -ge 5) { "$($f[1]) r$($f[2]) x$($f[3]) $($f[4])" + (($f | select -Skip 5) -join '') } else { $_ } }) -join '; '
}
$deaths = @()
$dIdx = @($idx | ? { $_.result -eq 'DIED' } | select -Last $Recent)
if ($dIdx.Count) {
  [array]::Reverse($dIdx)
  foreach ($d in $dIdx) {
    $ev = New-Object System.Collections.Generic.List[string]
    $path = Join-Path $Paths.replays $d.file
    $lines = @(Read-Lines $path)
    if ($lines.Count) {
      $body = @($lines | ? { $_ -match '^[SN] t=' })
      # Keep only the last attempt in the ring (game time restarts on a new level / Try Again); its last line is the death.
      $seg = 0; $pt = -1.0; for ($i = 0; $i -lt $body.Count; $i++) { $v = Num (($body[$i] -split ' ')[1] -replace 't=', ''); if ($v -lt $pt - 1) { $seg = $i }; $pt = $v }
      $body = @($body | select -Skip $seg)
      $lastT = 0.0; if ($body.Count) { $lastT = Num (($body[-1] -split ' ')[1] -replace 't=', '') }
      $from = $lastT - $Window; $lastSnapT = -99.0
      $snaps = @($body | ? { $_ -like 'S *' -and (Num (($_ -split ' ')[1] -replace 't=', '')) -ge $from })
      $finalS = $snaps | select -Last 1
      foreach ($l in $body) {
        if ($l -notmatch '^([SN]) t=([\d.]+) (.*)$') { continue }
        $k = $Matches[1]; $t = Num $Matches[2]; $rest = $Matches[3]
        if ($t -lt $from) { continue }
        if ($k -eq 'N') { $ev.Add(("{0,6:0.0} {1}" -f $t, $rest)); continue }
        if ($t - $lastSnapT -lt 2 -and $l -ne $finalS) { continue }   # one snapshot per ~2 s, plus the final one
        $lastSnapT = $t
        $hdr = if ($rest -match '^(sun=\S+ w=\S+ vr=\S+ mow=\S+(?: dz=\S+)?)') { $Matches[1] } else { '' }
        $vz = if ($rest -match '\| V:([^|]*)') { @($Matches[1].Trim() -split ' ' | ? { $_ }).Count } else { 0 }
        $zs = if ($rest -match '\| Z:([^|]*)') { Z-Compact $Matches[1] } else { '' }
        $s = "{0,6:0.0} S {1} vases={2} Z: {3}" -f $t, $hdr, $vz, $zs
        if ($l -eq $finalS -and $rest -match '\| P:([^|]*)') { $s += " | P: " + (($Matches[1].Trim() -split ' ' | ? { $_ } | % { $f = $_ -split ','; if ($f.Count -ge 3) { "$($f[0])$($f[1])$($f[2])" } else { $_ } }) -join ' ') }
        if ($s.Length -gt 260) { $s = $s.Substring(0, 257) + '...' }
        $ev.Add($s)
      }
    } else { $ev.Add("(replay file missing: $($d.file))") }
    $deaths += [pscustomobject][ordered]@{ time = $d.time; lvl = $d.lvl; round = $d.round; where = $d.where; killer = $d.killer; span = $d.dur; file = $d.file; last_events = @($ev) }
    Say ("* {0} lvl {1} {2} killer {3} span {4:0.0}s  {5}" -f $d.time, $d.lvl, $(if ($d.where) { $d.where } else { "R$($d.round)" }), $d.killer, $d.dur, $d.file)
    foreach ($e in $ev) { Say "    $e" }
  }
} else {
  # No replay dumps yet: fall back to pro_record + nemesis (same minute).
  $pd = @($pro | ? { $_.res -eq 'DIED' } | select -Last $Recent); [array]::Reverse($pd)
  if ($pd.Count) { Say "(no replay dumps yet - pro_record + nemesis only)" }
  foreach ($d in $pd) {
    $mn = $d.time.Substring(0, 16); $k = ($nem | ? { $_.time -eq $mn -and $_.lvl -eq $d.lvl } | select -Last 1).killer
    if (-not $k) { $k = '?' }
    $deaths += [pscustomobject][ordered]@{ time = $d.time; lvl = $d.lvl; round = $d.round; where = $d.where; killer = $k; span = $null; file = $null; last_events = @() }
    Say ("* {0} lvl {1} {2} killer {3}" -f $d.time, $d.lvl, $(if ($d.where) { $d.where } else { '-' }), $k)
  }
  if (-not $pd.Count) { Say "(no deaths recorded)" }
}
$Rep.recent_deaths = $deaths

# ---------------- sim calibration ----------------
if ($sim.Count) {
  Say ""; Say "## SIMCAL (simcal_report.txt, first lines)"
  $sim | ? { $_ -notmatch '^\s+(simDead|Predictions)' } | select -First 28 | % { Say $_ }
}
$Rep.simcal = $sim

if ($Json) { $Rep | ConvertTo-Json -Depth 8 } else { $ReportLines }
