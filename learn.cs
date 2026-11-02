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
  // Summoners: zombie kinds next to which BURSTS of new zombies pop up mid-lawn (a dancer calling backup).
  // One event = 2+ new zombies within 0.6 game-s around the same caller. A kind is a summoner only if its events
  // came on 2+ levels and its event rate per zombie-second (Wilson lower bound) is well above every other kind's.
  public class SumKnow { public int Events; public double Exposure; public readonly HashSet<int> Levels = new HashSet<int>(); }
  public static readonly Dictionary<string, SumKnow> Summon = new Dictionary<string, SumKnow>();
  static double sumEvents, sumExposure;   // totals over all kinds (base rate)
  const double SUM_TRIAL = 10;            // zombie-seconds per "trial" for the proportion test
  static readonly Dictionary<uint, List<double>> pendingSummon = new Dictionary<uint, List<double>>();   // caller id -> recent arrivals
  static readonly Dictionary<uint, double> lastSummonEvent = new Dictionary<uint, double>();
  static readonly HashSet<uint> fogSeen = new HashSet<uint>();   // zombies first seen hidden in fog: walking out of it is not a summon
  static SumKnow SK(string key) { SumKnow k; if (!Summon.TryGetValue(key, out k)) Summon[key] = k = new SumKnow(); return k; }
  static double WilsonLow(double k, double n) {
    if (n <= 0) return 0; double z2 = 1.96 * 1.96, p = Math.Min(1, k / n);
    return Math.Max(0, (p + z2 / (2 * n) - 1.96 * Math.Sqrt(p * (1 - p) / n + z2 / (4 * n * n))) / (1 + z2 / n));
  }
  // 0 = not a summoner; 0.3..1 = how far its burst rate stands above the base rate of all other kinds.
  public static double SummonConfidence(string key) {
    SumKnow k; if (key == null || !Summon.TryGetValue(key, out k) || k.Events < 3 || k.Levels.Count < 2) return 0;
    double lb = WilsonLow(k.Events, Math.Max(k.Events, k.Exposure / SUM_TRIAL));
    double oe = Math.Max(0, sumEvents - k.Events), on = Math.Max(oe, Math.Max(0, sumExposure - k.Exposure) / SUM_TRIAL);
    double ratio = lb / ((oe + 0.5) / (on + 50));   // smoothed base rate of the other kinds
    return ratio < 3 ? 0 : Math.Min(1, 0.3 + 0.7 * (ratio - 3) / 7);
  }
  public static bool IsSummoner(string key) { return SummonConfidence(key) > 0; }
  static bool NoSummon(int type) { string n = Mem.ZomName(type); return n == "BUNGEE" || n == "IMP"; }   // dropped / thrown in, never called
  static bool NearGrave(State s, int row, int col) { for (int c = col - 1; c <= col + 1; c++) if (s.Graves.Contains(row * 10 + c)) return true; return false; }
  static bool Tracked(uint id, double now) { ZRec r; return zr.TryGetValue(id, out r) && r.SeenT < now; }   // seen before this frame
  static void NoteSummon(Zom caller, double now) {
    double last; if (lastSummonEvent.TryGetValue(caller.Id, out last) && now - last < 2) return;   // rest of the same burst
    List<double> ts; if (!pendingSummon.TryGetValue(caller.Id, out ts)) pendingSummon[caller.Id] = ts = new List<double>();
    ts.RemoveAll(t => now - t > 0.6); ts.Add(now);
    if (ts.Count < 2) return;
    ts.Clear(); lastSummonEvent[caller.Id] = now;
    string ck = KeyOf(caller); bool was = IsSummoner(ck); var k = SK(ck); k.Events++; k.Levels.Add(CurLevel); sumEvents++;
    Viz.AddEvent("LEARN summon burst next to " + ck + " (" + k.Events + " bursts on " + k.Levels.Count + " level" + (k.Levels.Count == 1 ? "" : "s") + ")");
    if (!was && IsSummoner(ck)) Lesson(ck + " summons other zombies around it - kill it first (confidence " + SummonConfidence(ck).ToString("0.00") + ")");
  }
  // Shooter range, learned as CENSORED data from lanes holding a single (awake) shooter kind:
  //  - a hit at distance d from the front-most shooter proves range >= d  (RangeMax = robust proven reach, never lowered);
  //  - the nearest zombie sitting unhurt >= 2.5s at distance d, with that shooter alive and awake all along, proves range < d.
  // The range is only treated as short when the description says so or the "unhurt" evidence is strong; otherwise 900 (whole lane).
  public static readonly Dictionary<int, double> RangeMax = new Dictionary<int, double>();
  static readonly Dictionary<int, int> rangeN = new Dictionary<int, int>();
  static readonly Dictionary<int, List<double>> rangeSamples = new Dictionary<int, List<double>>();   // fresh hits since start (not persisted)
  static readonly Dictionary<int, List<double>> unhurtD = new Dictionary<int, List<double>>();        // persisted (RU|)
  static readonly Dictionary<int, double> rangeCap = new Dictionary<int, double>();                   // upper bound, set once evidence is strong
  static readonly Dictionary<int, int> unhurtNew = new Dictionary<int, int>();
  static readonly HashSet<uint> damagedNow = new HashSet<uint>();   // zombies that lost health this frame
  const int RANGE_EVERY = 20, UNHURT_EVERY = 5;
  static readonly long[] laneSig = new long[6]; static readonly double[] laneSince = new double[6];
  static readonly uint[] laneNear = new uint[6]; static readonly double[] laneNearSince = new double[6];
  public static double Range(int seed) {
    double lo, cap; int n;
    bool haveLo = RangeMax.TryGetValue(seed, out lo) && rangeN.TryGetValue(seed, out n) && n >= 40 && Sane("range:" + seed, lo, 60, 900, Mem.SeedName(seed) + " range (px)");
    bool capped = rangeCap.TryGetValue(seed, out cap);
    if (!capped && !Roles.ShortRange.Contains(seed)) return 900;   // no evidence of a short range: whole lane
    double r = haveLo ? lo : capped ? Math.Max(60, cap - 40) : 260;
    return r >= 550 ? 900 : r;   // ~7+ tiles = whole lane
  }
  static void RecalcCap(int t) {
    List<double> u; double lo; if (!unhurtD.TryGetValue(t, out u)) return;
    RangeMax.TryGetValue(t, out lo); u.RemoveAll(d => d <= lo);   // contradicted by a proven hit: that zombie was out of reach for another reason
    int need = Roles.RangeMentioned.Contains(t) ? 20 : 40;        // a description silent on range needs twice the evidence
    if (u.Count < need) { rangeCap.Remove(t); return; }
    var sorted = u.OrderBy(x => x).ToList(); double cap = sorted[(int)(sorted.Count * 0.2)];
    bool first = !rangeCap.ContainsKey(t); rangeCap[t] = cap;
    if (first) Lesson(Mem.SeedName(t) + " leaves zombies ~" + (cap / 80).ToString("0.0") + " tiles away untouched (" + u.Count + " times) -> short range");
  }
  static bool Awake(int t) { return !(Daytime && Roles.Nocturnal.Contains(t)); }
  // Something other than this lane's straight shooters can hurt its zombies: ground spikes, biters, unknown plants,
  // charmed zombies, or a multi-lane / splash shooter next door.
  static bool OtherDamage(State s, int row, Func<int, bool> isShooter) { return OtherDamage(s, row, isShooter, -1); }
  public static bool OtherDamage(State s, int row, Func<int, bool> isShooter, int excludeSeed) {
    foreach (var p in s.Plants) {
      if (p.Type == excludeSeed) continue;
      if (p.Row == row && !isShooter(p.Type)) { string r = Roles.RoleOf(p.Type); if (r == null || r == "GROUND" || r == "MELEE" || r == "HYPNO") return true; }
      if (Math.Abs(p.Row - row) == 1 && isShooter(p.Type) && Roles.MultiLane.Contains(p.Type)) return true;
    }
    foreach (var z in s.Zombies) if (z.Row == row && z.Charmed) return true;
    return false;
  }
  static void ObserveRange(State s, double[] laneDmg, HashSet<int> shooterTypes, float visibleX, double now) {
    CapsOnce();
    for (int row = 0; row < 6; row++) {
      int rw = row;
      var sh = s.Plants.Where(p => p.Row == rw && shooterTypes.Contains(p.Type) && Awake(p.Type)).ToList();
      if (sh.Count == 0 || sh.Select(p => p.Type).Distinct().Count() != 1 || OtherDamage(s, row, shooterTypes.Contains)) { laneSig[row] = -1; continue; }
      int t = sh[0].Type; double front = sh.Max(p => 40 + p.Col * 80 + 40);
      // The shooter line-up must be unchanged (alive, same front) for the "unhurt" test.
      long sig = ((long)t << 32) | ((long)sh.Count << 8) | (long)(front / 80);
      if (laneSig[row] != sig) { laneSig[row] = sig; laneSince[row] = now; }
      Zom near = null; foreach (var z in s.Zombies) if (z.Row == row && !z.Charmed && z.X < visibleX && z.X + 40 > front && (near == null || z.X < near.X)) near = z;
      if (near == null) laneNear[row] = 0; else if (laneNear[row] != near.Id) { laneNear[row] = near.Id; laneNearSince[row] = now; }
      // Proven reach: the FARTHEST zombie in this lane that lost health this frame.
      if (laneDmg[row] > 0 && laneDmg[row] <= 150) {
        var hit = s.Zombies.Where(z => z.Row == rw && z.X + 40 > front && damagedNow.Contains(z.Id)).Select(z => z.X + 40 - front).DefaultIfEmpty(-1).Max();
        if (hit >= 0) {
          List<double> hs; if (!rangeSamples.TryGetValue(t, out hs)) rangeSamples[t] = hs = new List<double>();
          hs.Add(hit); if (hs.Count > 300) hs.RemoveAt(0);
          int n; rangeN.TryGetValue(t, out n); rangeN[t] = n + 1;
          // Recompute only every RANGE_EVERY samples; fresh samples can raise the long-run reach but never lower it.
          if (hs.Count >= 40 && (n + 1) % RANGE_EVERY == 0) {
            var sorted = hs.OrderBy(x => x).ToList(); double p95 = sorted[(int)(sorted.Count * 0.95)], old;
            bool had = RangeMax.TryGetValue(t, out old);
            if (!had || p95 > old) {
              RangeMax[t] = p95; RecalcCap(t);
              if (!had || p95 > old + 40) Lesson(Mem.SeedName(t) + " hits zombies at least ~" + (p95 / 80).ToString("0.0") + " tiles away");
            }
          }
        }
      }
      // Unhurt: the nearest zombie, nearest and undamaged for 2.5s plus projectile flight time, shooter line-up stable as long.
      ZRec zrec;
      double dn = near == null ? 0 : near.X + 40 - front, win = 2.5 + dn / 250;
      if (near != null && now - laneSince[row] >= win && now - laneNearSince[row] >= win && zr.TryGetValue(near.Id, out zrec) && !zrec.UnhurtNoted
          && now - zrec.LastHurt >= win && !Untargetable(near.Type)) {
        double d = dn, lo; RangeMax.TryGetValue(t, out lo);
        if (d >= 200 && d > lo + 20) {
          zrec.UnhurtNoted = true;
          List<double> u; if (!unhurtD.TryGetValue(t, out u)) unhurtD[t] = u = new List<double>();
          u.Add(d); if (u.Count > 100) u.RemoveAt(0);
          int k; unhurtNew.TryGetValue(t, out k); unhurtNew[t] = ++k;
          if (k % UNHURT_EVERY == 0) RecalcCap(t);
        }
      }
    }
  }
  // Kinds plain shots often cannot touch (in the air, underground, under water, or dropping in).
  static bool Untargetable(int type) { string n = Mem.ZomName(type); return n == "BALLOON" || n == "DIGGER" || n == "SNORK" || n == "BUNGEE" || n == "DOLPH" || n == "BOSS" || n == "POLE"; }
  // Dropped seed packets: how long they stay on the lawn before fading (learned from ones that expired untouched).
  public static double PacketLifetime = -1; static int lifetimeN;
  static readonly Dictionary<uint, double> packetSeen = new Dictionary<uint, double>();
  static readonly Dictionary<uint, int> packetCtr = new Dictionary<uint, int>(), packetCtr0 = new Dictionary<uint, int>();   // disappear counter: last seen / first seen
  public static readonly HashSet<uint> PacketsTaken = new HashSet<uint>();   // ones we picked up ourselves
  public static double PacketTimeLeft(uint id) {
    double t0; if (PacketLifetime < 0 || !packetSeen.TryGetValue(id, out t0)) return -1;
    if (!Sane("packetlife", PacketLifetime, 3, 60, "dropped seed packet lifetime (s)")) return -1;
    return Math.Max(0, PacketLifetime - (lastFrame - t0));
  }
  static void TrackPackets(State s, double now) {
    var present = new HashSet<uint>();
    foreach (var c in s.Suns.Where(c => c.IsPacket)) { present.Add(c.Id); if (!packetSeen.ContainsKey(c.Id)) { packetSeen[c.Id] = now; packetCtr0[c.Id] = c.Disappear; } packetCtr[c.Id] = c.Disappear; }
    foreach (var id in packetSeen.Keys.Where(k => !present.Contains(k)).ToList()) {
      int c0, c1; packetCtr0.TryGetValue(id, out c0); packetCtr.TryGetValue(id, out c1); packetCtr0.Remove(id); packetCtr.Remove(id);
      // Only a packet that really ran its clock out counts (its fade counter was near the end when last seen) - packets
      // collected, wiped by a round change or swept up at level end used to drag the estimate down to ~11 s (real: ~15.7 s).
      // Time it was already on the lawn before we first saw it (bot restarted) is added back from the counter.
      double life = now - packetSeen[id] + Math.Max(0, c0) / 100.0; packetSeen.Remove(id);
      if (PacketsTaken.Remove(id) || life < 2 || s.Plants.Count == 0 || c1 < 1400) continue;
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
      if (OtherDamage(s, row, isShooter)) continue;                      // not only shooters could have done it
      int rw = row;
      // Credit only shooters that are awake and have a zombie within their reach (a col-0 puff did not hit the col-8 zombie).
      var sh = s.Plants.Where(p => p.Row == rw && isShooter(p.Type) && Awake(p.Type) && s.Zombies.Any(z => z.Row == rw && !z.Charmed && z.X < Bot.VISIBLE_X
                 && z.X + 40 > 40 + p.Col * 80 + 20 && z.X + 40 - (40 + p.Col * 80 + 40) <= Range(p.Type))).ToList();
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

  class ZRec { public string Key; public float SeenX, LastX, AnchorX; public double AnchorT, SeenT, LastHurt; public int LastTotal; public double Absorbed; public int Row; public bool UnhurtNoted; }
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
    bool hadPrev = lastFrame >= 0;   // captured BEFORE lastFrame moves: the first frame after a start/reset sees everyone as "new"
    double dt = !hadPrev || now < lastFrame ? 0 : Math.Min(5, now - lastFrame);
    lastFrame = now;
    var present = new HashSet<uint>();
    var laneDmg = new double[6]; bool explosion = false; damagedNow.Clear();
    var vanished = new List<ZRec>();
    foreach (var z in s.FogZombies) fogSeen.Add(z.Id);
    // Summons are only judged where nothing else drops zombies mid-lawn: no vases, nothing hidden in fog.
    bool summonWatch = hadPrev && s.Vases.Count == 0 && s.FogHidden <= 0 && s.FogZombies.Count == 0;

    foreach (var z in s.Zombies) {
      if (z.X >= visibleX) continue;
      present.Add(z.Id);
      ZRec r;
      if (!zr.TryGetValue(z.Id, out r)) {
        // A zombie appearing mid-lawn (not walking in from the right, not out of a grave / the pool / the sky) next to another one.
        int zcol = Math.Max(0, Math.Min(8, (int)((z.X + 30 - 40) / 80)));
        bool water = z.Row >= 0 && z.Row < 6 && s.Square[z.Row, zcol] == 3;
        if (summonWatch && z.X < 735 && !z.Charmed && !NoSummon(z.Type) && !fogSeen.Contains(z.Id) && !water && !NearGrave(s, z.Row, zcol)) {   // walkers first appear near x~755
          Zom caller = null; float best = 170;
          foreach (var o in s.Zombies)
            if (o.Id != z.Id && !o.Charmed && !NoSummon(o.Type) && Math.Abs(o.Row - z.Row) <= 1 && Math.Abs(o.X - z.X) < best && Tracked(o.Id, now)) { caller = o; best = Math.Abs(o.X - z.X); }
          if (caller != null) NoteSummon(caller, now);
        }
        r = new ZRec { Key = KeyOf(z), SeenX = z.X, LastX = z.X, AnchorX = z.X, AnchorT = now, SeenT = now, LastHurt = now, LastTotal = z.Total, Row = z.Row };
        zr[z.Id] = r;
        var k = K(r.Key); k.Seen++;
        if (k.Seen == 1) { Lesson("new zombie kind spotted: " + r.Key); Quips.NewKind(r.Key); }
        continue;
      }
      if (summonWatch && dt > 0 && !z.Charmed) { SK(r.Key).Exposure += dt; sumExposure += dt; }   // zombie-seconds watched
      int drop = r.LastTotal - z.Total;
      if (drop > 0) { damagedNow.Add(z.Id); r.LastHurt = now; }
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
    foreach (var id in zr.Keys.Where(id => !present.Contains(id)).ToList()) { vanished.Add(zr[id]); zr.Remove(id); pendingSummon.Remove(id); lastSummonEvent.Remove(id); fogSeen.Remove(id); }
    ObserveRange(s, laneDmg, shooterTypes, visibleX, now);
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
        int rw = row;
        if (!s.Zombies.Any(z => z.Row == rw && z.X < visibleX)) continue;
        // Awake shooters only (night mushrooms sleep by day), and only lanes where nothing else hurts zombies.
        var sh = s.Plants.Where(p => p.Row == rw && shooterTypes.Contains(p.Type) && Awake(p.Type)).ToList();
        if (sh.Count == 0 || sh.Select(p => p.Type).Distinct().Count() != 1 || OtherDamage(s, row, shooterTypes.Contains)) continue;
        int t = sh[0].Type;
        // Only count time when a zombie is actually within this shooter's reach (else short-range plants look useless).
        double front = sh.Max(p => 40 + p.Col * 80 + 40);
        double gap = s.Zombies.Where(z => z.Row == rw && z.X + 40 > front - 20).Select(z => z.X + 40 - front).DefaultIfEmpty(9999).Min();
        double rg = Range(t); if (gap > rg) continue;
        int inReach = sh.Count(p => gap + front - (40 + p.Col * 80 + 40) <= rg);   // shooters further back may not reach it
        double a, b; dmgAcc.TryGetValue(t, out a); shooterSec.TryGetValue(t, out b);
        dmgAcc[t] = a + laneDmg[row]; shooterSec[t] = b + Math.Max(1, inReach) * dt;
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
    zr.Clear(); mines.Clear(); pendingSummon.Clear(); lastSummonEvent.Clear(); fogSeen.Clear(); lastFrame = -1;
    for (int i = 0; i < 6; i++) { laneSig[i] = -1; laneNear[i] = 0; } levelStartTime = -1; skyThisLevel = 0; skySeen.Clear(); collected = 0;
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
      foreach (var se in Summon) lines.Add("SE|" + se.Key + "|" + se.Value.Events + "|" + se.Value.Exposure.ToString("0.#", IC) + "|" + string.Join(",", se.Value.Levels));
      foreach (var d in LevelDeaths) lines.Add("D2|" + d.Key + "|" + d.Value);
      foreach (var lv in LevelUse) foreach (var u in lv.Value) lines.Add("LU|" + lv.Key + "|" + u.Key + "|" + u.Value);
      foreach (var rg in RangeMax) lines.Add("R|" + rg.Key + "|" + rg.Value.ToString(IC) + "|" + (rangeN.ContainsKey(rg.Key) ? rangeN[rg.Key] : 0));
      foreach (var u in unhurtD) if (u.Value.Count > 0) lines.Add("RU|" + u.Key + "|" + string.Join(";", u.Value.Select(d => d.ToString("0", IC))));
      lines.Add("T2|" + PacketLifetime.ToString(IC) + "|" + lifetimeN);
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
        else if (p[0] == "SE") { var k = SK(p[1]); k.Events = int.Parse(p[2]); k.Exposure = double.Parse(p[3], IC); k.Levels.Clear(); if (p.Length > 4) foreach (var lv in p[4].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) k.Levels.Add(int.Parse(lv)); }
        else if (p[0] == "SU") { if (p[1] == "DANCER") legacySummon[p[1]] = int.Parse(p[2]); }   // old raw counts: all coincidences except the dancer
        else if (p[0] == "RU") { var u = new List<double>(); foreach (var d in p[2].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) u.Add(double.Parse(d, IC)); unhurtD[int.Parse(p[1])] = u; }
        else if (p[0] == "D2") LevelDeaths[int.Parse(p[1])] = int.Parse(p[2]);
        else if (p[0] == "LU") { int lv = int.Parse(p[1]); if (!LevelUse.ContainsKey(lv)) LevelUse[lv] = new Dictionary<int, int>(); LevelUse[lv][int.Parse(p[2])] = int.Parse(p[3]); }
        else if (p[0] == "R") { RangeMax[int.Parse(p[1])] = double.Parse(p[2], IC); rangeN[int.Parse(p[1])] = int.Parse(p[3]); }
        else if (p[0] == "T2") { PacketLifetime = double.Parse(p[1], IC); lifetimeN = int.Parse(p[2]); }
        else if (p[0] == "V") SeedValue[int.Parse(p[1])] = double.Parse(p[2], IC);
        else if (p[0] == "K") SkyBaseline = double.Parse(p[1], IC);
        else if (p[0] == "U") Picks[int.Parse(p[1])] = new[] { int.Parse(p[2]), int.Parse(p[3]) };
        else if (p[0] == "S") { Deaths = int.Parse(p[1]); Wins = int.Parse(p[2]); Streak = int.Parse(p[3]); BestStreak = int.Parse(p[4]); }
      } catch { }
    }
    // One-time migration of the old SU| counts (only DANCER survives): no exposure or levels were recorded, so its
    // exposure is estimated from sightings and it is marked as seen on two (unknown) levels.
    foreach (var kv in legacySummon) {
      if (Summon.ContainsKey(kv.Key)) continue;
      ZKnow z; var k = SK(kv.Key); k.Events = kv.Value; k.Exposure = Z.TryGetValue(kv.Key, out z) ? z.Seen * 20.0 : kv.Value * 200.0; k.Levels.Add(-1); k.Levels.Add(-2);
    }
    legacySummon.Clear();
    sumEvents = Summon.Values.Sum(k => (double)k.Events); sumExposure = Summon.Values.Sum(k => k.Exposure);
  }
  static readonly Dictionary<string, int> legacySummon = new Dictionary<string, int>();
  static bool capsReady;
  static void CapsOnce() { if (capsReady || !Roles.DescriptionsLoaded) return; capsReady = true; foreach (var t in unhurtD.Keys.ToList()) RecalcCap(t); }
}
