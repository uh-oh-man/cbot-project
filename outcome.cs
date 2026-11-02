// outcome.cs - did each one-shot plant do its job? Judges SQUASH / CHERRY / JALAPENO / MINE / HYPNO when they vanish,
// and CHOMPER bites, purely from what is on screen (ids, rows, x, hp; never hidden vase contents).
// Main thread only. Results: Take() / Judged event for the integrator, buffered append to C:\cbot\outcomes.txt, Summary() for the panel.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

public static class Outcomes {
  public const string FilePath = @"C:\cbot\outcomes.txt";
  const int CHERRY = 2, MINE = 4, CHOMP = 6, HYPNO = 12, SQUASH = 17, JALA = 20, GARG = 32, BOSS = 34;
  const double WIN = 1.5;      // look-back: a target may die up to 1.5 game s before its plant vanishes (squash fades after landing)
  const double SETTLE = 2.0;   // wait this long after the vanish before judging (dying / charred zombies linger)
  const double CK = 0.5;       // hp checkpoint spacing: 4 checkpoints = the last ~2 s of hp per zombie
  const double CHEW = 15;      // a chomper that just bit is chewing: later vanishes in front of it are someone else's kill
  const double SHOVEL = 3;     // a plant gone within 3 s of our shovel in that cell was dug up, not used
  static readonly CultureInfo CI = CultureInfo.InvariantCulture;

  class ZI {
    public uint Id; public int Type, Row, Total, Hp, SeenF, Hi; public float X; public bool Charmed, Fog, Gone, Claimed;
    public double GoneT, CkT; public int[] H = new int[4], HB = new int[4];
    public int RefTotal() { int m = Total; for (int i = 0; i < 4; i++) if (H[i] > m) m = H[i]; return m; }   // hp before the hit
    public int RefHp() { int m = Hp; for (int i = 0; i < 4; i++) if (HB[i] > m) m = HB[i]; return m; }
  }
  class PI { public uint Id; public int Type, Row, Col, Hp, SeenF; public double FirstT, LastT, HpDropT = -99, LastBiteT = -99; }
  class Pend { public PI P; public double T; public int Round; public List<ZI> Cands = new List<ZI>(); public List<int> Ref = new List<int>(); }
  class CE { public double T; public int Row, Type; public bool Used; }

  static readonly Dictionary<uint, ZI> zs = new Dictionary<uint, ZI>();
  static readonly Dictionary<uint, PI> ps = new Dictionary<uint, PI>();
  static readonly List<Pend> pending = new List<Pend>();
  static readonly List<CE> charms = new List<CE>();
  static readonly Dictionary<int, double> shoveled = new Dictionary<int, double>();
  static Dictionary<uint, int> prevCells = new Dictionary<uint, int>(), curCells = new Dictionary<uint, int>();
  static readonly List<ZI> goneNow = new List<ZI>();
  static readonly List<uint> drop = new List<uint>();
  static readonly List<PI> lostPlants = new List<PI>();
  static int frame, level = -1, lastVases = -1;
  static double lastT = -1, wipeT = -99;
  static bool primed;

  static readonly List<string> fresh = new List<string>();
  public static event Action<string> Judged;   // fired once per judgement with the OUTCOME line
  static readonly StringBuilder buf = new StringBuilder();
  static DateTime lastFlush = DateTime.Now;
  static readonly Dictionary<string, Dictionary<string, int>> counts = new Dictionary<string, Dictionary<string, int>>();
  static readonly Dictionary<string, Dictionary<string, int>> targets = new Dictionary<string, Dictionary<string, int>>();
  static bool loaded;

  static bool Tracked(int t) { return t == SQUASH || t == CHERRY || t == JALA || t == MINE || t == CHOMP || t == HYPNO; }
  static string Name(int t) {
    switch (t) { case SQUASH: return "SQUASH"; case CHERRY: return "CHERRY"; case JALA: return "JALAPENO"; case MINE: return "MINE";
      case CHOMP: return "CHOMPER"; case HYPNO: return "HYPNO"; default: return Mem.SeedName(t); }
  }
  static int CellX(int col) { return 80 + col * 80; }   // same as Bot.CellX
  static double Dx(ZI z, PI p) { return z.X + 40 - CellX(p.Col); }   // zombie centre vs plant cell centre, as bot.cs measures it

  // Our own shovel dug this cell (call next to Bot's shoveled[...] = ...): the vanish that follows is not a use.
  public static void Shoveled(int row, int col) { shoveled[row * 10 + col] = lastT; }
  static bool ShoveledRecently(int cell, double t) { double v; return shoveled.TryGetValue(cell, out v) && t - v < SHOVEL && t >= v - 0.5; }

