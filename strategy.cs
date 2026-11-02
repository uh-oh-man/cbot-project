// Strategy learning: each level the bot plays a parameterised strategy, scores the outcome,
// and either exploits its best strategy or experiments with a mutation of it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

public class Strat {
  public string Id;
  public double SunPerRow = 2, EconWeight = 1, PrebuildWeight = 1, WallWeight = 1, MineWeight = 1, InstantDanger = 1.1, SaveBias = 25;
  public int ShooterCol = 1;
  public int SunCol = 0;          // where sun makers start (0 = back row)
  public bool SunSpread = false;  // spread sun makers evenly across lanes instead of packing the safest ones
  public int Runs; public double FitSum; public string Parent = "-", Hypothesis = "baseline", Env = "day";
  public double Avg { get { return Runs == 0 ? 0 : FitSum / Runs; } }
  // Advantage = fitness minus the per-level baseline, rebuilt from strategy_runs.txt; PriorAdv/PriorN = weak prior from old raw scores.
  public int AdvN; public double AdvSum, AdvSq, PriorAdv, PriorN;
  public Dictionary<int, double[]> ByLevel = new Dictionary<int, double[]>();   // level -> {runs, advantage sum}
  public double N { get { return AdvN + PriorN; } }
  public double Adv { get { return N <= 0 ? 0 : (AdvSum + PriorAdv * PriorN) / N; } }
  public int RunsOn(int level) { double[] b; return ByLevel.TryGetValue(level, out b) ? (int)b[0] : 0; }

  public Strat Clone() { var s = (Strat)MemberwiseClone(); s.Runs = 0; s.FitSum = 0; s.AdvN = 0; s.AdvSum = 0; s.AdvSq = 0; s.PriorAdv = 0; s.PriorN = 0; s.ByLevel = new Dictionary<int, double[]>(); return s; }

  public string Name {
    get {
      var traits = new List<Tuple<double, string>> {
        Tuple.Create(EconWeight * SunPerRow / 2, "Greedy Gardener"), Tuple.Create(WallWeight, "Nut Fortress"),
        Tuple.Create(PrebuildWeight, "Pea Rush"), Tuple.Create(MineWeight, "Potato Prophet"),
        Tuple.Create(1.1 / InstantDanger, "Trigger-happy Cherry"), Tuple.Create(ShooterCol / 2.0, "Front-line Peas"),
        Tuple.Create(SaveBias / 25.0, "Patient Saver") };
      // Only traits the current loadout can actually express.
      var lo = Strategy.Loadout;
      Func<string, bool> has = role => lo.Count == 0 || lo.Any(sd => Roles.Is(sd, role));
      traits = traits.Where(t => (t.Item2 != "Nut Fortress" || has("WALL")) && (t.Item2 != "Potato Prophet" || has("MINE"))
                              && (t.Item2 != "Trigger-happy Cherry" || has("INSTANT")) && (t.Item2 != "Greedy Gardener" || has("SUN"))).ToList();
      string prefix = lo.Count(sd => sd >= 8 && sd <= 15) >= 2 ? "Shroom " : "";
      var top = traits.OrderByDescending(t => t.Item1).Take(2).ToList();
      if (top.Count == 0 || top[0].Item1 < 1.08) return prefix + "Balanced Botanist";
      return prefix + (top.Count > 1 && top[1].Item1 >= 1.08 ? top[0].Item2 + " x " + top[1].Item2 : top[0].Item2);
    }
  }
  public string Genes { get {
    return string.Format(CultureInfo.InvariantCulture, "sun/row {0:0.0} econ {1:0.00} prebuild {2:0.00} wall {3:0.00} mine {4:0.00} instant@{5:0.00} shooters@col{6} sun@col{8}{9} save {7:0}",
      SunPerRow, EconWeight, PrebuildWeight, WallWeight, MineWeight, InstantDanger, ShooterCol, SaveBias, SunCol, SunSpread ? " spread" : " packed"); } }
}

public static class Strategy {
  const string FILE = @"C:\cbot\strategies.txt";
  static readonly Random R = new Random();
  public static readonly List<Strat> Tried = new List<Strat>();
  public static Strat Cur = new Strat { Id = "S0" };
  public static int Generation;
  static int nextId = 1;
  // Per-level tallies feeding the fitness score.
  public static int MowersLost, PlantsLost;
  public static List<int> Loadout = new List<int>();   // plants brought this level (for naming)

