// Plant role discovery: unknown plants are tried, watched, and classified from what visibly happens.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

public static class Roles {
  const string FILE = @"C:\cbot\roles.txt";
  // seed -> evidence counts per role
  public static readonly Dictionary<int, Dictionary<string, int>> Evidence = new Dictionary<int, Dictionary<string, int>>();
  public static readonly Dictionary<int, string> Learned = new Dictionary<int, string>();
  // Starting beliefs from the vanilla game; evidence can overturn them.
  // No built-in beliefs: roles come from the in-game descriptions (hypotheses) and are then confirmed or corrected in play.
  static readonly Dictionary<int, string> Prior = new Dictionary<int, string>();

  class Watch { public uint Id; public int Seed, Row, Col; public double Planted; public bool Done, HpTested; }
  static readonly Dictionary<int, int> maxHpOf = new Dictionary<int, int>();   // seed -> real max health seen (Plant::mPlantMaxHealth)
  // Median max health of the known shooters and sun makers (the "ordinary" plants a wall is compared with); -1 if none seen.
  static double MedianHp(int exclude) {
    var hs = maxHpOf.Where(kv => kv.Key != exclude && kv.Value > 0 && (Is(kv.Key, "SHOOT") || Is(kv.Key, "SUN"))).Select(kv => kv.Value).OrderBy(v => v).ToList();
    return hs.Count == 0 ? -1 : hs.Count % 2 == 1 ? hs[hs.Count / 2] : (hs[hs.Count / 2 - 1] + hs[hs.Count / 2]) / 2.0;
  }
  static readonly Dictionary<uint, Watch> watching = new Dictionary<uint, Watch>();
  public static int Trials;

