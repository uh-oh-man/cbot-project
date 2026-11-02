// seen.cs - cbot v2: what a player could read off the screen (Seen), and simulator calibration logging (SimCal).
// Seen is Pro's only window onto HP: exact numbers only when the mod's health bars are drawn, otherwise the zombie's kind
// (its fixed max HP) times the damage state its sprite shows. SimCal only REPORTS how far the sim is off; Pro never reads it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

public static class Seen {
  const int WALLNUT = 3, CHOMP = 6, TALL = 23, PUMPKIN = 30;
  public const double CHOMP_BUSY = 40;   // a chomper chews ~40 game-s after a bite (visible: it sits there chewing)

  // ---------- Zombie HP as a player judges it ----------
  class HpC { public Zom Z; public double V; }
  static readonly Dictionary<uint, HpC> hpCache = new Dictionary<uint, HpC>();       // per frame: keyed by id, valid only for the same Zom object
  static readonly Dictionary<uint, int[]> armor0 = new Dictionary<uint, int[]>();    // id -> {helm, shield} as first seen = the intact armour of that kind
  // Armour strength by kind (original game's stats): what an intact cone / bucket / door is worth.
  static double HelmMax(string n) {
    switch (n) { case "CONE": return 370; case "PAIL": return 1100; case "FOOT": case "REGEN": return 1400; case "DIGGER": return 100; default: return 0; }
  }
  static double ShieldMax(string n) {
    switch (n) { case "DOOR": return 1100; case "NEWS": return 150; case "LADDER": return 500; default: return 0; }
  }
  // Three visible damage stages of armour or a wall: 1 intact, 2 damaged, 3 badly damaged (0 = gone).
  public static int Stage(double cur, double max) {
    if (cur <= 0) return 0;
    if (max <= 0) return 1;
    double f = cur / max;
    return f > 2 / 3.0 ? 1 : f > 1 / 3.0 ? 2 : 3;
  }
  // What a stage is worth as a fraction of full: intact counts as full (fresh is the common case), the others at about the middle of their band.
  public static double StageVal(int st) { return st == 1 ? 1.0 : st == 2 ? 0.5 : st == 3 ? 0.2 : 0; }

  public static double Hp(Zom z) {
    if (z == null) return 0;
    if (Bot.HealthVisible) return z.Total;   // the mod draws health bars: the exact number is on screen
    HpC c;
    if (hpCache.TryGetValue(z.Id, out c) && ReferenceEquals(c.Z, z)) return c.V;
    int[] a0;
    if (!armor0.TryGetValue(z.Id, out a0)) { a0 = new[] { z.Helm, z.Shield }; armor0[z.Id] = a0; }
    else { if (z.Helm > a0[0]) a0[0] = z.Helm; if (z.Shield > a0[1]) a0[1] = z.Shield; }   // a regrown helmet (REGEN) is intact again
    string n = Mem.ZomName(z.Type);
    double body = z.MaxHp > 0 ? z.MaxHp : 270;   // the kind's fixed max: known from what it is
    // Body sprite: intact above 2/3, arm gone below that, head gone at 0 (dying). Armless is judged a bit under half.
    double v = z.Hp <= 0 ? 0 : z.Hp > body * 2 / 3.0 ? body : body * 0.45;
    if (z.Helm > 0) { double hm = Math.Max(HelmMax(n), a0[0]); v += hm * StageVal(Stage(z.Helm, hm)); }
    if (z.Shield > 0) { double sm = Math.Max(ShieldMax(n), a0[1]); v += sm * StageVal(Stage(z.Shield, sm)); }
    if (c == null) { c = new HpC(); hpCache[z.Id] = c; }
    c.Z = z; c.V = v;
    return v;
  }
  public static bool HasShield(Zom z) { return z != null && z.Shield > 0; }   // a door / newspaper / ladder is plainly visible
  public static bool HasHelm(Zom z) { return z != null && z.Helm > 0; }

  // ---------- Plants: only wall-nut, tall-nut and pumpkin show damage (3 crack states) ----------
  static bool Cracks(int t) { return t == WALLNUT || t == TALL || t == PUMPKIN; }
  public static int PlantStage(Plt p) { return p == null ? 0 : Cracks(p.Type) && p.MaxHp > 0 ? Stage(p.Hp, p.MaxHp) : 1; }
  public static double PlantHp(Plt p) {
    if (p == null) return 0;
    double full = p.MaxHp > 0 ? p.MaxHp : 300;
    return Cracks(p.Type) ? full * StageVal(PlantStage(p)) : full;   // no visible damage on other plants: assume full
  }
  public static double ExactPlantHp(Plt p) { return p == null ? 0 : p.Hp; }   // the learning bot's sim keeps its old behaviour

