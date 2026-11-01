using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

// Post-mortem learning: every time a lane breaks (mower fires / game over) the bot looks at what was on screen at
// that moment, names the cause, and nudges the one behaviour responsible. Corrections are kept per environment
// (day, night, pool, ...), bounded, and saved, so a lesson learned on 2-9 does not have to be relearned.
public static class Diag {
  const string FILE = @"C:\cbot\diag.txt";
  public class Fix { public double Urgency = 1, BombEager = 1, Spend = 1, Econ = 1; public int Breaches, Wins; }
  static readonly Dictionary<string, Fix> fixes = new Dictionary<string, Fix>();
  public static Fix Cur { get { return For(Strategy.LevelEnv); } }
  static Fix For(string env) { Fix f; if (!fixes.TryGetValue(env, out f)) fixes[env] = f = new Fix(); return f; }
  static double Clamp(double v, double lo, double hi) { return Math.Max(lo, Math.Min(hi, v)); }

  // Called the moment a lane breaks. Everything here is what a player could see on screen.
  public static void Breach(int row, int shooters, int sun, bool bombReady, int sunMakers, int lanes, int zombiesInLane, int wave, int waves) {
    var f = Cur; f.Breaches++;
    var causes = new List<string>();
    if (shooters == 0) { f.Urgency = Clamp(f.Urgency * 1.1, 0.7, 2.5); causes.Add("EMPTY LANE (no shooter) -> defend lanes sooner, urgency " + f.Urgency.ToString("0.00")); }
    if (bombReady) { f.BombEager = Clamp(f.BombEager * 1.2, 0.7, 2.5); causes.Add("UNUSED BOMB (one was ready and affordable) -> bomb earlier, eagerness " + f.BombEager.ToString("0.00")); }
    if (sun >= 150 && !bombReady) { f.Spend = Clamp(f.Spend * 1.2, 0.7, 2.5); causes.Add("IDLE SUN (" + sun + " unspent) -> spend sooner, spend " + f.Spend.ToString("0.00")); }
    if (shooters >= 1 && sun < 100 && sunMakers < lanes) { f.Econ = Clamp(f.Econ * 1.1, 0.7, 2.0); causes.Add("BROKE (" + sunMakers + " sun makers for " + lanes + " lanes) -> more economy, econ " + f.Econ.ToString("0.00")); }
    if (causes.Count == 0) {
      f.BombEager = Clamp(f.BombEager * 1.1, 0.7, 2.5); f.Urgency = Clamp(f.Urgency * 1.05, 0.7, 2.5);
      causes.Add("OUTGUNNED (" + shooters + " shooters vs " + zombiesInLane + " zombies) -> more firepower and earlier bombs");
    }
    Learn.Lesson("POST-MORTEM lane " + row + " at wave " + wave + "/" + waves + ": " + string.Join("; ", causes));
    Save();
  }
  // A win means the current corrections are about right; drift back towards neutral only a little.
  public static void Won() {
    var f = Cur; f.Wins++;
    Func<double, double> settle = v => v + (1 - v) * 0.05;
    f.Urgency = settle(f.Urgency); f.BombEager = settle(f.BombEager); f.Spend = settle(f.Spend); f.Econ = settle(f.Econ);
    Save();
  }
  public static string Summary() { var f = Cur; return "urgency " + f.Urgency.ToString("0.00") + " bombs " + f.BombEager.ToString("0.00") + " spend " + f.Spend.ToString("0.00") + " econ " + f.Econ.ToString("0.00") + " (" + f.Breaches + " breaches studied)"; }

  public static void Save() {
    if (Bot.ProMode) return;   // pro mode never writes learned knowledge
    try {
      var ic = CultureInfo.InvariantCulture;
      File.WriteAllLines(FILE, fixes.Select(kv => string.Join("|", kv.Key, kv.Value.Urgency.ToString(ic), kv.Value.BombEager.ToString(ic),
        kv.Value.Spend.ToString(ic), kv.Value.Econ.ToString(ic), kv.Value.Breaches, kv.Value.Wins)));
    } catch { }
  }
  public static void Load() {
    if (!File.Exists(FILE)) return;
    var ic = CultureInfo.InvariantCulture;
    foreach (var l in File.ReadAllLines(FILE)) {
      var p = l.Split('|');
      try { fixes[p[0]] = new Fix { Urgency = double.Parse(p[1], ic), BombEager = double.Parse(p[2], ic), Spend = double.Parse(p[3], ic),
                                    Econ = double.Parse(p[4], ic), Breaches = int.Parse(p[5]), Wins = int.Parse(p[6]) }; } catch { }
    }
  }
}