  // Hypotheses from the plant's in-game description (the same text the Almanac / unlock screen shows).
  public static readonly Dictionary<int, string> DescRole = new Dictionary<int, string>();
  public static readonly Dictionary<int, string> Tooltip = new Dictionary<int, string>();
  public static readonly HashSet<int> Nocturnal = new HashSet<int>();
  public static readonly HashSet<int> ShortRange = new HashSet<int>();
  public static readonly HashSet<int> RangeMentioned = new HashSet<int>();   // the description states a range of some kind
  public static readonly HashSet<int> Penetrates = new HashSet<int>();   // description: goes through screen doors / shields
  public static readonly HashSet<int> WaterPad = new HashSet<int>();     // lets non-aquatic plants stand on water (lily pad)
  public static readonly HashSet<int> RoofPad = new HashSet<int>();      // lets plants stand on the roof (flower pot)
  public static readonly HashSet<int> Aquatic = new HashSet<int>();      // must be planted in water
  public static readonly HashSet<int> NeedsPad = new HashSet<int>();     // must be planted on a lily pad
  public static readonly HashSet<int> MultiLane = new HashSet<int>();    // hits other lanes too (three lanes, 5 directions, splash)
  public static bool IsPad(int seed) { LoadDescriptions(); return WaterPad.Contains(seed) || RoofPad.Contains(seed); }
  // Latched only once descriptions really loaded (file read AND plant names readable from the game); otherwise retried every 30s.
  static bool descLoaded, descTried; static int descRetryAt;
  public static bool DescriptionsLoaded { get { return descLoaded; } }
  static readonly System.Text.RegularExpressions.Regex ArmWord = new System.Text.RegularExpressions.Regex(@"\barm(s|ed|ing)?\b");
  public static void LoadDescriptions() {
    if (descLoaded) return;
    int tick = Environment.TickCount;
    if (descTried && unchecked(tick - descRetryAt) < 0) return;
    descTried = true; descRetryAt = unchecked(tick + 30000);
    string f = @"C:\Users\User\Downloads\Modded Plants VS Zombies\Release\properties\LawnStrings.txt";
    string[] t; try { t = System.IO.File.ReadAllLines(f); } catch { return; }
    Func<string, string> section = key => {
      int i = Array.FindIndex(t, l => l.Trim() == "[" + key + "]"); if (i < 0) return "";
      var sb = new System.Text.StringBuilder();
      for (int j = i + 1; j < t.Length && !t[j].StartsWith("["); j++) sb.Append(t[j]).Append(" ");
      return sb.ToString();
    };
    var names = new string[59];
    try { for (int seed = 0; seed < 59; seed++) names[seed] = Mem.PlantName(seed) ?? ""; } catch { return; }   // game not attached yet
    if (names.All(n => n.Length == 0)) return;
    for (int seed = 0; seed < 59; seed++) {
      string name = names[seed]; if (name.Length == 0) continue;
      string tip = section(name + "_TOOLTIP").Trim(), head = section(name + "_DESCRIPTION_HEADER"), body = section(name + "_DESCRIPTION");
      string all = (tip + " " + head + " " + body).ToLower();
      if (tip.Length > 0) Tooltip[seed] = tip;
      if (all.Contains("sleeps during the day")) Nocturnal.Add(seed);
      if (all.Contains("range:")) RangeMentioned.Add(seed);
      if (all.Contains("penetrat") || all.Contains("pass through")) Penetrates.Add(seed);
      if (all.Contains("non-aquatic plants can be planted on top") || all.Contains("plant non-aquatic plants on top")) WaterPad.Add(seed);
      if (all.Contains("plant on the roof")) RoofPad.Add(seed);
      if (all.Contains("aquatic plant")) Aquatic.Add(seed);
      if (all.Contains("must be planted on lily pads")) NeedsPad.Add(seed);
      if (all.Contains("range:{stat} short") || all.Contains("short distance") || all.Contains("short-ranged")) ShortRange.Add(seed);
      if (all.Contains("three lanes") || all.Contains("directions") || all.Contains("any lane") || all.Contains("nearby enemies on impact") || all.Contains("nearby zombies on impact")) MultiLane.Add(seed);
      // Ground contact (spikes): hurts what walks over it and is never eaten. Not a shooter, whatever its "damage:" line says.
      bool ground = all.Contains("walk over") || all.Contains("walks over") || all.Contains("step on") || all.Contains("can't be eaten") || all.Contains("caltrop") || all.Contains("spikeweed");
      string role = null;
      if (WaterPad.Contains(seed) || RoofPad.Contains(seed)) role = "PAD";
      else if (all.Contains("wake") && all.Contains("mushroom")) role = "WAKE";
      else if (all.Contains("blows away")) role = "BLOW";
      else if (all.Contains("see through fog")) role = "LIGHT";
      else if (all.Contains("grave")) role = "GRAVE";
      else if (all.Contains("fight for you")) role = "HYPNO";
      else if (all.Contains("two of the same")) role = "IMITATE";
      else if (all.Contains("sun production") || all.Contains("gives") && all.Contains("sun")) role = "SUN";
      else if (all.Contains("devour") || all.Contains("chomp")) role = "MELEE";
      else if (ground) role = "GROUND";
      else if (all.Contains("delayed activation") || ArmWord.IsMatch(all)) role = "MINE";   // the word "arm", not "armadillo"
      else if (all.Contains("single use") || all.Contains("explode") || all.Contains("blow up")) role = "INSTANT";
      else if (all.Contains("damage:")) role = "SHOOT";
      else if (all.Contains("toughness") || all.Contains("block") || all.Contains("protect")) role = "WALL";
      if (role != null) DescRole[seed] = role;
    }
    if (DescRole.Count > 0) descLoaded = true;
  }

  public static string RoleOf(int seed) {
    string r;
    LoadDescriptions();
    if (Learned.TryGetValue(seed, out r)) {
      // Sanity: a learned role that contradicts the game's own description needs lots of evidence before it is used.
      string d;
      if (DescRole.TryGetValue(seed, out d) && d != r) {
        int n = Evidence.ContainsKey(seed) && Evidence[seed].ContainsKey(r) ? Evidence[seed][r] : 0;
        if (!Learn.Flag("role:" + seed, n < 8, Mem.SeedName(seed) + " was learned as " + r + " but its description says " + d + " (only " + n + " observations)")) return d;
      }
      return r;
    }
    if (Prior.TryGetValue(seed, out r)) return r;
    return DescRole.TryGetValue(seed, out r) ? r : null;
  }
  // A role read from the description but not yet seen in play.
  public static bool Tentative(int seed) { return !Learned.ContainsKey(seed) && !Prior.ContainsKey(seed) && DescRole.ContainsKey(seed); }
  public static bool Is(int seed, string role) { return RoleOf(seed) == role; }
  public static bool Unknown(int seed) { return RoleOf(seed) == null; }
  public static bool Unconfirmed(int seed) { return RoleOf(seed) == null || Tentative(seed); }
  // A trial of this seed is already on the lawn and still being watched.
  public static bool TrialRunning(int seed) { return watching.Values.Any(w => !w.Done && w.Seed == seed); }

