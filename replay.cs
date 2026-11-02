// replay.cs - death replay ring: the last ~100 GAME seconds of what was on screen plus what the bot did,
// dumped to C:\cbot\replays on death / win / hot reload. Measure first, tune second.
// Main thread only (called from Track / Run), so no locks. Nothing here reads game memory.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

public static class Replay {
  public const string Dir = @"C:\cbot\replays";
  const int N = 250;          // ring size: 250 x 0.4 s = 100 game seconds (plus the action notes in between)
  const double GAP = 0.4;     // snapshot spacing in game seconds (speed flips x2/x5, so wall time would lie)
  const int KEEP = 300;       // dump files kept on disk
  static readonly CultureInfo CI = CultureInfo.InvariantCulture;
  static readonly string[] ring = new string[N]; static int head, count;
  static double lastSnap = -1, lastT = -1, startT = -1;
  static int lastLevel = -1, maxRound = 1, lastRound = -1, lastVases = -1;
  static readonly int[] lastMow = { -9, -9, -9, -9, -9, -9 };
  static string killer = "-";   // leftmost enemy in the newest snapshot: the one walking into the house
  static DateTime lastDump = DateTime.MinValue;
  static readonly List<int> vbuf = new List<int>();

  public static int Count { get { return count; } }
  public static int Level { get { return lastLevel; } }

  static string F1(double v) { return v.ToString("0.0", CI); }
  static void Push(string line) { ring[head] = line; head = (head + 1) % N; if (count < N) count++; }

  public static void Snapshot(State s, int vaseRound) { Snapshot(s, vaseRound, null); }
  // danger: Bot.LaneDanger (optional). Cheap checks every tick; one line built at most every 0.4 game s (~0.05 ms).
  public static void Snapshot(State s, int vaseRound, double[] danger) {
    if (s == null || !s.HasBoard) return;
    double t = s.GameTime;
    if (s.Level != lastLevel || t < lastT - 1) {   // new level or Try Again (mMainCounter restarts)
      if (lastLevel != -1) Push("N t=" + F1(t) + " LEVEL start lvl " + s.Level);
      lastLevel = s.Level; startT = t; lastSnap = -1; maxRound = vaseRound; lastRound = vaseRound; lastVases = -1;
      for (int r = 0; r < 6; r++) lastMow[r] = -9;
    }
    if (startT < 0) startT = t;
    lastT = t;
    if (lastRound != -1 && vaseRound != lastRound) Note("ROUND", lastRound + " -> " + vaseRound, t);
    lastRound = vaseRound; if (vaseRound > maxRound) maxRound = vaseRound;
    int vc = s.Vases.Count;
    if (lastVases >= 0 && vc > lastVases) Note("VASES", lastVases + " -> " + vc + " (new round laid out)", t);
    lastVases = vc;
    for (int r = 0; r < 6; r++) {
      if (lastMow[r] == 1 && s.Mowers[r] == 2) Note("MOWER", "lane " + r + " fired", t);
      lastMow[r] = s.Mowers[r];
    }
    if (lastSnap >= 0 && t - lastSnap < GAP) return;
    lastSnap = t;

    var sb = new StringBuilder(512);
    sb.Append("S t=").Append(F1(t)).Append(" sun=").Append(s.Sun).Append(" w=").Append(s.Wave).Append('/').Append(s.Waves)
      .Append(" vr=").Append(vaseRound).Append(" mow=");
    for (int r = 0; r < 6; r++) { int m = s.Mowers[r]; sb.Append(m < 0 || m > 9 ? '-' : (char)('0' + m)); }
    if (danger != null) {
      sb.Append(" dz=");
      for (int i = 0; i < danger.Length; i++) { if (i > 0) sb.Append(','); sb.Append(danger[i].ToString("0.#", CI)); }
    }
    sb.Append(" | V:");
    vbuf.Clear(); vbuf.AddRange(s.Vases); vbuf.Sort();
    foreach (int c in vbuf) sb.Append(' ').Append(c.ToString("00")).Append(s.LeafVases.Contains(c) ? 'g' : s.SkullVases.Contains(c) ? 's' : '?');
    sb.Append(" | Z:");
    float lx = 9999; string lt = null;
    foreach (var z in s.Zombies) {
      AppendZ(sb, z);
      if (!z.Charmed && z.X < lx) { lx = z.X; lt = Mem.ZomName(z.Type); }
    }
    if (s.FogZombies.Count > 0) { sb.Append(" | F:"); foreach (var z in s.FogZombies) AppendZ(sb, z); }   // hidden in fog: shown for the record only
    if (lt != null && lx < 800) killer = lt;
    sb.Append(" | P:");
    foreach (var p in s.Plants) sb.Append(' ').Append(Mem.SeedName(p.Type)).Append(',').Append(p.Row).Append(',').Append(p.Col).Append(',').Append(p.Hp);
    sb.Append(" | K:");
    foreach (var c in s.Suns) if (c.IsPacket)
      sb.Append(' ').Append(Mem.SeedName(c.Seed)).Append(',').Append(c.X).Append(',').Append(c.Y).Append(',').Append(c.Age).Append('/').Append(c.Disappear);
    sb.Append(" | B:");
    bool any = false;
    foreach (var p in s.Packets) if (p.Ready) { sb.Append(any ? "," : " ").Append(p.Slot); any = true; }
    if (!any) sb.Append(" -");
    Push(sb.ToString());
  }
  static void AppendZ(StringBuilder sb, Zom z) {
    sb.Append(' ').Append(z.Id.ToString("X")).Append(',').Append(Mem.ZomName(z.Type)).Append(',').Append(z.Row).Append(',')
      .Append((int)z.X).Append(',').Append(z.Total);
    if (z.Eating) sb.Append(",E");
    if (z.Charmed) sb.Append(",C");
  }

