// Real-time PvZ bot driven by memory state (Mem) and mouse input (Pvz).
// Every decision is a scored candidate list, published to Viz for the live panel.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

public class Cand {
  public Pkt P; public int Row, Col; public double Score; public string Why; public bool Affordable, OutOfReach;
  public bool ShovelFirst;   // dig up the plant in this cell before planting
  public string Label { get { return Mem.SeedName(P.Type) + " r" + Row + "c" + Col; } }
}

public static partial class Bot {
  static StreamWriter log;
  static void L(string s) {
    string line = DateTime.Now.ToString("HH:mm:ss") + " " + s;
    log.WriteLine(line); log.Flush();
  }
  static void Event(string s) { L(s); Viz.AddEvent(s); }

  static readonly HashSet<int> Shooters = new HashSet<int> { 0, 5, 7, 18, 40, 28, 32, 34, 39, 44, 26, 29, 10, 13, 50 };
  static readonly HashSet<int> SunMakers = new HashSet<int> { 1, 9, 41 };
  static readonly HashSet<int> Walls = new HashSet<int> { 3, 23 };
  static readonly HashSet<int> Instants = new HashSet<int> { 2, 17, 20, 15 }; // cherry, squash, jalapeno, doom
  const int MINE = 4;
  // Plant categories come from learned roles (vanilla beliefs as a starting point, overturned by evidence).
  static bool IsShooter(int t) { return Roles.Is(t, "SHOOT"); }
  static bool IsSun(int t) { return Roles.Is(t, "SUN"); }
  static bool IsWall(int t) { return Roles.Is(t, "WALL"); }
  static bool IsInstant(int t) { return Roles.Is(t, "INSTANT"); }
  static bool IsMine(int t) { return Roles.Is(t, "MINE"); }
  static bool IsMelee(int t) { return Roles.Is(t, "MELEE"); }
  // Rough damage per second per plant; repeaters and gatlings fire more peas.
  static double Dps(int t) { return t == 7 || t == 50 ? 28 : t == 40 ? 56 : t == 18 ? 14 : 14; }
  const double ZOMBIE_SPEED = 16; // px/s, slow walkers in the day levels

  public static int CellX(int col) { return 40 + col * 80 + 40; }
  // Day/night lawns have 5 rows of 100px; pool and fog lawns have 6 rows of 85px (same top edge).
  public static int Bg;
  public static int CellY(int row) { return (Bg == 2 || Bg == 3) ? 80 + row * 85 + 45 : 80 + row * 100 + 55; }
  static int ColOf(float zx) { return Math.Max(0, Math.Min(8, (int)((zx + 30 - 40) / 80))); }