  // ---------- Plant timers a player sees: mine arming (it pops up), chomper chewing ----------
  static readonly Dictionary<uint, double> plantBorn = new Dictionary<uint, double>();   // id -> game time first seen
  static readonly Dictionary<uint, double> chompFree = new Dictionary<uint, double>();   // id -> game time it stops chewing
  // Seconds since this plant appeared. Plants already on the lawn when the bot (re)started count as old (a mine there is up).
  public static double PlantAge(Plt p, double now) { double b; return p != null && plantBorn.TryGetValue(p.Id, out b) ? Math.Max(0, now - b) : 0; }
  public static double ChompBusy(Plt p, double now) { double f; return p != null && chompFree.TryGetValue(p.Id, out f) ? Math.Max(0, f - now) : 0; }

  // ---------- Per-frame observation (idempotent per State object) ----------
  static State last, lastBoard; static double lastGT = -1; static int lastLevel = -1, lastScene = -1; static bool primed;
  static DateTime frameAt = DateTime.MinValue, pruneAt = DateTime.MinValue;
  static readonly Dictionary<uint, DateTime> zSeen = new Dictionary<uint, DateTime>();   // zombie id -> wall time last seen (fog included)
  static List<Zom> prevZ = new List<Zom>();
  static readonly int[] mowers = { -1, -1, -1, -1, -1, -1 };
  public static void Frame(State s) {
    if (s == null || ReferenceEquals(s, last)) return;
    last = s;
    try { Observe(s); } catch (Exception) { }   // observation must never stop the bot
  }
  static void Observe(State s) {
    var wall = DateTime.Now; frameAt = wall;
    hpCache.Clear();
    // Game over: the lane that got through is the one whose zombie was nearest the house in the last in-play frame.
    if (s.Scene == 4 && lastScene == 3 && lastBoard != null) {
      var lz = lastBoard.Zombies.Where(z => !z.Charmed).OrderBy(z => z.X).FirstOrDefault();
      SimCal.Actual(lastBoard, lz == null ? -1 : lz.Row, "OVER", lz == null ? "-" : Mem.ZomName(lz.Type) + "@" + lz.X.ToString("0", CultureInfo.InvariantCulture));
      lastBoard = null;
    }
    lastScene = s.Scene;
    if (s.HasBoard) {
      // A new level or a retry restarts the game clock: per-level memory starts over.
      if (s.GameTime < lastGT - 1 || s.Level != lastLevel) NewLevel(s);
      lastGT = s.GameTime; lastLevel = s.Level;
      foreach (var z in s.Zombies) zSeen[z.Id] = wall;
      foreach (var z in s.FogZombies) zSeen[z.Id] = wall;
      if (s.Scene == 3) Play(s);
    }
    if ((wall - pruneAt).TotalSeconds >= 5) {
      pruneAt = wall;
      foreach (var k in zSeen.Where(kv => (wall - kv.Value).TotalSeconds > 30).Select(kv => kv.Key).ToList()) zSeen.Remove(k);
      foreach (var k in armor0.Keys.Where(x => !zSeen.ContainsKey(x)).ToList()) armor0.Remove(k);
    }
    SimCal.Tick(s);
  }
  static void NewLevel(State s) {
    plantBorn.Clear(); chompFree.Clear(); prevZ = new List<Zom>(); lastBoard = null;
    for (int r = 0; r < 6; r++) mowers[r] = -1;
    SimCal.NewLevel(s);
  }
  static void Play(State s) {
    double now = s.GameTime;
    var ids = new HashSet<uint>();
    foreach (var p in s.Plants) {
      ids.Add(p.Id);
      if (plantBorn.ContainsKey(p.Id)) continue;
      plantBorn[p.Id] = primed ? now : now - 1e4;
      if (primed) SimCal.Actual(s, p.Row, "PLANT", Mem.SeedName(p.Type) + " c" + p.Col);   // the lane changed: predictions made before this are confounded
    }
    primed = true;
    if (plantBorn.Count > ids.Count) foreach (var k in plantBorn.Keys.Where(x => !ids.Contains(x)).ToList()) { plantBorn.Remove(k); chompFree.Remove(k); }
    // A chomper bit: a zombie in reach last frame is gone now (not into fog, no mower running there) - it is chewing.
    if (prevZ.Count > 0 && s.Plants.Any(p => p.Type == CHOMP)) {
      var nowIds = new HashSet<uint>(s.Zombies.Select(z => z.Id));
      foreach (var z in s.FogZombies) nowIds.Add(z.Id);
      foreach (var p in s.Plants) {
        if (p.Type != CHOMP || ChompBusy(p, now) > 0 || p.Row < 0 || p.Row > 5 || s.Mowers[p.Row] == 2) continue;
        double px = Bot.CellX(p.Col);
        if (prevZ.Any(z => z.Row == p.Row && !z.Charmed && z.Hp > 0 && !nowIds.Contains(z.Id) && Mem.ZomName(z.Type) != "GARG" && z.X + 30 - px > -40 && z.X + 30 - px < 110))
          chompFree[p.Id] = now + CHOMP_BUSY;
      }
    }
    for (int r = 0; r < 6; r++) {
      if (mowers[r] == 1 && s.Mowers[r] == 2) {
        var lz = s.Zombies.Where(z => z.Row == r && !z.Charmed).OrderBy(z => z.X).FirstOrDefault();
        SimCal.Actual(s, r, "MOWER", lz == null ? "-" : Mem.ZomName(lz.Type) + "@" + lz.X.ToString("0", CultureInfo.InvariantCulture));
      }
      mowers[r] = s.Mowers[r];
    }
    prevZ = s.Zombies; lastBoard = s;
  }

