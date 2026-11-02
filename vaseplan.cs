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
      case "FOOT": return 1900; case "JACK": return 900; default: return 800;
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
    var xs = new float[20]; var hp = new double[20]; var sp = new double[20]; int n = 0;
    foreach (var z in live) if (z.Row == row && n < 19) { xs[n] = z.X; hp[n] = Seen.Hp(z); sp[n] = Speed(Mem.ZomName(z.Type)); n++; }
    if (extra != null) { xs[n] = extraX; hp[n] = Hp(extra); sp[n] = Speed(extra); n++; }
    if (n == 0) return 0;
    for (int i = 1; i < n; i++) for (int j = i; j > 0 && xs[j] < xs[j - 1]; j--) {   // nearest the house first
      float tx = xs[j]; xs[j] = xs[j - 1]; xs[j - 1] = tx; double th = hp[j]; hp[j] = hp[j - 1]; hp[j - 1] = th; th = sp[j]; sp[j] = sp[j - 1]; sp[j - 1] = th;
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
      t += hp[i] / dps;
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
  // A dropped packet about to fade: learned lifetime when known, else the coin's own disappear counter
  // (vanilla: 1500 ticks; the blink a player sees starts in the last ~3 s).
  public static bool Fading(State s, uint coinId) {
    double left = Learn.PacketTimeLeft(coinId);
    if (left >= 0) return left < 3;
    var c = s.Suns.FirstOrDefault(x => x.Id == coinId);
    return c != null && c.Disappear >= 1200;
  }

  // ---------- Observe: what broke, what came out ----------
  public static void Observe(State s, double now) {
    if (!s.HasBoard) return;
    if (!loaded) Load();
    if (s.Level != lvl) { if (active) EndRound("left"); else ResetAttempt(); lvl = s.Level; BuildPriors(); }
    if (lastGT >= 0 && s.GameTime + 1 < lastGT) { if (active) EndRound("died"); else ResetAttempt(); }   // the clock restarted: a new attempt
    lastGT = s.GameTime;
    if (s.Vases.Count == 0 && !active && Round == 0) { prevVases = null; status = ""; Hunting = false; return; }   // not a vase level (yet)
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
    if (Round == 0) { status = ""; return; }
    int res = SquashReserve(s), held = SquashHeld(s);
    status = "VASE R" + Round + (FinalRound ? " (final)" : "") + ": " + (s.Vases.Count == 0 ? "between rounds" : s.Vases.Count + " left (?" + e.Q + " leaf" + e.Leaf + " skull" + e.Skull + ")")
      + ", FOOT left~" + e.RemFoot.ToString("0.0", Inv) + ", squash " + held + "/reserve " + res + ", P(z|?)=" + F(e.PzQ) + (Hunting ? ", HUNT" : "");
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
    clearT = -1; lastClickGT = -99; prevVases = null; Hunting = false; status = ""; estFor = null;
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
  public static int ChooseBreak(State s, double now, out string why) {
    why = "";
    if (s.Vases.Count == 0 || now - lastBreakNow < 0.5 || now < nextEval) return -1;
    var e = Estimate(s); var live = Live(s); var rows = ByRow(s);
    int held = SquashHeld(s);
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
    if (weaponOnLawn && !desperate && !Hunting) { why = "wait: a dropped plant is still on the lawn"; nextEval = now + 0.25; return -1; }
    // Owner's rule: a zombie walking down a completely empty lane cannot be stopped anyway - then the last vase may as well go.
    bool hopeless = live.Any(z => !s.Plants.Any(p => p.Row == z.Row));
    if (s.Vases.Count == 1 && !FinalRound && live.Count > 0 && !hopeless) { why = "wait: last vase of the round - clearing the lawn first (breaking it wipes our plants)"; nextEval = now + 0.25; return -1; }

    var zf = Frac(e.RemZ, "NORM"); var sf = Frac(e.RemS, "PEA");
    double pSq; sf.TryGetValue("SQUASH", out pSq);
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
        if (kv.Key == "FOOT" && held > 0) d *= 0.5;   // a squash in hand answers one football once it stops to eat
        rz += kv.Value * d;
      }
      double risk = pz * (rz + 0.1 * live.Count), score;
      if (Hunting && feet.Count > 0) {   // Darth's gambit: dig for the squash, away from the football's lane
        int rd = feet.Min(f => Math.Abs(f.Row - row));
        score = -(pp * pSq * 3 + pp * 0.3) + 0.5 * risk - 0.05 * rd;
      } else score = risk - 0.6 * pp * plantVal;
      score -= 0.01 * col;   // tie-break from the right: whatever comes out starts far from the house
      if (score < bestScore) { bestScore = score; best = k; bestRisk = risk; bestPz = pz; bestPp = pp; }
    }
    if (best < 0) return -1;
    double gap = Hunting ? 0.5 : desperate ? 0.8 : threat ? 2.5 : 1.8;   // 1.0 flooded round 2 (10 vases in 9 s -> a football out before any defence)
    if (!Hunting && !threat && bestRisk < 0.15) gap = Math.Min(gap, 0.6);   // computed low risk: no reason to wait
    if (now - lastBreakNow < gap) { nextEval = now + 0.1; return -1; }
    if (!Hunting && !desperate) {
      if (live.Count >= 3 && bestPz > 0.2) { why = "wait: " + live.Count + " zombies out, best vase still P(zombie)=" + F(bestPz) + " - letting the shooters thin them"; nextEval = now + 0.25; return -1; }
      if (live.Count > 0 && bestRisk > 1.5) { why = "wait: every vase too risky now (best risk " + F(bestRisk) + ")"; nextEval = now + 0.25; return -1; }
    }
    int br = best / 10, bc = best % 10, armed = rows[br].Count(p => p.Col < bc && IsShooter(p.Type)), res = SquashReserve(s);
    why = "r" + br + "c" + bc + " " + LookName(LookIdx(s, best)) + " : P(zombie)=" + F(bestPz) + " P(plant)=" + F(bestPp) + ", lane " + br + " armed x" + armed
        + " (risk " + F(bestRisk) + ", lane now " + F(lane[br]) + "), FOOT remaining~" + e.RemFoot.ToString("0.0", Inv) + ", squash " + held + "/reserve " + res
        + (Hunting ? " [HUNT: P(squash|plant)=" + F(pSq) + "]" : desperate ? " [desperate]" : threat ? " [threat]" : "") + (FinalRound ? " [final round]" : "");
    lastBreakNow = now; lastClickGT = s.GameTime;
    return best;
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
    if (fading) return true;
    var live = Live(s); var rows = ByRow(s);
    double danger = z.Row >= 0 && z.Row < 6 ? LaneRisk(s, rows[z.Row], z.Row, live, null, 0) : 0;
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
  public static Zom HypnoTarget(State s) {
    Func<string, int> pri = nm => nm == "DANCER" ? 4 : nm == "BDANCE" ? 3 : (nm == "PAIL" || nm == "FOOT" || nm == "REGEN" || nm == "GARG") ? 2 : 0;
    return s.Zombies.Where(z => z.X < 650 && !z.Charmed && pri(Mem.ZomName(z.Type)) > 0
                              && !s.Plants.Any(q => q.Row == z.Row && q.Type == HYPNO && Bot.CellX(q.Col) < z.X + 30))
                    .OrderByDescending(z => pri(Mem.ZomName(z.Type))).ThenBy(z => z.X).FirstOrDefault();
  }
}
