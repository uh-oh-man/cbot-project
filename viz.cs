// Live "bot brain" panel: a non-activating window docked under the game, drawn from the bot's real data.
// Threading: the panel runs on its own WinForms thread. It never calls Mem.* or changes bot state; the main loop hands it
// data through Viz.Publish / SetDecision / AddEvent (under lock L) and the Guess snapshot (guess.cs, built on the main
// thread inside Viz.Publish). The panel may read small files itself (throttled to every 10 s), never the main loop.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

public static class Viz {
  static readonly DateTime Started = DateTime.Now;
  static readonly DateTime ProjectStart = LoadProjectStart();
  static DateTime LoadProjectStart() { try { return DateTime.Parse(System.IO.File.ReadAllText(@"C:\cbot\project_start.txt").Trim()); } catch { return DateTime.Now; } }
  static string Dur(TimeSpan t) { return t.TotalDays >= 1 ? string.Format("{0}d {1}h {2}m", (int)t.TotalDays, t.Hours, t.Minutes) : string.Format("{0}h {1}m", (int)t.TotalHours, t.Minutes); }
  static readonly object L = new object();
  static State state;
  static List<Cand> cands = new List<Cand>();
  static Cand chosen; static string saving;
  static readonly LinkedList<string> events = new LinkedList<string>();
  static readonly Queue<double> reacts = new Queue<double>();
  static readonly List<int> sunHist = new List<int>(), dpsHist = new List<int>();
  static int dmgThisSec; static DateTime secStart = DateTime.Now;
  static int flashRow = -1, flashCol = -1; static DateTime flashAt;
  public static double LoopHz, ReadMs;
  public static int Kills, SunClicks, CoinClicks, Clicks, Planted, Wins, Losses, MowersLost;
  public static string Status = "starting";

  // Main thread, once per tick (bot.cs Track): also runs the guess layer (fog / blackout ghosts, vase odds, lane sim).
  public static void Publish(State s) {
    Guess.Frame(s);   // never throws; < 0.1 ms
    lock (L) state = s;
  }

