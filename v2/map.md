##### loop-state
The tick loop is `Bot.Run` (bot.cs:87-287) and runs at about 16-21 Hz. Death is recorded at bot.cs:160-172 and a win at bot.cs:181-189. The best place to feed a ring buffer is `Track()` (bot.cs:533), and every snapshot field is a plain int or float, so serializing is cheap. Nothing was edited, built or run.

## 1. Tick loop: `Bot.Run(int secs, string logPath)`, bot.cs:87-287
- **Start.** `run.ps1` dot-sources `build.ps1` and then calls `[Bot]::Run($Secs,"C:\cbot\bot.log")`. Setup is at bot.cs:88-96: `Learn.Load/Strategy.Load/Roles.Load/Diag.Load`, `Viz.Start()` (a WinForms UI thread, viz.cs:84) and `ProMode` read from mode.txt.
- **Loop.** `while (DateTime.Now < end)`, bot.cs:101.
  - bot.cs:102: `if (File.Exists(@"C:\cbot\stop.flag")) { File.Delete(...); result = "hot reload"; break; }`
  - bot.cs:103: `ApplySpeed()` every 0.5 s. It writes `Mem.TimeScale` (the engine's `mUpdateMultiplier`, mem.cs:179). `PrecisionLevel` (vases) caps the speed at x2.
  - bot.cs:106: `var s = Mem.Get();` builds a new `State` on every tick. Nothing is reused, so storing references in a buffer is safe as long as nobody mutates them afterwards.
  - bot.cs:280: `Thread.Sleep(Speed >= 3 ? 5 : 20)`.
  - Measured in bot.log: `perf: 16-21 Hz | read 3.5-5.3 track 0.1-0.4 score 6.9-9.5 act 14.5-29.6 ms`. A plant action costs about 15-30 ms and does 1-2 extra `Mem.Get()` calls (bot.cs:670, 679) that are not recorded anywhere.
- **Branches before play.** Each of these `continue`s or sleeps, so a playing-state snapshot happens only past bot.cs:204:
  - dialogs (129-132)
  - Dave (134-145, sleeps 900 ms)
  - shovel tutorial (147-156)
  - menu (157)
  - **scene 4 = zombies won** (160-176)
  - **scene 5 = award** (177-194)
  - seed chooser (196-200)
  - `!HasBoard || Scene!=3 && !Complete` (201-204)
- **Playing path.**
  - `Track(s)` at bot.cs:212 (bot.cs:533-576). It ends with `lastPlay = s;` (550) and `Viz.Publish(s)` (575).
  - `if (s.Complete) { CollectAll(s); ...; continue; }` (215) is the earliest tick where a win is visible.
  - The pause handler (216-223), then `CollectAll` (225).
  - The vasebreaker block (226-272): `PrecisionLevel`, `footballHunt`, `desperate`, `lastVaseWait`, and the pick at 266. The vase break is logged with `L()` only, not `Event` (269).
  - Whack-a-zombie (274-277), then `TryPlant(s, ref lastPlant)` (278).
- **TryPlant** (bot.cs:607). In pro mode it runs `ProScore(s)`, then `ProDecide(cands, s, out psave)`, then `Viz.SetDecision`, then `ExecutePlant` (621-628). `ExecutePlant` (660-684) writes the `Event("PLANT ...")`/`"fail "` line at 682. Hook it here to record actions with their score and reason (`act.Label`, `act.Score`, `act.Why`, `psave`).
- **Exceptions.** bot.cs:279 catches, logs `err`, and sleeps 500 ms.

## 2. State (mem.cs:9-47, filled in `Mem.Get` mem.cs:86-175)
- **`State`.** `Scene, Sun, Wave, Waves, Level, Background(0 day,1 night,2 pool,3 fog,4 roof), Tutorial, FogHidden; double GameTime (mMainCounter/100); bool Paused, HasBoard, Complete`.
  - `Zombies` (List<Zom>), `Plants`, `Suns`, `Packets`.
  - `Blocked`, `Graves`, `Vases`, `LeafVases` (look==4), `SkullVases` (look==5). These are HashSet<int> keyed `row*10+col`, built at mem.cs:144-154.
  - `int[6,9] Square`.
  - `int[] Mowers` (-1 none, 0 rolling in, 1 ready, 2 triggered, 3 squished).
- **`Zom`** (mem.cs:9): `Id, Row, Type, Hp, MaxHp, Helm, Shield, X, Y, Total`.
  - There is **no eating flag**. layout.txt:131-171 has `Zombie size=0x188`, `0x28 mZombiePhase`, `0x51 mIsEating : bool`, `0xB0 mChilledCounter`, `0xB4 mButteredCounter`, `0xBE mHasHead`.
  - The stride used is 0x18C (0x188 plus the ID), so adding `Eating = it[0x51] != 0` at mem.cs:107 costs nothing (the bytes are already read). The same goes for chilled/buttered/phase.
  - Fog levels remove hidden zombies from the list (mem.cs:113-125), so a replay of a fog level will not show those zombies.
- **`Plt`**: `Id, Row, Col, Type, Hp, MaxHp`.
- **`Sun`** (mem.cs:12): `Id, X, Y, Type, Seed, Motion`.
  - `IsPacket` is `Type==23||24`, and `Seed` is the plant shown on a dropped packet.
  - Types 11/12/13 are sun (bot.cs:525); everything else is coins.
  - Packet age comes from `Learn.PacketTimeLeft(id)` (learn.cs:61). It returns -1 until the lifetime has been learned.
- **`Pkt`**: `Slot, Type, Cost, Ready, Active, Cd(0/1), FromCoin, CoinId`.
- **Not in State; these are static on Bot:**
  - `VaseRound` (bot.cs:457; set by Dave's text at bot.cs:137; reset to 1 on death/win)
  - `vaseFinalRound` (456), `wasHunting` (footballHunt), `lastVaseCount`
  - `PrecisionLevel`, `Panic`, `LaneDanger[6]` (686), `UnderThreat` (688; set in pro.cs:209 / bot.cs:777)
  - `lastWave/lastWaves` (bot.cs:120, scene 3 only), `Speed`
  - `Mem.CursorType()` is an extra memory read, so don't call it per snapshot.

## 3. Where death and win are detected (both in Bot.Run)
- **Death** is bot.cs:160-172, gated on `s.Scene == 4 && lastMenuClick < sceneSince && inScene < 1 && lastScene0 != -1`. In order:
  - `BreachDiag(lastPlay, ...)` (162) and the nemesis.txt append (164-165)
  - `lastPlay = null` (166)
  - the reset line (167): `VaseRound = 1; Learn.Died(); ... Strategy.LevelEnded(false, ...)`
  - **`Event("RESULT lvl " + lastLevel + " DIED (death #...)")` (168)**
  - **`if (ProMode) ProRecord("PRO lvl " + lastLevel + " DIED wave " + lastWave + "/" + lastWaves)` (169)**
  - **Hook:** insert the ring-buffer dump just before 166, while `lastPlay` (the final playing State) and `VaseRound` (reset at 167) still hold their values.
- **Win** is bot.cs:181-189, gated on `Scene==5 && inScene>4 && >4s since lastMenuClick && !winCounted`. It does the same kind of reset, then `Event("RESULT lvl N WON ...")` (186) and `ProRecord("PRO lvl N WON")` (187).
  - The game-side win starts earlier, at the first tick where `s.Complete` is true (bot.cs:215). Mark the replay end there.
- **`ProRecord`** (pro.cs:585-588) does an `AppendAllText` to pro_record.txt, then `ProTally()`, which re-reads the whole file every time.
- **Level start for a per-level replay file:** use the scene-change block (bot.cs:109-115) on `s.Scene == 3`, or the first tick that passes bot.cs:204.
  - Do **not** use `ResetLevel()` (514). It is called on every Try Again / Next Level click (173, 190) and so fires repeatedly.

## 4. Hot reload and build
- **restart.ps1:**
  1. Finds the `powershell` process running `run.ps1`.
  2. Creates `stop.flag` and waits up to 9 s (30×300 ms).
  3. Force-kills the process if it is still alive, deletes the flag, dot-sources build.ps1, and starts a new hidden `run.ps1`.
- **Consequences:**
  - Each reload is a new process, so **all statics, including a ring buffer, are lost**.
  - Dump the buffer at bot.cs:102 too, or flush the replay writer in the exit path at bot.cs:282-284.
  - A tick stuck in Dave sleeps or `ExecutePlant` can push past the 9 s wait and get killed with no cleanup.
  - watch.ps1 relaunches `run.ps1` if it is missing, unless `hold.flag` is present.
- **build.ps1:**
  - `$src` is a hard-coded list on line 4: pvz, mem, bot, viz, learn, quips, strategy, roles, diag, pro, prosim.
  - **A new .cs file must be added to that list.** Otherwise it is not compiled, and its timestamps do not trigger a rebuild, because `$newest` is taken over `$src` only.
  - It compiles with `Add-Type` (Windows PowerShell 5.1 CodeDom, C# 5): no `$""`, no `?.`, no `out var`, no expression-bodied members.
  - There is a guard for `//` comments pasted mid-line that swallow code.
  - If pvz.dll is locked it loads `pvz_HHmmssfff.dll` instead.
- **Existing reusable code:** `C:\cbot\recorder\rec.cs` already writes a read-only "SNAP" format every 2 s, using `Mem.ZomName@rROW:X:TOTAL` and `SeedName@rRcC`. It runs on `recorder\memro.cs`, which is a copy of mem.cs.

## 5. Suggested insertion points and serialization
- **Ring buffer:** append in `Track()` next to `lastPlay = s` (bot.cs:550).
  - It runs on the main thread only, so no lock is needed. Viz reads only through `Publish` under `lock(L)`.
  - Sample by `s.GameTime` delta ≥ 0.25-0.5 game seconds, not per tick. Game speed switches between x2 and x5, so wall-clock time misrepresents game time.
  - 90 s at 0.5 s is 180 entries. Use a fixed `string[]` with a head index; don't use `Queue<State>` holding full States.
- **Cheap per-snapshot line** (around 300-600 B, StringBuilder, well under 0.1 ms):
  - `t=GameTime sun wave/waves vr=VaseRound mow=6 digits danger=LaneDanger[6]:0.0 thr=UnderThreat`
  - `Z: id,type,row,(int)x,Total[,eat]`
  - `P: type,row,col,hp`
  - `V: cell+look(g/s/?)`
  - `K: packet id,seed,x,y,PacketTimeLeft`
  - `B: ready bank slots`
  - Write numeric types and put a name legend in the dump header. Avoid `ProSim` and `Mem.*` calls inside the snapshot.
- **Action events:** append to the same buffer from `ExecutePlant` (682), the vase break (269), `BOWL` (617) and the mower fired line (557).
- **Per-level replay:** hold one buffered `StreamWriter` per level. Do not use `File.AppendAllText` on every tick; `Viz.AddEvent` already pays that cost per event (viz.cs:60). Flush every ~2 s and on death/win/hot reload.

## 6. Defects found
1. **Useless death record on Vasebreaker.** pro_record.txt has `PRO lvl 35 DIED wave 0/20` on every death because Wave stays 0 there. Record `VaseRound` and `lastVaseCount` instead.
2. **False "! lost" events.** At a vasebreaker round change all plants are wiped, and bot.cs:564-569 reports each one as a loss and increments `Strategy.PlantsLost`. events.log 22:56:10 shows six of them at once. Only `IsConveyor` is excluded.
3. **Win can be counted twice.** The win path has no `lastScene0 != -1` guard, unlike the death path. A bot started or reloaded on the award screen counts the win again.
4. **Last breach actions are lost.** `lastPlay` is updated only in `Track`, while the extra `Mem.Get()` calls inside `ExecutePlant` are thrown away. The last ~100 ms before a breach is never captured.
5. **Missing eating flag.** Proposals that want a "zombie is eating" signal currently have to infer it from X not changing. The real flag sits in bytes already read (`it[0x51]`).
6. **Hand-written pro_record line.** pro_record.txt contains `PRO TUTORIAL (Help vase) WON`, but no code writes that string. It was added by hand, and `ProTally` counts it as a win.

##### vase
## Vasebreaker 4-5 (lvl 35): code map, defects, and data for a contents-tracking planner

### 1. What the logs show (events.log tail of 60k lines, bot.log tail of 120k lines, nemesis.txt)
- **lvl 35 has 0 wins** in the last 360 attempts. 363 starts reached round 1, 267 reached round 2, 41 reached round 3. The recent pro_record lines all read `PRO lvl 35 DIED wave 0/20`.
- **Last 35 deaths by round:** 9 in round 1, 18 in round 2, 8 in round 3.
- **Killers in the last 35 (nemesis.txt):** FOOT 17, BDANCE 7, NORM 7, PAIL 4. Across all lvl35 deaths: FOOT 196, NORM 61, PAIL 55, BDANCE 21. So round 2 deaths are footballs and round 3 deaths are backup dancers.
- **Vase count per round is fixed:** 15, then 20, then 25 (highest `left=` value in `VASE break` lines).
  - Green vases broken: round 2 = 144 over 266 rounds, round 3 = 75 over 41.
  - No skull vase was ever broken.
- **Contents per round.** Seeds are the average planted per completed round. Zombies are the average of the highest count seen at once in the 5 s state lines.

| Round | Vases | Seeds | Zombies | Inferred zombie vases |
|---|---|---|---|---|
| 1 | 15 | PEA 4.93, SQUASH 4.33 | NORM 1.6, PAIL 0.65 | about 5 |
| 2 | 20 | SNOW 4.98, PEA 4.0, SQUASH 3.66 | NORM, FOOT, PAIL | about 7 |
| 3 | 25 | PEA, SNOW, HYPNO (95 hypnos over 41 attempts), never a squash | DANCER about 1, BDANCE 4.07, NORM 2.9, PAIL 1.44, JACK rare | — |

- **BDANCE is not vase contents.** The dancer summons them, so the planner must not count them against the vases.
- **Squash use per death:** round 1 always uses all of its about 5 squashes; round 2 uses 1 to 4.
- **Why each squash was planted, in round 2:**

| Reason | Count |
|---|---|
| Ambush park (about to fade) | 311 |
| "Path of the nearest zombie" | 233 |
| SIM rule | 75 |
| "squash the pail" | 50 |
| **"squash the foot"** | **12** |

  Squashes are almost never spent on the football that kills the bot.

### 2. Vase break block, bot.cs:226-272 (main thread, every tick, after `CollectAll` at :225)
- `weaponOnLawn` (:228) is true when any dropped packet other than a squash (`c.Seed != 17`) is on the lawn. Squash packets never block breaking.
- `PrecisionLevel` and `precisionLvl` (:230-231) switch on with the first vase and off when the level changes.
- `onLawn` (:237) is zombies with `X < VISIBLE_X` (760).
- `desperate` (:238): no weapon on the lawn, 3 or fewer zombies, and one with `X < 380` in a lane with no plant ahead of it.
- `footballHunt` (:241): a FOOT is on the lawn, no squash packet, and **no squash plant anywhere**. One parked squash in another lane cancels the hunt.
- `vaseGap` (:246): 0.5 s when hunting, 0.8 s when desperate, 2.5 s under threat, 1.8 s otherwise.
- `hopeless` (:251) and `lastVaseWait` (:252): with 1 vase left, not the final round, nothing hopeless, and a zombie on the lawn, the bot holds the last vase.
- Gate (:254): `Mem.CursorType()==0` (one extra read per tick).
- `armed(k)` (:257) counts `ProIsShooter` plants left of the vase in that row.
- Choosing a vase (:259-266):
  - Leaf vases always qualify; other vases only when the lane is armed.
  - When hunting, any vase qualifies.
  - With 3 or more zombies out, only leaf vases qualify, otherwise it jumps to `vaseDone`.
  - Fallbacks: any non-skull vase, then any vase. **Lines :264 and :265 are the same fallback (dead code).**
  - Sort order: leaf first, skull last, more armed first, rightmost column, then lowest row.
- Click (:270): `Pvz.FastClick(CellX(v%10), CellY(v/10))`. `lastVase` is set at :267.

**Hook:** replace :228-272 with `VasePlanner.ChooseBreak(s, out why)`, which returns a cell or -1. Keep `lastVaseCount` and `wasHunting`, because the status line at pro.cs:572 reads them.

### 3. Round tracking
- bot.cs:137 sets `VaseRound` from Dave's lines: "last of" gives `vaseFinalRound=true, VaseRound=3`; "more vases" gives 2.
- The round is reset to 1 at :167 (death) and :185 (win), not in `ResetLevel` (:514).
- Declared at :456-457 as plain statics.
- **Defect:** hot reload (bot.cs:102, `stop.flag`, 122 times in the bot.log tail) restarts the process, so `VaseRound` goes back to 1 mid-level. The squash rules then switch off and `lastVaseWait` misfires.
- **Fix:** infer the round from the vase count when a round starts (15, 20 or 25). The count rises in one frame after the vases briefly vanish (comment at :229).

### 4. Squash, hypno and dropped-packet logic in pro.cs
- `SquashFree` (pro.cs:13) is `s.Vases.Count <= 3 && no FOOT on the lawn`. It uses vases left as a stand-in for "no footballs left"; the planner should replace it.
- Dropped packets become `Pkt{FromCoin=true, CoinId, CoinX/Y, Cost=0}` at pro.cs:157 (and bot.cs:740 for learn mode).
- Hypno priority `hypPri` (:345): DANCER 4, BDANCE 3, PAIL/FOOT/REGEN/GARG 2. Hypno is placed at `ColOf(z.X)-1` down to -3 (:351-357).
- Squash target loop (:347-375):
  - `tough` (:361) is `Total>=1000` or FOOT/REGEN/GARG/ZAMB/POLE/DOLPH/SNORK.
  - A fast zombie that is not eating is skipped (:366).
  - The round 2+ scarcity rule is at :368.
- Dropped-packet fallback (:460-519, fixed score 260 at :517):
  - **Ambush** (:481-483): squash, no zombies, packet fading in under 3 s. It parks in front of the leftmost vase of a lane without a squash.
  - Bombs and squash in general (:484-510): the round 2+ pool is `Total>=600` or FOOT/REGEN (:486), held unless fading (:487), then `pool=live` (:488). The squash is skipped unless the zombie is tough or the packet is fading (:497). A fast zombie that is not eating gets a parked squash only when fading (:498-505). Otherwise it is dropped at `ColOf(z.X)` (:507).
  - **Waste path:** a fading squash falls through :488 to the nearest NORM (233 cases in round 2).
- prosim.cs:138 has its own squash reserve without `SquashFree`, so it disagrees with pro.cs:368/486/497.
- **Thresholds are wrong for this mod:** a normal zombie reads about 570 hp in this mod and pails 1090-1670. `Total>=600` and `>=1000` decide "tough" for a damaged pail, but not on purpose.
- **Hooks:** replace pro.cs:13, :368, :486-487, :497 and prosim.cs:138 with one predicate, `VasePlanner.SpendSquashOn(s, z, fade)`. Replace :481-483 and :498-505 with `VasePlanner.AmbushCell(s)`.
- **Ambush rule worth changing:** park only in a lane whose unbroken vases are likely zombies. That means a skull vase, or the most "?" vases times P(zombie), where P(zombie) = zombies left ÷ unknown vases left. Never park when the vases left are only those that end the round (the wipe kills it): 22:55:50 parked r3c5, lost in the wipe at 22:56:10.

### 5. Defects found
1. **Hypnotized zombies are still treated as enemies.** mem.cs:107 never reads `mMindControlled` at Zombie+0xBC (layout.txt:169). Evidence: 22:37:13 and 22:37:14, two hypnos at r4c0, both for "the pail (490 hp)" (same zombie, same hp). The second hypno was wasted. Fix: read `it[0xBC]` into a new `Zom.Charmed` field and filter it out of `live`, the shooting logic and nemesis.
2. **"! lost SQUASH" fires for every squash use.** bot.cs:567 `consumedByDesign` covers only MINE/INSTANT/GRAVE/HYPNO; roles.txt:5 marks 17 as `MELEE`. This inflates `Strategy.PlantsLost` and the Quips. The round wipe also triggers "! lost" for every plant at once (22:54:39, 22:56:10).
3. **`footballHunt` (:241) is cancelled by a squash planted in any lane,** not only the football's lane.
4. **bot.cs:264-265 duplicate fallback** (dead code).
5. **Hot-reload resets `VaseRound`** (section 3).
6. **`Learn.PacketTimeLeft` (learn.cs:61-65)** returns -1 until one packet has faded untouched. Until then the fade-based rules (:487/:497/:481) never fire.
   - The game's own timers are Coin+0x4C `mCoinAge` and +0x54 `mDisappearCounter` (layout.txt:289/291). They match the visible blink, so reading them is fair play.

### 6. Seeing what a broken vase held, without reading the hidden contents
- mem.cs:149-153 reads only grid item type 7, cell, and the look (+0xC: 4 leaf, 5 skull). The contents are deliberately not read (comment at mem.cs:30).
- Packets: `Sun.IsPacket` is `Type==23||24` (mem.cs:12). The seed comes from Coin+0x68 `mUsableSeedType` (mem.cs:135), which is the picture on the packet, so it is visible.
- Packets are skipped by `CollectAll` (bot.cs:522) and picked up only by `ExecutePlant` (bot.cs:660-683): click `p.CX/CY` (= CoinX/Y), click the cell, add to `Learn.PacketsTaken` (:664). Success is `Plants.Count > before || Sun < s.Sun` (:680); on failure it right-clicks to drop and marks the cell `Bad` (:683).
- **Proposed `VasePlanner.Observe(s)`,** called right after `Track(s)` at bot.cs:212 (main thread, O(entities), no extra reads):
  - Keep `prevVases`, `prevZids` and `prevCoinIds`.
  - `broken = prevVases − s.Vases`. Accept only 1-2 cells per frame, near a recent click (`lastVase` within 1.5 s). This ignores the frames between rounds when the vases briefly vanish.
  - A new zombie id at `X < VISIBLE_X` is attributed to a broken cell in the same row with the smallest |CellX(c) − (z.X+40)|. **Skip BDANCE ids that appear near a live DANCER.**
  - A new packet coin is attributed to the nearest broken cell by X/Y.
  - Use a pending window of about 1 s of `s.GameTime`.
  - Per-round counters: `zombiesSeen[type]`, `seedsSeen[seed]`, and unknown "?" vases left.
- **Priors:** persist each round's observed totals per lvl/round to a new `C:\cbot\vase_contents.txt` and take the most common value across attempts as the prior. Starting values are the table in section 1. Then remaining = prior − seen.

### 7. Measuring squash hit vs waste
- Hook the plant-vanished loop at bot.cs:564-571 when `pt==17`.
- Add `zLast[id] = (Row, X, Type, Total)` next to `zHp` (bot.cs:541-548).
- When the squash vanishes, look up to 1.5 s back and forward:
  - **HIT_KILL:** a zombie id in row k/10 with last X within ±120 px of `CellX(col)` disappeared.
  - **HIT_DMG:** such a zombie dropped by 1000 hp or more.
  - **MISS:** neither happened.
  - **WIPED:** 3 or more plants vanished in the same frame, or the vase count rose.
  - **EATEN:** the squash's `Plt.Hp` was falling beforehand.
- Log the target type and round, and compute value (a FOOT hit in round 2 is good; a NORM in round 2 is a waste) in the `ProRecord`/events stream.

### 8. Per-tick cost and threading
- The main loop is single-threaded. Viz runs on its own WinForms thread (viz.cs:84) and only receives `Viz.Publish(s)` (bot.cs:575), so new planner statics are safe on the main thread.
- The panel should read only a published snapshot.
- All proposed work is LINQ over 20 or fewer vases and 30 or fewer zombies: negligible next to `ProSimDefence`.
- Persist the planner file only at round end, never per tick, since all statics die on hot reload.

The two parse scripts are in `C:\Users\User\AppData\Local\Temp\claude\C--Users-User-AppData-Local-Claude-Data-claude-pzs8sxrjxfjjc-scratch-workspaces-255952f6-c3d3-44e6-9b5d-1b6405604111-48bbb1d3-f6be-4bb6-9185-acd7de645e4d-scratch-2026-10-31-b92cb2\a0f54e73-aade-4bf4-a2c1-7714576869c0\scratchpad\vase.ps1` (it runs only through the `\\?\` long-path prefix).

Files: C:\cbot\bot.cs, C:\cbot\pro.cs, C:\cbot\prosim.cs, C:\cbot\mem.cs, C:\cbot\learn.cs, C:\cbot\roles.txt, C:\cbot\layout.txt, C:\cbot\events.log, C:\cbot\bot.log, C:\cbot\nemesis.txt

##### learn
Learning-system map for C:\cbot (read only: nothing was edited, built, run or killed)

## (a) Summoner detection — finding: NORM and nearly every common zombie kind are wrongly marked as summoners

**Mechanism** (`learn.cs:242-251`):
- A zombie is counted as "summoned" the first time its Id is seen, if it is already mid-lawn (`z.X < 735`), its cell is not a grave, and it is not the first frame (`lastFrame >= 0`).
- The "caller" is the nearest already-tracked zombie within one row and 170px. It gets `SummonEvidence[KeyOf(caller)]++`.
- `IsSummoner` (`learn.cs:23`) is `n >= 3`. There is no time decay, no normalisation by how often the kind is seen, and no per-level split.
- The count is persisted in `SU|` lines (`learn.cs:382/404`).
- **Where the 2.5x is applied:** `bot.cs:709`, `double summon = Learn.IsSummoner(Learn.KeyOf(z)) ? 2.5 : 1;`, inside `Perceived()`. It multiplies both return paths (`bot.cs:710-711`). That is the only caller (checked by grep across all .cs files).

**Evidence of false positives** (knowledge.txt): `SU|NORM|19`, `DIRTY|30`, `CONE+helm|9`, `FLAG+helm|5`, `DOOR+helm+shield|5`, `PAIL+helm|4`, `DOOR+shield|3`, plus DANCER 14 and BDANCE 6.
- That is 9 kinds at 3 or more, so nearly every common zombie gets 2.5x.
- The effect: kinds that are not marked (POLE, FOOT, LJUMP, NEWS…) are *relatively* under-weighted.
- events.log also shows "summons" lessons for SNORK+helm, CONE and PAIL.

**Defects behind it:**
1. **The first-frame guard never works.** `learn.cs:233` sets `lastFrame = now;` *before* the zombie loop, so the check `lastFrame >= 0` at line 245 is always true.
   - After `LevelReset()` (`learn.cs:370`, which clears `zr`), a bot start or a hot reload (`bot.cs:102` stop.flag), every zombie already on the lawn counts as "summoned", and its neighbour gets the credit.
2. **Vasebreaker:** zombies pop out of broken vases mid-lawn next to other zombies. There is no `s.Vases` check, which is the likely source of `DIRTY|30`.
3. **Graves:** the grave check is an exact cell (`s.Graves.Contains(z.Row*10+zcol)`, line 245). A grave riser whose X maps one column off is miscounted. Bungee drops are not excluded either.
4. **No base rate:** NORM is seen 5690 times, so 19 coincidences means nothing. The rule ignores `ZKnow.Seen`.

**Minimal fix** in `Learn.Observe`:
```csharp
bool hadPrev = lastFrame >= 0;  // capture BEFORE line 233 sets lastFrame = now
...
if (hadPrev && s.Vases.Count == 0 && z.X < 735 && !NearGrave(s, z.Row, zcol /*±1 col*/)) { ... pendingSummon[caller.Id].Add(now) ... }
```
- Count a summon event only when **2 or more** new zombies appear within about 0.6s around the **same caller Id**. That matches a real dancer calling 4 backups at once and rejects one-off coincidences.
- Store events per kind as well as per-kind exposure (zombie-seconds on the lawn).
- `IsSummoner` = Wilson lower bound of events/exposure above a threshold, **and** events seen on 2 or more distinct `CurLevel`s.
- Return a confidence value and scale the multiplier with it: `summon = 1 + 1.5*conf` instead of a binary 2.5.
- Migration: discard the existing `SU|` lines, or keep DANCER/BDANCE only.

## (b) Plant roles — finding: FUME/PUFF/SCAREDY are stored as WALL because SHOOT evidence can never build up

**Current data:**
- roles.txt holds `3|WALL 10|WALL 8|WALL 13|WALL 17|MELEE` (8 = PUFF, 10 = FUME, 13 = SCAREDY).
- I parsed LawnStrings.txt with the same rules (`roles.cs:56-70`). It gives SHOOT for 8, 10 and 13, and SR (short range) for 8 and 24.
- So the description is right and the learned role is wrong.
- sanity.txt keeps logging "FUME/PUFF/SCAREDY was learned as WALL but its description says SHOOT (only **0** observations)". SNOW was learned as WALL at 22:37:28 with 4 observations.

**Root causes:**
1. **SHOOT evidence is impossible for described plants.**
   - The SHOOT test at `roles.cs:147` requires `Unknown(w.Seed)`. That is false whenever the description gave a role, so `support` (line 105) stays 0 and `need = max(4, 2*0+1) = 4` (line 107).
   - Four "WALL" observations therefore overturn the description.
   - Also, `knownLaneDps` (`bot.cs:538`, `!Roles.Unknown(p.Type)`) *includes* the tentative plant's own DPS (default 14 from `Learn.Dps`). So even without the gate, `extra` would be about 0.
2. **The WALL test is not gated** by `descSaysOther` (line 141), unlike the MELEE test.
   - `beside` = any zombie with `|z.X+40-cx| < 60` (line 140). That includes a zombie eating the plant in the *next* column, zombies frozen by snow peas, and a chain of different zombies, since `BesideSince` is never tied to a zombie Id.
   - "Survived 12s" never checks that this plant's own HP actually dropped.
3. **Evidence is not persisted.** `Roles.Save` (line 170) writes only `Learned`, so after any restart `n = 0`.
   - The sanity override in `RoleOf` (lines 81-84) then returns the description role, which only works if the descriptions loaded.
4. **External-file dependency and a latch bug.** `LoadDescriptions` (line 35) sets `descLoaded = true` *before* reading.
   - It depends on the hard-coded `C:\Users\User\Downloads\Modded Plants VS Zombies\Release\properties\LawnStrings.txt` (exists today).
   - It also depends on `Mem.PlantName(seed)` (`mem.cs:224`), which reads process memory.
   - If the first `RoleOf` call happens before the game is attached (names come back ""), or the file read fails, `DescRole` stays empty for the whole process. The learned WALL is then used unchecked for SHOOT/DPS/placement (`bot.cs:29-31`, `prosim.cs:32`).
5. **Description parser misfires** (from my parse):
   - CACTUS → MINE, from the `all.Contains("arm")` substring at line 67.
   - SPIKEWEED → SHOOT (it is ground-contact).
   - SQUASH / TORCHWOOD / MAGNET / GARLIC / MARIGOLD → none.
   - SQUASH learned as MELEE, but it is single-use. `bot.cs:567` `consumedByDesign` omits MELEE, so it inflates `Strategy.PlantsLost`.

**Minimal fixes:**
- **Use real HP.** `Plt` already has `Hp, MaxHp` (`mem.cs:10, 129`, offsets 0x40/0x44). Replace line 141 with:
  `WALL iff p.MaxHp >= 3 * median(MaxHp of SHOOT/SUN plants seen)`, or an absolute ≥1000.
  - Optional: only count chewing time while this plant's `Hp` is actually decreasing.
- **SHOOT test:**
  - Change line 147 `Unknown(w.Seed)` to `Unconfirmed(w.Seed)`.
  - Pass `knownLaneDps(row, excludeSeed)` and exclude every plant of `w.Seed`'s type from the sum at `bot.cs:538`.
- **Persist evidence:** add a format like `E|seed|role|n` in roles.txt.
- **Latch fix:** set `descLoaded` only when `DescRole.Count > 0`, and retry every N seconds otherwise.
- **Parser fixes:**
  - Use `Regex @"\barm(s|ed|ing)?\b"` instead of the "arm" substring.
  - Make "can't be eaten" or "walk over it" map to a GROUND role.
- **Data cleanup:** delete the 8/10/13 lines from roles.txt.

## (c) Damage and range attribution — finding: the range estimate measures where zombies stand, not how far a plant shoots

**Range** (`learn.cs:39-56`, used by `Range()` at 30-38, `FireTime` at `bot.cs:691`, and `prosim.cs:33`):
- The sample is the farthest damaged zombie's distance from the front-most shooter. The estimate is the 97th percentile of the last 300 samples.
- The comment at line 48 says "85th", which is wrong.
- **Bias:** this measures zombie positions at hit time, not reach, so it underestimates long-range shooters.
- Results in knowledge.txt:
  - PEA `R|0|484` → `Range(0)` returns **484**, because it is ≥400 so not flagged, and below the 550 cutoff. Peashooter is modelled as about 6 tiles.
  - REPEATER `549.65` → **549**, 0.35px under the cutoff.
  - SNOW 609 → 900; SCAREDY 636 → 900.
- **Persistence mismatch:** `rangeSamples` is not persisted but `RangeMax`/`rangeN` are. After a restart, the first 40 fresh samples overwrite the long-run value.
  - This is why sanity.txt flaps: PEA 127/154/257/374px, then "back in range", 123 range flags in total.
- **Fix:**
  - Only take a sample when a zombie exists *beyond* the candidate distance. That gives censored data: range ≥ hit distance, and range < d only if a zombie at d sat unhurt for at least 2s with the shooter alive.
  - Estimate range = max over persisted hits, capped by the "unhurt" bound.
  - Default to 900 unless `ShortRange.Contains(seed)` or the unhurt evidence counts 20 or more.
  - Persist the percentile buffer, or do not overwrite the estimate until 150 or more new samples.
- **Per-tick cost:** `OrderBy` over 300 samples, per lane, every frame (line 52). Recompute only every N samples.

**DPS** (`learn.cs:329-349`):
- Only lanes with a single shooter type, and no frame with a >150 drop (explosion).
- `laneDmg` includes damage from non-shooters (spikeweed, a chomper bite under 150, splash from neighbouring lanes).
- `sh.Count` includes sleeping daytime mushrooms (`Roles.Nocturnal` is not checked) and hidden scaredies, so DPS is underestimated.
- **Fix:**
  - Exclude `Nocturnal` plants when `Learn.Daytime` is true.
  - Skip lanes containing any non-SHOOT damage source.
  - Read the asleep flag; vanilla `Plant::mIsAsleep` is at 0x143, but this offset is not verified for the mod.

**Contribution credit** (`learn.cs:117-130`):
- Lane damage is split by `Dps(type)/tot` across *all* shooters in the lane, regardless of range, column, sleep, or whether the target is in reach.
- A col-0 puff gets credit for hits at col 8. So `SeedValue` and `PickScore` (lines 98-108) are biased.
- **Fix:** weight each shooter by `inReach(p, nearestZombie) && !asleep` before splitting. Credit 0 if no shooter is in reach.

**Toughness** (lines 275-282): reasonable. Kills by mower or instant are excluded by `LastTotal > 60`. Snow-slowed speeds pull `Speed` low (EMA at line 265); minor.

## (d) Strategy evaluation — finding: variants are compared by environment only, so the level a strategy happened to play drives its score

**How it works now:**
- The pool is filtered by `Env` only (`strategy.cs:62`). `ChooseForLevel(int level, ...)` **never uses `level`**.
- Fitness (lines 126-127): a win is 60-100; a loss is `-100 + 100*wave/waves - 10*mowers`.
- Exploit ranking is UCB `Avg + 15/sqrt(Runs)` (line 71). The bonus is tiny against a 200-point fitness spread.
- `best` is by raw mean, so it can be a 1-run fluke.
- The trial rule is 3 runs (line 78). `lastWasBad` (line 77) compares a 3-run mean to the best mean with a fixed 30-point margin and no variance check.

**Confounding evidence:**
- Night lineage: S19 has 63 runs, avg -33.7; S10 has 6 runs, avg -63.75.
- But `LU|19|*` shows level 19 brought 75 times, and `D2|19|81` deaths. S19 mostly played the hardest night level while the others played different levels.
- The averages therefore mostly reflect which level each strategy faced.
- `Picks` and `SeedValue` (`learn.cs:93-150`) have the same level confound.

**Minimal fix:**
- Keep a per-level baseline `B[level]`: an EMA of fitness over all strategies on that level.
- Store a per-run record (stratId, level, fit, wave/waves) in a separate file such as `strategy_runs.txt`.
- Rank by mean **advantage** `fit - B[level]`.
- Change `ChooseForLevel`:
  - Prefer strategies with 2 or more runs on *this* level.
  - Use UCB with `c * sd / sqrt(n)`, where sd is the pooled standard deviation (about 60).
  - Declare "bad" only when the upper confidence bound on advantage is below the best strategy's lower bound.
- **Bug:** `Load()` sets `nextId = Tried.Count + 1` (line 165). Any line that fails to parse causes Id collisions. Use max(Id)+1 instead.
- `Load()` also picks `Cur` across all envs (line 166).

## Other notes
- **Persistence is stale or not happening:**
  - mode.txt = `pro`, and every `Save()` returns early in ProMode (`learn.cs:375`, `roles.cs:169`, `strategy.cs:146`).
  - roles.txt, strategies.txt and knowledge.txt were last written 10/31 10:41-10:56 AM. Yet events.log has later learning (S49 scored at 22:02-22:11, SNOW→WALL at 22:37), and S49 is absent from strategies.txt.
  - Learning in pro mode, or since that time, is lost on hot reload.
- **Static state:** all of Learn, Roles and Strategy is static, on the single main-loop thread `Track()` (`bot.cs:533-538`). It is rebuilt on hot reload from the files only.
- **Per-tick cost:** `RoleOf` calls `Learn.Flag` (a dictionary lookup) on every `Is()` call, which is fine. The range sort above is the main waste.

##### sim
**Pro scoring and simulator map: what the sim predicts, where Pro reads hidden data, fog handling, and how to add calibration logging and a single visible-HP view**

## 1. What the simulator predicts (prosim.cs)

- **Output:** `SimOut` at prosim.cs:9 has four fields:
  - `BreachT`: game-seconds until the first zombie reaches `X < -60` (line 103). 9999 means no breach.
  - `HpLeft`: the summed HP of zombies still alive when the run ends (line 108).
  - `Lost`: the summed sun cost of plants eaten (line 101).
  - `Cost`: a score for how bad the lane is, `(breach ? 5000 + 120*(SIM_H-BreachT) : 0) + debt + Lost`, where `debt` is the remaining HP weighted by how close each zombie got (lines 110-111).
- **Horizon:** `SIM_H = 40`, `SIM_DT = 0.25` (line 13), so 160 ticks. The comments at pro.cs:452 and 456 ("30 s", "all lanes hold 30s") are out of date; it is 40 s.
- **Not modelled:**
  - Lawnmowers. A breach means "reached the house"; the missing mower is only a +300 score term at prosim.cs:159.
  - Zombies spawning during the horizon.
  - Torchwood cancelling snow.
  - Plants already on the lawn: an armed potato mine never explodes (it is skipped as a blocker at line 93 and nothing ever detonates it). A waiting squash/chomper is only a 300-HP blocker. See defect D3.
- **Where it is called:**
  - pro.cs:453 builds `baseOut` for each lane: `rows.ToDictionary(r => r, r => SimLane(r, s, spd))`.
  - pro.cs:454 calls `ProSimDefence` (prosim.cs:117), which for each failing lane runs up to roughly 26 defenders x up to 5 lanes x 9 columns of `SimLane(r, s, spd, p.Type, c)` (line 150).
  - pro.cs:455-457 build `ProStatus` and set `ProBreachT` (static, pro.cs:15). `ProDecide` (pro.cs:540-541) uses it to decide whether to save sun.
  - The learning bot uses the same sim with `LearnedModel()` at bot.cs:1053-1063.
- **Cost per tick:** bot.log perf lines show `score 6.7-9.5 ms` at 16-21 Hz, so adding cheap logging is fine. Do not add extra `SimLane` calls inside the inner loops.
- **State and threading:** single-threaded; the main loop runs `Mem.Get` (bot.cs:106), then `Track` (bot.cs:533), then `TryPlant` → `ProScore` (bot.cs:621). `proModel` (prosim.cs:19) is a static cached `SimModel` whose `ZSpeed` closure is reassigned on every `ProModel(spd)` call (line 23). That is fine while single-threaded, but it is a trap if the sim is ever moved to another thread.
- **Hot reload:** `stop.flag` breaks out of the loop (bot.cs:102), and every static (`proTrack`, `fullHp`, `seenPlantHp`, `proModel`) is lost. Any calibration state must be appended to a file.

## 2. Every place Pro reads non-visible memory data

The raw values come from mem.cs:107-109 (`Hp` 0xCC, `MaxHp` 0xD0, `Helm` 0xD4, `Shield` 0xE4). `Zom.Total = Hp + Helm + Shield` (mem.cs:9). Pro uses `Total` no matter what `HealthVisible` says (bot.cs:117 sets it, mem.cs:237 reads it). It is only legitimate when the mod's health bars are on.

**Exact zombie HP, by location:**

| Location | Use |
|---|---|
| prosim.cs:22 | `ZHp = z => z.Total`. This is the main leak: the sim starts every zombie at its exact HP. |
| prosim.cs:44 | `Hp0 = m.ZHp(z)`: REGEN regrows to its current exact HP. |
| prosim.cs:138 | `z.Total >= 600` (vasebreaker squash gate) |
| pro.cs:202 | lane `danger`: `hp = zs.Sum(z => (double)z.Total)` |
| pro.cs:285 | snow bonus: `z.Total > 600` |
| pro.cs:313 | cherry/doom pile HP |
| pro.cs:328 | jalapeno lane HP |
| pro.cs:355 | hypno "Why" text (display only) |
| pro.cs:361 | `tough = z.Total >= 1000 ...` |
| pro.cs:368 | `z.Total < 600` |
| pro.cs:373 | squash/kelp/chomp score `550 + z.Total / 5`, plus text |
| pro.cs:475, 477 | belt/vasebreaker lane ordering, sums of `Total` |
| pro.cs:486, 496 | `zz.Total >= 600`, `toughZ` |

**Armor:** pro.cs:287 checks `z.Shield > 0`. The shield's presence can be seen on screen, so this is acceptable, but it should go through the new view. Helm/Shield amounts are only used via `Total`.

**Plant HP (smaller leak):**
- prosim.cs:46 uses `p.Hp` exactly for every plant.
- pro.cs:390 uses `wall.Hp < wall.MaxHp / 3`. On screen, only wall-nut, tall-nut and pumpkin show crack states (3 each); other plants show nothing.

**A further leak:** prosim.cs:43 includes zombies with `z.X < VISIBLE_X + 40` (800), while Pro's `live` at pro.cs:185 uses `< VISIBLE_X` (760, bot.cs:700). So the sim sees zombies at 760-800 that Pro treats as off-screen. Because those zombies are missing from `spd`, they also fall back to a speed of 17 (line 23), so fast zombies at the edge are under-predicted.

**Not a leak:** `Mem.ZomName(z.Type)` (appearance), the X position, and `ProSpeed` (measured from X changes, pro.cs:71-82, using `s.GameTime`).

## 3. Fog handling

- The only fog code is mem.cs:113-125. On `Background == 3` it reads `mGridCelFog` (+0x4C0) and `mFogOffset` (+0x5C8), and removes any zombie standing in a cell with density >= 200 from `s.Zombies`. `s.FogHidden` is set but never read anywhere.
- mem.cs:119 computes the column as `(z.X + 40 - 40 - fogOff)/80`, which does not match `ColOf` (bot.cs, `(zx+30-40)/80`). I have not verified this.
- Pro has no memory of zombies that disappear into fog. They drop out of `live`, `danger` and `SimLane`, so on fog levels `BreachT` is optimistic.
- The only fog-specific plays are the plantern at pro.cs:421 and the blover at pro.cs:405.

## 4. Defects found

- **D1. Zombies hidden by fog are counted as kills.**
  - bot.cs:546-547: a zombie id missing from the frame triggers `Viz.Kills++; Quips.Kill()`.
  - learn.cs:270-277: missing zombies go into `vanished` and get `k.Kills++`. They are also passed to roles through `LastVanished` (learn.cs:272 → bot.cs:537) and used for bomb credit (learn.cs:293).
  - Because mem.cs:117 removes fog-hidden zombies before any of this runs, fog levels corrupt kill stats and the learned stats; a zombie that comes back out is then a new record.
- **D2. Visibility leaks:** prosim.cs:43 (the +40 px, see section 2) and every `Total` use listed in section 2.
- **D3. Plants already on the lawn are ignored by the sim:**
  - An existing potato mine has `Effect == "MINE"`, is excluded from blocking (line 93) and never detonates.
  - A waiting chomper or squash is just a 300-HP blocker.
  - Result: breaches are over-predicted and the bot spends plants it does not need.
- **D4. `ProPlantHp` (prosim.cs:114) is a dead path for Pro.** `PlantHp` is only used for an added plant; existing plants use exact `p.Hp`. A newly planted wall gets 4000, but a damaged existing wall uses its exact remaining HP.
- **D5.** prosim.cs:74 counts torches with `q.Col * 80 < ahead[0].X` instead of `CellX(q.Col)`, an inconsistency of about 80 px. prosim.cs:96: a vaulting zombie hits `continue` and skips the breach check for that tick.
- **D6. The sim's result is conditional on doing nothing more, but the bot plants right after predicting.** Any check of predicted breach time against actual mower events is therefore confounded (see 5b).
- **D7.** `proTrack` (pro.cs:70) and `fullHp` (bot.cs:703) are never cleared, so they grow slowly forever.

## 5. Hook: logging and calibrating sim predictions

**5a. Prediction record, at pro.cs:453-457, right after `baseOut`.** Everything needed is available there: `s.GameTime` (game seconds, survives speed changes), `rows`, `baseOut[r]`, `live`, `spd`, `s.Wave`, `s.Level`. Throttle per lane, either every 2 game-seconds or whenever `BreachT` jumps by more than 3 s:
```csharp
static readonly double[] simLogAt = new double[6];
foreach (var r in rows) if (s.GameTime - simLogAt[r] >= 2) { simLogAt[r] = s.GameTime;
  var b = baseOut[r]; var lz = live.Where(z => z.Row == r).OrderBy(z => z.X).FirstOrDefault();
  File.AppendAllText(@"C:\cbot\simcal.log", string.Format("P {0:0.00} L{1} r{2} brk {3:0.0} hpLeft {4:0} lost {5:0} n {6} lead {7}@{8:0} v {9:0.0} mow {10}\n",
    s.GameTime, s.Level, r, b.BreachT, b.HpLeft, b.Lost, live.Count(z=>z.Row==r), lz==null?"-":Mem.ZomName(lz.Type), lz==null?0:lz.X, lz==null?0:spd[lz.Id], s.Mowers[r])); }
```
`File.AppendAllText` takes about 0.1 ms, so writing at most 6 lines every 2 s is fine. Keep it out of bot.log, which is already 8 MB.

**5b. Ground truth, and why a lane-level check alone is not enough.**
- **Lane-level truth:** the mower firing at bot.cs:554 (`lastMowers[r]==1 && s.Mowers[r]==2`) gives the actual breach time for lane `r`; append `A <GameTime> r<r> MOWER`. Game over (scene 4, bot.cs:160) gives the final breach. Also log `A t r PLANT type col` from `ExecutePlant` (bot.cs:681), so predictions can be dropped when a lane was changed within `BreachT`.
- **Per-zombie check (better):** the action confound does not matter over a short window.
  - Add `public uint Id;` to `SZ` (prosim.cs:10) and fill it at line 44.
  - Add an optional `Action<double, List<SZ>> probe` parameter to `SimLane`, called at fixed times t = 5 and 10.
  - In the base run only, store `(Id, t0+5, predX, predHp)`.
  - In `Track`, when that game time arrives, compare against the real `z.X` and the visible HP estimate. Treat a missing id as dead, but only if it was not hidden by fog (see D1).
  - The X error calibrates speed and eating; the HP error calibrates `ProDps`; the error in predicted death time calibrates both.
- **Fitting:** the outputs should be per-zombie-type speed bias and per-plant-type DPS scale factors. Feed them back into `ProModel` as fixed multipliers, read from a file at startup so they survive hot reload. If Pro must stay non-learning, report them only.

## 6. Hook: one visible-HP interface for Pro

**Where:** add a cached field `public double Vis = -1;` to `Zom` (mem.cs:9), and fill it once per frame at the top of `ProScore` (pro.cs:146), or right after `Mem.Get()` at bot.cs:106 so that `Track` and the calibration code use the same view:
```csharp
static double SeenHp(Zom z) {
  if (HealthVisible) return z.Total;                       // bars on screen: exact HP really is visible
  string n = Mem.ZomName(z.Type);
  double body = z.MaxHp > 0 ? z.MaxHp : 270;               // per-type constant (or use a table, e.g. {GARG 3000, ZAMB 1350, POLE 500 ...})
  Func<double,double,double> q = (cur,max) => max <= 0 || cur <= 0 ? 0 : cur/max > 0.66 ? max : cur/max > 0.33 ? max*0.66 : max*0.33;   // 3 sprite states
  return q(z.Hp, body) + (z.Helm > 0 ? q(z.Helm, HelmMax(n)) : 0) + (z.Shield > 0 ? q(z.Shield, ShieldMax(n)) : 0);
}
```
The three-state quantisation matches the damage sprites a player sees: arm lost, cone/bucket/door dented, and so on.

**Armor maximums:** there is no MaxHp read for helm or shield. Either use a table (CONE 370, PAIL 1100, FOOT 1400, DIGGER 100, DOOR 1100, NEWS 150, LADDER 500), or read the max fields. This build's offsets are shifted +4 from GOTY, which suggests HelmMax at about 0xD8 and ShieldMax at about 0xE8. Those offsets are a guess and need checking before use.

**Unknown mod zombies (REGEN, DIRTY):** fall back to `MaxHp`.

**Replacement (mechanical):**
- Replace `z.Total` with `z.Vis` at every line in the section 2 table.
- At prosim.cs:22, change `ZHp` to `z => z.Vis`. That also fixes `Hp0` for REGEN, which then regrows to the visible full value.
- Replace `z.Shield > 0` at pro.cs:287 with a `HasShield` flag on the view.
- **Plants:** in prosim.cs:46, use `ProWall.Contains(p.Type) || p.Type == PUMPKIN ? quantised(p.Hp, p.MaxHp) : ProPlantHp(p.Type)`. Leave pro.cs:390 (the worn wall) as it is, since crack states are visible.

**Also:**
- Change the prosim.cs:43 filter to `z.X < VISIBLE_X`.
- To keep leaks from coming back, add a build check that fails if `pro.cs` or `prosim.cs` contains `\.Total|\.Hp\b|\.Helm|\.Shield`. `Vis` is the only HP field Pro should read.

**Fog follow-up (optional):** in the same observation pass, keep a "ghost" list. When a known id disappears on `Background == 3`, it was not low-HP, and its last cell is foggy, keep it with its X moved forward at its tracked `ProSpeed`. Include ghosts in `live` and `SimLane`, and exclude them from kill counting in bot.cs:546 and learn.cs:270.

Files: `C:\cbot\pro.cs`, `C:\cbot\prosim.cs`, `C:\cbot\mem.cs`, `C:\cbot\bot.cs`, `C:\cbot\learn.cs`.

##### exec
**Findings: plant execution, verification and watchdog (C:\cbot)**

The plant check is a heuristic that guesses wrong both ways: it reports good plants as failures and accepts wrong ones as successes. Nothing watches for a stall during play (scene 3). `watch.ps1` cannot see a bot stuck in a loop that keeps logging events.

## 1. How a plant action works now

**`TryPlant`, bot.cs:607-659**
- **Bowling** (608-619): clicks the packet, sleeps 80ms, clicks `CellX(2),CellY(row)`. It then counts the plant as done with `Viz.Planted++` and `justPlanted.Add`. Nothing checks it.
- **Pro mode** (622-628): `ProDecide` runs, then `ExecutePlant` if 120ms have passed since `lastPlant`.
- **Learn mode** (630-658): weighs saving sun against acting, then `ExecutePlant`.

**`ExecutePlant`, bot.cs:660-684**
```
662 var p = act.P; int before = s.Plants.Count;
673 Pvz.FastClick(p.CX, p.CY);  Thread.Sleep(Pvz.UsePost ? 15 : 60);
675 Pvz.FastClick(CellX(act.Col), CellY(act.Row));
677 Thread.Sleep(Pvz.UsePost ? 25 : 80);
679 var s2 = Mem.Get();
680 bool ok = s2.Plants.Count > before || s2.Sun < s.Sun;
683 if (!ok) { Pvz.FastClick(400, 590, true); Bad.Add(act.Row * 10 + act.Col); }
```
- There is one read and no retry. A failure right-clicks to drop the held seed, then adds the cell to `Bad` (bot.cs:49).
- `Bad` is only cleared in `ResetLevel` (bot.cs:514). `Recover` (bot.cs:508) does not clear it.
- Both brains skip `Bad` cells: Learn at bot.cs:728 and 907, Pro at pro.cs:164.

**Dropped packets:** a lawn packet becomes a fake `Pkt` with `FromCoin=true` and `CoinX/CoinY` (bot.cs:740, pro.cs:157). `Pkt.CX/CY` (mem.cs:17-18) gives the coin position from the snapshot taken at the start of the tick.

**Conveyor packets:** `CX = BankX + X + OffsetX + W/2`, read from the start-of-tick snapshot while the belt keeps moving (bot.cs:737, mem.cs:164).

## 2. Defects, with evidence

**D1. Counting plants gives false failures when anything leaves the board at the same time.**
- `Plants.Count > before` fails whenever a plant dies, a squash jumps, a mine explodes, or a bowled nut leaves in the same ~100ms window.
- Free seeds (dropped, conveyor, cost 0) never lower sun, so the sun check cannot rescue them.
- Evidence from the last 20,000 lines of events.log: 7,754 PLANT and 587 fail. 388 of the fails (66%) are SQUASH and 383 have the "packet:" (dropped-packet) reason.
- Example: `22:22:19 fail SQUASH r4c7` is followed by `! lost SQUASH at r2c7` in the same second. The earlier squash jumped and was removed, so the count stayed flat.
- Each false failure adds a good cell to `Bad` for the rest of the level. On Vasebreaker that includes later vase rounds, where plants are wiped but `Bad` is kept.

**D2. False successes.**
- `ok` is true if any plant count rises or sun drops. A seed landing in the wrong cell counts as success. So does the wrong packet: the conveyor slid, or a dropped coin was still bouncing.
- With PostMessage input (`UsePost`, pvz.cs:43 and 53-58), clicks are queued and handled later. A sun drop from the previous action's delayed click can be credited to this one.
- Nothing compares type, row or column.

**D3. Async input timing.** With `UsePost`, the check runs 25ms after `PostMessage` with no guarantee the game has handled the click. At game speed x1 to x2 (Vasebreaker, bot.cs:79), 25ms is about 2-5 game frames. Wall-clock sleeps are the wrong unit; `State.GameTime` (mem.cs:99, mMainCounter/100) is the right one.

**D4. The cursor state is never checked before or after.**
- `Mem.CursorType()` (mem.cs:231-234) returns 0 for nothing held, 1 for a bank seed, 2 for a dropped packet and 6 for the shovel.
- `ExecutePlant` never checks it. If the shovel is still held after a failed `ShovelFirst` (bot.cs:665-671), the "packet click" does something else.
- The packet click is never confirmed, so a missed pickup cannot be told apart from a cell that refuses the plant. Today both poison the cell.

**D5. The bowling path (bot.cs:614-617) has no verification and counts every attempt as planted.**
- Nut position comes from a stale `OffsetX`.
- The 700ms gap (bot.cs:611) is the only protection against re-firing on a nut that was never picked up.

**D6. `Learn.PacketsTaken.Add(p.CoinId)` (bot.cs:664) runs before success is known.** If the pickup fails and the packet later fades, learn.cs:71 removes the id and skips the fade sample. The fade-time model loses data.

**D7. Squash is not treated as "consumed by design".** It is missing from the list at bot.cs:567 (mines, instants, GRAVE, HYPNO), so every squash that jumps logs `! lost SQUASH` and `Strategy.PlantsLost++`. That skews strategy statistics. It is also the trigger behind D1.

**D8. Two smaller issues.**
- After `ShovelFirst`, the code sets `before = Mem.Get().Plants.Count` (bot.cs:670) but never checks the dug cell is empty. If the dig failed, the next plant lands on top or fails, and the cell is poisoned.
- `Bg` (bot.cs:41) sets `CellY` row heights and is only updated in scenes 2/3 (bot.cs:118). On the first tick of a new board type, rows can be wrong. Low risk.

## 3. Proposed `ExecutePlant` verification (replaces bot.cs:672-683)

Data available at this point: `s.Plants` (Id/Row/Col/Type, mem.cs:126-130), `s.Packets[].Cd` (the refreshing flag, which flips to 1 right after a bank plant), `s.Suns[].Id` (a dropped packet's Id disappears once used), and `Mem.CursorType()`.

```csharp
var oldIds = new HashSet<uint>(s.Plants.Select(q => q.Id));
// 1) pick up, confirm via cursor (poll by game time, not wall time)
Pvz.FastClick(p.CX_fresh, p.CY_fresh);              // re-read the coin/belt position right before clicking (see 4)
int want = p.FromCoin ? 2 : 1;
if (!WaitGame(() => Mem.CursorType() == want, 0.30)) { L("pickup miss " + act.Label); return; }   // NOT a Bad cell
// 2) place, then confirm the EXACT plant appeared
Pvz.FastClick(CellX(act.Col), CellY(act.Row));
Plt landed = null; State s2 = null;
WaitGame(() => { s2 = Mem.Get(); landed = s2.Plants.FirstOrDefault(q => !oldIds.Contains(q.Id)); return landed != null || Mem.CursorType() == 0; }, 0.40);
bool exact = landed != null && landed.Row == act.Row && landed.Col == act.Col && TypeMatches(landed.Type, p.Type);
```
- `WaitGame(pred, gameSecs)` polls every ~5ms until `Mem.Get().GameTime` has moved `gameSecs` past the start. Cap it at ~500ms wall time so a paused game cannot hang it.
- `TypeMatches` must allow Imitater (it plants as its own type, then transforms). Squash/instant/mine must also count if they show up then vanish: treat "a new Id appeared at any point during the poll" as success, so record it on first sight.
- Corroborating signals, any one is enough:
  - bank seed: `s2.Packets[slot].Cd == 1`, or sun fell by exactly `p.Cost`
  - dropped packet: `CoinId` no longer in `s2.Suns`
  - conveyor: that packet slot has gone or shifted

**Outcomes:**
- **Exact:** `justPlanted.Add(...)`, then the PLANT event.
- **Landed in the wrong cell or with the wrong type:** log `MISPLANT want rRcC got rR'cC' type T`. Do not add to `Bad`. Feed the error into a click-offset calibration, an `(dRow, dCol)` histogram that flags a `CellY`/`Bg` mismatch.
- **Nothing landed, cursor still holding:** the cell refused. Right-click to drop, then mark the cell bad with a timestamp: `Bad[k] = GameTime + 15`, not permanent. Change `Bad` to a `Dictionary<int,double>` and filter `Bad.TryGetValue(k, out t) && t > s.GameTime` at bot.cs:728 and 907 and pro.cs:164.
- **Nothing landed, cursor empty:** the packet was consumed somewhere unknown or the click was eaten. Log it and leave `Bad` unchanged.
- Also: on `VaseRound` changes, clear `Bad` and `shoveled`.

**Retry rule:** allow one immediate retry, only for a pickup miss, with fresh coordinates. Never retry the same cell within the same tick.

**Cost:** each `Mem.Get()` is a full board read (`TRead`, logged in "perf:" lines). In the poll, call only the plant `ForEach` plus `CursorType`. Add a `Mem.Plants()` light reader that reads just `board+0xA0`, the way mem.cs:126 does.

**Threading and static state:** everything runs on the main loop thread (bot.cs:101). `Viz` runs separately, so do not touch `Bad` from Viz. All the static state shown here survives within a run. A stop.flag hot reload (bot.cs:102) rebuilds it, so the new `Bad` dictionary needs no persistence.

## 4. Dropped and conveyor packets

- **Fresh coordinates:** inside `ExecutePlant`, re-read the coin (`Mem.Get().Suns.First(c => c.Id == p.CoinId)`, or a light coin reader) or the belt slot's `OffsetX` just before clicking. The current position can be ~100ms of motion old.
- If the coin Id has gone (faded or collected), abort quietly: no fail event, no `Bad`.
- Move `Learn.PacketsTaken.Add` (bot.cs:664) to after a confirmed `CursorType()==2`.
- **Bowling (614-617):** apply the same cursor-confirm, then `Plants` new-Id check. A nut leaves its cell by design, so check for a new Id, not cell occupancy.

## 5. In-bot watchdog (`Recover`, bot.cs:503-512)

**Triggers today, all time-in-scene only:**
- Dave talking >25s (bot.cs:138)
- scene 4 >60s (bot.cs:174)
- scene 5 >40s (bot.cs:192)
- `ChooseSeeds` returns false (bot.cs:198)
- not in scene 3 and >45s (bot.cs:202)

**Gaps:**
- Nothing covers scene 3 (actual play). A frozen game clock, a stuck held cursor, a dialog the code does not recognise, or a loop of fails or pickup misses is never caught.
- Event analysis shows 0 WATCHDOG lines in the last 20,000 events while 587 fails happened.
- `Recover` does not clear `Bad`, `justPlanted` or `shoveled`, so the wrong assumptions behind a stall survive the reset.

**Proposed: add a progress tracker in the main loop just after `Track(s)` (bot.cs:212):**
```csharp
static double progGameT; static DateTime progWall = DateTime.Now; static string progKey = "";
string key = s.Level + "|" + s.Wave + "|" + Viz.Kills + "|" + Viz.Planted + "|" + s.Plants.Count;
if (s.GameTime > progGameT + 0.5 || key != progKey) { if (key != progKey) progWall = DateTime.Now; progGameT = s.GameTime; progKey = key; }
bool clockFrozen = (DateTime.Now - lastClockMove).TotalSeconds > 10 && !s.Paused;  // GameTime not advancing
bool noOutcome = (DateTime.Now - progWall).TotalSeconds > 90;  // clock runs but nothing planted/killed/waved
bool cursorStuck = Mem.CursorType() is 1 or 2 or 6 for > 5s while no action pending;
bool failLoop = fails in last 20 actions >= 10;
```
- Any of these calls `Recover(3)` with the reason added to the WATCHDOG text.
- Extend `Recover` to: clear `Bad`, `justPlanted` and `shoveled`; right-click to drop; send Escape or Unpause if `s.Paused`; and note `Mem.OpenDialogs()` plus `DialogText()` in the event.
- Have the in-level watchdog check `Mem.DaveState()` and `OpenDialogs()` first so it does not fight their handlers.
- **Cost:** string building once per tick is negligible next to `Mem.Get()`.

**Heartbeat for the external watcher:** every ~5s, write C:\cbot\progress.txt with `scene, lvl, wave, gameTime, kills, planted, fails, lastPlantOkUtc, watchdogs`. Write to a temp file and swap it in (`File.Replace`) so a reader never sees half a file.

## 6. `watch.ps1` flaw and fix

**Current signal, watch.ps1:28:**
```
$sig = "$lvl|$wave|$($ev.Count)"
```
- `$ev.Count` (the number of events.log lines) counts as progress whenever anything is logged, including failure loops:
  - `fail ...` lines, about one per 120ms of trying (bot.cs:682)
  - `MENU main menu -> Adventure` every 5s (bot.cs:158)
  - `STORE:` every 4s (bot.cs:141)
  - `DIALOG` every 2s (bot.cs:468)
  - the WATCHDOG events themselves (bot.cs:507)

  So a bot stuck in any of these loops never shows STUCK. Only a completely silent hang is detected.
- `lvl` and `wave` come from the last `scene=` line in the last 60 lines of bot.log (watch.ps1:25). `L(s.Text())` only runs in scene 3 (bot.cs:122-123). Outside play the values are stale, or 0/'?' once the tail has scrolled past.
- watch.ps1:18 reads the whole 4.2MB events.log every run. That is slow, and `lines` misbehaves if the file is rotated (handled only by the reset when it shrinks, :19).
- `STUCK` (:37) only prints. It never relaunches or recovers.

**Fix:** read the heartbeat file and use real progress:
```powershell
$hb = @{}; Get-Content C:\cbot\progress.txt | % { $k,$v = $_ -split '=',2; $hb[$k]=$v }
$sig = "$($hb.lvl)|$($hb.wave)|$($hb.kills)|$($hb.planted)|$([math]::Floor([double]$hb.gameTime/30))"
$hbAge = ((Get-Date) - (Get-Item C:\cbot\progress.txt).LastWriteTime).TotalSeconds
```
- **STUCK** if `$hbAge > 60` (the bot thread is hung even though run.ps1 is alive), or the signature has not changed for `$StuckMin`.
- **Fail-loop alarm** if `fails` rose by more than 50 while `planted` rose by less than 5 since the last check.
- On STUCK with a stale heartbeat: write stop.flag, which triggers the hot reload at bot.cs:102. If the heartbeat is still stale on the next run, kill and relaunch run.ps1.
- Replace `Get-Content events.log` with `Get-Content -Tail 2000` for the RESULT/WATCHDOG counts. Use the file length for the "+N events" delta.

## 7. Small fix list

| Where | Fix |
|---|---|
| bot.cs:567 | Add SQUASH (17) or `Roles.Is(pt,"SQUASH")` to consumedByDesign |
| bot.cs:664 | Move `PacketsTaken.Add` after pickup is confirmed |
| bot.cs:670 | After the shovel, check the cell is empty in `Mem.Get()`. If not, abort without poisoning the cell |
| bot.cs:680 | Replace the count/sun check with the new-Id + exact row/col/type check (section 3) |
| bot.cs:683 | Make `Bad` time-limited (`Dictionary<int,double>`, expiry in GameTime) and only mark it when the cursor still holds a seed after placing |
| bot.cs:508 | `Recover` also clears `Bad`, `shoveled` and `justPlanted` |
| bot.cs:614-617 | Bowling: cursor-confirm, fresh `OffsetX`, `Viz.Planted++` only on success |
| bot.cs:212 | In-level stall watchdog plus progress.txt heartbeat |
| watch.ps1:18, 25-28, 37 | Read the heartbeat, use the new signature, tail reads only, act on STUCK |

##### mem
# mem.cs memory reader: field map for replay snapshots and squash judging (read-only audit)

Every array stride, id offset and dead-flag offset in the `ForEach` calls was checked against the game code in `C:\cbot\disasm.txt` (the `Board::Iterate*` and `GetGridItemAt` functions). They are all correct. Offsets marked **[vanilla]** are not in `layout.txt`; they are inferred from the original PvZ layout and fit the checked stride, id and dead offsets.

## 1. What `Mem.Get()` reads now (mem.cs:86-175)

**Pointers**
- gLawnApp is at module base + 0x474860 (`RVA_gLawnApp`, mem.cs:58).
- Board pointer is app+0x6F0 (mem.cs:91). Scene is app+0x780 (mem.cs:90).

**Board header** (one read of 0xC4 bytes at board+0x31468, mem.cs:94-99)

| State field | Offset | Game field |
|---|---|---|
| Level | +0x31468 | |
| Sun | +0x31478 | |
| Waves | +0x3147C | |
| GameTime | +0x31480 | mMainCounter / 100 |
| Wave | +0x31494 | |
| Tutorial | +0x3149C | |
| Complete | +0x31518 | |

Also read:
- Background: +0x31464 (mem.cs:95)
- Paused: +0x15C (mem.cs:100)
- GridSquareType: +0x160, column-major, (c*6+r)*4 (mem.cs:101-102)
- Fog: +0x4C0 int[9][7]; mFogOffset +0x5C8, only when Background==3 (mem.cs:113-125)
- `RowTypes()`: +0x5D0 (mem.cs:252)

**Zombies** (board+0x84, stride 0x18C, id at +0x188, skipped if mDead +0xF4; mem.cs:105-110; checked at disasm:62940-62960)

| Field | Offset | Type |
|---|---|---|
| Row | 0x1C | |
| Type | 0x24 | |
| X | 0x2C | float |
| Y | 0x30 | float |
| Hp | 0xCC | mBodyHealth |
| MaxHp | 0xD0 | |
| Helm | 0xD4 | |
| Shield | 0xE4 | |

**Plants** (board+0xA0, stride 0x150, id at +0x14C, skipped if mDead +0x141; mem.cs:126-130)
- Read: Row 0x1C, Type 0x24, Col 0x28, Hp 0x40, MaxHp 0x44.

**Coins / suns / dropped packets** (board+0xD8, stride 0xE0, id at +0xD8, skipped if mDead +0x38 or mIsBeingCollected +0x50; mem.cs:131-136)
- Read: W 0x10, H 0x14, PosX 0x24 (float), PosY 0x28 (float), Type 0x58, Motion 0x5C, Seed 0x68 (mUsableSeedType).
- `IsPacket` is Type 23 or 24 (mem.cs:12).
- bot.cs:740 and pro.cs:157 turn dropped packets into `Pkt{FromCoin=true}`.

**Lawn mowers** (board+0xF4, stride 0x48, id at +0x44, dead +0x30; mem.cs:138-142)
- Read: row 0x14, mMowerState 0x2C.

**Grid items** (board+0x110, stride 0xEC, id at +0xE8, dead +0x20; mem.cs:144-154; checked at disasm:26179-26212)
- Read: type +0x8, gridX +0x10, gridY +0x14.
- Types: 1 grave, 2 crater, 7 vase. Type 7 is confirmed by `GetScaryPotAt` pushing 7 (disasm:26310). The mod adds wood ladder 0xD and red ladder 0xE (disasm:26290, 26300).
- `look` at +0xC is **mGridItemState**: 4 = leaf vase, 5 = skull vase (mem.cs:151-153). State 3 (question-mark vase) has no set of its own; it is `Vases − LeafVases − SkullVases`.
- **mScaryPotType is not read.** It and the other hidden contents (mZombieType, mSeedType) sit at about +0x3C/+0x40/+0x44 **[vanilla]**. They are inside the copied `it` byte buffer but nothing reads them. Keep it that way.

**Seed bank** (pointer at board+0x138, read 0x350 bytes; mem.cs:156-173)
- Bank X +8, Y +0xC, count +0x24.
- Packet i starts at 0x28 + i*0x50. Read: X +8, Y +0xC, W +0x10, H +0x14, OffsetX +0x30, Type +0x34, mActive +0x48, mRefreshing +0x49.
- Cost comes from gPlantDefs (base + 0x467C60, seed*0x24 + 0x10).

**Other reads in mem.cs**

| Function | Address | mem.cs line |
|---|---|---|
| TimeScale (read/write double) | app+0x458 | 179-182 |
| Seed chooser | app+0x700; cards at 0x98 + i*0x3C (seed +0x20, state +0x24); counts 0xE74 / 0xE7C / 0xE80 | 186-197 |
| DaveState | app+0x7C8 | 200 |
| DaveText | std::string at app+0x7D8 | 201-207 |
| Dialogs | list at app+0x314, size app+0x318; text at dlg+0x94 and +0xC4 | 216-222 |
| Cursor type | board+0x12C → +0x30 | 231-234 |
| Health bars option | app+0x57A | 237 |

## 2. Game clock for deterministic replay

- **`Board::mMainCounter` (+0x31480) is the clock to use.**
  - It increases by 1 per game tick, in `Board::UpdateGame` only (disasm:47861, `inc [esi+31480h]`).
  - Before that it checks scene==3 (`cmp [eax+780h],3`, disasm:47853), with one cutscene exception. So it stands still in the seed chooser, the intro and pause.
  - It is set to 0 when a new board starts (disasm:24604, 30459).
  - 100 ticks = 1 game second at x1. It already exists as `State.GameTime`, but as a double divided by 100; keep the raw int in snapshots.
- **mEffectCounter (+0x31484)** increases in `Board::Update` (disasm:48751), which includes non-gameplay frames. Do not use it for replay.
- **Per-object clocks:** `Zombie::mZombieAge` +0x60 (ticks since spawn) and `Coin::mCoinAge` +0x4C.
- **Ids are stable:** DataArray id = (key<<16)|index, and the key keeps increasing within a board. This makes `Zom.Id` and `Plt.Id` safe to use for tracking across snapshots.
- **Sampling rate:** the bot loop runs at about 16-21 Hz (bot.log: `perf: 19 Hz | read 3.7 ... act 14.5 ms`). At x1 that is roughly every 5 ticks; with TimeScale raised it is every 10 or more. Short states can be missed (see section 4).

## 3. Fields available but not read

**Zombie** (layout.txt:131-224)

| Field | Offset | Why it matters |
|---|---|---|
| mZombiePhase | 0x28 | **Phases 1, 2, 3 mean dying, burned, mowered**: `IsDeadOrDying` checks mDead OR phase in {1,2,3} (disasm:230234-230246) |
| mVelX | 0x34 | |
| mIsEating | 0x51 | Confirmed: `StartEating` sets byte +0x51 (disasm:218538, 218544) |
| mJustGotShotCounter | 0x54 | Shows recent hits |
| mZombieAge | 0x60 | Ticks since spawn |
| mFromWave | 0x70 | |
| mSquashForgotJob | 0x76 | |
| mTargetCol | 0x84 | |
| mAltitude | 0x88 | |
| mZombieRect | 0x90 | TRect, hitbox |
| mChilledCounter | 0xB0 | Squash target lead uses it (disasm:396954) |
| mButteredCounter | 0xB4 | |
| mIceTrapCounter | 0xB8 | |
| **mMindControlled** | 0xBC | Confirmed: `StartMindControlled` sets +0xBC=1 (disasm:219436) |
| mHasHead | 0xBE | |
| mInPool | 0xC1 | |
| mHelmType | 0xC8 | |
| mHelmMaxHealth | 0xDC | |
| mShieldType | 0xE0 | |
| mShieldMaxHealth | 0xE8 | |
| **mFlyingHealth / Max** | 0xEC / 0xF0 | Balloon zombies |
| mTargetPlantID | 0x130 | The plant it is eating |
| mBeingMowed | 0x180 | |

**Plant** (layout.txt:225-276)

| Field | Offset | Notes |
|---|---|---|
| mX / mY | 0x8 / 0xC | GameObject; the squash animation moves mY (disasm:397266) |
| **mState** | 0x3C | Squash and potato states below |
| mDoSpecialCountdown | 0x50 | |
| **mStateCountdown** | 0x54 | |
| **mTargetX** | 0x80 | Squash landing x |
| mPlantRect | 0x60 | |
| mPlantAttackRect | 0x70 | |
| mRecentlyEatenCountdown | 0xB4 | |
| **mTargetZombieID** | 0x12C | |
| mSquished | 0x142 | |
| mIsAsleep | 0x143 | |
| mImitaterType | 0x138 | |

- Squash `mState` path (UpdateSquash, disasm:396945-397458): 0 idle → **3 LOOK** (countdown 80) → **4** → **5** → **6** → **7 DONE**, then `Die` after a countdown of 100.
- Potato `mState` (UpdatePotato, disasm:395457, 395592): 0 not ready → **15 (0x0F) rising** → **16 (0x10) armed**.
- `mTargetZombieID` is written on squash lock-on: `mov ecx,[esi+188h]; mov [edi+12Ch],ecx` (disasm:396952-396953). It is the zombie's DataArray id, i.e. the same number as `Zom.Id`.

**Seed packet**
- mRefreshCounter +0x24 and mRefreshTime +0x28 give exact cooldown progress. Today `Pkt.Cd` is only 0 or 1 (mem.cs:168).
- mTimesUsed +0x4C; bank mConveyorBeltCounter +0x34C.

**Coin**
- mCoinAge +0x4C and mDisappearCounter +0x54 tell when a dropped packet or sun will expire.
- mHitGround +0xCA.

**Projectiles** (board+0xBC; not read anywhere)
- Checked: stride 0x94, id +0x90, mDead +0x50 (disasm:63013-63041).
- **[vanilla]** fields: mPosX 0x30, mPosY 0x34, mVelX 0x3C, mMotionType 0x58, mProjectileType 0x5C, mTargetZombieID 0x88. Row is GameObject +0x1C.

**Board**
- mZombieCountDown 0x314B8, mHugeWaveCountDown 0x314C0
- mZombieHealthToNextWave 0x314B0
- mIceMinX 0x604
- mWaveRowGotLawnMowered 0x5E8
- mBoardRandSeed 0x31538 (useful to log for replay)
- mPlantsEaten 0x316B4

**Missing layouts:** GridItem, Projectile, LawnMower, CursorObject, and the ZombiePhase / PlantState / GridItemState enums are not in `layout.txt`. Dump them (not run, per read-only) with:

`C:\cbot\pdbdump\pdbdump.exe "C:\Users\User\Downloads\Modded Plants VS Zombies\Release\LawnProject.pdb" class Projectile GridItem LawnMower CursorObject`

and the same with `enum ZombiePhase PlantState GridItemState ProjectileType`.

## 4. Judging squash hits from snapshots

- **Lock-on:** a squash Plt has mState 0→3 and mTargetZombieID = the target `Zom.Id`. Record mTargetX and the target's X, Hp+Helm+Shield and phase at that moment.
- **Damage:** dealt during the falling branch (disasm:397267-397364) to every zombie in the plant's attack rect.
  - Only zombies in the same row are hit, except type 0x22 = 34 (BOSS) (disasm:397288-397292).
  - Each hit is `TakeDamage(0x708=1800, flags 0x12)` (disasm:397332-397334).
  - **There are two `TakeDamage` calls per zombie**, with the overlap tolerance switched for type 0x0A (FOOT) and type 0x0B (REGEN) (disasm:397326, 397356). This looks like a mod double-hit, up to 3600 damage. Check it against hp drops in a replay before trusting it.
  - On a pool square the squash dies straight away (`IsPoolSquare`, then `Die`; disasm:397376-397403).
- **Result:** check the target id in the next 2-3 snapshots.
  - A hit means one of: Total dropped by at least 1800, or the id vanished, or mZombiePhase is 1-3.
  - A miss means the zombie is alive with roughly the same hp when the squash Plt disappears (state 7 then Die), or the squash's target id no longer matches anything in its row.
- **Timing:** LOOK lasts 80 ticks (0.8 s at x1). The later states are short (one countdown is 10 ticks, disasm:397223). At 20 Hz polling and raised TimeScale you can miss states 4-6. Judge by the zombie's hp before and after across the squash's lifetime, not by catching state 6.

## 5. Defects

1. **Dying, burned and mowered zombies count as live.** The filter is only `it[0xF4]` (mem.cs:106), but the game's own `IsDeadOrDying` also checks phase 1/2/3. Mowered zombies may still have hp; `prosim.cs:84` only removes `Hp<=0`. Effect: phantom threats, and a "hit" could be judged where the zombie was already dying.
2. **Hypnotized zombies are treated as enemies.** mMindControlled +0xBC is never read.
3. **Balloon hp is understated.** `Zom.Total` (mem.cs:9) leaves out mFlyingHealth +0xEC.
4. **`Pkt.Cd` is only 0 or 1** (mem.cs:168), though exact refresh progress is at +0x24/+0x28.
5. **Every `Mem.*` call runs `Attach()`, which runs `Process.GetProcessesByName`** (mem.cs:61). A single loop calls `Get`, `OpenDialogs`, `DaveState` and others (bot.cs:106, 129, 134), so this repeats several times per loop: process enumeration and garbage on every call. This probably accounts for much of the 3.5-5 ms read time.
   - `Attach` also throws "game not running" when `MainWindowHandle` is 0 (mem.cs:62, 69), e.g. during loading.
   - `hProc` is never closed when the pid changes.
6. **Reads are not atomic.** Each `Get()` makes about 8 separate ReadProcessMemory calls, so zombies and plants can come from different ticks. For replay, store mMainCounter both before and after the read, or read it once and accept up to 1 tick of skew.
7. **`RowTypes()` does not check for a null board** (mem.cs:251-252): it reads from address 0x5D0 and throws.
8. **The fog column formula** `(z.X + 40 - 40 - fogOff)/80` (mem.cs:119) has a redundant +40-40. It does not match what the game does, so whether it hides the right zombies is unverified.
9. **`recorder\memro.cs` is a hand-made copy of mem.cs.** It differs only in read-only access rights and the Blocked line, so any new field must be added in both places.
10. **Plant X (+0x8) is not read**, and during the squash jump the plant's mY changes. If anything locates the squash by pixel position rather than Col/Row, it will be wrong mid-jump.

## 6. Hook points for snapshots

- Add the new fields to the existing per-item lambdas in `Mem.Get()`: zombie at mem.cs:107-109, plant at 128-129. The whole item is already in `it` (`BlockCopy`, mem.cs:265), so extra fields cost no more ReadProcessMemory calls.
- Add a projectile `ForEach(board+0xBC, 0x94, 0x90, ...)` that skips items where `it[0x50]` is set.
- Store raw mMainCounter as an int: `BitConverter.ToInt32(bh, 0x18)` (mem.cs:99).
- **Pitfalls:**
  - Everything in Mem is static and not thread-safe (`hProc`, `pid`, `costCache`, `BankX`/`BankY` are static fields, mem.cs:55-56, 176). A recorder thread must not call `Mem.Get()` at the same time as the bot loop without a lock.
  - After a hot reload (stop.flag, bot.cs:102) the static state starts fresh, but snapshot timestamps carry on correctly because mMainCounter lives in the game.