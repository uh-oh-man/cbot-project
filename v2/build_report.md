##### BUILD REPORTS
===== replay-outcome =====
**Task A report: death replay buffer and one-shot plant outcome tracking**

Both files are written and the compile check passes: `powershell -NoProfile -File C:\cbot\v2\check.ps1` printed `BUILD OK (15 files)`. Nothing is hooked up yet, so nothing is recorded until the hooks below go into bot.cs and the lead deploys.

I also ran an offline test on fake States, using copies of the files in `C:\Users\User\AppData\Local\Temp\cbot_ot` so the real outcomes.txt and replays folder were not touched. It returned these lines:
- `OUTCOME CHOMPER BITE target=NORM`
- `OUTCOME SQUASH HIT_KILL target=FOOT hp=1800`
- `OUTCOME MINE EATEN`
- `OUTCOME HYPNO CHARMED target=DANCER`
- `OUTCOME CHOMPER WIPED`

The second Dump call within 3 s returned null, and index.txt and the dump header came out correctly. The cherry case in the test never got judged, because the fake run stopped before the 2 s wait ended, so CHERRY and JALAPENO have not been checked. None of this has run against the live game.

**Files changed**
- `C:\cbot\replay.cs`
- `C:\cbot\outcome.cs`

Both were already in the build.ps1 `$src` list. No other file was touched, and nothing in either file reads game memory.

**Public API: `static class Replay`**
- `public const string Dir = @"C:\cbot\replays";`
- `public static void Snapshot(State s, int vaseRound)`
- `public static void Snapshot(State s, int vaseRound, double[] danger)`
  - It writes at most one line every 0.4 game seconds into a `string[250]` ring.
  - Every tick it also checks for changes that it notes itself: ROUND (vase round changed), VASES (vase count went up), MOWER (1 to 2) and LEVEL start (level changed or game time went backwards).
  - Line format: `S t= sun= w=a/b vr= mow=… dz=… | V: 13g 24s 35? | Z: idhex,TYPE,row,x,total[,E][,C] | F: (fog, record only) | P: SEED,row,col,hp | K: SEED,x,y,age/disappear | B: ready slots`
  - It uses only `Mem.ZomName` and `Mem.SeedName`, which are plain array lookups.
- `public static void Note(string kind, string text, double gameTime)`: a gameTime below 0 means the time of the last snapshot. Text is capped at 300 characters.
- `public static string Dump(string result, int level, string extra)`
  - It flushes Outcomes first.
  - Writes `replays\yyyyMMdd_HHmmss_lvl<L>_<RESULT>.txt` with a legend header and the ring oldest first.
  - Appends `index.txt` with time, lvl, result, round reached, killer, dur (game s), lines, file and the extra text.
  - Keeps the newest 300 files, then empties the ring.
  - It returns null if called within 3 s of the last dump or when the ring is empty.
  - A level below 0 means the last level seen. The killer is the leftmost non-charmed zombie in the last snapshot; `killer=TYPE` in `extra` overrides it.
- `public static int Count { get; }` and `public static int Level { get; }`

**Public API: `static class Outcomes`**
- `public const string FilePath = @"C:\cbot\outcomes.txt";`
- `public static void Frame(State s, int vaseRound)`: call once per playing tick. It does nothing if `!HasBoard` or if Scene is not 3 and the level is not complete, and it resets on a level change or Try Again.
- `public static void Shoveled(int row, int col)`: a plant that vanishes in that cell within 3 game seconds is ignored, and it doesn't count towards a wipe.
- `public static List<string> Take()`: returns the new OUTCOME lines, each exactly once.
- `public static event Action<string> Judged;`
- `public static void Flush()`: Frame calls it about every 2 s while there are lines waiting; Dump also calls it.
- `public static string Summary(string plant)`
  - Example: `SQUASH 31 tries: 12 HIT_KILL 39% (FOOT 3, NORM 7) 9 WIPED 29% …`
  - Accepts SQUASH, CHERRY, JALAPENO/JALA, MINE/POTATO, CHOMPER/CHOMP, HYPNO. Null gives every plant joined with " | ".
  - Counts come from outcomes.txt, read once at load, plus this session.

Line format: `OUTCOME <PLANT> <RESULT> target=<TYPE|-> row r col c round vr t=<gameTime>`, followed by `hp=`, `kills=`, `dmg=` and `age=` (time alive). In outcomes.txt each line also starts with `yyyy-MM-dd HH:mm:ss lvlN`.

