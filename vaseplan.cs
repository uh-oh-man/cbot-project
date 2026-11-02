// vaseplan.cs - cbot v2: the Vasebreaker planner. Which vase to open next, when, and what to keep the squashes for.
// Plays like a player with a notebook: it sees only what is on screen - the vase LOOKS (leaf / "?" / skull), what pops
// out of a vase it just broke, the lawn - plus its own notes from earlier attempts (C:\cbot\vase_contents.txt).
// It never reads a vase's hidden contents (GridItem mScaryPotType).
// Main thread only (Observe/ChooseBreak from the bot loop); the panel reads Status(), a cached string.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

public static class VasePlanner {
  const string FILE = @"C:\cbot\vase_contents.txt";
  const int SQUASH = 17, HYPNO = 12, SNOW = 5;
  const int LQ = 0, LLEAF = 1, LSKULL = 2;   // vase look index: "?", green leaf, skull
  const int OZ = 0, OP = 1, ON = 2;          // what came out: zombie, plant, nothing
  static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

  // One round of one attempt, as written in the notebook. Partial rounds (died / reloaded) are lower bounds.
  class Rec {
    public int Lvl, Round, Start, Broken; public string Result = ""; public bool Reload;
    public Dictionary<string, int> Z = new Dictionary<string, int>(), S = new Dictionary<string, int>();
    public int[,] Look = new int[3, 3];   // [look, outcome] counts
  }
  class Mark { public int Lvl, Round, N; public double Gt; }   // "round started" marker: survives a hot reload
  class Broke { public int Cell, Look; public double T; }      // a vase that just broke, waiting for what came out
  class Est {   // what the notebook says is still inside the unopened vases
    public int Q, Leaf, Skull; public bool Notebook;
    public double PzQ, PpQ, PzLeaf, PpLeaf, PzSkull, PpSkull, RemZt, RemSt, RemFoot, RemSquash;
    public Dictionary<string, double> RemZ = new Dictionary<string, double>(), RemS = new Dictionary<string, double>();
    public double Pz(int look) { return look == LLEAF ? PzLeaf : look == LSKULL ? PzSkull : PzQ; }
    public double Pp(int look) { return look == LLEAF ? PpLeaf : look == LSKULL ? PpSkull : PpQ; }
  }

  static readonly List<Rec> history = new List<Rec>();
  static readonly List<Mark> marks = new List<Mark>();
  static bool loaded;
  static int lvl = -1;
  public static int Round;                 // 0 = no vase round seen this attempt
  public static bool Hunting;              // a football is out, nothing to squash it with, squashes may still be in vases
  // Mod author, round 3: "When the dancer shows up, start breaking all vases for hypnos. If there are one or two more backup
  // dancers left, spam peas on them." On while an enemy DANCER is on the lawn, or 3+ enemy backups; off at <= 2 backups.
  public static bool DanceHunt;
  public static string LastWhy = "";   // the planner's current decision, for the panel
  public const double BDANCE_GROW = 35;    // hp / game-s a backup dancer gains while it is not eating (replays 17:13:55, 17:16:02,
                                           // 17:23:09: +40 every ~1 s from 270 to 2190; it paused while the backup was chewing)
  static bool active, hintFinal; static int hintRound, maxRound;
  static Rec cur;
  static HashSet<int> prevVases, vanished;
  static readonly Dictionary<int, int> look = new Dictionary<int, int>();
  static HashSet<uint> prevZ = new HashSet<uint>(), prevC = new HashSet<uint>();
  static readonly List<Broke> pending = new List<Broke>();
  static double lastGT = -1, clearT = -1, lastClickGT = -99, lastBreakNow = -99, nextEval = -99;
  static readonly Dictionary<string, int> hpSeen = new Dictionary<string, int>();
  static readonly List<string> events = new List<string>();
  static Dictionary<string, double> priorZ = new Dictionary<string, double>(), priorS = new Dictionary<string, double>();
  static readonly double[,] lookOdds = new double[3, 3];
  static string status = "";
  static State estFor; static Est est;
  public static string AmbushWhy = "";

  // ---------- Expert facts (same numbers as pro.cs; that file's helpers are private to Bot) ----------
  static readonly Dictionary<int, double> Dps = new Dictionary<int, double> {
    { 0, 14 }, { 5, 14 }, { 8, 14 }, { 13, 14 }, { 10, 14 }, { 24, 14 }, { 26, 14 }, { 50, 14 }, { 7, 28 }, { 18, 14 }, { 28, 14 },
    { 29, 14 }, { 40, 56 }, { 42, 40 }, { 43, 28 }, { 32, 28 }, { 34, 18 }, { 39, 56 }, { 44, 56 } };
  static readonly HashSet<string> Tough = new HashSet<string> { "REGEN", "GARG", "ZAMB", "POLE", "DOLPH", "SNORK", "JACK", "DANCER", "LADDER", "POGO", "DIGGER" };
  public static bool IsShooter(int seed) { return Dps.ContainsKey(seed); }
  static bool IsShooterName(string n) { return Dps.Keys.Any(k => Mem.SeedName(k) == n); }
  static double Speed(string n) {   // px per game second
    switch (n) {
      case "POLE": case "FOOT": case "BALLOON": case "CAT": case "CAT2": case "CAT3": case "DOLPH": case "POGO": return 32;
      case "NEWS": return 22; case "DANCER": return 20; case "BOB": return 50; case "DIGGER": return 40;
      default: return 17;
    }
  }
  static int Hp(string n) {   // this mod's numbers: first-sight Total when we have seen one, else the logged typical value
    int v; if (hpSeen.TryGetValue(n, out v)) return v;
    switch (n) {
      case "NORM": case "BDANCE": return 570; case "CONE": return 940; case "PAIL": case "DOOR": return 1400;
      case "FOOT": return 1900; case "JACK": return 900;
      case "BDANCEG": return 900;   // a backup as the planner should fear it: it keeps growing while it walks
      default: return 800;
    }
  }
  static string F(double x) { return x.ToString("0.00", Inv); }
  static int LookIdx(State s, int k) { return s.LeafVases.Contains(k) ? LLEAF : s.SkullVases.Contains(k) ? LSKULL : LQ; }
  static string LookName(int l) { return l == LLEAF ? "leaf" : l == LSKULL ? "skull" : "?"; }
  static List<Zom> Live(State s) { return s.Zombies.Where(z => z.X < Bot.VISIBLE_X && !z.Charmed).ToList(); }

  // Starting notes for 4-5 (map.md: 15/20/25 vases; seeds and zombies per round from 360 attempts of logs).
  static Dictionary<string, double> SeedPrior(int level, int round, bool zom) {
    var d = new Dictionary<string, double>();
    if (level != 35) return d;
    if (round == 1) { if (zom) { d["NORM"] = 4; d["PAIL"] = 1; } else { d["PEA"] = 5; d["SQUASH"] = 5; } }
    if (round == 2) { if (zom) { d["NORM"] = 4; d["FOOT"] = 2; d["PAIL"] = 1; } else { d["SNOW"] = 5; d["PEA"] = 4; d["SQUASH"] = 4; } }
    if (round == 3) { if (zom) { d["DANCER"] = 1; d["NORM"] = 3; d["PAIL"] = 2; } else { d["PEA"] = 8; d["SNOW"] = 8; d["HYPNO"] = 3; } }
    return d;
  }
  static int SeedStart(int level, int round) { return level == 35 && round >= 1 && round <= 3 ? 10 + 5 * round : 0; }

