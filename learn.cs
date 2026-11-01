// Learns the modded game's rules by observation: zombie toughness/speed per kind, mine arm time,
// shooter DPS and a caution factor driven by failures. Persists to knowledge.txt between runs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

public class ZKnow { public string Key; public int Seen, Kills, ToughN, SpeedN; public double Tough, Speed; }

public static class Learn {
  const string FILE = @"C:\cbot\knowledge.txt";
  static readonly CultureInfo IC = CultureInfo.InvariantCulture;
  public static readonly Dictionary<string, ZKnow> Z = new Dictionary<string, ZKnow>();
  // Potato mine: starts with a naive belief, corrected by every success/failure.
  public static double MineArm = 6; public static int MineOk, MineFail; public static double MaxFailAge, MinOkAge = 9999;
  public static readonly Dictionary<int, double> DpsLearned = new Dictionary<int, double>();
  static readonly Dictionary<int, double> dmgAcc = new Dictionary<int, double>(), shooterSec = new Dictionary<int, double>();
  public static double Caution = 1.0;
  public static int Lessons;
  // Summoners: zombie kinds next to which new zombies keep popping up mid-lawn (e.g. a dancer calling backup).
  public static readonly Dictionary<string, int> SummonEvidence = new Dictionary<string, int>();
  public static bool IsSummoner(string key) { int n; return SummonEvidence.TryGetValue(key, out n) && n >= 3; }
  // Shooter range, learned: the farthest a zombie has been from the front-most shooter of a lane (with only that
  // kind of shooter) when it took damage. Unknown = assume it covers the whole lane.
  public static readonly Dictionary<int, double> RangeMax = new Dictionary<int, double>();
  static readonly Dictionary<int, int> rangeN = new Dictionary<int, int>();
  static readonly Dictionary<int, List<double>> rangeSamples = new Dictionary<int, List<double>>();
  static readonly HashSet<uint> damagedNow = new HashSet<uint>();   // zombies that lost health this frame
  public static double Range(int seed) {
    double r; int n;
    if (RangeMax.TryGetValue(seed, out r) && rangeN.TryGetValue(seed, out n) && n >= 40 && Sane("range:" + seed, r, 60, 900, Mem.SeedName(seed) + " range (px)")) {
      // Sanity vs description: only plants described as short-range should measure short.
      if (!Roles.RangeMentioned.Contains(seed) && !Flag("rangedesc:" + seed, r < 400, Mem.SeedName(seed) + " measured range " + r.ToString("0") + "px but its description does not say short range")) return 900;
      return r >= 550 ? 900 : r;   // ~7+ tiles = whole lane
    }
    return Roles.ShortRange.Contains(seed) ? 260 : 900;   // until measured, trust the description ("short range")
  }
  static void ObserveRange(State s, double[] laneDmg, HashSet<int> shooterTypes) {
    for (int row = 0; row < 6; row++) {
      if (laneDmg[row] <= 0 || laneDmg[row] > 150) continue;
      var sh = s.Plants.Where(p => p.Row == row && shooterTypes.Contains(p.Type)).ToList();
      if (sh.Count == 0 || sh.Select(p => p.Type).Distinct().Count() != 1) continue;
      int t = sh[0].Type; double front = sh.Max(p => 40 + p.Col * 80 + 40);
      // Reach = the FARTHEST zombie in this lane that actually lost health this frame (nearest-zombie distance underestimates long range).
      var hit = s.Zombies.Where(z => z.Row == row && z.X + 40 > front && damagedNow.Contains(z.Id)).Select(z => z.X + 40 - front).DefaultIfEmpty(-1).Max();
      if (hit < 0) continue;
      // Robust range: 85th percentile of recent hit distances (a stray pea from a dead shooter must not inflate it).
      List<double> hs; if (!rangeSamples.TryGetValue(t, out hs)) rangeSamples[t] = hs = new List<double>();
      hs.Add(hit); if (hs.Count > 300) hs.RemoveAt(0);
      int n; rangeN.TryGetValue(t, out n);
      if (hs.Count >= 40) { var sorted = hs.OrderBy(x => x).ToList(); RangeMax[t] = sorted[(int)(sorted.Count * 0.97)]; }
      rangeN[t] = n + 1;
      if (n + 1 == 40) Lesson(Mem.SeedName(t) + " hits zombies up to ~" + (RangeMax[t] / 80).ToString("0.0") + " tiles away" + (RangeMax[t] < 500 ? " (short range!)" : ""));
    }
  }
  // Dropped seed packets: how long they stay on the lawn before fading (learned from ones that expired untouched).
  public static double PacketLifetime = -1; static int lifetimeN;
  static readonly Dictionary<uint, double> packetSeen = new Dictionary<uint, double>();
  public static readonly HashSet<uint> PacketsTaken = new HashSet<uint>();   // ones we picked up ourselves
  public static double PacketTimeLeft(uint id) {
    double t0; if (PacketLifetime < 0 || !packetSeen.TryGetValue(id, out t0)) return -1;
    if (!Sane("packetlife", PacketLifetime, 3, 60, "dropped seed packet lifetime (s)")) return -1;
    return Math.Max(0, PacketLifetime - (lastFrame - t0));
  }
  static void TrackPackets(State s, double now) {
    var present = new HashSet<uint>();
    foreach (var c in s.Suns.Where(c => c.IsPacket)) { present.Add(c.Id); if (!packetSeen.ContainsKey(c.Id)) packetSeen[c.Id] = now; }
    foreach (var id in packetSeen.Keys.Where(k => !present.Contains(k)).ToList()) {
      double life = now - packetSeen[id]; packetSeen.Remove(id);
      if (PacketsTaken.Remove(id) || life < 2 || s.Plants.Count == 0) continue;   // we used it, or the level ended
      double old = PacketLifetime;
      PacketLifetime = lifetimeN == 0 ? life : PacketLifetime * 0.7 + life * 0.3; lifetimeN++;
      if (lifetimeN == 1 || Math.Abs(PacketLifetime - old) > 2) Lesson("dropped seed packets fade after ~" + PacketLifetime.ToString("0") + "s if nobody picks them up");
    }
  }
  // Sun income actually collected this level, per game-minute (-1 until a minute has passed).
  static double collected;
  public static void SunCollected(int amount) { collected += amount; }
  public static double IncomePerMin { get { double mins = (lastFrame - levelStartTime) / 60; return mins > 1 ? collected / mins : -1; } }
  // Sky sun: suns first seen high up (falling from the sky) per game-minute, this level vs the usual rate.
  public static double SkyBaseline = -1; static int skyThisLevel; static double levelStartTime = -1; static readonly HashSet<uint> skySeen = new HashSet<uint>();
  public static double SkyRateNow { get { double mins = (lastFrame - levelStartTime) / 60; return mins > 0.5 ? skyThisLevel / mins : -1; } }
  // 0 = normal sky sun, up to 1 = none at all compared with what we usually get.
  public static double SkyStarvation() {
    double now = SkyRateNow; if (now < 0 || SkyBaseline <= 0) return 0;
    return Math.Max(0, Math.Min(1, 1 - now / SkyBaseline));
  }
  // Shared with role discovery: this frame's lane damage, zombies that vanished, and game-time step.
  public static double[] LastLaneDmg = new double[6]; public static double LastDt;
  public static List<Tuple<int, float, int>> LastVanished = new List<Tuple<int, float, int>>();   // row, x, hp when last seen
  // Seed picks: how often each plant was brought and how often that level was then won.
  public static readonly Dictionary<int, int[]> Picks = new Dictionary<int, int[]>();   // seed -> {picked, won}
  public static List<int> LastLoadout = new List<int>();
  // Picks are keyed per environment (env * 100 + seed): a plant can be great at night and useless by day.
  public static int Env;   // 0 day, 1 night, ... (set from the visible background)
  public static bool Daytime { get { return Env == 0 || Env == 2 || Env == 4; } }   // day, pool, roof: the sky is lit
  public static double PickScore(int seed) {
    // Core picks by role (read from descriptions, confirmed in play): some sun income and some damage every level.
    double core = Roles.Is(seed, "SUN") ? 300 : Roles.Is(seed, "SHOOT") ? 200 : 0;
    double measured = ValueOf(seed);
    if (Roles.Is(seed, "SHOOT") && measured >= 0 && measured / Math.Max(25, Mem.Cost(seed)) < 0.5) core = 60;   // proven weak shooter: no core bonus
    int[] p; Picks.TryGetValue(Env * 100 + seed, out p); if (p == null) p = new int[2];
    double winRate = p[0] == 0 ? 0.5 : (double)p[1] / p[0];
    double value = ValueOf(seed);   // proven contribution outweighs noisy level outcomes
    double valueBonus = value < 0 ? 0 : 60 * Math.Tanh(value / Math.Max(25, Mem.Cost(seed)) / 2);
    return core + 50 + 25 * (winRate - 0.5) + 30.0 / (1 + p[0]) + valueBonus;
  }
  // Contribution: value each plant kind actually produced, in sun-equivalents per plant placed (per environment).
  // Shooters earn credit for damage dealt in their lane (shared by learned DPS), sun makers for sun that pops out next to them.
  public static readonly Dictionary<int, double> SeedValue = new Dictionary<int, double>();   // env*100+seed -> value per plant
  static readonly Dictionary<int, double> levelValue = new Dictionary<int, double>();
  static readonly Dictionary<int, HashSet<uint>> levelPlanted = new Dictionary<int, HashSet<uint>>();
  public static double ValueOf(int seed) { double v; return SeedValue.TryGetValue(Env * 100 + seed, out v) ? v : -1; }
  public static void CreditBomb(int seed, double v) { Credit(seed, v); }
  static void Credit(int seed, double v) { double c; levelValue.TryGetValue(seed, out c); levelValue[seed] = c + v; }
  public static void TrackContribution(State s, Func<int, bool> isShooter, Func<int, bool> isSun) {
    foreach (var p in s.Plants) { HashSet<uint> h; if (!levelPlanted.TryGetValue(p.Type, out h)) levelPlanted[p.Type] = h = new HashSet<uint>(); h.Add(p.Id); }
    for (int row = 0; row < 6; row++) {
      if (LastLaneDmg[row] <= 0 || LastLaneDmg[row] > 150) continue;   // skip explosions
      var sh = s.Plants.Where(p => p.Row == row && isShooter(p.Type)).ToList();
      double tot = sh.Sum(p => Dps(p.Type)); if (tot <= 0) continue;
      foreach (var p in sh) Credit(p.Type, LastLaneDmg[row] * Dps(p.Type) / tot / 10.0);   // 10 damage ~ 1 sun
    }
    foreach (var c in s.Suns) {
      if (!(c.Type == 11 || c.Type == 12 || c.Type == 13) || c.Motion != 2 || !creditedSun.Add(c.Id)) continue;   // only sun that came out of a plant
      var maker = s.Plants.Where(p => isSun(p.Type) && Math.Abs(40 + p.Col * 80 + 40 - c.X) < 70 && Math.Abs((Bot.CellY(p.Row) - 5) - c.Y) < 70).FirstOrDefault();
      if (maker != null) Credit(maker.Type, c.Type == 12 ? 15 : c.Type == 13 ? 50 : 25);
    }
  }
  static readonly HashSet<uint> creditedSun = new HashSet<uint>();
  static void CloseContribution() {
    foreach (var kv in levelValue) {
      HashSet<uint> h; int n = levelPlanted.TryGetValue(kv.Key, out h) ? Math.Max(1, h.Count) : 1;
      double per = kv.Value / n; int key = Env * 100 + kv.Key; double old;
      SeedValue[key] = SeedValue.TryGetValue(key, out old) ? old * 0.6 + per * 0.4 : per;
    }
    if (levelValue.Count > 0) Viz.AddEvent("LEARN plant value this level: " + string.Join("  ", levelValue.OrderByDescending(kv => kv.Value).Select(kv => Mem.SeedName(kv.Key) + " " + (kv.Value / Math.Max(1, levelPlanted.ContainsKey(kv.Key) ? levelPlanted[kv.Key].Count : 1)).ToString("0") + "/plant")));
    levelValue.Clear(); levelPlanted.Clear(); creditedSun.Clear();
  }