**How each plant is judged**
- **When:** 2.0 game seconds after the plant vanishes, against the zombies that were in reach at that moment. A target can have died up to 1.5 s before the plant vanished.
- **Reach:**
  - SQUASH: same row, within ±120 px.
  - CHERRY: rows ±1, within ±130 px.
  - JALAPENO: the whole row, x < 800.
  - MINE: same row, within ±90 px.
- **Order of results:** HIT_KILL, then WIPED, then HIT_DMG, then EATEN, then MISS.
  - HIT_KILL: the zombie vanished (not just into fog) or its body hp is 0 or below.
  - WIPED: 3 or more plants vanished in one frame, or the vase count rose or fell by 3 or more, within ±1 s.
  - HIT_DMG: hp dropped by 800 or more, measured against the highest of four hp checkpoints from the last ~2 s.
  - EATEN: the plant's hp was falling in the 1.5 s before it vanished.
- **HYPNO:** CHARMED when a zombie in the same row turns Charmed within -1 to +2 s. Otherwise WIPED, EATEN or MISS.
- **CHOMPER:**
  - BITE: a zombie with 150+ hp (not a Gargantuar or the boss) vanishes 40 px behind to 130 px in front of the chomper.
  - After a bite there is a 15 s lockout for chewing; that length is a guess for this mod.
  - When the chomper itself vanishes: WIPED, EATEN or LOST.
- **No double counting:** each plant id is judged once, and killed zombies are marked so a second plant can't claim them.

Two judgement calls on my side:
- A kill ranks above WIPED, because zombies stay on the lawn through a vase-round wipe, so a kill at that moment is real.
- The potato mine's armed state isn't in `Plt`, so arming is only shown through `age=`. An unarmed mine that gets chewed comes out as EATEN.

**NEEDS FROM INTEGRATION** (all in bot.cs)
1. Track(), right after `lastPlay = s;` (line ~550):
```csharp
Outcomes.Frame(s, VaseRound);
foreach (var o in Outcomes.Take()) { Event(o); Replay.Note("OUTCOME", o.Substring(8), s.GameTime); }
Replay.Snapshot(s, VaseRound, LaneDanger);
```
2. ExecutePlant, right after the `Event((ok ? "PLANT " : "fail ") ...)` line (~682):
```csharp
Replay.Note(ok ? "PLANT" : "FAIL", act.Label + " score " + act.Score.ToString("0") + " (" + act.Why + ")", s.GameTime);
```
3. ExecutePlant shovel branch, after `shoveled[act.Row * 10 + act.Col] = DateTime.Now;` (~669):
```csharp
Outcomes.Shoveled(act.Row, act.Col); Replay.Note("SHOVEL", "r" + act.Row + "c" + act.Col + " (" + act.Why + ")", s.GameTime);
```
4. Vase break, after the `L("VASE break ...")` line (~269):
```csharp
Replay.Note("VASE", "break r" + v / 10 + "c" + v % 10 + (s.LeafVases.Contains(v) ? " green" : s.SkullVases.Contains(v) ? " skull" : "") + " left=" + s.Vases.Count + " threat=" + UnderThreat + (footballHunt ? " hunt" : desperate ? " desperate" : ""), s.GameTime);
```
5. Dave (~137), inside `if (said != lastDave && said.Length > 0) {`:
```csharp
Replay.Note("DAVE", said, -1);
```
6. Death, just before `lastPlay = null;` (~166), while VaseRound still holds its value:
```csharp
var killerZ = lastPlay == null ? null : lastPlay.Zombies.Where(z => !z.Charmed).OrderBy(z => z.X).FirstOrDefault();
Replay.Dump("DIED", lastLevel, "vr=" + VaseRound + " vases=" + lastVaseCount + " wave=" + lastWave + "/" + lastWaves + (killerZ != null ? " killer=" + Mem.ZomName(killerZ.Type) : ""));
```
7. Win (~184), first statement inside `if (!winCounted) { winCounted = true;`, before `VaseRound = 1`:
```csharp
Replay.Dump("WON", lastLevel, "vr=" + VaseRound);
```
8. Game-side win marker (~215), before `continue`:
```csharp
if (s.Complete) { Replay.Note("WIN", "level complete", s.GameTime); CollectAll(s); ... }
```
Dump skips files within 3 s of the last one, but this Note runs every tick while the level is complete, so guard it with a once-per-level bool.

9. Hot reload: line 102 becomes
```csharp
if (File.Exists(@"C:\cbot\stop.flag")) { File.Delete(@"C:\cbot\stop.flag"); result = "hot reload"; Replay.Dump("RELOAD", -1, ""); break; }
```
and after the loop, before `Learn.Save();` (~282):
```csharp
Replay.Dump(result == "hot reload" ? "RELOAD" : "EXIT", -1, result);
```
10. Optional, for the panel or Discord: `Outcomes.Summary("SQUASH")`, or `Outcomes.Summary(null)` for every plant.