  // Drop ids of zombies not seen for 30 s from any per-zombie dictionary (proTrack, fullHp...). Throttled per dictionary.
  static readonly Dictionary<object, DateTime> pruned = new Dictionary<object, DateTime>();
  public static void Prune<T>(Dictionary<uint, T> d) {
    if (d == null || d.Count == 0) return;
    var now = DateTime.Now;
    if ((now - frameAt).TotalSeconds > 5) return;   // Frame is not being called: gone and unobserved look the same, so keep everything
    DateTime at;
    if (pruned.TryGetValue(d, out at) && (now - at).TotalSeconds < 5) return;
    pruned[d] = now;
    List<uint> drop = null;
    foreach (var k in d.Keys) if (!zSeen.ContainsKey(k)) { if (drop == null) drop = new List<uint>(); drop.Add(k); }   // zSeen itself forgets after 30 s
    if (drop != null) foreach (var k in drop) d.Remove(k);
  }
}

// Simulator calibration: what the base sim predicted per lane, against what happened. Append-only text, buffered.
// simcal.log lines (game seconds, per level segment started by S):
//   S <wallclock> <lvl> <bg>                                  new level / retry / restart
//   P <t> <lvl> <row> <breachT> <hpLeft> <lost> <n> <lead>@<x> <v> <mower>     base prediction (every 2 game-s per lane)
//   Z <t> <lvl> <row> <id> <kind> <x0> <v> <predX|DEAD>      where the sim puts this zombie at t+5
//   X <t> <lvl> <row> <id> <kind> <t0> <realX|FOG|GONE>       where it really was at t0+5
//   A <t> <lvl> <row> MOWER|OVER|PLANT <info>                  actual events (row -1 = unknown)
public static class SimCal {
  const string LOG = @"C:\cbot\simcal.log", REPORT = @"C:\cbot\simcal_report.txt";
  public const double PROBE_T = 5, EVERY = 2;
  static readonly CultureInfo IC = CultureInfo.InvariantCulture;
  static readonly StringBuilder buf = new StringBuilder();
  static readonly double[] at = { -99, -99, -99, -99, -99, -99 };
  static DateTime flushAt = DateTime.Now, reportAt = DateTime.Now, sizeAt = DateTime.MinValue;
  static int busy;
  class Probe { public uint Id; public int Row; public string N; public double T0, Due; }
  static readonly List<Probe> probes = new List<Probe>();

  // Is a calibration record due for this lane? (Pro then runs its base sim with a probe.)
  public static bool Due(State s, int row) { return row >= 0 && row < 6 && (s.GameTime - at[row] >= EVERY || s.GameTime < at[row]); }