  // Every playing tick, right after the state read. O(zombies + plants), no allocations beyond new ids.
  public static void Frame(State s, int vaseRound) {
    if (s == null || !s.HasBoard || (s.Scene != 3 && !s.Complete)) return;
    Load();
    double t = s.GameTime;
    if (s.Level != level || t < lastT - 1) Reset(s.Level);   // new level / Try Again: drop everything, judge nothing
    lastT = t; frame++;

    // Vasebreaker round wipe: the board clears all plants and lays out the next round's vases.
    int vc = s.Vases.Count;
    if (lastVases >= 0 && (vc > lastVases || lastVases - vc >= 3)) wipeT = t;
    lastVases = vc;

    // Zombies (fog-hidden ones too, so walking into fog is not a "kill"; bookkeeping only).
    UpdateZ(s.Zombies, false, t); UpdateZ(s.FogZombies, true, t);
    goneNow.Clear(); drop.Clear();
    foreach (var z in zs.Values) {
      if (!z.Gone && z.SeenF != frame) { z.Gone = true; z.GoneT = t; if (primed) goneNow.Add(z); }
      else if (z.Gone && t - z.GoneT > 4) drop.Add(z.Id);
    }
    foreach (var id in drop) zs.Remove(id);
    for (int i = charms.Count - 1; i >= 0; i--) if (t - charms[i].T > 5) charms.RemoveAt(i);

    // Plants: wipe count over all of them, outcome tracking for the one-shots.
    curCells.Clear();
    foreach (var p in s.Plants) {
      curCells[p.Id] = p.Row * 10 + p.Col;
      if (!Tracked(p.Type)) continue;
      PI pi;
      if (!ps.TryGetValue(p.Id, out pi)) { pi = new PI { Id = p.Id, Type = p.Type, FirstT = t, Hp = p.Hp }; ps[p.Id] = pi; }
      else if (p.Hp < pi.Hp) pi.HpDropT = t;   // something is chewing on it
      pi.Hp = p.Hp; pi.Row = p.Row; pi.Col = p.Col; pi.LastT = t; pi.SeenF = frame;
    }
    if (primed) {
      int vanished = 0;
      foreach (var kv in prevCells) if (!curCells.ContainsKey(kv.Key) && !ShoveledRecently(kv.Value, t)) vanished++;
      if (vanished >= 3) wipeT = t;
    }
    var sw = prevCells; prevCells = curCells; curCells = sw;

    // Chomper bites: a healthy zombie vanishing right in front of an idle chomper was swallowed.
    foreach (var z in goneNow) {
      if (z.Charmed || z.Claimed || z.Hp <= 0 || z.Total < 150 || z.Type == GARG || z.Type == BOSS) continue;
      PI best = null; double bd = 999;
      foreach (var pi in ps.Values) {
        if (pi.Type != CHOMP || pi.SeenF != frame || pi.Row != z.Row || t - pi.LastBiteT < CHEW) continue;
        double dx = Dx(z, pi);
        if (dx >= -40 && dx <= 130 && Math.Abs(dx) < bd) { bd = Math.Abs(dx); best = pi; }
      }
      if (best == null) continue;
      best.LastBiteT = t; z.Claimed = true;
      Emit(CHOMP, "BITE", Mem.ZomName(z.Type), best.Row, best.Col, vaseRound, t, " hp=" + z.Total);
    }

    // Tracked plants that vanished: open a judgement with the candidates that were in reach right now.
    lostPlants.Clear();
    foreach (var pi in ps.Values) if (pi.SeenF != frame) lostPlants.Add(pi);
    foreach (var pi in lostPlants) {
      ps.Remove(pi.Id);
      if (!primed || ShoveledRecently(pi.Row * 10 + pi.Col, t)) continue;
      var pd = new Pend { P = pi, T = t, Round = vaseRound };
      foreach (var z in zs.Values) {
        if (z.Charmed || z.Claimed || (z.Gone && z.GoneT < t - WIN) || z.RefHp() <= 0 || !InReach(pi, z)) continue;
        pd.Cands.Add(z); pd.Ref.Add(z.RefTotal());
      }
      pending.Add(pd);
    }

    for (int i = 0; i < pending.Count; i++) if (t >= pending[i].T + SETTLE) { Judge(pending[i]); pending.RemoveAt(i); i--; }
    primed = true;
    if (buf.Length > 0 && (DateTime.Now - lastFlush).TotalSeconds >= 2) Flush();
  }