  // Tracking for the panel's stats.
  static Dictionary<uint, int> zHp = new Dictionary<uint, int>();
  static Dictionary<uint, DateTime> sunSeen = new Dictionary<uint, DateTime>();
  static HashSet<int> plantKeys = new HashSet<int>();
  static HashSet<int> Bad = new HashSet<int>();   // cells that refused a plant (grave, crater, etc.)
  static HashSet<int> justPlanted = new HashSet<int>();
  static int[] lastMowers = { -1, -1, -1, -1, -1, -1 };
  static readonly Dictionary<int, DateTime> shoveled = new Dictionary<int, DateTime>();   // cells we dug up ourselves
  static DateTime lastShovel = DateTime.MinValue;
  // Level progress as shown by the on-screen progress bar: flags come every 10th wave.
  public static int WavesToFlag(State s) { if (s.Waves <= 0) return -1; int next = ((s.Wave / 10) + 1) * 10; return next <= s.Waves ? next - s.Wave : -1; }
  public static double Progress(State s) { return s.Waves > 0 ? (double)s.Wave / s.Waves : 0; }
  static bool mowerLostThisLevel, loadoutPending, stratChosen;
  static int lastWave, lastWaves;
  static DateTime lastSpeedCheck = DateTime.MinValue, lastHbCheck = DateTime.MinValue;
  static int awardClicks;
  public static double Speed = 1, UserSpeed = 1;
  public static bool SlowMo, AllowSlowMo = false;
  // Per-stage timing (ms, smoothed) so the loop can be profiled live.
  public static double TRead, TTrack, TCollect, TScore, TAct;
  static double Ema(double old, double v) { return old * 0.9 + v * 0.1; }
  public static double Panic;
  public static bool PrecisionLevel; static int precisionLvl = -1;   // 0..1, set by the decision code from danger, flag timing and how close zombies are
  // Game speed from C:\cbot\speed.txt, applied through the engine's own time scale.
  static void ApplySpeed() {
    double want = 1;
    try { want = double.Parse(File.ReadAllText(@"C:\cbot\speed.txt").Trim(), System.Globalization.CultureInfo.InvariantCulture); } catch { }
    want = Math.Max(0.25, Math.Min(20, want));
    UserSpeed = want;
    // Slow-mo: when things get dangerous (or a flag wave is about to hit) the bot cannot react fast enough at high speed.
    bool panic = AllowSlowMo && Panic > 0.5;   // off by default: the bot should simply think faster
    if (panic && want > 2) want = 2;
    // Precision levels (Vasebreaker: squash a football in the second it stops to eat) are not playable at 5x reaction-wise:
    // cap at x2 only while vases are on the lawn; every other level keeps the speed the user chose.
    if (PrecisionLevel && want > 2) want = 2;   // (x1 tested: no better than x2, and half as many attempts per hour)
    if (panic != SlowMo) { SlowMo = panic; Event(panic ? "SLOW-MO on: danger is high, dropping to x" + want.ToString("0.#") : "SLOW-MO off: back to x" + want.ToString("0.#")); }
    double cur = Mem.TimeScale;
    if (Math.Abs(cur - want) > 0.01) { Mem.TimeScale = want; Event("game speed x" + cur.ToString("0.##") + " -> x" + want.ToString("0.##")); }
    Speed = want;
  }
  static Dictionary<int, int> lastPlantType = new Dictionary<int, int>();
  [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
  [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

  public static string Run(int secs, string logPath) {
    // Rotate at start-up once the log passes 20 MB (time stamps have no date; readers scan the whole file).
    try { var fi = new FileInfo(logPath); if (fi.Exists && fi.Length > (20L << 20)) { File.Copy(logPath, logPath + ".1", true); File.Delete(logPath); } } catch { }
    log = new StreamWriter(logPath, true);
    Learn.Load(); Strategy.Load(); Roles.Load(); Diag.Load();
    // The game eats most of the CPU at high speed; make sure the bot is not starved.
    try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; } catch { }
    Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
    Viz.Start();
    try { timeBeginPeriod(1); } catch { }   // 1 ms timer: Sleep(2) otherwise takes 15.6 ms (every plant cost ~78 ms of sleeps)
    lightStop = false;
    var lightThread = new Thread(LightLoop); lightThread.IsBackground = true; lightThread.Priority = ThreadPriority.BelowNormal; lightThread.Start();
    L("bot start");
    try { ProMode = File.Exists(@"C:\cbot\mode.txt") && File.ReadAllText(@"C:\cbot\mode.txt").Trim().ToLower() == "pro"; } catch { ProMode = false; }
    if (ProMode) { ProTally(); Event("PRO MODE: expert brain playing - nothing is learned or saved to knowledge in this mode (pro record so far: " + ProWins + " wins, " + ProDeaths + " deaths)"); }
    var end = DateTime.Now.AddSeconds(secs);
    DateTime lastLog = DateTime.MinValue, lastPlant = DateTime.MinValue, sceneSince = DateTime.Now, lastMenuClick = DateTime.MinValue;
    string result = "timeout"; int lastScene = -1, losses = 0, lastLevel = 0;
    var loopTimer = System.Diagnostics.Stopwatch.StartNew(); int loops = 0;
    while (DateTime.Now < end) {
      if (File.Exists(@"C:\cbot\stop.flag")) { File.Delete(@"C:\cbot\stop.flag"); result = "hot reload"; try { Replay.Dump("RELOAD", -1, ""); } catch { } break; }
      if ((DateTime.Now - lastSpeedCheck).TotalSeconds > 0.5) { lastSpeedCheck = DateTime.Now; ApplySpeed(); }
      try {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = Mem.Get();
        // Blackout (mod author, 4-10: "when the screen is black make the bot unable to see"): sample the real screen over the
        // lawn every 0.25 s; while it is black, every zombie is invisible to the bot exactly as to a player (kept as fog
        // bookkeeping so they don't count as kills). Works on any level that goes dark.
        if (s.Scene == 3 && s.HasBoard && !s.Paused) {
          // The screen grabs run on LightLoop's own thread (3 CopyFromScreen calls stalled this loop 15-90 ms each time);
          // here we only read the latest sample.
          lightWanted = true; lightEveryMs = s.Level == 40 ? 250 : 500;   // 4-10 is the blackout level: look more often there
          if (lightNow != lawnLight || (DateTime.Now - lastDarkCheck).TotalSeconds >= 0.25) {
            lastDarkCheck = DateTime.Now; lawnLight = lightNow;
            bool dark = lawnLight < DARK_LIGHT;
            if (dark != ScreenDark) { ScreenDark = dark; Event(dark ? "BLACKOUT: the screen went black (light " + lawnLight + ") - the bot can't see zombies until it clears" : "light is back (" + lawnLight + ") - zombies visible again"); }
          }
          if (ScreenDark) { s.FogZombies.AddRange(s.Zombies); s.Zombies.Clear(); }
        } else { if (lightWanted) { lightWanted = false; lightNow = 999; lawnLight = 999; } ScreenDark = false; }
        Seen.Frame(s);   // visible-only observation + sim calibration events (mower fire, game over, probes)
        Viz.ReadMs = sw.Elapsed.TotalMilliseconds; TRead = Ema(TRead, Viz.ReadMs);
        if (++loops % 20 == 0) { Viz.LoopHz = loops / loopTimer.Elapsed.TotalSeconds; loops = 0; loopTimer.Restart(); }
        if (s.Scene != lastScene) {
          lastScene0 = lastScene;
          if (s.Scene != 5) winCounted = false;
          L("scene " + lastScene + "->" + s.Scene + " lvl=" + s.Level); lastScene = s.Scene; sceneSince = DateTime.Now;
          Viz.Status = SceneName(s.Scene);
          if ((s.Scene == 2 || s.Scene == 3) && !stratChosen && !ProMode) { stratChosen = true; Strategy.ChooseForLevel(s.Level, s.HasBoard ? s.Background : 0); }
        }
        if (s.HasBoard && s.Level > 0) { lastLevel = s.Level; Learn.CurLevel = s.Level; }
        if ((DateTime.Now - lastHbCheck).TotalSeconds > 3) { lastHbCheck = DateTime.Now; HealthVisible = Mem.ZombieHealthbars(); }
        if (s.HasBoard && (s.Scene == 2 || s.Scene == 3)) { Learn.Env = Math.Max(0, s.Background); Bg = s.Background; }
        if (s.HasBoard) Strategy.LevelEnv = s.Background >= 0 && s.Background < Strategy.EnvNames.Length ? Strategy.EnvNames[s.Background] : "other";
        if (s.Scene == 3 && s.HasBoard) { lastWave = s.Wave; lastWaves = s.Waves; Strategy.LevelEnv = s.Background >= 0 && s.Background < Strategy.EnvNames.Length ? Strategy.EnvNames[s.Background] : "other"; }
        double inScene = (DateTime.Now - sceneSince).TotalSeconds;
        if (s.Scene == 3 && (DateTime.Now - lastLog).TotalSeconds >= 5) {
          L(s.Text()); lastLog = DateTime.Now;
          L(string.Format("perf: {0:0} Hz | light " + lawnLight + " | read {1:0.0} track {2:0.0} collect {3:0.0} score {4:0.0} act {5:0.0} ms", Viz.LoopHz, TRead, TTrack, TCollect, TScore, TAct));
        }

        // A Yes/No dialog outside the seed chooser (e.g. Dave's shop): read it and decide like a player.
        // (game over / award screens have their own handling below; the pause menu is handled by Unpause)
        if (s.Scene != 4 && s.Scene != 5 && !s.Paused && Mem.OpenDialogs() > 0 && !(s.Scene == 2 && Mem.SeedChooserOpen() && Mem.DaveState() == 0)) {
          if ((DateTime.Now - lastDialogAnswer).TotalSeconds > 2) { AnswerDialog(); lastDialogAnswer = DateTime.Now; }
          Thread.Sleep(300); continue;
        }
        // Crazy (Demon) Dave talking: read his line and click to continue, like a player would.
        int dave = Mem.DaveState();
        if (dave >= 3) {
          string said = Mem.DaveText();
          if (said != lastDave && said.Length > 0) {
            Event("DAVE: " + said); Replay.Note("DAVE", said, -1); lastDave = said; daveSince = DateTime.Now;
            if (said.Contains("last of")) { vaseFinalRound = true; VaseRound = 3; VasePlanner.HintRound(3, true); }
            else if (said.Contains("more vases")) { VaseRound = 2; VasePlanner.HintRound(2, false); }
          }
          if ((DateTime.Now - daveSince).TotalSeconds > 25) { Recover(s.Scene); daveSince = DateTime.Now; }   // he wants something we do not understand
          // "NO_CLICK" lines are his store chatter (Crazy Dave's Twiddydinkies): clicking him does nothing - leave via NEXT LEVEL.
          if (said.StartsWith("NO_CLICK")) {
            if ((DateTime.Now - lastStoreExit).TotalSeconds > 4) {
              // Store mapping (darthspiker wants roof cleaners bought): record what the shop looks like + the wallet, once per visit.
              try { int co = Mem.PlayerCoins(); Event("STORE: Dave's shop is open - coins " + co + " ($" + (co * 10) + "), roof cleaner owned=" + Mem.Purchase(23) + ", pool cleaner=" + Mem.Purchase(22) + ", seed slots=" + Mem.Purchase(21) + " - pressing NEXT LEVEL (buying not automated yet)"); Pvz.Shot(@"C:\cbot\store_" + DateTime.Now.ToString("HHmmss") + ".png", 1.0); } catch { Event("STORE: Dave's shop is open - pressing NEXT LEVEL to keep playing"); }
              lastStoreExit = DateTime.Now; Pvz.Click(434, 554); }
            Thread.Sleep(900); continue;
          }
          Pvz.Click(400, 300); Thread.Sleep(900); continue;
        }
        // Shovel tutorial (game asks to pick up the shovel and dig up plants): do what it asks.
        if (s.HasBoard && s.Tutorial >= 15 && s.Tutorial <= 17 && Mem.DaveState() == 0) {   // runs in the intro scene too
          var dig = s.Plants.FirstOrDefault();
          if (dig != null && (DateTime.Now - lastTutorial).TotalSeconds > 1.2) {
            lastTutorial = DateTime.Now;
            Pvz.Click(ShovelX(s), 36); Thread.Sleep(300);
            Pvz.Click(CellX(dig.Col), CellY(dig.Row));
            Event("TUTORIAL: the game asked me to dig with the shovel - dug up r" + dig.Row + "c" + dig.Col);
          }
          Thread.Sleep(200); continue;
        }
        if (s.Scene == 0 && inScene > 4 && (DateTime.Now - lastMenuClick).TotalSeconds > 4 && Mem.OpenDialogs() == 0) {   // title screen: "CLICK TO START!" (stuck_183108.png)
          Event("MENU title screen -> click to start"); Pvz.Click(400, 555); lastMenuClick = DateTime.Now; Thread.Sleep(500); continue;
        }
        if (s.Scene == 1 && inScene > 3 && (DateTime.Now - lastMenuClick).TotalSeconds > 5 && Mem.OpenDialogs() == 0) {   // main menu: go play Adventure
          Event("MENU main menu -> Adventure"); Pvz.Click(560, 130); lastMenuClick = DateTime.Now; Thread.Sleep(500); continue;
        }
        if (s.Scene == 4) {                         // zombies won: count the death once, then keep trying "Try Again"
          if (lastMenuClick < sceneSince && inScene < 1 && lastScene0 != -1) {   // a game over already on screen at startup was counted before the reload
            // The zombie that got through = the enemy closest to the house (a hypnotized one is on our side).
            // Record only (no play decision): fog/blackout-hidden zombies count too, or a death in the dark names no killer.
            var killer = lastPlay == null ? null : lastPlay.Zombies.Concat(lastPlay.FogZombies).Where(z => !z.Charmed && z.X < VISIBLE_X).OrderBy(z => z.X).FirstOrDefault();
            if (killer != null) BreachDiag(lastPlay, killer.Row);   // the lane that got through
            // Super nemesis: remember which kind of zombie got through.
            if (killer != null)
              try { File.AppendAllText(@"C:\cbot\nemesis.txt", DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " " + (ProMode ? "pro" : "learn") + " lvl" + lastLevel + " " + Mem.ZomName(killer.Type) + Environment.NewLine); } catch { }
            // Vase levels have no waves: record the round and vases left instead of a meaningless "wave 0/20".
            bool vaseLvl = PrecisionLevel && lastLevel == precisionLvl;
            string where = vaseLvl ? "round " + VaseRound + ", vases left " + lastVaseCount : "wave " + lastWave + "/" + lastWaves;
            try { Replay.Dump("DIED", lastLevel, where.Replace(", ", " ") + (killer != null ? " killer=" + Mem.ZomName(killer.Type) : "")); } catch { }
            if (vaseLvl) VasePlanner.EndRound("died");
            lastPlay = null;
            vaseFinalRound = false; VaseRound = 1; Learn.Died(); Viz.Losses++; mowerLostThisLevel = false; Viz.FaithOnDeath(); Quips.Died(lastLevel, Learn.Deaths); if (!ProMode) Strategy.LevelEnded(false, lastWave, lastWaves); stratChosen = false;
            Event("RESULT lvl " + lastLevel + " DIED (death #" + (ProMode ? ProDeaths + 1 : Learn.Deaths) + ")");
            if (ProMode) ProRecord("PRO lvl " + lastLevel + " DIED " + where);
            if (++losses >= 3) Event("died " + losses + "x in a row on lvl " + lastLevel + " - the strategy brain will keep experimenting");
            lastMenuClick = DateTime.Now;
          }
          if (inScene > 7 && (DateTime.Now - lastMenuClick).TotalSeconds > 3) { Pvz.Click(390, 366); lastMenuClick = DateTime.Now; ResetLevel(); }
          if (inScene > 60) { Recover(s.Scene); sceneSince = DateTime.Now; }
          Thread.Sleep(300); continue;
        }
        if (s.Scene == 5 && Mem.OpenDialogs() > 0) {   // something opened over the award screen (e.g. the Almanac): close it
          if ((DateTime.Now - lastMenuClick).TotalSeconds > 2) { Event("closing a window that opened over the award screen"); Pvz.Click(727, 580); lastMenuClick = DateTime.Now; }
          Thread.Sleep(300); continue;
        }
        if (s.Scene == 5) {                         // award screen: Next Level
          if (inScene > 4 && (DateTime.Now - lastMenuClick).TotalSeconds > 4) {
            if (!winCounted && lastScene0 != -1) {   // once per award screen; an award screen already up at startup was counted before the reload
              winCounted = true;
              try { Replay.Dump("WON", lastLevel, "vr=" + VaseRound); } catch { }
              if (PrecisionLevel && lastLevel == precisionLvl) VasePlanner.EndRound("won");
              vaseFinalRound = false; VaseRound = 1; Diag.Won(); Learn.RoundWon(!mowerLostThisLevel); Viz.Wins++; losses = 0; Quips.Won(Learn.Streak, lastLevel); if (!ProMode) Strategy.LevelEnded(true, lastWave, lastWaves); stratChosen = false;
              Event("RESULT lvl " + lastLevel + " WON (streak " + Learn.Streak + ")");
              if (ProMode) ProRecord("PRO lvl " + lastLevel + " WON");
              mowerLostThisLevel = false;
            }
            Pvz.Click(400, awardClicks++ % 2 == 0 ? 520 : 558); lastMenuClick = DateTime.Now; ResetLevel();   // plant award vs note screens
          }
          if (inScene > 40) { Recover(s.Scene); sceneSince = DateTime.Now; }
          Thread.Sleep(300); continue;
        }
        // The chooser pointer can linger after use, so only trust it while still stuck in the intro.
        if (s.Scene == 2 && inScene > 4 && Mem.SeedChooserOpen() && !loadoutPending && dave == 0 && Mem.OpenDialogs() == 0
            && s.Tutorial == 0 && !IsConveyor(s)) {   // a leftover chooser pointer must not trigger picking on tutorial/conveyor levels
          if (!ChooseSeeds(s)) Recover(s.Scene);
          sceneSince = DateTime.Now; continue;
        }
        if (!s.HasBoard || (s.Scene != 3 && !s.Complete)) {
          if (inScene > 45 && s.Scene != 3) { Recover(s.Scene); sceneSince = DateTime.Now; }
          Thread.Sleep(300); continue;
        }
        if (loadoutPending && s.Scene == 3) {
          loadoutPending = false;
          Learn.LastLoadout = s.Packets.Where(p => p.Type >= 0).Select(p => p.Type).ToList();
          Strategy.Loadout = Learn.LastLoadout.ToList();
          Event("LOADOUT " + string.Join(" ", Learn.LastLoadout.Select(sd => Mem.SeedName(sd) + "(" + Learn.PickScore(sd).ToString("0") + ")")));
        }
        var tw = System.Diagnostics.Stopwatch.StartNew();
        Track(s);
        VasePlanner.Observe(s, DateTime.Now.Ticks / 1e7);   // what came out of each broken vase (only what a player sees)
        TTrack = Ema(TTrack, tw.Elapsed.TotalMilliseconds);

        if (s.Complete) {
          if (winNotedLvl != s.Level) { winNotedLvl = s.Level; Replay.Note("WIN", "level complete", s.GameTime); }
          CollectAll(s); Thread.Sleep(150); continue;
        }
        winNotedLvl = -1;
        if (s.Paused) {
          // Returning to a level that was left mid-game asks "Continue game?": continue it (left button).
          if (Mem.OpenDialogs() > 0 && Mem.DialogText().ToLower().Contains("continue your current game")) {
            if ((DateTime.Now - lastMenuClick).TotalSeconds > 3) { Event("DIALOG continue game? -> CONTINUE"); Pvz.Click(279, 371); lastMenuClick = DateTime.Now; }
            Thread.Sleep(300); continue;
          }
          if (Pvz.Unpause()) Event("game lost focus -> unpaused"); continue;
        }

        tw.Restart(); CollectAll(s); TCollect = Ema(TCollect, tw.Elapsed.TotalMilliseconds);
        // Vasebreaker: the planner (vaseplan.cs) picks which vase to open and when, from what has come out of vases so far
        // (round contents it has seen before + what is left), the lane defences and the threats - not fixed hand rules.
        // Stays on for the whole level (vases vanish briefly between rounds), off once a different level is being played.
        if (s.Vases.Count > 0 && !PrecisionLevel) { PrecisionLevel = true; precisionLvl = s.Level; Event("PRECISION level (vases): capping speed at x2 so timing plays work"); }
        if (PrecisionLevel && s.Level != precisionLvl) { PrecisionLevel = false; Event("precision level over: back to the chosen speed"); }
        if (PrecisionLevel) {
          foreach (var ve in VasePlanner.DrainEvents()) { L(ve); Replay.Note("PLAN", ve, s.GameTime); }
          if (VasePlanner.Hunting && !wasHunting) Event("DARTH'S GAMBIT: football out - breaking vases fast to find a squash");
          wasHunting = VasePlanner.Hunting; lastVaseCount = s.Vases.Count;
          if (VasePlanner.Round > 0 && VasePlanner.Round != VaseRound) { Replay.Note("ROUND", "vase round " + VasePlanner.Round, s.GameTime); VaseRound = VasePlanner.Round; }   // survives hot reload (inferred from the vases)
          vaseFinalRound = vaseFinalRound || VasePlanner.FinalRound;
          if (s.Vases.Count > 0 && Mem.CursorType() == 0) {
            string vwhy; int v = VasePlanner.ChooseBreak(s, DateTime.Now.Ticks / 1e7, out vwhy);
            if (!string.IsNullOrEmpty(vwhy)) VasePlanner.LastWhy = (v >= 0 ? "breaking " : "") + vwhy;
            if (v >= 0) {
              lastVase = DateTime.Now;
              if (!vaseSeen) { vaseSeen = true; Event("VASEBREAKER: risk planner - opening the vase with the best odds, only when the lawn can take it"); }
              L("VASE break " + vwhy + " left=" + s.Vases.Count);
              Replay.Note("VASE", "break " + vwhy + " left=" + s.Vases.Count, s.GameTime);
              Pvz.FastClick(CellX(v % 10), CellY(v / 10));
            } else if (!string.IsNullOrEmpty(vwhy) && (DateTime.Now - lastVaseWhy).TotalSeconds > 5) { lastVaseWhy = DateTime.Now; L("VASE wait: " + vwhy); }
          }
        }
        // Whack-a-Zombie: the cursor is a hammer, so hit the zombie closest to the house first.
        if (Mem.CursorType() == 7) {
          var whack = s.Zombies.Where(z => z.X < VISIBLE_X && z.X > -40).OrderBy(z => z.X).FirstOrDefault();
          if (whack != null) { Pvz.FastClick((int)whack.X + 40, (int)whack.Y + 60); Viz.Clicks++; if (!whackSeen) { whackSeen = true; Event("WHACK mode: the cursor is a hammer - bonking zombies, nearest to the house first"); } }
        }
        TryPlant(s, ref lastPlant);
      } catch (Exception e) { L("err " + e.Message + " @ " + ((e.StackTrace ?? "").Split('\n').FirstOrDefault() ?? "").Trim()); Thread.Sleep(500); }
      Thread.Sleep(Speed >= 3 ? 5 : 20);   // faster game -> tighter loop
    }
    lightStop = true; lightWanted = false;
    try { timeEndPeriod(1); } catch { }
    try { Replay.Dump(result == "hot reload" ? "RELOAD" : "EXIT", -1, result); Outcomes.Flush(); SimCal.Flush(); } catch { }
    Learn.Save();
    L("bot end: " + result); Viz.Status = "stopped: " + result; Viz.AddEvent("bot stopped: " + result);
    log.Close();
    Thread.Sleep(1500);
    return result;
  }

  // Seed chooser: rank the plants that are actually unlocked, pick the top ones (verifying each card lands
  // in the bank), press Let's Rock, and treat a "you'll need X" warning as advice to bring something new.
  // What a plant with a known role is worth in a loadout: cheap shooters most, then other useful roles.
  static double RoleValue(int seed) {
    int cost = Mem.Cost(seed);
    switch (Roles.RoleOf(seed)) {
      case "SHOOT": return 25 + 30 * (1 - Math.Min(1, cost / 200.0));
      case "SUN": return seed == 1 ? 0 : 20;
      case "INSTANT": return Learn.DeathsHere > 0 ? 55 : 15;   // died here before (flag hordes): bombs matter
      case "WALL": case "MELEE": case "MINE": return 10;
      case "GRAVE": return !Learn.Daytime ? 25 : -100;   // graves only exist at night
      case "HYPNO": return 15;
      case "IMITATE": return -20;
      default: return 0;
    }
  }
  static HashSet<int> vetoed = new HashSet<int>();   // optional picks dropped after a warning this level
  static HashSet<int> locked = new HashSet<int>();   // cards that did nothing when clicked
  static readonly Dictionary<int, HashSet<int>> recommended = new Dictionary<int, HashSet<int>>();   // level -> plants the game asked for
  static HashSet<int> Recommended(int level) { HashSet<int> h; if (!recommended.TryGetValue(level, out h)) recommended[level] = h = new HashSet<int>(); return h; }
  static bool ChooseSeeds(State s) {
    int inBank, toChoose, inFlight;
    DismissNo();   // dismiss a warning dialog if one is up
    toChoose = s.Packets.Count;               // bank slots
    // Unlocked = the card is actually drawn (bright); locked slots are empty brown. Cards already in the bank count too.
    toChoose = s.Packets.Count;
    List<Mem.Card> cards = null;
    for (int tryLit = 0; tryLit < 3; tryLit++) {
      cards = Mem.ChooserCards(out inBank, out toChoose, out inFlight).Where(c => c.Seed >= 0 && c.State != 4)
                  .Where(c => c.State == 1 || Pvz.RegionBrightness(c.X + 5, c.Y + 10, 40, 50) > 300)
                  .GroupBy(c => c.Seed).Select(g => g.First()).ToList();
      if (cards.Count > s.Packets.Count) break;
      Thread.Sleep(1000);   // chooser may still be sliding in after a cutscene: look again
    }
    if (cards.Count <= s.Packets.Count) {
      // Still too few lit cards to fill the bank: the screen read is unreliable here, so trust the chooser's own list
      // and let the click test below find genuinely locked cards.
      cards = Mem.ChooserCards(out inBank, out toChoose, out inFlight).Where(c => c.Seed >= 0 && c.State != 4)
                  .GroupBy(c => c.Seed).Select(g => g.First()).ToList();
      Event("MENU couldn't tell which cards are lit - trying all " + cards.Count + " and letting clicks reveal locked ones");
    }
    toChoose = s.Packets.Count;
    if (cards.Count == 0 || toChoose < 1 || toChoose > 10) return true;
    // The level preview (zombies lined up on the right of the chooser) is visible to any player: read it.
    var preview = s.Zombies.ToList();
    bool previewShields = preview.Any(z => z.Shield > 0 || Mem.ZomName(z.Type) == "DOOR");
    bool previewHorde = preview.Count >= 8;
    if (previewShields || previewHorde) Event("MENU preview: " + preview.Count + " zombies" + (previewShields ? ", shielded/screen-door ones among them" : "") + " - adjusting picks");
    // Plants with the best measured value per sun in this environment are kept no matter what rotation suggests.
    var proven = new HashSet<int>(cards.Select(cd => cd.Seed).Where(sd => Learn.ValueOf(sd) > 0)
                   .OrderByDescending(sd => Learn.ValueOf(sd) / Math.Max(25, Mem.Cost(sd))).Take(4));
    if (proven.Count > 0) Event("MENU proven keepers: " + string.Join(" ", proven.Select(Mem.SeedName)));
    var rnd = new Random();
    bool waterLanes = Mem.RowTypes().Any(t => t == 2);   // the pool is visible on the lawn behind the chooser
    var val = cards.ToDictionary(c => c.Seed, c => Learn.PickScore(c.Seed) + Strategy.Affinity(c.Seed) + rnd.NextDouble() * 12 - (vetoed.Contains(c.Seed) ? 200 : 0)
                                                   + (Roles.Unknown(c.Seed) ? 45 : RoleValue(c.Seed))  // unknowns get explored; known roles have value
                                                   + (Recommended(s.Level).Contains(c.Seed) ? 500 : 0)  // the game told us this level needs it
                                                   + (Learn.Daytime && Roles.Nocturnal.Contains(c.Seed) ? -300 : 0)   // "sleeps during the day"
                                                   + (waterLanes && Roles.WaterPad.Contains(c.Seed) ? 600 : 0)   // visible water lanes are unusable without a pad: always bring one
                                                   + (previewShields && Roles.Penetrates.Contains(c.Seed) ? 150 : 0)  // preview shows screen doors / shields
                                                   + (previewHorde && Roles.Is(c.Seed, "INSTANT") ? 60 : 0)            // preview shows a big horde
                                                   + Math.Min(140, 25.0 * Learn.DeathsHere / (1 + Learn.TimesUsedHere(c.Seed)))   // keeps losing here: try what we rarely brought
                                                   + (proven.Contains(c.Seed) ? 150 : 0)   // ...but never rotate out the plants with the best measured value
                                                   + (Roles.Is(c.Seed, "GRAVE") && s.Graves.Count >= 6 ? 60 + 20 * s.Graves.Count : 0));   // a lawn full of graves (visible) needs busting
    // Build the loadout greedily with diminishing returns per role, so it ends up balanced
    // (sun, damage, burst, defence) rather than five of the same thing. At most one experiment.
    Func<List<int>> pickWant = () => {
      var res = new List<int>(); bool explored = false; var roleCount = new Dictionary<string, int>();
      bool hurtHere = Learn.DeathsHere > 0;
      while (res.Count < toChoose) {
        int bestSd = -1; double bestV = double.MinValue;
        foreach (var kv in val) {
          if (locked.Contains(kv.Key) || res.Contains(kv.Key)) continue;
          if (Roles.Unknown(kv.Key) && explored) continue;
          string role = Roles.RoleOf(kv.Key) ?? "?"; int n; roleCount.TryGetValue(role, out n);
          double f = role == "SHOOT" ? new[] { 1, 0.75, 0.55, 0.4, 0.3 }[Math.Min(4, n)]
                   : role == "SUN" ? new[] { 1, 0.8, 0.2 }[Math.Min(2, n)]
                   : n == 0 ? 1 : 0.4;
          double v = kv.Value * f;
          if (hurtHere && n == 0 && (role == "INSTANT" || role == "WALL")) v += role == "INSTANT" ? 90 : 60;   // died here before: bring burst and a wall
          if (v > bestV) { bestV = v; bestSd = kv.Key; }
        }
        if (bestSd < 0) break;
        res.Add(bestSd); if (Roles.Unknown(bestSd)) explored = true;
        string r0 = Roles.RoleOf(bestSd) ?? "?"; int c0; roleCount.TryGetValue(r0, out c0); roleCount[r0] = c0 + 1;
      }
      return res;
    };

    Func<List<int>> plan = () => ProMode ? ProLoadout(cards.Select(cd => cd.Seed).Where(sd => !locked.Contains(sd)).Distinct().ToList(), toChoose, s) : pickWant();
    var want = plan();
    if (ProMode) Event("PRO loadout: " + string.Join(" ", want.Select(Mem.SeedName)));
    Event("MENU weighing plants: " + string.Join(" ", val.Where(kv => !locked.Contains(kv.Key)).OrderByDescending(kv => kv.Value).Take(toChoose + 4).Select(kv => Mem.SeedName(kv.Key) + "=" + kv.Value.ToString("0"))) + " -> taking " + toChoose);
    for (int step = 0; step < 60; step++) {
      if (Mem.OpenDialogs() > 0) { DismissNo(); continue; }   // never click cards behind a dialog
      cards = Mem.ChooserCards(out inBank, out toChoose, out inFlight); toChoose = s.Packets.Count;
      if (inFlight > 0) { Thread.Sleep(150); continue; }
      var unwanted = cards.FirstOrDefault(c => c.State == 1 && !want.Contains(c.Seed));
      var missing = cards.FirstOrDefault(c => c.State == 3 && want.Contains(c.Seed));
      var click = unwanted ?? (inBank < toChoose ? missing : null);
      if (click == null) break;
      Pvz.Click(click.X + 25, click.Y + 35);
      // Success = the clicked card leaves its spot (starts flying). Poll; the in-flight counter is not reliable in this build.
      bool moved = false;
      for (int t = 0; t < 8 && !moved; t++) {
        Thread.Sleep(120);
        int ib2, tc2, fl2;
        var after = Mem.ChooserCards(out ib2, out tc2, out fl2).FirstOrDefault(c => c.Seed == click.Seed);
        moved = after == null || after.State != click.State;
      }
      if (moved) Thread.Sleep(450);   // let it land before the next click
      bool knownGood = Learn.Picks.Keys.Any(k => k % 100 == click.Seed);   // used before: cannot be locked
      if (click == missing && !moved && knownGood) { Thread.Sleep(400); continue; }
      if (click == missing && !moved) {
        // Clicked it and nothing happened: that plant is not actually available. Remember and replan.
        locked.Add(click.Seed); want = plan();
        Event("MENU " + Mem.SeedName(click.Seed) + " did not respond - must be locked; picking " + Mem.SeedName(want.LastOrDefault()) + " instead");
      }
    }
    Pvz.Click(232, 565);   // Let's Rock
    // The chooser takes a moment to slide away; only a chooser still up after 4s means the game is warning us.
    bool started = false, warned = false;
    for (int t = 0; t < 20 && !started && !warned; t++) { Thread.Sleep(200); started = Mem.Get().Scene != 2; warned = Mem.OpenDialogs() > 0; }
    if (warned) {
      cards = Mem.ChooserCards(out inBank, out toChoose, out inFlight); toChoose = s.Packets.Count;
      {
        // The game is warning us. Read it like a player (which of our plants does it name?), then answer "No".
        Thread.Sleep(300);
        string text = Mem.DialogText();
        DismissNo(); Thread.Sleep(300);
        string norm = new string(text.ToLower().Where(char.IsLetter).ToArray());
        var named = cards.Select(cd => cd.Seed).Distinct().Where(sd => { var n = new string(Mem.PlantName(sd).ToLower().Where(char.IsLetter).ToArray()); return n.Length > 3 && norm.Contains(n); }).ToList();
        // No plant named? Read what kind of plant it asks for ("sun producing", "aquatic", ...) and bring the best of that role.
        if (named.Count == 0) {
          var roleWords = new[] { new[] { "sun", "SUN" }, new[] { "aquatic", "PAD" }, new[] { "water", "PAD" }, new[] { "pool", "PAD" },
                                  new[] { "roof", "PAD" }, new[] { "fog", "LIGHT" }, new[] { "grave", "GRAVE" } };
          foreach (var rw in roleWords) {
            if (!norm.Contains(rw[0])) continue;
            int best = cards.Select(cd => cd.Seed).Distinct().Where(sd => !locked.Contains(sd) && Roles.Is(sd, rw[1])
                                && !(Learn.Daytime && Roles.Nocturnal.Contains(sd)))
                            .OrderByDescending(sd => val.ContainsKey(sd) ? val[sd] : 0).DefaultIfEmpty(-1).First();
            if (best >= 0 && !named.Contains(best)) named.Add(best);
          }
        }
        if (named.Count > 0) {
          foreach (var sd in named) Recommended(s.Level).Add(sd);
          Event("MENU read the warning: \"" + text.Trim() + "\" -> will bring " + string.Join(", ", named.Select(Mem.SeedName)));
          return true;   // re-pick with the recommendation
        }
        var drop = want.Where(sd => sd != 0 && sd != 1 && !vetoed.Contains(sd)).OrderBy(sd => val[sd]).FirstOrDefault();
        vetoed.Add(drop);
        Event("MENU the game warned the loadout is missing something important -> dropping " + Mem.SeedName(drop) + " to bring an untried plant");
        Quips.Warned();
        return true;   // next loop pass picks again without the vetoed plant
      }
    }
    vetoed.Clear();
    loadoutPending = true;   // the bank only fills in once the level starts; recorded in the main loop
    return true;
  }
  // Watchdog: instead of quitting when nothing progresses, snapshot the screen for later diagnosis,
  // drop assumptions that may be wrong, nudge the UI, and carry on.
  static int recoveries;
  static bool winCounted;
  static int lastScene0 = -1;
  static bool whackSeen, vaseSeen;
  static int winNotedLvl = -1, lastWipeVases;
  public static bool ScreenDark; static int lawnLight = 999; static DateTime lastDarkCheck = DateTime.MinValue;
  // Background screen-light sampler (blackout detection). Only touches Pvz screen reads; publishes one int.
  static volatile int lightNow = 999; static volatile bool lightWanted; static volatile int lightEveryMs = 500;
  static void LightLoop() {
    while (true) {
      try {
        if (lightWanted) { int v = (Pvz.RegionBrightness(150, 150, 60, 40) + Pvz.RegionBrightness(400, 300, 60, 40) + Pvz.RegionBrightness(620, 450, 60, 40)) / 3; if (lightWanted) lightNow = v; }
      } catch { lightNow = 999; }
      if (lightStop) return;
      Thread.Sleep(lightWanted ? lightEveryMs : 300);
    }
  }
  static volatile bool lightStop;
  const int DARK_LIGHT = 45;   // mean R+G+B over 3 lawn patches; normal night/fog lawns read well above this, a blackout near 0
  static DateTime lastVase = DateTime.MinValue, lastVaseWhy = DateTime.MinValue;
  static bool vaseFinalRound;   // set when Dave announces the final batch of vases; cleared when a level ends
  public static int VaseRound = 1; static bool wasHunting;   // 1 until Dave brings "more vases", then 2; 3 at "the last of 'em"
  static DateTime lastTutorial = DateTime.MinValue;
  static DateTime lastDialogAnswer = DateTime.MinValue;
  // Dialog buttons sit at the bottom of the tombstone dialog: Yes on the left, No on the right.
  static void AnswerDialog() {
    string text = Mem.DialogText().Trim();
    string low = text.ToLower();
    var money = System.Text.RegularExpressions.Regex.Match(text, @"\$([\d,]+)");
    bool shopOffer = low.Contains("upgrade") || low.Contains("buy") || low.Contains("purchase");
    bool usefulItem = low.Contains("seed slot");   // more plants per level is always an upgrade
    bool yes = shopOffer && usefulItem;
    Event("DIALOG \"" + text + "\" -> " + (yes ? "YES (a seed slot means one more plant per level)" : "NO (not sure what this does, playing it safe)"));
    if (yes) Quips.Bought(text);
    Pvz.Click(yes ? 424 : 600, 497);
  }
  // Live self-test of message clicks: pick up a seed by message and check the game's cursor state.
  static int postTest, postTries;   // 0 untested, 1 works, -1 does not (retried every 15 s, up to 8 times)
  static DateTime postRetryAt = DateTime.MinValue;
  static void SelfTestPostClick(Pkt p) {
    if (Mem.CursorType() != 0) return;
    Pvz.PostClick(p.CX, p.CY);
    int ct = -1; for (int i = 0; i < 10 && ct != 1 && ct != 2; i++) { Thread.Sleep(25); ct = Mem.CursorType(); }
    if (ct == 1 || ct == 2) {
      Pvz.PostClick(400, 590, true); Thread.Sleep(40);   // put the seed back
      postTest = 1; Pvz.UsePost = true;
      Event("SPEED-UP: message clicks work (seed picked up without moving the mouse) - switching to fast input");
    } else {
      postTest = -1; Pvz.UsePost = false; postTries++; postRetryAt = DateTime.Now.AddSeconds(15);
      Event("message clicks did not register (cursor state " + ct + ") - normal mouse clicks for now, retrying in 15 s (try " + (postTries + 1) + "/8)");
    }
  }
  // The No button moves with the dialog's text length (2-line vs 3-line warnings), so try the likely heights until it closes.
  static void DismissNo() {
    foreach (int y in new[] { 394, 422, 408, 380, 436 }) {
      if (Mem.OpenDialogs() == 0) return;
      Pvz.Click(480, y); Thread.Sleep(500);
    }
  }
  // Post-mortem: what was on screen when a lane broke, handed to Diag to name the cause and correct it.
  static State lastPlay;
  static void BreachDiag(State s, int r) {
    var ready = s.Packets.Where(p => p.Active && p.Cd == 0 && IsInstant(p.Type) && p.Cost <= s.Sun).ToList();
    var lanes = Mem.RowTypes().Count(t => t == 1 || t == 2 || t == 3);
    Diag.Breach(r, s.Plants.Count(p => p.Row == r && IsShooter(p.Type)), s.Sun, ready.Count > 0, s.Plants.Count(p => IsSun(p.Type)), lanes,
                s.Zombies.Count(z => z.Row == r && z.X < VISIBLE_X), s.Wave, s.Waves);
  }
  static string lastDave = ""; static DateTime daveSince = DateTime.Now, lastStoreExit = DateTime.MinValue;
  static void Recover(int scene) {
    recoveries++;
    string shot = @"C:\cbot\stuck_" + DateTime.Now.ToString("HHmmss") + ".png";
    try { Pvz.Shot(shot, 0.5); } catch { }
    Event("WATCHDOG no progress in scene " + scene + " (recovery #" + recoveries + ", screenshot " + shot + ") - resetting assumptions");
    locked.Clear(); vetoed.Clear(); loadoutPending = false;
    DismissNo();                      // "No" on a warning dialog
    Thread.Sleep(400);
    Pvz.FastClick(400, 590, true);    // drop anything held
  }

  static void ResetLevel() { Quips.LevelStart(); Bad.Clear(); zHp.Clear(); sunSeen.Clear(); plantKeys.Clear(); Learn.LevelReset(); }

  static string SceneName(int sc) {
    switch (sc) { case 1: return "menu"; case 2: return "level intro"; case 3: return "playing"; case 4: return "zombies won"; case 5: return "award"; default: return "scene " + sc; }
  }

  static void CollectAll(State s) {
    foreach (var c in s.Suns) {
      if (c.IsPacket) continue;   // dropped seed packets are planted deliberately, not collected
      Pvz.FastClick(c.X, c.Y); Viz.Clicks++;
      DateTime t0;
      bool isSun = c.Type == 11 || c.Type == 12 || c.Type == 13;   // coins (silver/gold/diamond) are money, not sun
      if (isSun) Learn.SunCollected(c.Type == 12 ? 15 : c.Type == 13 ? 50 : 25); else Viz.CoinClicks++;
      if (sunSeen.TryGetValue(c.Id, out t0)) { double ms = (DateTime.Now - t0).TotalMilliseconds; Viz.AddReaction(ms); Quips.Reaction(ms); }
      if (isSun) Viz.SunClicks++;
    }
  }

  // Damage, kills, sun reaction times and plant losses, from frame-to-frame entity IDs.
  static void Track(State s) {
    var now = DateTime.Now;
    Learn.Observe(s, new HashSet<int>(s.Plants.Select(p => p.Type).Where(IsShooter)), VISIBLE_X);
    Learn.TrackContribution(s, IsShooter, IsSun);
    Roles.Observe(s, Learn.LastLaneDmg, Learn.LastDt, Learn.LastVanished,
                  (row, ex) => s.Plants.Where(p => p.Row == row && p.Type != ex && IsShooter(p.Type) && !Roles.Unknown(p.Type)).Sum(p => Learn.Dps(p.Type)));   // lane DPS without the plant being judged
    foreach (var c in s.Suns) if (!sunSeen.ContainsKey(c.Id)) sunSeen[c.Id] = now;
    int dmg = 0; var seen = new HashSet<uint>();
    foreach (var z in s.Zombies) {
      seen.Add(z.Id); int prev;
      if (zHp.TryGetValue(z.Id, out prev) && prev > z.Total) dmg += prev - z.Total;
      zHp[z.Id] = z.Total;
    }
    foreach (var z in s.FogZombies) seen.Add(z.Id);   // walked into fog: still alive, not a kill
    foreach (var id in zHp.Keys.Where(k => !seen.Contains(k)).ToList()) {
      zHp.Remove(id); Viz.Kills++; Quips.Kill();
    }
    Viz.AddDamage(dmg);
    lastPlay = s;
    // v2 measurement: did each squash/bomb/mine/hypno actually do its job, and a rolling replay of the last ~100 s.
    Outcomes.Frame(s, VaseRound);
    foreach (var o in Outcomes.Take()) { Event(o); Replay.Note("OUTCOME", o.Length > 8 ? o.Substring(8) : o, s.GameTime); }
    Replay.Snapshot(s, VaseRound, LaneDanger);
    var keys = new HashSet<int>(s.Plants.Select(p => p.Row * 10 + p.Col));
    // A mower firing means our defence in that lane failed: record it as a mistake.
    for (int r = 0; r < 6; r++) {
      if (lastMowers[r] == 1 && s.Mowers[r] == 2) {
        Viz.MowersLost++; mowerLostThisLevel = true; Learn.MowerLost(r); Quips.Mower(r); Strategy.MowersLost++;
        var zs = s.Zombies.Concat(s.FogZombies).Where(z => z.Row == r && z.X < VISIBLE_X && !z.Charmed).ToList();   // log only: hidden ones count
        Event("!! MOWER FIRED lane " + r + " - defence failed (" + s.Plants.Count(p => p.Row == r && IsShooter(p.Type)) +
              " shooters vs " + zs.Count + " zombies, wave " + s.Wave + "/" + s.Waves + ")");
        BreachDiag(s, r);
      }
      lastMowers[r] = s.Mowers[r];
    }
    // Vasebreaker round change wipes every plant at once (and the vases come back): that is the game, not a loss.
    int goneNow = plantKeys.Count(k => !keys.Contains(k) && !justPlanted.Contains(k));
    bool roundWipe = PrecisionLevel && goneNow >= 3 && (s.Vases.Count > lastWipeVases || keys.Count == 0);
    lastWipeVases = s.Vases.Count;
    if (!IsConveyor(s) && !roundWipe)   // bowled nuts leave their cell by design
      foreach (var k in plantKeys) if (!keys.Contains(k) && !justPlanted.Contains(k) && !(shoveled.ContainsKey(k) && (DateTime.Now - shoveled[k]).TotalSeconds < 3)) {
        int pt; lastPlantType.TryGetValue(k, out pt);
        var eater = s.Zombies.Where(z => !z.Charmed && z.Row == k / 10 && Math.Abs(z.X + 40 - CellX(k % 10)) < 110).OrderBy(z => Math.Abs(z.X + 40 - CellX(k % 10))).FirstOrDefault();
        // used up by design (a squash jumps; Outcomes judges whether it hit)
        bool consumedByDesign = IsMine(pt) || IsInstant(pt) || Roles.Is(pt, "GRAVE") || Roles.Is(pt, "HYPNO") || Roles.Is(pt, "MELEE") || Mem.SeedName(pt) == "SQUASH"
                                || pt == BLOVER || Roles.Is(pt, "BLOW");   // a blover blows away after one gust, like an instant
        if (!consumedByDesign) Event("! lost " + Mem.SeedName(pt) + " at r" + (k / 10) + "c" + (k % 10));
        if (!consumedByDesign) Strategy.PlantsLost++;
        if (!consumedByDesign) Quips.PlantEaten(Mem.SeedName(pt), eater == null ? null : Learn.KeyOf(eater));   // mines vanish by exploding too
      }
    // The plant a cell is "about" is the one on top, not the pot / lily pad / pumpkin listed first.
    lastPlantType = s.Plants.GroupBy(p => p.Row * 10 + p.Col).ToDictionary(g => g.Key, g => g.OrderBy(p => ProIsPad(p.Type) || p.Type == PUMPKIN ? 1 : 0).First().Type);
    Quips.Hoard(s.Sun); Quips.Wave = s.Wave;
    plantKeys = keys; justPlanted.Clear();
    Viz.Publish(s);
  }

  // Conveyor levels (e.g. Wall-nut Bowling) fill all 10 slots with free, pre-chosen seeds.
  static bool IsConveyor(State s) { return s.Packets.Count >= 10; }
  const int BELT_END_X = 575;
  // Bowling = a conveyor that only ever delivers nuts; other conveyor levels are played normally with free seeds.
  static readonly HashSet<int> Nuts = new HashSet<int> { 3, 48, 55 };
  static bool IsBowling(State s) {
    var onBelt = s.Packets.Where(p => p.Active && p.Type >= 0).ToList();
    return IsConveyor(s) && onBelt.Count > 0 && onBelt.All(p => Nuts.Contains(p.Type));
  }

  static List<Cand> ScoreBowling(State s) {
    var list = new List<Cand>();
    // Only nuts already visible on the belt; ones still sliding in sit under the shovel (x ~620+).
    var nut = s.Packets.Where(p => p.Active && p.Type >= 0 && p.CX < BELT_END_X).OrderBy(p => p.CX).FirstOrDefault();
    if (nut == null) return list;
    var rowTypes = Mem.RowTypes();
    var live = s.Zombies.Where(z => z.X < VISIBLE_X && !z.Charmed).ToList();
    for (int r = 0; r < 6; r++) {
      if (rowTypes[r] != 1) continue;
      var zs = live.Where(z => z.Row == r).ToList();
      if (zs.Count == 0) continue;
      int rolling = s.Plants.Count(p => p.Row == r);   // nuts already on their way down this lane
      double sc = zs.Sum(z => Perceived(z)) / 10 + (VISIBLE_X - zs.Min(z => z.X)) / 8 - 70 * rolling;
      list.Add(new Cand { P = nut, Row = r, Col = 2, Score = sc, Affordable = true,
        Why = "bowl at " + zs.Count + " zombie(s), nearest x=" + zs.Min(z => z.X).ToString("0") });
    }
    return list.OrderByDescending(c => c.Score).ToList();
  }

  static void TryPlant(State s, ref DateTime lastPlant) {
    if (IsBowling(s)) {
      var bc = ScoreBowling(s);
      Viz.SetDecision(bc, bc.FirstOrDefault(), bc.Count == 0 ? "holding nuts: no zombies visible" : null);
      if (bc.Count == 0 || (DateTime.Now - lastPlant).TotalMilliseconds < 700) return;
      lastPlant = DateTime.Now;
      var b = bc[0];
      Pvz.FastClick(b.P.CX, b.P.CY); Thread.Sleep(80);
      Pvz.FastClick(CellX(b.Col), CellY(b.Row)); Viz.Clicks += 2; Viz.Planted++; Viz.Flash(b.Row, b.Col);
      justPlanted.Add(b.Row * 10 + b.Col);
      Event("BOWL " + b.Label + "  score " + b.Score.ToString("0") + "  (" + b.Why + ")");
      return;
    }
    var tsc = System.Diagnostics.Stopwatch.StartNew();
    var cands = ProMode ? ProScore(s) : Score(s);
    if (ProMode) {
      TScore = Ema(TScore, tsc.Elapsed.TotalMilliseconds);
      string psave; var pact = ProDecide(cands, s, out psave);
      Viz.SetDecision(cands, pact, psave);
      if (pact == null || (DateTime.Now - lastPlant).TotalMilliseconds < 120) return;
      ExecutePlant(s, pact, ref lastPlant);
      return;
    }
    TScore = Ema(TScore, tsc.Elapsed.TotalMilliseconds);
    // Out of reach = could not afford it within ~30s of current income: never plan around it.
    int flowersNow = s.Plants.Count(pl => IsSun(pl.Type));
    double incomeNow = flowersNow * 25 / 24.0 + Math.Max(0, Learn.SkyRateNow) * 25 / 60.0 + 0.05;
    foreach (var cd in cands) cd.OutOfReach = !cd.Affordable && (cd.P.Cost - s.Sun) / incomeNow > 30;
    cands = cands.Where(cd => !cd.OutOfReach).ToList();
    var best = cands.FirstOrDefault();
    var bestReady = cands.FirstOrDefault(c => c.Affordable);
    string saving = null;
    Cand act = null;
    // How long would saving take? Income ~ one sun drop per flower per ~24s plus what falls from the sky.
    int flowers = s.Plants.Count(pl => IsSun(pl.Type));
    double incomePerSec = flowers * 25 / 24.0 + Math.Max(0, Learn.SkyRateNow) * 25 / 60.0 + 0.05;
    double secsToAfford = best != null ? Math.Max(0, best.P.Cost - s.Sun) / incomePerSec : 0;
    var investNow = cands.FirstOrDefault(c => c.Affordable && IsSun(c.P.Type) && c.Score > 0);
    if (best != null && !best.Affordable && secsToAfford > 25 && investNow != null) {
      act = investNow;
      saving = null;
      Viz.AddEventThrottled("would need " + secsToAfford.ToString("0") + "s to afford " + best.Label + " - investing in a sunflower first");
    }
    else if (best != null && !best.Affordable && !UnderThreat && (bestReady == null || best.Score > bestReady.Score + Strategy.Cur.SaveBias) && best.Score > 20)
      saving = "saving for " + best.Label + " (" + best.P.Cost + " sun, need " + (best.P.Cost - s.Sun) + " more)";
    else if (bestReady != null && bestReady.Score > 8) act = bestReady;
    // Saving never blocks free plants.
    if (saving != null) { var freebie = cands.FirstOrDefault(c => c.Affordable && c.P.Cost == 0 && c.Score > 8); if (freebie != null) act = freebie; }
    Viz.SetDecision(cands, act, saving);

    if (act == null || (DateTime.Now - lastPlant).TotalMilliseconds < 120) return;
    ExecutePlant(s, act, ref lastPlant);
  }
  static void ExecutePlant(State s, Cand act, ref DateTime lastPlant) {
    lastPlant = DateTime.Now;
    var p = act.P; int before = s.Plants.Count;
    // Fast (message) input: test it, and if a test fails (e.g. right after a reload the game is still busy) try again later
    // instead of staying on slow mouse clicks for the whole run (seen 17:29: one failed test = 250 ms per plant all session).
    if ((postTest == 0 || (postTest == -1 && postTries < 8 && DateTime.Now > postRetryAt)) && !act.ShovelFirst) { SelfTestPostClick(p); if (Pvz.UsePost) { lastPlant = DateTime.Now; } }
    if (p.FromCoin) Learn.PacketsTaken.Add(p.CoinId);
    if (act.ShovelFirst) {
      Pvz.FastClick(ShovelX(s), 36); Thread.Sleep(60);
      Pvz.FastClick(CellX(act.Col), CellY(act.Row)); Thread.Sleep(120);
      Event("SHOVEL r" + act.Row + "c" + act.Col + " to make room (" + act.Why + ")");
      shoveled[act.Row * 10 + act.Col] = DateTime.Now; lastShovel = DateTime.Now;
      Outcomes.Shoveled(act.Row, act.Col); Replay.Note("SHOVEL", "r" + act.Row + "c" + act.Col + " (" + act.Why + ")", s.GameTime);
      before = Mem.Get().Plants.Count;
    }
    var ta = System.Diagnostics.Stopwatch.StartNew();
    Pvz.FastClick(p.CX, p.CY);
    Thread.Sleep(Pvz.UsePost ? 15 : 60);
    Pvz.FastClick(CellX(act.Col), CellY(act.Row));
    Viz.Clicks += 2;
    Thread.Sleep(Pvz.UsePost ? 30 : 80);   // (1 ms timer now: 30 keeps roughly the old real wait before verifying)
    TAct = Ema(TAct, ta.Elapsed.TotalMilliseconds);
    var s2 = Mem.Get();
    // Verify the exact result: the intended plant in the intended cell. (Counting plants / sun alone once
    // accepted a plant that landed elsewhere, and blamed the wrong cell when it failed.)
    bool exact = s2.Plants.Any(q => q.Row == act.Row && q.Col == act.Col && q.Type == p.Type);
    bool wrongHere = !exact && s2.Plants.Any(q => q.Row == act.Row && q.Col == act.Col && !s.Plants.Any(o => o.Id == q.Id));
    // A bank packet that started recharging was used: a free plant (0 sun) killed in the same frame otherwise looked like a failure.
    bool spent = !p.FromCoin && !IsConveyor(s) && p.Cd == 0 && s2.Packets.Any(q => q.Slot == p.Slot && q.Type == p.Type && q.Cd > 0);
    bool loose = s2.Plants.Count > before || s2.Sun < s.Sun || spent;
    bool ok = exact || (loose && !wrongHere);   // loose: an instant can already be gone by the time we look
    string failWhy = ok ? "" : wrongHere ? "a different plant landed there" : s.Blocked.Contains(act.Row * 10 + act.Col) ? "cell blocked" : "nothing changed";
    if (ok) { justPlanted.Add(act.Row * 10 + act.Col); Viz.Planted++; Viz.Flash(act.Row, act.Col); Quips.Planted(Mem.SeedName(p.Type), act.Row); }
    Event((ok ? "PLANT " : "fail ") + act.Label + "  score " + act.Score.ToString("0") + "  (" + act.Why + ")" + (ok && !exact ? " [unverified]" : "") + (ok ? "" : " - " + failWhy));
    Replay.Note(ok ? "PLANT" : "FAIL", act.Label + " score " + act.Score.ToString("0") + " (" + act.Why + ")" + (ok ? "" : " - " + failWhy), s.GameTime);
    if (!ok) { Pvz.FastClick(400, 590, true); Bad.Add(act.Row * 10 + act.Col); }
  }

  public static double[] LaneDanger = new double[6];
  public static bool HealthVisible;
  static bool UnderThreat;   // the game is drawing zombie health bars (mod option)
  // Seconds a shooter at this column will be firing at a zombie now at x, given its learned range.
  static double FireTime(int seed, int col, double zombieX, double speed) {
    double dist = Math.Max(0, zombieX + 40 - CellX(col)), range = Learn.Range(seed);
    if (zombieX >= 9000) dist = 700;   // nothing on screen yet: judge against a zombie entering the lawn
    double t = range >= 800 || dist <= range ? dist / speed : range / speed;
    return Math.Max(2, t);
  }
  // The shovel sits just right of the last seed packet.
  static int ShovelX(State s) { return s.Packets.Count == 0 ? 480 : s.Packets.Max(p => Mem.BankX + p.X + p.W) + 42; }

  // Zombies at or right of this x are still off-screen and ignored.
  public const float VISIBLE_X = 760;
  // What a player can judge: how tough this kind of zombie looked when it arrived (its full health),
  // times its visible damage state (intact / damaged / nearly dead). Exact HP numbers are not used.
  static readonly Dictionary<uint, int> fullHp = new Dictionary<uint, int>();
  public static double Perceived(Zom z) {
    Seen.Prune(fullHp);   // throttled inside (5 s); forgets zombies unseen for 30 s
    if (z.Charmed) return 0;   // hypnotized: on our side
    int full;
    if (!fullHp.TryGetValue(z.Id, out full) || z.Total > full) { full = Math.Max(1, z.Total); fullHp[z.Id] = full; }
    double frac = (double)z.Total / full;
    double state = frac > 0.66 ? 1.0 : frac > 0.33 ? 0.66 : 0.33;
    double summon = 1 + 1.5 * Learn.SummonConfidence(Learn.KeyOf(z));   // learned summoner: up to 2.5x, scaled by how sure we are
    if (HealthVisible) return summon * Math.Max(1, z.Total);   // health bars are on screen: use what is shown
    return summon * Learn.Tough(Learn.KeyOf(z)) * state;   // otherwise: learned toughness of this kind x visible damage state
  }

  static List<Cand> Score(State s) {
    SeePlants(s);
    var rowTypes = Mem.RowTypes();
    // Lanes we can fight in: grass, water (with pads) and high ground / roof (with pots).
    var rows = Enumerable.Range(0, 6).Where(r => rowTypes[r] == 1 || rowTypes[r] == 2 || rowTypes[r] == 3).ToList();
    var list = new List<Cand>();
    if (rows.Count == 0) return list;
    var occ = new HashSet<int>(s.Plants.Select(p => p.Row * 10 + p.Col));
    // Cell model: water and roof cells need a pad/pot underneath a normal plant; pads go on empty water/roof cells.
    bool roof = s.Background == 4 || s.Background == 5;
    Func<int, int, bool> water = (r, c) => s.Square[r, c] == 3 || rowTypes[r] == 2;   // lane type is the reliable signal (pool lanes)
    Func<int, int, bool> padAt = (r, c) => s.Plants.Any(pl => pl.Row == r && pl.Col == c && Roles.IsPad(pl.Type));
    Func<int, int, bool> otherAt = (r, c) => s.Plants.Any(pl => pl.Row == r && pl.Col == c && !Roles.IsPad(pl.Type));
    Func<int, int, int, bool> canPlace = (seed, r, c) => {
      if (c < 0 || c > 8 || Bad.Contains(r * 10 + c) || s.Blocked.Contains(r * 10 + c)) return false;
      if (Roles.WaterPad.Contains(seed)) return water(r, c) && !occ.Contains(r * 10 + c);
      if (Roles.RoofPad.Contains(seed)) return !water(r, c) && !occ.Contains(r * 10 + c);
      if (Roles.Aquatic.Contains(seed)) return water(r, c) && !occ.Contains(r * 10 + c);
      if (Roles.NeedsPad.Contains(seed) || water(r, c) || roof) return padAt(r, c) && !otherAt(r, c);
      return !occ.Contains(r * 10 + c);
    };
    Func<int, int, bool> free = (r, c) => canPlace(-1, r, c);   // a normal plant could stand here
    bool belt = IsConveyor(s);
    var usable = belt ? s.Packets.Where(p => p.Active && p.Type >= 0 && p.CX < BELT_END_X).GroupBy(p => p.Type).Select(g => g.OrderBy(p => p.CX).First()).ToList()
                      : s.Packets.Where(p => p.Active && p.Cd == 0).ToList();
    // Seed packets dropped on the lawn are free plants; use them before they fade.
    usable.AddRange(s.Suns.Where(c => c.IsPacket && c.Seed >= 0).Select(c => new Pkt { Type = c.Seed, Cost = 0, Active = true, FromCoin = true, CoinX = c.X, CoinY = c.Y, CoinId = c.Id, W = 50, H = 70 }));

    // Fair play: only zombies already visible on screen count.
    var live = s.Zombies.Where(z => z.X < VISIBLE_X && rows.Contains(z.Row) && !z.Charmed).ToList();   // hypnotized zombies fight for us
    var danger = new Dictionary<int, double>(); var nearest = new Dictionary<int, float>(); var shooters = new Dictionary<int, int>();
    var speedOf = new Dictionary<int, double>();
    // Lane firepower in "peashooter equivalents": dps x reach, so a row of short-range free plants is not mistaken for a fortress.
    Func<int, double> power = r => s.Plants.Where(p => p.Row == r && IsShooter(p.Type)).Sum(p => Learn.Dps(p.Type) * Math.Min(700, Learn.Range(p.Type))) / (Math.Max(1, Learn.Dps(0)) * 700);
    var dangerArr = new double[6];
    foreach (var r in rows) {
      var zs = live.Where(z => z.Row == r).ToList();
      nearest[r] = zs.Count > 0 ? zs.Min(z => z.X) : 9999;
      shooters[r] = s.Plants.Count(p => p.Row == r && IsShooter(p.Type));
      double hp = zs.Sum(z => Perceived(z));
      var lead = zs.OrderBy(z => z.X).FirstOrDefault();
      speedOf[r] = lead != null ? Learn.Speed(Learn.KeyOf(lead)) : 20;
      // Damage the lane can deal before the lead zombie arrives: each shooter only fires while the zombie is within its learned range.
      double near = nearest[r], spd = speedOf[r];
      double dmgBudget = s.Plants.Where(p => p.Row == r && IsShooter(p.Type)).Sum(p => Learn.Dps(p.Type) * FireTime(p.Type, p.Col, near, spd));
      danger[r] = hp <= 0 ? 0 : Learn.Caution * hp / (dmgBudget + 60);   // >1 = this lane loses as things stand
      if (s.Mowers[r] != 1 && s.Mowers[r] != 0) danger[r] *= 1.5;   // no mower left: no second chance here
      dangerArr[r] = danger[r];
    }
    LaneDanger = dangerArr;
    double maxDanger = rows.Max(r => danger[r]);
    double rawMaxDanger = maxDanger / Math.Max(1, Learn.Caution);   // economy decisions use danger without the caution boost
    // Fear of death: worst lane's danger, how close anything is to the house, and whether its mower is gone.
    int worst = rows.OrderByDescending(r => danger[r]).First();
    double closeness = live.Count == 0 ? 0 : Math.Max(0, (400 - live.Min(z => z.X)) / 400.0);
    double fear = Math.Min(1, maxDanger / 3) * 0.6 + closeness * 0.4;
    if (live.Count > 0) {
      int closestRow = live.OrderBy(z => z.X).First().Row;
      Quips.Threat = Learn.KeyOf(live.OrderBy(z => z.X).First());
      if (s.Mowers[closestRow] != 1 && s.Mowers[closestRow] != 0) fear = Math.Min(1, fear * 1.5);
    }
    Viz.UpdateFaith(fear, worst);
    // Under threat = a zombie close to the house, or one walking down a lane with no real firepower: no time to save up.
    UnderThreat = rows.Any(r => nearest[r] < 300 || (nearest[r] < 600 && shooters[r] == 0));
    // Panic (drives slow-mo): a lane in real trouble, zombies near the house, or a flag wave about to hit. Calms down slowly.
    double panicNow = Math.Max(Math.Min(1, rawMaxDanger / 2.5), Math.Max(closeness, WavesToFlag(s) >= 0 && WavesToFlag(s) <= 1 ? 0.6 : 0));
    Panic = panicNow > Panic ? panicNow : Panic * 0.995;
    int sunCount = s.Plants.Count(p => IsSun(p.Type));
    var st = Strategy.Cur;
    // Flag waves are big: shortly before one, lean towards defence; right after, it is a good time to invest.
    int toFlag = WavesToFlag(s);
    bool flagSoon = toFlag >= 0 && toFlag <= 1;
    double defenceBoost = flagSoon ? 1.35 : 1.0, econBoost = flagSoon ? 0.7 : (toFlag >= 5 ? 1.15 : 1.0);
    // Less sky sun than usual (e.g. night) -> lean harder on our own sun production.
    double skyStarved = Learn.SkyStarvation();
    // Sun makers per lane: the strategy's choice plus a little extra when sky sun is scarce, but never more than 2.5 per lane.
    int sunTarget = (int)Math.Round(rows.Count * Math.Max(1, Math.Min(2.5, st.SunPerRow + 0.5 * skyStarved)));
    bool lateGame = s.Waves > 0 && s.Wave >= s.Waves - 1;
    // Forecast: zombies get more numerous as the progress bar fills, and flag waves are huge. Each lane should already
    // hold the firepower the coming waves will need - measured in peashooter-equivalents - before zombies show up in it.
    double progress = s.Waves > 0 ? Math.Min(1, (double)s.Wave / s.Waves) : 0.5;
    int flagAhead = WavesToFlag(s);
    bool flagPrep = flagAhead >= 0 && flagAhead <= 3;
    Func<int, double> laneNeed = r => Math.Min(1.3, Math.Sqrt(Diag.Cur.Urgency)) * (1.0 + 1.5 * progress + (flagPrep ? 1.0 : 0) + (s.Mowers[r] != 1 && s.Mowers[r] != 0 ? 0.5 : 0));
    Func<int, double> forecastScore = r => { double d = laneNeed(r) - power(r); return d <= 0 ? 0 : st.PrebuildWeight * (40 + 60 * Math.Min(2, d)) * (flagPrep ? 1.3 : 1); };
    Func<int, string> forecastWhy = r => "forecast: lane needs ~" + laneNeed(r).ToString("0.0") + " peas by now, has " + power(r).ToString("0.0") + (flagPrep ? " - FLAG WAVE in " + flagAhead : "");

    foreach (var p in usable) {
      Action<int, int, double, string> add = (r, c, sc, why) => {
        if (!IsSun(p.Type) && !IsInstant(p.Type)) sc *= 1.5 - 0.5 * Math.Min(1, p.Cost / 200.0);   // cheap plants give more per sun
        if (s.Sun > 200 / Diag.Cur.Spend && sc > 5 && p.Cost > 0 && !IsInstant(p.Type)) sc += 15 + (s.Sun - 200) / 10.0;   // idle sun is wasted sun
        // Bomb reserve: right before a flag wave keep enough sun for an area bomb, unless this move is urgent.
        int bombCost = usable.Where(u => IsInstant(u.Type) && !u.FromCoin && u.Cost > 0).Select(u => u.Cost).DefaultIfEmpty(0).Min();   // any bomb we carry
        // Hard reserve: one wave before a flag (or the final wave), never spend below a bomb's cost on anything that is not urgent.
        bool bigWaveNext = (toFlag >= 0 && toFlag <= 1) || (s.Waves > 0 && s.Waves - s.Wave <= 1);
        if (bombCost > 0 && bigWaveNext && !IsInstant(p.Type) && p.Cost > 0 && s.Sun - p.Cost < bombCost && nearest[r] > 320) return;   // keep the bomb money unless a zombie is actually close
        // Dropped packets: free, and we know roughly how long until they fade. Only a useful move gets urgency.
        double left = p.FromCoin ? Learn.PacketTimeLeft(p.CoinId) : -1;
        if (p.FromCoin && sc > 30 && left >= 0 && left < 6) sc += 25;
        string tag = p.FromCoin ? "dropped packet" + (left >= 0 ? " (" + left.ToString("0") + "s left)" : "") + ": " : "";
        if (canPlace(p.Type, r, c)) list.Add(new Cand { P = p, Row = r, Col = c, Score = sc, Why = tag + why,
                                            Affordable = belt || p.FromCoin || p.Cost <= s.Sun });
      };
      if (IsSun(p.Type) && sunCount < sunTarget) {
        // Payback view: a flower is worth more with many waves left; only a zombie near the house argues against it.
        double wavesLeft = s.Waves > 0 ? Math.Max(0, (double)(s.Waves - s.Wave) / s.Waves) : 1;
        bool houseThreat = rows.Any(r => nearest[r] < 300 && danger[r] > 1);
        double sc = econBoost * st.EconWeight * Diag.Cur.Econ * (1 + skyStarved) * 70 * (1 - (double)sunCount / sunTarget) * (0.3 + wavesLeft) + (s.Wave == 0 ? 25 : 0) - (houseThreat ? 60 : 0) - (lateGame ? 30 : 0);
        // No income at all and nothing affordable: rebuilding the economy beats everything else.
        int cheapestPaid = usable.Where(u => u.Cost > 0 && !IsSun(u.Type)).Select(u => u.Cost).DefaultIfEmpty(9999).Min();
        if (sunCount == 0 && s.Sun < cheapestPaid) sc = Math.Max(sc, 300);
        // Income floor: one sun producer per lane before anything that is not an immediate emergency.
        bool undefended = rows.Any(r => nearest[r] < 450 * Diag.Cur.Urgency && shooters[r] == 0);   // a zombie is getting close in a lane with no shooter at all
        if (sunCount < rows.Count && !houseThreat && !undefended && wavesLeft > 0.25) sc = Math.Max(sc, 250);
        // Past the first flower per lane, economy waits while total firepower is thin (under one peashooter per lane).
        double totalPower = rows.Sum(rr => power(rr));
        if (sunCount >= rows.Count && totalPower < rows.Count) sc *= 0.5;
        // Income check: sun actually collected per game-minute versus what one more shooter costs.
        if (Learn.IncomePerMin >= 0 && Learn.IncomePerMin < 150 && sunCount < sunTarget) sc += 40 * (1 - Learn.IncomePerMin / 150);
        // Placement comes from the strategy: which column sun makers start at, and whether they are spread across lanes.
        var colOrder = Enumerable.Range(0, 7).OrderBy(cc => Math.Abs(cc - st.SunCol)).ThenBy(cc => cc);
        var sunPerLane = rows.ToDictionary(rr => rr, rr => s.Plants.Count(pl => pl.Row == rr && IsSun(pl.Type)));
        // Land first: a flower on water needs a pad (extra sun) and takes the pad a shooter could use. Water only when land is full.
        bool placedSun = false;
        for (int pass = 0; pass < 2 && !placedSun; pass++)
        foreach (int c in colOrder) {
          var laneOrder = st.SunSpread ? rows.OrderBy(rr => sunPerLane[rr]).ThenBy(rr => danger[rr]) : rows.OrderBy(rr => danger[rr]);
          var r = laneOrder.Where(rr => free(rr, c) && (pass == 1 || !water(rr, c)) && CellX(c) < nearest[rr] - 120).Cast<int?>().FirstOrDefault();
          if (r != null) { add(r.Value, c, sc - Math.Abs(c - st.SunCol) * 3 - (water(r.Value, c) ? 25 : 0), "economy " + sunCount + "/" + sunTarget + " sun makers (col " + c + (st.SunSpread ? ", spread" : "") + (water(r.Value, c) ? ", land is full" : "") + ")"); placedSun = true; break; }
        }
      }
      if (IsShooter(p.Type)) {
        foreach (var r in rows) {
          int c = Enumerable.Range(st.ShooterCol, 6 - st.ShooterCol).Concat(Enumerable.Range(1, st.ShooterCol - 1)).FirstOrDefault(cc => free(r, cc)); if (c == 0) continue;
          double sc = defenceBoost * (100 * Math.Min(3, danger[r]) + (shooters[r] == 0 && nearest[r] < VISIBLE_X ? 260 * Diag.Cur.Urgency : 0));   // a lane with no shooter and a zombie in it comes first
          // How much would this plant really add here, compared with a full-range peashooter in the same spot?
          double effRef = Learn.Dps(0) * FireTime(0, c, nearest[r], speedOf[r]);
          double eff = effRef <= 0 ? 1 : Learn.Dps(p.Type) * FireTime(p.Type, c, nearest[r], speedOf[r]) / effRef;
          sc *= Math.Max(0.15, Math.Min(1.5, eff));
          // Free plants are great filler, but a lane stuffed with the same free plant crowds out real firepower.
          if (p.Cost == 0 && s.Plants.Count(pl => pl.Row == r && pl.Type == p.Type) >= 2) sc *= 0.3;
          string why = "lane danger " + danger[r].ToString("0.00");
          if (WavesToFlag(s) >= 0 && WavesToFlag(s) <= 2 && nearest[r] >= VISIBLE_X && power(r) < 2.5) why = "fortify before the flag wave (lane power " + power(r).ToString("0.0") + " peas)";
          // Description says it penetrates shields/screen doors: worth more against shielded zombies in this lane.
          if (Roles.Penetrates.Contains(p.Type) && live.Any(z => z.Row == r && (z.Shield > 0 || Mem.ZomName(z.Type) == "DOOR"))) { sc *= 1.8; why += ", goes through shields"; }
          if (nearest[r] >= VISIBLE_X) { sc = s.Sun > 300 ? 8 : 0; why = "pre-build lane (" + shooters[r] + " shooters)"; }
          // Build for what is coming, not just for what is on screen.
          double fs = forecastScore(r) * Math.Max(0.4, Math.Min(1.2, eff));
          if (fs > sc) { sc = fs; why = forecastWhy(r); }
          if (water(r, c) && padAt(r, c) && sc > 0) { sc += 60; why += " (pad already down and waiting)"; }   // finish what the pad was for
          if (shooters[r] >= 6) sc = 0;
          add(r, c, sc, why);
        }
      }
      if (IsInstant(p.Type)) {
        foreach (var r in rows.Where(r => nearest[r] < 380 + 60 * (Diag.Cur.BombEager - 1) && danger[r] > st.InstantDanger / Diag.Cur.BombEager)) {
          int c = ColOf(nearest[r]);
          int nearby = live.Count(z => Math.Abs(z.Row - r) <= 1 && Math.Abs(z.X - nearest[r]) < 130);
          add(r, Math.Min(8, c), 150 + 40 * danger[r] + 25 * nearby, "emergency: " + nearby + " zombies at col " + c);
        }
      }
      if (IsWall(p.Type)) {
        foreach (var r in rows.Where(r => danger[r] > 0.5 && nearest[r] < 700 && shooters[r] >= 1)) {
          int c = Math.Min(6, ColOf(nearest[r]) - 1);
          int lastShooter = s.Plants.Where(pl => pl.Row == r).Select(pl => pl.Col).DefaultIfEmpty(0).Max();
          if (c > lastShooter) add(r, c, st.WallWeight * (40 + 45 * Math.Min(2, danger[r])), "stall zombie in front of shooters");
        }
      }
      // A dropped (free) bomb should not fade unused: aim it at the toughest group in blast range, more urgently as it fades.
      if (p.FromCoin && IsInstant(p.Type) && live.Count > 0) {
        double left = Learn.PacketTimeLeft(p.CoinId);
        var target = live.Select(z => new { z, grp = live.Where(o => Math.Abs(o.Row - z.Row) <= 1 && Math.Abs(o.X - z.X) < 100).ToList() })
                         .OrderByDescending(t => t.grp.Sum(o => Perceived(o))).First();
        double hp = target.grp.Sum(o => Perceived(o));
        int tc = Math.Min(8, ColOf(target.z.X));
        foreach (int cc in new[] { tc, tc - 1, tc + 1 })
          if (cc >= 0 && cc <= 8 && free(target.z.Row, cc)) {
            list.Add(new Cand { P = p, Row = target.z.Row, Col = cc, Affordable = true,
              Score = 60 + hp / 10 + (left >= 0 && left < 6 ? 80 : 0),
              Why = "free bomb on " + target.grp.Count + " zombie(s), ~" + hp.ToString("0") + " hp" + (left >= 0 ? " (" + left.ToString("0") + "s before it fades)" : "") });
            break;
          }
      }
      // Area bombs (cherry, doom) are worth most on a packed group, wherever it is on the lawn.
      // Try every cell and keep the one whose blast destroys the most zombie health; a big pile is worth a big score.
      if (IsInstant(p.Type) && (p.Type == 2 || p.Type == 15) && live.Count > 0) {
        double reach = p.Type == 15 ? 250 : 115; int rowReach = p.Type == 15 ? 2 : 1;   // cherry: 3x3, doom: much wider
        int bR = -1, bC = -1; double bHp = 0, bEff = 0; int bN = 0; float bClose = 9999; bool bDig = false; string bDug = "";
        bool houseInDanger = live.Any(z => z.X < 300);
        foreach (var r in rows) for (int cc = 0; cc < 9; cc++) {
          // Emergency: if no free cell reaches the pile, dig up our own plant there (a pad stays and takes the bomb).
          bool dig = false;
          if (!canPlace(p.Type, r, cc)) {
            if (!houseInDanger || !otherAt(r, cc) || (water(r, cc) && !padAt(r, cc)) || Bad.Contains(r * 10 + cc) || s.Blocked.Contains(r * 10 + cc)) continue;
            dig = true;
          }
          var hit = live.Where(o => Math.Abs(o.Row - r) <= rowReach && Math.Abs(o.X + 30 - CellX(cc)) <= reach).ToList();
          if (hit.Count == 0) continue;
          double hp = hit.Sum(o => Perceived(o));
          var victim = dig ? s.Plants.FirstOrDefault(pl => pl.Row == r && pl.Col == cc && !Roles.IsPad(pl.Type)) : null;
          double eff = hp - (dig ? 4 * (victim != null ? Math.Max(25, Mem.Cost(victim.Type)) : 100) : 0);   // losing a plant costs ~4x its price in hp-equivalents: cheap ones go first
          if (eff > bEff || (eff == bEff && hit.Count > bN)) { bEff = eff; bHp = hp; bN = hit.Count; bR = r; bC = cc; bClose = hit.Min(o => o.X); bDig = dig; bDug = victim != null ? Mem.SeedName(victim.Type) : ""; }
        }
        // Worth a bomb: a real pile (3+), a tough pair, or one tough zombie about to reach the house - not any lone walker.
        double tough = 1400 / Math.Sqrt(Diag.Cur.BombEager);
        if (bR >= 0 && (bN >= 3 || (bHp >= tough && (bN >= 2 || bClose < 450)))) {
          bool noMower = Enumerable.Range(Math.Max(0, bR - rowReach), 2 * rowReach + 1).Any(rr => rr < 6 && rows.Contains(rr) && s.Mowers[rr] != 1 && s.Mowers[rr] != 0);
          double bsc = (80 + bHp / 4 + 40 * bN + (bClose < 450 ? 150 : 0) + (noMower ? 100 : 0)) * (1.1 / st.InstantDanger) * Math.Sqrt(Diag.Cur.BombEager);
          string bwhy = "cluster bomb: " + bN + " zombies, ~" + bHp.ToString("0") + " hp in the blast" + (bClose < 450 ? ", close to the house" : "");
          if (bDig) list.Add(new Cand { P = p, Row = bR, Col = bC, Score = Math.Min(1400, bsc + 100), ShovelFirst = true, Affordable = belt || p.FromCoin || p.Cost <= s.Sun,
                                        Why = "EMERGENCY " + bwhy + " - digging up my " + bDug + " to make room" });
          else add(bR, bC, Math.Min(1400, bsc), bwhy);
        }
      }
      // Description says it sleeps during the day: useless on a day lawn.
      if (Roles.Nocturnal.Contains(p.Type) && s.Background != 1 && s.Background != 3) continue;
      // Blow plan (description: "blows away all balloon zombies"): use it as soon as balloons are on screen.
      if (Roles.Is(p.Type, "BLOW")) {
        int balloons = live.Count(z => Mem.ZomName(z.Type) == "BALLOON");
        if (balloons > 0) {
          int bc0 = Enumerable.Range(0, 9).FirstOrDefault(cc => free(rows[0], cc));
          add(rows[0], bc0, 150 + 40 * balloons, (Roles.Tentative(p.Type) ? "plan from description: " : "") + "blow away " + balloons + " balloon zombie(s)");
        }
        continue;
      }
      // Pad / pot plan: a lane whose front cells are water or roof is useless until pads are down.
      if (Roles.IsPad(p.Type)) {
        foreach (var r in rows.OrderByDescending(r => danger[r]).ThenBy(r => r)) {
          int usable2 = Enumerable.Range(0, 6).Count(cc => free(r, cc));
          // An empty pad already waiting in this lane is enough: lay the next one only once something stands on it.
          if (Enumerable.Range(1, 5).Any(cc => padAt(r, cc) && !otherAt(r, cc))) continue;   // (a stray pad outside shooter columns does not count)
          // Put it where a shooter would stand (the strategy's shooter column first), never right in front of a zombie.
          // Same columns shooters use (never column 0, where nothing would ever be planted on it).
          var padCols = Enumerable.Range(Math.Max(1, st.ShooterCol), Math.Max(0, 6 - Math.Max(1, st.ShooterCol))).Concat(Enumerable.Range(1, Math.Max(0, st.ShooterCol - 1)));
          int pc = padCols.Where(cc => canPlace(p.Type, r, cc) && CellX(cc) < nearest[r] - 80).DefaultIfEmpty(-1).First();
          if (pc < 0) continue;
          double psc = (usable2 < 2 ? 85 : usable2 < 4 ? 45 : 15) + 25 * Math.Min(2, danger[r]);
          // A water lane with zombies in it and no shooter: the pad IS the defence (a shooter needs it first), so rank it like one.
          if (water(r, pc) && shooters[r] == 0 && nearest[r] < VISIBLE_X) psc = Math.Max(psc, 0.9 * defenceBoost * (100 * Math.Min(3, danger[r]) + 260 * Diag.Cur.Urgency));
          else if (water(r, pc) && forecastScore(r) > psc) psc = forecastScore(r) + (shooters[r] == 0 ? 30 : 0);   // a water lane short of its forecast firepower needs a pad first
          add(r, pc, psc, (Roles.Tentative(p.Type) ? "plan from description: " : "") + (roof ? "pot" : "pad") + " for lane " + r + " (" + shooters[r] + " shooters, " + usable2 + " usable cells)");
        }   // every lane gets a proposal; the scores decide which lane needs it most
        continue;
      }
      // Grave Buster plan (from its description): plant it on a gravestone to clear the cell.
      if (Roles.Is(p.Type, "GRAVE")) {
        foreach (int g in s.Graves.OrderBy(gc => gc % 10)) {
          int gr = g / 10, gcol = g % 10;
          if (!rows.Contains(gr) || occ.Contains(g)) continue;
          list.Add(new Cand { P = p, Row = gr, Col = gcol, Score = 40 + 12 * Math.Min(12, s.Graves.Count) + (gcol < 6 ? 25 : 0), Affordable = belt || p.FromCoin || p.Cost <= s.Sun,
                              Why = (Roles.Tentative(p.Type) ? "plan from description: " : "") + "bust the grave at r" + gr + "c" + gcol + " (" + s.Graves.Count + " graves)" });
          break;
        }
        continue;
      }
      // Hypno plan: put it in the path of the toughest zombie so that zombie switches sides.
      if (Roles.Is(p.Type, "HYPNO")) {
        var big = live.OrderByDescending(z => Perceived(z)).FirstOrDefault();
        if (big != null) {
          int hc = ColOf(big.X) - 1;
          if (hc >= 1 && free(big.Row, hc))
            add(big.Row, hc, Math.Min(160, 30 + Perceived(big) / 12), (Roles.Tentative(p.Type) ? "plan from description: " : "") + "make the " + Learn.KeyOf(big) + " fight for us");
        }
        continue;
      }
      if (Roles.Is(p.Type, "IMITATE")) continue;
      // Never seen this plant do anything: try it where its effect will be visible.
      // Every plant we own but do not understand gets tried (one specimen at a time per kind).
      // Prefer a lane with a zombie so attacks/bites are observable; any lane works for sun makers.
      // (Bombs are never "tested" on an empty lawn: they are single-use with a long cooldown, so their real uses are the test.)
      if (Roles.Unconfirmed(p.Type) && !Roles.TrialRunning(p.Type) && !Roles.Is(p.Type, "SUN") && !Roles.Is(p.Type, "INSTANT")) {
        double tsc = p.Cost == 0 ? 90 : 60;
        bool placed = false;
        foreach (var r in rows.OrderBy(r => nearest[r] < VISIBLE_X ? 0 : 1).ThenBy(r => danger[r])) {
          int c = new[] { 2, 3, 1, 4, 5, 6, 7 }.FirstOrDefault(cc => free(r, cc) && CellX(cc) < nearest[r] - 80);
          if (c > 0) { add(r, c, tsc, Roles.Tentative(p.Type) ? "TEST: description says " + Roles.RoleOf(p.Type).ToLower() + " - checking" : "EXPERIMENT: what does " + Mem.SeedName(p.Type) + " do?"); placed = true; break; }
        }
        if (!placed) Viz.AddEventThrottled("wanted to test " + Mem.SeedName(p.Type) + " but the lawn is full - waiting for room");
      }
      // Shovel: a lane that is losing, no room left, and a weaker shooter (e.g. a free one) occupying a spot -> swap it.
      if (IsShooter(p.Type) && p.Cost > 0 && !belt && !IsConveyor(s)) {
        bool shovelReady = (DateTime.Now - lastShovel).TotalSeconds > 8 && p.Cost <= s.Sun;   // only when we can plant the replacement right away
        // Upgrade when a lane is losing, when a flag wave is near, or when sun is piling up with nowhere to go.
        bool upgradeTime = (toFlag >= 0 && toFlag <= 2 && s.Sun >= p.Cost + 25) || s.Sun > 250;
        foreach (var r in rows.Where(r => shovelReady && (danger[r] > 1.2 || upgradeTime) && !Enumerable.Range(1, 5).Any(cc => free(r, cc)))
                              .OrderByDescending(r => danger[r]).ThenBy(r => s.Plants.Where(pl => pl.Row == r && IsShooter(pl.Type)).Sum(pl => Learn.Dps(pl.Type)))) {
          var weakest = s.Plants.Where(pl => pl.Row == r && pl.Col >= 1 && pl.Col <= 5 && IsShooter(pl.Type) && Learn.Dps(pl.Type) < Learn.Dps(p.Type) * 0.6
                                         && !(shoveled.ContainsKey(pl.Row * 10 + pl.Col) && (DateTime.Now - shoveled[pl.Row * 10 + pl.Col]).TotalSeconds < 30))
                                .OrderBy(pl => Learn.Dps(pl.Type)).FirstOrDefault();
          if (weakest == null) continue;
          list.Add(new Cand { P = p, Row = r, Col = weakest.Col, Score = Math.Max(60, 80 * Math.Min(3, danger[r])), ShovelFirst = true, Affordable = p.Cost <= s.Sun,
                              Why = "lane full: replace " + Mem.SeedName(weakest.Type) + " (" + Learn.Dps(weakest.Type).ToString("0") + " dps) with " + Mem.SeedName(p.Type) });
          break;
        }
      }
      // Guard: a wall in front of a lane's frontmost plant, worth more the more value sits behind it,
      // and much more when that lane has no mower left to save it.
      if (IsWall(p.Type)) {
        foreach (var r in rows.Where(r => nearest[r] < VISIBLE_X)) {
          var mine = s.Plants.Where(pl => pl.Row == r).ToList(); if (mine.Count == 0) continue;
          int front = mine.Max(pl => pl.Col);
          if (mine.Any(pl => pl.Col == front && IsWall(pl.Type))) continue;   // already guarded
          int gc = front + 1; if (gc > 8 || CellX(gc) > nearest[r] - 40) continue;
          double behind = mine.Sum(pl => IsSun(pl.Type) ? 75 : Math.Max(25, Mem.Cost(pl.Type)));
          bool noMower = s.Mowers[r] != 1 && s.Mowers[r] != 0;
          double gsc = st.WallWeight * (20 + behind / 8) * (noMower ? 1.8 : 1) * Math.Min(1.5, 0.5 + danger[r]);
          add(r, gc, gsc, "guard " + mine.Count + " plants (~" + behind.ToString("0") + " sun of value)" + (noMower ? ", NO MOWER in this lane" : ""));
        }
      }
      // Once a free plant has a known useful role, it is always worth placing.
      if (p.Cost == 0 && !Roles.Unknown(p.Type) && (IsShooter(p.Type) || IsWall(p.Type))) {
        foreach (var r in rows.OrderByDescending(r => danger[r])) {
          int c = Enumerable.Range(1, 6).FirstOrDefault(cc => free(r, cc)); if (c == 0) continue;
          double fEff = Learn.Dps(p.Type) * Math.Min(700, Learn.Range(p.Type)) / (Math.Max(1, Learn.Dps(0)) * 700);   // vs a peashooter
          double fsc = (35 + 50 * Math.Min(2, danger[r])) * (s.Sun >= 100 ? Math.Max(0.2, Math.Min(1, fEff * 2)) : 1);   // with sun to spare, real shooters come first
          if (s.Plants.Count(pl => pl.Row == r && pl.Type == p.Type) >= 2) fsc *= 0.3;   // enough of these here already
          add(r, c, fsc, "free " + Roles.RoleOf(p.Type).ToLower() + " plant"); break;
        }
      }
      if (IsMelee(p.Type)) {
        foreach (var r in rows.Where(r => nearest[r] < 600 && danger[r] > 0.3)) {
          int c = ColOf(nearest[r]) - 1;
          if (c >= 1) add(r, c, 55 + 40 * Math.Min(2, danger[r]), "learned eater: put it in the zombie's path");
        }
      }
      if (IsMine(p.Type)) {
        // Use the learned arm time: only where the zombie arrives after the mine is ready.
        foreach (var r in rows.Where(r => nearest[r] < VISIBLE_X && danger[r] > 0.4)) {
          for (int c = ColOf(nearest[r]) - 1; c >= 1; c--) {
            double eta = (nearest[r] + 30 - CellX(c)) / speedOf[r];
            if (eta < Learn.MineArmTrusted * 1.15) continue;
            add(r, c, st.MineWeight * (45 + 35 * Math.Min(2, danger[r])), "zombie ETA " + eta.ToString("0") + "s > believed arm " + Learn.MineArmTrusted.ToString("0.0") + "s");
            break;
          }
        }
      }
    }
    // Look ahead with what this bot has MEASURED (its own damage, range, speed and toughness numbers):
    // for a lane predicted to break, try each plant it holds in each free cell and favour what actually stops it.
    var lm = LearnedModel();
    foreach (var r in rows) {
      var b = SimLane(r, s, lm);
      if (!b.Breach) continue;
      foreach (var p in usable.GroupBy(u => u.Type).Select(g => g.First())) {
        string role = Roles.RoleOf(p.Type) ?? "";
        if (role != "SHOOT" && role != "WALL" && role != "INSTANT" && role != "MINE") continue;
        double bestGain = 0; int bestC = -1; SimOut bo = null;
        for (int c = 0; c < 9; c++) {
          if (!canPlace(p.Type, r, c)) continue;
          var o = SimLane(r, s, lm, p.Type, c);
          if (b.Cost - o.Cost > bestGain + 1) { bestGain = b.Cost - o.Cost; bestC = c; bo = o; }
        }
        if (bo == null || bestGain < 50) continue;
        bool fixes = !bo.Breach;
        list.Add(new Cand { P = p, Row = r, Col = bestC, Affordable = belt || p.FromCoin || p.Cost <= s.Sun,
          Score = 800 + Math.Min(600, bestGain / 8) + (fixes ? 400 : 0) - 0.6 * p.Cost,
          Why = "LOOKAHEAD lane " + r + " breaks in " + b.BreachT.ToString("0") + "s (my measured numbers) -> " + Mem.SeedName(p.Type) + (fixes ? " here holds it" : " here delays it") });
      }
    }
    return list.OrderByDescending(c => c.Score).ToList();
  }
}