  // ---------- Notebook file ----------
  static void Load() {
    loaded = true;
    try {
      if (!File.Exists(FILE)) return;
      foreach (var line in File.ReadAllLines(FILE)) {
        try {
          if (line.StartsWith("V ")) { var r = ParseV(line); if (r != null) history.Add(r); }
          else if (line.StartsWith("S ")) { var m = ParseS(line); if (m != null) marks.Add(m); }
        } catch { }   // one bad line never costs the notebook
      }
      if (marks.Count > 40) marks.RemoveRange(0, marks.Count - 40);
    } catch { }
  }
  static Dictionary<string, string> Kv(string line) {
    var d = new Dictionary<string, string>();
    foreach (var t in line.Split(' ')) { int e = t.IndexOf('='); if (e > 0) d[t.Substring(0, e)] = t.Substring(e + 1); }
    return d;
  }
  static Rec ParseV(string line) {
    var d = Kv(line); var r = new Rec(); string v;
    if (!d.TryGetValue("lvl", out v) || !int.TryParse(v, out r.Lvl)) return null;
    if (!d.TryGetValue("r", out v) || !int.TryParse(v, out r.Round) || r.Round <= 0) return null;
    if (d.TryGetValue("n", out v)) int.TryParse(v, out r.Start);
    if (d.TryGetValue("br", out v)) int.TryParse(v, out r.Broken);
    if (d.TryGetValue("res", out v)) r.Result = v;
    r.Reload = d.TryGetValue("rl", out v) && v == "1";
    if (d.TryGetValue("z", out v)) Counts(v, r.Z);
    if (d.TryGetValue("s", out v)) Counts(v, r.S);
    if (d.TryGetValue("lk", out v))
      foreach (var part in v.Split(',')) {   // q:Z/P/N,l:Z/P/N,k:Z/P/N
        var kv = part.Split(':'); if (kv.Length != 2) continue;
        int li = kv[0] == "l" ? LLEAF : kv[0] == "k" ? LSKULL : LQ; var n = kv[1].Split('/');
        for (int o = 0; o < 3 && o < n.Length; o++) int.TryParse(n[o], out r.Look[li, o]);
      }
    return r;
  }
  static void Counts(string v, Dictionary<string, int> into) {
    if (v == "-") return;
    foreach (var part in v.Split(',')) { var kv = part.Split(':'); int c; if (kv.Length == 2 && int.TryParse(kv[1], out c)) into[kv[0]] = c; }
  }
  static Mark ParseS(string line) {
    var d = Kv(line); var m = new Mark(); string v;
    if (!d.TryGetValue("lvl", out v) || !int.TryParse(v, out m.Lvl)) return null;
    if (!d.TryGetValue("r", out v) || !int.TryParse(v, out m.Round)) return null;
    if (!d.TryGetValue("n", out v) || !int.TryParse(v, out m.N)) return null;
    if (!d.TryGetValue("gt", out v) || !double.TryParse(v, NumberStyles.Float, Inv, out m.Gt)) return null;
    return m;
  }
  static string Join(Dictionary<string, int> d) { return d.Count == 0 ? "-" : string.Join(",", d.Select(kv => kv.Key + ":" + kv.Value).ToArray()); }
  static string Line(Rec r) {
    var sb = new StringBuilder("V ");
    sb.Append(DateTime.Now.ToString("yyyy-MM-dd_HH:mm:ss", Inv)).Append(" lvl=").Append(r.Lvl).Append(" r=").Append(r.Round)
      .Append(" n=").Append(r.Start).Append(" br=").Append(r.Broken).Append(" res=").Append(r.Result).Append(" rl=").Append(r.Reload ? 1 : 0)
      .Append(" z=").Append(Join(r.Z)).Append(" s=").Append(Join(r.S)).Append(" lk=");
    string[] ln = { "q", "l", "k" };
    for (int i = 0; i < 3; i++) sb.Append(i > 0 ? "," : "").Append(ln[i]).Append(':').Append(r.Look[i, 0]).Append('/').Append(r.Look[i, 1]).Append('/').Append(r.Look[i, 2]);
    return sb.ToString();
  }
  static void Append(string line) { try { File.AppendAllText(FILE, line + Environment.NewLine); } catch { } }