  static void UpdateZ(List<Zom> list, bool fog, double t) {
    foreach (var z in list) {
      ZI zi; bool isNew = !zs.TryGetValue(z.Id, out zi);
      if (isNew) {
        zi = new ZI { Id = z.Id, CkT = t };
        for (int i = 0; i < 4; i++) { zi.H[i] = z.Total; zi.HB[i] = z.Hp; }
        zs[z.Id] = zi;
      } else if (!zi.Charmed && z.Charmed) charms.Add(new CE { T = t, Row = z.Row, Type = z.Type });   // just hypnotized
      zi.Type = z.Type; zi.Row = z.Row; zi.X = z.X; zi.Total = z.Total; zi.Hp = z.Hp; zi.Charmed = z.Charmed;
      zi.Fog = fog; zi.Gone = false; zi.SeenF = frame;
      if (t - zi.CkT >= CK) { zi.Hi = (zi.Hi + 1) & 3; zi.H[zi.Hi] = z.Total; zi.HB[zi.Hi] = z.Hp; zi.CkT = t; }
    }
  }

  // Where each plant can hurt: squash about a cell either way, cherry 3x3, jalapeno the whole row, mine its own cell.
  static bool InReach(PI p, ZI z) {
    double dx = Dx(z, p);
    switch (p.Type) {
      case SQUASH: return z.Row == p.Row && Math.Abs(dx) <= 120;
      case CHERRY: return Math.Abs(z.Row - p.Row) <= 1 && Math.Abs(dx) <= 130;
      case JALA: return z.Row == p.Row && z.X < 800;
      case MINE: return z.Row == p.Row && Math.Abs(dx) <= 90;
      default: return false;   // hypno is judged by charm events, chomper by bites
    }
  }

  static void Judge(Pend pd) {
    var p = pd.P; double age = pd.T - p.FirstT;
    // Only a wipe AT the vanish: the squash that kills the round's last zombie is followed by a wipe a moment later.
    bool wiped = wipeT >= pd.T - 1.0 && wipeT <= pd.T + 1.0;
    bool eaten = p.HpDropT >= p.LastT - WIN;
    string ageS = " age=" + age.ToString("0.0", CI);
    if (p.Type == HYPNO || p.Type == CHOMP) {
      if (p.Type == HYPNO) {
        foreach (var c in charms) if (!c.Used && c.Row == p.Row && c.T >= pd.T - 1.0 && c.T <= pd.T + SETTLE) {
          c.Used = true; Emit(p.Type, "CHARMED", Mem.ZomName(c.Type), p.Row, p.Col, pd.Round, pd.T, ageS); return;
        }
      }
      Emit(p.Type, wiped ? "WIPED" : eaten ? "EATEN" : p.Type == CHOMP ? "LOST" : "MISS", "-", p.Row, p.Col, pd.Round, pd.T, ageS);
      return;
    }
    ZI kill = null; int killRef = 0, kills = 0; ZI hurt = null; int hurtDrop = 0;
    for (int i = 0; i < pd.Cands.Count; i++) {
      var z = pd.Cands[i]; int rf = pd.Ref[i];
      if (z.Claimed) continue;   // already credited to another plant
      bool dead = (z.Gone && !z.Charmed && z.GoneT >= pd.T - WIN) || (!z.Gone && z.Hp <= 0);
      if (dead) { kills++; if (kill == null || rf > killRef) { kill = z; killRef = rf; } continue; }
      int d = rf - Math.Max(0, z.Total);
      if (d >= 800 && d > hurtDrop) { hurt = z; hurtDrop = d; }
    }
    if (kill != null) {
      for (int i = 0; i < pd.Cands.Count; i++) {
        var z = pd.Cands[i];
        if ((z.Gone && z.GoneT >= pd.T - WIN) || (!z.Gone && z.Hp <= 0)) z.Claimed = true;
      }
      Emit(p.Type, "HIT_KILL", Mem.ZomName(kill.Type), p.Row, p.Col, pd.Round, pd.T, " hp=" + killRef + (kills > 1 ? " kills=" + kills : "") + ageS);
    } else if (wiped) Emit(p.Type, "WIPED", "-", p.Row, p.Col, pd.Round, pd.T, ageS);   // zombies survive a wipe, so a kill above is still real
    else if (hurt != null) Emit(p.Type, "HIT_DMG", Mem.ZomName(hurt.Type), p.Row, p.Col, pd.Round, pd.T, " dmg=" + hurtDrop + ageS);
    else Emit(p.Type, eaten ? "EATEN" : "MISS", "-", p.Row, p.Col, pd.Round, pd.T, ageS);
  }