  // Which plants were brought on each attempt of each level (level -> seed -> times brought).
  public static readonly Dictionary<int, Dictionary<int, int>> LevelUse = new Dictionary<int, Dictionary<int, int>>();
  public static int TimesUsedHere(int seed) { Dictionary<int, int> d; int n; return LevelUse.TryGetValue(CurLevel, out d) && d.TryGetValue(seed, out n) ? n : 0; }

  public static void LoadoutResult(bool won) {
    Dictionary<int, int> lu; if (!LevelUse.TryGetValue(CurLevel, out lu)) LevelUse[CurLevel] = lu = new Dictionary<int, int>();
    foreach (var sd in LastLoadout) { int n; lu.TryGetValue(sd, out n); lu[sd] = n + 1; }
    CloseContribution();
    foreach (var sd in LastLoadout) { int key = Env * 100 + sd; int[] p; if (!Picks.TryGetValue(key, out p)) Picks[key] = p = new int[2]; p[0]++; if (won) p[1]++; }
    LastLoadout.Clear();
  }

  // Scoreboard (persists): deaths, levels won, current and best no-death streak.
  public static int Deaths, Wins, Streak, BestStreak;
  public static void RoundWon(bool clean) {
    Wins++; Streak++; BestStreak = Math.Max(BestStreak, Streak); LoadoutResult(true);
    Viz.AddEvent("LEVEL " + ((CurLevel - 1) / 10 + 1) + "-" + ((CurLevel - 1) % 10 + 1) + " BEATEN after " + DeathsHere + " death" + (DeathsHere == 1 ? "" : "s"));
    if (clean) CleanWin();
    Save();
  }
  public static void Died() { Deaths++; Streak = 0; LoadoutResult(false); int n; LevelDeaths.TryGetValue(CurLevel, out n); LevelDeaths[CurLevel] = n + 1; Save(); }
  // Deaths on each level until it was beaten (current level keeps counting until the win).
  public static readonly Dictionary<int, int> LevelDeaths = new Dictionary<int, int>();
  public static int CurLevel;
  public static int DeathsHere { get { int n; return LevelDeaths.TryGetValue(CurLevel, out n) ? n : 0; } }