  static void Say(string s) { Viz.AddEvent("STRAT " + s); }

  // Called when a new level starts: weigh known strategies and decide what to play.
  public static readonly string[] EnvNames = { "day", "night", "pool", "fog", "roof", "boss" };
  // ----- fair comparison: every run is scored against how hard ITS level is (per-level baseline B[level]) -----
  const string RUNS = @"C:\cbot\strategy_runs.txt";   // one line per scored run (append only)
  public static readonly Dictionary<int, double> Baseline = new Dictionary<int, double>();   // level -> EMA of fitness over all strategies
  const double B_ALPHA = 0.2, UCB_C = 1.0;
  static double sdPooled = 60;   // pooled sd of advantage within strategies (default until enough runs)
  static int curLevel = -1;
  // Scores one run: advantage vs the baseline BEFORE this run, then moves the baselines. Returns the advantage.
  // The first run on a level only sets its baseline (advantage 0): another level's difficulty says nothing about this one.
  static double ApplyRun(Strat t, int level, string env, double fit) {
    double b; bool has = Baseline.TryGetValue(level, out b);
    double adv = has ? fit - b : 0;
    Baseline[level] = has ? b + B_ALPHA * (fit - b) : fit;
    if (t != null) {
      t.AdvN++; t.AdvSum += adv; t.AdvSq += adv * adv;
      double[] bl; if (!t.ByLevel.TryGetValue(level, out bl)) t.ByLevel[level] = bl = new double[2];
      bl[0]++; bl[1] += adv;
    }
    return adv;
  }
  static void RecalcSd() {
    double ss = 0; int dof = 0;
    foreach (var t in Tried) if (t.AdvN >= 2) { double m = t.AdvSum / t.AdvN; ss += Math.Max(0, t.AdvSq - t.AdvN * m * m); dof += t.AdvN - 1; }
    sdPooled = dof >= 5 ? Math.Max(15, Math.Sqrt(ss / dof)) : 60;
  }
  // Expected advantage on this level: overall advantage, pulled toward its record on this very level once it has 2+ runs here.
  static double Est(Strat t, int level) {
    double[] bl; if (level >= 0 && t.ByLevel.TryGetValue(level, out bl) && bl[0] >= 2) return (bl[1] + 3 * t.Adv) / (bl[0] + 3);
    return t.Adv;
  }
  static double Ucb(Strat t, int level) { return t.N <= 0 ? 1000 : Est(t, level) + UCB_C * sdPooled / Math.Sqrt(t.N); }
  static double Lcb(Strat t, int level) { return t.N <= 0 ? -1000 : Est(t, level) - UCB_C * sdPooled / Math.Sqrt(t.N); }

