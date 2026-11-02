// Guess layer for the brain panel: what the bot believes about things it cannot see right now.
//  * Ghosts: a fair "player memory" of zombies that vanished into fog or a screen BLACKOUT (last seen x / lane / kind,
//    estimated x now = last x - speed * time, speed from the motion seen before it vanished, else a per-kind default;
//    confidence fades to 0 over GHOST_LIFE game-s). A ghost is dropped when the same zombie shows up again, when it
//    would be past the house (x < -50) or when its confidence runs out. Whether a vanishing zombie went into fog or died
//    is what a player sees at that moment (it fades into fog vs. a death animation); after that the ghost lives on its
//    own - game memory is used only for the panel's accuracy check, never to steer a ghost.
//  * Per-lane: Bot.LaneDanger and the 40 s simulator's breach time (Guess.SetSim from pro.cs if hooked, else parsed from
//    Bot.ProStatus "sim: lane r@Ts" - same numbers).
//  * Vasebreaker: P(zombie) / P(plant) per unopened vase (VasePlanner.VaseOdds) and the planner's last named vase.
// Everything is computed on the MAIN thread (Viz.Publish calls Guess.Frame) and handed to the panel as an immutable
// snapshot under a lock. Cost: a few small loops per tick (< 0.1 ms); the snapshot is rebuilt at most every 80 ms.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

public static class Guess {
  public const double GHOST_LIFE = 10;   // game-s until a ghost's confidence reaches 0

  public class Ghost {
    public uint Id; public int Row, Type; public string Name = "";
    public float LastX, EstX, Band, Speed; public double SeenT, Age, Conf; public bool Eating, Dark; public double Hp;
    public float TrueX = float.NaN;   // where the game really has it (panel accuracy check only; NaN = unknown/dead)
  }
  public class Zv { public uint Id; public int Row, Type; public string Name = ""; public float X; public double Hp = -1, Speed; public bool Eating, Charmed, Offscreen; }
  public class Pv { public int Row, Col, Type, Hp, MaxHp; }
  public class Cv { public int X, Y, Type, Seed; public bool Packet; public double Left = -1; }
  public class Snap {
    public DateTime At; public int Level, Bg, Sun, Wave, Waves, Scene, RowsN = 5; public double GameTime;
    public bool Dark, FogLevel, ProMode, HealthVisible;
    public int[] RowType = new int[6];    // 0 dirt/none, 1 lawn, 2 water, 3 roof
    public int[] Mowers = { -1, -1, -1, -1, -1, -1 };
    public double[] Danger = new double[6];
    public double[] BreachT = { -1, -1, -1, -1, -1, -1 };   // -1 unknown, >= 9000 holds the 40 s look-ahead, else game-s to breach
    public double[] HpLeft = { -1, -1, -1, -1, -1, -1 };    // -1 unknown (only with the SetSim hook)
    public bool SimKnown, SimExact;
    public List<Pv> Plants = new List<Pv>();
    public List<Zv> Zombies = new List<Zv>();
    public List<Ghost> Ghosts = new List<Ghost>();
    public List<Cv> Coins = new List<Cv>();
    public List<int> Blocked = new List<int>();
    public Dictionary<int, double[]> Vases = new Dictionary<int, double[]>();   // cell (row*10+col) -> {P(zombie), P(plant), look 0 ? / 1 leaf / 2 skull}
    public bool VaseActive; public int PickCell = -1; public bool PickWait; public string PickWhy = "";
    public double RemZ = -1, RemS = -1, RemFoot = -1, RemSquash = -1;
    public float FogEdgeX = -1;          // x where zombies come out of the fog (seen), -1 unknown
    public int HiddenTruth;              // zombies the game has hidden right now (fog / blackout): panel info only
    public double ErrAvg = -1; public int ErrN; public double LiveErr = -1; public int LiveN;
    public string Error = "";
  }

  static readonly object G = new object();
  static Snap cur;
  public static Snap Current { get { lock (G) return cur; } }
  public static string LastError = "";