  // ---------- Faith: how religious the bot gets as it feels closer to death ----------
  public static double Faith;          // 0..100, smoothed
  static int faithTier = 0, worstLane = -1; static double peakFaith;
  static readonly string[] Tiers = {
    "ATHEIST - science and peashooters will save us",
    "AGNOSTIC - probably fine. probably.",
    "lights a candle, just in case",
    "prays to the Sun God for 25 more sun",
    "HAS FOUND RELIGION - lane {0} is in god's hands now",
    "SPEAKING IN TONGUES - promises Dave to stop eating tacos",
    "FULL PROPHET - sees the light. it is a zombie's mouth",
  };
  static readonly double[] TierAt = { 0, 10, 25, 40, 58, 75, 90 };
  public static string FaithText { get { return string.Format(Tiers[faithTier], worstLane); } }
  public static void UpdateFaith(double fear, int lane) {
    double target = Math.Max(0, Math.Min(100, fear * 100));
    Faith += (target - Faith) * (target > Faith ? 0.08 : 0.02);   // panics fast, calms down slowly
    worstLane = lane; peakFaith = Math.Max(peakFaith, Faith);
    int t = 0; for (int i = 0; i < TierAt.Length; i++) if (Faith >= TierAt[i]) t = i;
    if (t > faithTier && t >= 3) Quips.Pray(t, lane, Faith);
    if (t <= 1 && faithTier >= 4) { Quips.Answered(peakFaith); peakFaith = 0; }
    faithTier = t;
  }
  public static void FaithOnDeath() { Quips.Forsaken(worstLane, Faith); Faith = 100; }
  public static void SetDecision(List<Cand> c, Cand ch, string sav) { lock (L) { cands = c; chosen = ch; saving = sav; } }
  public static void AddEvent(string e) {
    string line = DateTime.Now.ToString("HH:mm:ss") + "  " + e;
    lock (L) {
      events.AddFirst(line); while (events.Count > 40) events.RemoveLast();
      MaybeToast(e);
      try { System.IO.File.AppendAllText(@"C:\cbot\events.log", line + Environment.NewLine); } catch { }
    }
  }
  // ---------- toasts: big moments pop on the panel (kills, wins, deaths, mowers, blackouts) ----------
  public class Toast { public string Text; public Color C; public DateTime At; }
  static readonly List<Toast> toasts = new List<Toast>();
  public static int ToastSeq;
  static readonly List<string> plantPops = new List<string>(); public static int PlantSeq;
  static void MaybeToast(string e) {   // called under lock L
    string t = null; Color c = Color.White;
    if (e.StartsWith("RESULT") && e.Contains("WON")) { t = "LEVEL CLEARED!"; c = Color.FromArgb(90, 235, 120); }
    else if (e.StartsWith("RESULT") && e.Contains("DIED")) { t = "DEATH"; c = Color.FromArgb(245, 80, 70); }
    else if (e.StartsWith("OUTCOME") && (e.Contains("HIT_KILL") || e.Contains("CHARMED") || e.Contains("BITE"))) {
      var w = e.Split(' '); string tg = ""; foreach (var p in w) if (p.StartsWith("target=")) tg = p.Substring(7);
      t = (w.Length > 1 ? w[1] : "") + (e.Contains("CHARMED") ? " CHARMED " : " > ") + tg; c = e.Contains("CHARMED") ? Color.FromArgb(200, 150, 255) : Color.FromArgb(255, 210, 60);
    }
    else if (e.Contains("MOWER FIRED")) { t = "MOWER FIRED!"; c = Color.FromArgb(255, 150, 60); }
    else if (e.StartsWith("BLACKOUT")) { t = "BLACKOUT"; c = Color.FromArgb(170, 150, 255); }
    else if (e.StartsWith("light is back")) { t = "LIGHTS ON"; c = Color.FromArgb(255, 240, 160); }
    else if (e.StartsWith("DAVE:") && e.Contains("last of")) { t = "FINAL ROUND"; c = Color.FromArgb(255, 120, 90); }
    if (e.StartsWith("PLANT ")) { var pw = e.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); if (pw.Length > 1 && pw[1] != "POT") { plantPops.Add(pw[1]); if (plantPops.Count > 8) plantPops.RemoveAt(0); PlantSeq++; } }
    if (t == null) return;
    toasts.Add(new Toast { Text = t, C = c, At = DateTime.Now }); if (toasts.Count > 6) toasts.RemoveAt(0); ToastSeq++;
  }
  // Same as AddEvent but at most once per 60 s for the same text prefix.
  static readonly Dictionary<string, DateTime> throttle = new Dictionary<string, DateTime>();
  public static void AddEventThrottled(string e) {
    string key = e.Length > 18 ? e.Substring(0, 18) : e; DateTime last;
    if (throttle.TryGetValue(key, out last) && (DateTime.Now - last).TotalSeconds < 60) return;
    throttle[key] = DateTime.Now; AddEvent(e);
  }
  public static void AddReaction(double ms) { lock (L) { reacts.Enqueue(ms); while (reacts.Count > 30) reacts.Dequeue(); } }
  public static void Flash(int r, int c) { flashRow = r; flashCol = c; flashAt = DateTime.Now; }
  public static void AddDamage(int d) {
    lock (L) {
      dmgThisSec += d;
      if ((DateTime.Now - secStart).TotalSeconds >= 1) {
        dpsHist.Add(dmgThisSec); sunHist.Add(state != null ? state.Sun : 0);
        if (dpsHist.Count > 120) { dpsHist.RemoveAt(0); sunHist.RemoveAt(0); }
        dmgThisSec = 0; secStart = DateTime.Now;
      }
    }
  }

  public static void Start() {
    var t = new Thread(() => { Application.EnableVisualStyles(); Application.Run(new Panel()); });
    t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start();
  }

  // ---------- window ----------
  class Panel : Form {
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Lf, T, R, B; }
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    DateTime lastDock = DateTime.MinValue;
    readonly Font f9 = new Font("Consolas", 9f), f9b = new Font("Consolas", 9f, FontStyle.Bold),
                  f11 = new Font("Consolas", 11f, FontStyle.Bold), f8 = new Font("Consolas", 8f), f8b = new Font("Consolas", 8f, FontStyle.Bold);

    public Panel() {
      Text = "PvZ bot brain"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
      BackColor = Color.FromArgb(14, 18, 24); DoubleBuffered = true; StartPosition = FormStartPosition.Manual;
      Bounds = new Rectangle(0, 700, 1200, 300);
      timer.Interval = 50;    // 20 fps: smooth animations (toasts, shimmer, sparks); painting stays well under 10 ms
      timer.Tick += (a, b) => { try { DockToGame(); } catch { } Invalidate(); };
      timer.Start();
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
      get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x80 /*TOOLWINDOW*/ | 0x8 /*TOPMOST*/; return cp; }
    }
    const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
    static readonly double[] Speeds = { 1, 2, 3, 5, 10 };
    readonly List<Tuple<Rectangle, double>> speedButtons = new List<Tuple<Rectangle, double>>();
    Rectangle truthButton = Rectangle.Empty; bool showTruth;
    protected override void OnMouseDown(MouseEventArgs e0) {
      var e = new MouseEventArgs(e0.Button, e0.Clicks, e0.X, e0.Y - panelOffY, e0.Delta);   // buttons live in the shifted bottom panel
      foreach (var b in speedButtons)
        if (b.Item1.Contains(e.Location)) {
          try { System.IO.File.WriteAllText(@"C:\cbot\speed.txt", b.Item2.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
          AddEvent("speed button pressed: x" + b.Item2);
        }
      if (truthButton.Contains(e.Location)) { showTruth = !showTruth; Invalidate(); }
    }

    protected override void WndProc(ref Message m) {
      if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
      base.WndProc(ref m);
    }

    // Keep the game at the top of the screen and fill the space below it.
    void DockToGame() {
      if ((DateTime.Now - lastDock).TotalSeconds < 2) return;
      lastDock = DateTime.Now;
      var g = System.Diagnostics.Process.GetProcessesByName("LawnProject").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
      if (g == null) return;
      RECT r; GetWindowRect(g.MainWindowHandle, out r);
      var wa = Screen.FromHandle(g.MainWindowHandle).WorkingArea;
      int gw = r.R - r.Lf, gh = r.B - r.T;
      if (wa.Bottom - r.B < 300 && r.T > wa.Top) {   // not enough room below: lift the game up
        int nx = Math.Max(wa.Left, Math.Min(r.Lf, wa.Right - gw));
        SetWindowPos(g.MainWindowHandle, IntPtr.Zero, nx, wa.Top, 0, 0, 0x0001 | 0x0004 | 0x0010);
        GetWindowRect(g.MainWindowHandle, out r);
      }
      // Owner: use the FULL screen. One borderless window over the whole work area, with a hole cut where the game is.
      var want = new Rectangle(wa.Left, wa.Top, wa.Width, wa.Height);
      if (Bounds != want) Bounds = want;
      var hole = new Rectangle(r.Lf - wa.Left, r.T - wa.Top, gw, gh);
      if (hole != gameHole) { gameHole = hole; panelOffY = Math.Max(0, r.B - wa.Top); var rg = new Region(new Rectangle(0, 0, want.Width, want.Height)); rg.Exclude(hole); Region = rg; }
    }

    // ---------- palette (one meaning per colour) ----------
    static readonly Color BgTop = Color.FromArgb(18, 23, 30), BoxBg = Color.FromArgb(22, 28, 36), BoxEdge = Color.FromArgb(44, 54, 66), Track = Color.FromArgb(34, 42, 52);
    static readonly Color Txt = Color.FromArgb(220, 230, 240), Dim = Color.FromArgb(120, 140, 160);
    static readonly Color Good = Color.FromArgb(90, 220, 120), Bad = Color.FromArgb(240, 80, 70), Gold = Color.FromArgb(255, 210, 60),
                          Cyan = Color.FromArgb(90, 210, 230), Orange = Color.FromArgb(255, 160, 90), Blue = Color.FromArgb(140, 200, 255),
                          Purple = Color.FromArgb(200, 150, 255), GuessC = Color.FromArgb(255, 190, 70), SunC = Color.FromArgb(255, 225, 90),
                          VaseLo = Color.FromArgb(60, 170, 160), VaseHi = Color.FromArgb(230, 70, 60), FogC = Color.FromArgb(170, 170, 210);

    // Distinct colour per plant kind, so the minimap reads at a glance.
    static Color PlantColor(int t) {
      switch (t) {
        case 0: return Color.FromArgb(80, 200, 80);     // peashooter: green
        case 1: return Color.FromArgb(255, 205, 40);    // sunflower: yellow
        case 2: return Color.FromArgb(220, 40, 50);     // cherry: red
        case 3: return Color.FromArgb(170, 110, 50);    // wall-nut: brown
        case 4: return Color.FromArgb(200, 160, 110);   // potato mine: tan
        case 5: return Color.FromArgb(90, 190, 255);    // snow pea: ice blue
        case 6: return Color.FromArgb(170, 70, 200);    // chomper: purple
        case 7: return Color.FromArgb(30, 140, 60);     // repeater: dark green
        default:
          var r = new Random(t * 7919);                  // stable random colour for anything else
          return Color.FromArgb(80 + r.Next(170), 80 + r.Next(170), 80 + r.Next(170));
      }
    }
    static Color Lerp(Color a, Color b, double t) {
      t = Math.Max(0, Math.Min(1, t));
      return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
    static Color A(int alpha, Color c) { return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c); }
    static readonly Color VaseMid = Color.FromArgb(220, 160, 50);
    static Color Heat(double p) { return p < 0.5 ? Lerp(VaseLo, VaseMid, p * 2) : Lerp(VaseMid, VaseHi, (p - 0.5) * 2); }   // P(zombie): teal - amber - red

    // ---------- GDI caches (panel thread only): no brush/pen churn at 10 fps ----------
    readonly Dictionary<int, SolidBrush> brushes = new Dictionary<int, SolidBrush>();
    readonly Dictionary<long, Pen> pens = new Dictionary<long, Pen>();
    SolidBrush B(Color c) { SolidBrush b; int k = c.ToArgb(); if (!brushes.TryGetValue(k, out b)) { if (brushes.Count > 400) brushes.Clear(); b = new SolidBrush(c); brushes[k] = b; } return b; }
    Pen P(Color c, float w) { return P(c, w, false); }
    Pen P(Color c, float w, bool dash) {
      long k = ((long)(uint)c.ToArgb() << 16) | ((long)(w * 10) << 1) | (dash ? 1L : 0L); Pen p;
      if (!pens.TryGetValue(k, out p)) { if (pens.Count > 400) pens.Clear(); p = new Pen(c, w); if (dash) p.DashStyle = DashStyle.Dash; pens[k] = p; }
      return p;
    }
    readonly Dictionary<Font, float> charW = new Dictionary<Font, float>();
    float CW(Graphics g, Font f) {   // Consolas is monospaced: one width per font
      float w; if (charW.TryGetValue(f, out w)) return w;
      w = (g.MeasureString(new string('M', 40), f).Width - g.MeasureString(new string('M', 20), f).Width) / 20f;
      if (w <= 0) w = f.Size; charW[f] = w; return w;
    }
    // Draw text cut to fit maxW pixels (ending in "~" when cut).
    float T(Graphics g, string s, Font f, Color c, float x, float y, float maxW) {
      if (string.IsNullOrEmpty(s) || maxW < 6) return x;
      int n = Math.Max(1, (int)((maxW - 2) / CW(g, f)));
      string d = Fit(s, n); g.DrawString(d, f, B(c), x, y);
      return x + d.Length * CW(g, f) + 4;
    }
    // Word-wrap to at most maxLines lines of maxChars (the last one cut with "~").
    static List<string> Wrap(string s, int maxChars, int maxLines) {
      var lines = new List<string>(); if (string.IsNullOrEmpty(s) || maxChars < 4) return lines;
      string rest = s;
      while (rest.Length > 0 && lines.Count < maxLines) {
        if (rest.Length <= maxChars) { lines.Add(rest); rest = ""; break; }
        if (lines.Count == maxLines - 1) { lines.Add(Fit(rest, maxChars)); rest = ""; break; }
        int cut = rest.LastIndexOf(' ', maxChars); if (cut < maxChars / 2) cut = maxChars;
        lines.Add(rest.Substring(0, cut).TrimEnd()); rest = rest.Substring(cut).TrimStart();
      }
      return lines;
    }
    static string Fit(string s, int max) { return s == null ? "" : s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 1)) + "~"; }
    static string Pct(double p) { return p < 0 ? "?" : (100 * p).ToString("0") + "%"; }
    static string LevelName(int id) { return id <= 0 ? "?" : ((id - 1) / 10 + 1) + "-" + ((id - 1) % 10 + 1); }
    static string AreaName(int bg) { switch (bg) { case 0: return "DAY"; case 1: return "NIGHT"; case 2: return "POOL"; case 3: return "FOG"; case 4: return "ROOF"; case 5: return "ROOF NIGHT"; default: return "AREA " + bg; } }
    static string Ago(DateTime t) {
      var d = DateTime.Now - t; if (d.TotalSeconds < 0) return "just now";
      return d.TotalMinutes < 1 ? (int)d.TotalSeconds + "s ago" : d.TotalHours < 1 ? (int)d.TotalMinutes + "m ago" : d.TotalDays < 1 ? (int)d.TotalHours + "h ago" : (int)d.TotalDays + "d ago";
    }

    // White text with a dark outline, centred in the box.
    void CenterLabel(Graphics g, string s, RectangleF box) { CenterLabel(g, s, box, f8b, Color.White); }
    void CenterLabel(Graphics g, string s, RectangleF box, Font f, Color c) {
      var sz = g.MeasureString(s, f);
      float x = box.X + (box.Width - sz.Width) / 2, y = box.Y + (box.Height - sz.Height) / 2;
      var shadow = B(Color.FromArgb(230, 0, 0, 0));
      for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) if (dx != 0 || dy != 0) g.DrawString(s, f, shadow, x + dx, y + dy);
      g.DrawString(s, f, B(c), x, y);
    }

    // ---------- small files, read on THIS thread at most every 10 s (shared read: never blocks the bot's appends) ----------
    DateTime filesAt = DateTime.MinValue;
    readonly Dictionary<int, int[]> lvlRec = new Dictionary<int, int[]>();   // level -> {deaths, wins, deaths since last win}
    string lastDeath = ""; DateTime lastDeathAt = DateTime.MinValue;
    string hitLine = "";
    static List<string> ReadShared(string path, int tailBytes) {
      var lines = new List<string>();
      try {
        if (!File.Exists(path)) return lines;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
          bool cut = false;
          if (tailBytes > 0 && fs.Length > tailBytes) { fs.Seek(-tailBytes, SeekOrigin.End); cut = true; }
          using (var sr = new StreamReader(fs)) {
            string l; bool first = true;
            while ((l = sr.ReadLine()) != null) { if (first && cut) { first = false; continue; } first = false; lines.Add(l); }
          }
        }
      } catch { }
      return lines;
    }
    void RefreshFiles() {
      if ((DateTime.Now - filesAt).TotalSeconds < 10) return;
      filesAt = DateTime.Now;
      try {
        lvlRec.Clear();
        foreach (var l in ReadShared(@"C:\cbot\pro_record.txt", 0)) {
          var m = Regex.Match(l, @"PRO lvl (\d+) (WON|DIED)"); if (!m.Success) continue;
          int lv = int.Parse(m.Groups[1].Value); int[] a;
          if (!lvlRec.TryGetValue(lv, out a)) { a = new int[3]; lvlRec[lv] = a; }
          if (m.Groups[2].Value == "DIED") { a[0]++; a[2]++; } else { a[1]++; a[2] = 0; }
        }
      } catch { }
      try {
        var idx = ReadShared(@"C:\cbot\replays\index.txt", 16384);
        for (int i = idx.Count - 1; i >= 0; i--) {
          string l = idx[i]; if (!l.Contains("result=DIED")) continue;
          var lv = Regex.Match(l, @"lvl=(\d+)"); var k = Regex.Match(l, @"killer=([A-Z0-9_]+)");
          int fi = l.IndexOf(".txt "); string where = fi > 0 ? l.Substring(fi + 5).Trim() : "";
          where = Regex.Replace(where, @"\s*killer=\S+", "").Trim();
          DateTime at; if (l.Length >= 19 && DateTime.TryParse(l.Substring(0, 19), CultureInfo.InvariantCulture, DateTimeStyles.None, out at)) lastDeathAt = at;
          lastDeath = (lv.Success ? LevelName(int.Parse(lv.Groups[1].Value)) : "?") + (where.Length > 0 ? " " + where : "") + (k.Success && k.Groups[1].Value != "-" ? " - killer " + k.Groups[1].Value : "");
          break;
        }
      } catch { }
      try {   // "SQUASH 259 tries: 219 HIT_KILL 84% (...) 23 WIPED 8% ..." per plant
        var parts = new List<string>();
        string[] order = { "SQUASH", "CHERRY", "JALAPENO", "HYPNO", "MINE", "CHOMPER" };
        foreach (var seg in Outcomes.Summary(null).Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries)
                                    .OrderBy(x => { int i = Array.IndexOf(order, x.Split(' ')[0]); return i < 0 ? 99 : i; })) {
          var head = Regex.Match(seg, @"^([A-Z]+) (\d+) tries"); if (!head.Success) continue;
          int total = int.Parse(head.Groups[2].Value), hit = 0, miss = 0;
          foreach (Match m in Regex.Matches(seg, @"(\d+) ([A-Z_]+) \d+%")) {
            int n = int.Parse(m.Groups[1].Value); string r = m.Groups[2].Value;
            if (r.StartsWith("HIT") || r == "CHARMED") hit += n; else if (r == "MISS") miss += n;
          }
          if (total > 0) parts.Add(head.Groups[1].Value.ToLower() + " " + (100 * hit / total) + "% of " + total + (miss > 0 ? " (" + miss + " miss)" : ""));
        }
        hitLine = parts.Count == 0 ? "no squash / cherry / hypno results yet" : string.Join("   ", parts.ToArray());
      } catch { hitLine = "(outcomes unavailable)"; }
    }

    // A paint error must never kill the panel (WinForms stops repainting a control after one), so draw defensively.
    protected override void OnPaint(PaintEventArgs e) {
      try {
        e.Graphics.Clear(Color.FromArgb(8, 10, 16));
        DrawSides(e.Graphics);
        e.Graphics.TranslateTransform(0, panelOffY);
        if (Shaking) e.Graphics.TranslateTransform(rng.Next(-6, 7), rng.Next(-4, 5));   // death shake
        PaintPanel(e); e.Graphics.ResetClip(); DrawToasts(e.Graphics); Fx(e.Graphics, ClientSize.Width, (ClientSize.Height - panelOffY)); DrawFloaters(e.Graphics, ClientSize.Width, (ClientSize.Height - panelOffY)); Heartbeat(e.Graphics, ClientSize.Width, (ClientSize.Height - panelOffY)); Captions(e.Graphics, ClientSize.Width, (ClientSize.Height - panelOffY));
      }
      catch (Exception ex) { try { e.Graphics.ResetClip(); e.Graphics.DrawString("panel glitch: " + ex.Message, f8, Brushes.OrangeRed, 4, 4); } catch { } }
    }

    // ---------- animation state (panel thread only) ----------
    static double Anim { get { return (Environment.TickCount & int.MaxValue) / 1000.0; } }
    int seenDeaths = -1, seenWins = -1; DateTime deathFlash = DateTime.MinValue, winFlash = DateTime.MinValue;
    Rectangle toastArea = Rectangle.Empty;
    readonly Font fToast = new Font("Consolas", 15f, FontStyle.Bold);
    // Big moments slide in from the right, float up and fade (2.6 s each, newest on top).
    static Color Hue(double h) {   // h in turns; full-saturation rainbow
      h = h - Math.Floor(h); double r = Math.Abs(h * 6 - 3) - 1, gg = 2 - Math.Abs(h * 6 - 2), b = 2 - Math.Abs(h * 6 - 4);
      Func<double, int> c = v => (int)(255 * Math.Max(0, Math.Min(1, v)) * 0.85 + 30);
      return Color.FromArgb(c(r), c(gg), c(b));
    }
    int lastSun = int.MaxValue; DateTime sunPop = DateTime.MinValue;
    int plantSeq;
    class Floater { public string Text; public float X; public DateTime At; public Color C; }
    readonly List<Floater> floaters = new List<Floater>();
    void DrawFloaters(Graphics g, int W, int H) {
      List<string> ps; int seq; lock (L) { ps = plantPops.ToList(); seq = PlantSeq; }
      for (int i = Math.Max(0, ps.Count - (seq - plantSeq)); i < ps.Count && seq != plantSeq; i++)
        floaters.Add(new Floater { Text = "+" + ps[i], X = 30 + (float)rng.NextDouble() * (W * 0.3f), At = DateTime.Now, C = Hue(rng.NextDouble()) });
      plantSeq = seq;
      for (int i = floaters.Count - 1; i >= 0; i--) {
        var fl = floaters[i]; double age = (DateTime.Now - fl.At).TotalSeconds; if (age > 1.6) { floaters.RemoveAt(i); continue; }
        int al = (int)(255 * (1 - age / 1.6)); float y = H - 40 - (float)(age * 90);
        g.DrawString(fl.Text, f11, B(A(al, Color.Black)), fl.X + 2, y + 2);
        g.DrawString(fl.Text, f11, B(A(al, fl.C)), fl.X, y);
      }
    }
    // ---------- TikTok mode: word-by-word captions, a corner runner, heartbeat on danger ----------
    string capText = ""; DateTime capAt = DateTime.MinValue; string lastSays = "";
    readonly Font fCap = new Font("Impact", 34f, FontStyle.Regular);
    void Captions(Graphics g, int W, int H) {
      string says = null;
      lock (L) foreach (var ev2 in events) { int q = ev2.IndexOf("SAYS \""); if (q >= 0) { says = ev2.Substring(q + 6).TrimEnd('"'); break; } }
      if (says != null && says != lastSays) { lastSays = says; capText = says; capAt = DateTime.Now; }
      if (capText.Length == 0) return;
      var words = capText.Split(' '); double age = (DateTime.Now - capAt).TotalSeconds;
      int idx = (int)(age / 0.32); if (idx >= words.Length + 4) return;   // one word every 0.32 s, then linger
      int lo = Math.Max(0, Math.Min(idx, words.Length - 1) - 2), hi = Math.Min(words.Length - 1, idx);
      string line = string.Join(" ", words, lo, hi - lo + 1).ToUpper();
      double wAge = age - idx * 0.32; float sc = idx < words.Length ? (float)(1 + 0.4 * Math.Max(0, 1 - wAge * 6)) : 1f;
      using (var f = new Font("Impact", 34f * sc)) using (var gp = new GraphicsPath()) {
        var sz = g.MeasureString(line, f); float x = W / 2f - sz.Width / 2, y = H * 0.55f;
        gp.AddString(line, f.FontFamily, 0, f.Size * 1.33f, new PointF(x, y), StringFormat.GenericDefault);
        g.DrawPath(new Pen(Color.Black, 7) { LineJoin = LineJoin.Round }, gp);
        g.FillPath(B(idx < words.Length ? Color.FromArgb(255, 235, 60) : Color.White), gp);
      }
    }
    // A tiny Sproutbot running forever, hopping zombies (pure eye-candy, bottom-right corner).
    void Runner(Graphics g, int W, int H) {
      var r = new RectangleF(W - 300, H - 92, 290, 84);
      g.FillRectangle(B(A(200, Color.FromArgb(14, 18, 26))), r); g.DrawRectangle(P(Hue(Anim * 0.2), 2), r.X, r.Y, r.Width, r.Height);
      float ground = r.Bottom - 14, speed = 160;
      for (float sx = r.X - (float)((Anim * speed) % 30); sx < r.Right; sx += 30) g.FillRectangle(B(A(90, Good)), Math.Max(r.X, sx), ground, 14, 3);
      double period = 1.4, ph = Anim % period; float jump = ph < 0.55 ? (float)(Math.Sin(ph / 0.55 * Math.PI) * 34) : 0;
      float zx = r.X + 60 + (float)((period - ph) / period * 170);   // the zombie it hops, arriving every 1.4 s
      g.FillRectangle(B(Bad), zx, ground - 22, 12, 22); g.FillRectangle(B(Color.FromArgb(120, 160, 110)), zx, ground - 30, 12, 9);
      float bx = r.X + 50, by = ground - 26 - jump;
      g.FillRectangle(B(Color.FromArgb(170, 178, 186)), bx, by, 22, 20); g.FillEllipse(B(Color.FromArgb(168, 255, 96)), bx + 4, by + 5, 6, 6); g.FillEllipse(B(Color.FromArgb(168, 255, 96)), bx + 12, by + 5, 6, 6);
      g.FillEllipse(B(Color.FromArgb(120, 200, 70)), bx + 6, by - 8, 7, 5); g.FillEllipse(B(Color.FromArgb(140, 215, 85)), bx + 11, by - 9, 7, 5);
      float leg = (float)Math.Sin(Anim * 18) * 4; g.DrawLine(P(Color.White, 2), bx + 6, by + 20, bx + 6 + leg, by + 26); g.DrawLine(P(Color.White, 2), bx + 15, by + 20, bx + 15 - leg, by + 26);
      g.DrawString("SPROUTBOT RUN  " + ((int)(Anim * 10) % 100000) + "m", f8b, B(Hue(Anim * 0.5)), r.X + 6, r.Y + 4);
    }
    // ---------- full screen: the space beside the game ----------
    Rectangle gameHole = Rectangle.Empty; int panelOffY; static bool ShowTicker = false;
    List<Tuple<int, double>> splits = new List<Tuple<int, double>>(); DateTime runStart = DateTime.MinValue, lastSplitAt = DateTime.MinValue, splitsRead = DateTime.MinValue;
    void ReadSplits() {
      if ((DateTime.Now - splitsRead).TotalSeconds < 10) return; splitsRead = DateTime.Now;
      var ls = ReadShared(@"C:\cbot\pro_record.txt", 60000); var sp = new List<Tuple<int, double>>(); DateTime prev = DateTime.MinValue, first = DateTime.MinValue;
      foreach (var l in ls) {
        var m = System.Text.RegularExpressions.Regex.Match(l, @"^(\S+ \S+) PRO lvl (\d+) (WON|DIED)"); DateTime at;
        if (!m.Success || !DateTime.TryParse(m.Groups[1].Value, out at) || at.Date != DateTime.Today) continue;
        if (first == DateTime.MinValue) first = at;
        if (m.Groups[3].Value == "WON") { if (prev != DateTime.MinValue) sp.Add(Tuple.Create(int.Parse(m.Groups[2].Value), (at - prev).TotalSeconds)); prev = at; }
      }
      splits = sp; runStart = first; lastSplitAt = prev;
    }
    static string Clock(double sec) { var ts = TimeSpan.FromSeconds(Math.Max(0, sec)); return ts.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds) : string.Format("{0}:{1:00}.{2}", ts.Minutes, ts.Seconds, ts.Milliseconds / 100); }
    void DrawSides(Graphics g) {
      if (gameHole.IsEmpty || panelOffY <= 0) return;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      var left = new Rectangle(8, 8, gameHole.Left - 16, panelOffY - 16);
      var right = new Rectangle(gameHole.Right + 8, 8, ClientSize.Width - gameHole.Right - 16, panelOffY - 16);
      if (left.Width > 160) SpeedrunHud(g, left);
      if (right.Width > 90) BigStats(g, right);
    }
    void SpeedrunHud(Graphics g, Rectangle r) {
      ReadSplits();
      using (var lg = new LinearGradientBrush(r, Color.FromArgb(30, 20, 40), Color.FromArgb(10, 12, 20), LinearGradientMode.Vertical)) g.FillRectangle(lg, r);
      g.DrawRectangle(P(Hue(Anim * 0.2), 3), r);
      using (var ft = new Font("Impact", 20f)) g.DrawString("SWEATY SPEEDRUN  any% Adventure", ft, B(Gold), r.X + 10, r.Y + 6);
      double runSec = runStart == DateTime.MinValue ? 0 : (DateTime.Now - runStart).TotalSeconds, splitSec = lastSplitAt == DateTime.MinValue ? 0 : (DateTime.Now - lastSplitAt).TotalSeconds;
      double avg = splits.Count > 0 ? splits.Average(q => q.Item2) : 0, best = splits.Count > 0 ? splits.Min(q => q.Item2) : 0;
      using (var fb = new Font("Consolas", 40f, FontStyle.Bold)) { g.DrawString(Clock(runSec), fb, B(Color.Black), r.X + 13, r.Y + 43); g.DrawString(Clock(runSec), fb, B(Hue(Anim * 0.25)), r.X + 10, r.Y + 40); }
      Color live = avg <= 0 ? Txt : splitSec < best ? Gold : splitSec < avg ? Good : Bad;
      using (var fs = new Font("Consolas", 16f, FontStyle.Bold)) g.DrawString("split " + Clock(splitSec) + (avg > 0 ? (splitSec < avg ? "  AHEAD -" : "  BEHIND +") + Clock(Math.Abs(splitSec - avg)) : ""), fs, B(live), r.X + 10, r.Y + 104);
      // the runner: sprinting flat out, gritting teeth, sweat flying
      var run = new RectangleF(r.X + 10, r.Y + 136, r.Width - 20, 120);
      float ground = run.Bottom - 10, legSp = 32, speed = 420;
      for (float sx = run.X - (float)((Anim * speed) % 40); sx < run.Right; sx += 40) g.FillRectangle(B(A(120, Good)), Math.Max(run.X, sx), ground, 20, 4);
      for (int k = 0; k < 9; k++) { float ly = run.Y + 6 + k * 11; float lx = run.Right - (float)((Anim * 700 + k * 91) % run.Width); g.DrawLine(P(A(70, Color.White), 2), lx, ly, lx + 34, ly); }
      double period = 1.1, ph = Anim % period; float jump = ph < 0.45 ? (float)(Math.Sin(ph / 0.45 * Math.PI) * 52) : 0;
      float zx = run.X + 100 + (float)((period - ph) / period * (run.Width - 110));
      g.FillRectangle(B(Bad), zx, ground - 34, 18, 34); g.FillRectangle(B(Color.FromArgb(120, 160, 110)), zx, ground - 48, 18, 14);
      float bob = (float)Math.Abs(Math.Sin(Anim * legSp / 2)) * 4, bx = run.X + 60, by = ground - 50 - bob - jump, S = 1.6f;
      g.FillRectangle(B(Color.FromArgb(170, 178, 186)), bx, by, 24 * S, 20 * S); g.DrawRectangle(P(Color.FromArgb(70, 78, 86), 2), bx, by, 24 * S, 20 * S);
      g.FillEllipse(B(Color.FromArgb(168, 255, 96)), bx + 21 * S, by + 5 * S, 7 * S, 7 * S); g.FillEllipse(B(Color.FromArgb(168, 255, 96)), bx + 4 * S, by + 5 * S, 6 * S, 6 * S);
      g.FillEllipse(B(Color.FromArgb(120, 200, 70)), bx + 6 * S, by - 7 * S, 7 * S, 5 * S); g.FillEllipse(B(Color.FromArgb(140, 215, 85)), bx + 12 * S, by - 8 * S, 7 * S, 5 * S);
      g.DrawLine(P(Color.FromArgb(70, 78, 86), 3), bx + 6 * S, by + 15 * S, bx + 18 * S, by + 13 * S);
      float lgg = (float)Math.Sin(Anim * legSp) * 12; g.DrawLine(P(Color.White, 4), bx + 10, by + 32, bx + 10 + lgg, by + 48); g.DrawLine(P(Color.White, 4), bx + 28, by + 32, bx + 28 - lgg, by + 48);
      g.DrawLine(P(Color.White, 3), bx + 2, by + 14, bx - 12 - lgg / 2, by + 24); g.DrawLine(P(Color.White, 3), bx + 36, by + 14, bx + 50 + lgg / 2, by + 6);
      for (int k = 0; k < 7; k++) { double sa = ((Anim * 2.6 + k * 0.143) % 1); g.FillEllipse(B(A((int)(230 * (1 - sa)), Color.FromArgb(110, 190, 255))), bx - 6 - (float)(sa * 44), by + 4 + (float)(sa * sa * 50) - (k % 3) * 4, 5, 8); }
      using (var fw = new Font("Impact", 14f)) g.DrawString((int)(Anim * 12) % 2 == 0 ? "*pant*" : "*PANT*", fw, B(A(200, Color.White)), bx + 50, by - 22);
      // splits, newest first
      float yy = run.Bottom + 8;
      using (var fsp = new Font("Consolas", 13f, FontStyle.Bold))
        foreach (var sp in splits.Skip(Math.Max(0, splits.Count - 8)).Reverse()) {
          if (yy > r.Bottom - 18) break;
          Color c = sp.Item2 <= best + 0.5 ? Gold : sp.Item2 < avg ? Good : Bad;
          g.DrawString(LevelName(sp.Item1).PadRight(6) + Clock(sp.Item2).PadLeft(9) + (sp.Item2 <= best + 0.5 ? "  BEST" : ""), fsp, B(c), r.X + 10, yy); yy += 20;
        }
    }
    void BigStats(Graphics g, Rectangle r) {
      using (var lg = new LinearGradientBrush(r, Color.FromArgb(20, 30, 24), Color.FromArgb(10, 12, 20), LinearGradientMode.Vertical)) g.FillRectangle(lg, r);
      g.DrawRectangle(P(Hue(Anim * 0.2 + 0.5), 3), r);
      float y = r.Y + 8; Action<string, string, Color> stat = (lab, val, c) => {
        g.DrawString(lab, f8b, B(Dim), r.X + 8, y);
        using (var fv = new Font("Impact", 26f)) g.DrawString(val, fv, B(c), r.X + 6, y + 12);
        y += 58; };
      stat("KILLS", Kills.ToString(), Gold); stat("WINS", (Bot.ProWins < 0 ? 0 : Bot.ProWins).ToString(), Good); stat("DEATHS", (Bot.ProDeaths < 0 ? 0 : Bot.ProDeaths).ToString(), Bad);
      stat("PLANTED", Planted.ToString(), Cyan); stat("LOOP Hz", LoopHz.ToString("0"), Hue(Anim * 0.4));
    }
    void Heartbeat(Graphics g, int W, int H) {
      double worst = 0; foreach (var d in Bot.LaneDanger) worst = Math.Max(worst, d);
      if (worst < 1.5) return;
      double bpm = Math.Min(3.5, 1 + worst * 0.3), ph = (Anim * bpm) % 1;
      double beat = ph < 0.12 ? ph / 0.12 : ph < 0.3 ? 1 - (ph - 0.12) / 0.18 : 0;
      int al = (int)(Math.Min(90, 20 + worst * 8) * beat);
      for (int k = 0; k < 6; k++) g.DrawRectangle(P(A(al / (k + 1), Bad), 6), k * 6, k * 6, W - 1 - k * 12, H - 1 - k * 12);
    }
    // ---------- arcade FX: particles, kill streaks, death flash + shake, level-clear confetti ----------
    class Part { public float X, Y, VX, VY, Life, Max, Size; public Color C; public bool Confetti; }
    readonly List<Part> parts = new List<Part>();
    readonly Random rng = new Random();
    int fxSeq; DateTime lastKill = DateTime.MinValue, deathAt = DateTime.MinValue, winAt = DateTime.MinValue, streakAt = DateTime.MinValue;
    int streak; string streakText = ""; double lastFx = Anim;
    void Burst(float x, float y, Color c, int count, float speed) {
      for (int i = 0; i < count; i++) { double a = rng.NextDouble() * Math.PI * 2, v = speed * (0.4 + rng.NextDouble());
        parts.Add(new Part { X = x, Y = y, VX = (float)(Math.Cos(a) * v), VY = (float)(Math.Sin(a) * v) - 40, Life = 0, Max = 0.6f + (float)rng.NextDouble() * 0.7f, Size = 2 + (float)rng.NextDouble() * 4, C = c }); }
    }
    void Confetti(int W) {
      Color[] cs = { Gold, Good, Cyan, Purple, Orange, Bad, Color.White };
      for (int i = 0; i < 160; i++) parts.Add(new Part { X = (float)(rng.NextDouble() * W), Y = -10 - (float)rng.NextDouble() * 120, VX = (float)(rng.NextDouble() * 80 - 40), VY = 60 + (float)rng.NextDouble() * 120,
        Life = 0, Max = 3 + (float)rng.NextDouble() * 1.5f, Size = 4 + (float)rng.NextDouble() * 5, C = cs[rng.Next(cs.Length)], Confetti = true });
    }
    // New toasts become effects once; then all effects animate (called every paint, panel thread).
    void Fx(Graphics g, int W, int H) {
      double now = Anim, dt = Math.Max(0, Math.Min(0.1, now - lastFx)); lastFx = now;
      List<Toast> ts; int seq; lock (L) { ts = toasts.ToList(); seq = ToastSeq; }
      for (int i = Math.Max(0, ts.Count - (seq - fxSeq)); i < ts.Count && seq != fxSeq; i++) {
        var t = ts[i]; float bx = toastArea.Right - 120, by = toastArea.Y + 16;
        if (t.Text == "DEATH") { deathAt = DateTime.Now; streak = 0; Burst(W / 2f, H / 2f, Bad, 60, 260); }
        else if (t.Text == "LEVEL CLEARED!") { winAt = DateTime.Now; Confetti(W); }
        else if (t.Text.Contains(">") || t.Text.Contains("CHARMED")) {
          Burst(bx, by, t.C, 26, 170);
          streak = (DateTime.Now - lastKill).TotalSeconds < 6 ? streak + 1 : 1; lastKill = DateTime.Now;
          if (streak >= 2) { streakText = streak == 2 ? "DOUBLE KILL!" : streak == 3 ? "TRIPLE KILL!" : streak == 4 ? "MEGA KILL!" : "RAMPAGE x" + streak + "!!"; streakAt = DateTime.Now; }
        }
        else Burst(bx, by, t.C, 14, 120);
      }
      fxSeq = seq;
      // death: red flash fading over 1.2 s
      double dAge = (DateTime.Now - deathAt).TotalSeconds;
      if (dAge < 1.2) g.FillRectangle(B(A((int)(120 * (1 - dAge / 1.2)), Bad)), 0, 0, W, H);
      // particles
      for (int i = parts.Count - 1; i >= 0; i--) {
        var p = parts[i]; p.Life += (float)dt; if (p.Life > p.Max || p.Y > H + 20) { parts.RemoveAt(i); continue; }
        p.X += p.VX * (float)dt; p.Y += p.VY * (float)dt; p.VY += (p.Confetti ? 30 : 260) * (float)dt;
        if (p.Confetti) p.VX += (float)(Math.Sin(now * 4 + i) * 40 * dt);
        int al = (int)(255 * (1 - p.Life / p.Max));
        if (p.Confetti) g.FillRectangle(B(A(al, p.C)), p.X, p.Y, p.Size, p.Size * 0.5f + (float)Math.Abs(Math.Sin(now * 8 + i)) * p.Size * 0.5f);
        else g.FillEllipse(B(A(al, p.C)), p.X - p.Size / 2, p.Y - p.Size / 2, p.Size, p.Size);
      }
      if (parts.Count > 600) parts.RemoveRange(0, parts.Count - 600);
      // kill streak banner
      double sAge = (DateTime.Now - streakAt).TotalSeconds;
      if (sAge < 1.6 && streakText.Length > 0) {
        float sc = (float)(1 + 0.35 * Math.Max(0, 1 - sAge * 5)); int al = (int)(255 * Math.Min(1, (1.6 - sAge) / 0.5));
        using (var f = new Font("Impact", 26f * sc, FontStyle.Regular)) {
          var sz = g.MeasureString(streakText, f); float x = W / 2f - sz.Width / 2, y = 60;
          g.DrawString(streakText, f, B(A(al, Color.Black)), x + 3, y + 3);
          g.DrawString(streakText, f, B(A(al, Lerp(Gold, Bad, 0.5 + 0.5 * Math.Sin(now * 12)))), x, y);
        }
      }
      // level clear banner
      double wAge = (DateTime.Now - winAt).TotalSeconds;
      if (wAge < 4) {
        int al = (int)(255 * Math.Min(1, (4 - wAge) / 0.8)); float sc = (float)(1 + 0.15 * Math.Sin(now * 6));
        using (var f = new Font("Impact", 44f * sc, FontStyle.Regular)) {
          string tx = "LEVEL CLEARED!"; var sz = g.MeasureString(tx, f); float x = W / 2f - sz.Width / 2, y = H / 2f - sz.Height / 2;
          g.DrawString(tx, f, B(A(al, Color.Black)), x + 4, y + 4);
          g.DrawString(tx, f, B(A(al, Lerp(Good, Gold, 0.5 + 0.5 * Math.Sin(now * 5)))), x, y);
        }
      }
    }
    public bool Shaking { get { return (DateTime.Now - deathAt).TotalSeconds < 0.5; } }
    void DrawToasts(Graphics g) {
      if (toastArea.IsEmpty) return;
      List<Toast> ts; lock (L) ts = toasts.ToList();
      g.ResetClip(); int k = 0;
      for (int i = ts.Count - 1; i >= 0 && k < 4; i--) {
        double age = (DateTime.Now - ts[i].At).TotalSeconds; if (age > 2.6) continue;
        double inT = Math.Min(1, age / 0.25), outT = age > 1.9 ? (age - 1.9) / 0.7 : 0;
        int alpha = (int)(235 * (1 - outT));
        var sz = g.MeasureString(ts[i].Text, fToast);
        float x = toastArea.Right - (float)(sz.Width + 24) * (float)(1 - Math.Pow(1 - inT, 3)), y = toastArea.Y + k * 38 - (float)(outT * 14);
        if (inT < 1) x = toastArea.Right - (float)((sz.Width + 24) * inT);
        var box = new RectangleF(x, y, sz.Width + 20, sz.Height + 6);
        g.FillRectangle(B(A((int)(alpha * 0.8), Color.FromArgb(10, 12, 16))), box);
        g.FillRectangle(B(A(alpha, ts[i].C)), box.X, box.Y, 4, box.Height);
        if (age < 0.35) g.DrawRectangle(P(A((int)(255 * (1 - age / 0.35)), ts[i].C), 2), box.X - 2, box.Y - 2, box.Width + 4, box.Height + 4);   // impact ring
        g.DrawString(ts[i].Text, fToast, B(A(alpha, ts[i].C)), box.X + 12, box.Y + 3);
        k++;
      }
    }
    // ---------- layout helpers ----------
    // A titled box; returns the inner content rectangle and clips drawing to it. 'note' is a dim right-aligned hint.
    Rectangle Box(Graphics g, Rectangle r, string title, Color accent) { return Box(g, r, title, null, accent); }
    Rectangle Box(Graphics g, Rectangle r, string title, string note, Color accent) {
      g.ResetClip();
      using (var lg = new LinearGradientBrush(r, Color.FromArgb(28, 36, 47), Color.FromArgb(17, 22, 29), LinearGradientMode.Vertical)) g.FillRectangle(lg, r);
      g.DrawRectangle(P(A(170, Hue(Anim * 0.15 + r.X * 0.0009 + r.Y * 0.0013)), 1.6f), r);   // neon border cycling through the rainbow
      double pulse = 0.5 + 0.5 * Math.Sin(Anim * 2.2 + r.X * 0.01);
      g.FillRectangle(B(A(60, accent)), r.X, r.Y, r.Width, 1);                       // thin accent line along the top
      g.FillRectangle(B(A(90 + (int)(110 * pulse), accent)), r.X, r.Y, 3, 16);       // breathing accent tab
      g.SetClip(new Rectangle(r.X, r.Y, r.Width, 17));
      float tx = T(g, title, f8b, accent, r.X + 7, r.Y + 2, r.Width - 12);
      if (!string.IsNullOrEmpty(note)) {
        float room = r.Right - 6 - tx;
        int n = (int)(room / CW(g, f8));
        if (n > 8) { string s = Fit(note, n); g.DrawString(s, f8, B(Dim), r.Right - 6 - s.Length * CW(g, f8) - 2, r.Y + 3); }
      }
      var inner = new Rectangle(r.X + 6, r.Y + 18, r.Width - 12, r.Height - 22);
      g.SetClip(inner);
      return inner;
    }

    void PaintPanel(PaintEventArgs e) {
      var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
      State s; List<Cand> cs; Cand ch; string sav; string[] ev; double[] rx; int[] sh, dh;
      lock (L) { s = state; cs = cands.ToList(); ch = chosen; sav = saving; ev = events.ToArray(); rx = reacts.ToArray(); sh = sunHist.ToArray(); dh = dpsHist.ToArray(); }
      var gs = Guess.Current;
      RefreshFiles();
      int W = ClientSize.Width, H = ClientSize.Height - panelOffY;
      float go = (float)((Anim * 22) % 40);
      for (float gx2 = -40 + go; gx2 < W; gx2 += 40) g.DrawLine(P(A(28, Hue(Anim * 0.1 + gx2 * 0.0006)), 1), gx2, 0, gx2 + 60, H);
      for (float gy2 = go; gy2 < H; gy2 += 40) g.DrawLine(P(A(18, Cyan), 1), 0, gy2, W, gy2);
      float scan = (float)((Anim * 160) % (H + 80)) - 40;
      g.FillRectangle(B(A(18, Color.White)), 0, scan, W, 3);
      const int pad = 6, topH = 52;
      bool board = s != null && s.HasBoard;
      bool vaseLvl = board && VasePlanner.Active && (s.Vases.Count > 0 || Bot.PrecisionLevel);

      // ===== top bar: level & progress | record & health of the bot | speed =====
      using (var tg = new LinearGradientBrush(new Rectangle(0, 0, W, topH), Color.FromArgb(26, 38, 52), Color.FromArgb(12, 16, 22), LinearGradientMode.Vertical)) g.FillRectangle(tg, 0, 0, W, topH);
      for (int i = 0; i < 26; i++) {   // drifting sun-sparks: pure decoration, deterministic from time
        double sp = 12 + (i % 5) * 7, px0 = (i * 173.3 + Anim * sp) % (W + 40) - 20, py0 = 6 + (i * 37 % 40) + 3 * Math.Sin(Anim * 1.3 + i);
        int al = (int)(40 + 50 * (0.5 + 0.5 * Math.Sin(Anim * 2 + i * 1.7)));
        float sz = 2 + (i % 3);
        g.FillEllipse(B(A(al, i % 4 == 0 ? Good : SunC)), (float)px0, (float)py0, sz, sz);
      }
      g.FillRectangle(B(A(70, Gold)), 0, topH - 1, W, 1);
      float x = T(g, board ? "LEVEL " + LevelName(s.Level) + "  " + AreaName(s.Background) : "WAITING FOR GAME", f11, Gold, pad, 3, 260);
      if (board) {
        if (s.Sun > lastSun) sunPop = DateTime.Now; lastSun = s.Sun;
        double sp2 = 1 - (DateTime.Now - sunPop).TotalSeconds / 0.5;
        float bump = sp2 > 0 ? (float)(-4 * Math.Sin(sp2 * Math.PI)) : 0;
        if (sp2 > 0) g.FillEllipse(B(A((int)(120 * sp2), SunC)), x + 4, 1 + bump, 26, 20);
        T(g, "SUN " + s.Sun, f11, sp2 > 0 ? Color.White : SunC, x + 8, 3 + bump, 120);
      }
      var pb = new Rectangle(pad, 26, Math.Max(120, Math.Min(300, W / 4)), 10);
      g.FillRectangle(B(Track), pb);
      if (board && vaseLvl) {   // vase levels: rounds instead of waves
        int vr = Math.Max(1, Math.Min(3, Bot.VaseRound));
        for (int i = 0; i < 3; i++) {
          var seg = new Rectangle(pb.X + i * pb.Width / 3 + 1, pb.Y, pb.Width / 3 - 2, pb.Height);
          g.FillRectangle(B(i + 1 < vr ? A(200, Good) : i + 1 == vr ? Gold : Track), seg);
        }
        string vt = "VASE ROUND " + vr + "/3" + (VasePlanner.FinalRound ? " (final)" : "") + " - " + s.Vases.Count + " vase" + (s.Vases.Count == 1 ? "" : "s") + " left"
                    + (VasePlanner.Hunting ? "  DARTH'S GAMBIT" : "") + (VasePlanner.DanceHunt ? "  DANCE HUNT" : "");
        T(g, vt, f8b, VasePlanner.Hunting || VasePlanner.DanceHunt ? Bad : Txt, pb.Right + 8, pb.Y - 2, 300);
      } else if (board && s.Waves > 0) {
        float fillW = (float)(pb.Width * Math.Min(1, (double)s.Wave / s.Waves));
        if (fillW > 1) using (var wg = new LinearGradientBrush(new RectangleF(pb.X, pb.Y, fillW + 1, pb.Height), Color.FromArgb(80, 170, 70), Color.FromArgb(150, 230, 110), LinearGradientMode.Horizontal)) g.FillRectangle(wg, pb.X, pb.Y, fillW, pb.Height);
        float shx = pb.X + (float)((Anim * 90) % (fillW + 40)) - 20;   // shimmer sweeping along the filled part
        if (fillW > 6) { g.SetClip(new RectangleF(pb.X, pb.Y, fillW, pb.Height)); g.FillRectangle(B(A(90, Color.White)), shx, pb.Y, 14, pb.Height); g.ResetClip(); }
        for (int f = 10; f <= s.Waves; f += 10) {
          float fx = pb.X + pb.Width * f / (float)s.Waves - 4;
          g.FillRectangle(B(Bad), fx, pb.Y - 3, 3, pb.Height + 6);
          g.FillPolygon(B(Bad), new[] { new PointF(fx + 3, pb.Y - 3), new PointF(fx + 10, pb.Y + 1), new PointF(fx + 3, pb.Y + 5) });
        }
        int tf = Bot.WavesToFlag(s);
        string prog = "WAVE " + s.Wave + "/" + s.Waves + "  " + (tf < 0 ? "no flags left" : tf == 0 ? "FLAG WAVE NOW" : "flag in " + tf);
        T(g, prog, f8b, tf >= 0 && tf <= 1 ? Bad : Txt, pb.Right + 8, pb.Y - 2, 200);
      }
      // scrolling ticker: the latest thing the bot did, like a news crawl
      string latest = ""; lock (L) { if (events.Count > 0) latest = events.First.Value; }
      if (ShowTicker && latest.Length > 0) {   // ticker off: it overlapped the attempt/perf lines (owner)
        float tw = latest.Length * CW(g, f8) + 60, tx0 = W * 0.37f, tw0 = W * 0.27f;
        float off = (float)((Anim * 70) % (tw + tw0));
        g.SetClip(new RectangleF(tx0, topH - 13, tw0, 12));
        g.DrawString(">> " + latest, f8, B(Hue(Anim * 0.3)), tx0 + tw0 - off, topH - 13);
        g.ResetClip();
      }
      // Hall of Fame (a promise kept): the mod author beat 4-5 before the bot did.
      T(g, "HALL OF FAME: darthspiker - the Proer Pro (beat 4-5 before the bot)", f8, Color.Gold, pad, 39, Math.Max(200, W * 0.36f - 10));

      int speedW = Speeds.Length * 38;
      float mx = Math.Max(pb.Right + 210, W * 0.37f), mw = W - speedW - pad - 14 - mx;
      if (Bot.ProMode && Bot.ProDeaths >= 0) {
        float px = T(g, "PRO", f11, Dim, mx, 3, mw);
        if (Bot.ProDeaths != seenDeaths) { if (seenDeaths >= 0) deathFlash = DateTime.Now; seenDeaths = Bot.ProDeaths; }
        if (Bot.ProWins != seenWins) { if (seenWins >= 0) winFlash = DateTime.Now; seenWins = Bot.ProWins; }
        double df = 1 - (DateTime.Now - deathFlash).TotalSeconds / 1.5, wf = 1 - (DateTime.Now - winFlash).TotalSeconds / 2.5;
        if (df > 0) g.FillRectangle(B(A((int)(150 * df), Bad)), px + 2, 2, 110, 20);
        if (wf > 0) g.FillRectangle(B(A((int)(170 * wf), Good)), px + 118, 2, 90, 20);
        px = T(g, "DEATHS " + Bot.ProDeaths, f11, df > 0 ? Color.White : Bad, px + 6, 3, mx + mw - px);
        px = T(g, "WINS " + Bot.ProWins, f11, Good, px + 10, 3, mx + mw - px);
        T(g, "ROUNDS " + (Bot.ProDeaths + Bot.ProWins), f11, Txt, px + 10, 3, mx + mw - px);
      } else T(g, string.Format("DEATHS {0}   STREAK {1} (best {2})   ROUNDS {3}", Learn.Deaths, Learn.Streak, Learn.BestStreak, Learn.Deaths + Learn.Wins), f11, Learn.Streak > 0 ? Good : Bad, mx, 3, mw);
      // this level's record + the last death
      string lvlLine = "";
      if (board) {
        int[] rec; int att;
        if (Bot.ProMode && lvlRec.TryGetValue(s.Level, out rec)) {
          att = rec[2] + 1;
          lvlLine = "this level: attempt " + att + (rec[1] > 0 ? " since the last win" : "") + "  (" + rec[0] + " death" + (rec[0] == 1 ? "" : "s") + ", " + rec[1] + " win" + (rec[1] == 1 ? "" : "s") + ")";
        }
        else if (Bot.ProMode) lvlLine = "this level: attempt 1  (new level)";
        else lvlLine = "this level: attempt " + (Learn.DeathsHere + 1);
      }
      T(g, lvlLine, f8b, Orange, mx, 24, mw);
      string perf = Status + string.Format(CultureInfo.InvariantCulture, "  |  loop {0:0} Hz  read {1:0.0} ms  act {2:0.0} ms  |  up {3} (total {4})",
        LoopHz, Bot.TRead, Bot.TAct, Dur(DateTime.Now - Started), Dur(DateTime.Now - ProjectStart));
      T(g, perf, f8, Dim, mx, 38, mw);
      speedButtons.Clear();
      for (int i = 0; i < Speeds.Length; i++) {
        var rb = new Rectangle(W - speedW - pad + i * 38, 16, 35, 20);
        bool on = Math.Abs(Bot.UserSpeed - Speeds[i]) < 0.01;
        g.FillRectangle(B(on ? Color.FromArgb(255, 160, 40) : Color.FromArgb(40, 50, 62)), rb);
        g.DrawRectangle(P(Color.FromArgb(90, 110, 130), 1), rb);
        CenterLabel(g, Speeds[i] + "x", rb);
        speedButtons.Add(Tuple.Create(rb, Speeds[i]));
      }
      T(g, Bot.SlowMo ? "SLOW-MO x" + Bot.Speed.ToString("0.#") + " (danger)" : Bot.PrecisionLevel ? "speed (vases: max x2)" : "speed", f8b, Bot.SlowMo ? Bad : Dim, W - speedW - pad, 2, speedW);
      toastArea = new Rectangle((int)(W * 0.36f), topH + 4, (int)(W * 0.3f), 160);

      // ===== columns: lawn | now + guesses + knowledge | record + graphs + log =====
      int bodyY = topH + pad, bodyH = H - bodyY - pad;
      int colA = (int)(W * 0.40), colB = (int)(W * 0.31), colC = W - colA - colB - 4 * pad;
      int xA = pad, xB = xA + colA + pad, xC = xB + colB + pad;

      // --- LAWN ---
      var lawnR = new Rectangle(xA, bodyY, colA, bodyH);
      var lawn = Box(g, lawnR, "LAWN", "solid = seen   dashed ? = guessed   right: danger / 40s sim", Good);
      if (board && gs != null) DrawLawn(g, lawn, gs);
      else T(g, board ? "waiting for the first published frame..." : "no level on screen", f9, Dim, lawn.X, lawn.Y + 4, lawn.Width);
      g.ResetClip();
      truthButton = new Rectangle(lawnR.Right - 92, lawnR.Bottom - 17, 86, 14);
      if (board && gs != null && (gs.Ghosts.Count > 0 || showTruth)) {   // check the guesses against game memory (off by default: the bot never sees this)
        g.FillRectangle(B(showTruth ? Color.FromArgb(90, 80, 40) : Color.FromArgb(34, 42, 52)), truthButton);
        g.DrawRectangle(P(Color.FromArgb(90, 110, 130), 1), truthButton);
        CenterLabel(g, showTruth ? "truth: ON" : "check truth", truthButton, f8, showTruth ? Gold : Dim);
      } else truthButton = Rectangle.Empty;

      // --- NOW (decisions) ---
      int nowH = Math.Max(100, (int)(bodyH * 0.40)), guessH = Math.Max(80, (int)(bodyH * 0.30)), knowH = bodyH - nowH - guessH - 2 * pad;
      var dr = Box(g, new Rectangle(xB, bodyY, colB, nowH), Bot.ProMode ? "NOW   (what the bot is doing and why)" : "NOW   strategy " + Strategy.Cur.Id + " (" + Strategy.Cur.Env + ", gen " + Strategy.Generation + ")", Blue);
      int cpl8 = (int)(dr.Width / CW(g, f8)), cpl9 = (int)(dr.Width / CW(g, f9b));
      int y = dr.Y;
      string main; Color mainC; bool mainIsVase = false;
      if (!string.IsNullOrEmpty(sav)) { main = "SAVING: " + sav; mainC = Gold; }
      else if (ch != null) { main = "DOING: " + ch.Label + " - " + ch.Why; mainC = Good; }
      else if (VasePlanner.Active && VasePlanner.LastWhy.Length > 0) { main = "VASES: " + VasePlanner.LastWhy; mainC = Good; mainIsVase = true; }   // vase levels: the action is breaking vases
      else { main = "nothing worth planting right now"; mainC = Dim; }
      foreach (var l in Wrap(main, cpl9, 2)) { g.DrawString(l, f9b, B(mainC), dr.X, y); y += 14; }
      if (!mainIsVase && VasePlanner.Active && VasePlanner.LastWhy.Length > 0)
        foreach (var l in Wrap("vases: " + VasePlanner.LastWhy, cpl8, 2)) { g.DrawString(l, f8, B(Color.FromArgb(150, 220, 160)), dr.X, y); y += 12; }
      if (Bot.ProMode) { foreach (var l in Wrap("brain: " + Bot.ProStatus, cpl8, 2)) { g.DrawString(l, f8, B(Orange), dr.X, y); y += 12; } }
      else {
        T(g, "'" + Strategy.Cur.Name + "'  avg " + Strategy.Cur.Avg.ToString("0") + " over " + Strategy.Cur.Runs + " runs  | " + Strategy.Cur.Genes, f8, Dim, dr.X, y, dr.Width); y += 12;
        T(g, "post-mortem: " + Diag.Summary(), f8, Orange, dr.X, y, dr.Width); y += 12;
      }
      y += 3;
      if (cs.Count > 0 && y < dr.Bottom - 12) {
        g.DrawString("candidate moves (best first)        score", f8, B(Dim), dr.X, y); y += 12;
        double top = Math.Max(1, cs[0].Score);
        int labW = (int)(CW(g, f8) * 15), barX = dr.X + labW, barW = Math.Max(40, Math.Min(140, dr.Width / 4));
        foreach (var c in cs) {
          if (y > dr.Bottom - 12) break;
          bool isCh = ch != null && c.Label == ch.Label;
          var col = isCh ? Good : c.Affordable ? Color.FromArgb(70, 130, 200) : Color.FromArgb(90, 90, 110);
          T(g, c.Label, isCh ? f8b : f8, c.Affordable ? Txt : Dim, dr.X, y, labW - 2);
          g.FillRectangle(B(Color.FromArgb(isCh ? 210 : 150, col)), barX, y + 2, (float)(barW * Math.Max(0, Math.Min(1, c.Score / top))), 9);
          g.DrawString(c.Score.ToString("0") + (c.Affordable ? "" : "$"), f8, B(Txt), barX + 2, y);
          T(g, c.Why, f8, Dim, barX + barW + 6, y, dr.Right - barX - barW - 6);
          y += 13;
        }
      }

      // --- GUESSES ---
      var gr = Box(g, new Rectangle(xB, bodyY + nowH + pad, colB, guessH), "GUESSES   (what the bot can't see but believes)", GuessC);
      DrawGuesses(g, gr, gs, board);

      // --- KNOWLEDGE ---
      var kr = Box(g, new Rectangle(xB, bodyY + nowH + guessH + 2 * pad, colB, knowH), Bot.ProMode ? "PRO KNOWLEDGE   (fixed expert rules + live record)" : string.Format("KNOWLEDGE   ({0} lessons, caution x{1:0.00})", Learn.Lessons, Learn.Caution), Cyan);
      int ky = kr.Y;
      if (Bot.ProMode) {   // pro mode does not learn: show its record, the super nemesis and the live gambit state instead
        int kcpl = (int)(kr.Width / CW(g, f8));
        foreach (var line in Bot.ProKnowledge())
          foreach (var wl in Wrap(line, kcpl, 2)) { if (ky > kr.Bottom - 11) break; g.DrawString(wl, f8, B(line.StartsWith("DARTH") ? Color.Gold : Txt), kr.X, ky); ky += 12; }
      } else {
        string mine = Learn.MineOk + Learn.MineFail == 0 ? "untested" : Learn.MineOk + " boom / " + Learn.MineFail + " eaten";
        T(g, "mine arm time " + Learn.MineArm.ToString("0.0") + "s (" + mine + ")    sky sun " + (Learn.SkyRateNow < 0 ? "?" : Learn.SkyRateNow.ToString("0.0")) + "/min", f8, Txt, kr.X, ky, kr.Width); ky += 13;
        string dps; try { dps = string.Join("  ", Learn.DpsLearned.ToList().Select(d => Mem.SeedName(d.Key) + " " + d.Value.ToString("0"))); } catch { dps = "..."; }
        T(g, "dps/plant: " + (dps.Length > 0 ? dps : "measuring..."), f8, Txt, kr.X, ky, kr.Width); ky += 13;
        string roles; try { roles = string.Join("  ", Roles.Learned.ToList().Select(r => Mem.SeedName(r.Key) + "=" + r.Value.ToLower())); } catch { roles = ""; }
        if (roles.Length > 0) { T(g, "discovered: " + roles, f8, Cyan, kr.X, ky, kr.Width); ky += 13; }
        string plans; try { plans = string.Join("  ", Strategy.Loadout.Where(Roles.Tentative).Select(sd => Mem.SeedName(sd) + "=" + Roles.RoleOf(sd).ToLower() + "?")); } catch { plans = ""; }
        if (plans.Length > 0) { T(g, "from descriptions (unconfirmed): " + plans, f8, Color.FromArgb(255, 200, 120), kr.X, ky, kr.Width); ky += 13; }
        g.DrawString(string.Format("{0,-18}{1,5}{2,6}{3,7}{4,6}", "zombie kind", "seen", "kills", "tough", "speed"), f8, B(Dim), kr.X, ky); ky += 12;
        List<ZKnow> zks; try { zks = Learn.Z.Values.ToList(); } catch { zks = new List<ZKnow>(); }   // bot thread may be adding kinds
        foreach (var k in zks.OrderByDescending(v => v.Seen)) {
          if (ky > kr.Bottom - 11) break;
          g.DrawString(string.Format("{0,-18}{1,5}{2,6}{3,7}{4,6}", Fit(k.Key, 17), k.Seen, k.Kills, k.ToughN > 0 ? k.Tough.ToString("0") : "?", k.SpeedN >= 3 ? k.Speed.ToString("0.0") : "?"),
            f8, B(k.ToughN > 0 ? Txt : Color.FromArgb(200, 160, 120)), kr.X, ky);
          ky += 11;
        }
      }

      // --- RECORD & STATS ---
      int recH = Math.Min(Math.Max(96, bodyH / 3), 112), graphH = Math.Max(44, Math.Min(80, bodyH / 5));
      var mr = Box(g, new Rectangle(xC, bodyY, colC, recH), "RECORD & STATS", Purple);
      int my = mr.Y;
      T(g, "NEMESIS: " + (Bot.ProMode ? Bot.SuperNemesis() : Quips.Nemesis), f9b, Bad, mr.X, my, mr.Width); my += 15;
      T(g, "last death: " + (lastDeath.Length > 0 ? lastDeath + " (" + Ago(lastDeathAt) + ")" : "none on record"), f8, Color.FromArgb(255, 150, 140), mr.X, my, mr.Width); my += 13;
      foreach (var hl in Wrap("hit rate: " + hitLine, (int)(mr.Width / CW(g, f8)), 2)) { g.DrawString(hl, f8, B(Good), mr.X, my); my += 12; }
      my += 1;
      double avgR = rx.Length > 0 ? rx.Average() : 0;
      int dpsNow = dh.Length > 0 ? dh[dh.Length - 1] : 0;
      T(g, string.Format("this run: kills {0}  planted {1}  suns {2}  coins {3}  clicks {4}  react {5:0}ms  mowers lost {6}  dps {7}",
        Kills, Planted, SunClicks, CoinClicks, Clicks, avgR, MowersLost, dpsNow), f8, Txt, mr.X, my, mr.Width); my += 14;
      var fr = new Rectangle(mr.X, my, Math.Min(150, mr.Width / 3), 13);
      g.FillRectangle(B(Track), fr);
      using (var lg = new LinearGradientBrush(new Rectangle(fr.X, fr.Y, fr.Width + 1, fr.Height), Color.FromArgb(120, 140, 160), Color.FromArgb(200, 120, 255), 0f))
        g.FillRectangle(lg, fr.X, fr.Y, (float)(fr.Width * Faith / 100), fr.Height);
      g.DrawString("FAITH " + Faith.ToString("0") + "%", f8b, Brushes.White, fr.X + 3, fr.Y);
      T(g, FaithText, f8b, Purple, fr.Right + 8, fr.Y, mr.Right - fr.Right - 8);

      // --- GRAPHS ---
      var grr = Box(g, new Rectangle(xC, bodyY + recH + pad, colC, graphH), "GRAPHS   (last 2 minutes)", Gold);
      Spark(g, new Rectangle(grr.X, grr.Y, grr.Width / 2 - 4, grr.Height), sh, Gold, "sun bank");
      Spark(g, new Rectangle(grr.X + grr.Width / 2 + 4, grr.Y, grr.Width / 2 - 4, grr.Height), dh, Bad, "damage / sec");

      // --- EVENT LOG ---
      int logY = bodyY + recH + graphH + 2 * pad;
      var lr = Box(g, new Rectangle(xC, logY, colC, H - logY - pad), "EVENT LOG   (newest first)", Txt);
      int ly = lr.Y;
      foreach (var line in ev) {
        if (ly > lr.Bottom - 12) break;
        Color c = line.Contains("STRAT") || line.Contains("MENU") ? Orange : line.Contains("SAYS") || line.Contains("DAVE") ? Color.FromArgb(255, 230, 120)
              : line.Contains("BLACKOUT") || line.Contains("light is back") ? GuessC
              : line.Contains("FAITH") ? Purple : line.Contains("LEARN") ? Cyan : line.Contains("PLANT") || line.Contains("SHOVEL") ? Good
              : line.Contains("!") || line.Contains("DIED") || line.Contains("fail") || line.Contains("WATCHDOG") ? Bad : line.Contains("WON") ? Gold : Txt;
        T(g, line, f8, c, lr.X, ly, lr.Width); ly += 12;
      }
      g.ResetClip();
    }

    // ---------- GUESSES box: ghosts, vase odds, the lane simulator's forecast ----------
    void DrawGuesses(Graphics g, Rectangle r, Guess.Snap gs, bool board) {
      int y = r.Y; int cpl = (int)(r.Width / CW(g, f8));
      if (!board) { T(g, "no level on screen", f8, Dim, r.X, y, r.Width); return; }
      if (gs == null) { T(g, "guess layer has not run yet (it runs inside Viz.Publish on the main loop)", f8, Dim, r.X, y, r.Width); return; }
      bool stale = (DateTime.Now - gs.At).TotalSeconds > 3;
      if (stale) { T(g, "(no fresh frame for " + (DateTime.Now - gs.At).TotalSeconds.ToString("0") + "s - showing the last one)", f8, Dim, r.X, y, r.Width); y += 12; }
      // ghosts
      string head;
      if (gs.Dark) head = "BLACKOUT: the bot is blind - " + (gs.Ghosts.Count == 0 ? "nothing remembered to guess" : "guessing " + gs.Ghosts.Count + " zombie" + (gs.Ghosts.Count == 1 ? "" : "s") + " from memory");
      else if (gs.Ghosts.Count > 0) head = gs.Ghosts.Count + " zombie" + (gs.Ghosts.Count == 1 ? "" : "s") + " out of sight - position guessed from last sighting + speed, fades over " + Guess.GHOST_LIFE.ToString("0") + "s";
      else head = gs.FogLevel ? "fog level: nothing lost in the fog right now" + (gs.FogEdgeX >= 0 ? " (zombies come out of the fog at x~" + gs.FogEdgeX.ToString("0") + ")" : "") : "every zombie in play is visible - nothing to guess";
      foreach (var l in Wrap(head, cpl, 2)) { g.DrawString(l, f8b, B(gs.Dark ? Gold : gs.Ghosts.Count > 0 ? GuessC : Dim), r.X, y); y += 12; }
      int shown = 0;
      foreach (var gh in gs.Ghosts) {
        if (y > r.Bottom - 12 * 3 || shown >= 4) break;
        string ln = string.Format(CultureInfo.InvariantCulture, "? {0,-6} lane {1}  x{2:0} {3:0.0}s ago -> ~x{4:0} \u00b1{5:0}  {6} sure  {7}",
          gh.Name, gh.Row, gh.LastX, gh.Age, gh.EstX, gh.Band, Pct(gh.Conf), gh.Eating ? "was eating" : gh.Speed.ToString("0") + "px/s");
        if (showTruth) ln += float.IsNaN(gh.TrueX) ? "  [real: gone]" : "  [real x" + gh.TrueX.ToString("0") + "]";
        T(g, ln, f8, A((int)(110 + 145 * gh.Conf), GuessC), r.X, y, r.Width); y += 12; shown++;
      }
      if (gs.Ghosts.Count > shown) { T(g, "  +" + (gs.Ghosts.Count - shown) + " more", f8, Dim, r.X, y, r.Width); y += 12; }
      // vases
      if (gs.VaseActive && gs.Vases.Count > 0 && y < r.Bottom - 11) {
        var byLook = new Dictionary<int, double[]>();   // look -> {count, pz, pp}
        foreach (var v in gs.Vases.Values) { int lk = (int)v[2]; double[] a; if (!byLook.TryGetValue(lk, out a)) { a = new double[3]; byLook[lk] = a; } a[0]++; a[1] = v[0]; a[2] = v[1]; }
        string[] lookN = { "?", "leaf", "skull" };
        var parts = byLook.OrderBy(kv => kv.Key).Select(kv => kv.Value[0] + " " + lookN[Math.Max(0, Math.Min(2, kv.Key))] + " Z" + Pct(kv.Value[1]) + "/P" + Pct(kv.Value[2]));
        T(g, "vase odds (planner): " + string.Join("   ", parts.ToArray()), f8, Txt, r.X, y, r.Width); y += 12;
        if (gs.RemZ >= 0 && y < r.Bottom - 11) {
          T(g, string.Format(CultureInfo.InvariantCulture, "still inside (notebook guess): ~{0:0.0} zombies, ~{1:0.0} plants, FOOT ~{2:0.0}, SQUASH ~{3:0.0}", gs.RemZ, gs.RemS, gs.RemFoot, gs.RemSquash), f8, Dim, r.X, y, r.Width); y += 12;
        }
      }
      // simulator forecast
      if (y < r.Bottom - 11) {
        string sim;
        if (!gs.SimKnown) sim = gs.ProMode ? "lane sim: no forecast this tick" : "lane sim: Pro mode only";
        else {
          var brk = Enumerable.Range(0, gs.RowsN).Where(i => gs.BreachT[i] >= 0 && gs.BreachT[i] < 9000).OrderBy(i => gs.BreachT[i]).ToList();
          sim = brk.Count == 0 ? "lane sim (40s ahead): every lane holds" : "lane sim (40s ahead): " + string.Join(", ", brk.Select(i => "lane " + i + " breaks in " + gs.BreachT[i].ToString("0") + "s").ToArray());
          if (gs.SimExact) { var leak = Enumerable.Range(0, gs.RowsN).Where(i => gs.BreachT[i] >= 9000 && gs.HpLeft[i] > 50).ToList(); if (leak.Count > 0) sim += " | leaks: " + string.Join(", ", leak.Select(i => "lane " + i + " " + gs.HpLeft[i].ToString("0") + "hp").ToArray()); }
        }
        T(g, sim, f8, sim.Contains("breaks") ? Bad : Dim, r.X, y, r.Width); y += 12;
      }
      // how good the guesses have been + what HP numbers mean
      if (y < r.Bottom - 11) {
        string acc = gs.ErrN > 0 ? "guess accuracy: off by " + gs.ErrAvg.ToString("0") + "px on average (" + gs.ErrN + " came back into view)" : "guess accuracy: no hidden zombie has come back yet";
        if (showTruth && gs.LiveN > 0) acc += " | now " + gs.LiveErr.ToString("0") + "px vs memory";
        T(g, acc, f8, Dim, r.X, y, r.Width); y += 12;
      }
      if (y < r.Bottom - 11 && gs.ProMode && !gs.HealthVisible) T(g, "zombie hp '~N' = judged from visible damage (armour, arm), not read exactly", f8, Dim, r.X, y, r.Width);
      if (gs.Error.Length > 0 && y < r.Bottom - 11) T(g, "guess error: " + gs.Error, f8, Bad, r.X, r.Bottom - 12, r.Width);
    }

    // ---------- the mini lawn ----------
    void DrawLawn(Graphics g, Rectangle area, Guess.Snap gs) {
      int rowsN = Math.Max(1, Math.Min(6, gs.RowsN)), labW = 16, stripW = Math.Min(92, Math.Max(60, area.Width / 7)), legendH = 15;
      float x0 = area.X + labW, y0 = area.Y;
      float gridW = area.Width - labW - stripW - 4, gridH = area.Height - legendH - 2;
      float cw = gridW / 9f, rh = gridH / rowsN;
      Func<float, float> X2P = zx => x0 + (zx - 10) / 80f * cw;   // zombie x -> panel x (cell col = (x - 10) / 80)
      var grid = new RectangleF(x0, y0, 9 * cw, rowsN * rh);

      // cells
      for (int r = 0; r < rowsN; r++)
        for (int c = 0; c < 9; c++) {
          int rt = gs.RowType[r]; bool odd = (r + c) % 2 == 0;
          var baseC = rt == 1 ? (odd ? Color.FromArgb(30, 70, 38) : Color.FromArgb(36, 82, 44))
                    : rt == 2 ? (odd ? Color.FromArgb(28, 60, 96) : Color.FromArgb(34, 70, 110))
                    : rt == 3 ? (odd ? Color.FromArgb(92, 52, 40) : Color.FromArgb(106, 60, 46))
                    : Color.FromArgb(52, 40, 30);
          g.FillRectangle(B(baseC), x0 + c * cw, y0 + r * rh, cw - 1, rh - 1);
        }
      // fog (where zombies are seen to come out of it)
      if (gs.FogLevel) {
        if (gs.FogEdgeX >= 0) {
          float fx = Math.Max(x0, Math.Min(grid.Right, X2P(gs.FogEdgeX)));
          using (var hb = new HatchBrush(HatchStyle.WideDownwardDiagonal, A(55, FogC), A(25, FogC)))
            g.FillRectangle(hb, fx, y0, grid.Right - fx, grid.Height);
          g.DrawLine(P(A(140, FogC), 1, true), fx, y0, fx, grid.Bottom);
          T(g, "FOG", f8b, FogC, fx + 3, y0 + 1, grid.Right - fx);
        } else T(g, "fog: edge not seen yet", f8, FogC, grid.Right - 140, y0 + 1, 140);
      }
      // graves / craters
      foreach (int k in gs.Blocked) {
        int br = k / 10, bcol = k % 10; if (br >= rowsN) continue;
        var gc = new RectangleF(x0 + bcol * cw + cw * 0.22f, y0 + br * rh + rh * 0.15f, cw * 0.56f, rh * 0.65f);
        g.FillRectangle(B(Color.FromArgb(120, 125, 135)), gc);
        g.DrawRectangle(P(Color.FromArgb(60, 60, 70), 1.5f), gc.X, gc.Y, gc.Width, gc.Height);
        CenterLabel(g, "RIP", gc);
      }
      // vases: heat = P(zombie); the planner's named vase gets a gold frame
      foreach (var kv in gs.Vases) {
        int vr = kv.Key / 10, vc = kv.Key % 10; if (vr < 0 || vr >= rowsN || vc < 0 || vc > 8) continue;
        double pz = kv.Value[0], pp = kv.Value[1]; int lk = (int)kv.Value[2];
        var vb = new RectangleF(x0 + vc * cw + 3, y0 + vr * rh + 3, cw - 7, rh - 7);
        g.FillRectangle(B(A(230, pz < 0 ? Color.FromArgb(110, 110, 120) : Heat(pz))), vb);
        g.DrawRectangle(P(Color.FromArgb(200, 20, 20, 20), 1), vb.X, vb.Y, vb.Width, vb.Height);
        bool pick = kv.Key == gs.PickCell;
        if (pz >= 0) {
          CenterLabel(g, "Z" + Pct(pz), new RectangleF(vb.X, vb.Y + vb.Height * 0.2f, vb.Width, vb.Height * 0.36f));
          CenterLabel(g, "P" + Pct(pp), new RectangleF(vb.X, vb.Y + vb.Height * 0.54f, vb.Width, vb.Height * 0.36f), f8, Color.FromArgb(220, 255, 220));
        } else CenterLabel(g, "vase", vb);
        if (lk == 1) g.FillEllipse(B(Color.FromArgb(70, 220, 90)), vb.X + 2, vb.Y + 2, 8, 6);                      // leaf look
        else if (lk == 2) { g.FillEllipse(Brushes.White, vb.X + 2, vb.Y + 2, 8, 8); g.FillRectangle(Brushes.Black, vb.X + 4, vb.Y + 5, 1.5f, 1.5f); g.FillRectangle(Brushes.Black, vb.X + 7, vb.Y + 5, 1.5f, 1.5f); }   // skull look
        if (pick) {   // the planner's named vase: gold frame + tag inside the top of the vase
          g.DrawRectangle(P(Gold, 3), vb.X - 1, vb.Y - 1, vb.Width + 2, vb.Height + 2);
          var tag = new RectangleF(vb.X + 10, vb.Y + 1, vb.Width - 11, 12);
          g.FillRectangle(B(Gold), tag);
          CenterLabel(g, gs.PickWait ? "next" : "NEXT", tag, f8b, Color.Black);
        }
      }
      if (gs.PickCell >= 0 && !gs.Vases.ContainsKey(gs.PickCell) && gs.PickCell / 10 < rowsN) {   // the planner's pick was just opened
        int pr = gs.PickCell / 10, pc = gs.PickCell % 10;
        g.DrawRectangle(P(A(200, Gold), 2, true), x0 + pc * cw + 2, y0 + pr * rh + 2, cw - 5, rh - 5);
        CenterLabel(g, "opened", new RectangleF(x0 + pc * cw, y0 + pr * rh + rh - 14, cw, 12), f8, Gold);
      }
      // danger lanes glow and pulse red (faster and stronger the worse the lane is)
      var ld = Bot.LaneDanger;
      for (int lr = 0; lr < rowsN && lr < ld.Length; lr++) {
        double dg = ld[lr]; if (dg < 0.8) continue;
        double pul = 0.5 + 0.5 * Math.Sin(Anim * (3 + Math.Min(6, dg)) + lr);
        int al = (int)(Math.Min(110, 25 + 25 * dg) * (0.45 + 0.55 * pul));
        g.FillRectangle(B(A(al, Bad)), x0, y0 + lr * rh, cw * 9, rh);
        g.FillRectangle(B(A(Math.Min(255, al * 2), Bad)), x0, y0 + lr * rh, 4, rh);
      }
      // plants: pads / pots flat at the bottom, pumpkins as a ring, everything else a disc
      foreach (var p in gs.Plants) {
        if (p.Row < 0 || p.Row >= rowsN) continue;
        float cx = x0 + p.Col * cw, cy = y0 + p.Row * rh;
        if (p.Type == 16 || p.Type == 33) { g.FillEllipse(B(p.Type == 16 ? Color.FromArgb(60, 160, 70) : Color.FromArgb(170, 95, 60)), cx + cw * 0.08f, cy + rh * 0.66f, cw * 0.84f, rh * 0.24f); continue; }
        if (p.Type == 30) { g.DrawEllipse(P(Color.FromArgb(240, 140, 30), 3), cx + cw * 0.05f, cy + rh * 0.06f, cw * 0.9f, rh * 0.76f); continue; }
        float sway = (float)(Math.Sin(Anim * 2.2 + p.Col * 0.9 + p.Row) * 1.5);   // plants gently sway
        var disc = new RectangleF(cx + cw * 0.12f + sway, cy + rh * 0.1f, cw * 0.76f, rh * 0.64f);
        g.FillEllipse(B(PlantColor(p.Type)), disc);
        g.DrawEllipse(P(Color.FromArgb(160, 0, 0, 0), 1.5f), disc);
        string nm = Mem.SeedName(p.Type);
        CenterLabel(g, nm.Substring(0, Math.Min(cw > 46 ? 5 : 4, nm.Length)), disc);
        if (p.MaxHp > 0) g.FillRectangle(B(Lerp(Bad, Good, p.Hp / (double)p.MaxHp)), disc.X, cy + rh - 6, disc.Width * Math.Max(0, Math.Min(1, p.Hp / (float)p.MaxHp)), 3);
      }
      double fl = (DateTime.Now - flashAt).TotalSeconds;
      if (flashRow >= 0 && flashRow < rowsN && fl < 1.2)
        g.DrawRectangle(P(Color.FromArgb((int)(255 * (1 - fl / 1.2)), Gold), 3), x0 + flashCol * cw - 2, y0 + flashRow * rh - 2, cw + 3, rh + 3);
      // suns, coins and dropped seed packets (with seconds before they fade)
      float ySc = rowsN == 6 ? 85f : 100f;
      foreach (var c in gs.Coins) {
        float cx2 = x0 + (c.X - 40) / 80f * cw, cy2 = y0 + (c.Y - 80) / ySc * rh;
        if (c.Packet) {
          g.FillRectangle(Brushes.White, cx2 - 5, cy2 - 7, 10, 14); g.DrawRectangle(Pens.DarkGreen, cx2 - 5, cy2 - 7, 10, 14);
          CenterLabel(g, Mem.SeedName(c.Seed) + (c.Left >= 0 ? " " + c.Left.ToString("0") + "s" : ""), new RectangleF(cx2 + 6, cy2 - 7, 70, 12), f8b, c.Left >= 0 && c.Left < 2.5 ? Bad : Color.White);
        }
        else if (c.Type >= 11 && c.Type <= 13) g.FillEllipse(B(A(220, Gold)), cx2 - 5, cy2 - 5, 10, 10);
        else g.FillEllipse(B(Color.FromArgb(200, 200, 205, 215)), cx2 - 4, cy2 - 4, 8, 8);
      }
      g.SetClip(grid);   // zombie labels never spill out of the lawn
      float zw = Math.Max(8, cw * 0.18f);
      // blackout: dim the lawn first, so the guesses drawn next stay readable on top of it
      if (gs.Dark) {
        g.FillRectangle(B(Color.FromArgb(160, 0, 0, 0)), grid);
        string bt = "BLACKOUT - the bot is blind" + (gs.Ghosts.Count > 0 ? ", guessing " + gs.Ghosts.Count + " zombie" + (gs.Ghosts.Count == 1 ? "" : "s") : "");
        float bw = Math.Min(grid.Width - 8, bt.Length * CW(g, f11) + 16);
        var banner = new RectangleF(grid.X + (grid.Width - bw) / 2, grid.Y + 2, bw, 22);
        g.FillRectangle(B(Color.FromArgb(220, 90, 20, 20)), banner);
        g.DrawRectangle(P(Gold, 1.5f), banner.X, banner.Y, banner.Width, banner.Height);
        CenterLabel(g, Fit(bt, (int)(bw / CW(g, f11))), banner, f11, Gold);
      }
      // guessed zombies: dashed outline + "?", band = how unsure the position is, dotted line back to where it was last seen
      foreach (var gh in gs.Ghosts) {
        if (gh.Row < 0 || gh.Row >= rowsN) continue;
        int al = (int)(70 + 185 * gh.Conf); var gc = A(al, GuessC);
        float ry = y0 + gh.Row * rh, gx = Math.Max(x0, Math.Min(grid.Right - zw, X2P(gh.EstX))), gy = ry + rh * 0.22f, gHt = rh * 0.52f;
        float b0 = Math.Max(x0, X2P(gh.EstX - gh.Band)), b1 = Math.Min(grid.Right, X2P(gh.EstX + gh.Band) + zw);
        g.FillRectangle(B(A(al / 3, GuessC)), b0, ry + rh * 0.80f, Math.Max(2, b1 - b0), 4);
        float lx = Math.Max(x0, Math.Min(grid.Right - 4, X2P(gh.LastX)));
        if (Math.Abs(lx - gx) > 3) g.DrawLine(P(A(al / 2, GuessC), 1, true), gx + zw / 2, gy + gHt / 2, lx + 3, gy + gHt / 2);
        g.DrawEllipse(P(A(al, GuessC), 1), lx, gy + gHt / 2 - 3, 6, 6);   // last seen here
        g.FillRectangle(B(A(al / 4, GuessC)), gx, gy, zw, gHt);
        g.DrawRectangle(P(gc, 1.6f, true), gx, gy, zw, gHt);
        CenterLabel(g, "?", new RectangleF(gx - 6, gy - 13, zw + 12, 12), f9b, gc);
        string glab = gh.Name + "? " + Pct(gh.Conf); float glw = glab.Length * CW(g, f8) + 2;
        g.DrawString(glab, f8, B(gc), gx + zw + 2 + glw > grid.Right ? gx - glw - 2 : gx + zw + 2, gy + gHt - 11);
        if (showTruth && !float.IsNaN(gh.TrueX)) {   // where the game really has it (never shown to the bot)
          float tx = Math.Max(x0, Math.Min(grid.Right - 4, X2P(gh.TrueX)));
          g.DrawLine(P(Color.FromArgb(200, 255, 255, 255), 1.5f), tx, gy + 2, tx + 6, gy + 8); g.DrawLine(P(Color.FromArgb(200, 255, 255, 255), 1.5f), tx + 6, gy + 2, tx, gy + 8);
        }
      }
      // seen zombies: solid; labels alternate top / bottom of the lane so neighbours don't overlap
      foreach (var row in gs.Zombies.Where(z => z.Row >= 0 && z.Row < rowsN && z.X <= 840).GroupBy(z => z.Row)) {
        int i = 0;
        foreach (var z in row.OrderBy(q => q.X)) {
          float ry = y0 + z.Row * rh, zx = Math.Max(x0, Math.Min(grid.Right - zw, X2P(z.X))), zy = ry + rh * 0.22f + (z.Eating ? 0 : (float)(Math.Abs(Math.Sin(Anim * 7 + z.X * 0.05)) * -3));   // zombies bob as they walk
          var zc = z.Charmed ? Purple : z.Offscreen ? A(110, Bad) : Bad;
          g.FillRectangle(B(zc), zx, zy, zw, rh * 0.52f);
          if (z.Eating) { g.FillRectangle(B(Gold), zx, zy, zw, 3); if ((int)(Anim * 8) % 2 == 0) g.DrawRectangle(P(Gold, 1.5f), zx - 1, zy - 1, zw + 2, rh * 0.52f + 2); }   // chomping: blinks
          if (z.Offscreen) continue;   // still off the right edge: not seen yet
          string lab = z.Name + (z.Hp >= 0 ? (gs.HealthVisible ? " " : " ~") + z.Hp.ToString("0") : "") + (z.Charmed ? " ally" : "");
          float lw = lab.Length * CW(g, f8) + 2, lxp = zx + zw + 2 + lw > grid.Right ? zx - lw - 2 : zx + zw + 2, lyp = i % 2 == 0 ? ry + 1 : ry + rh - 24;
          g.FillRectangle(B(Color.FromArgb(150, 10, 12, 16)), lxp, lyp + 1, lw, 12);
          g.DrawString(lab, f8, B(z.Charmed ? Purple : Color.FromArgb(255, 175, 165)), lxp, lyp);
          i++;
        }
      }
      g.SetClip(area);
      // lane labels and mowers
      for (int r = 0; r < rowsN; r++) {
        float ry = y0 + r * rh;
        g.DrawString(r.ToString(), f8, B(Dim), area.X, ry + 1);
        int m = gs.Mowers[r]; float my = ry + rh * 0.45f;
        if (m == 1 || m == 0) g.FillRectangle(B(Color.FromArgb(220, 70, 200, 90)), area.X + 1, my, labW - 5, rh * 0.3f);
        else if (gs.RowType[r] != 0) { var pn = P(Bad, 2); g.DrawLine(pn, area.X + 1, my, area.X + labW - 4, my + rh * 0.3f); g.DrawLine(pn, area.X + labW - 4, my, area.X + 1, my + rh * 0.3f); }
      }
      // per-lane strip: danger now (bar) + the 40 s simulator's verdict
      float sx = grid.Right + 6, sw = area.Right - sx;
      for (int r = 0; r < rowsN; r++) {
        float ry = y0 + r * rh;
        double d = gs.Danger[r];
        var bar = new RectangleF(sx, ry + rh * 0.12f, sw, Math.Max(10, rh * 0.3f));
        g.FillRectangle(B(Track), bar);
        g.FillRectangle(B(Lerp(Good, Bad, d / 2)), bar.X, bar.Y, (float)(bar.Width * Math.Min(1, d / 3)), bar.Height);
        g.DrawString((sw >= 66 ? "danger " : "") + d.ToString("0.0"), f8, B(Txt), bar.X + 1, bar.Y + (bar.Height - 12) / 2);
        var sb = new RectangleF(sx, ry + rh * 0.50f, sw, Math.Max(12, rh * 0.32f));
        double bt = gs.BreachT[r];
        if (!gs.SimKnown || bt < 0) g.DrawString("sim -", f8, B(Dim), sb.X + 1, sb.Y + (sb.Height - 12) / 2);
        else if (bt < 9000) { g.FillRectangle(B(Color.FromArgb(200, 170, 30, 30)), sb); CenterLabel(g, (sw >= 70 ? "BREAK " : "BRK ") + bt.ToString("0") + "s", sb, f8b, Color.White); }
        else if (gs.SimExact && gs.HpLeft[r] > 50) { g.FillRectangle(B(Color.FromArgb(150, 150, 100, 20)), sb); CenterLabel(g, (sw >= 78 ? "leaks " : "") + gs.HpLeft[r].ToString("0") + "hp", sb, f8b, Color.White); }
        else g.DrawString(sw >= 70 ? "holds 40s" : "holds", f8, B(A(200, Good)), sb.X + 1, sb.Y + (sb.Height - 12) / 2);
      }
      // legend
      float lgx = area.X, lgy = grid.Bottom + 3, lmax = area.Right - 96;
      Action<string, Action<float>> item = (label, glyph) => {
        if (lgx + 14 + label.Length * CW(g, f8) > lmax) return;
        glyph(lgx); lgx += 14; g.DrawString(label, f8, B(Dim), lgx, lgy); lgx += label.Length * CW(g, f8) + 10;
      };
      item("seen", gx => g.FillRectangle(B(Bad), gx + 3, lgy + 1, 6, 11));
      item("guess (fades)", gx => { g.DrawRectangle(P(GuessC, 1.4f, true), gx + 2, lgy + 1, 7, 11); });
      item("ally", gx => g.FillRectangle(B(Purple), gx + 3, lgy + 1, 6, 11));
      if (gs.Vases.Count > 0) {
        item("vase P(zombie) low-high", gx => { for (int i = 0; i < 3; i++) g.FillRectangle(B(Heat(i / 2.0)), gx + i * 4, lgy + 1, 4, 11); });
        item("planner's pick", gx => g.DrawRectangle(P(Gold, 2), gx + 1, lgy + 2, 10, 9));
      }
      if (gs.FogLevel) item("fog", gx => { using (var hb = new HatchBrush(HatchStyle.WideDownwardDiagonal, A(120, FogC), A(40, FogC))) g.FillRectangle(hb, gx, lgy + 1, 12, 11); });
    }
    void Spark(Graphics g, Rectangle r, int[] data, Color c, string label) {
      g.FillRectangle(B(BoxBg), r);
      g.DrawString(label, f8, B(Dim), r.X + 3, r.Y + 2);
      if (data.Length < 2) return;
      int mx = Math.Max(1, data.Max());
      var pts = data.Select((v, i) => new PointF(r.X + i * (r.Width - 1) / (float)Math.Max(1, data.Length - 1), r.Bottom - 2 - v * (r.Height - 16) / (float)mx)).ToArray();
      var fill = pts.Concat(new[] { new PointF(pts[pts.Length - 1].X, r.Bottom), new PointF(pts[0].X, r.Bottom) }).ToArray();
      g.FillPolygon(B(Color.FromArgb(50, c)), fill);
      g.DrawLines(P(c, 1.5f), pts);
      string last = data[data.Length - 1].ToString();
      g.DrawString(last, f8, B(c), r.Right - last.Length * CW(g, f8) - 6, r.Y + 2);
    }
  }
}