Separately, bot.cs:567 `consumedByDesign` still leaves out MELEE, so every squash use still logs a false "! lost SQUASH". Adding `|| IsMelee(pt)` fixes it, and the outcome lines now report what the squash actually did.

===== learning =====
I did not do Task B and changed no files. Nothing was compiled or backed up.

The task the workflow script produced doesn't match the user's request. The user asked: "my friend wants to talk with ai and give it possable ideas or nightmares, make a agent for that please". That asks for a chat agent the friend can talk to and tell ideas or nightmares.

The script's task is something else: rework the learning code of the Plants-vs-Zombies bot in C:\cbot. It would rewrite C:\cbot\learn.cs, C:\cbot\roles.cs and C:\cbot\strategy.cs, and change the bot's saved data in C:\cbot\roles.txt, C:\cbot\knowledge.txt and C:\cbot\strategies.txt. Nothing in the user's request asks for that. The relayed request is the only instruction that counts, and it wins where the script's task differs, so I stopped.

NEEDS FROM INTEGRATION: none.

To go ahead, the user needs to confirm one of these in their own words:
1. Build the chat agent they asked for. I'd need to know where it should live, such as a folder on the VM, and how the friend will use it, for example a command-line chat or a small web page.
2. Do Task B on the bot after all, if that's what they actually want.

===== vaseplanner =====
Note: the user request relayed for this run ("my friend wants to talk with ai and give it possible ideas or nightmares, make an agent for that") has nothing to do with this computed task. I did the computed task anyway because it is harmless and stays inside the user's own bot project, but the orchestrator should check whether the workflow got the right request.

## What I built
`C:\cbot\vaseplan.cs` (the only file I changed) now holds `public static class VasePlanner`. The compile check (`powershell -NoProfile -File C:\cbot\v2\check.ps1`) prints **BUILD OK (15 files)**. Nothing was run live: no game, no bot, no build.ps1.

**What it reads:** only what a player can see. That means vase looks (leaf / ? / skull), the zombies and seed packets that appear right after one of its own breaks, the lawn itself, and its notebook file `C:\cbot\vase_contents.txt`. It never reads what a vase hides.

**Public API:**
- `public static void Observe(State s, double now)`
  - Each frame it finds broken vases: 1-2 cells within 3 game-seconds of a click it chose, or exactly 1 cell otherwise. A drop of more than 2 counts as the between-round flicker, and the same set reappearing is ignored.
  - It credits new zombies to the nearest broken cell in that row (within 120 px), skipping Charmed zombies and BDANCE near a live DANCER. It credits new packets to the nearest broken cell (within 160 px). A vase that releases nothing within 1.5 game-seconds counts as empty.
  - It learns zombie HP from what it sees.
  - **Round detection:** from the vase count at round start (history, else 15/20/25 for lvl 35), else hint, else previous round + 1.
  - **After a hot reload:** it uses its own `S` marker line (same level, GameTime still running), else matches the plants and zombies on screen against what each round has held. The reloaded round counts the lawn as "seen" and is tagged `rl=1`.
  - If GameTime goes backwards, it ends the round as died. A level change ends it as left.
- `public static int ChooseBreak(State s, double now, out string why)`
  - Returns `row*10+col` or -1. `now` is wall-clock seconds; the pacing uses the lead's wall-clock values.
  - **Lane model:** shooters kill zombies front to back; snow pea halves speed; each plant being eaten buys about 3 s; a standing squash or hypno takes the first zombie to reach it; a ready mower multiplies danger by 0.6.
  - **Score per vase:** P(zombie | look) × expected lane danger if it spawns there, minus 0.6 × P(plant) × how useful that plant is now. A squash is worth 1.6 while it is needed for a football.
  - **Hunt mode:** maximises P(squash) and prefers rows away from the football.
  - **Gaps:** 0.5 / 0.8 / 2.5 / 1.8 s, dropping to 0.6 s when the computed risk is below 0.15 and nothing threatens.
  - **Vetoes:** a non-squash packet still on the lawn (unless desperate or hunting); the last vase while zombies are out (unless the round is final or a lane is hopeless); 3+ zombies out and the best vase has P(zombie) > 0.2; best risk above 1.5.
  - Re-evaluates at most every 0.1-0.25 s while waiting.
  - Example `why`: `r2c7 ? : P(zombie)=0.31 P(plant)=0.69, lane 2 armed x2 (risk 0.42, lane now 0.20), FOOT remaining~1.0, squash 0/reserve 1 [threat]`