  // ---------- main-thread state ----------
  class Seen1 { public float X; public int Row, Type; public bool Eating; public double T, Hp; }
  static readonly Dictionary<uint, double[]> track = new Dictionary<uint, double[]>();   // id -> {x, t, speed, samples}
  static Dictionary<uint, Seen1> prevVis = new Dictionary<uint, Seen1>();
  static readonly Dictionary<uint, Ghost> ghosts = new Dictionary<uint, Ghost>();
  static readonly Queue<double> errs = new Queue<double>();
  static int lastLevel = -1, frames; static double lastT = -1;
  static float fogEdge = -1;
  static DateTime snapAt = DateTime.MinValue, oddsAt = DateTime.MinValue, simAt = DateTime.MinValue, pruneAt = DateTime.MinValue;
  static Dictionary<int, double[]> odds = new Dictionary<int, double[]>(); static int oddsN = -1;
  static readonly double[] simBreach = new double[6], simHp = new double[6];

  // Optional exact hook from pro.cs (right after baseOut is filled): Guess.SetSim(baseOut);
  public static void SetSim(Dictionary<int, Bot.SimOut> baseOut) {
    try {
      if (baseOut == null) return;
      for (int r = 0; r < 6; r++) { simBreach[r] = 9999; simHp[r] = 0; }
      foreach (var kv in baseOut) {
        if (kv.Key < 0 || kv.Key > 5 || kv.Value == null) continue;
        simBreach[kv.Key] = kv.Value.Breach ? kv.Value.BreachT : 9999; simHp[kv.Key] = kv.Value.HpLeft;
      }
      simAt = DateTime.Now;
    } catch (Exception) { }
  }

  static double DefaultSpeed(string n) {   // px per game-s, same table as pro.cs ProSpeedOf
    switch (n) {
      case "POLE": case "FOOT": case "BALLOON": case "CAT": case "CAT2": case "CAT3": case "DOLPH": case "POGO": return 32;
      case "NEWS": return 22; case "DANCER": return 20; case "BOB": return 50; case "DIGGER": return 40;
      default: return 17;
    }
  }

  // Called once per main-loop tick with the fresh State (Viz.Publish does it). Never throws.
  public static void Frame(State s) {
    if (s == null || !s.HasBoard) return;
    try { FrameCore(s); } catch (Exception e) { LastError = e.GetType().Name + ": " + e.Message; }
  }

