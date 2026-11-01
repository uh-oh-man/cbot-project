# Discord relay for the PvZ bot project.
# - Reads ONE channel (config: channel name, default "pvz-bot") by polling Discord's REST API. No packages, no gateway.
# - "!" commands are answered instantly from local project files (text only).
# - Anything else is queued in inbox.jsonl for Claude; Claude's replies are dropped into outbox\ and posted here.
# - Outbound is text only, mentions are disabled, the token never leaves this machine except in the auth header.
param([string]$ChannelName = "pvz-bot", [string]$ChannelId = "1566162258810249226", [string]$OwnerName = "uh_oh_man")
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$dir = "C:\cbot\discord"
$log = "$dir\relay.log"
function Log($m) { Add-Content $log ((Get-Date -Format "yyyy-MM-dd HH:mm:ss") + " " + $m) }
$tok = (New-Object System.Net.NetworkCredential("", (Get-Content "$dir\token.dpapi" | ConvertTo-SecureString))).Password
$H = @{ Authorization = "Bot $tok"; "User-Agent" = "DiscordBot (local-pvz-relay, 1.0)" }
$api = "https://discord.com/api/v10"

function Api($method, $path, $body = $null) {
  for ($try = 0; $try -lt 3; $try++) {
    try {
      if ($body -ne $null) {
        $json = $body | ConvertTo-Json -Depth 6 -Compress
        $r = Invoke-RestMethod -Method $method -Uri "$api$path" -Headers $H -ContentType "application/json; charset=utf-8" -Body ([Text.Encoding]::UTF8.GetBytes($json))
      } else { $r = Invoke-RestMethod -Method $method -Uri "$api$path" -Headers $H }
      return ($r | ForEach-Object { $_ })   # PS 5.1 returns JSON arrays as one object: unroll them
    } catch {
      $code = $_.Exception.Response.StatusCode.value__
      if ($code -eq 429) { Start-Sleep -Seconds 3; continue }   # rate limited: back off
      Log "api $method $path failed ($code): $($_.Exception.Message)"
      return $null
    }
  }
}
function Say($text, $replyTo = $null) {
  # Split long text into Discord-sized chunks; never ping anyone.
  $chunks = @(); $t = [string]$text
  while ($t.Length -gt 1900) { $cut = $t.LastIndexOf("`n", 1900); if ($cut -lt 500) { $cut = 1900 }; $chunks += $t.Substring(0, $cut); $t = $t.Substring($cut) }
  $chunks += $t
  foreach ($ch in $chunks) {
    $body = @{ content = $ch; allowed_mentions = @{ parse = @() } }
    if ($replyTo) { $body.message_reference = @{ message_id = $replyTo; fail_if_not_exists = $false }; $replyTo = $null }
    Api POST "/channels/$script:chan/messages" $body | Out-Null
    Start-Sleep -Milliseconds 400
  }
}
function Tail($path, $n) { if (Test-Path $path) { (Get-Content $path -Tail $n) -join "`n" } else { "(none yet)" } }
function Uptime {
  $fmt = { param($ts) if ($ts.TotalDays -ge 1) { "{0}d {1}h {2}m" -f [int][Math]::Floor($ts.TotalDays), $ts.Hours, $ts.Minutes } else { "{0}h {1}m" -f [int][Math]::Floor($ts.TotalHours), $ts.Minutes } }
  $p = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | ? { $_.CommandLine -match "-File\s+C:\\cbot\\run\.ps1" } | Select -First 1
  $since = if ($p) { & $fmt ((Get-Date) - $p.CreationDate) } else { "not running" }
  $start = [datetime]::Parse((Get-Content C:\cbot\project_start.txt).Trim())
  "**Uptime** - since last update/restart: **$since** | total since the project started (" + $start.ToString("MMM d, h:mm tt") + "): **" + (& $fmt ((Get-Date) - $start)) + "**"
}
function StatusText {
  $scene = Select-String C:\cbot\bot.log -Pattern '^.{9}scene=3' | Select -Last 1
  $lvl = if ($scene -and $scene.Line -match 'wave=(\d+)/(\d+) lvl=(\d+)') { "level id $($matches[3]), wave $($matches[1])/$($matches[2])" } else { "not in a level right now" }
  $mode = if (Test-Path C:\cbot\mode.txt) { (Get-Content C:\cbot\mode.txt).Trim() } else { "learn" }
  $speed = if (Test-Path C:\cbot\speed.txt) { (Get-Content C:\cbot\speed.txt).Trim() } else { "1" }
  $running = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | ? { $_.CommandLine -match 'run\.ps1' }).Count -gt 0
  (Uptime) + "`n" + "**PvZ bot status**`nmode: **$mode** | running: **$running** | game speed: x$speed`nnow: $lvl`nlast results:`n" + '```' + "`n" + (Tail C:\cbot\pro_record.txt 5) + "`n" + '```'
}
function Command($m) {
  $c = $m.content.Trim().ToLower()
  switch -Regex ($c) {
    '^!help'     { return "Commands: ``!status`` ``!progress`` (learning bot record) ``!pro`` (pro results) ``!events`` (recent bot events) ``!nemesis`` (zombies that kill it most) ``!uptime`` ``!speed N`` (owner: instant game speed, e.g. 0.5 = panic slow-mo) ``!help``.`nAnything else you write is passed to Claude, who replies here when the session is active (project-related requests only)." }
    '^!status'   { return StatusText }
    '^!progress' { $p = if (Test-Path C:\cbot\progress.md) { (Get-Content C:\cbot\progress.md -TotalCount 60) -join "`n" } else { "(no report yet)" }; return $p }
    '^!pro'      { return "**Pro mode results (latest 15)**`n" + '```' + "`n" + (Tail C:\cbot\pro_record.txt 15) + "`n" + '```' }
    '^!uptime'   { return Uptime }
    '^!nemesis'  { if (-not (Test-Path C:\cbot\nemesis.txt)) { return 'No deaths recorded yet.' }; $k = Get-Content C:\cbot\nemesis.txt | % { ($_ -split ' ')[-1] } | Group-Object | Sort-Object Count -Descending | Select -First 5; return '**SUPER NEMESIS** (zombie kinds that broke through when the bot died):' + [Environment]::NewLine + (($k | % { '- **' + $_.Name + '** x' + $_.Count }) -join [Environment]::NewLine) }
    '^!events'   { return '```' + "`n" + ((Get-Content C:\cbot\events.log -Tail 15) -join "`n") + "`n" + '```' }
    default      { return "Unknown command - try ``!help``." }
  }
}