  public static void ChooseForLevel(int level, int background) {
    MowersLost = 0; PlantsLost = 0; curLevel = level;
    string env = background >= 0 && background < EnvNames.Length ? EnvNames[background] : "other";
    if (Tried.Count == 0) { Cur = new Strat { Id = "S0", Env = env }; Tried.Add(Cur); nextId = 1; Say("no experience yet -> starting with the baseline (" + Cur.Name + ")"); return; }
    var pool = Tried.Where(t => t.Env == env).ToList();
    if (pool.Count == 0) {
      // New environment: transfer the most reliably good strategy from anywhere, then evolve a separate lineage here.
      var src = Tried.Where(t => t.N > 0).OrderByDescending(t => Lcb(t, -1)).FirstOrDefault() ?? Tried[0];
      Cur = src.Clone(); Cur.Id = "S" + nextId++; Cur.Env = env; Cur.Parent = src.Id; Cur.Hypothesis = "first time in " + env + ": try my " + src.Env + " champion here";
      Tried.Add(Cur); Save();
      Say("new environment: " + env.ToUpper() + "! no experience here - transferring " + src.Id + " '" + src.Name + "' and starting a " + env + " lineage");
      return;
    }
    var scored = pool.Where(t => t.N > 0).ToList();
    // Best = highest LOWER bound on advantage (a one-run fluke cannot be champion). Exploit = highest UPPER bound,
    // among strategies with 2+ runs on this level when there are any.
    var best = scored.OrderByDescending(t => Lcb(t, level)).FirstOrDefault() ?? pool[0];
    var here = scored.Where(t => t.RunsOn(level) >= 2).ToList();
    var ranked = (here.Count > 0 ? here : scored).OrderByDescending(t => Ucb(t, level)).ToList();
    double bl; string lvlBase = Baseline.TryGetValue(level, out bl) ? "level " + level + " baseline " + bl.ToString("0") + ", " : "new level " + level + ", ";
    string field = string.Join(" | ", ranked.Take(3).Select(t => t.Id + " " + t.Name + " adv " + Est(t, level).ToString("+0;-0") + " +-" + (sdPooled / Math.Sqrt(Math.Max(1, t.N))).ToString("0") + " (" + t.AdvN + " runs, " + t.RunsOn(level) + " here)"));
    Say("weighing (" + lvlBase + "sd " + sdPooled.ToString("0") + "): " + (field.Length > 0 ? field : "nothing scored yet"));
    double explore = Math.Max(0.15, 0.4 - 0.03 * pool.Sum(t => t.Runs));
    // Give a strategy a fair trial (3 runs), then call it bad only if even its optimistic advantage is below the champion's pessimistic one.
    bool lastWasBad = Cur.Env == env && Cur.N >= 3 && pool.Contains(Cur) && Cur != best && Ucb(Cur, level) < Lcb(best, level);
    bool stillTrialing = Cur.Env == env && Cur.Runs > 0 && Cur.Runs < 3 && pool.Contains(Cur);
    if (stillTrialing) {
      Say("TRIAL: giving " + Cur.Id + " '" + Cur.Name + "' run " + (Cur.Runs + 1) + " of 3 before judging it (avg so far " + Cur.Avg.ToString("0") + ")");
      return;
    }
    if (lastWasBad) Say(Cur.Id + " judged worse than " + best.Id + ": best case " + Ucb(Cur, level).ToString("0") + " < champion's worst case " + Lcb(best, level).ToString("0"));
    if (R.NextDouble() < explore || lastWasBad) {
      Cur = Mutate(best);
      Generation++;
      Say("EXPERIMENT gen " + Generation + ": " + Cur.Id + " '" + Cur.Name + "' from " + best.Id + " - hypothesis: " + Cur.Hypothesis);
    } else {
      var pick = ranked.FirstOrDefault() ?? best;
      Cur = pick;
      Say("EXPLOIT: sticking with " + Cur.Id + " '" + Cur.Name + "' (advantage " + Est(Cur, level).ToString("+0;-0") + " over " + Cur.AdvN + " runs, " + Cur.RunsOn(level) + " on this level)");
    }
  }

  static Strat Mutate(Strat b) {
    var s = b.Clone(); s.Id = "S" + nextId++; s.Parent = b.Id; s.Env = b.Env;
    var notes = new List<string>();
    int n = 1 + R.Next(3);
    var genes = Enumerable.Range(0, 10).OrderBy(x => R.Next()).Take(n).ToList();   // distinct traits per experiment
    foreach (int gene in genes) {
      double f = R.NextDouble() < 0.5 ? 0.75 : 1.3;
      string more = f > 1 ? "more" : "less";
      switch (gene) {
        case 0: s.SunPerRow = Clamp(s.SunPerRow + (f > 1 ? 0.5 : -0.5), 1, 3); notes.Add(more + " sunflowers"); break;
        case 1: s.EconWeight = Clamp(s.EconWeight * f, 0.4, 2.2); notes.Add(more + " greed"); break;
        case 2: s.PrebuildWeight = Clamp(s.PrebuildWeight * f, 0.3, 2.5); notes.Add(more + " early peas"); break;
        case 3: s.WallWeight = Clamp(s.WallWeight * f, 0.2, 2.5); notes.Add(more + " wall-nuts"); break;
        case 4: s.MineWeight = Clamp(s.MineWeight * f, 0.2, 2.5); notes.Add(more + " potato mines"); break;
        case 5: s.InstantDanger = Clamp(s.InstantDanger / f, 0.6, 2.2); notes.Add((f > 1 ? "earlier" : "later") + " cherry bombs"); break;
        case 6: s.ShooterCol = (int)Clamp(s.ShooterCol + (f > 1 ? 1 : -1), 1, 4); notes.Add(f > 1 ? "shooters further forward" : "shooters further back"); break;
        case 7: s.SaveBias = Clamp(s.SaveBias * f, 5, 60); notes.Add(f > 1 ? "more patience saving sun" : "spend sun faster"); break;
        case 8: s.SunCol = (int)Clamp(s.SunCol + (f > 1 ? 1 : -1), 0, 4); notes.Add("sun makers at column " + s.SunCol); break;
        case 9: s.SunSpread = !s.SunSpread; notes.Add(s.SunSpread ? "spread sun makers across lanes" : "pack sun makers in safe lanes"); break;
      }
    }
    s.Hypothesis = "what if " + string.Join(" + ", notes.Distinct()) + "?";
    Tried.Add(s);
    return s;
  }
  static double Clamp(double v, double lo, double hi) { return Math.Max(lo, Math.Min(hi, v)); }