  public static void Predict(State s, int row, Bot.SimOut b, List<Zom> live, Dictionary<uint, double> spd, Dictionary<uint, double> probeX) {
    if (row < 0 || row >= 6 || b == null) return;
    at[row] = s.GameTime;
    try {
      var lz = live.Where(z => z.Row == row).OrderBy(z => z.X).ToList();
      var lead = lz.FirstOrDefault();
      double v = 0; if (lead != null) spd.TryGetValue(lead.Id, out v);
      buf.AppendFormat(IC, "P {0:0.00} {1} {2} {3:0.0} {4:0} {5:0} {6} {7}@{8:0} {9:0.0} {10}\n", s.GameTime, s.Level, row, b.BreachT, b.HpLeft, b.Lost, lz.Count,
                       lead == null ? "-" : Mem.ZomName(lead.Type), lead == null ? 0 : lead.X, v, s.Mowers[row]);
      if (probeX == null) return;
      foreach (var z in lz.Take(4)) {
        if (probes.Count >= 200) break;
        double px, zv = 0; spd.TryGetValue(z.Id, out zv);
        string pred = probeX.TryGetValue(z.Id, out px) ? px.ToString("0", IC) : "DEAD";
        buf.AppendFormat(IC, "Z {0:0.00} {1} {2} {3} {4} {5:0} {6:0.0} {7}\n", s.GameTime, s.Level, row, z.Id, Mem.ZomName(z.Type), z.X, zv, pred);
        probes.Add(new Probe { Id = z.Id, Row = row, N = Mem.ZomName(z.Type), T0 = s.GameTime, Due = s.GameTime + PROBE_T });
      }
    } catch (Exception) { }
  }

  public static void Actual(State s, int row, string kind, string info) {
    if (!Bot.ProMode || s == null) return;
    buf.AppendFormat(IC, "A {0:0.00} {1} {2} {3} {4}\n", s.GameTime, s.Level, row, kind, info);
    if (kind != "PLANT") Flush();   // a breach is the record that matters: get it on disk now
  }
  public static void NewLevel(State s) {
    probes.Clear();
    for (int r = 0; r < 6; r++) at[r] = -99;
    if (!Bot.ProMode) return;
    buf.AppendFormat(IC, "S {0} {1} {2}\n", DateTime.Now.ToString("yyyy-MM-dd_HH:mm:ss", IC), s.Level, s.Background);
  }

  // Every frame (via Seen.Frame): resolve due probes, flush every ~2 s, rebuild the report every 5 min (off the main thread).
  public static void Tick(State s) {
    if (probes.Count > 0 && s.HasBoard && s.Scene == 3) {
      double now = s.GameTime;
      for (int i = probes.Count - 1; i >= 0; i--) {
        var p = probes[i];
        if (p.Due > now) continue;
        probes.RemoveAt(i);
        if (now - p.Due > 2) continue;   // paused bot / long gap: the reading would not be at +5 s
        string real = "GONE";
        foreach (var z in s.Zombies) if (z.Id == p.Id) { real = z.X.ToString("0", IC); break; }
        if (real == "GONE") foreach (var z in s.FogZombies) if (z.Id == p.Id) { real = "FOG"; break; }
        buf.AppendFormat(IC, "X {0:0.00} {1} {2} {3} {4} {5:0.00} {6}\n", now, s.Level, p.Row, p.Id, p.N, p.T0, real);
      }
    }
    var wall = DateTime.Now;
    if (buf.Length > 0 && (wall - flushAt).TotalSeconds >= 2) Flush();
    if (Bot.ProMode && (wall - reportAt).TotalMinutes >= 5 && Interlocked.CompareExchange(ref busy, 1, 0) == 0) {
      reportAt = wall; Flush();
      ThreadPool.QueueUserWorkItem(o => { try { BuildReport(); } catch (Exception) { } finally { Interlocked.Exchange(ref busy, 0); } });
    }
  }
  public static void Flush() {
    flushAt = DateTime.Now;
    if (buf.Length == 0) return;
    try {
      if ((flushAt - sizeAt).TotalSeconds > 60) {   // keep the log bounded: one rotation at 40 MB
        sizeAt = flushAt;
        var fi = new FileInfo(LOG);
        if (fi.Exists && fi.Length > 40L << 20 && busy == 0) { File.Copy(LOG, LOG + ".1", true); File.Delete(LOG); }
      }
      File.AppendAllText(LOG, buf.ToString()); buf.Clear();
    } catch (Exception) { if (buf.Length > 1 << 20) buf.Clear(); }   // locked file: retry next flush, but never grow without bound
  }