  // Action / event line into the same ring (PLANT, VASE, OUTCOME, DAVE, ...). gameTime < 0 = time of the last snapshot call.
  public static void Note(string kind, string text, double gameTime) {
    if (gameTime < 0) gameTime = lastT;
    if (text == null) text = "";
    if (text.Length > 300) text = text.Substring(0, 300);
    Push("N t=" + F1(gameTime) + " " + kind + " " + text.Replace('\r', ' ').Replace('\n', ' '));
  }

  // Writes the ring (oldest first) to replays\<stamp>_lvl<L>_<result>.txt, appends a line to index.txt, prunes to 300 files,
  // then empties the ring for the next attempt. Returns the path, or null when skipped (within 3 s of the last dump, or empty).
  // level < 0 = the level of the last snapshot. extra may carry "killer=TYPE" to override the guess.
  public static string Dump(string result, int level, string extra) {
    Outcomes.Flush();
    if ((DateTime.Now - lastDump).TotalSeconds < 3 || count == 0) return null;
    lastDump = DateTime.Now;
    if (level < 0) level = lastLevel;
    string res = Clean(string.IsNullOrEmpty(result) ? "END" : result.ToUpperInvariant());
    if (extra == null) extra = "";
    string kil = res == "DIED" ? killer : "-";
    int ki = extra.IndexOf("killer=");
    if (ki >= 0) { int e = extra.IndexOf(' ', ki); kil = e < 0 ? extra.Substring(ki + 7) : extra.Substring(ki + 7, e - ki - 7); }
    double dur = lastT >= 0 && startT >= 0 ? lastT - startT : 0;
    string path = null;
    try {
      Directory.CreateDirectory(Dir);
      string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
      path = Path.Combine(Dir, stamp + "_lvl" + level + "_" + res + ".txt");
      for (int k = 2; File.Exists(path); k++) path = Path.Combine(Dir, stamp + "_lvl" + level + "_" + res + "_" + k + ".txt");
      var lines = new List<string>(count + 16);
      lines.Add("# cbot replay  lvl " + level + "  " + res + "  vase round " + maxRound + "  killer " + kil + "  span " + F1(dur) + " game s  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
      if (extra.Length > 0) lines.Add("# extra: " + extra);
      lines.Add("# Oldest first. Times are GAME seconds (Board::mMainCounter/100). Cells are row*10+col (row 0 = top, col 0 = left).");
      lines.Add("# S = snapshot (every 0.4 s): sun, w=wave/waves, vr=vase round, mow=mower per row (- none, 0 rolling in, 1 ready, 2 fired, 3 squished),");
      lines.Add("#     dz=lane danger per row (if given)");
      lines.Add("#   V: vases  cell + look (g green leaf = plant inside, s skull = zombie, ? plain; contents are never read)");
      lines.Add("#   Z: zombies  id(hex),TYPE,row,x,total hp(body+helm+shield)[,E eating][,C charmed/hypnotized]   F: same, hidden in fog");
      lines.Add("#   P: plants  SEED,row,col,hp     K: dropped packets  SEED,x,y,age/disappear (ticks of 1/100 s)     B: ready bank slots");
      lines.Add("# N = note: PLANT (score, reason), VASE break (reason), OUTCOME, MOWER, DAVE, ROUND, VASES, LEVEL");
      for (int i = 0; i < count; i++) lines.Add(ring[(head - count + i + N) % N]);
      File.WriteAllLines(path, lines.ToArray());
      File.AppendAllText(Path.Combine(Dir, "index.txt"),
        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " lvl=" + level + " result=" + res + " round=" + maxRound + " killer=" + kil +
        " dur=" + F1(dur) + "s lines=" + count + " file=" + Path.GetFileName(path) + (extra.Length > 0 ? " " + extra.Replace('\n', ' ') : "") + "\r\n");
      Prune();
    } catch (Exception) { }
    // This attempt is on disk: the next one starts clean.
    for (int i = 0; i < N; i++) ring[i] = null;
    head = 0; count = 0; lastSnap = -1;
    return path;
  }

  static string Clean(string s) {
    var sb = new StringBuilder();
    foreach (char c in s) if (char.IsLetterOrDigit(c) || c == '-') sb.Append(c);
    return sb.Length == 0 ? "END" : sb.ToString();
  }
  static void Prune() {
    var files = Directory.GetFiles(Dir, "*_lvl*.txt");
    if (files.Length <= KEEP) return;
    Array.Sort(files, StringComparer.Ordinal);   // names start with yyyyMMdd_HHmmss: name order is age order
    for (int i = 0; i < files.Length - KEEP; i++) { try { File.Delete(files[i]); } catch (Exception) { } }
  }
}