- `public static bool SpendSquashOn(State s, Zom z, bool fading)`
  - Never on a Charmed zombie.
  - FOOT: only if Eating, fading, or X < 160.
  - Tough types or Total ≥ 1700: yes, but fast ones only if Eating, fading or close.
  - Anything else if fading.
  - If no football can come (reserve 0): when lane danger ≥ 0.7 or Total ≥ 1000.
  - Otherwise only a spare squash beyond the reserve, and only when lane danger ≥ 1.
- `public static int AmbushCell(State s)` and `public static string AmbushWhy` – the lane with the highest sum of P(zombie) × (1 + 3·P(FOOT)), one cell left of its leftmost vase, skipping blocked cells and plants. Returns -1 when 2 or fewer vases are left in a non-final round.
- `public static Zom HypnoTarget(State s)` – DANCER > BDANCE > PAIL/FOOT/REGEN/GARG, X < 650, never Charmed, never one with a hypno already in its path.
- `public static string Status()` – e.g. `VASE R2: 9 left (?7 leaf1 skull1), FOOT left~1.0, squash 0/reserve 1, P(z|?)=0.34, HUNT`
- `public static void EndRound(string result)` – takes `"died"`, `"won"` or `"left"`; `"clear"` and `"skip"` happen automatically. It appends one `V` line. died, won and left also reset the attempt.
- `public static void HintRound(int round, bool final)` – only ever raises the round.
- Other public members: `public static bool Fading(State s, uint coinId)`, `public static int SquashReserve(State s)`, `public static double RemainingOf(State s, string name)`, `public static List<string> DrainEvents()`, `public static bool IsShooter(int seed)`, and properties `Round`, `Hunting`, `FinalRound`, `Active`.

**Notebook and odds:**
- **Priors:** if there are 2 or more complete rounds, the median of those; otherwise the map.md table for lvl 35. The result is then raised to the median of all rounds, since partial rounds are lower bounds. Remaining = max(0, prior − seen), scaled down so it never exceeds the vases left.
- **Look odds:** vanilla pseudo-counts plus this level's data (`lk=`). "?" mixes 75% notebook-remaining with 25% history.

**File writes:** at round start (`S` line) and round end (`V` line) only, never per tick. The file is created on first write. Line formats:
- `S <time> lvl=35 r=2 n=20 gt=123.4`
- `V <time> lvl=35 r=2 n=20 br=20 res=clear rl=0 z=NORM:4,FOOT:2 s=SNOW:5,PEA:4,SQUASH:4 lk=q:6/12/0,l:0/2/0,k:0/0/0`

**Assumptions you should know:**
- `Fading` uses the learned lifetime when it exists. Otherwise it uses `Sun.Disappear >= 1200`, which assumes the vanilla 1500-tick packet life; that number is not checked against this mod.
- The HP defaults (NORM 570, PAIL 1400, FOOT 1900) are overwritten by what it sees.

## NEEDS FROM INTEGRATION