  // Score the level just played with the current strategy.
  public static string LevelEnv = "day";   // environment of the level being played, set by the bot
  public static void LevelEnded(bool won, int wave, int waves) {
    if (Cur.Env != LevelEnv) { Say("not scoring " + Cur.Id + ": it is a " + Cur.Env + " strategy but this was a " + LevelEnv + " level"); return; }
    // Any win must beat any loss: losses score <= 0, a win scores 60..100 depending on how clean it was.
    double fit = won ? 100 - Math.Min(40, 10 * MowersLost + 0.5 * PlantsLost)
                     : -100 + 100.0 * wave / Math.Max(1, waves) - 10 * MowersLost;   // dying late is much better than dying early
    Cur.Runs++; Cur.FitSum += fit;
    int level = curLevel >= 0 ? curLevel : Learn.CurLevel;
    double b; double baseBefore = Baseline.TryGetValue(level, out b) ? b : double.NaN;
    var prevBest = Tried.Where(t => t.N > 0 && t.Env == Cur.Env).OrderByDescending(t => Lcb(t, level)).FirstOrDefault();
    double adv = ApplyRun(Cur, level, LevelEnv, fit); RecalcSd();
    if (!Bot.ProMode)
      try { File.AppendAllText(RUNS, string.Join("|", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Cur.Id, level, LevelEnv, fit.ToString("0.##", CultureInfo.InvariantCulture),
              won ? 1 : 0, wave, waves, MowersLost, PlantsLost) + Environment.NewLine); } catch { }
    Say(Cur.Id + " '" + Cur.Name + "' scored " + fit.ToString("0") + " (" + (won ? "won" : "died") + ", " + MowersLost + " mowers, " + PlantsLost + " plants lost) on level " + level
        + (double.IsNaN(baseBefore) ? " (first score here)" : ", baseline " + baseBefore.ToString("0") + " -> advantage " + adv.ToString("+0;-0")) + "; overall advantage " + Cur.Adv.ToString("+0;-0") + " over " + Cur.AdvN + " runs");
    var best = Tried.Where(t => t.N > 0 && t.Env == Cur.Env).OrderByDescending(t => Lcb(t, level)).First();
    if (best == Cur && prevBest != Cur && Cur.Parent != "-") Say("new champion! the hypothesis '" + Cur.Hypothesis + "' paid off");
    Save();
  }

  // Seed chooser affinity: plants that fit the current strategy's style score higher.
  public static double Affinity(int seed) {
    switch (seed) {
      case 3: case 23: return 30 * (Cur.WallWeight - 1);
      case 4: return 30 * (Cur.MineWeight - 1);
      case 2: case 17: case 20: return 30 * (1.1 / Cur.InstantDanger - 1);
      default: return 0;
    }
  }