  static void Add(int seed, string role, string why) {
    Dictionary<string, int> ev;
    if (!Evidence.TryGetValue(seed, out ev)) Evidence[seed] = ev = new Dictionary<string, int>();
    int n; ev.TryGetValue(role, out n); ev[role] = ++n;
    var best = ev.OrderByDescending(kv => kv.Value).First();
    string old = RoleOf(seed);
    // The in-game description is strong evidence: overturning it needs clearly more contradicting observations.
    string desc; int support = DescRole.TryGetValue(seed, out desc) && ev.ContainsKey(desc) ? ev[desc] : 0;
    bool contradictsDesc = desc != null && best.Key != desc;
    int need = contradictsDesc ? Math.Max(4, 2 * support + 1) : 2;
    if (best.Value >= need && best.Key != old) {
      Learned[seed] = best.Key;
      Viz.AddEvent("LEARN " + Mem.SeedName(seed) + " is a " + best.Key + " plant (" + why + ")" + (old != null ? " - was believed " + old : ""));
    } else if (n == 1 && Unknown(seed)) Viz.AddEvent("LEARN clue: " + Mem.SeedName(seed) + " looks like " + role + " (" + why + ")");
    Save();   // evidence is persisted too (rare: at most once per watched plant)
  }

  // laneDmg: damage zombies took this frame per row; vanished: zombies (row, x) that disappeared this frame.
  // Old signature: the plant's OWN kind is taken out of the known lane DPS here (same filter as bot.cs: known shooters).
  public static void Observe(State s, double[] laneDmg, double dt, List<Tuple<int, float, int>> vanished, Func<int, double> knownLaneDps) {
    Observe(s, laneDmg, dt, vanished, (row, ex) => knownLaneDps(row) - (Is(ex, "SHOOT") && !Unknown(ex) ? s.Plants.Count(p => p.Row == row && p.Type == ex) * Learn.Dps(ex) : 0));
  }
  // knownLaneDps(row, excludeSeed): DPS of the known shooters in that row, leaving out every plant of excludeSeed's kind.
  public static void Observe(State s, double[] laneDmg, double dt, List<Tuple<int, float, int>> vanished, Func<int, int, double> knownLaneDps) {
    double now = s.GameTime;
    var present = new HashSet<uint>();
    foreach (var p in s.Plants) {
      present.Add(p.Id);
      Watch w;
      if (!watching.TryGetValue(p.Id, out w)) {
        int hp; if (p.MaxHp > 0 && p.MaxHp < 100000 && (!maxHpOf.TryGetValue(p.Type, out hp) || p.MaxHp > hp)) maxHpOf[p.Type] = p.MaxHp;
        watching[p.Id] = w = new Watch { Id = p.Id, Seed = p.Type, Row = p.Row, Col = p.Col, Planted = now, Done = !Unconfirmed(p.Type) }; continue;
      }
      if (w.Done) continue;
      float cx = 40 + w.Col * 80 + 40, cy = (Bot.CellY(w.Row) - 5);
      // sun appearing right next to it
      // Sun appearing right next to it, where it is the closest plant and no known sun maker is nearby.
      var wcx = cx; var wcy = cy; var wrow = w.Row; var wcol = w.Col;
      if (now - w.Planted > 3 && s.Suns.Any(c => (c.Type >= 11 && c.Type <= 13) && c.Motion == 2 && Math.Abs(c.X - wcx) < 60 && Math.Abs(c.Y - wcy) < 60
            && !s.Plants.Any(o => (o.Row != wrow || o.Col != wcol) && Math.Abs(40 + o.Col * 80 + 40 - c.X) < 100 && Math.Abs((Bot.CellY(o.Row) - 5) - c.Y) < 100 && (Roles.Is(o.Type, "SUN") || Math.Abs(40 + o.Col * 80 + 40 - c.X) < Math.Abs(wcx - c.X)))))
        { Add(w.Seed, "SUN", "sun popped out next to it"); w.Done = true; continue; }
      // a zombie right beside it vanished while it lives -> it ate it
      // (only a zombie that was still healthy: one worn down by peas dying nearby proves nothing)
      bool mowerHere = s.Mowers[w.Row] == 2;   // a running lawnmower deletes healthy zombies too
      // Only test for "bites" on plants not already described as something else (shooters finishing a zombie look like bites).
      string wdesc; bool descSaysOther = DescRole.TryGetValue(w.Seed, out wdesc) && wdesc != "MELEE";
      if (descSaysOther) mowerHere = true;   // reuse the skip below
      if (!mowerHere && vanished.Any(v => v.Item1 == w.Row && v.Item2 + 40 > cx - 20 && v.Item2 + 40 < cx + 130 && v.Item3 > 120))
        { Add(w.Seed, "MELEE", "a zombie next to it disappeared"); w.Done = true; continue; }
      // Wall = real max health far above the ordinary plants (3x the median of shooters / sun makers, or 1000+), checked once per plant.
      // Not tested when the description says it is something else. The watch stays open: a tough plant can still prove it shoots.
      bool beside = s.Zombies.Any(z => z.Row == wrow && Math.Abs(z.X + 40 - wcx) < 60);
      if (!w.HpTested && p.MaxHp > 0) {
        w.HpTested = true;
        string hd; bool descOther = DescRole.TryGetValue(w.Seed, out hd) && hd != "WALL";
        double med = MedianHp(w.Seed);
        if (!descOther && (p.MaxHp >= 1000 || (med > 0 && p.MaxHp >= 3 * med)))
          Add(w.Seed, "WALL", "max health " + p.MaxHp + (med > 0 ? " vs ~" + med.ToString("0") + " for shooters/sun makers" : ""));
      }
      // lane damage beyond what known shooters explain -> it shoots
      // A big single chunk taken by the zombie right beside it is a bite, not shooting.
      if (laneDmg[w.Row] > 150 && beside && Unknown(w.Seed) && !descSaysOther) { Add(w.Seed, "MELEE", "the zombie beside it lost " + laneDmg[w.Row].ToString("0") + " hp in one go"); w.Done = true; continue; }
      // Sustained lane damage beyond what known shooters explain, averaged over 6 game-seconds -> it shoots.
      // Runs for described plants too (Unconfirmed, not just Unknown), with the plant's own kind left out of the "known" damage.
      if (dt > 0 && laneDmg[w.Row] < 150 && s.Zombies.Any(z => z.Row == wrow && z.X > wcx && z.X < 760) && now - w.Planted > 2 && Unconfirmed(w.Seed)
          && !(Learn.Daytime && Nocturnal.Contains(w.Seed)) && !Learn.OtherDamage(s, w.Row, sd => Is(sd, "SHOOT"), w.Seed)) {
        double[] acc; if (!extraAcc.TryGetValue(w.Id, out acc)) extraAcc[w.Id] = acc = new double[2];
        acc[0] += laneDmg[w.Row] - knownLaneDps(w.Row, w.Seed) * dt; acc[1] += dt;
        if (acc[1] >= 6) {
          double extra = acc[0] / acc[1];
          if (extra > 8) { Add(w.Seed, "SHOOT", "lane took ~" + extra.ToString("0") + " extra dmg/s for 6s"); w.Done = true; }
          acc[0] = 0; acc[1] = 0;
        }
      }
    }
    foreach (var id in watching.Keys.Where(id => !present.Contains(id)).ToList()) {
      var w = watching[id]; watching.Remove(id); extraAcc.Remove(id);
      if (w.Done) continue;
      double age = now - w.Planted;
      float cx = 40 + w.Col * 80 + 40;
      bool blast = vanished.Count(v => Math.Abs(v.Item1 - w.Row) <= 1 && Math.Abs(v.Item2 + 40 - cx) < 160) >= 1;
      if (age < 4 && (blast || laneDmg[Math.Max(0, Math.Min(5, w.Row))] > 150)) Add(w.Seed, "INSTANT", "vanished after " + age.ToString("0.0") + "s and took zombies with it");
    }
  }
  static readonly Dictionary<uint, double[]> extraAcc = new Dictionary<uint, double[]>();   // {extra damage, seconds}

  // roles.txt: "seed|ROLE" (learned role, old format) plus "E|seed|ROLE|n" (the evidence behind it, survives restarts).
  public static void Save() {
    if (Bot.ProMode) return;   // pro mode never writes learned knowledge
    try {
      var lines = Learned.Select(kv => kv.Key + "|" + kv.Value).ToList();
      foreach (var e in Evidence) foreach (var r in e.Value) lines.Add("E|" + e.Key + "|" + r.Key + "|" + r.Value);
      File.WriteAllLines(FILE, lines);
    } catch { }
  }
  public static void Load() {
    if (!File.Exists(FILE)) return;
    foreach (var l in File.ReadAllLines(FILE)) {
      var p = l.Split('|');
      try {
        if (p[0] == "E") { int sd = int.Parse(p[1]); if (!Evidence.ContainsKey(sd)) Evidence[sd] = new Dictionary<string, int>(); Evidence[sd][p[2]] = int.Parse(p[3]); }
        else Learned[int.Parse(p[0])] = p[1];
      } catch { }
    }
  }
}
