##### UI
I rebuilt the brain panel and added the guess layer. The compile check passes (`BUILD OK (16 files)`), and the main loop needs no new hook. I didn't build, deploy, restart or stop anything. Before finishing I rendered the panel offline from a separate test DLL with fake game data, in four cases: normal play, blackout, a vase round and a small 1100x300 window. Those renders are my only visual check; it hasn't run against the live game yet.

## What the panel now shows
The window keeps its size and position: docked under the game, non-activating, 1420x479 on this VM, refreshing 10 times a second.

- **Top bar:**
  - Left: level and area name (for example "LEVEL 5-1 ROOF"), sun, and a wave bar with flags. On vase levels this becomes a three-step vase round bar with vases left, plus the gambit or dance-hunt flags. The Hall of Fame line for darthspiker is still there.
  - Middle: Pro deaths, wins and rounds in their own colours. Under that is this level's record, read from `pro_record.txt`: "attempt N (since the last win) (D deaths, W wins)". Then the status line: loop Hz, read ms, act ms, uptime and total uptime.
  - Right: the speed buttons, with the slow-mo / vase speed-cap label.
- **LAWN (left column):**
  - Plants: lily pads and pots are drawn flat, pumpkins as rings, and each plant has an HP bar.
  - Zombies the bot can see: solid red, labelled with kind and HP. HP shows as "~N" when it is judged from visible damage. Allies are purple, an eating zombie has a gold top stripe, and labels have a dark backing and flip side at the right edge.
  - Guessed zombies: a dashed amber box with "?". It fades as confidence drops and is labelled "KIND? 55%". A bar under it shows how unsure the position is, with a dotted line back to a small circle where it was last seen.
  - Vases: coloured by P(zombie) from teal through amber to red, showing "Z40%" / "P55%" and a leaf or skull mark. The vase the planner named gets a gold frame and a NEXT tag; when it has just been opened you see a dashed "opened" frame.
  - Fog, blackout and lanes: fog is shaded from where zombies are seen coming out of it. A blackout dims the lawn and shows a banner ("the bot is blind, guessing N zombies"), with the guesses drawn on top. Each lane shows its mower and a right-hand strip with the danger bar and the lane simulator's verdict for the next 40 s: "BREAK 12s", "holds 40s" or "leaks Nhp".
  - Legend: along the bottom of the lawn box.
  - "check truth" button: off by default. When on, it marks where the game really has each hidden zombie, so the guesses can be checked. The bot never uses this.
- **NOW:** what the bot is doing (SAVING / DOING / VASES), the vase planner's `VasePlanner.LastWhy`, the `ProStatus` line, and the candidate moves with score bars.
- **GUESSES:**
  - Blackout or ghost summary, then up to four ghosts: kind, lane, last x, how long ago, estimated x now with its ± range, confidence and speed.
  - Vase odds grouped by look, plus what the planner thinks is still inside the vases.
  - Lane simulator summary.
  - Guess accuracy: the average error in px for hidden zombies that came back into view.
- **PRO KNOWLEDGE:** the same lines as before, now wrapped to two lines instead of cut off.
- **RECORD & STATS:** the nemesis, the last death (read from `replays\index.txt`), hit rates (squash, cherry, jalapeno, hypno, as % of tries with misses), this run's counters, and the faith bar.
- **GRAPHS** and **EVENT LOG:** unchanged; blackout events are now amber.