  static void Emit(int type, string res, string target, int row, int col, int round, double t, string extra) {
    string line = "OUTCOME " + Name(type) + " " + res + " target=" + target + " row " + row + " col " + col + " round " + round +
                  " t=" + t.ToString("0.0", CI) + extra;
    fresh.Add(line);
    if (fresh.Count > 200) fresh.RemoveAt(0);   // nobody draining: keep it bounded
    buf.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" lvl").Append(level).Append(' ').Append(line).Append("\r\n");
    Tally(line);
    var h = Judged; if (h != null) { try { h(line); } catch (Exception) { } }
  }

  // New OUTCOME lines since the last call (each line is returned exactly once).
  public static List<string> Take() { var r = new List<string>(fresh); fresh.Clear(); return r; }

  public static void Flush() {
    lastFlush = DateTime.Now;
    if (buf.Length == 0) return;
    try { File.AppendAllText(FilePath, buf.ToString()); buf.Length = 0; } catch (Exception) { }
  }

  static void Reset(int lvl) {
    zs.Clear(); ps.Clear(); pending.Clear(); charms.Clear(); shoveled.Clear(); prevCells.Clear(); curCells.Clear();
    level = lvl; lastVases = -1; wipeT = -99; primed = false;
  }

  static void Load() {
    if (loaded) return;
    loaded = true;
    try { if (File.Exists(FilePath)) foreach (var l in File.ReadAllLines(FilePath)) Tally(l); } catch (Exception) { }
  }
  static readonly object gate = new object();   // the panel thread reads counts while the main loop adds to them
  static void Tally(string line) { lock (gate) TallyCore(line); }
  static void TallyCore(string line) {
    int i = line.IndexOf("OUTCOME ");
    if (i < 0) return;
    var w = line.Substring(i).Split(' ');
    if (w.Length < 4) return;
    string plant = w[1], res = w[2], tgt = w[3].StartsWith("target=") ? w[3].Substring(7) : "-";
    Dictionary<string, int> d;
    if (!counts.TryGetValue(plant, out d)) { d = new Dictionary<string, int>(); counts[plant] = d; }
    int n; d.TryGetValue(res, out n); d[res] = n + 1;
    if (tgt == "-") return;
    string k = plant + " " + res;
    if (!targets.TryGetValue(k, out d)) { d = new Dictionary<string, int>(); targets[k] = d; }
    d.TryGetValue(tgt, out n); d[tgt] = n + 1;
  }

  // "SQUASH 31 tries: 12 HIT_KILL 39% (FOOT 3, NORM 7) 9 WIPED 29% 5 MISS 16% ..." from outcomes.txt plus this session.
  // plant: SQUASH CHERRY JALAPENO MINE CHOMPER HYPNO (seed names JALA / CHOMP accepted); null or "" = all plants, " | " joined.
  public static string Summary(string plant) { lock (gate) return SummaryCore(plant); }
  static string SummaryCore(string plant) {
    Load();
    if (string.IsNullOrEmpty(plant)) {
      var parts = new List<string>();
      foreach (var k in counts.Keys.OrderBy(x => x)) parts.Add(Summary(k));
      return parts.Count == 0 ? "no outcomes yet" : string.Join(" | ", parts.ToArray());
    }
    plant = plant.ToUpperInvariant();
    if (plant == "JALA") plant = "JALAPENO"; else if (plant == "CHOMP") plant = "CHOMPER"; else if (plant == "POTATO" || plant == "POTATO MINE") plant = "MINE";
    Dictionary<string, int> d;
    if (!counts.TryGetValue(plant, out d) || d.Count == 0) return plant + " 0 tries";
    int total = d.Values.Sum();
    var sb = new StringBuilder(plant + " " + total + " tries:");
    foreach (var kv in d.OrderByDescending(x => x.Value)) {
      sb.Append(' ').Append(kv.Value).Append(' ').Append(kv.Key).Append(' ').Append((100 * kv.Value / total)).Append('%');
      Dictionary<string, int> tg;
      if (targets.TryGetValue(plant + " " + kv.Key, out tg) && tg.Count > 0)
        sb.Append(" (").Append(string.Join(", ", tg.OrderByDescending(x => x.Value).Take(4).Select(x => x.Key + " " + x.Value).ToArray())).Append(')');
    }
    return sb.ToString();
  }
}
