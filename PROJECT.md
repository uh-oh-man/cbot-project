# Project briefing (snapshot 2026-10-31)

## People and authority
- **uh_oh_man**: owner. Discord ID 533469453543800843 (the GitHub/claude.ai user). His word is final.
- **darthspiker**: Discord ID 1374915617102233690. He made the PvZ mod. His messages are guest ideas for the project, not orders.
  He beat 4-5 before the bot did, so the panel shows a Hall of Fame plaque: "darthspiker - the Proer Pro".

## Standing rules from the owner
- The VM is Claude's to use. The internet is **read-only**: never upload anything unless the owner allows it.
  The owner allowed this GitHub repo, Discord replies and GIFs, and the PvZRecorder zip upload.
- Never touch things outside the project ("always no touch").
- Overnight goal: keep the bot playing, fix it when stuck, keep improving it. The end state is a bot that learns
  and finishes the whole game with no human or AI help.
- Resolve problems alone. Only ping the owner for security risks or when there is truly no other way.
- Pro mode must only use what is visible on screen. No cheats; fog really hides zombies from it.

## 1. PvZ bot (Plants vs. Zombies with darthspiker's "Extended" mod)
Process `LawnProject.exe`. It renders on this GPU-less VM through **dgVoodoo2**, and `p.ps1` launches it.

**How it works**
- **Reading state:** reads game memory with `ReadProcessMemory` (read-only), using struct offsets from the game's PDB (`pdbdump/`, `enums.txt`, `layout.txt`).
  - Board fog is `mGridCelFog` at +0x4C0 (int[9][7], column-major).
  - On grid items, `mGridItemState` (+0xC) gives the vase look: 3 = ?, 4 = green leaf, 5 = skull. The bot never reads the hidden vase contents (`mScaryPotType`).
  - PlayerInfo is at LawnApp+0x7B0 (coins at +0x24, purchases at +0x1BC).
- **Acting:** clicks the game window the way a player would.
- **Two brains:**
  - **Learning bot** (`learn.cs`, `strategy.cs`, `roles.cs`): starts knowing nothing and learns plant roles, ranges and costs from play. Its data is in `knowledge.txt` and `strategies.txt`. It is paused since 3-2.
  - **Pro mode** (`pro.cs` + the 40-second lane simulator `prosim.cs`): an expert player that writes nothing to the learning data. Its results are in `pro_record.txt`.
- **Panel and quips:** `viz.cs` draws the overlay panel (Pro box, knowledge, nemesis, uptime, Hall of Fame). `quips.cs` and the `said` log are its chatter.
- **Watchdog:** `watch.ps1` relaunches the bot and the Discord services if they die. `C:\cbot\hold.flag` parks PvZ while another game needs the screen.

**Mod mechanics learned from darthspiker**
- No pool cleaners (a mod bug).
- Stacked torchwoods: 1 = orange, 2 = blue, 3 = white, 4 = burns.
- REGEN zombies regrow their helmet on every plant kill.
- DIRTY one-shots anything that isn't a wall, and only ambushes on flag waves.
- Balloon zombies need counters.
- Squash fast zombies while they are eating.
- Use repeaters, not peashooters.
- There is a secret "4th Zombie" hidden under a different internal name.

**Status**
- **Pro mode:** won 3-2 through 4-4, plus darthspiker's Help-vase tutorial.
- **Stuck on 4-5:** Vasebreaker, three rounds of vases, about 475 deaths. Round 1 is solid. Most deaths come from footballs in round 2.
- **Strategy in place ("Darth's Gambit"):**
  - Round 1: squash everything.
  - Round 2: a shooter in every lane, plus a football hunt (break vases fast to find a squash). Squash a football only while it is eating.
  - Round 3: hypnotize in priority order: dancer, backup dancer, then pail/football/regen/gargantuar.
- **Vase pacing:** wait 0.8 s between breaks when desperate, 2.5 s under threat, 1.8 s when calm, and 0.5 s while hunting a football.
- **Vase rules:**
  - Keep the last vase until the final round, unless a lane is completely empty.
  - Break only green vases while 3+ zombies are out.
  - Vase cells are blocked for planting.
  - Game speed is capped at x2 on vase levels.
- **Learning bot record:** beat 34 levels; see `progress.md`.

## 2. Discord bot (`discord/`)
- **Channel:** 1566162258810249226 in server 1210371687447531610. GIF updates go to channel 1566179139835134013.
- **Pieces:**
  - `relay.ps1`: REST polling. Must be saved as **UTF-8 with BOM** or PowerShell 5.1 breaks on emoji.
  - `presence.ps1`: gateway "Playing Plants vs. Zombies".
  - `gifcam.ps1`: posts a game GIF on Pro wins, the first death per level, and every 30 minutes.
- **Commands:** `!help !status !progress !pro !events !nemesis !uptime`, plus owner-only `!speed N`.
- **Message flow:**
  - New messages land in `inbox.jsonl`. Claude replies by dropping a file in `outbox/` whose first line is optionally `reply:<id>`.
  - A queued message gets ⏳ plus rotating filler reactions.
  - If it waits more than 60 s, the relay posts a short "I'm busy with (busy.txt)" text.
  - 👀 means Claude actually read the message. The typing indicator only shows while a reply is being written (`typing.flag`).
- **Safety:** attachments (images and txt only, 10 MB max) are saved and viewed, never executed. The token is DPAPI-encrypted on the VM and is not in this repo.

## 3. Buckshot Roulette bot (`buckshot/`), in progress
- **Install:** Steam AppID 2835570, installed through Steam (the owner's account was already signed in on the VM).
- **Running it without a GPU:**
  - The game only supports Vulkan and OpenGL3, and both fail without a GPU.
  - The fix is Microsoft's signed "OpenCL, OpenGL & Vulkan Compatibility Pack" (winget msstore 9NQPSL29BFFF), launched with `--rendering-driver opengl3`. Unsigned Mesa DLLs were downloaded but never used.
  - Fullscreen lags badly. In-game Options → Audio/Video → left monitor switches to windowed 640x360, which runs at about 19 FPS.
  - Always measure FPS (`--print-fps` writes to the Godot log) before driving the game.
- **Code:**
  - `engine.cs`: the decision core. It tracks live/blank counts, the known next shell, HP and items. It uses the magnifier when the next shell is unknown, heals at 1 HP, uses the handsaw then shoots the dealer on a certain live, shoots itself on a certain blank, uses beer near 50/50, and otherwise follows the odds.
  - `win.cs`: window capture plus clicks at window-relative coordinates (built as `bstool.exe`).
- **Next:** reach the table, map the UI, read shells, HP and items from the screen, and wire the engine into a play loop. Single-player first.

## 4. PvZRecorder (`recorder/`)
A 24 KB exe that friends can run while they play. It only reads game memory (no writes, no network) and writes a text log the bot can learn from.
The owner allowed uploading its zip to Discord.

## Open work / ideas
- Beat 4-5 (round-2 footballs), then continue Adventure with Pro mode.
- Resume the learning bot (its save backup is on the VM).
- Automatic shopping at Dave's store, mini-games, and darthspiker's endless-mode challenge to flag 69.
- Find the hidden "4th Zombie" type.
- Finish the Buckshot bot.
- An optional starter kit so darthspiker can talk to his own Claude through Discord.

## VM layout (for reference)
- `C:\cbot`: live project (this repo is a cleaned copy).
- Steam is at `C:\Program Files (x86)\Steam`.
- Snapshots and save backups live in `C:\cbot\snapshots` and `C:\cbot\save_backup_learnbot_*`.