# Find the channel the bot was invited to.
$me = Api GET "/users/@me"
if (-not $me) { Log "cannot reach Discord or token rejected"; exit 1 }
$script:chan = $ChannelId
while (-not $script:chan) {
  foreach ($g in @(Api GET "/users/@me/guilds")) {
    if (-not $g) { continue }
    $c = @(Api GET "/guilds/$($g.id)/channels") | ? { $_.name -eq $ChannelName -and $_.type -eq 0 } | Select -First 1
    if ($c) { $script:chan = $c.id; Log "using #$ChannelName ($($c.id)) in $($g.name)"; break }
  }
  if (-not $script:chan) { Log "waiting: bot not in a server with a #$ChannelName channel yet"; Start-Sleep 30 }
}
$stateFile = "$dir\last_id.txt"
$last = if (Test-Path $stateFile) { (Get-Content $stateFile).Trim() } else { $null }
if (-not $last) {
  $recent = @(Api GET "/channels/$script:chan/messages?limit=1")
  $last = if ($recent.Count -gt 0 -and $recent[0]) { $recent[0].id } else { "0" }
  Say "Hi! I relay messages to Claude, who runs the PvZ bot on this machine. Type ``!help`` for instant commands; anything else goes to Claude."
}
New-Item -ItemType Directory -Force "$dir\outbox" | Out-Null
$script:typingUntil = Get-Date; $script:lastTyping = [datetime]::MinValue; $script:queued = 0
$script:waiting = @{}
# Filler reactions (URL-encoded): hourglass, thinking, brain, gear, magnifier, test tube, seedling, zombie
$script:filler = @("%E2%8F%B3","%F0%9F%A4%94","%F0%9F%A7%A0","%E2%9A%99%EF%B8%8F","%F0%9F%94%8E","%F0%9F%A7%AA","%F0%9F%8C%B1","%F0%9F%A7%9F")
Log "relay running as $($me.username)"
while (-not (Test-Path "$dir\stop.flag")) {
  try {
    # Incoming
    $msgs = @(Api GET "/channels/$script:chan/messages?after=$last&limit=50") | ? { $_ } | Sort-Object { [decimal]$_.id }
    foreach ($m in $msgs) {
      $last = $m.id; Set-Content $stateFile $last
      if ($m.author.bot) { continue }
      # Attachments (owner-approved): images and text files only, max 10 MB, each message in its own folder; never executed.
      $saved = @()
      foreach ($a in @($m.attachments)) {
        if (-not $a) { continue }
        $ext = [IO.Path]::GetExtension([string]$a.filename).ToLower()
        if (@(".png",".jpg",".jpeg",".gif",".webp",".txt",".log") -notcontains $ext -or [long]$a.size -gt 10MB) { Log "skipped attachment $($a.filename) ($ext, $($a.size) bytes)"; continue }
        $adir = "$dir\attachments\$($m.id)"; New-Item -ItemType Directory -Force $adir | Out-Null
        $safe = ([IO.Path]::GetFileNameWithoutExtension([string]$a.filename) -replace "[^A-Za-z0-9_-]", "_") + $ext
        try { Invoke-WebRequest -Uri $a.url -OutFile "$adir\$safe" -UseBasicParsing -TimeoutSec 30; $saved += "$adir\$safe"; Log "saved attachment $adir\$safe" } catch { Log "attachment download failed: $($_.Exception.Message)" }
      }
      if ([string]::IsNullOrWhiteSpace($m.content) -and $saved.Count -eq 0) { Log "empty content from $($m.author.username) - is Message Content Intent enabled?"; continue }
      # Owner-only instant control (no Claude round trip): !speed N sets the game speed within ~3 s, e.g. "!speed 0.5" as a panic slow-mo.
      $ownerNow = (Test-Path "$dir\owner_id.txt") -and ((Get-Content "$dir\owner_id.txt").Trim() -eq $m.author.id)
      if ($m.content.Trim() -match '^!speed\s+([0-9]*\.?[0-9]+)$') {
        if (-not $ownerNow) { Say "Only the owner can change the speed." $m.id; continue }
        $sp = [Math]::Max(0.25, [Math]::Min(10, [double]$matches[1]))
        Set-Content C:\cbot\speed.txt ($sp.ToString([Globalization.CultureInfo]::InvariantCulture)) -Encoding ascii
        Say ("Game speed set to x" + $sp + " (vase levels still cap at x2).") $m.id; Log "owner set speed $sp"; continue
      }
      if ($m.content.Trim().StartsWith("!")) { Say (Command $m) $m.id; continue }
      # The owner (uhohman) speaks with the user's authority; the account id is locked on first sight so a lookalike name cannot claim it.
      $ownerFile = "$dir\owner_id.txt"
      if ($m.author.username -eq $OwnerName -and -not (Test-Path $ownerFile)) { Set-Content $ownerFile $m.author.id; Log "owner $OwnerName locked to id $($m.author.id)" }
      $isOwner = (Test-Path $ownerFile) -and ((Get-Content $ownerFile).Trim() -eq $m.author.id)
      $entry = [ordered]@{ id = $m.id; author = $m.author.username; author_id = $m.author.id; role = $(if ($isOwner) { "OWNER" } else { "guest-idea" }); time = $m.timestamp; content = $m.content; attachments = $saved }
      Add-Content "$dir\inbox.jsonl" ($entry | ConvertTo-Json -Compress)
      Api PUT "/channels/$script:chan/messages/$($m.id)/reactions/%E2%8F%B3/@me" | Out-Null   # hourglass = queued (eyes come only once Claude has actually read it)
      $script:waiting[$m.id] = @{ idx = 0; since = Get-Date; first = Get-Date }   # cycles filler reactions until seen
      Log "queued message $($m.id) from $($m.author.username)"

    }
    # Outgoing (Claude's replies): outbox\<anything>.txt; first line "reply:<message id>" optional.
    foreach ($f in Get-ChildItem "$dir\outbox" -Filter *.txt | Sort-Object LastWriteTime) {
      $lines = Get-Content $f.FullName -Encoding UTF8
      $reply = $null
      if ($lines.Count -gt 0 -and $lines[0] -match '^reply:(\d+)$') { $reply = $matches[1]; $lines = $lines | Select -Skip 1 }
      Say (($lines) -join "`n") $reply
      Remove-Item $f.FullName
      Log "posted $($f.Name)"
      if (@(Get-ChildItem "$dir\outbox" -Filter *.txt).Count -eq 0) { Remove-Item "$dir\typing.flag" -ErrorAction SilentlyContinue }   # all replies sent: stop typing
    }
    # Seen: Claude's waiter writes the ids it actually handed over -> swap the hourglass for eyes.
    if (Test-Path "$dir\seen.txt") {
      foreach ($sid in @(Get-Content "$dir\seen.txt") | ? { $_ -match '^\d+$' } | Select -Unique) {
        if ($script:waiting.ContainsKey($sid)) { Api DELETE "/channels/$script:chan/messages/$sid/reactions/$($script:filler[$script:waiting[$sid].idx])/@me" | Out-Null; $script:waiting.Remove($sid) }
        Api DELETE "/channels/$script:chan/messages/$sid/reactions/%E2%8F%B3/@me" | Out-Null
        Api PUT "/channels/$script:chan/messages/$sid/reactions/%F0%9F%91%80/@me" | Out-Null
      }
      Remove-Item "$dir\seen.txt" -ErrorAction SilentlyContinue
    }
    # While queued and not yet seen: rotate a filler reaction every 10 s so the wait looks alive.
    foreach ($wid in @($script:waiting.Keys)) {
      $wst = $script:waiting[$wid]
      if (((Get-Date) - $wst.since).TotalSeconds -lt 10) { continue }
      $old = $script:filler[$wst.idx]; $wst.idx = ($wst.idx + 1) % $script:filler.Count; $wst.since = Get-Date
      Api DELETE "/channels/$script:chan/messages/$wid/reactions/$old/@me" | Out-Null
      Api PUT "/channels/$script:chan/messages/$wid/reactions/$($script:filler[$wst.idx])/@me" | Out-Null
      # Waited over a minute unread: one short text saying what Claude is busy with (busy.txt), real answer follows.
      if (-not $wst.told -and ((Get-Date) - [datetime]$wst.first).TotalSeconds -gt 60) {
        $wst.told = $true
        $busy = (Get-Content "$dir\busy.txt" -Raw -ErrorAction SilentlyContinue); if (-not $busy) { $busy = "working on the bots" }
        Say ("Got your message - I'm mid-task right now (" + $busy.Trim() + "). A real reply is coming as soon as I'm free.") $wid
      }
      if (((Get-Date) - [datetime]$wst.first).TotalMinutes -gt 60) { $script:waiting.Remove($wid) }
    }
    # Typing only while Claude is actually writing a reply (it raises typing.flag right before writing; cleared once posted, max 2 min).
    if ((Test-Path "$dir\typing.flag") -and ((Get-Date) - (Get-Item "$dir\typing.flag").LastWriteTime).TotalMinutes -gt 2) { Remove-Item "$dir\typing.flag" -ErrorAction SilentlyContinue }
    if ((Test-Path "$dir\typing.flag") -and ((Get-Date) - $script:lastTyping).TotalSeconds -ge 8) { Api POST "/channels/$script:chan/typing" @{} | Out-Null; $script:lastTyping = Get-Date }
  } catch { Log "loop error: $($_.Exception.Message)" }
  Start-Sleep -Seconds 3
}
Log "relay stopped"
