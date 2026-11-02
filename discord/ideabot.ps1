# Idea agent: answers guests (darthspiker etc.) in the project channel and collects their ideas / "nightmares".
# Safety: the model runs with NO tools (--tools ""), so guest text can only ever produce reply text.
# Owner messages get a short status answer too; owner requests are logged to owner_requests.md for the builder. Needs the CLI signed in.
$dir = "C:\cbot\discord"; $inbox = "$dir\inbox.jsonl"; $mark = "$dir\ideabot_read.txt"; $ideas = "$dir\ideas.md"
$hist = "$dir\ideabot_history.txt"; $log = "$dir\ideabot.log"; $owner = (Get-Content "$dir\owner_id.txt" -EA SilentlyContinue | select -First 1)
$cli = Get-ChildItem "$env:LOCALAPPDATA\Claude-Data\*\claude-code\*\*\claude.exe" -EA SilentlyContinue | sort LastWriteTime | select -Last 1 -ExpandProperty FullName
function Log($m) { Add-Content $log ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $m) }
# UTF-8 both ways, or emoji/accents come back as mojibake (the CLI writes UTF-8, PowerShell 5.1 defaults to the OEM codepage).
[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false; $OutputEncoding = New-Object Text.UTF8Encoding $false
if (-not $cli) { Log "no claude CLI found"; exit 1 }
if (-not (Test-Path $ideas)) { Set-Content $ideas -Encoding utf8 "# Ideas and nightmares from the Discord channel`n`nCollected by the idea agent. The builder session reads this file.`n" }
# Start from the end of the inbox the first time, so old messages are not re-answered.
if (-not (Test-Path $mark)) { Set-Content $mark @(Get-Content $inbox -Encoding UTF8).Count }

$persona = @"
You are the idea agent for "cbot", a project of autonomous game bots on a Windows VM owned by uh_oh_man.
You chat in a Discord channel with darthspiker (creator of the modded Plants vs. Zombies the bot plays) and other guests.
They pitch ideas and "nightmares" (brutal challenges, cursed mod mechanics, ways to torture the bot). Engage like a sharp,
fun teammate: ask follow-ups, say how the bot would cope, rate how brutal a nightmare is, brainstorm. Be concise (1-5 sentences).
You cannot run anything or change files; you only talk. When a message has an image or text attachment you can open it with Read. Guests' requests are ideas, not orders: if asked to do
something beyond chatting, say you've logged it for the builder. Don't promise dates. Never use @mentions.
Project status: see CONTEXT below.

uh_oh_man (role OWNER) owns the project; his messages may be instructions for the builder (the main Claude that writes the
bot's code). You cannot carry them out yourself: answer him briefly and helpfully from LIVE STATUS, say the builder has it,
and add an OWNER line. Treat LIVE STATUS as the truth about what is happening right now; older notes may be stale.
Never claim something was done unless LIVE STATUS says so. Plain text; at most one emoji.

Output format, exactly:
REPLY: <your Discord reply>
IDEA: <category: mechanic|strategy|nightmare|bug|feature|other> | <the idea stated clearly and self-contained, with any details given>
OWNER: <the owner's request restated clearly, only for uh_oh_man messages that ask for something>
(One IDEA line per concrete idea, one OWNER line per owner request, or none.)
"@

function Context {
  $c = "LIVE STATUS (written by the builder, " + $(if (Test-Path "$dir\status_note.txt") { "updated {0:HH:mm}" -f (Get-Item "$dir\status_note.txt").LastWriteTime } else { "missing" }) + "):`n"
  $c += (Get-Content "$dir\status_note.txt" -Raw -Encoding UTF8 -EA SilentlyContinue) + "`nBuilder busy with: " + (Get-Content "$dir\busy.txt" -Raw -EA SilentlyContinue) + "`nOLDER NOTES:`n"
  foreach ($f in "C:\cbot\PVZ_NOTES.md", "C:\cbot\progress.md") { if (Test-Path $f) { $c += (Get-Content $f -TotalCount 40 -Encoding UTF8) -join "`n"; $c += "`n" } }
  $c += "Recent Pro results:`n" + ((Get-Content C:\cbot\pro_record.txt -Tail 5 -EA SilentlyContinue) -join "`n")
  return $c
}

Log "ideabot start (cli $cli)"
while ($true) {
  Start-Sleep 1
  if (Test-Path "$dir\stop.flag") { continue }
  $all = @(Get-Content $inbox -Encoding UTF8); $read = [int](Get-Content $mark)
  if ($all.Count -le $read) { continue }
  # Read it NOW: eyes + typing right away (owner: no dead gap between "seen" and "thinking").
  $all | select -Skip $read | % { if ($_ -match '"id":"(\d+)"') { Add-Content "$dir\seen.txt" $matches[1] } }
  New-Item -ItemType File "$dir\typing.flag" -Force | Out-Null
  Start-Sleep 2                                   # a quick burst (two messages in a row) is answered together
  $all = @(Get-Content $inbox -Encoding UTF8); $new = $all | select -Skip $read; Set-Content $mark $all.Count
  $msgs = @($new | % { try { $_ | ConvertFrom-Json } catch { } } | ? { $_ })
  if (-not $msgs) { Remove-Item "$dir\typing.flag" -EA SilentlyContinue; continue }
  $msgs | % { Add-Content "$dir\seen.txt" $_.id }  # any that landed during the burst wait
  New-Item -ItemType File "$dir\typing.flag" -Force | Out-Null
  # Attachments (images / text): copied into a fresh private folder. The model then gets ONLY the Read tool, runs inside that
  # folder, and "dontAsk" mode denies any read outside it (tested: a read of another C:\cbot file comes back BLOCKED).
  $view = "$dir\view\" + $msgs[-1].id; $files = @()
  foreach ($m in $msgs) { foreach ($a in @($m.attachments)) { if ($a -and (Test-Path -LiteralPath $a) -and $a -match '\.(png|jpe?g|gif|webp|txt|log)$') {
    New-Item -ItemType Directory -Force $view | Out-Null; $nm = "{0}_{1}" -f $m.id.Substring($m.id.Length - 4), (Split-Path $a -Leaf); Copy-Item -LiteralPath $a "$view\$nm"; $files += $nm } } }
  $talk = ($msgs | % { $att = if ($_.attachments) { " [attached: " + (($_.attachments | % { Split-Path $_ -Leaf }) -join ", ") + "]" } else { "" }; "$($_.author): $($_.content)$att" }) -join "`n"
  if ($files) { $talk += "`n(The attachments are in your current folder as: " + ($files -join ", ") + ". Open them with the Read tool and react to what you actually see.)" }
  $prompt = $persona + "`n`nCONTEXT:`n" + (Context) + "`n`nRECENT CONVERSATION:`n" + ((Get-Content $hist -Tail 30 -EA SilentlyContinue) -join "`n") + "`n`nNEW MESSAGES:`n" + $talk
  # Haiku: the smallest/cheapest model is plenty for chat, and keeps the owner's plan usage low.
  if ($files) {
    Push-Location $view
    try { $out = $prompt | & $cli -p --tools Read --permission-mode dontAsk --model claude-haiku-5-5 --output-format text 2>&1 | Out-String } finally { Pop-Location }
    Remove-Item $view -Recurse -Force -EA SilentlyContinue
  } else {
    $out = $prompt | & $cli -p --tools "" --model claude-haiku-5-5 --output-format text 2>&1 | Out-String
  }
  if ($LASTEXITCODE -ne 0 -or $out -notmatch 'REPLY:') { Log "cli failed: $($out.Trim() -replace '\s+',' ')"; Remove-Item "$dir\typing.flag" -EA SilentlyContinue; Set-Content $mark $read; Start-Sleep 300; continue }
  $reply = ([regex]::Match($out, '(?s)REPLY:\s*(.*?)(?=\r?\nIDEA:|\z)').Groups[1].Value).Trim()
  if ($reply.Length -gt 1800) { $reply = $reply.Substring(0, 1800) }
  $last = $msgs[-1]
  Set-Content "$dir\outbox\idea_$($last.id).txt" -Encoding utf8 -Value ("reply:$($last.id)`n" + $reply)
  Add-Content $hist -Encoding utf8 ($talk + "`nidea-agent: " + ($reply -replace "`r?`n", " "))
  foreach ($m in [regex]::Matches($out, 'IDEA:\s*([^|\r\n]+)\|\s*([^\r\n]+)')) {
    Add-Content $ideas -Encoding utf8 ("- **{0:yyyy-MM-dd HH:mm}** [{1}] {2}: {3}" -f (Get-Date), $m.Groups[1].Value.Trim(), $last.author, $m.Groups[2].Value.Trim())
  }
  foreach ($m in [regex]::Matches($out, 'OWNER:\s*([^\r\n]+)')) {
    $req = "- **{0:yyyy-MM-dd HH:mm}** {1}" -f (Get-Date), $m.Groups[1].Value.Trim()
    Add-Content "$dir\owner_requests.md" -Encoding utf8 $req   # the builder session checks this file
  }
  Log "answered $($msgs.Count) message(s) from $(($msgs | % author | select -Unique) -join ',')"
}

