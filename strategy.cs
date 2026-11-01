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

  public Strat Clone() { var s = (Strat)MemberwiseClone(); s.Runs = 0; s.FitSum = 0; return s; }

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
  public static void ChooseForLevel(int level, int background) {
    MowersLost = 0; PlantsLost = 0;
    string env = background >= 0 && background < EnvNames.Length ? EnvNames[background] : "other";
    if (Tried.Count == 0) { Cur = new Strat { Id = "S0", Env = env }; Tried.Add(Cur); Say("no experience yet -> starting with the baseline (" + Cur.Name + ")"); return; }
    var pool = Tried.Where(t => t.Env == env).ToList();
    if (pool.Count == 0) {
      // New environment: transfer the best strategy from anywhere, then evolve a separate lineage here.
      var src = Tried.Where(t => t.Runs > 0).OrderByDescending(t => t.Avg).FirstOrDefault() ?? Tried[0];
      Cur = src.Clone(); Cur.Id = "S" + nextId++; Cur.Env = env; Cur.Parent = src.Id; Cur.Hypothesis = "first time in " + env + ": try my " + src.Env + " champion here";
      Tried.Add(Cur); Save();
      Say("new environment: " + env.ToUpper() + "! no experience here - transferring " + src.Id + " '" + src.Name + "' and starting a " + env + " lineage");
      return;
    }
    var ranked = pool.Where(t => t.Runs > 0).OrderByDescending(t => t.Avg + 15.0 / Math.Sqrt(t.Runs)).ToList();
    var best = pool.Where(t => t.Runs > 0).OrderByDescending(t => t.Avg).FirstOrDefault() ?? pool[0];
    string field = string.Join(" | ", ranked.Take(3).Select(t => t.Id + " " + t.Name + " avg " + t.Avg.ToString("0") + " (" + t.Runs + " runs)"));
    Say("weighing: " + (field.Length > 0 ? field : "nothing scored yet"));
    double explore = Math.Max(0.15, 0.4 - 0.03 * pool.Sum(t => t.Runs));
    // Give a strategy a fair trial (3 runs) before calling it bad, and only if it trails the best clearly.
    bool lastWasBad = Cur.Env == env && Cur.Runs >= 3 && Cur.Avg < best.Avg - 30;
    bool stillTrialing = Cur.Env == env && Cur.Runs > 0 && Cur.Runs < 3 && pool.Contains(Cur);
    if (stillTrialing) {
      Say("TRIAL: giving " + Cur.Id + " '" + Cur.Name + "' run " + (Cur.Runs + 1) + " of 3 before judging it (avg so far " + Cur.Avg.ToString("0") + ")");
      return;
    }
    if (R.NextDouble() < explore || lastWasBad) {
      Cur = Mutate(best);
      Generation++;
      Say("EXPERIMENT gen " + Generation + ": " + Cur.Id + " '" + Cur.Name + "' from " + best.Id + " - hypothesis: " + Cur.Hypothesis);
    } else {
      var pick = ranked.FirstOrDefault() ?? best;
      Cur = pick;
      Say("EXPLOIT: sticking with " + Cur.Id + " '" + Cur.Name + "' (avg " + Cur.Avg.ToString("0") + " over " + Cur.Runs + " runs)");
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
    Say(Cur.Id + " '" + Cur.Name + "' scored " + fit.ToString("0") + " (" + (won ? "won" : "died") + ", " + MowersLost + " mowers, " + PlantsLost + " plants lost) -> avg " + Cur.Avg.ToString("0"));
    var best = Tried.Where(t => t.Runs > 0 && t.Env == Cur.Env).OrderByDescending(t => t.Avg).First();
    if (best == Cur && Cur.Parent != "-" && Cur.Runs == 1 && Cur.Avg > 50) Say("new champion! the hypothesis '" + Cur.Hypothesis + "' paid off");
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
    nextId = Tried.Count + 1; Generation = Tried.Count(t => t.Parent != "-");
    if (Tried.Count > 0) Cur = Tried.OrderByDescending(t => t.Avg).First();
  }
}