  static void FrameCore(State s) {
    double now = s.GameTime; var wall = DateTime.Now;
    if (s.Level != lastLevel || now < lastT - 1) {   // new level / retry: memory starts over
      ghosts.Clear(); track.Clear(); prevVis = new Dictionary<uint, Seen1>(); fogEdge = -1; errs.Clear(); odds = new Dictionary<int, double[]>(); oddsN = -1;
      lastLevel = s.Level; frames = 0;
    }
    lastT = now; frames++;
    bool dark = Bot.ScreenDark, fogLvl = s.Background == 3;
    var hidden = new HashSet<uint>(); var hiddenX = new Dictionary<uint, float>();
    foreach (var z in s.FogZombies) { hidden.Add(z.Id); hiddenX[z.Id] = z.X; }

    // 1. what is visible now: speeds, reappearing ghosts, where zombies come out of the fog
    var vis = new Dictionary<uint, Seen1>();
    foreach (var z in s.Zombies) {
      if (z.Charmed || z.X >= Bot.VISIBLE_X) continue;
      double[] t; bool fresh = false;
      if (!track.TryGetValue(z.Id, out t)) { t = new double[] { z.X, now, DefaultSpeed(Mem.ZomName(z.Type)), 0 }; track[z.Id] = t; fresh = true; }
      else {
        double dt = now - t[1];
        if (dt >= 0.5) {
          double v = (t[0] - z.X) / dt;
          if (v > 2 && v < 120) { t[2] = t[3] == 0 ? v : t[2] * 0.7 + v * 0.3; t[3]++; }
          t[0] = z.X; t[1] = now;
        }
      }
      Ghost g;
      if (ghosts.TryGetValue(z.Id, out g)) {   // it came back: score the guess, drop the ghost
        if (now - g.SeenT > 0.3) { errs.Enqueue(Math.Abs(g.EstX - z.X)); while (errs.Count > 40) errs.Dequeue(); }
        ghosts.Remove(z.Id);
        if (fogLvl && !dark && z.X < Bot.VISIBLE_X - 30) fogEdge = fogEdge < 0 ? z.X : fogEdge * 0.7f + z.X * 0.3f;
      } else if (fresh && frames > 5 && fogLvl && !dark && z.X < Bot.VISIBLE_X - 30) {
        fogEdge = fogEdge < 0 ? z.X : fogEdge * 0.8f + z.X * 0.2f;   // first sight well inside the lawn = it walked out of the fog here
      }
      vis[z.Id] = new Seen1 { X = z.X, Row = z.Row, Type = z.Type, Eating = z.Eating, T = now, Hp = Bot.ProMode ? Seen.Hp(z) : (Bot.HealthVisible ? z.Total : -1) };
    }

    // 2. visible last tick, gone now: into fog / blackout -> ghost (a death is seen as a death: no ghost)
    foreach (var kv in prevVis) {
      if (vis.ContainsKey(kv.Key) || ghosts.ContainsKey(kv.Key)) continue;
      if (!(dark || hidden.Contains(kv.Key))) continue;
      var p = kv.Value; double[] t; track.TryGetValue(kv.Key, out t);
      string n = Mem.ZomName(p.Type);
      ghosts[kv.Key] = new Ghost { Id = kv.Key, Row = p.Row, Type = p.Type, Name = n, LastX = p.X, EstX = p.X, SeenT = p.T,
                                   Speed = (float)(t != null && t[3] > 0 ? Math.Max(8, t[2]) : DefaultSpeed(n)), Eating = p.Eating, Dark = dark, Hp = p.Hp, Conf = 1 };
    }
    prevVis = vis;

    // 3. move every ghost along its guess, fade it
    List<uint> drop = null;
    foreach (var g in ghosts.Values) {
      double dt = Math.Max(0, now - g.SeenT);
      double walk = g.Eating ? Math.Max(0, dt - 3) : dt;   // was chewing: probably still on that plant for a moment
      g.Age = dt; g.EstX = (float)(g.LastX - g.Speed * walk);
      g.Band = (float)(10 + 0.25 * g.Speed * dt + (g.Eating ? 25 : 0));
      g.Conf = Math.Max(0, 1 - dt / GHOST_LIFE);
      float tx; g.TrueX = hiddenX.TryGetValue(g.Id, out tx) ? tx : float.NaN;
      if (g.Conf <= 0 || g.EstX < -50) { if (drop == null) drop = new List<uint>(); drop.Add(g.Id); }
    }
    if (drop != null) foreach (var id in drop) ghosts.Remove(id);
    if ((wall - pruneAt).TotalSeconds > 5) {   // forget speed tracks of zombies gone for good
      pruneAt = wall;
      var gone = track.Where(kv => now - kv.Value[1] > 30 && !ghosts.ContainsKey(kv.Key) && !vis.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
      foreach (var id in gone) track.Remove(id);
    }

    // 4. vase odds (planner's own numbers), refreshed every 0.25 s or when a vase opens
    if (s.Vases.Count > 0) {
      if (s.Vases.Count != oddsN || (wall - oddsAt).TotalSeconds >= 0.25) {
        oddsAt = wall; oddsN = s.Vases.Count;
        try { odds = VasePlanner.VaseOdds(s); } catch (Exception) { odds = new Dictionary<int, double[]>(); }
      }
    } else if (oddsN != 0) { odds = new Dictionary<int, double[]>(); oddsN = 0; }

    // 5. snapshot for the panel (at most every 80 ms)
    if ((wall - snapAt).TotalMilliseconds < 80) return;
    snapAt = wall;
    var sn = new Snap { At = wall, Level = s.Level, Bg = s.Background, Sun = s.Sun, Wave = s.Wave, Waves = s.Waves, Scene = s.Scene, GameTime = now,
                        Dark = dark, FogLevel = fogLvl, ProMode = Bot.ProMode, HealthVisible = Bot.HealthVisible, FogEdgeX = fogEdge, HiddenTruth = s.FogZombies.Count };
    sn.RowsN = (s.Background == 2 || s.Background == 3) ? 6 : 5;
    for (int r = 0; r < 6; r++) {
      bool water = false, lawn = false;
      for (int c = 0; c < 9; c++) { int q = s.Square[r, c]; if (q == 3) water = true; else if (q == 1 || q == 4) lawn = true; }
      sn.RowType[r] = water ? 2 : !lawn ? 0 : s.Background == 4 ? 3 : 1;
      sn.Mowers[r] = s.Mowers[r];
    }
    var ld = Bot.LaneDanger; if (ld != null) for (int r = 0; r < 6 && r < ld.Length; r++) sn.Danger[r] = ld[r];
    // simulator: exact hook if it ran in the last 1.5 s, else Pro's status line (same numbers, rounded)
    if ((wall - simAt).TotalSeconds < 1.5) {
      sn.SimKnown = sn.SimExact = true;
      for (int r = 0; r < 6; r++) { sn.BreachT[r] = simBreach[r]; sn.HpLeft[r] = simHp[r]; }
    } else if (Bot.ProMode) {
      string ps = Bot.ProStatus ?? ""; int i = ps.IndexOf("sim: ");
      if (i >= 0) {
        sn.SimKnown = true;
        for (int r = 0; r < 6; r++) sn.BreachT[r] = 9999;
        int end = ps.IndexOf(" | ", i); string part = end > i ? ps.Substring(i, end - i) : ps.Substring(i);
        foreach (Match m in Regex.Matches(part, @"(\d)@(\d+)s")) {
          int r = int.Parse(m.Groups[1].Value); if (r >= 0 && r < 6) sn.BreachT[r] = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }
      }
    }
    foreach (var p in s.Plants) sn.Plants.Add(new Pv { Row = p.Row, Col = p.Col, Type = p.Type, Hp = p.Hp, MaxHp = p.MaxHp });
    foreach (var z in s.Zombies) {
      Seen1 v; double[] t;
      sn.Zombies.Add(new Zv { Id = z.Id, Row = z.Row, Type = z.Type, Name = Mem.ZomName(z.Type), X = z.X, Eating = z.Eating, Charmed = z.Charmed,
                              Offscreen = z.X >= Bot.VISIBLE_X, Hp = vis.TryGetValue(z.Id, out v) ? v.Hp : -1,
                              Speed = track.TryGetValue(z.Id, out t) && t[3] > 0 ? t[2] : -1 });
    }
    foreach (var g in ghosts.Values.OrderByDescending(x => x.Conf))
      sn.Ghosts.Add(new Ghost { Id = g.Id, Row = g.Row, Type = g.Type, Name = g.Name, LastX = g.LastX, EstX = g.EstX, Band = g.Band, Speed = g.Speed,
                                SeenT = g.SeenT, Age = g.Age, Conf = g.Conf, Eating = g.Eating, Dark = g.Dark, Hp = g.Hp, TrueX = g.TrueX });
    foreach (var c in s.Suns)
      sn.Coins.Add(new Cv { X = c.X, Y = c.Y, Type = c.Type, Seed = c.Seed, Packet = c.IsPacket, Left = c.IsPacket ? Math.Max(0, (VasePlanner.PACKET_LIFE - c.Disappear) / 100.0) : -1 });
    sn.Blocked.AddRange(s.Blocked);
    sn.VaseActive = VasePlanner.Active && s.Vases.Count > 0;
    foreach (int k in s.Vases) {
      double[] o; if (odds.TryGetValue(k, out o)) sn.Vases[k] = o;
      else sn.Vases[k] = new double[] { -1, -1, s.LeafVases.Contains(k) ? 1 : s.SkullVases.Contains(k) ? 2 : 0 };
    }
    double[] rem; if (odds.TryGetValue(-1, out rem) && rem.Length >= 4) { sn.RemZ = rem[0]; sn.RemS = rem[1]; sn.RemFoot = rem[2]; sn.RemSquash = rem[3]; }
    string why = VasePlanner.LastWhy ?? "";
    if (sn.VaseActive && why.Length > 0) {
      var m = Regex.Match(why, @"r(\d)c(\d)");
      if (m.Success) { sn.PickCell = int.Parse(m.Groups[1].Value) * 10 + int.Parse(m.Groups[2].Value); sn.PickWait = why.StartsWith("wait"); }
      sn.PickWhy = why;
    }
    if (errs.Count > 0) { sn.ErrAvg = errs.Average(); sn.ErrN = errs.Count; }
    var live = sn.Ghosts.Where(g => !float.IsNaN(g.TrueX)).ToList();
    if (live.Count > 0) { sn.LiveErr = live.Average(g => Math.Abs(g.EstX - g.TrueX)); sn.LiveN = live.Count; }
    sn.Error = LastError;
    lock (G) cur = sn;
  }
}