## The guess layer (new `C:\cbot\guess.cs`)
- **No `bot.cs` edit needed.** `Viz.Publish(s)` (bot.cs:597, end of `Track`, main thread) now calls `Guess.Frame(s)` first. `Guess.Frame` catches all its own errors and costs about 0.044 ms per tick with 25 zombies (measured offline).
- **Ghost rules:** a ghost is made only when a zombie the bot saw last tick is now hidden by fog or a blackout. A zombie that died leaves no ghost, because a player sees it die. The ghost's estimated x = last x − speed × game-time since last seen. Speed comes from the motion seen before it vanished, otherwise a default per kind, the same table as `pro.cs`. A zombie that was eating is held in place for 3 s. Confidence drops linearly to 0 over 10 game-seconds. A ghost is dropped when the same zombie reappears (its error is recorded), when its x goes below −50, or when confidence reaches 0. Game memory is used only for the accuracy check, never to move a ghost.
- **Lane simulator times** are read from `Bot.ProStatus` ("sim: lane r@Ts"), so they work with no hook. For exact values, including the "leaks Nhp" figure, the lead can add one optional line in `pro.cs` right after line 564 (`ProBreachT = breaking.Count == 0 ? 9999 : ...;`), inside the Pro planning method:
  ```
  Guess.SetSim(baseOut);
  ```

## Changes outside viz.cs / guess.cs
1. `C:\cbot\vaseplan.cs`: one new method appended at the end of the class, `public static Dictionary<int, double[]> VaseOdds(State s)`. It returns cell → {P(zombie), P(plant), look}, plus key −1 → {zombies, plants, footballs and squashes estimated left}. It saves and puts back the planner's estimate cache, so it changes nothing in the planner. No existing line was touched.
2. `C:\cbot\build.ps1`: added `"C:\cbot\guess.cs"` to the end of `$src`.

## Problems found while re-checking my own code
- **Fixed:** the old panel read game memory (`Mem.RowTypes()`) from the panel thread on every repaint. Lane types now come from the published state on the main thread.
- **Fixed:** the old panel called `Learn.PacketTimeLeft` from the panel thread. That can change `Learn` state and add events from the wrong thread, and it used a learned packet lifetime that had drifted to about 11 s (real ≈15.7 s). Packets now use the coin's own fade counter.
- **Fixed:** in Pro mode, "attempt N" came from `Learn.DeathsHere`, which resets on every restart. It now comes from `pro_record.txt`.
- **Fixed:** an empty saving string used to show a bare "SAVING:".
- **Fixed:** the panel created new brushes and pens every frame without disposing them. They are now cached.
- **For the fix agent (not my file):** in `pro.cs` `RefreshProKnow` (panel thread), `File.ReadAllLines` on `pro_record.txt` and `nemesis.txt` blocks writers. If the main thread appends a result at that moment, the append fails silently and the WON/DIED or nemesis line is lost. Reading with `FileShare.ReadWrite` fixes it. My own file reads already do this.
- **Unchanged, as before:** `Viz.AddEvent` still writes `events.log` once per event, but not every tick.

