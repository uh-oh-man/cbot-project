# cbot: autonomous game bots (PvZ Extended mod + Buckshot Roulette)

This repo is the reference copy of a project built by Claude (Claude Code) on a dedicated Windows 11 VM for uh_oh_man.
It exists so the owner can talk about the project in any chat and point Claude at real code and notes.
**If you are Claude reading this in a new chat, start with `PROJECT.md`.**

| Path | What it is |
|---|---|
| `PROJECT.md` | Full project briefing: goals, rules, architecture, current state, history, open work |
| `*.cs` (root) | The PvZ bot (C#, compiled to `pvz.dll`, run by `run.ps1` / `bot.ps1`) |
| `*.ps1` (root) | Build, run, watchdog, screenshot, report scripts |
| `discord/` | Discord relay bot (PowerShell REST + gateway presence + GIF poster) |
| `buckshot/` | Buckshot Roulette bot (decision engine + window I/O, in progress) |
| `recorder/` | PvZRecorder: read-only memory recorder friends can run to record their own play |
| `pdbdump/` | Tool that dumped struct offsets from the game's PDB |
| `logs/` | Recent tails of the bot event log and its chat quips |
| `progress.md`, `pro_record.txt`, `nemesis.txt`, `knowledge.txt`, `strategies.txt` | Live results and learned data |
| `PVZ_NOTES.md`, `TODO.txt` | Notes and pending ideas |

Not included on purpose: the Discord bot token (DPAPI-encrypted, VM-only), game files, the game disassembly,
third-party binaries (dgVoodoo2, Steam, Mesa), screenshots, save files, and full multi-MB logs.