  // ---------- Report (background thread: reads only the log files, touches no shared state) ----------
  class Seg { public List<double[]>[] P = Lists<double[]>(); public List<double>[] Plant = Lists<double>(), Act = Lists<double>(); public double Last; }
  static List<T>[] Lists<T>() { var a = new List<T>[6]; for (int i = 0; i < 6; i++) a[i] = new List<T>(); return a; }
  static double D(string t) { double v; return double.TryParse(t, NumberStyles.Float, IC, out v) ? v : double.NaN; }
  static List<string> ReadTail(string path, long max) {
    var list = new List<string>();
    if (!File.Exists(path)) return list;
    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
      bool cut = fs.Length > max;
      if (cut) fs.Seek(fs.Length - max, SeekOrigin.Begin);
      using (var sr = new StreamReader(fs)) { string l; if (cut) sr.ReadLine(); while ((l = sr.ReadLine()) != null) list.Add(l); }
    }
    return list;
  }
  // lane stats: 0 preds, 1 breach predicted, 2 clean hits, 3 sum err, 4 sum |err|, 5 confounded hits, 6 averted (planted after), 7 false alarms,
  //             8 confounded misses, 9 clean misses, 10 correct holds
  static void EvalSeg(Seg g, double[][] lane, double[][] hz) {
    for (int r = 0; r < 6; r++) foreach (var pr in g.P[r]) {
      double t0 = pr[0], brk = pr[1], ta = -1;
      foreach (var a in g.Act[r]) if (a >= t0 && (ta < 0 || a < ta)) ta = a;
      bool hasA = ta >= 0 && ta <= t0 + 45;
      if (!hasA && t0 + 45 > g.Last) continue;   // window not fully seen (level won / ended / reload)
      double end = hasA ? ta : brk < 9000 ? t0 + brk : t0 + 40;
      bool conf = g.Plant[r].Any(tp => tp > t0 && tp <= end);
      var L = lane[r]; L[0]++;
      if (brk < 9000) {
        L[1]++;
        if (hasA) {
          if (conf) { L[5]++; continue; }
          double err = ta - (t0 + brk); L[2]++; L[3] += err; L[4] += Math.Abs(err);
          int bk = brk < 10 ? 0 : brk < 20 ? 1 : 2; hz[bk][0]++; hz[bk][1] += err; hz[bk][2] += Math.Abs(err);
        } else if (conf) L[6]++; else L[7]++;
      } else if (hasA && ta <= t0 + 40) { if (conf) L[8]++; else L[9]++; }
      else L[10]++;
    }
  }
  static void BuildReport() {
    var lines = ReadTail(LOG + ".1", 15L << 20); lines.AddRange(ReadTail(LOG, 30L << 20));
    var lane = new double[6][]; for (int i = 0; i < 6; i++) lane[i] = new double[11];
    var hz = new[] { new double[3], new double[3], new double[3] };
    // per kind: 0 n, 1 sim px moved, 2 real px moved, 3 sum |x err|, 4 sim dead but alive, 5 sim alive but gone, 6 both gone
    var kind = new Dictionary<string, double[]>();
    var zp = new Dictionary<string, double[]>();   // "id@t0" -> {x0, predX or NaN}
    var seg = new Seg(); int segs = 0;
    foreach (var l in lines) {
      var t = l.Split(' ');
      if (t.Length < 4) continue;
      if (t[0] == "S") { EvalSeg(seg, lane, hz); seg = new Seg(); zp.Clear(); segs++; continue; }
      double tm = D(t[1]); int r; if (double.IsNaN(tm) || !int.TryParse(t[3], out r)) continue;
      seg.Last = Math.Max(seg.Last, tm);
      bool okRow = r >= 0 && r < 6;
      if (t[0] == "P" && okRow && t.Length > 4) seg.P[r].Add(new[] { tm, D(t[4]) });
      else if (t[0] == "A" && okRow && t.Length > 4) { if (t[4] == "PLANT") seg.Plant[r].Add(tm); else seg.Act[r].Add(tm); }
      else if (t[0] == "Z" && t.Length > 8) zp[t[4] + "@" + t[1]] = new[] { D(t[6]), t[8] == "DEAD" ? double.NaN : D(t[8]) };
      else if (t[0] == "X" && t.Length > 7) {
        double[] z; if (!zp.TryGetValue(t[4] + "@" + t[6], out z)) continue;
        zp.Remove(t[4] + "@" + t[6]);
        if (t[7] == "FOG") continue;
        double[] k; if (!kind.TryGetValue(t[5], out k)) { k = new double[7]; kind[t[5]] = k; }
        bool simDead = double.IsNaN(z[1]);
        if (t[7] == "GONE") { if (simDead) k[6]++; else k[5]++; continue; }
        double rx = D(t[7]); if (double.IsNaN(rx)) continue;
        if (simDead) { k[4]++; continue; }
        k[0]++; k[1] += z[0] - z[1]; k[2] += z[0] - rx; k[3] += Math.Abs(rx - z[1]);
      }
    }
    EvalSeg(seg, lane, hz);
    var sb = new StringBuilder();
    sb.AppendLine("SIM CALIBRATION REPORT " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", IC) + "  (" + lines.Count + " log lines, " + segs + " level segments)");
    sb.AppendLine("Report only: Pro is non-learning and never reads this file.");
    sb.AppendLine();
    sb.AppendLine("ZOMBIE MOTION at +" + PROBE_T + " s, per kind (speed bias = real px moved / sim px moved; >1 means the sim is too slow)");
    sb.AppendLine(string.Format(IC, "{0,-8} {1,6} {2,9} {3,9} {4,7} {5,9} {6,10} {7,10} {8,9}", "kind", "n", "sim px", "real px", "bias", "|x err|", "simDead", "simAlive", "bothGone"));
    foreach (var kv in kind.OrderByDescending(x => x.Value[0])) {
      var k = kv.Value;
      sb.AppendLine(string.Format(IC, "{0,-8} {1,6:0} {2,9:0.0} {3,9:0.0} {4,7} {5,9:0.0} {6,10:0} {7,10:0} {8,9:0}", kv.Key, k[0],
        k[0] > 0 ? k[1] / k[0] : 0, k[0] > 0 ? k[2] / k[0] : 0, k[0] > 0 && Math.Abs(k[1]) > 1e-6 ? (k[2] / k[1]).ToString("0.00", IC) : "-",
        k[0] > 0 ? k[3] / k[0] : 0, k[4], k[5], k[6]));
    }
    sb.AppendLine("  simDead = sim killed it within 5 s but it was alive; simAlive = sim kept it but it was gone (killed, mowed, or bombed).");
    sb.AppendLine();
    sb.AppendLine("LANE BREACH TIME (actual = mower fired / game over in that lane; err = actual - predicted, s; confounded = a plant went into the lane first)");
    sb.AppendLine(string.Format(IC, "{0,-4} {1,6} {2,7} {3,6} {4,9} {5,9} {6,6} {7,8} {8,8} {9,6} {10,8} {11,7}", "lane", "preds", "brkPred", "hits", "mean err", "mean|err|", "conf", "averted", "falseAl", "miss", "missConf", "holdOk"));
    for (int r = 0; r < 6; r++) {
      var L = lane[r]; if (L[0] == 0) continue;
      sb.AppendLine(string.Format(IC, "{0,-4} {1,6:0} {2,7:0} {3,6:0} {4,9} {5,9} {6,6:0} {7,8:0} {8,8:0} {9,6:0} {10,8:0} {11,7:0}", r, L[0], L[1], L[2],
        L[2] > 0 ? (L[3] / L[2]).ToString("0.0", IC) : "-", L[2] > 0 ? (L[4] / L[2]).ToString("0.0", IC) : "-", L[5], L[6], L[7], L[9], L[8], L[10]));
    }
    sb.AppendLine();
    sb.AppendLine("BREACH ERROR BY HORIZON (clean hits only)");
    string[] hn = { "<10 s", "10-20 s", "20-40 s" };
    for (int i = 0; i < 3; i++) sb.AppendLine(string.Format(IC, "{0,-8} n {1,5:0}  mean err {2,7}  mean|err| {3,7}", hn[i], hz[i][0],
      hz[i][0] > 0 ? (hz[i][1] / hz[i][0]).ToString("0.0", IC) : "-", hz[i][0] > 0 ? (hz[i][2] / hz[i][0]).ToString("0.0", IC) : "-"));
    sb.AppendLine("  Predictions repeat every 2 s per lane, so counts are correlated; averted = sim said breach, a plant went in, no breach followed.");
    File.WriteAllText(REPORT, sb.ToString());
  }
}