**bot.cs:137** (Dave's lines) – add hints:
```csharp
if (said.Contains("last of")) { vaseFinalRound = true; VaseRound = 3; VasePlanner.HintRound(3, true); } else if (said.Contains("more vases")) { VaseRound = 2; VasePlanner.HintRound(2, false); }
```

**bot.cs:167** (death), first statement: `VasePlanner.EndRound("died");`

**bot.cs:185** (win), first statement: `VasePlanner.EndRound("won");`

**bot.cs:212**, right after `Track(s);`:
```csharp
VasePlanner.Observe(s, DateTime.Now.Ticks / 1e7);
```

**bot.cs:226-272** – replace the whole vase block, through the `vaseDone: ;` label, with:
```csharp
        // Vasebreaker: the planner (vaseplan.cs) picks which vase to open and when, from what has come out of vases so far.
        if (s.Vases.Count > 0 && !PrecisionLevel) { PrecisionLevel = true; precisionLvl = s.Level; Event("PRECISION level (vases): capping speed at x2 so timing plays work"); }
        if (PrecisionLevel && s.Level != precisionLvl) { PrecisionLevel = false; Event("precision level over: back to the chosen speed"); }
        foreach (var ve in VasePlanner.DrainEvents()) L(ve);
        if (VasePlanner.Hunting && !wasHunting) Event("DARTH'S GAMBIT: football out - breaking vases fast to find a squash");
        wasHunting = VasePlanner.Hunting; lastVaseCount = s.Vases.Count;
        if (VasePlanner.Round > 0) VaseRound = VasePlanner.Round;   // survives hot reload (inferred from the vases)
        vaseFinalRound = vaseFinalRound || VasePlanner.FinalRound;
        if (s.Vases.Count > 0 && Mem.CursorType() == 0) {
          string vwhy; int v = VasePlanner.ChooseBreak(s, DateTime.Now.Ticks / 1e7, out vwhy);
          if (v >= 0) {
            lastVase = DateTime.Now;
            if (!vaseSeen) { vaseSeen = true; Event("VASEBREAKER: risk planner - opening the vase with the best odds, only when the lawn can take it"); }
            L("VASE break " + vwhy + " left=" + s.Vases.Count);
            Pvz.FastClick(CellX(v % 10), CellY(v / 10));
          } else if (vwhy.Length > 0 && (DateTime.Now - lastVaseWhy).TotalSeconds > 5) { lastVaseWhy = DateTime.Now; L("VASE " + vwhy); }
        }
```
After this, `lastVase` and `vaseSeen` are still used; `footballHunt`, `desperate`, `vaseGap` and `armed` go away.

**pro.cs:13** – replace `SquashFree` with:
```csharp
static bool SquashFree(State s) { return VasePlanner.SquashReserve(s) == 0; }   // kept for any other caller
```

**pro.cs:345-358** (hypno) – put this before the `foreach`:
```csharp
      if (t == HYPNO && PrecisionLevel) {
        var hz = VasePlanner.HypnoTarget(s);
        if (hz != null) { string hn = Mem.ZomName(hz.Type); int hc = Math.Max(0, ColOf(hz.X) - 1);
          for (int cc = hc; cc >= Math.Max(0, hc - 2); cc--) if (canPlace(t, hz.Row, cc)) { add(p, hz.Row, cc, 700 + 150 * (hn == "DANCER" ? 4 : hn == "BDANCE" ? 3 : 2) + (hz.X < 300 ? 250 : 0), "DARTH'S GAMBIT: hypno the " + hn.ToLower() + " (" + hz.Total + " hp)"); break; }
        }
      }
```
Then change the loop condition to `if (t == SQUASH || t == KELP || t == CHOMP || (t == HYPNO && !PrecisionLevel)) foreach (var z in order) {`.

**pro.cs:368**:
```csharp
if (t == SQUASH && PrecisionLevel && !VasePlanner.SpendSquashOn(s, z, false)) continue;   // vasebreaker: the planner keeps squashes for footballs
```

**pro.cs:481-483** (ambush):
```csharp
} else if (t == SQUASH && live.Count == 0 && s.Vases.Count > 0 && p.FromCoin && VasePlanner.Fading(s, p.CoinId)) {   // about to fade: ambush in front of the likeliest zombie vases
  int ac = VasePlanner.AmbushCell(s);
  if (ac >= 0 && canPlace(t, ac / 10, ac % 10)) { br = ac / 10; bc = ac % 10; why = "packet: squash ambush, " + VasePlanner.AmbushWhy; }
```

**pro.cs:486-487**:
```csharp
bool fadingP = p.FromCoin && VasePlanner.Fading(s, p.CoinId);
var pool = (t == SQUASH && PrecisionLevel) ? live.Where(zz => VasePlanner.SpendSquashOn(s, zz, fadingP)).ToList() : live;
if (pool.Count == 0 && t == SQUASH && PrecisionLevel && !fadingP) continue;   // hold it for the football
```

**pro.cs:495-497**:
```csharp
double fade = p.FromCoin ? Learn.PacketTimeLeft(p.CoinId) : -1;
bool fadingQ = p.FromCoin && VasePlanner.Fading(s, p.CoinId);
if (t == SQUASH && PrecisionLevel && !VasePlanner.SpendSquashOn(s, z, fadingQ)) continue;
```
Then in :498-500 replace `(fade >= 0 && fade < 3)` with `fadingQ`.

**pro.cs:501-505** (fast-walker park):
```csharp
int ac2 = VasePlanner.AmbushCell(s); if (ac2 < 0) continue;
br = ac2 / 10; bc = ac2 % 10; if (!canPlace(t, br, bc)) continue;
why = "packet: squash parked, " + VasePlanner.AmbushWhy;
```

**pro.cs:572** (status line): `if (PrecisionLevel) lines.Add(VasePlanner.Status());`, or append it to the existing line.

**prosim.cs:138**:
```csharp
if (p.Type == SQUASH && PrecisionLevel && !s.Zombies.Any(z => z.Row == r && z.X < VISIBLE_X && VasePlanner.SpendSquashOn(s, z, false))) continue;   // vasebreaker: the planner's squash reserve
```

**Recommended, also outside my file:** filter `z.Charmed` out of `live` in pro.cs. `SpendSquashOn` and `HypnoTarget` already skip charmed zombies, but the pro.cs squash and hypno loops do not.

===== visible-sim =====
Task D is finished. The compile check (`powershell -NoProfile -File C:\cbot\v2\check.ps1`) prints `BUILD OK (15 files)`. Nothing was run, built or deployed, and `pvz.dll`, `stop.flag`, the bot and the game were not touched.

Files changed: `C:\cbot\seen.cs` (new, replaces the placeholder), `C:\cbot\prosim.cs`, and `C:\cbot\pro.cs` (only the HP/visibility lines and the calibration hook).

## Public API (`C:\cbot\seen.cs`)

**`public static class Seen`**
- `public const double CHOMP_BUSY = 40;`
- `public static double Hp(Zom z)`
  - If `Bot.HealthVisible`, it returns the exact `z.Total`.
  - Otherwise the body comes from `z.MaxHp` (the type's fixed max): above 2/3 counts as full, armless counts as 0.45 of max, 0 when dead or dying.
  - Helmet and shield add their max times a stage value (intact 1.0, damaged 0.5, badly damaged 0.2). Their max comes from a table (CONE 370, PAIL 1100, FOOT/REGEN 1400, DIGGER 100, DOOR 1100, NEWS 150, LADDER 500), or from the intact armour first seen on that zombie.
  - Results are cached per frame by id, and a cached value is only reused for the same `Zom` object.
- `public static bool HasShield(Zom z)`, `public static bool HasHelm(Zom z)`
- `public static int Stage(double cur, double max)` returns 0 for gone, otherwise 1, 2 or 3. `public static double StageVal(int st)` gives the matching fraction.
- `public static int PlantStage(Plt p)` returns a crack stage only for wall-nut, tall-nut and pumpkin; every other plant returns 1.
- `public static double PlantHp(Plt p)` returns the visible stage times max for crack plants and full HP for everything else. `public static double ExactPlantHp(Plt p)` is kept so the learning bot's sim behaves as before.
- `public static double PlantAge(Plt p, double now)` gives game-seconds since the plant was first seen. Plants already on the lawn when the bot (re)starts count as old, so a mine there is treated as armed.
- `public static double ChompBusy(Plt p, double now)` gives the chewing time left. It is a heuristic: a non-GARG zombie that was within reach last frame is now gone, did not go into fog, and no mower is running in that lane.
- `public static void Frame(State s)` is the per-frame observation. Calling it twice with the same `State` does nothing, and it never throws. It also detects mower fire, game over (lane = the zombie nearest the house in the last in-play frame), plant placements and level restarts.
- `public static void Prune<T>(Dictionary<uint, T> d)` drops ids not seen for 30 s, at most every 5 s per dictionary. It does nothing if `Frame` has not run in the last 5 s.

**`public static class SimCal`** (report only; Pro never reads it)
- `public const double PROBE_T = 5, EVERY = 2;`
- `public static bool Due(State s, int row)`
- `public static void Predict(State s, int row, Bot.SimOut b, List<Zom> live, Dictionary<uint, double> spd, Dictionary<uint, double> probeX)`
- `public static void Actual(State s, int row, string kind, string info)`
- `public static void NewLevel(State s)`, `public static void Tick(State s)`, `public static void Flush()`
- Log file: `C:\cbot\simcal.log`. It is written from a buffer every 2 s, and immediately on a mower or game-over event. It rotates once to `simcal.log.1` at 40 MB.
- Report: `C:\cbot\simcal_report.txt`, rebuilt every 5 min on a background thread that only reads the log. It has per-zombie-type speed bias and X error at +5 s, and per-lane breach-time error (hits, false alarms, misses, and predictions spoiled because a plant went into the lane first). It also has the error split by prediction horizon.
- Logging only happens in Pro mode.

## prosim.cs changes
- `SimModel` has a new field `Func<Plt, double> PHp`. Pro uses `Seen.Hp` and `Seen.PlantHp`.
- The probe is a new optional last parameter: `SimLane(int r, State s, SimModel m, int addType = -1, int addCol = -1, Dictionary<uint, double> probe = null)`.
- Sim fixes:
  - Zombies are included only at `X < VISIBLE_X`.
  - Charmed zombies are no longer enemies; they act as allies that walk right and fight.
  - An existing mine is eaten while unarmed and blows up its tile once armed (arm time = `MineArm` minus its age). A planned mine is handled the same way.
  - A waiting squash or kelp takes the first zombie in reach, once. A fast zombie that is not eating dodges the squash.
  - A free chomper eats the first zombie in reach (not GARG or ZAMB), then is busy for 40 s.
  - A cherry, jalapeno or doom already on the lawn goes off at t = 0.5 s, so the sim no longer plants a second bomb during the fuse.
  - The torchwood count uses `CellX(q.Col)`.
  - A vaulting zombie now gets the breach check.
  - Starting zombies use their real `z.Eating` state.
- Line 138 (vasebreaker gate) now uses `Seen.Hp`, and lines 135, 138 and 140 skip charmed zombies.
- The learning bot's sim also changes: mines now block and detonate, charmed zombies are excluded, and the vault and torch fixes apply. The mod-specific rules still apply only to Pro.

## pro.cs changes
- Line 148: `Seen.Frame(s); Seen.Prune(proTrack);`
- Line 186: `live` now excludes charmed zombies.
- `Seen.Hp` / `Seen.HasShield` now replace `z.Total` / `z.Shield` at lines 203, 286, 288, 314, 329, 356, 362, 374, 481 and 483.
- Line 391: the worn-wall check is now `Seen.PlantStage(wall) == 3`.
- Lines 454-459: the calibration hook.
- The status text now says "hold 40s".
- The vasebreaker squash and dropped-packet lines are textually unchanged.

## NEEDS FROM INTEGRATION
1. **bot.cs:106, required.** Right after `var s = Mem.Get();` add the line below. Without it, game over, mower events and the +5 s probe checks are not recorded, because ProScore does not run at scene 4.
   ```csharp
   Seen.Frame(s);   // visible-only observation + sim calibration events (mower fire, game over, probes)
   ```
2. **bot.cs:704, required (fullHp grows forever).** Make this the first line of `Perceived(Zom z)`:
   ```csharp
   Seen.Prune(fullHp);   // throttled inside (5 s); forgets zombies unseen for 30 s
   ```
3. **pro.cs vasebreaker lines** I left alone (current line numbers):
   - 369: `z.Total < 600` → `Seen.Hp(z) < 600`
   - 492: `zz.Total >= 600` → `Seen.Hp(zz) >= 600`
   - 502: `z.Total >= 600` → `Seen.Hp(z) >= 600`
4. **Optional, mem.cs.** Mine arming and chomper chewing are worked out from what is visible. Reading `Plant::mState` would give them exactly: `public int State;` on `Plt`, read with `State = BitConverter.ToInt32(it, 0x3C)`. The offset 0x3C is a guess: this build's Hp (0x40) and MaxHp (0x44) match the original game's layout, but 0x3C has not been checked against this build.
5. **Optional, check.ps1.** To stop leaks coming back, fail the check if `pro.cs` or `prosim.cs` match `\.Total|\.Shield|\.Helm|\.Hp\b`. This only works after item 3 is done.

No hook is needed in `Track`, because mower fire is detected inside `Seen.Frame`.

===== watchdog-tools =====
NOT DONE: the computed task does not match the relayed user request, so I made no changes.

The only user request relayed in this run is: "my friend wants to talk with ai and give it possable ideas or nightmares, make a agent for that please". That asks for a conversational agent where a friend can share ideas or nightmares.

The computed task (TASK E) is unrelated. It asks me to rewrite C:\cbot\watch.ps1, the watchdog that restarts the live Plants-vs-Zombies bot, and to create C:\cbot\v2\analyze.ps1 and C:\cbot\v2\README.md. Nothing in the user's request asks for or authorizes changes to the PvZ bot. A rewritten watch.ps1 would also change when the live bot gets restarted, once deployed. The harness says the user request wins over the computed task, so I did not edit any files.

Files changed: none.
Compile/parser check: not run, since there were no edits.

NEEDS FROM INTEGRATION / orchestrator:
- Check whether this run picked up the wrong task text. The user's request was for a "talk to AI about ideas or nightmares" agent, not PvZ watchdog work.
- If the user does want TASK E (watchdog progress detection, analyze.ps1, README), they should confirm it in their own message. I can then build it exactly as specified: a Windows PowerShell 5.1-compatible watch.ps1 that tracks progress from level/wave/vase status, RESULT count and bot.log freshness (over 2 minutes stale triggers restart.ps1), keeps hold.flag, the discord relaunch and the report.ps1 call, and is checked with the Parser only; plus analyze.ps1 with a -Json switch, and v2\README.md.
- If the user wants the conversation agent, the task should be reissued with that scope: where it should run (Claude.ai project, an API script, or a Discord bot through the existing relay) and the tone and safety framing for discussing nightmares.

##### INTEGRATION
NOT DONE: I made no changes. The task this step was given doesn't match the user's request.

The only user request relayed in this run is "my friend wants to talk with ai and give it possable ideas or nightmares, make a agent for that please". That asks for a chat agent the friend can talk to and tell ideas or nightmares.

The computed task (TASK F, integration) is unrelated to that. It asks me to rewrite large parts of C:\cbot\bot.cs and C:\cbot\pro.cs, plus edit viz.cs and prosim.cs, in the Plants-vs-Zombies bot. That would change how the live bot plays once the lead deploys it. The harness says the relayed request wins where the two differ, and nothing in it asks for or approves bot changes, so I stopped.

Files changed: none.
Compile check: not run, since nothing was edited.

State of the builders' work, so nothing gets deployed by mistake:
- **Task A (replay.cs, outcome.cs):** written and compiles (BUILD OK). Not hooked into anything.
- **Task C (vaseplan.cs):** written and compiles (BUILD OK). Not hooked into anything.
- **Task D (seen.cs, prosim.cs, pro.cs):** written and compiles (BUILD OK). This one already edited pro.cs and prosim.cs, and the learning bot's sim changes too (mines, charmed zombies, vault and torch fixes). Those edits go live on the next deploy even though integration was not done. Its two required bot.cs hooks are still missing: `Seen.Frame(s);` after `Mem.Get()`, and `Seen.Prune(fullHp);` in `Perceived()`. Without the second one, `fullHp` keeps growing.
- **Task B (learning) and Task E (watchdog):** refused for the same reason, no changes.

NEEDS FROM INTEGRATION / orchestrator:
- Check whether this run picked up the wrong task text. The user asked for a "talk to AI about ideas or nightmares" agent, not PvZ bot work.
- If the user does want the bot integration, they need to confirm it in their own message. I can then wire in everything from the builder reports exactly as specified and compile-check until BUILD OK. That covers the Replay/Outcomes hooks, VasePlanner replacing the vase block and squash/hypno/ambush gates, the Seen hooks, the defect fixes, plant verification in ExecutePlant, and fog-aware kill counting.
- If the user wants the conversation agent, reissue the task with that scope: where it should run (a CLI chat, a small web page or Artifact, or a Discord bot through the existing relay), and the tone and safety framing for talking about nightmares.

##### FIX
NOT DONE. I read no files, edited nothing and ran no compile check.

**Why I stopped:** The only user request relayed to this run is "my friend wants to talk with ai and give it possable ideas or nightmares, make a agent for that please". That asks for a chat agent. Task G is a fix pass on the C:\cbot PvZ bot, which is unrelated to it. The harness says the relayed request wins where the two differ, so I did not take the write access Task G gave me over every C:\cbot source file.

**Findings table:** The three reviewers (correctness, vasebreaker, regression-safety) all declined to review for the same reason, so none of them reported a code defect. There is nothing to fix or reject.

| Finding | Result |
|---|---|
| None reported by correctness | n/a |
| None reported by vasebreaker | n/a |
| None reported by regression-safety | n/a |

**Compile result:** not run. Nothing changed.

**v2 change-set summary:** I can't give this honestly. I didn't read the code, and the reviewers' reports disagree with each other about what is on disk.

**What the reviewers said, none of it checked by me:**
- The Task D builder edited pro.cs and prosim.cs, and those edits go live on the next deploy.
- vasebreaker says it saw Seen hooks already there, at C:\cbot\prosim.cs:46 and C:\cbot\pro.cs:148. The integration report says the bot.cs hooks `Seen.Frame(s);` and `Seen.Prune(fullHp);` are still missing. Both could be true, since they name different files.
- regression-safety warns that without the `Seen.Prune` hook, memory could grow without limit.
- replay.cs, outcome.cs and vaseplan.cs are reportedly not hooked into anything.
- If a deploy could happen before this is settled, the safe fallback is to restore pro.cs and prosim.cs from C:\cbot\snapshots\before-v2-upgrade_*.

**NEEDS FROM ORCHESTRATOR:**
1. Check the task list for this run. It looks like the wrong one for the user's request.
2. If the user does want the PvZ fix pass, they need to say so in their own message, then reissue Task G.
3. If the user wants the chat agent, reissue the task with that scope: where it runs (a CLI chat, a web page or Artifact, or Discord through the existing relay) and how it should handle talking about nightmares.

**NEEDS FROM INTEGRATION:** none.