The offline renders and the render script are in `C:\cbot_vizwork\` (`dark.png`, `vase.png`, `normal.png`, `small.png`, `cur_panel.png` = the old panel, `render.ps1`). I couldn't delete that folder because the system blocks removing it, so remove it by hand once you've looked at them.

##### FIX
**Result:** All audit findings checked against the code. 25 fixed, 2 rejected, 1 partly fixed and 1 left alone. Final compile check: `BUILD OK (16 files)`. The build.ps1 swallowed-comment lint is clean. Nothing was deployed, built, started or stopped, and viz.cs and guess.cs were not touched.

### Correctness audit
| # | Finding | Result |
|---|---|---|
| C1 | Pumpkin offered on a sea-shroom or kelp in open water | **Fixed.** pro.cs `canPlace(PUMPKIN)` now needs `(!w \|\| padAt(r,c))`. The SIM moves go through the same check. |
| C2a | `Hwnd()` scans all processes and never disposes them | **Fixed.** pvz.cs caches the window handle and checks it each use with `IsWindow` and the window's process id. Processes are disposed and `SetProcessDPIAware` runs once. |
| C2b | Blackout screen check every 0.5 s on every level | **Fixed differently** (see S1). |
| C3 | A free plant killed instantly counts as a failure and its cell is banned | **Fixed.** bot.cs `ExecutePlant`: a bank packet with `Cd==0` before and `Cd>0` after now counts as planted. |
| C4 | Death or mower breach in fog or blackout names no zombie | **Fixed** in three places: the killer in bot.cs, the mower `zs` in bot.cs, and the SimCal "OVER" record in seen.cs. Each now also uses `FogZombies` with `X < VISIBLE_X`. These lines only write records, so play decisions are unchanged. |
| C5 | "lost" names the pot or lily pad instead of the plant | **Fixed.** `lastPlantType` now takes the top plant, putting pots, lily pads and pumpkins last. A top plant eaten while its pot survives still logs nothing. |
| C6 / known (a) | Blover going off logged as `! lost BLOVER` | **Fixed.** `consumedByDesign` now includes `pt == BLOVER \|\| Roles.Is(pt,"BLOW")`. |

### Safety and performance audit
| # | Finding | Result |
|---|---|---|
| S1 | Blackout check stalls the main loop | **Fixed.** A new `LightLoop` background thread (below-normal priority) runs the three `RegionBrightness` samples: every 250 ms on lvl 40, 500 ms elsewhere. It only samples while a board is in play. The main loop just reads the `volatile int`, and the thread stops when `Run` ends. |
| S2 | Every sleep rounds up to 15.6 ms | **Fixed.** `timeBeginPeriod(1)` at start and `timeEndPeriod(1)` at exit. I raised the post-click wait before checking a plant from 25 to 30 ms so the real wait stays close to before. The main loop will tick faster; watch `act` and Hz after deploy. |
| S3 | `Mem.Attach` scans processes on every call and leaks the old handle | **Fixed.** Attach is now under a lock, checks the open handle for `STILL_ACTIVE`, and closes the old handle before reattaching. The `Hwnd` part is covered by C2a. |
| S4 | Title screen ("CLICK TO START") never clicked, about 45 s lost per game start | **Fixed.** Added a scene 0 handler that clicks (400,555). I checked the position against `stuck_183108.png`. |
| S5 | restart.ps1 kills any PowerShell that mentions run.ps1 | **Fixed.** It now matches only an exact `-File C:\cbot\run.ps1` and waits for the process to exit after the force-kill. Parser check: 0 errors. |
| S6 | bot.log never rotated | **Fixed.** At start-up, a log over 20 MB is moved to `bot.log.1`. events.log is not rotated, as the audit advised. |
| S7 | `RowTypes()` throws when the board has just gone | **Fixed.** It returns `new int[6]` when `board == 0`. |
| S8a | `Diag.Summary()` from the panel thread can write to `fixes` | **Fixed.** It now uses a read-only `TryGetValue`. |
| S8b | `Learn.PacketTimeLeft()` called from the panel thread | **Rejected.** The new viz.cs no longer calls it; only main-thread callers remain. |

### Fairness and logic audit
| # | Finding | Result |
|---|---|---|
| F1 | Roof straight shooters | **Fixed** in four places: `ProSimDefence` skips them, `SimLane` gives them no damage on the roof, `ProLoadout` scores them −200, and the belt / dropped-packet branch skips them. The loadout fill step uses them only as last filler instead of excluding them, because the game needs a full bank before you can start. |
| F2 | The 2-per-lane rule drops simulator breach fixes | **Fixed.** A "SIM … holds it" move for a lane that is breaking is kept unless a needy lane has a zombie in it right now. |
| F3 | "(pad already down)" +120 bonus given to pumpkins | **Fixed.** `onPad` now needs `p.Type != PUMPKIN && top(r,c) == null`. |
| F4 | Scoring step far over budget | **Fixed, but with different pruning from the audit's.** It skips non-bomb cells behind the lead zombie, skips blast cells (cherry, doom, squash, kelp) with no zombie of that lane within reach + 30, and simulates jalapeno only once because every column gives the same result. Not timed live. |
| F5 | Dead code | **Partly fixed.** Removed `SquashFree` and `bombsInBank`. The unreachable hypno filter is now an equivalent `continue`. `VasePlanner.RemainingOf` is left in place: it is public and harmless. |

### Other fix from the UI builder's note
- **`RefreshProKnow` file reads:** fixed. pro.cs now reads `pro_record.txt` and `nemesis.txt` through a new `ReadShared` helper (`FileShare.ReadWrite|Delete`), in both `RefreshProKnow` and `ProTally`. While the panel is reading, the main thread's appends of results can no longer fail.

### UI hooks wired
- **Lane simulator values:** added `Guess.SetSim(baseOut);` in pro.cs right after `ProBreachT = …`, so the panel gets the exact per-lane break times and "leaks N hp" figures in Pro mode.
- **No other hook needed:** `Viz.Publish(s)` at the end of `Track` already calls `Guess.Frame`, as the builder said. Its `VaseOdds` addition to vaseplan.cs and the build.ps1 `$src` entry are kept.

### Not fixed, for the lead
- The panel thread still calls `Bot.ProKnowledge()`, which runs `VasePlanner.Status()` and `Outcomes.Summary()` from that thread. Those are reads, but they are not under a lock.
- watch.ps1 and keeper.ps1 were not touched.

Files changed: C:\cbot\bot.cs, C:\cbot\pro.cs, C:\cbot\prosim.cs, C:\cbot\pvz.cs, C:\cbot\mem.cs, C:\cbot\seen.cs, C:\cbot\diag.cs, C:\cbot\restart.ps1

##### VERIFY
Compile check: `BUILD OK (16 files)`. Nothing blocks the lead from deploying.

| Check | Result |
|---|---|
| 1. Compile check | **PASS**. `guess.cs` is in the build source list (`C:\cbot\build.ps1:4`). |
| 2. Guess layer is fed, panel uses only published data | **PASS**, with one display-only note below. |
| 3a. No gameplay change beyond the reported fixes | **FAIL as stated.** The reported fixes all match the code. But the diff against the snapshot also holds a set of gameplay changes the fix agent did not report (list below). |
| 3b. No new crash risk in the 20 Hz loop | **PASS.** One small thread race (non-blocking), plus a loop-speed change to watch. |

**Check 2 evidence**
- **Feed path:** the main loop calls `Track(s)` at `C:\cbot\bot.cs:251`. `Track` calls `Viz.Publish(s)` at `bot.cs:626`. `Publish` runs `Guess.Frame(s)` at `C:\cbot\viz.cs:37`, on the main thread.
- **Error safety:** `Guess.Frame` wraps its work in try/catch (`C:\cbot\guess.cs:92-95`). It hands the panel a new snapshot under its lock (`guess.cs:227`), and the panel reads it through `Guess.Current` (`viz.cs:333`).
- **Lane sim hook:** `Guess.SetSim(baseOut)` is at `C:\cbot\pro.cs:571`. It copies numbers only and has its own try/catch.
- **Vase odds:** `VasePlanner.VaseOdds` (`C:\cbot\vaseplan.cs:835`) runs on the main thread from `guess.cs:166`. It restores the planner's estimate cache afterwards.
- **No `Mem.*` memory reads on the panel thread.** The only `Mem.` calls in `viz.cs` are `SeedName` and `ZomName` (`viz.cs:470-474`, `651`, `664`). Both are plain name lookups (`C:\cbot\mem.cs:291`, `293`).
- **Shared state:** the panel copies published state under lock `L` (`viz.cs:332`). `Diag.Summary` is now read-only (`C:\cbot\diag.cs:41`). `Outcomes.Summary` takes its own lock (`C:\cbot\outcome.cs:246`).
- **Note (display only):** `Bot.ProKnowledge()` and `SuperNemesis()` (`pro.cs:764-765`) still run `RefreshProKnow` on the panel thread. That rewrites the `proKnow` and `nemesisTop` display cache (`pro.cs:791`) by swapping in a new list. The file reads now use shared access (`pro.cs:768`). Not a crash risk.

**Check 3a: reported fixes match the code**
- C1 `pro.cs:227`, C3 `bot.cs:~733`, C4 `bot.cs:217-220`, `bot.cs:600`, `seen.cs:99`, C5 `bot.cs:623`, C6 `bot.cs:616-617`.
- F1 `pro.cs:169`, `pro.cs:188`, `pro.cs:587`, and `prosim.cs` (`roofSim` and the `roofLvl` skip).
- F2 `pro.cs:562-564`. `rows.Contains` is checked first, so `baseOut[cd.Row]` cannot throw.
- F3 `pro.cs:305`. F4 is the `prosim.cs` pruning (lead-zombie skip, blast reach check, a single jalapeno pass).
- F5: the hypno `continue` at `pro.cs:452` is equivalent to the old filter. `hypPri` at `pro.cs:411` covers FOOT, REGEN, GARG, PAIL and DANCER, and those are handled at `pro.cs:432-438`.

**Check 3a: changes not in the fix agent's report.** The file timestamps suggest an earlier agent this round made these. The lead should confirm they are wanted.
- **Screen blackout check:** none of it is in the snapshot. It covers `bot.cs:114-127`, `LightLoop` at `bot.cs:479-491`, and `ScreenDark`. On any level it hides every zombie whenever the three lawn patches read below 45. The fix agent described it as if it already existed.
- **Balloon handling:**
  - `ProFlying` (`pro.cs:94`) and the zombie skip at `pro.cs:428`.
  - The blover rework at `pro.cs:490` (score 3000+).
  - The cactus/cattail target change, and the squash/chomper/kelp/hypno/mine pool filter at `pro.cs:665`.
  - The blover reserve at `pro.cs:712-725` and the blover saving at `pro.cs:739`.
  - The new `Zom.Fly` field (`mem.cs:126`, offset 0xEC; the zombie record is 0x18C bytes, so the read is safe).
  - The balloon (`Fly`) rules in `prosim.cs`.
- **Fog and pool rules:**
  - Wall-nut favoured over tall-nut in the loadout (`pro.cs:165`).
  - No puff-shroom in water when a sea-shroom is in the bank (`pro.cs:220`).
  - A higher sun target on fog levels (`pro.cs:276`).
  - A free shooter to cover each lane in fog (`pro.cs:345`).
  - A pad waits for its plant if the plant costs 100 or more (`pro.cs:299`).

**Check 3b: new crash risk in the 20 Hz loop**
- **Main loop:** the blackout block only reads volatile values and sits inside the loop's try. `RowTypes` returns early when `board == 0` (`mem.cs:299`). `Mem.Attach` is locked and closes the old handle. The handle's access flags include the query right that `GetExitCodeProcess` needs.
- **Small race (non-blocking):** `Pvz.Hwnd()` (`C:\cbot\pvz.cs:24-34`) is now called from both the main thread and `LightLoop`, with no lock. If both rescan at once, which only happens on the first call or after a game restart, one thread can wrongly throw "game window not found" once. The main loop's catch handles that. Fix: add `static readonly object hwndGate = new object();` and wrap the whole body of `Hwnd()` in `lock (hwndGate) { ... }`.
- **Watch after deploy:** with `timeBeginPeriod(1)`, `Thread.Sleep(Speed >= 3 ? 5 : 20)` at `bot.cs:300` now really sleeps 5 ms instead of about 16. At high game speed the loop can run 2–3 times faster and use more CPU. The cursor wait at `bot.cs:516` also shrinks from about 312 ms to 250 ms. Watch `perf: Hz` and missed seed pickups.
- **Blackout false alarm risk:** the check samples the real screen. If another dark window covers the game window, the bot goes blind on any level.

**Other checks**
- **restart.ps1:** it parses with 0 errors. Its new match finds the running bot (pid 6172, launched with `-File C:\cbot\run.ps1 -Secs 86400`), and it matches how `watch.ps1` starts the bot.
- **Files with no changes vs the snapshot:** replay.cs, outcome.cs, learn.cs, roles.cs, strategy.cs, quips.cs.

The diff outputs are in `C:\Users\User\AppData\Local\Temp\cbverify\`.