  class ZRec { public string Key; public float SeenX, LastX, AnchorX; public double AnchorT; public int LastTotal; public double Absorbed; public int Row; }
  static readonly Dictionary<uint, ZRec> zr = new Dictionary<uint, ZRec>();
  class MRec { public double Planted; public int Row, Col; }
  static readonly Dictionary<uint, MRec> mines = new Dictionary<uint, MRec>();
  static Dictionary<uint, Plt> prevPlants = new Dictionary<uint, Plt>();
  // All learning runs on game time (seconds of Board::mMainCounter), so it holds at any speed multiplier.
  static double lastFrame = -1; static DateTime lastSave = DateTime.Now;

  public static string KeyOf(Zom z) {
    return Mem.ZomName(z.Type) + (z.Helm > 0 ? "+helm" : "") + (z.Shield > 0 ? "+shield" : "");
  }
  static ZKnow K(string key) { ZKnow k; if (!Z.TryGetValue(key, out k)) { k = new ZKnow { Key = key }; Z[key] = k; } return k; }

  // ----- sanity limits: a belief outside its plausible range is flagged and not used until it comes back -----
  public static readonly Dictionary<string, string> SanityWarnings = new Dictionary<string, string>();
  public static bool Flag(string key, bool bad, string msg) {
    if (bad) {
      if (!SanityWarnings.ContainsKey(key)) {
        SanityWarnings[key] = msg; Viz.AddEvent("SANITY " + msg + " -> not trusted until confirmed");
        try { File.AppendAllText(@"C:\cbot\sanity.txt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + Environment.NewLine); } catch { }
      }
      return false;
    }
    if (SanityWarnings.Remove(key)) {
      Viz.AddEvent("SANITY " + key + " is back in a plausible range -> trusted again");
      try { File.AppendAllText(@"C:\cbot\sanity.txt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + key + " back in range, trusted again" + Environment.NewLine); } catch { }
    }
    return true;
  }
  public static bool Sane(string key, double v, double lo, double hi, string what) {
    return Flag(key, v < lo || v > hi, what + " = " + v.ToString("0.##") + " is outside the plausible range " + lo + " to " + hi);
  }
  static bool SaneTough(ZKnow k) { return k.ToughN > 0 && Sane("tough:" + k.Key, k.Tough, 30, 12000, k.Key + " toughness"); }
  static bool SaneSpeed(ZKnow k) { return k.SpeedN >= 3 && Sane("speed:" + k.Key, k.Speed, 2, 80, k.Key + " walking speed (px/s)"); }
  // Mine arm time, if believable; otherwise a neutral fallback.
  public static double MineArmTrusted { get { return Sane("minearm", MineArm, 1, 60, "potato mine arm time (s)") ? MineArm : 18; } }   // too-short guesses lose lanes, so the fallback errs long

  // ----- what the bot believes -----
  public static bool KnowsTough(string key) { ZKnow k; return Z.TryGetValue(key, out k) && k.ToughN > 0; }
  public static double Tough(string key) {
    ZKnow k;
    if (Z.TryGetValue(key, out k) && SaneTough(k)) return k.Tough;
    var known = Z.Values.Where(v => v.ToughN > 0 && v.Tough >= 30 && v.Tough <= 12000).ToList();
    return known.Count > 0 ? known.Average(v => v.Tough) : 300;   // guess: average of what we've learned
  }
  public static double Speed(string key) {
    ZKnow k;
    if (Z.TryGetValue(key, out k) && SaneSpeed(k)) return k.Speed;
    var known = Z.Values.Where(v => v.SpeedN >= 3 && v.Speed >= 2 && v.Speed <= 80).ToList();
    return known.Count > 0 ? known.Max(v => v.Speed) : 20;         // unknown kinds: assume the fastest seen
  }
  public static double Dps(int seed) {
    double d;
    return DpsLearned.TryGetValue(seed, out d) && Sane("dps:" + seed, d, 0.5, 250, Mem.SeedName(seed) + " damage per second") ? d : 14;
  }

  public static void Lesson(string s) { Lessons++; Viz.AddEvent("LEARN " + s); }

  // ----- observation, once per frame -----
  public static void Observe(State s, HashSet<int> shooterTypes, float visibleX) {
    double now = s.GameTime;
    if (levelStartTime < 0 || now < levelStartTime) levelStartTime = now;
    TrackPackets(s, now);
    foreach (var c in s.Suns) if ((c.Type == 11 || c.Type == 12 || c.Type == 13) && c.Motion <= 1 && skySeen.Add(c.Id)) skyThisLevel++;
    double dt = lastFrame < 0 || now < lastFrame ? 0 : Math.Min(5, now - lastFrame);
    lastFrame = now;
    var present = new HashSet<uint>();
    var laneDmg = new double[6]; bool explosion = false; damagedNow.Clear();
    var vanished = new List<ZRec>();

    foreach (var z in s.Zombies) {
      if (z.X >= visibleX) continue;
      present.Add(z.Id);
      ZRec r;
      if (!zr.TryGetValue(z.Id, out r)) {
        // A zombie appearing mid-lawn (not walking in from the right, not out of a grave) next to another one: summoned.
        int zcol = Math.Max(0, Math.Min(8, (int)((z.X + 30 - 40) / 80)));
        if (lastFrame >= 0 && z.X < 735 && !s.Graves.Contains(z.Row * 10 + zcol)) {   // walkers first appear near x~755
          var caller = s.Zombies.Where(o => o.Id != z.Id && zr.ContainsKey(o.Id) && Math.Abs(o.Row - z.Row) <= 1 && Math.Abs(o.X - z.X) < 170).OrderBy(o => Math.Abs(o.X - z.X)).FirstOrDefault();
          if (caller != null) {
            string ck = KeyOf(caller); int n; SummonEvidence.TryGetValue(ck, out n); SummonEvidence[ck] = n + 1;
            if (n + 1 == 3) Lesson(ck + " summons other zombies around it - kill it first");
          }
        }
        r = new ZRec { Key = KeyOf(z), SeenX = z.X, LastX = z.X, AnchorX = z.X, AnchorT = now, LastTotal = z.Total, Row = z.Row };
        zr[z.Id] = r;
        var k = K(r.Key); k.Seen++;
        if (k.Seen == 1) { Lesson("new zombie kind spotted: " + r.Key); Quips.NewKind(r.Key); }
        continue;
      }
      int drop = r.LastTotal - z.Total;
      if (drop > 0) damagedNow.Add(z.Id);
      if (drop > 0) { r.Absorbed += drop; if (z.Row >= 0 && z.Row < 6) laneDmg[z.Row] += drop; if (drop > 150) explosion = true; }
      r.LastTotal = z.Total; r.LastX = z.X; r.Row = z.Row;
      double el = now - r.AnchorT;
      if (el >= 1.0) {
        double v = (r.AnchorX - z.X) / el;
        if (v > 1 && v < 150) { var k = K(r.Key); k.Speed = k.SpeedN == 0 ? v : k.Speed + (v - k.Speed) * 0.1; k.SpeedN++;
          if (k.SpeedN == 3) Lesson(r.Key + " walks ~" + k.Speed.ToString("0") + " px/s"); }
        r.AnchorX = z.X; r.AnchorT = now;
      }
    }
    foreach (var id in zr.Keys.Where(id => !present.Contains(id)).ToList()) { vanished.Add(zr[id]); zr.Remove(id); }
    ObserveRange(s, laneDmg, shooterTypes);
    LastLaneDmg = laneDmg; LastDt = dt; LastVanished = vanished.Select(v => Tuple.Create(v.Row, v.LastX, v.LastTotal)).ToList();

    // Toughness = damage it soaked before dying, only for zombies watched from the moment they walked in.
    foreach (var r in vanished) {
      if (r.LastTotal > 60 || r.SeenX < 600) continue;
      var k = K(r.Key); k.Kills++;
      double t = r.Absorbed + Math.Max(0, r.LastTotal);
      k.Tough = k.ToughN == 0 ? t : (k.Tough * k.ToughN + t) / (k.ToughN + 1); k.ToughN++;
      if (k.ToughN == 1 || k.ToughN == 3 || k.ToughN == 10)
        Lesson(r.Key + " takes ~" + k.Tough.ToString("0") + " damage to kill (" + k.ToughN + " kills)");
    }

    // Mines: success if a zombie in that lane near the mine died or was blasted the moment it went.
    var minePresent = new HashSet<uint>();
    // Plants that vanished this very frame (needed to rule out bombs going off next to a mine).
    var plantNow = s.Plants.ToDictionary(p => p.Id, p => p);
    var goneThisFrame = prevPlants.Where(kv => !plantNow.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
    prevPlants = plantNow;
    // Bombs: credit an instant plant with the toughness of zombies that vanished beside it in the same frame it went off.
    foreach (var g in goneThisFrame.Where(g => Roles.Is(g.Type, "INSTANT") || Roles.Is(g.Type, "MINE"))) {
      float gx = 40 + g.Col * 80 + 40;
      double blown = vanished.Where(v => Math.Abs(v.Row - g.Row) <= 1 && Math.Abs(v.LastX + 40 - gx) < 160).Sum(v => (double)Math.Max(0, v.LastTotal));
      if (blown > 0) CreditBomb(g.Type, blown / 10.0);   // 10 damage ~ 1 sun, same scale as shooters
    }
    foreach (var p in s.Plants.Where(p => Roles.Is(p.Type, "MINE"))) {
      minePresent.Add(p.Id);
      if (!mines.ContainsKey(p.Id)) mines[p.Id] = new MRec { Planted = now, Row = p.Row, Col = p.Col };
    }
    foreach (var id in mines.Keys.Where(id => !minePresent.Contains(id)).ToList()) {
      var m = mines[id]; mines.Remove(id);
      double age = now - m.Planted;
      float cx = 40 + m.Col * 80 + 40;
      // Rule out other causes of zombies vanishing: a running mower, a chomper nearby, or a bomb going off this frame.
      bool mowerRunning = s.Mowers[m.Row] == 2;
      bool meleeNear = s.Plants.Any(pl => pl.Row == m.Row && Roles.Is(pl.Type, "MELEE") && Math.Abs(40 + pl.Col * 80 + 40 - cx) < 170);
      bool bombNear = goneThisFrame.Any(g => Roles.Is(g.Type, "INSTANT") && Math.Abs(g.Row - m.Row) <= 1);
      if (mowerRunning || meleeNear || bombNear) {
        Viz.AddEvent("LEARN mine at r" + m.Row + "c" + m.Col + " vanished during a " + (mowerRunning ? "mower run" : meleeNear ? "chomper bite" : "bomb blast") + " - not counted");
        continue;
      }
      // Explosion = the mine and a zombie standing on it disappear in the same frame.
      bool boom = vanished.Any(r => r.Row == m.Row && Math.Abs(r.LastX + 40 - cx) < 90);
      // "Eaten" needs a zombie right beside it; otherwise it vanished for another reason (level ended, shovel...).
      bool zombieBeside = s.Zombies.Any(z => z.Row == m.Row && Math.Abs(z.X + 40 - cx) < 80) || vanished.Any(r => r.Row == m.Row && Math.Abs(r.LastX + 40 - cx) < 80);
      if (!boom && (!zombieBeside || s.Plants.Count == 0)) continue;
      if (boom) { MineOk++; MinOkAge = Math.Min(MinOkAge, age); }
      else { MineFail++; MaxFailAge = Math.Max(MaxFailAge, age); }
      Quips.Mine(boom, age);
      double old = MineArm;
      // A detonation proves the mine was armed by that age, so successes outrank contradicting "failures"
      // (e.g. a pole vaulter jumping it). Without any success, assume it needs a bit longer than the oldest failure.
      MineArm = MinOkAge < 9999 ? (MaxFailAge < MinOkAge ? (MaxFailAge + MinOkAge) / 2 : MinOkAge) : Math.Max(MineArm, MaxFailAge + 3);
      Lesson("potato mine " + (boom ? "EXPLODED" : "was eaten unarmed") + " at age " + age.ToString("0.0") + "s -> arm time belief " +
             old.ToString("0.0") + "s => " + MineArm.ToString("0.0") + "s");
    }

    // Shooter DPS: lanes with a single shooter kind and a zombie in front; explosions excluded.
    if (dt > 0 && !explosion) {
      for (int row = 0; row < 6; row++) {
        if (!s.Zombies.Any(z => z.Row == row && z.X < visibleX)) continue;
        var sh = s.Plants.Where(p => p.Row == row && shooterTypes.Contains(p.Type)).ToList();
        if (sh.Count == 0 || sh.Select(p => p.Type).Distinct().Count() != 1) continue;
        int t = sh[0].Type;
        // Only count time when a zombie is actually within this shooter's reach (else short-range plants look useless).
        double front = sh.Max(p => 40 + p.Col * 80 + 40);
        double gap = s.Zombies.Where(z => z.Row == row && z.X + 40 > front - 20).Select(z => z.X + 40 - front).DefaultIfEmpty(9999).Min();
        if (gap > Range(t)) continue;
        double a, b; dmgAcc.TryGetValue(t, out a); shooterSec.TryGetValue(t, out b);
        dmgAcc[t] = a + laneDmg[row]; shooterSec[t] = b + sh.Count * dt;
        if (shooterSec[t] >= 20) {
          double d = dmgAcc[t] / shooterSec[t];
          bool first = !DpsLearned.ContainsKey(t);
          DpsLearned[t] = first ? d : DpsLearned[t] + (d - DpsLearned[t]) * 0.3;
          if (first) Lesson(Mem.SeedName(t) + " deals ~" + d.ToString("0") + " dmg/s each");
          dmgAcc[t] = 0; shooterSec[t] = 0;
        }
      }
    }
    if ((DateTime.Now - lastSave).TotalSeconds > 30) { Save(); lastSave = DateTime.Now; }
  }

  public static void MowerLost(int row) {
    double old = Caution; Caution = Math.Min(1.8, Caution * 1.15);
    Lesson("lane " + row + " broke through -> caution " + old.ToString("0.00") + " => " + Caution.ToString("0.00"));
  }
  public static void CleanWin() {
    if (Caution <= 1.0) return;
    double old = Caution; Caution = Math.Max(1.0, Caution * 0.93);
    Lesson("clean win -> caution " + old.ToString("0.00") + " => " + Caution.ToString("0.00"));
  }
  public static void LevelReset() {
    double rate = SkyRateNow;
    if (rate >= 0) {
      double old = SkyBaseline;
      SkyBaseline = Math.Max(SkyBaseline, rate);   // "normal" = the best sky sun seen so far
      if (rate < SkyBaseline * 0.4) Lesson("barely any sun fell from the sky this level (" + rate.ToString("0.0") + "/min vs usual " + SkyBaseline.ToString("0.0") + ") - more sunflowers next time it is like this");
    }
    if (IncomePerMin >= 0) Viz.AddEvent("level economy: collected ~" + IncomePerMin.ToString("0") + " sun/min");
    zr.Clear(); mines.Clear(); lastFrame = -1; levelStartTime = -1; skyThisLevel = 0; skySeen.Clear(); collected = 0;
  }

  // ----- persistence -----
  public static void Save() {
    if (Bot.ProMode) return;   // pro mode never writes learned knowledge
    try {
      var lines = new List<string>();
      foreach (var k in Z.Values) lines.Add(string.Join("|", "Z", k.Key, k.Seen, k.Kills, k.Tough.ToString(IC), k.ToughN, k.Speed.ToString(IC), k.SpeedN));
      lines.Add(string.Join("|", "M", MineArm.ToString(IC), MineOk, MineFail, MaxFailAge.ToString(IC), MinOkAge.ToString(IC)));
      foreach (var d in DpsLearned) lines.Add(string.Join("|", "D", d.Key, d.Value.ToString(IC)));
      lines.Add("C|" + Caution.ToString(IC));
      foreach (var se in SummonEvidence) lines.Add("SU|" + se.Key + "|" + se.Value);
      foreach (var d in LevelDeaths) lines.Add("D2|" + d.Key + "|" + d.Value);
      foreach (var lv in LevelUse) foreach (var u in lv.Value) lines.Add("LU|" + lv.Key + "|" + u.Key + "|" + u.Value);
      foreach (var rg in RangeMax) lines.Add("R|" + rg.Key + "|" + rg.Value.ToString(IC) + "|" + (rangeN.ContainsKey(rg.Key) ? rangeN[rg.Key] : 0));
      lines.Add("T|" + PacketLifetime.ToString(IC) + "|" + lifetimeN);
      foreach (var v in SeedValue) lines.Add("V|" + v.Key + "|" + v.Value.ToString(IC));
      lines.Add("K|" + SkyBaseline.ToString(IC));
      foreach (var p in Picks) lines.Add(string.Join("|", "U", p.Key, p.Value[0], p.Value[1]));
      lines.Add(string.Join("|", "S", Deaths, Wins, Streak, BestStreak));
      File.WriteAllLines(FILE, lines);
    } catch { }
  }
  public static void Load() {
    if (!File.Exists(FILE)) return;
    foreach (var l in File.ReadAllLines(FILE)) {
      var p = l.Split('|');
      try {
        if (p[0] == "Z") Z[p[1]] = new ZKnow { Key = p[1], Seen = int.Parse(p[2]), Kills = int.Parse(p[3]), Tough = double.Parse(p[4], IC),
          ToughN = int.Parse(p[5]), Speed = double.Parse(p[6], IC), SpeedN = int.Parse(p[7]) };
        else if (p[0] == "M") { MineArm = double.Parse(p[1], IC); MineOk = int.Parse(p[2]); MineFail = int.Parse(p[3]); MaxFailAge = double.Parse(p[4], IC); MinOkAge = double.Parse(p[5], IC); }
        else if (p[0] == "D") DpsLearned[int.Parse(p[1])] = double.Parse(p[2], IC);
        else if (p[0] == "C") Caution = double.Parse(p[1], IC);
        else if (p[0] == "SU") SummonEvidence[p[1]] = int.Parse(p[2]);
        else if (p[0] == "D2") LevelDeaths[int.Parse(p[1])] = int.Parse(p[2]);
        else if (p[0] == "LU") { int lv = int.Parse(p[1]); if (!LevelUse.ContainsKey(lv)) LevelUse[lv] = new Dictionary<int, int>(); LevelUse[lv][int.Parse(p[2])] = int.Parse(p[3]); }
        else if (p[0] == "R") { RangeMax[int.Parse(p[1])] = double.Parse(p[2], IC); rangeN[int.Parse(p[1])] = int.Parse(p[3]); }
        else if (p[0] == "T") { PacketLifetime = double.Parse(p[1], IC); lifetimeN = int.Parse(p[2]); }
        else if (p[0] == "V") SeedValue[int.Parse(p[1])] = double.Parse(p[2], IC);
        else if (p[0] == "K") SkyBaseline = double.Parse(p[1], IC);
        else if (p[0] == "U") Picks[int.Parse(p[1])] = new[] { int.Parse(p[2]), int.Parse(p[3]) };
        else if (p[0] == "S") { Deaths = int.Parse(p[1]); Wins = int.Parse(p[2]); Streak = int.Parse(p[3]); BestStreak = int.Parse(p[4]); }
      } catch { }
    }
  }
}
