# cbot v2: architecture and logs

The bot is one C# program (`build.ps1` compiles every `C:\cbot\*.cs` source into `pvz.dll`; `run.ps1` runs `[Bot]::Run`). `bot.cs` holds the main loop. `pro.cs` is the expert "Pro" brain, which does not learn, and `learn.cs`/`strategy.cs` are the learning brain. The v2 modules below sit next to them. They only measure, plan and record, and every one of them runs on the main loop thread.

## v2 source files

| File | What it does | Writes |
|---|---|---|
| `replay.cs` (`Replay`) | Ring of the last ~100 **game** seconds: a snapshot every 0.4 s, plus notes on what the bot did. It is dumped on death, win and hot reload. | `replays\<stamp>_lvl<L>_<RESULT>.txt` (newest 300 kept) and one line per dump in `replays\index.txt` |
| `outcome.cs` (`Outcomes`) | Judges each one-shot plant when it vanishes (SQUASH, CHERRY, JALAPENO, MINE, HYPNO) and each CHOMPER bite. It uses only what is on screen. | `outcomes.txt`, flushed every 2 s |
| `vaseplan.cs` (`VasePlanner`) | Vasebreaker planner: picks the vase to break and when, decides the squash reserve, ambush cell and hypno target, and keeps a notebook of what each round held. | `vase_contents.txt` (`S` line at round start, `V` line at round end) |
| `seen.cs` (`Seen`) | Pro's only view of HP: what a player can read off the screen (health bars, or the zombie's kind times its damage stage). | nothing |
| `seen.cs` (`SimCal`) | Logs what the sim predicted for each lane next to what actually happened. It is a report only, and Pro never reads it. | `simcal.log` (rotated at 40 MB to `.1`), plus `simcal_report.txt` rebuilt every 5 min |
| `v2\check.ps1` | Compile check with the same compiler and source list as `build.ps1`. It never touches `pvz.dll`. | prints `BUILD OK` / errors |

Older logs that are still written: `bot.log` (every action plus a `scene=... wave=a/b lvl=N` status line every few seconds), `events.log` (`RESULT lvl N DIED|WON`, PLANT, DAVE, WATCHDOG...), `pro_record.txt` (one line per Pro result), and `nemesis.txt` (the zombie type that got through on each death).

## Line formats

- **replays\index.txt**: `yyyy-MM-dd HH:mm:ss lvl=35 result=DIED round=2 killer=FOOT dur=85.2s lines=250 file=<name> <extra>`
  - On a death, `extra` is `round R vases left N killer=T` on vase levels and `wave a/b killer=T` on other levels. On a win it is `vr=R`.
  - `result` can also be `RELOAD`.
- **replay dump**: `#` header lines, then oldest-first lines with times in game seconds. Cells are `row*10+col`.
  - `S t= sun= w=a/b vr= mow=<per row: - none,0 rolling in,1 ready,2 fired,3 squished> dz=<lane danger> | V: <cell><g leaf|s skull|? plain> | Z: id,TYPE,row,x,hp[,E eating][,C charmed] | F: (fog, record only) | P: SEED,row,col,hp | K: dropped packets SEED,x,y,age/disappear | B: ready slots`
  - `N t= <KIND> <text>`, where KIND is PLANT, VASE, OUTCOME, MOWER, DAVE, ROUND, VASES or LEVEL.
- **outcomes.txt**: `yyyy-MM-dd HH:mm:ss lvlN OUTCOME <PLANT> <RESULT> target=<TYPE|-> row r col c round R t=<game s> [hp=|kills=|dmg=] age=`
  - Results are HIT_KILL, HIT_DMG, MISS, EATEN and WIPED (the vase round was cleared). HYPNO also has CHARMED, and CHOMPER has BITE and LOST.
  - Misses have `target=-`.
- **vase_contents.txt**:
  - `S <time> lvl=35 r=2 n=20 gt=123.4`
  - `V <time> lvl=35 r=2 n=<vases at start> br=<broken> res=clear|died|won|left|skip rl=<1 = resumed after reload> z=NORM:4,FOOT:2 s=SNOW:5,... lk=q:Z/P/N,l:Z/P/N,k:Z/P/N`
  - In `lk`, each look (q plain, l leaf, k skull) maps to how many of those vases held a zombie, a plant or nothing.
- **simcal.log**: `S` segment start, `P` lane prediction, `Z` predicted zombie x at +5 s, `X` the real x, `A` an actual event. The header of `SimCal` in `seen.cs` documents the format. Read `simcal_report.txt`, not the raw log.
- **pro_record.txt**: `yyyy-MM-dd HH:mm:ss PRO lvl N DIED wave a/b`, `... DIED round R, vases left N` (vase levels, newer builds), or `... WON`.
- **nemesis.txt**: `yyyy-MM-dd HH:mm pro|learn lvlN TYPE`

## How to read them

- **Start with `powershell -NoProfile -File C:\cbot\v2\analyze.ps1`.** It reads only, and any input may be missing. The report shows deaths by level and vase round, top killers, plant hit rates by round and target, average vase-round contents, and the last 5 deaths with their final 10 game seconds from the replay dumps.
  - `-Json` gives the same data as JSON. `-Recent N` and `-Window S` change the death section.
  - `-Root <dir>` runs it against a copy of the files.
- **Open a raw replay only for one specific death.** The last `S` line is the moment of the breach. Read the `N` lines above it to see what the bot tried.
- **`C:\cbot\watch.ps1` is the live watchdog** (about 5 lines of output).
  - It relaunches a dead bot and runs `restart.ps1` when `bot.log` has been silent for more than 2 min while `run.ps1` is up.
  - It keeps the Discord relay, presence, gifcam and ideabot running. Ideabot is relaunched only if `discord\ideabot_read.txt` exists.
  - It prints `STUCK?` when level, wave, vase round, vases left and the RESULT count have all stayed the same for 6 min.
  - It honours `hold.flag` and `discord\stop.flag`.
  - Its state is in `watch_state.txt`.