  // ---------- Priors: the notebook's typical round, minus what has come out already ----------
  static double Median(List<int> xs) { if (xs.Count == 0) return 0; xs.Sort(); int n = xs.Count; return n % 2 == 1 ? xs[n / 2] : (xs[n / 2 - 1] + xs[n / 2]) / 2.0; }
  static Dictionary<string, double> Prior(int level, int round, bool zom) {
    var recs = history.Where(r => r.Lvl == level && r.Round == round).ToList();
    var full = recs.Where(r => !r.Reload && r.Start > 0 && r.Broken >= r.Start).ToList();
    var seed = SeedPrior(level, round, zom);
    var types = new HashSet<string>(seed.Keys);
    foreach (var r in recs) foreach (var k in (zom ? r.Z : r.S).Keys) types.Add(k);
    var d = new Dictionary<string, double>();
    foreach (var t in types) {
      string tt = t;
      Func<Rec, int> cnt = r => { int c; return (zom ? r.Z : r.S).TryGetValue(tt, out c) ? c : 0; };
      double sv; seed.TryGetValue(t, out sv);
      double v = full.Count >= 2 ? Median(full.Select(cnt).ToList()) : sv;
      // partial rounds are lower bounds: when even they typically saw more, the true total is at least that
      if (recs.Count > 0) v = Math.Max(v, Median(recs.Select(cnt).ToList()));
      if (v > 0) d[t] = v;
    }
    return d;
  }
  static void BuildPriors() {
    priorZ = Prior(lvl, Round, true); priorS = Prior(lvl, Round, false);
    maxRound = Math.Max(lvl == 35 ? 3 : 0, history.Where(r => r.Lvl == lvl && r.Result == "won").Select(r => r.Round).DefaultIfEmpty(0).Max());
    // per-look odds: vanilla pseudo-counts (leaf = plant, skull = zombie, "?" mostly plants), then this level's data
    double[,] c = { { 1, 2, 0.1 }, { 0.2, 5, 0.2 }, { 5, 0.2, 0.2 } };
    foreach (var r in history) if (r.Lvl == lvl) for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) c[i, j] += r.Look[i, j];
    if (cur != null) for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) c[i, j] += cur.Look[i, j];
    for (int i = 0; i < 3; i++) { double t = c[i, 0] + c[i, 1] + c[i, 2]; for (int j = 0; j < 3; j++) lookOdds[i, j] = c[i, j] / t; }
    estFor = null;
  }
  static int RoundForStart(int level, int n) {
    var g = history.Where(r => r.Lvl == level && r.Start == n && !r.Reload).GroupBy(r => r.Round).OrderByDescending(x => x.Count()).FirstOrDefault();
    if (g != null) return g.Key;
    for (int r = 1; r <= 3; r++) if (SeedStart(level, r) == n) return r;
    return 0;
  }
  static int StartFor(int level, int round) {
    var g = history.Where(r => r.Lvl == level && r.Round == round && !r.Reload && r.Start > 0).GroupBy(r => r.Start).OrderByDescending(x => x.Count()).FirstOrDefault();
    return g != null ? g.Key : SeedStart(level, round);
  }
  public static bool FinalRound { get { return hintFinal || (maxRound > 0 && Round >= maxRound); } }
  public static bool Active { get { return active; } }

  static Est Estimate(State s) {
    if (ReferenceEquals(s, estFor) && est != null) return est;
    var e = new Est();
    foreach (int k in s.Vases) { int l = LookIdx(s, k); if (l == LLEAF) e.Leaf++; else if (l == LSKULL) e.Skull++; else e.Q++; }
    e.PzLeaf = lookOdds[LLEAF, OZ]; e.PpLeaf = lookOdds[LLEAF, OP]; e.PzSkull = lookOdds[LSKULL, OZ]; e.PpSkull = lookOdds[LSKULL, OP];
    if (cur != null) {
      foreach (var kv in priorZ) { int seen; cur.Z.TryGetValue(kv.Key, out seen); double r = Math.Max(0, kv.Value - seen); if (r > 0) e.RemZ[kv.Key] = r; }
      foreach (var kv in priorS) { int seen; cur.S.TryGetValue(kv.Key, out seen); double r = Math.Max(0, kv.Value - seen); if (r > 0) e.RemS[kv.Key] = r; }
    }
    e.RemZt = e.RemZ.Values.Sum(); e.RemSt = e.RemS.Values.Sum();
    double tot = e.RemZt + e.RemSt, left = s.Vases.Count;
    if (tot > left && tot > 0) {   // the vases left cannot hold more than one thing each
      double f = left / tot;
      foreach (var k in e.RemZ.Keys.ToList()) e.RemZ[k] *= f;
      foreach (var k in e.RemS.Keys.ToList()) e.RemS[k] *= f;
      e.RemZt *= f; e.RemSt *= f;
    }
    e.Notebook = priorZ.Count + priorS.Count > 0;
    e.PzQ = lookOdds[LQ, OZ]; e.PpQ = lookOdds[LQ, OP];
    if (e.Notebook && e.Q > 0) {
      double zq = Math.Max(0, e.RemZt - e.Skull * e.PzSkull - e.Leaf * e.PzLeaf), pq = Math.Max(0, e.RemSt - e.Leaf * e.PpLeaf - e.Skull * e.PpSkull);
      if (zq + pq > 0.01) { e.PzQ = 0.75 * zq / (zq + pq) + 0.25 * lookOdds[LQ, OZ]; e.PpQ = 0.75 * pq / (zq + pq) + 0.25 * lookOdds[LQ, OP]; }
    }
    e.PzQ = Math.Max(0.02, Math.Min(0.98, e.PzQ)); e.PpQ = Math.Max(0, Math.Min(1 - e.PzQ, e.PpQ));
    double v;
    if (e.Notebook) { e.RemFoot = e.RemZ.TryGetValue("FOOT", out v) ? v : 0; e.RemSquash = e.RemS.TryGetValue("SQUASH", out v) ? v : 0; }
    else { e.RemFoot = (Round >= 2 && s.Vases.Count > 3) ? 1 : 0; e.RemSquash = s.Vases.Count > 0 ? 1 : 0; }   // no notes: the old rule of thumb
    if (s.Vases.Count == 0) { e.RemFoot = 0; e.RemSquash = 0; }
    estFor = s; est = e; return e;
  }
  static Dictionary<string, double> Frac(Dictionary<string, double> rem, string dflt) {
    var d = new Dictionary<string, double>(); double t = rem.Values.Sum();
    if (t < 0.01) { d[dflt] = 1; return d; }
    foreach (var kv in rem) d[kv.Key] = kv.Value / t;
    return d;
  }

  // ---------- Lane model ----------
  // The row's zombies walk to the house; its shooters kill them front to back. Returns the worst
  // (time needed to kill / time it has) over the zombies: < 1 holds, > 1 a breach is expected (capped at 3).
  // A standing squash / hypno takes the first zombie to reach it; each other plant in the way costs ~3 s of eating.
  static double LaneRisk(State s, List<Plt> rowPlants, int row, List<Zom> live, string extra, float extraX) {
    var xs = new float[20]; var hp = new double[20]; var sp = new double[20]; var gr = new bool[20]; int n = 0;
    foreach (var z in live) if (z.Row == row && n < 19) { string zn = Mem.ZomName(z.Type); xs[n] = z.X; hp[n] = Seen.Hp(z); sp[n] = Speed(zn); gr[n] = zn == "BDANCE"; n++; }
    if (extra != null) { xs[n] = extraX; hp[n] = Hp(extra); sp[n] = Speed(extra); gr[n] = extra == "BDANCE" || extra == "BDANCEG"; n++; }
    if (n == 0) return 0;
    for (int i = 1; i < n; i++) for (int j = i; j > 0 && xs[j] < xs[j - 1]; j--) {   // nearest the house first
      float tx = xs[j]; xs[j] = xs[j - 1]; xs[j - 1] = tx; double th = hp[j]; hp[j] = hp[j - 1]; hp[j - 1] = th; th = sp[j]; sp[j] = sp[j - 1]; sp[j - 1] = th;
      bool tg = gr[j]; gr[j] = gr[j - 1]; gr[j - 1] = tg;
    }
    foreach (var p in rowPlants) if (p.Type == SQUASH || p.Type == HYPNO)
      for (int i = 0; i < n; i++) if (hp[i] > 0 && xs[i] + 40 > Bot.CellX(p.Col) - 40) { hp[i] = 0; break; }
    double t = 0, worst = 0;
    for (int i = 0; i < n; i++) {
      if (hp[i] <= 0) continue;
      double dps = 0; int block = 0; bool slow = false;
      foreach (var p in rowPlants) {
        if (Bot.CellX(p.Col) >= xs[i] + 40) continue;
        double d; if (Dps.TryGetValue(p.Type, out d)) dps += d;
        if (p.Type == SNOW) slow = true;
        if (p.Type != SQUASH && p.Type != HYPNO) block++;
      }
      if (dps <= 0) { worst = 3; break; }
      double reach = Math.Max(1, (xs[i] + 40) / (sp[i] * (slow ? 0.5 : 1))) + 3 * block;
      // A backup dancer grows ~35 hp/s: one or two peas never catch up with it (17:23:09 lane 3: two shooters, 870 -> 1450 hp).
      if (gr[i]) { double net = dps - BDANCE_GROW * 0.8; if (net <= 2) { worst = 3; break; } t += hp[i] / net; }
      else t += hp[i] / dps;
      worst = Math.Max(worst, t / reach);
    }
    if (row >= 0 && row < 6 && s.Mowers[row] == 1) worst *= 0.6;   // a ready mower forgives one breach
    return Math.Min(3, worst);
  }
  static List<Plt>[] ByRow(State s) {
    var a = new List<Plt>[6]; for (int r = 0; r < 6; r++) a[r] = new List<Plt>();
    foreach (var p in s.Plants) if (p.Row >= 0 && p.Row < 6) a[p.Row].Add(p);
    return a;
  }
  static int SquashHeld(State s) { return s.Suns.Count(c => c.IsPacket && c.Seed == SQUASH) + s.Packets.Count(p => !p.FromCoin && p.Active && p.Type == SQUASH); }
  // Footballs on the lawn with no squash standing in front of them.
  static List<Zom> UncoveredFeet(State s, List<Zom> live) {
    return live.Where(z => Mem.ZomName(z.Type) == "FOOT" && !s.Plants.Any(q => q.Row == z.Row && q.Type == SQUASH && Bot.CellX(q.Col) < z.X + 70)).ToList();
  }
  // Squashes to keep for footballs: one per uncovered football out now, plus the ones the notebook says are still in vases.
  public static int SquashReserve(State s) {
    var e = Estimate(s); var live = Live(s);
    return UncoveredFeet(s, live).Count + (e.RemFoot >= 0.5 ? (int)Math.Ceiling(e.RemFoot - 0.25) : 0);
  }
  public static double RemainingOf(State s, string name) {
    var e = Estimate(s); double v;
    return e.RemZ.TryGetValue(name, out v) ? v : e.RemS.TryGetValue(name, out v) ? v : 0;
  }
  // A dropped packet about to fade. MEASURED in this mod (replays, K: age/disappear): a packet vanishes when its own
  // disappear counter reaches 1500 ticks (8 untouched fades, all at 1488-1500; ~15.7 game-s after it first shows).
  // Learn.PacketLifetime had drifted to ~11.3 s (it learns from packets that vanished for other reasons) and made every
  // squash look "fading" ~8 s after it dropped - that parked/spent all of them before the football came out.
  // So the coin's counter (it drives the blink a player sees) is the only clock used here.
  public const int PACKET_LIFE = 1500, FADE_AT = 1250;   // ticks; fading = the last 2.5 game-s
  public static double PacketLeft(State s, uint coinId) {   // game-s before it fades, -1 unknown
    var c = s.Suns.FirstOrDefault(x => x.Id == coinId);
    return c == null ? -1 : Math.Max(0, (PACKET_LIFE - c.Disappear) / 100.0);
  }
  public static bool Fading(State s, uint coinId) {
    var c = s.Suns.FirstOrDefault(x => x.Id == coinId);
    return c != null && c.Disappear >= FADE_AT;
  }
  // Expected danger from this lane's unopened vases (P(zombie) by look, football-weighted): where shooters / snow peas should stand.
  public static double LaneThreat(State s, int row) {
    if (s.Vases.Count == 0) return 0;
    var e = Estimate(s); var zf = Frac(e.RemZ, "NORM"); double pf; zf.TryGetValue("FOOT", out pf);
    return s.Vases.Where(k => k / 10 == row).Sum(k => e.Pz(LookIdx(s, k))) * (1 + 3 * pf);
  }
  // A plant (not a squash / hypno) in the lane that a football will stop to eat, with a free cell right of it for the squash.
  static bool HasBait(State s, List<Plt> rowPlants, int row) {
    foreach (var p in rowPlants) {
      if (p.Type == SQUASH || p.Type == HYPNO) continue;
      int c = p.Col + 1;
      if (c <= 8 && !s.Blocked.Contains(row * 10 + c) && !rowPlants.Any(q => q.Col == c)) return true;
      if (p.Col >= 1 && !s.Blocked.Contains(row * 10 + p.Col - 1) && !rowPlants.Any(q => q.Col == p.Col - 1)) return true;
    }
    return false;
  }

  // ---------- Observe: what broke, what came out ----------
  public static void Observe(State s, double now) {
    if (!s.HasBoard) return;
    if (!loaded) Load();
    if (s.Level != lvl) { if (active) EndRound("left"); else ResetAttempt(); lvl = s.Level; BuildPriors(); }
    if (lastGT >= 0 && s.GameTime + 1 < lastGT) { if (active) EndRound("died"); else ResetAttempt(); }   // the clock restarted: a new attempt
    lastGT = s.GameTime;
    if (s.Vases.Count == 0 && !active && Round == 0) { prevVases = null; status = ""; Hunting = false; DanceHunt = false; return; }   // not a vase level (yet)
    double gt = s.GameTime;
    if (prevVases == null) { if (s.Vases.Count > 0) Resume(s); Snapshot(s); Finish(s); return; }

    var vs = s.Vases;
    if (vs.Count < prevVases.Count) {
      var gone = prevVases.Where(k => !vs.Contains(k)).ToList();
      bool recent = gt - lastClickGT >= 0 && gt - lastClickGT < 3;
      if (gone.Count <= 2 && (recent || gone.Count == 1) && cur != null) {
        foreach (int k in gone) { int lk; if (!look.TryGetValue(k, out lk)) lk = LQ; pending.Add(new Broke { Cell = k, Look = lk, T = gt }); cur.Broken++; }
        if (vs.Count == 0) clearT = gt;
      } else if (gone.Count > 2) vanished = new HashSet<int>(prevVases);   // the between-round flicker: not breaks
    } else if (vs.Count > prevVases.Count) {
      if (vanished != null && vs.SetEquals(vanished)) { }   // same vases back: nothing happened
      else if (prevVases.Count == 0 || vs.Count >= prevVases.Count + 3) StartRound(s, vs.Count);
      vanished = null;
    }
    foreach (int k in vs) look[k] = LookIdx(s, k);

    // New zombies come out of the broken vase in their row (backup dancers are summoned, not vase contents).
    foreach (var z in s.Zombies) {
      if (prevZ.Contains(z.Id)) continue;
      string n = Mem.ZomName(z.Type);
      int h; int hv = (int)Seen.Hp(z); if (!hpSeen.TryGetValue(n, out h) || hv > h) hpSeen[n] = hv;
      if (z.X >= Bot.VISIBLE_X || z.Charmed || pending.Count == 0 || cur == null) continue;
      if (n == "BDANCE" && s.Zombies.Any(d => Mem.ZomName(d.Type) == "DANCER" && Math.Abs(d.Row - z.Row) <= 1 && Math.Abs(d.X - z.X) < 240)) continue;
      Zom zz = z;
      var b = pending.Where(p => p.Cell / 10 == zz.Row && Math.Abs(Bot.CellX(p.Cell % 10) - (zz.X + 40)) < 120).OrderBy(p => Math.Abs(Bot.CellX(p.Cell % 10) - (zz.X + 40))).FirstOrDefault();
      if (b == null) continue;
      pending.Remove(b); Inc(cur.Z, n); cur.Look[b.Look, OZ]++; estFor = null;
      events.Add("VASE r" + b.Cell / 10 + "c" + b.Cell % 10 + " " + LookName(b.Look) + " -> zombie " + n);
    }
    // New seed packets on the lawn come out of the nearest broken vase.
    foreach (var c in s.Suns) {
      if (!c.IsPacket || prevC.Contains(c.Id) || pending.Count == 0 || cur == null) continue;
      Sun cc = c;
      Func<Broke, double> dist = p => { double dx = Bot.CellX(p.Cell % 10) - cc.X, dy = Bot.CellY(p.Cell / 10) - cc.Y; return Math.Sqrt(dx * dx + dy * dy); };
      var b = pending.OrderBy(dist).FirstOrDefault();
      if (b == null || dist(b) > 160) continue;
      pending.Remove(b); string sn = Mem.SeedName(c.Seed); Inc(cur.S, sn); cur.Look[b.Look, OP]++; estFor = null;
      events.Add("VASE r" + b.Cell / 10 + "c" + b.Cell % 10 + " " + LookName(b.Look) + " -> plant " + sn);
    }
    // Nothing came out within 1.5 game seconds: an empty vase.
    for (int i = pending.Count - 1; i >= 0; i--)
      if (gt - pending[i].T > 1.5) { if (cur != null) cur.Look[pending[i].Look, ON]++; events.Add("VASE r" + pending[i].Cell / 10 + "c" + pending[i].Cell % 10 + " " + LookName(pending[i].Look) + " -> nothing"); pending.RemoveAt(i); }
    if (clearT >= 0 && pending.Count == 0 && gt - clearT > 1.5 && active && !FinalRound) { EndRound("clear"); clearT = -1; }
    Snapshot(s); Finish(s);
  }
  static void Inc(Dictionary<string, int> d, string k) { int v; d.TryGetValue(k, out v); d[k] = v + 1; }
  static void Snapshot(State s) {
    prevVases = new HashSet<int>(s.Vases);
    prevZ = new HashSet<uint>(s.Zombies.Select(z => z.Id));
    prevC = new HashSet<uint>(s.Suns.Where(c => c.IsPacket).Select(c => c.Id));
  }
  static void Finish(State s) {
    estFor = null;
    var e = Estimate(s); var live = Live(s);
    Hunting = s.Vases.Count > 0 && SquashHeld(s) == 0 && e.RemSquash > 0.3 && UncoveredFeet(s, live).Count > 0;
    int nDancer = live.Count(z => Mem.ZomName(z.Type) == "DANCER"), nBack = live.Count(z => Mem.ZomName(z.Type) == "BDANCE");
    DanceHunt = s.Vases.Count > 0 && !Hunting && (nDancer > 0 || nBack >= 1);   // owner: backups keep healing, peas never finish them - hunt hypnos while ANY backup is alive
    if (Round == 0) { status = ""; return; }
    int res = SquashReserve(s), held = SquashHeld(s);
    status = "VASE R" + Round + (FinalRound ? " (final)" : "") + ": " + (s.Vases.Count == 0 ? "between rounds" : s.Vases.Count + " left (?" + e.Q + " leaf" + e.Leaf + " skull" + e.Skull + ")")
      + ", FOOT left~" + e.RemFoot.ToString("0.0", Inv) + ", squash " + held + "/reserve " + res + ", P(z|?)=" + F(e.PzQ) + (Hunting ? ", HUNT" : "") + (DanceHunt ? ", DANCE HUNT" : "");
  }

  static void StartRound(State s, int n) {
    if (active && cur != null) EndRound(clearT >= 0 || cur.Broken >= cur.Start ? "clear" : "skip");
    int r = RoundForStart(lvl, n);
    if (r == 0) r = Math.Max(hintRound, Round + 1);
    Round = r; cur = new Rec { Lvl = lvl, Round = r, Start = n }; active = true; clearT = -1; pending.Clear(); look.Clear(); vanished = null;
    BuildPriors();
    marks.Add(new Mark { Lvl = lvl, Round = r, N = n, Gt = s.GameTime });
    Append("S " + DateTime.Now.ToString("yyyy-MM-dd_HH:mm:ss", Inv) + " lvl=" + lvl + " r=" + r + " n=" + n + " gt=" + s.GameTime.ToString("0.0", Inv));
    events.Add("VASE round " + r + " starts: " + n + " vases" + (FinalRound ? " (final round)" : ""));
  }
  // First frame after a (re)start with vases on the lawn: find which round this is without the lost in-memory state.
  static void Resume(State s) {
    int n = s.Vases.Count; double gt = s.GameTime;
    Mark m = null;   // only THIS attempt's latest marker (file order); an older attempt's round-3 marker must not win
    for (int i = marks.Count - 1; i >= 0; i--) if (marks[i].Lvl == lvl) { m = marks[i]; break; }
    if (m != null && !(m.Gt <= gt + 0.5 && gt - m.Gt < 900 && n <= m.N)) m = null;
    bool clean = s.Plants.Count == 0 && !s.Suns.Any(c => c.IsPacket) && Live(s).Count == 0;
    int r, start;
    if (m != null) {
      if (n == m.N && clean) {   // reloaded right at the round start: nothing was missed
        StartRound(s, n); return;   // writes this attempt's own marker
      }
      r = m.Round; start = m.N;
    } else {
      if (clean) { StartRound(s, n); return; }   // an untouched lawn: a fresh round, whatever its size
      r = InferRound(s); start = Math.Max(n, StartFor(lvl, r));
    }
    Round = r; cur = new Rec { Lvl = lvl, Round = r, Start = start, Broken = Math.Max(0, start - n), Reload = true }; active = true;
    // Everything on the lawn now came out of this round's vases (plants are wiped between rounds): a lower bound of what was seen.
    foreach (var p in s.Plants) Inc(cur.S, Mem.SeedName(p.Type));
    foreach (var c in s.Suns) if (c.IsPacket) Inc(cur.S, Mem.SeedName(c.Seed));
    foreach (var z in s.Zombies) if (z.X < Bot.VISIBLE_X && Mem.ZomName(z.Type) != "BDANCE") Inc(cur.Z, Mem.ZomName(z.Type));
    BuildPriors();
    events.Add("VASE resumed after a restart: round " + r + " (" + n + " of " + start + " vases left" + (m != null ? ", from my round marker" : ", inferred") + ")");
  }
  // Which round fits the lawn: enough vases for its size, and every plant / zombie type on screen is one that round has held before.
  static int InferRound(State s) {
    int n = s.Vases.Count, top = Math.Max(maxRound, Math.Max(hintRound, history.Where(x => x.Lvl == lvl).Select(x => x.Round).DefaultIfEmpty(1).Max()));
    var plantT = new HashSet<string>(s.Plants.Select(p => Mem.SeedName(p.Type)));
    var zomT = new HashSet<string>(s.Zombies.Where(z => z.X < Bot.VISIBLE_X && Mem.ZomName(z.Type) != "BDANCE").Select(z => Mem.ZomName(z.Type)));
    int best = Math.Max(1, hintRound), bestBad = int.MaxValue;
    for (int r = Math.Max(1, hintRound); r <= top; r++) {
      int st = StartFor(lvl, r); if (st > 0 && st < n) continue;
      var seeds = new HashSet<string>(SeedPrior(lvl, r, false).Keys); var zs = new HashSet<string>(SeedPrior(lvl, r, true).Keys);
      foreach (var h in history) if (h.Lvl == lvl && h.Round == r) { foreach (var k in h.S.Keys) seeds.Add(k); foreach (var k in h.Z.Keys) zs.Add(k); }
      if (seeds.Count + zs.Count == 0) continue;
      int bad = plantT.Count(t => !seeds.Contains(t)) + zomT.Count(t => !zs.Contains(t));
      if (bad < bestBad) { bestBad = bad; best = r; }
    }
    return best;
  }
  static void ResetAttempt() {
    Round = 0; active = false; cur = null; hintFinal = false; hintRound = 0; vanished = null; pending.Clear(); look.Clear();
    clearT = -1; lastClickGT = -99; prevVases = null; Hunting = false; DanceHunt = false; status = ""; estFor = null;
  }

  // Dave's lines (bot.cs): "more vases" -> 2, "the last of 'em" -> final. Only ever raises the round (hot reload resets the bot's copy to 1).
  public static void HintRound(int round, bool final) {
    if (final) hintFinal = true;
    if (round <= hintRound) return;
    hintRound = round;
    if (cur != null && round > Round && RoundForStart(lvl, cur.Start) == 0) { Round = round; cur.Round = round; BuildPriors(); }
  }

  // Round over: write what this round showed to the notebook. result: clear / skip (automatic), died, won, left (from the bot).
  public static void EndRound(string result) {
    if (active && cur != null) {
      if (result == "clear" || result == "won") foreach (var p in pending) cur.Look[p.Look, ON]++;   // nothing came out in time
      pending.Clear();
      cur.Result = result;
      if (cur.Broken > 0 || cur.Reload) { history.Add(cur); Append(Line(cur)); }
      active = false; cur = null; clearT = -1;
      BuildPriors();
    }
    if (result == "died" || result == "won" || result == "left") ResetAttempt();
  }

  public static List<string> DrainEvents() { var l = events.ToList(); events.Clear(); return l; }
  public static string Status() { return status; }

  // ---------- ChooseBreak: the risk planner ----------
  // now: monotone wall-clock seconds (pacing defaults are the lead's wall-clock values). Returns row*10+col, or -1 (why says why, "" = just pacing).
  const double RUSH_GAP = 0.3;   // wall-clock s between round-1 breaks
  public static int ChooseBreak(State s, double now, out string why) {
    why = "";
    // Owner's order: round 1 (NORM / PAIL, ~5 squashes inside) is broken back-to-back to get the squashes out fast; no
    // risk / threat / crowd / weapon-on-lawn vetoes. bot.cs only calls this with an empty cursor, and dropped packets are
    // still planted by the pro loop between clicks. (An older all-at-once try died in round 1 3/4 times, but that was with the
    // broken packet-fade clock that dumped the squashes early.)
    bool rush = Round == 1 && !FinalRound;
    if (s.Vases.Count == 0 || now - lastBreakNow < (rush ? RUSH_GAP : 0.5) || now < nextEval) return -1;
    var e = Estimate(s); var live = Live(s); var rows = ByRow(s);
    int held = SquashHeld(s);
    if (rush) {
      bool hopeless1 = live.Any(z => !s.Plants.Any(p => p.Row == z.Row));
      if (s.Vases.Count == 1 && live.Count > 0 && !hopeless1) { why = "wait: last vase of round 1 - clearing the lawn first (breaking it wipes our plants)"; nextEval = now + 0.25; return -1; }
      int rb = -1; double rs = 1e9;
      foreach (int k in s.Vases) {   // most likely plant first, from the right (whatever comes out starts far from the house)
        double sc = -e.Pp(LookIdx(s, k)) - 0.01 * (k % 10);
        if (sc < rs) { rs = sc; rb = k; }
      }
      if (rb < 0) return -1;
      why = "r" + rb / 10 + "c" + rb % 10 + " " + LookName(LookIdx(s, rb)) + " : P(plant)=" + F(e.Pp(LookIdx(s, rb))) + ", squash " + held + ", zombies out " + live.Count + " [round 1 rush]";
      lastBreakNow = now; lastClickGT = s.GameTime;
      return rb;
    }
    bool weaponOnLawn = s.Suns.Any(c => c.IsPacket && c.Seed != SQUASH && !(c.Seed == HYPNO && HypnoTarget(s) == null));   // a held squash never blocks breaking
    bool desperate = !weaponOnLawn && live.Count <= 3 && live.Any(z => z.X < 380 && !s.Plants.Any(p => p.Row == z.Row && p.Col * 80 + 40 < z.X));
    var feet = UncoveredFeet(s, live);
    double[] lane = new double[6]; bool threat = false;
    for (int r = 0; r < 6; r++) {
      lane[r] = LaneRisk(s, rows[r], r, live, null, 0);
      double dps = rows[r].Sum(p => { double d; return Dps.TryGetValue(p.Type, out d) ? d : 0; });
      int rr = r;
      if (lane[r] > 1 || live.Any(z => z.Row == rr && (z.X < 300 || (z.X < 600 && dps == 0)))) threat = true;
    }
    if (weaponOnLawn && !desperate && !Hunting && !DanceHunt) { why = "wait: a dropped plant is still on the lawn"; nextEval = now + 0.25; return -1; }
    // Owner's rule: a zombie walking down a completely empty lane cannot be stopped anyway - then the last vase may as well go.
    bool hopeless = live.Any(z => !s.Plants.Any(p => p.Row == z.Row));
    if (s.Vases.Count == 1 && !FinalRound && live.Count > 0 && !hopeless) { why = "wait: last vase of the round - clearing the lawn first (breaking it wipes our plants)"; nextEval = now + 0.25; return -1; }

    var zf = Frac(e.RemZ, "NORM"); var sf = Frac(e.RemS, "PEA");
    double pSq; sf.TryGetValue("SQUASH", out pSq);
    double pHy; sf.TryGetValue("HYPNO", out pHy);
    var dancers = live.Where(z => { var nm = Mem.ZomName(z.Type); return nm == "DANCER" || nm == "BDANCE"; }).ToList();
    bool needSquash = held == 0 && (feet.Count > 0 || e.RemFoot >= 0.5);
    bool needHypno = live.Any(z => { var nm = Mem.ZomName(z.Type); return nm == "DANCER" || nm == "BDANCE" || nm == "PAIL" || nm == "FOOT"; });
    bool needShooter = lane.Any(d => d > 0.8) || s.Vases.Any(k => k / 10 >= 0 && k / 10 < 6 && !rows[k / 10].Any(p => IsShooter(p.Type)));
    double plantVal = 0;
    foreach (var kv in sf) plantVal += kv.Value * (kv.Key == "SQUASH" ? (needSquash ? 1.6 : 0.6) : kv.Key == "HYPNO" ? (needHypno ? 1.2 : 0.5) : IsShooterName(kv.Key) ? (needShooter ? 1.0 : 0.5) : 0.4);

    int best = -1; double bestScore = 1e9, bestRisk = 0, bestPz = 0, bestPp = 0;
    foreach (int k in s.Vases) {
      int row = k / 10, col = k % 10, lk = LookIdx(s, k);
      if (row < 0 || row > 5) continue;
      double pz = e.Pz(lk), pp = e.Pp(lk), rz = 0;
      foreach (var kv in zf) {
        double d = LaneRisk(s, rows[row], row, live, kv.Key, Bot.CellX(col) - 40);
        // A squash in hand answers one football once it stops to eat - only if the lane has a plant for it to stop at
        // (a football in an empty lane never stops: replay 17:04:22, lane 3 got its first plant 1.7 s after the football came out).
        if (kv.Key == "FOOT" && held > 0 && HasBait(s, rows[row], row)) d *= 0.5;
        else if (kv.Key == "FOOT" && held > 0) d *= 1.2;
        // A dancer brings backups into its lane and both neighbours: the release is only as safe as the weakest of the three.
        if (kv.Key == "DANCER")
          for (int nr = row - 1; nr <= row + 1; nr++) if (nr >= 0 && nr < 6 && (nr == row || rows[nr].Count > 0 || s.Vases.Any(v => v / 10 == nr)))
            d = Math.Max(d, LaneRisk(s, rows[nr], nr, live, "BDANCEG", Bot.CellX(col) - 140));
        rz += kv.Value * d;
      }
      double risk = pz * (rz + 0.1 * live.Count), score;
      if (Hunting && feet.Count > 0) {   // Darth's gambit: dig for the squash, away from the football's lane
        int rd = feet.Min(f => Math.Abs(f.Row - row));
        score = -(pp * pSq * 3 + pp * 0.3) + 0.5 * risk - 0.05 * rd;
      } else if (DanceHunt) {   // dig for hypnos (and shooters), away from the dancer group's lanes
        bool near = dancers.Any(z => Math.Abs(z.Row - row) <= 1);
        score = -(pp * pHy * 3 + pp * 0.4) + 0.5 * risk + (near ? 0.3 * pz : 0);
      } else score = risk - 0.6 * pp * plantVal;
      // Never pour zombies into a lane with no shooter: replay 17:34:37 - the dance hunt opened four vases in unarmed
      // lane 3 in 7 s (PAIL + 2 NORM out), lane 3 stayed empty and they walked in after the last vase. Hunts skip the
      // safety waits, so this penalty is what keeps them out of undefended lanes (worse if that lane already has enemies).
      int lsh = rows[row].Count(p => IsShooter(p.Type));
      // (raised 1.2 -> 2.0: 17:42:48 / 17:34:37 still opened the last lane-3 vases with "armed x0" during the dance hunt)
      if (lsh == 0) score += pz * (2.0 + 0.8 * live.Count(z => z.Row == row) + (lane[row] >= 2 ? 1.5 : 0));
      // Busy lane: its shooters are already working on a tough zombie (or losing). Another zombie there now only stacks on
      // them (17:42:48: lane 4 opened 4 vases in 4 s - NORM, JACK, PAIL out against 2 shooters - and broke).
      else if (Busy(row, live, lane)) score += pz * (0.8 + Math.Min(2, lane[row]));
      score -= 0.01 * col;   // tie-break from the right: whatever comes out starts far from the house
      if (score < bestScore) { bestScore = score; best = k; bestRisk = risk; bestPz = pz; bestPp = pp; }
    }
    if (best < 0) return -1;
    double gap = Hunting ? 0.5 : DanceHunt ? 0.6 : desperate ? 0.6 : threat ? 1.2 : 0.8;   // owner: "it should always be doing an action" (was 2.5 / 1.8 s of idle)   // 1.0 flooded round 2 (10 vases in 9 s -> a football out before any defence)
    if (!Hunting && !threat && bestRisk < 0.15) gap = Math.Min(gap, 0.4);   // computed low risk: no reason to wait
    // Squash clock: a squash in hand lives ~15.7 game-s and a football can still come. Every vase opened while it lives is a
    // chance to meet the football WITH the squash; at the old 3.5 s pace the four round-2 squashes all expired before it came
    // (17:05:45: football from the 19th vase at 136 s, last squash gone at ~115 s). Open vases quickly while it lasts.
    bool squashClock = held > 0 && feet.Count == 0 && e.RemFoot >= 0.5;
    if (squashClock) gap = Math.Min(gap, threat ? 1.4 : 0.9);
    if (now - lastBreakNow < gap) { nextEval = now + 0.1; return -1; }
    // Final round endgame pacing: nothing new can come after the last vase and the last-vase wait is off, so do not release
    // a likely zombie into a lane that is unarmed or already busy - give its shooters up to 5 game-s to clear it first (the
    // dance hunt skips the other waits; backups keep healing, so this wait is capped, never a stall).
    {
      int fr = best / 10;
      bool unarmed = !rows[fr].Any(p => IsShooter(p.Type));
      if (FinalRound && !Hunting && !desperate && bestPz > 0.3 && (unarmed || Busy(fr, live, lane)) && s.GameTime - lastClickGT >= 0 && s.GameTime - lastClickGT < (DanceHunt ? 3.0 : 5.0)) {
        why = "wait: final round - best vase r" + fr + "c" + best % 10 + " P(zombie)=" + F(bestPz) + " goes into lane " + fr + (unarmed ? " (no shooter)" : " (busy, risk " + F(lane[fr]) + ")") + " - pacing";
        nextEval = now + 0.25; return -1;
      }
    }
    if (!Hunting && !desperate && !DanceHunt) {
      if (live.Count >= 4 && bestPz > 0.35) { why = "wait: " + live.Count + " zombies out, best vase still P(zombie)=" + F(bestPz) + " - letting the shooters thin them"; nextEval = now + 0.25; return -1; }
      if (live.Count > 0 && bestRisk > 2.5) { why = "wait: every vase too risky now (best risk " + F(bestRisk) + ")"; nextEval = now + 0.25; return -1; }
    }
    int br = best / 10, bc = best % 10, armed = rows[br].Count(p => p.Col < bc && IsShooter(p.Type)), res = SquashReserve(s);
    why = "r" + br + "c" + bc + " " + LookName(LookIdx(s, best)) + " : P(zombie)=" + F(bestPz) + " P(plant)=" + F(bestPp) + ", lane " + br + " armed x" + armed
        + " (risk " + F(bestRisk) + ", lane now " + F(lane[br]) + "), FOOT remaining~" + e.RemFoot.ToString("0.0", Inv) + ", squash " + held + "/reserve " + res
        + (Hunting ? " [HUNT: P(squash|plant)=" + F(pSq) + "]" : DanceHunt ? " [DANCE HUNT: " + dancers.Count + " dancers out, P(hypno|plant)=" + F(pHy) + "]" : desperate ? " [desperate]" : threat ? " [threat]" : "") + (squashClock ? " [squash clock]" : "") + (FinalRound ? " [final round]" : "");
    lastBreakNow = now; lastClickGT = s.GameTime;
    return best;
  }

  // A lane whose shooters are busy: a tough enemy (bucket, jack, dancer, backup, football...) walking in it, or it is losing.
  static bool Busy(int row, List<Zom> live, double[] lane) {
    if (row < 0 || row > 5) return false;
    if (lane[row] > 0.8) return true;
    return live.Any(z => {
      if (z.Row != row || z.X >= 720) return false;
      string n = Mem.ZomName(z.Type);
      return n == "PAIL" || n == "JACK" || n == "DANCER" || n == "BDANCE" || n == "FOOT" || n == "CONE" || n == "DOOR" || Seen.Hp(z) >= 900;
    });
  }

  // ---------- Squash / hypno advice ----------
  // Should a squash go on z now? Footballs always (once they stop to eat - a squash misses a fast walker), other tough ones too;
  // normal / pail only when the packet is fading, or no football can still come and the lane is in trouble.
  public static bool SpendSquashOn(State s, Zom z, bool fading) {
    if (z == null || z.Charmed) return false;
    string n = Mem.ZomName(z.Type);
    bool fast = Speed(n) >= 25 || n == "REGEN";
    if (n == "FOOT") return z.Eating || fading || z.X < 160;   // near the house nothing is lost by trying
    if (Tough.Contains(n) || Seen.Hp(z) >= 1700) return !fast || z.Eating || fading || z.X < 160;
    var live = Live(s); var rows = ByRow(s);
    double danger = z.Row >= 0 && z.Row < 6 ? LaneRisk(s, rows[z.Row], z.Row, live, null, 0) : 0;
    // A fading squash on a zombie the lane already handles is wasted (17:05:26: squash on a 190 hp NORM facing a pea + snow).
    // Then the caller parks it in front of the vases instead (AmbushCell), or falls back to any zombie if there is no spot.
    if (fading) return danger >= 0.5 || Seen.Hp(z) >= 1000;
    if (!active && s.Vases.Count == 0) return danger > 1 || Seen.Hp(z) >= 1000;   // not a vase round: no reserve to keep
    int reserve = SquashReserve(s), held = SquashHeld(s);
    if (reserve == 0) return danger >= 0.7 || Seen.Hp(z) >= 1000;   // no football can come: squashes are free
    return danger >= 1 && held > reserve;                         // only a spare beyond the football reserve
  }

  // Where to park a squash that is about to fade: just left of the lane whose unopened vases most likely release a dangerous
  // zombie (football-weighted). Never while only the round's last vases remain - the round wipe takes the parked squash.
  public static int AmbushCell(State s) {
    AmbushWhy = "";
    if (s.Vases.Count == 0 || (!FinalRound && s.Vases.Count <= 2)) return -1;
    var e = Estimate(s); var zf = Frac(e.RemZ, "NORM"); double pf; zf.TryGetValue("FOOT", out pf);
    var live = Live(s);
    int bestCell = -1; double bestW = 0.25;
    for (int row = 0; row < 6; row++) {
      int rr = row;
      var vs = s.Vases.Where(k => k / 10 == rr).ToList();
      if (vs.Count == 0 || s.Plants.Any(q => q.Row == rr && q.Type == SQUASH)) continue;
      int c = vs.Min(k => k % 10) - 1;
      while (c >= 0 && (s.Blocked.Contains(row * 10 + c) || s.Plants.Any(q => q.Row == rr && q.Col == c))) c--;
      if (c < 0) continue;
      int cc = c;
      if (live.Any(z => z.Row == rr && z.X + 30 < Bot.CellX(cc))) continue;   // something already walked past the spot
      double w = vs.Sum(k => e.Pz(LookIdx(s, k))) * (1 + 3 * pf);
      if (w > bestW) { bestW = w; bestCell = row * 10 + c; }
    }
    if (bestCell >= 0) AmbushWhy = "lane " + bestCell / 10 + ": expected zombies in its vases x danger " + F(bestW) + ", P(FOOT|zombie)=" + F(pf);
    return bestCell;
  }

  // Hypno target: the dancer, then its backup dancers, then tough ones. Never a charmed zombie, never one with a hypno already in its path.
  public static Zom HypnoTarget(State s) { return HypnoTargets(s).FirstOrDefault(); }
  public static List<Zom> HypnoTargets(State s) {
    Func<string, int> pri = nm => nm == "DANCER" ? 4 : nm == "BDANCE" ? 3 : (nm == "PAIL" || nm == "FOOT" || nm == "REGEN" || nm == "GARG") ? 2 : 0;
    var rows = ByRow(s); var live = Live(s);
    // Dancers are worth chasing up to the vase columns (a dancer from a col-8 vase starts at ~x 690).
    return s.Zombies.Where(z => z.X < (pri(Mem.ZomName(z.Type)) >= 3 ? 740 : 650) && !z.Charmed && pri(Mem.ZomName(z.Type)) > 0
                              && !s.Plants.Any(q => q.Row == z.Row && q.Type == HYPNO && Bot.CellX(q.Col) < z.X + 30)
                              // Already being finished by one of OUR charmed zombies (replay 17:42:48: the last hypno went on a 271 hp
                              // dancer locked in a fight with our charmed backup - it was dead 3 s later; the PAIL in unarmed lane 3 then walked in).
                              && !(Seen.Hp(z) < 500 && s.Zombies.Any(o => o.Charmed && o.Row == z.Row && Math.Abs(o.X - z.X) < 90))
                              // A bucket the lane's shooters already beat is not worth a hypno while vases (the dancer!) can still come:
                              // hypnos are the only answer to backups (owner), so keep it for those.
                              && !(Mem.ZomName(z.Type) == "PAIL" && s.Vases.Count > 0 && z.Row >= 0 && z.Row < 6 && LaneRisk(s, rows[z.Row], z.Row, live, null, 0) < 0.7))
                    .OrderByDescending(z => pri(Mem.ZomName(z.Type))).ThenBy(z => z.X).ToList();
  }
  // A hypno packet with no target yet: hold it while it lives (a dancer / pail may come out of the next vases and the main hypno
  // rule then puts it exactly in that zombie's path), and park it only in its last ~4 game-s. Parking at once (the old rule)
  // fed hypnos to whatever came next - 17:44:47: three hypnos stacked in lane 0, one charmed a NORM, and the dancer from the
  // last vase (lane 2) met none.
  public const double HYPNO_PARK_LEFT = 4.0;
  public static bool ParkHypnoNow(State s, uint coinId) {
    double left = PacketLeft(s, coinId);
    return left < 0 || left <= HYPNO_PARK_LEFT || s.Vases.Count <= 1;
  }
  // Where to park a hypno that would otherwise fade. One per lane (stacking a 2nd/3rd in one lane wasted them, 17:44:47), in the
  // lane whose unopened vases most likely release a tough zombie (dancer x2, pail, jack), preferring lanes with shooters, and
  // just in front of those shooters with a free column before the nearest vase: weak NORMs die to the peas on the way, tough
  // ones arrive and eat it (17:44:47: a hypno right next to a vase at r3c4 charmed the 270 hp NORM that came out of it).
  public static string HypnoParkWhy = "";
  public static int HypnoParkCell(State s, Func<int, int, bool> free) {
    HypnoParkWhy = "";
    if (s.Vases.Count == 0) return -1;
    var e = Estimate(s); var zf = Frac(e.RemZ, "NORM"); var live = Live(s); var rows = ByRow(s);
    double pT = 0, v;
    if (zf.TryGetValue("DANCER", out v)) pT += 2 * v;
    foreach (var nm in new[] { "PAIL", "JACK", "FOOT", "CONE", "DOOR" }) if (zf.TryGetValue(nm, out v)) pT += v;
    int bestCell = -1; double bestW = 0;
    for (int row = 0; row < 6; row++) {
      int rr = row;
      var vs = s.Vases.Where(k => k / 10 == rr).ToList();
      if (vs.Count == 0) continue;
      int minV = vs.Min(k => k % 10);
      if (minV < 2) continue;
      int front = rows[row].Where(p => IsShooter(p.Type)).Select(p => p.Col).DefaultIfEmpty(-1).Max();
      int nShoot = rows[row].Count(p => IsShooter(p.Type));
      int hyp = rows[row].Count(p => p.Type == HYPNO);
      int lo = Math.Max(1, front + 1), hi = minV - 1;
      if (lo > hi) continue;
      int target = Math.Max(lo, Math.Min(Math.Min(3, minV - 2), hi));
      var order = new List<int>();
      for (int c = target; c >= lo; c--) order.Add(c);
      for (int c = target + 1; c <= hi; c++) order.Add(c);
      int cell = -1;
      foreach (int c in order) if (free(row, c)) { cell = c; break; }
      if (cell < 0) continue;
      int cc = cell;
      if (live.Any(z => z.Row == rr && z.X + 30 < Bot.CellX(cc))) continue;   // something already walked past the spot
      double w = vs.Sum(k => e.Pz(LookIdx(s, k))) * (0.3 + pT) * (nShoot >= 1 ? 1.0 : 0.6) / (1 + 3 * hyp);
      if (live.Any(z => z.Row == rr && z.X + 30 >= Bot.CellX(cc))) w *= 0.5;   // a walker already on its way would get it first
      if (w > bestW) { bestW = w; bestCell = row * 10 + cell; }
    }
    if (bestCell >= 0) HypnoParkWhy = "lane " + bestCell / 10 + " (tough-zombie weight " + F(bestW) + ", in front of its shooters, one hypno per lane)";
    return bestCell;
  }
  // Where a hypno must go so that TARGET z is the first enemy to bite it. A dancer always has a backup ~100 px in front of it
  // in its own lane, so the old "one column ahead" spot fed the hypno to that backup (17:13:55: hypno r4c4 for the dancer at
  // x 466 went to the backup at 366; 17:16:02: parked r0c3, dancer stopped at 365, backup at 265 took it). The cells that
  // worked were the dancer's own (17:23:09 r3c5 vs x 438, 17:18:31 r4c3 vs x 283): measured, a zombie at x bites a plant in
  // the column whose left edge L = 40 + 80c lies about in (x - 40, x + 15). So: the column under the target, or the next one
  // ahead - but never where another enemy in the lane would reach it first. free(row, col) = may plant there. -1: no spot.
  public static int HypnoCell(State s, Zom z, Func<int, int, bool> free) {
    if (z == null) return -1;
    int c0 = (int)Math.Round((z.X - 50) / 80.0);
    for (int c = Math.Min(8, c0); c >= Math.Max(0, c0 - 2); c--) {
      double L = 40 + 80 * c;
      if (L > z.X + 20) continue;   // already behind the target (17:42:48: L 360 vs x 336-339 = +21..24 -> MISS)
      bool first = !s.Zombies.Any(o => o.Id != z.Id && !o.Charmed && o.Row == z.Row && o.X < z.X && o.X > L - 45);
      if (!first) continue;
      if (free(z.Row, c)) return z.Row * 10 + c;
    }
    return -1;
  }

  // Panel (guess.cs, main thread): the planner's odds for each unopened vase, cell -> {P(zombie), P(plant), look 0 ? / 1 leaf / 2 skull},
  // plus key -1 -> {zombies left, plants left, footballs left, squashes left} (notebook estimate). Read-only: leaves the
  // planner's Estimate cache exactly as it found it.
  public static Dictionary<int, double[]> VaseOdds(State s) {
    var d = new Dictionary<int, double[]>();
    if (s == null || s.Vases.Count == 0) return d;
    State f0 = estFor; Est e0 = est;
    try {
      var e = Estimate(s);
      foreach (int k in s.Vases) { int l = LookIdx(s, k); d[k] = new double[] { e.Pz(l), e.Pp(l), l }; }
      d[-1] = new double[] { e.RemZt, e.RemSt, e.RemFoot, e.RemSquash };
    } finally { estFor = f0; est = e0; }
    return d;
  }
}
