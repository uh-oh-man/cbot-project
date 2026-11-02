// Live "bot brain" panel: a non-activating window docked under the game, drawn from the bot's real data.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
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

  public static void Publish(State s) { lock (L) state = s; }

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
      try { System.IO.File.AppendAllText(@"C:\cbot\events.log", line + Environment.NewLine); } catch { }
    }
  }
  // Same as AddEvent but at most once per 20s for the same text prefix.
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
      timer.Interval = 100;   // 10 fps is plenty for the panel; leaves CPU for the bot loop
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
    protected override void OnMouseDown(MouseEventArgs e) {
      foreach (var b in speedButtons)
        if (b.Item1.Contains(e.Location)) {
          try { System.IO.File.WriteAllText(@"C:\cbot\speed.txt", b.Item2.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
          AddEvent("speed button pressed: x" + b.Item2);
        }
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
      var want = new Rectangle(wa.Left, r.B, wa.Width, Math.Max(200, wa.Bottom - r.B));
      if (Bounds != want) Bounds = want;
    }

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
    // White text with a dark outline, centred in the box.
    void CenterLabel(Graphics g, string s, RectangleF box) {
      var sz = g.MeasureString(s, f8b);
      float x = box.X + (box.Width - sz.Width) / 2, y = box.Y + (box.Height - sz.Height) / 2;
      var shadow = new SolidBrush(Color.FromArgb(230, 0, 0, 0));
      for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) if (dx != 0 || dy != 0) g.DrawString(s, f8b, shadow, x + dx, y + dy);
      g.DrawString(s, f8b, Brushes.White, x, y);
    }

    static Color Lerp(Color a, Color b, double t) {
      t = Math.Max(0, Math.Min(1, t));
      return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    // A paint error must never kill the panel (WinForms stops repainting a control after one), so draw defensively.
    protected override void OnPaint(PaintEventArgs e) {
      try { PaintPanel(e); }
      catch (Exception ex) { try { e.Graphics.DrawString("panel glitch: " + ex.Message, f8, Brushes.OrangeRed, 4, 4); } catch { } }
    }

    // ---------- layout helpers ----------
    static readonly Color BoxBg = Color.FromArgb(22, 28, 36), BoxEdge = Color.FromArgb(44, 54, 66);
    // A titled box; returns the inner content rectangle and clips drawing to it.
    Rectangle Box(Graphics g, Rectangle r, string title, Color accent) {
      g.ResetClip();
      g.FillRectangle(new SolidBrush(BoxBg), r);
      g.DrawRectangle(new Pen(BoxEdge), r);
      g.FillRectangle(new SolidBrush(accent), r.X, r.Y, 3, 16);
      g.DrawString(title, f8b, new SolidBrush(accent), r.X + 7, r.Y + 2);
      var inner = new Rectangle(r.X + 6, r.Y + 18, r.Width - 12, r.Height - 22);
      g.SetClip(inner);
      return inner;
    }
    static string Fit(string s, int max) { return s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 1)) + "~"; }

    void PaintPanel(PaintEventArgs e) {
      var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
      State s; List<Cand> cs; Cand ch; string sav; string[] ev; double[] rx; int[] sh, dh;
      lock (L) { s = state; cs = cands.ToList(); ch = chosen; sav = saving; ev = events.ToArray(); rx = reacts.ToArray(); sh = sunHist.ToArray(); dh = dpsHist.ToArray(); }
      int W = ClientSize.Width, H = ClientSize.Height;
      var dim = new SolidBrush(Color.FromArgb(120, 140, 160)); var txt = new SolidBrush(Color.FromArgb(220, 230, 240));
      var green = Color.FromArgb(90, 220, 120); var red = Color.FromArgb(240, 80, 70); var gold = Color.FromArgb(255, 210, 60);
      var cyan = Color.FromArgb(90, 210, 230); var orange = Color.FromArgb(255, 160, 90); var blue = Color.FromArgb(140, 200, 255); var purple = Color.FromArgb(200, 150, 255);
      const int pad = 6, topH = 50;

      // ===== top bar: level, progress with flags, scoreboard, speed =====
      g.FillRectangle(new SolidBrush(Color.FromArgb(18, 23, 30)), 0, 0, W, topH);
      string hdr = s == null || !s.HasBoard ? "waiting for game" : string.Format("LEVEL {0}-{1}  (attempt {5})    WAVE {2}/{3}    SUN {4}", (s.Level - 1) / 10 + 1, (s.Level - 1) % 10 + 1, s.Wave, s.Waves, s.Sun, Learn.DeathsHere + 1);
      g.DrawString(hdr, f11, new SolidBrush(gold), pad, 4);
      // progress bar with flag markers (what the in-game progress meter shows)
      var pb = new Rectangle(pad, 26, Math.Min(380, W / 3), 12);
      g.FillRectangle(new SolidBrush(Color.FromArgb(34, 42, 52)), pb);
      if (s != null && s.Waves > 0) {
        g.FillRectangle(new SolidBrush(Color.FromArgb(120, 200, 90)), pb.X, pb.Y, (float)(pb.Width * Math.Min(1, (double)s.Wave / s.Waves)), pb.Height);
        for (int f = 10; f <= s.Waves; f += 10) {
          float fx = pb.X + pb.Width * f / (float)s.Waves - 4;
          g.FillRectangle(new SolidBrush(red), fx, pb.Y - 3, 3, pb.Height + 6);
          g.FillPolygon(new SolidBrush(red), new[] { new PointF(fx + 3, pb.Y - 3), new PointF(fx + 10, pb.Y + 1), new PointF(fx + 3, pb.Y + 5) });
        }
        int tf = Bot.WavesToFlag(s);
        string prog = (tf < 0 ? "no flags left" : tf == 0 ? "FLAG WAVE NOW" : "next flag in " + tf + " wave" + (tf == 1 ? "" : "s")) + "   " + (100.0 * s.Wave / s.Waves).ToString("0") + "% done";
        g.DrawString(prog, f8b, tf >= 0 && tf <= 1 ? new SolidBrush(red) : dim, pb.Right + 8, pb.Y - 1);
      }
      // scoreboard (uptime: since this bot process started = since the last update; total since the project began)
      string score = Bot.ProMode && Bot.ProDeaths >= 0
        ? string.Format("PRO  DEATHS {0}   WINS {1}   ROUNDS {2}", Bot.ProDeaths, Bot.ProWins, Bot.ProDeaths + Bot.ProWins)
        : string.Format("DEATHS {0}   STREAK {1} (best {2})   ROUNDS {3}", Learn.Deaths, Learn.Streak, Learn.BestStreak, Learn.Deaths + Learn.Wins);
      var scoreSz = g.MeasureString(score, f11);
      int speedW = Speeds.Length * 38;
      float scoreX = Math.Max(pb.Right + 230, W - speedW - scoreSz.Width - 20);
      g.DrawString(score, f11, new SolidBrush(Learn.Streak > 0 ? green : red), scoreX, 4);
      g.DrawString(Status + string.Format("   {0:0} Hz / {1:0.0} ms   up {2} (total {3})", LoopHz, ReadMs, Dur(DateTime.Now - Started), Dur(DateTime.Now - ProjectStart)), f8, dim, scoreX, 27);
      // Hall of Fame (a promise kept): the mod author beat 4-5 before the bot did.
      g.DrawString("HALL OF FAME: darthspiker - the Proer Pro (beat 4-5 before the bot)", f8, new SolidBrush(Color.Gold), pb.X, 40);
      speedButtons.Clear();
      for (int i = 0; i < Speeds.Length; i++) {
        var rb = new Rectangle(W - speedW - pad + i * 38, 14, 35, 20);
        bool on = Math.Abs(Bot.UserSpeed - Speeds[i]) < 0.01;
        g.FillRectangle(new SolidBrush(on ? Color.FromArgb(255, 160, 40) : Color.FromArgb(40, 50, 62)), rb);
        g.DrawRectangle(new Pen(Color.FromArgb(90, 110, 130)), rb);
        CenterLabel(g, Speeds[i] + "x", rb);
        speedButtons.Add(Tuple.Create(rb, Speeds[i]));
      }
      g.DrawString(Bot.SlowMo ? "SLOW-MO x" + Bot.Speed.ToString("0.#") + " (danger)" : "speed", f8b, Bot.SlowMo ? new SolidBrush(red) : dim, W - speedW - pad, 1);

      // ===== columns =====
      int bodyY = topH + pad, bodyH = H - bodyY - pad;
      int colA = (int)(W * 0.34), colB = (int)(W * 0.33), colC = W - colA - colB - 4 * pad;
      int xA = pad, xB = xA + colA + pad, xC = xB + colB + pad;

      // --- LAWN ---
      var lawn = Box(g, new Rectangle(xA, bodyY, colA, bodyH), "LAWN   (mowers | plants & zombies | lane danger)", green);
      if (s != null && s.HasBoard) DrawLawn(g, lawn, s, txt, dim, green, red, gold);

      // --- STRATEGY ---
      int stratH = 60, knowH = Math.Max(110, bodyH * 2 / 5), decH = bodyH - stratH - knowH - 2 * pad;
      var sr = Box(g, new Rectangle(xB, bodyY, colB, stratH), Bot.ProMode ? "PRO MODE   (expert brain - not learning)" : "STRATEGY " + Strategy.Cur.Id + "  (" + Strategy.Cur.Env + ", gen " + Strategy.Generation + ")", orange);
      g.DrawString(Bot.ProMode ? Fit("Pro record: " + Bot.ProWins + " wins / " + Bot.ProDeaths + " deaths", 70) : Fit("'" + Strategy.Cur.Name + "'  avg " + Strategy.Cur.Avg.ToString("0") + " over " + Strategy.Cur.Runs + " runs", 70), f9b, txt, sr.X, sr.Y);
      if (Bot.ProMode) {
        g.DrawString(Fit("playbook: fixed expert rules for this area (no learning, no saved knowledge)", 95), f8, dim, sr.X, sr.Y + 14);
        g.DrawString(Fit(Bot.ProStatus, 95), f8, new SolidBrush(orange), sr.X, sr.Y + 27);
      } else {
        g.DrawString(Fit(Strategy.Cur.Genes, 95), f8, dim, sr.X, sr.Y + 14);
        g.DrawString(Fit("post-mortem: " + Diag.Summary(), 95), f8, new SolidBrush(orange), sr.X, sr.Y + 27);
      }

      // --- DECISIONS ---
      var dr = Box(g, new Rectangle(xB, bodyY + stratH + pad, colB, decH), "DECISIONS   (candidate moves, best first)", blue);
      int y = dr.Y;
      if (sav != null) g.DrawString("SAVING: " + sav, f9b, new SolidBrush(gold), dr.X, y);
      else if (ch != null) g.DrawString("DOING: " + ch.Label + " - " + ch.Why, f9b, new SolidBrush(green), dr.X, y);
      else if (VasePlanner.Active && VasePlanner.LastWhy.Length > 0) g.DrawString(Fit("VASES: " + VasePlanner.LastWhy, 95), f9b, new SolidBrush(green), dr.X, y);   // vase levels: the action is breaking vases
      else g.DrawString("nothing worth planting right now", f9, dim, dr.X, y);
      y += 17;
      double top = cs.Count > 0 ? Math.Max(1, cs[0].Score) : 1;
      int barX = dr.X + 112, barW = Math.Max(40, dr.Width / 3);
      foreach (var c in cs) {
        if (y > dr.Bottom - 13) break;
        bool isCh = ch != null && c.Label == ch.Label;
        var col = isCh ? green : c.Affordable ? Color.FromArgb(70, 130, 200) : Color.FromArgb(90, 90, 110);
        g.DrawString(c.Label, isCh ? f8b : f8, c.Affordable ? txt : dim, dr.X, y);
        g.FillRectangle(new SolidBrush(Color.FromArgb(isCh ? 210 : 150, col)), barX, y + 2, (float)(barW * Math.Max(0, c.Score) / top), 10);
        g.DrawString(c.Score.ToString("0") + (c.Affordable ? "" : "$"), f8, txt, barX + 2, y);
        g.DrawString(c.Why, f8, dim, barX + barW + 6, y);
        y += 14;
      }

      // --- KNOWLEDGE ---
      var kr = Box(g, new Rectangle(xB, bodyY + stratH + decH + 2 * pad, colB, knowH), Bot.ProMode ? "PRO KNOWLEDGE   (fixed expert rules + live record)" : string.Format("KNOWLEDGE   ({0} lessons, caution x{1:0.00})", Learn.Lessons, Learn.Caution), cyan);
      int ky = kr.Y;
      if (Bot.ProMode) {   // pro mode does not learn: show its record, the super nemesis and the live gambit state instead
        foreach (var line in Bot.ProKnowledge()) { if (ky > kr.Bottom - 11) break; g.DrawString(Fit(line, 95), f8, line.StartsWith("DARTH") ? new SolidBrush(Color.Gold) : txt, kr.X, ky); ky += 13; }
      } else {
      string mine = Learn.MineOk + Learn.MineFail == 0 ? "untested" : Learn.MineOk + " boom / " + Learn.MineFail + " eaten";
      g.DrawString("mine arm time " + Learn.MineArm.ToString("0.0") + "s (" + mine + ")    sky sun " + (Learn.SkyRateNow < 0 ? "?" : Learn.SkyRateNow.ToString("0.0")) + "/min", f8, txt, kr.X, ky); ky += 13;
      string dps; try { dps = string.Join("  ", Learn.DpsLearned.ToList().Select(d => Mem.SeedName(d.Key) + " " + d.Value.ToString("0"))); } catch { dps = "..."; }
      g.DrawString("dps/plant: " + (dps.Length > 0 ? dps : "measuring..."), f8, txt, kr.X, ky); ky += 13;
      string roles; try { roles = string.Join("  ", Roles.Learned.ToList().Select(r => Mem.SeedName(r.Key) + "=" + r.Value.ToLower())); } catch { roles = ""; }
      if (roles.Length > 0) { g.DrawString("discovered: " + roles, f8, new SolidBrush(cyan), kr.X, ky); ky += 13; }
      string plans; try { plans = string.Join("  ", Strategy.Loadout.Where(Roles.Tentative).Select(sd => Mem.SeedName(sd) + "=" + Roles.RoleOf(sd).ToLower() + "?")); } catch { plans = ""; }
      if (plans.Length > 0) { g.DrawString("from descriptions (unconfirmed): " + plans, f8, new SolidBrush(Color.FromArgb(255, 200, 120)), kr.X, ky); ky += 13; }
      g.DrawString(string.Format("{0,-18}{1,5}{2,6}{3,7}{4,6}", "zombie kind", "seen", "kills", "tough", "speed"), f8, dim, kr.X, ky); ky += 12;
      List<ZKnow> zks; try { zks = Learn.Z.Values.ToList(); } catch { zks = new List<ZKnow>(); }   // bot thread may be adding kinds
      foreach (var k in zks.OrderByDescending(v => v.Seen)) {
        if (ky > kr.Bottom - 11) break;
        g.DrawString(string.Format("{0,-18}{1,5}{2,6}{3,7}{4,6}", Fit(k.Key, 17), k.Seen, k.Kills, k.ToughN > 0 ? k.Tough.ToString("0") : "?", k.SpeedN >= 3 ? k.Speed.ToString("0.0") : "?"),
          f8, k.ToughN > 0 ? txt : new SolidBrush(Color.FromArgb(200, 160, 120)), kr.X, ky);
        ky += 11;
      }
      }

      // --- MOOD & STATS ---
      int moodH = 74, graphH = Math.Min(90, bodyH / 4);
      var mr = Box(g, new Rectangle(xC, bodyY, colC, moodH), "MOOD & STATS", purple);
      var fr = new Rectangle(mr.X, mr.Y, Math.Min(200, mr.Width / 3), 14);
      g.FillRectangle(new SolidBrush(Color.FromArgb(34, 42, 52)), fr);
      using (var lg = new LinearGradientBrush(fr, Color.FromArgb(120, 140, 160), Color.FromArgb(200, 120, 255), 0f))
        g.FillRectangle(lg, fr.X, fr.Y, (float)(fr.Width * Faith / 100), fr.Height);
      g.DrawString("FAITH " + Faith.ToString("0") + "%", f8b, Brushes.White, fr.X + 3, fr.Y + 1);
      g.DrawString(FaithText, f8b, new SolidBrush(purple), fr.Right + 8, fr.Y + 1);
      g.DrawString("NEMESIS: " + (Bot.ProMode ? Bot.SuperNemesis() : Quips.Nemesis), f9b, new SolidBrush(red), mr.X, mr.Y + 18);
      double avgR = rx.Length > 0 ? rx.Average() : 0;
      int dpsNow = dh.Length > 0 ? dh[dh.Length - 1] : 0;
      g.DrawString(string.Format("dps {0}  kills {1}  planted {2}  suns {3}  coins {4}  clicks {5}  reaction {6:0}ms  mowers lost {7}",
        dpsNow, Kills, Planted, SunClicks, CoinClicks, Clicks, avgR, MowersLost), f8, txt, mr.X, mr.Y + 36);

      // --- GRAPHS ---
      var gr = Box(g, new Rectangle(xC, bodyY + moodH + pad, colC, graphH), "GRAPHS   (last 2 minutes)", gold);
      g.ResetClip();
      Spark(g, new Rectangle(gr.X, gr.Y, gr.Width / 2 - 4, gr.Height), sh, gold, "sun bank");
      Spark(g, new Rectangle(gr.X + gr.Width / 2 + 4, gr.Y, gr.Width / 2 - 4, gr.Height), dh, red, "damage / sec");

      // --- EVENT LOG ---
      int logY = bodyY + moodH + graphH + 2 * pad;
      var lr = Box(g, new Rectangle(xC, logY, colC, H - logY - pad), "EVENT LOG   (newest first)", txt.Color);
      int ly = lr.Y;
      foreach (var line in ev) {
        if (ly > lr.Bottom - 12) break;
        var b = line.Contains("STRAT") || line.Contains("MENU") ? new SolidBrush(orange) : line.Contains("SAYS") || line.Contains("DAVE") ? new SolidBrush(Color.FromArgb(255, 230, 120))
              : line.Contains("FAITH") ? new SolidBrush(purple) : line.Contains("LEARN") ? new SolidBrush(cyan) : line.Contains("PLANT") || line.Contains("SHOVEL") ? new SolidBrush(green)
              : line.Contains("!") || line.Contains("DIED") || line.Contains("fail") || line.Contains("WATCHDOG") ? new SolidBrush(red) : line.Contains("WON") ? new SolidBrush(gold) : txt;
        g.DrawString(line, f8, b, lr.X, ly); ly += 12;
      }
      g.ResetClip();
    }

    void DrawLawn(Graphics g, Rectangle area, State s, SolidBrush txt, SolidBrush dim, Color green, Color red, Color gold) {
      int[] rt; try { rt = Mem.RowTypes(); } catch { rt = new int[6]; }
      int rowsN = (Bot.Bg == 2 || Bot.Bg == 3) ? 6 : 5, laneBar = 40, mowerW = 12;   // pool and fog lawns have 6 lanes
      int x0 = area.X + mowerW, my = area.Y;
      float cw = (area.Width - laneBar - mowerW - 6) / 9f, rh = area.Height / (float)rowsN;
      for (int r = 0; r < rowsN; r++)
        for (int c = 0; c < 9; c++) {
          var baseC = rt[r] == 1 ? ((r + c) % 2 == 0 ? Color.FromArgb(30, 70, 38) : Color.FromArgb(36, 82, 44))
                    : rt[r] == 2 ? ((r + c) % 2 == 0 ? Color.FromArgb(28, 60, 96) : Color.FromArgb(34, 70, 110))   // water lane
                    : Color.FromArgb(52, 40, 30);
          g.FillRectangle(new SolidBrush(baseC), x0 + c * cw, my + r * rh, cw - 1, rh - 1);
        }
      foreach (int k in s.Blocked) {   // graves / craters
        int br = k / 10, bcol = k % 10; if (br >= rowsN) continue;
        var gc = new RectangleF(x0 + bcol * cw + cw * 0.22f, my + br * rh + rh * 0.15f, cw * 0.56f, rh * 0.65f);
        g.FillRectangle(new SolidBrush(Color.FromArgb(120, 125, 135)), gc);
        g.DrawRectangle(new Pen(Color.FromArgb(60, 60, 70), 1.5f), gc.X, gc.Y, gc.Width, gc.Height);
        CenterLabel(g, "RIP", gc);
      }
      double fl = (DateTime.Now - flashAt).TotalSeconds;
      if (flashRow >= 0 && fl < 1.2)
        g.DrawRectangle(new Pen(Color.FromArgb((int)(255 * (1 - fl / 1.2)), gold), 3), x0 + flashCol * cw - 2, my + flashRow * rh - 2, cw + 3, rh + 3);
      foreach (var p in s.Plants) {
        if (p.Row >= rowsN) continue;
        var disc = new RectangleF(x0 + p.Col * cw + cw * 0.1f, my + p.Row * rh + rh * 0.1f, cw * 0.8f, rh * 0.68f);
        g.FillEllipse(new SolidBrush(PlantColor(p.Type)), disc);
        g.DrawEllipse(new Pen(Color.FromArgb(160, 0, 0, 0), 1.5f), disc);
        CenterLabel(g, Mem.SeedName(p.Type).Substring(0, Math.Min(5, Mem.SeedName(p.Type).Length)), disc);
        if (p.MaxHp > 0) g.FillRectangle(new SolidBrush(Lerp(red, green, p.Hp / (double)p.MaxHp)), disc.X, my + p.Row * rh + rh - 6, disc.Width * Math.Max(0, Math.Min(1, p.Hp / (float)p.MaxHp)), 3);
      }
      foreach (var z in s.Zombies) {
        if (z.Row < 0 || z.Row >= rowsN || z.X > 840) continue;
        float zx = Math.Max(x0, Math.Min(x0 + 9 * cw - 8, x0 + (z.X + 30 - 40) / 80f * cw)), zy = my + z.Row * rh + rh * 0.2f;
        g.FillRectangle(new SolidBrush(z.X < Bot.VISIBLE_X ? red : Color.FromArgb(110, red)), zx, zy, 6, rh * 0.55f);
        g.DrawString(Mem.ZomName(z.Type) + (Bot.HealthVisible ? " " + z.Total : ""), f8, new SolidBrush(Color.FromArgb(255, 170, 160)), zx + 7, zy - 2);
      }
      foreach (var c in s.Suns) {
        float cx2 = x0 + (c.X - 40) / 80f * cw, cy2 = my + (c.Y - 80) / ((Bot.Bg == 2 || Bot.Bg == 3) ? 85f : 100f) * rh;
        if (c.IsPacket) {
          g.FillRectangle(Brushes.White, cx2 - 5, cy2 - 7, 10, 14); g.DrawRectangle(Pens.DarkGreen, cx2 - 5, cy2 - 7, 10, 14);
          double left = Learn.PacketTimeLeft(c.Id);
          g.DrawString(Mem.SeedName(c.Seed) + (left >= 0 ? " " + left.ToString("0") + "s" : ""), f8, Brushes.White, cx2 + 7, cy2 - 6);
        }
        else if (c.Type >= 11 && c.Type <= 13) g.FillEllipse(new SolidBrush(Color.FromArgb(220, gold)), cx2 - 5, cy2 - 5, 10, 10);
        else g.FillEllipse(new SolidBrush(Color.FromArgb(200, 200, 205, 215)), cx2 - 4, cy2 - 4, 8, 8);
      }
      for (int r = 0; r < rowsN; r++) {   // mowers
        int m = s.Mowers[r]; float y0 = my + r * rh + rh * 0.3f;
        if (m == 1 || m == 0) g.FillRectangle(new SolidBrush(Color.FromArgb(220, 70, 200, 90)), area.X, y0, mowerW - 3, rh * 0.4f);
        else if (rt[r] == 1) { var pn = new Pen(red, 2); g.DrawLine(pn, area.X, y0, area.X + mowerW - 3, y0 + rh * 0.4f); g.DrawLine(pn, area.X + mowerW - 3, y0, area.X, y0 + rh * 0.4f); }
      }
      var ld = Bot.LaneDanger;   // lane danger meters
      for (int r = 0; r < rowsN; r++) {
        float bx = x0 + 9 * cw + 6, by = my + r * rh + rh * 0.25f, bw = laneBar - 6, bh = rh * 0.5f;
        g.FillRectangle(new SolidBrush(Color.FromArgb(34, 42, 52)), bx, by, bw, bh);
        double d = r < ld.Length ? ld[r] : 0;
        g.FillRectangle(new SolidBrush(Lerp(green, red, d / 2)), bx, by, (float)(bw * Math.Min(1, d / 3)), bh);
        if (d > 0.01) g.DrawString(d.ToString("0.0"), f8, txt, bx + 1, by + 1);
      }
    }
    void Spark(Graphics g, Rectangle r, int[] data, Color c, string label) {
      g.FillRectangle(new SolidBrush(Color.FromArgb(22, 28, 36)), r);
      g.DrawString(label, f8, new SolidBrush(Color.FromArgb(120, 140, 160)), r.X + 3, r.Y + 2);
      if (data.Length < 2) return;
      int mx = Math.Max(1, data.Max());
      var pts = data.Select((v, i) => new PointF(r.X + i * (r.Width - 1) / (float)Math.Max(1, data.Length - 1), r.Bottom - 2 - v * (r.Height - 16) / (float)mx)).ToArray();
      var fill = pts.Concat(new[] { new PointF(pts[pts.Length - 1].X, r.Bottom), new PointF(pts[0].X, r.Bottom) }).ToArray();
      g.FillPolygon(new SolidBrush(Color.FromArgb(50, c)), fill);
      g.DrawLines(new Pen(c, 1.5f), pts);
      g.DrawString(data[data.Length - 1].ToString(), f8, new SolidBrush(c), r.Right - 34, r.Y + 2);
    }
  }
}