  public static void Save() {
    if (Bot.ProMode) return;   // pro mode never writes learned knowledge
    try {
      File.WriteAllLines(FILE, Tried.Select(t => string.Join("|", t.Id, t.Parent, t.Runs, t.FitSum.ToString(CultureInfo.InvariantCulture),
        t.SunPerRow.ToString(CultureInfo.InvariantCulture), t.EconWeight.ToString(CultureInfo.InvariantCulture), t.PrebuildWeight.ToString(CultureInfo.InvariantCulture),
        t.WallWeight.ToString(CultureInfo.InvariantCulture), t.MineWeight.ToString(CultureInfo.InvariantCulture), t.InstantDanger.ToString(CultureInfo.InvariantCulture),
        t.ShooterCol, t.SaveBias.ToString(CultureInfo.InvariantCulture), t.Hypothesis.Replace("|", "/"), t.Env, t.SunCol, t.SunSpread ? 1 : 0)));
    } catch { }
  }
  public static void Load() {
    if (!File.Exists(FILE)) return;
    var ic = CultureInfo.InvariantCulture;
    foreach (var l in File.ReadAllLines(FILE)) {
      var p = l.Split('|');
      try {
        Tried.Add(new Strat { Id = p[0], Parent = p[1], Runs = int.Parse(p[2]), FitSum = double.Parse(p[3], ic), SunPerRow = double.Parse(p[4], ic),
          EconWeight = double.Parse(p[5], ic), PrebuildWeight = double.Parse(p[6], ic), WallWeight = double.Parse(p[7], ic), MineWeight = double.Parse(p[8], ic),
          InstantDanger = double.Parse(p[9], ic), ShooterCol = int.Parse(p[10]), SaveBias = double.Parse(p[11], ic), Hypothesis = p[12], Env = p.Length > 13 ? p[13] : "day", SunCol = p.Length > 14 ? int.Parse(p[14]) : 0, SunSpread = p.Length > 15 && p[15] == "1" });
      } catch { }
    }
    // Per-run records (id, level, env, fit) - replayed in order to rebuild the baselines and every advantage.
    var runs = new List<Tuple<string, int, string, double>>();
    if (File.Exists(RUNS))
      foreach (var l in File.ReadAllLines(RUNS)) {
        var p = l.Split('|');
        try { runs.Add(Tuple.Create(p[1], int.Parse(p[2]), p[3], double.Parse(p[4], ic))); } catch { }
      }
    // Runs older than the records only have a raw average, confounded by which level they met: kept as a weak prior
    // (advantage vs the environment's mean, worth at most 2 runs).
    var byId = new Dictionary<string, Strat>(); foreach (var t in Tried) byId[t.Id] = t;
    var recN = new Dictionary<string, int>(); var recFit = new Dictionary<string, double>();
    foreach (var r in runs) { int n; double f; recN.TryGetValue(r.Item1, out n); recFit.TryGetValue(r.Item1, out f); recN[r.Item1] = n + 1; recFit[r.Item1] = f + r.Item4; }
    var envMean = new Dictionary<string, double[]>();   // env -> {legacy runs, legacy fit}
    foreach (var t in Tried) {
      int n; double f; recN.TryGetValue(t.Id, out n); recFit.TryGetValue(t.Id, out f);
      int legacy = t.Runs - n; if (legacy <= 0) continue;
      double[] em; if (!envMean.TryGetValue(t.Env, out em)) envMean[t.Env] = em = new double[2];
      em[0] += legacy; em[1] += t.FitSum - f;
    }
    foreach (var t in Tried) {
      int n; double f; recN.TryGetValue(t.Id, out n); recFit.TryGetValue(t.Id, out f);
      int legacy = t.Runs - n; double[] em; if (legacy <= 0 || !envMean.TryGetValue(t.Env, out em) || em[0] <= 0) continue;
      t.PriorAdv = (t.FitSum - f) / legacy - em[1] / em[0]; t.PriorN = Math.Min(2, legacy);
    }
    foreach (var r in runs) { Strat t; byId.TryGetValue(r.Item1, out t); ApplyRun(t, r.Item2, r.Item3, r.Item4); }
    RecalcSd();
    // nextId = max(Id)+1, so an unparsable line can never cause an Id collision.
    int maxId = -1; foreach (var t in Tried) { int v; if (t.Id != null && t.Id.Length > 1 && int.TryParse(t.Id.Substring(1), out v)) maxId = Math.Max(maxId, v); }
    nextId = maxId + 1; Generation = Tried.Count(t => t.Parent != "-");
    // Current strategy: the most reliable one of the environment being played (day until the bot sees the board).
    var inEnv = Tried.Where(t => t.Env == LevelEnv).ToList();
    if (Tried.Count > 0) Cur = (inEnv.Count > 0 ? inEnv : Tried).OrderByDescending(t => Lcb(t, -1)).ThenByDescending(t => t.Avg).First();
  }
}
