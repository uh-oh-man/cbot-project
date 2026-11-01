// PRO MODE: a separate, non-learning brain that plays from expert knowledge of the game.
// It sees exactly what the learning bot sees (the visible lawn, seed bank, sun, progress bar, zombie health bars)
// and uses the same hands (clicks, menus, sun collection). It never reads or writes learned knowledge.
// Expert facts come from the original game's documented stats (plantsvszombies.wiki.gg) and standard play.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public static partial class Bot {
  public static bool ProMode;
  // Mod author: after the football is dealt with, spend the remaining squashes on the last zombies.
  static bool SquashFree(State s) { return s.Vases.Count <= 3 && !s.Zombies.Any(z => z.X < VISIBLE_X && Mem.ZomName(z.Type) == "FOOT"); }
  public static string ProStatus = "";
  static double ProBreachT = 9999;

  // ---------- Expert knowledge (fixed, never learned) ----------
  // Seed ids (this build): 0 PEA 1 SUNF 2 CHERRY 3 WALL 4 MINE 5 SNOW 6 CHOMP 7 REPEAT 8 PUFF 9 SUNSH 10 FUME 11 GRAVE
  // 12 HYPNO 13 SCAREDY 14 ICE 15 DOOM 16 LILY 17 SQUASH 18 THREE 19 KELP 20 JALA 21 SPIKE 22 TORCH 23 TALL 24 SEASH
  // 25 LANTERN 26 CACTUS 27 BLOVER 28 SPLIT 29 STAR 30 PUMPKIN 31 MAGNET 32 CABBAGE 33 POT 34 KERNEL 35 COFFEE
  // 36 GARLIC 37 UMBRELLA 38 MARIGOLD 39 MELON 40 GATLING 41 TWINSUN 42 GLOOM 43 CATTAIL 44 WMELON 45 GMAGNET
  // 46 SPIKEROCK 47 COB
  const int PEA = 0, SUNF = 1, CHERRY = 2, WALLNUT = 3, PMINE = 4, SNOW = 5, CHOMP = 6, REPEAT = 7, PUFF = 8, SUNSH = 9,
            FUME = 10, GRAVEB = 11, HYPNO = 12, SCAREDY = 13, ICE = 14, DOOM = 15, LILY = 16, SQUASH = 17, THREE = 18,
            KELP = 19, JALA = 20, SPIKE = 21, TORCH = 22, TALL = 23, SEASH = 24, LANTERN = 25, CACTUS = 26, BLOVER = 27,
            SPLIT = 28, STAR = 29, PUMPKIN = 30, MAGNET = 31, CABBAGE = 32, POT = 33, KERNEL = 34, COFFEE = 35,
            UMBRELLA = 37, MELON = 39, GATLING = 40, TWINSUN = 41, GLOOM = 42, CATTAIL = 43, WMELON = 44, SPIKEROCK = 46;
  static readonly HashSet<int> ProNight = new HashSet<int> { PUFF, SUNSH, FUME, HYPNO, SCAREDY, ICE, DOOM, SEASH, MAGNET, GLOOM, 45 };
  static readonly HashSet<int> ProSun = new HashSet<int> { SUNF, SUNSH, TWINSUN };
  static readonly HashSet<int> ProLobbed = new HashSet<int> { CABBAGE, KERNEL, MELON, WMELON };
  static readonly HashSet<int> ProWall = new HashSet<int> { WALLNUT, TALL };
  // Damage per second against the lane (a pea = 20 damage, ~1.4 s between shots).
  static double ProDps(int t) {
    switch (t) {
      case PEA: case SNOW: case PUFF: case SCAREDY: case FUME: case SEASH: case CACTUS: case 50: return 14;
      case REPEAT: return 28;
      case THREE: return 14;          // per lane, covers three lanes
      case SPLIT: return 14;
      case STAR: return 14;
      case GATLING: return 56;
      case GLOOM: return 40;
      case CATTAIL: return 28;
      case CABBAGE: return 28;
      case KERNEL: return 18;
      case MELON: return 56;
      case WMELON: return 56;
      default: return 0;
    }
  }
  // How far ahead (px) a shooter reaches; 0 = whole lane.
  static double ProRange(int t) { return t == PUFF || t == SEASH ? 260 : t == FUME || t == GLOOM ? 340 : 0; }
  static bool ProIsShooter(int t) { return ProDps(t) > 0; }
  static bool ProIsPad(int t) { return t == LILY || t == POT; }
  // Zombie knowledge by this build's names: walking speed (px/s) and what counters it.
  static double ProSpeedOf(string n) {
    switch (n) {
      case "POLE": case "FOOT": case "BALLOON": case "CAT": case "CAT2": case "CAT3": case "DOLPH": case "POGO": return 32;
      case "NEWS": return 22;
      case "DANCER": return 20;
      case "BOB": return 50;
      case "DIGGER": return 40;
      default: return 17;
    }
  }
  static readonly HashSet<string> Metal = new HashSet<string> { "PAIL", "DOOR", "FOOT", "JACK", "POGO", "LADDER", "DIGGER" };
  static readonly HashSet<string> Jumpers = new HashSet<string> { "POLE", "POGO", "DOLPH" };
  static readonly HashSet<string> Water = new HashSet<string> { "DUCK", "SNORK", "DOLPH" };

  // ---------- Perception that is not learning: how fast each zombie on screen is moving right now ----------
  static readonly Dictionary<uint, double[]> proTrack = new Dictionary<uint, double[]>();   // id -> {x, time, speed}
  static double ProSpeed(Zom z, double now) {
    double[] t;
    double def = ProSpeedOf(Mem.ZomName(z.Type));
    if (!proTrack.TryGetValue(z.Id, out t)) { proTrack[z.Id] = new[] { z.X, now, def }; return def; }
    double dt = now - t[1];
    if (dt >= 0.5) {
      double v = (t[0] - z.X) / dt;
      if (v > 2 && v < 120) t[2] = t[2] * 0.7 + v * 0.3;
      t[0] = z.X; t[1] = now;
    }
    return Math.Max(8, t[2]);
  }

  // ---------- Loadout ----------
  public static List<int> ProLoadout(List<int> available, int slots, State s) {
    var have = new HashSet<int>(available);
    int bg = s.Background;
    var rt = Mem.RowTypes();
    bool water = rt.Any(t => t == 2), roof = bg == 4 || bg == 5, night = bg == 1 || bg == 3 || bg == 5;
    var preview = new HashSet<string>(s.Zombies.Select(z => Mem.ZomName(z.Type)));
    bool graves = s.Graves.Count > 0;
    // Area playbooks: plants in the order a strong player would take them.
    int[] plan;
    if (roof) plan = new[] { POT, SUNF, CABBAGE, KERNEL, MELON, CHERRY, JALA, UMBRELLA, SQUASH, WALLNUT, PUMPKIN, ICE, COFFEE, TALL };
    else if (bg == 3) plan = new[] { SUNSH, PUFF, LILY, SEASH, FUME, BLOVER, LANTERN, KELP, CHERRY, ICE, DOOM, SCAREDY, SQUASH, JALA, TALL, MAGNET, SUNF };
    else if (water) plan = new[] { SUNF, LILY, PEA, REPEAT, TORCH, CHERRY, KELP, THREE, TALL, WALLNUT, SQUASH, JALA, PMINE, SPIKE, SNOW };
    else if (night) plan = new[] { SUNSH, PUFF, FUME, GRAVEB, CHERRY, SCAREDY, ICE, DOOM, WALLNUT, SQUASH, HYPNO, MAGNET, JALA, SUNF, PEA, PMINE };
    else plan = new[] { SUNF, PEA, REPEAT, TORCH, CHERRY, WALLNUT, PMINE, SQUASH, THREE, JALA, CHOMP, TALL, SNOW, SPIKE, SPLIT };
    var score = new Dictionary<int, double>();
    for (int i = 0; i < plan.Length; i++) if (have.Contains(plan[i]) && !score.ContainsKey(plan[i])) score[plan[i]] = 100 - 4 * i;
    Action<int, double> bump = (sd, v) => { if (have.Contains(sd)) { double o; score.TryGetValue(sd, out o); score[sd] = o + v; } };
    // Counters for what the level preview shows (visible on the right of the plant chooser).
    if (graves && night) bump(GRAVEB, 40);
    if (preview.Overlaps(new[] { "DOOR", "LADDER" })) { bump(FUME, 25); bump(MAGNET, night ? 30 : 0); bump(CABBAGE, 10); bump(KERNEL, 10); bump(MELON, 10); }
    if (preview.Overlaps(new[] { "PAIL", "FOOT" })) { bump(MAGNET, night ? 25 : 0); bump(SQUASH, 15); bump(CHOMP, 10); bump(SNOW, 15); bump(HYPNO, 10); }
    if (preview.Contains("BALLOON")) { bump(BLOVER, 60); bump(CACTUS, 40); bump(CATTAIL, 40); }
    if (preview.Overlaps(Jumpers)) bump(TALL, 40);
    if (preview.Contains("DIGGER")) { bump(SPLIT, 50); bump(MAGNET, 30); bump(STAR, 20); }
    if (preview.Contains("ZAMB")) { bump(SPIKE, 45); bump(JALA, 25); }
    if (preview.Contains("BUNGEE") || preview.Overlaps(new[] { "CAT", "CAT2", "CAT3" })) bump(UMBRELLA, 50);
    if (preview.Overlaps(Water)) { bump(KELP, 30); bump(TALL, 10); }
    if (preview.Contains("SNORK")) { bump(KELP, 60); bump(CABBAGE, 20); bump(WALLNUT, 45); bump(TALL, 35); }   // snorkels swim under peas: kelp, lobbed shots, or a wall that makes them surface to eat (tip from the mod author)
    if (preview.Contains("GARG")) { bump(JALA, 40); bump(SQUASH, 30); bump(CHERRY, 20); bump(DOOM, 20); }
    // Mod: the DIRTY zombie one-shots non-wall plants and never shows in the preview - it ambushes during flag waves from
    // graves, the pool or bungee drops. So on those lawns a wall plant is always worth a slot (tip from the mod author).
    if (preview.Contains("DIRTY") || water || graves || roof) { bump(WALLNUT, 30); bump(TALL, 30); bump(PUMPKIN, 15); }
    if (preview.Contains("REGEN")) { bump(HYPNO, 45); bump(TALL, 35); bump(WALLNUT, 25); }   // mod: regrows its helmet on every plant it kills (tip from the mod author)
    if (preview.Contains("DANCER")) { bump(FUME, 20); bump(THREE, 20); bump(CHERRY, 15); }
    if (preview.Contains("JACK")) { bump(MAGNET, night ? 30 : 0); }
    if (s.Zombies.Count >= 8) { bump(CHERRY, 20); bump(JALA, 10); bump(ICE, 10); bump(DOOM, 10); }
    // In this mod torchwoods STACK (1 orange, 2 blue, 3 white, a 4th burns) - per the mod author. Fire also cancels snow.
    if (!night && !roof) { bump(TORCH, 60); bump(SNOW, -40); }
    // Mushrooms sleep in daylight (no coffee bean plan by day).
    if (!night) foreach (var k in score.Keys.ToList()) if (ProNight.Contains(k)) score[k] -= 200;
    // Must-haves: income, a pad where the lawn needs one, and real damage.
    var picks = new List<int>();
    Action<IEnumerable<int>> must = opts => { foreach (int o in opts) if (have.Contains(o) && score.ContainsKey(o) && score[o] > -50) { if (!picks.Contains(o)) picks.Add(o); return; } };
    if (roof) must(new[] { POT });
    if (water) must(new[] { LILY });
    must(night ? new[] { SUNSH, SUNF } : new[] { SUNF, SUNSH });
    must(roof ? new[] { CABBAGE, KERNEL, MELON } : night ? new[] { FUME, PUFF, SCAREDY, PEA, REPEAT } : new[] { REPEAT, PEA, THREE, SNOW });
    must(new[] { CHERRY, JALA, DOOM, SQUASH });
    // The mod author's rule: repeaters, never plain peashooters (same damage per sun, double per tile - lane space is the limit).
    if (have.Contains(REPEAT) && score.ContainsKey(PEA)) score[PEA] = -100;
    if (!night && !roof) { must(new[] { TORCH }); must(new[] { REPEAT, PEA }); }
    if (water && (preview.Overlaps(Water))) must(new[] { KELP });
    if (preview.Contains("ZAMB")) must(new[] { SPIKE, JALA, SQUASH });
    if (preview.Contains("BALLOON")) must(new[] { BLOVER, CACTUS, CATTAIL });   // balloons fly over everything else   // zombonis crush plants: bring what kills them
    foreach (var kv in score.OrderByDescending(kv => kv.Value)) { if (picks.Count >= slots) break; if (kv.Value > -50 && !picks.Contains(kv.Key)) picks.Add(kv.Key); }
    // Fill any leftover slots with whatever is unlocked, cheapest first (more options is never worse).
    foreach (var sd in available.Where(a => !picks.Contains(a) && !(ProNight.Contains(a) && !night) && a != 54 && a != 53 && !(a == PEA && have.Contains(REPEAT))).OrderBy(a => Mem.Cost(a))) { if (picks.Count >= slots) break; picks.Add(sd); }
    return picks.Take(slots).ToList();
  }

  // ---------- Playing ----------
  static List<Cand> ProScore(State s) {
    var list = new List<Cand>();
    var rowTypes = Mem.RowTypes();
    var rows = Enumerable.Range(0, 6).Where(r => rowTypes[r] == 1 || rowTypes[r] == 2 || rowTypes[r] == 3).ToList();
    if (rows.Count == 0) return list;
    int bg = s.Background;
    bool roof = bg == 4 || bg == 5, night = bg == 1 || bg == 3 || bg == 5;
    double now = s.GameTime;
    bool belt = IsConveyor(s);
    var usable = belt ? s.Packets.Where(p => p.Active && p.Type >= 0 && p.CX < BELT_END_X).GroupBy(p => p.Type).Select(g => g.OrderBy(p => p.CX).First()).ToList()
                      : s.Packets.Where(p => p.Active && p.Cd == 0).ToList();
    usable.AddRange(s.Suns.Where(c => c.IsPacket && c.Seed >= 0).Select(c => new Pkt { Type = c.Seed, Cost = 0, Active = true, FromCoin = true, CoinX = c.X, CoinY = c.Y, CoinId = c.Id, W = 50, H = 70 }));
    // Every seed in the bank (even recharging) tells us what we will have soon.
    var bankTypes = new HashSet<int>(s.Packets.Where(p => p.Type >= 0).Select(p => p.Type));

    Func<int, int, bool> isWater = (r, c) => rowTypes[r] == 2;
    Func<int, int, Plt> top = (r, c) => s.Plants.Where(p => p.Row == r && p.Col == c && !ProIsPad(p.Type) && p.Type != PUMPKIN).FirstOrDefault();
    Func<int, int, bool> padAt = (r, c) => s.Plants.Any(p => p.Row == r && p.Col == c && ProIsPad(p.Type));
    Func<int, int, bool> blocked = (r, c) => c < 0 || c > 8 || Bad.Contains(r * 10 + c) || s.Blocked.Contains(r * 10 + c) || s.Graves.Contains(r * 10 + c);
    // Where may this seed go?
    Func<int, int, int, bool> canPlace = (sd, r, c) => {
      if (blocked(r, c)) return false;
      bool w = isWater(r, c);
      if (sd == LILY) return w && !s.Plants.Any(p => p.Row == r && p.Col == c);
      if (sd == POT) return !w && !s.Plants.Any(p => p.Row == r && p.Col == c);
      if (sd == KELP || sd == SEASH) return w && !s.Plants.Any(p => p.Row == r && p.Col == c);
      if (sd == TWINSUN) return s.Plants.Any(p => p.Row == r && p.Col == c && p.Type == SUNF);
      if (sd == GATLING) return s.Plants.Any(p => p.Row == r && p.Col == c && p.Type == REPEAT);
      if (sd == GLOOM) return s.Plants.Any(p => p.Row == r && p.Col == c && p.Type == FUME);
      if (sd == WMELON) return s.Plants.Any(p => p.Row == r && p.Col == c && p.Type == MELON);
      if (sd == CATTAIL) return w && padAt(r, c) && top(r, c) == null;
      if (sd == PUMPKIN) return top(r, c) != null && !s.Plants.Any(p => p.Row == r && p.Col == c && p.Type == PUMPKIN);
      if (top(r, c) != null) return false;
      if (w || roof) return padAt(r, c);
      return true;
    };
    // A cell a normal plant could use once a pad/pot is down (the pad comes first).
    Func<int, int, bool> needsBase = (r, c) => !blocked(r, c) && top(r, c) == null && !padAt(r, c) && (isWater(r, c) || roof);

    var live = s.Zombies.Where(z => z.X < VISIBLE_X && rows.Contains(z.Row)).ToList();
    var spd = live.ToDictionary(z => z.Id, z => ProSpeed(z, now));
    // Lane firepower now (threepeaters help neighbours; torchwood doubles peas passing through).
    var dps = new double[6];
    foreach (var p in s.Plants) {
      double d = ProDps(p.Type);
      if (d <= 0) continue;
      if (p.Type == THREE) { for (int dr = -1; dr <= 1; dr++) if (p.Row + dr >= 0 && p.Row + dr < 6) dps[p.Row + dr] += d; }
      else dps[p.Row] += d * ((p.Type == PEA || p.Type == REPEAT || p.Type == GATLING) ? 1 + Math.Min(3, s.Plants.Count(t => t.Type == TORCH && t.Row == p.Row && t.Col > p.Col)) : 1);
    }
    var shooters = rows.ToDictionary(r => r, r => s.Plants.Count(p => ProIsShooter(p.Type) && (p.Row == r || (p.Type == THREE && Math.Abs(p.Row - r) == 1))));
    var nearest = rows.ToDictionary(r => r, r => live.Where(z => z.Row == r).Select(z => z.X).DefaultIfEmpty(9999).Min());
    // Danger: zombie health in the lane versus damage the lane can deal before the lead zombie reaches the house side.
    var danger = new Dictionary<int, double>();
    foreach (var r in rows) {
      var zs = live.Where(z => z.Row == r).ToList();
      if (zs.Count == 0) { danger[r] = 0; continue; }
      double hp = zs.Sum(z => (double)z.Total);
      var lead = zs.OrderBy(z => z.X).First();
      double secs = Math.Max(1, (lead.X - 60) / spd[lead.Id]);
      // With no shooter yet, judge against what one peashooter planted now would manage before it arrives.
      danger[r] = dps[r] > 0 ? hp / (dps[r] * secs + 1) : hp / (14 * secs + 1) * 0.8;
      if (s.Mowers[r] != 1 && s.Mowers[r] != 0) danger[r] *= 1.4;   // no mower behind: no second chance
    }
    UnderThreat = rows.Any(r => danger[r] > 1 || nearest[r] < 300);
    ProStatus = "sun makers " + s.Plants.Count(p => ProSun.Contains(p.Type)) + " | lane dps " + string.Join(" ", rows.Select(r => dps[r].ToString("0"))) + " | worst lane " + rows.OrderByDescending(r => danger[r]).First() + " danger " + rows.Max(r => danger[r]).ToString("0.0");
    LaneDanger = rows.Aggregate(new double[6], (a, r) => { a[r] = danger[r]; return a; });

    double progress = s.Waves > 0 ? Math.Min(1, (double)s.Wave / s.Waves) : 0;
    int toFlag = WavesToFlag(s);
    bool flagSoon = toFlag >= 0 && toFlag <= 2;
    bool finalSoon = s.Waves > 0 && s.Waves - s.Wave <= 2;
    // Target firepower per lane in dps: about one peashooter early, three by the end, more before flags.
    // Sized to what the waves bring: a flag wave lane holds ~2500-4000 hp that crosses the lawn in ~45 s, so ~70 dps by the first flag.
    // No mower/cleaner behind a lane (pool cleaners do not appear in this mod yet): that lane is a last line, build it harder.
    Func<int, double> target = r => 14 * (1 + 4.5 * progress) + (flagSoon || finalSoon ? 28 : 0) + (s.Mowers[r] != 1 && s.Mowers[r] != 0 ? 28 : 0);
    var landRows = rows.Where(r => rowTypes[r] != 2).ToList();
    int sunCount = s.Plants.Count(p => ProSun.Contains(p.Type));
    int sunTarget = night ? landRows.Count + (progress < 0.5 ? landRows.Count / 2 : 0) : (int)Math.Round(2.5 * landRows.Count);
    int bombsInBank = s.Packets.Count(p => p.Type == CHERRY || p.Type == JALA || p.Type == DOOM || p.Type == ICE);

    Action<Pkt, int, int, double, string> add = (p, r, c, sc, why) => {
      if (sc <= 0) return;
      if (!canPlace(p.Type, r, c)) {
        // Needs a pad/pot first: propose the pad for this spot instead, at nearly the same importance.
        if (needsBase(r, c) && !ProIsPad(p.Type)) {
          var pad = usable.FirstOrDefault(u => (isWater(r, c) ? u.Type == LILY : u.Type == POT));
          // Only worth full priority if we can follow up with the plant itself; otherwise the pad is just a cheap distraction.
          // Pad + plant is one purchase: full priority, but only "affordable" once both can be paid, so the saving logic
          // holds sun back for a starving water lane instead of letting land lanes spend it all.
          // A pad may go down on its own if none is already waiting empty in this lane (no spam), so water lanes are not
          // starved by land lanes that grab every 100 sun first; the plant then follows onto it with a bonus.
          bool waiting = Enumerable.Range(0, 9).Any(cc => padAt(r, cc) && top(r, cc) == null);
          bool both = pad != null && (belt || p.FromCoin || pad.FromCoin || s.Sun >= pad.Cost + p.Cost || (!waiting && pad.Cost <= s.Sun));
          if (pad != null && canPlace(pad.Type, r, c)) list.Add(new Cand { P = pad, Row = r, Col = c, Score = sc * 0.97, Affordable = both, Why = (isWater(r, c) ? "lily pad" : "flower pot") + " first, for: " + why });
        }
        return;
      }
      bool onPad = (isWater(r, c) || roof) && padAt(r, c) && !ProIsPad(p.Type);
      if (onPad) { sc += 120; why += " (pad already down)"; }
      list.Add(new Cand { P = p, Row = r, Col = c, Score = sc, Affordable = belt || p.FromCoin || p.Cost <= s.Sun, Why = why });
    };
    // Columns: sun makers at the back, shooters next, walls well in front.
    int[] sunCols = { 0, 1, 2 };
    int[] shootCols = night ? new[] { 2, 3, 1, 4, 5, 0 } : new[] { 2, 3, 4, 1, 5, 0 };

    foreach (var p in usable) {
      int t = p.Type;
      // --- Economy ---
      if (ProSun.Contains(t) && t != TWINSUN && sunCount < sunTarget && (t != SUNSH || night)) {
        // Income is what wins: one sun maker per land lane first, then a second column during the first half.
        bool threat = rows.Any(r => danger[r] > 1.2 && nearest[r] < 350);
        double sc = sunCount < landRows.Count ? 720 : progress < 0.5 ? 540 : progress < 0.7 ? 300 : 80;
        if (sunCount < 2) sc = 800;                      // the opening: income before anything
        if (threat) sc *= 0.5;
        foreach (int c in sunCols) {
          var r = landRows.OrderBy(rr => s.Plants.Count(q => q.Row == rr && ProSun.Contains(q.Type))).ThenBy(rr => danger[rr])
                          .Where(rr => canPlace(t, rr, c) && nearest[rr] > CellX(c) + 160).Cast<int?>().FirstOrDefault();
          if (r != null) { add(p, r.Value, c, sc - 5 * c, "economy " + sunCount + "/" + sunTarget + " sun makers"); break; }
        }
      }
      if (t == TWINSUN && progress < 0.7) {
        var f = s.Plants.FirstOrDefault(q => q.Type == SUNF);
        if (f != null) add(p, f.Row, f.Col, 300, "upgrade a sunflower to twin");
      }
      // --- Firepower ---
      if (ProIsShooter(t) && !(ProNight.Contains(t) && !night)) {
        foreach (var r in rows) {
          if (roof && !ProLobbed.Contains(t) && t != CATTAIL) continue;   // the roof slope blocks straight shots
          double need = target(r) - dps[r];
          double sc = 0; string why = null;
          // Urgency grows as the lead zombie closes in: far-away trouble can wait for the economy.
          double prox = nearest[r] < 350 ? 1.3 : nearest[r] < 500 ? 1.0 : 0.7;
          if (danger[r] > 0.6) { sc = prox * (450 + 200 * Math.Min(3, danger[r])); why = "lane " + r + " is losing (danger " + danger[r].ToString("0.0") + ", lead at x=" + nearest[r].ToString("0") + ")"; }
          else if (shooters[r] == 0 && nearest[r] < VISIBLE_X) { sc = Math.Min(900, 300 + (VISIBLE_X - nearest[r])); why = "first shooter for the zombie in lane " + r + " (x=" + nearest[r].ToString("0") + ")"; }
          else if (need > 0 && sunCount >= landRows.Count) { sc = 250 + 10 * need + (flagSoon || finalSoon ? 120 : 0); why = "build lane " + r + " to " + (target(r) / 14).ToString("0.0") + " peas" + (flagSoon ? " before the FLAG wave" : finalSoon ? " before the FINAL wave" : ""); }
          if (sc <= 0) continue;
          // Best shooter for the job: cheap now, strong later.
          double val = ProDps(t) / 14.0;
          if (t == THREE) val = Math.Min(3, rows.Count(rr => Math.Abs(rr - r) <= 1 && target(rr) - dps[rr] > 0)) * 0.9;
          if (t == SNOW && live.Any(z => z.Row == r && z.Total > 600)) val += 0.6;
          if (t == SNOW && shooters[r] >= 2 && !s.Plants.Any(q => q.Row == r && q.Type == SNOW)) val += 1.5;   // slowing the lane ~doubles every shooter's damage
          if ((t == FUME || t == GLOOM) && live.Any(z => z.Row == r && z.Shield > 0)) val += 1;
          if (t == PUFF) val = p.Cost == 0 && s.Plants.Count(q => q.Row == r && q.Type == PUFF) < 2 ? 1.1 : 0.3;
          sc *= 0.75 + 0.25 * Math.Min(3, val);
          int[] cols = t == PUFF || t == SEASH ? new[] { 4, 5, 3, 6 } : t == SCAREDY ? new[] { 1, 0, 2 } : shootCols;
          if (isWater(r, 0) && t != CATTAIL) cols = new[] { 2, 3, 4, 1, 5 };
          int c = cols.Where(cc => (canPlace(t, r, cc) || needsBase(r, cc)) && CellX(cc) < nearest[r] - 60).Select(cc => (int?)cc).FirstOrDefault() ?? -1;
          if (c < 0) continue;
          // Upgrades go on top of their base plant.
          if (t == GATLING || t == GLOOM || t == WMELON) {
            int baseT = t == GATLING ? REPEAT : t == GLOOM ? FUME : MELON;
            var b = s.Plants.Where(q => q.Row == r && q.Type == baseT).FirstOrDefault();
            if (b == null) continue; c = b.Col;
          }
          if (t == CATTAIL) { int cc2 = Enumerable.Range(0, 9).FirstOrDefault(cc => canPlace(CATTAIL, r, cc)); if (!canPlace(CATTAIL, r, cc2)) continue; c = cc2; }
          add(p, r, c, sc, why);
        }
      }
      // --- Area bombs on piles (cherry 3x3, doom wide) ---
      if ((t == CHERRY || t == DOOM) && live.Count > 0) {
        double reach = t == DOOM ? 250 : 115; int rr0 = t == DOOM ? 2 : 1;
        Cand best = null;
        foreach (var r in rows) for (int c = 0; c < 9; c++) {
          bool dig = false;
          if (!canPlace(t, r, c)) { var occ = top(r, c); if (occ == null || !ProSun.Contains(occ.Type) || !live.Any(z => z.X < 300)) continue; dig = true; }
          var hit = live.Where(z => Math.Abs(z.Row - r) <= rr0 && Math.Abs(z.X + 30 - CellX(c)) <= reach).ToList();
          if (hit.Count == 0) continue;
          double hp = hit.Sum(z => (double)z.Total);
          double close = hit.Min(z => z.X);
          if (close > 620 && hp < 3000) continue;   // still at the lawn edge: shooters get their full run at them first
          bool worth = t == DOOM ? (hit.Count >= 6 || hp >= 3500 || (close < 250 && hp >= 1500)) : (hit.Count >= 3 || hp >= 1500 || (close < 260 && hp >= 700));
          if (!worth) continue;
          double sc = 450 + hp / 4 + 50 * hit.Count + (close < 350 ? 250 : 0) - (dig ? 80 : 0) - (t == DOOM && c < 3 ? 150 : 0);
          if (best == null || sc > best.Score)
            best = new Cand { P = p, Row = r, Col = c, Score = Math.Min(1800, sc), ShovelFirst = dig, Affordable = belt || p.FromCoin || p.Cost <= s.Sun,
                              Why = (t == DOOM ? "doom" : "cherry") + " on " + hit.Count + " zombies (~" + hp.ToString("0") + " hp)" + (dig ? " - digging a sunflower for room" : "") };
        }
        if (best != null) list.Add(best);
      }
      // --- Jalapeno: a whole lane at once ---
      if (t == JALA) foreach (var r in rows) {
        var zs = live.Where(z => z.Row == r).ToList();
        double hp = zs.Sum(z => (double)z.Total);
        if (zs.Count >= 4 || hp >= 2000 || (danger[r] > 1.5 && nearest[r] < 350 && hp >= 600)) {
          int c = Enumerable.Range(0, 9).Where(cc => canPlace(t, r, cc)).Select(cc => (int?)cc).FirstOrDefault() ?? -1;
          if (c >= 0) add(p, r, c, 500 + hp / 4 + (nearest[r] < 350 ? 250 : 0), "jalapeno lane " + r + ": " + zs.Count + " zombies (~" + hp.ToString("0") + " hp)");
        }
      }
      // --- Ice: freeze a flood ---
      if (t == ICE && night) {
        int close = live.Count(z => z.X < 500);
        if (close >= 6 || rows.Count(r => danger[r] > 1.5) >= 2) {
          int r = rows.First(); int c = Enumerable.Range(0, 9).Where(cc => canPlace(t, r, cc)).Select(cc => (int?)cc).FirstOrDefault() ?? -1;
          if (c >= 0) add(p, r, c, 700 + 40 * close, "ice: freeze " + close + " zombies near the house");
        }
      }
      // --- Squash / tangle kelp / chomper / hypno: one tough zombie ---
      // Hypno priority (mod author, 4-5 round 3): the dancer first, then its backup dancers (they gain HP over time),
      // then buckets / footballs; peas handle the rest. Other plants just take the nearest worthy target.
      Func<string, int> hypPri = nm => nm == "DANCER" ? 4 : nm == "BDANCE" ? 3 : (nm == "PAIL" || nm == "FOOT" || nm == "REGEN" || nm == "GARG") ? 2 : 0;
      var order = t == HYPNO ? live.OrderByDescending(z => hypPri(Mem.ZomName(z.Type))).ThenBy(z => z.X) : live.OrderBy(z => z.X);
      if (t == SQUASH || t == KELP || t == CHOMP || t == HYPNO) foreach (var z in order) {
        string n = Mem.ZomName(z.Type);
        // A hypno already waiting in front of this zombie means it is handled - do not stack a second one.
        if (t == HYPNO && s.Plants.Any(q => q.Row == z.Row && q.Type == HYPNO && CellX(q.Col) < z.X + 30)) continue;
        if (t == HYPNO && hypPri(n) > 0 && z.X < 650) {   // hypno the priority target in its path, whatever its hp
          int hc = Math.Max(0, ColOf(z.X) - 1);
          bool put = false;
          for (int cc = hc; cc >= Math.Max(0, hc - 2) && !put; cc--)
            if (canPlace(t, z.Row, cc)) { add(p, z.Row, cc, 700 + 150 * hypPri(n) + (z.X < 300 ? 250 : 0), "DARTH'S GAMBIT: hypno the " + n.ToLower() + " (" + z.Total + " hp)"); put = true; }
          if (put) break;
          continue;
        }
        if (t == KELP && !isWater(z.Row, 0)) continue;
        if (t != KELP && isWater(z.Row, 0) && !padAt(z.Row, Math.Max(0, ColOf(z.X) - 1))) continue;
        bool tough = z.Total >= 1000 || n == "FOOT" || n == "REGEN" || n == "GARG" || n == "ZAMB" || n == "POLE" || n == "DOLPH" || n == "SNORK";
        bool near = z.X < (t == CHOMP ? 420 : 520);
        // Fast zombies dodge a squash in motion: wait until it stops to eat a plant (tip from the mod author).
        bool fast = ProSpeedOf(n) >= 25 || n == "REGEN";
        bool eating = s.Plants.Any(q => q.Row == z.Row && !ProIsPad(q.Type) && Math.Abs(CellX(q.Col) - (z.X + 30)) < 45);
        if (t == SQUASH && fast && !eating) continue;
        if (!(near && (tough || danger[z.Row] > 1))) continue;
        if (t == SQUASH && PrecisionLevel && VaseRound >= 2 && !SquashFree(s) && !tough && z.Total < 600) continue;   // vasebreaker: squashes are scarce - save them for footballs and other tough zombies
        int c = Math.Max(0, ColOf(z.X) - (t == SQUASH ? 0 : 1));
        if (t == HYPNO && !(n == "FOOT" || n == "REGEN" || n == "GARG" || n == "PAIL" || n == "DANCER")) continue;
        if (t == CHOMP && n == "GARG") continue;
        for (int cc = c; cc >= Math.Max(0, c - 1); cc--)
          if (canPlace(t, z.Row, cc)) { add(p, z.Row, cc, 550 + z.Total / 5 + (z.X < 300 ? 250 : 0), Mem.SeedName(t).ToLower() + " the " + n.ToLower() + " (" + z.Total + " hp)"); break; }
        break;
      }
      // --- Potato mine: free early kill when the zombie is far enough away to let it arm (~15 s) ---
      if (t == PMINE) foreach (var r in rows) {
        var lead = live.Where(z => z.Row == r).OrderBy(z => z.X).FirstOrDefault();
        if (lead == null || shooters[r] >= 2) continue;
        for (int c = 5; c >= 3; c--) {
          double eta = (lead.X + 30 - CellX(c)) / spd[lead.Id];
          if (eta >= 16 && canPlace(t, r, c)) { add(p, r, c, 380 + (shooters[r] == 0 ? 200 : 0), "potato mine ahead of the " + Mem.ZomName(lead.Type).ToLower() + " (arms in time: " + eta.ToString("0") + "s away)"); break; }
        }
      }
      // --- Walls: in front of a built lane, before tough waves; replace worn ones ---
      if (ProWall.Contains(t) || t == PUMPKIN) foreach (var r in rows) {
        if (shooters[r] < 1 || (shooters[r] < 2 && danger[r] < 0.8 && !(flagSoon || finalSoon))) continue;
        int front = s.Plants.Where(q => q.Row == r && !ProIsPad(q.Type)).Select(q => q.Col).DefaultIfEmpty(0).Max();
        var wall = s.Plants.Where(q => q.Row == r && ProWall.Contains(q.Type)).OrderByDescending(q => q.Col).FirstOrDefault();
        bool worn = wall != null && wall.MaxHp > 0 && wall.Hp < wall.MaxHp / 3;
        bool wantTall = live.Any(z => z.Row == r && Jumpers.Contains(Mem.ZomName(z.Type)));
        if (t == PUMPKIN) { if (worn && canPlace(PUMPKIN, r, wall.Col)) add(p, r, wall.Col, 420, "pumpkin over the worn wall in lane " + r); continue; }
        if (wall != null && !worn) continue;
        int c = Math.Min(8, Math.Max(5, (wall != null ? wall.Col + 1 : front + 2)));
        if (CellX(c) > nearest[r] - 30) c = Math.Max(front + 1, ColOf(nearest[r]) - 1);
        double sc = (danger[r] > 0.8 ? 450 : flagSoon || finalSoon ? 330 : 200) + (worn ? 100 : 0) + (t == TALL && wantTall ? 150 : 0);
        add(p, r, c, sc, (worn ? "replace the worn wall" : "wall in front of lane " + r) + (wantTall ? " (tall: stops jumpers)" : ""));
      }
      // --- Utility counters ---
      if (t == GRAVEB) foreach (int g in s.Graves.OrderByDescending(gc => gc % 10)) {
        int gr = g / 10, gc2 = g % 10; if (!rows.Contains(gr)) continue;
        list.Add(new Cand { P = p, Row = gr, Col = gc2, Score = 280 + 20 * s.Graves.Count + (finalSoon ? 150 : 0), Affordable = belt || p.FromCoin || p.Cost <= s.Sun, Why = "bust grave r" + gr + "c" + gc2 + " (graves spawn zombies)" });
        break;
      }
      if (t == BLOVER && (live.Any(z => Mem.ZomName(z.Type) == "BALLOON" && z.X < 600) || (bg == 3 && finalSoon && s.Wave == s.Waves))) {
        int r = rows.First(); int c = Enumerable.Range(0, 9).Where(cc => canPlace(t, r, cc)).Select(cc => (int?)cc).FirstOrDefault() ?? -1;
        if (c >= 0) add(p, r, c, 950, "blover: blow away balloons/fog");
      }
      if ((t == CACTUS || t == CATTAIL) && live.Any(z => Mem.ZomName(z.Type) == "BALLOON")) {
        var bz = live.First(z => Mem.ZomName(z.Type) == "BALLOON");
        foreach (int c in new[] { 2, 3, 1 }) if (canPlace(t, bz.Row, c)) { add(p, bz.Row, c, 600, "pop the balloon in lane " + bz.Row); break; }
      }
      if (t == MAGNET && night && live.Any(z => Metal.Contains(Mem.ZomName(z.Type)) && z.X < 700) && s.Plants.Count(q => q.Type == MAGNET) < 2) {
        var mz = live.Where(z => Metal.Contains(Mem.ZomName(z.Type))).OrderBy(z => z.X).First();
        foreach (int c in new[] { 3, 2, 4 }) if (canPlace(t, mz.Row, c)) { add(p, mz.Row, c, 450, "magnet: strip the metal off the " + Mem.ZomName(mz.Type).ToLower()); break; }
      }
      if (t == UMBRELLA && roof && s.Plants.Count(q => q.Type == UMBRELLA) < 2 && progress > 0.1) {
        int r = rows[rows.Count / 2]; int c = s.Plants.Count(q => q.Type == UMBRELLA) == 0 ? 2 : 4;
        add(p, r, c, 320, "umbrella over the middle (bungee/catapult cover)");
      }
      if (t == LANTERN && bg == 3 && !s.Plants.Any(q => q.Type == LANTERN)) {
        int r = rows[rows.Count / 2]; add(p, r, 4, 300, "plantern: see through the fog");
      }
      if (t == TORCH && !roof) foreach (var r in rows) {
        var peas = s.Plants.Where(q => q.Row == r && (q.Type == PEA || q.Type == REPEAT || q.Type == GATLING)).ToList();
        int torches = s.Plants.Count(q => q.Row == r && q.Type == TORCH);
        if (peas.Count < 2 || torches >= 3 || s.Plants.Any(q => q.Row == r && q.Type == SNOW)) continue;
        // Stack them in a row straight in front of the peas (a 4th would burn the peas).
        int c = Math.Max(peas.Max(q => q.Col), s.Plants.Where(q => q.Row == r && q.Type == TORCH).Select(q => q.Col).DefaultIfEmpty(-1).Max()) + 1;
        if (c <= 6) { add(p, r, c, 350 + 150 * peas.Count + (torches > 0 ? 100 : 0), "torchwood #" + (torches + 1) + " for " + peas.Count + " shooters in lane " + r + " (stacked fire)"); break; }
      }
      if (t == SPIKE && live.Any(z => Mem.ZomName(z.Type) == "ZAMB")) {
        var zz = live.First(z => Mem.ZomName(z.Type) == "ZAMB");
        foreach (int c in new[] { 6, 5, 7 }) if (canPlace(t, zz.Row, c) && CellX(c) < zz.X) { add(p, zz.Row, c, 600, "spikeweed pops the zomboni"); break; }
      }
      if ((t == SPLIT || t == STAR) && live.Any(z => Mem.ZomName(z.Type) == "DIGGER")) {
        var dz = live.First(z => Mem.ZomName(z.Type) == "DIGGER");
        foreach (int c in new[] { 2, 3 }) if (canPlace(t, dz.Row, c)) { add(p, dz.Row, c, 500, "split pea covers the back against diggers"); break; }
      }
      // --- Pads/pots placed proactively where the next shooter will stand ---
      if ((t == LILY || t == POT) && !list.Any(cd => cd.P == p)) foreach (var r in rows) {
        bool needThisLane = (t == LILY && rowTypes[r] == 2) || (t == POT && roof);
        if (!needThisLane) continue;
        if (Enumerable.Range(0, 9).Any(cc => padAt(r, cc) && top(r, cc) == null)) continue;   // one waiting pad per lane is enough
        double need = target(r) - dps[r];
        if (need <= 0 && danger[r] < 0.5) continue;
        if (sunCount < landRows.Count && danger[r] < 0.5) continue;   // opening: income first, pads when they are needed
        int c = (t == POT ? new[] { 0, 1, 2, 3, 4 } : new[] { 2, 3, 4, 1, 5 }).Where(cc => canPlace(t, r, cc) && CellX(cc) < nearest[r] - 80).Select(cc => (int?)cc).FirstOrDefault() ?? -1;
        if (c >= 0) add(p, r, c, 220 + 8 * Math.Max(0, need) + 200 * Math.Min(2, danger[r]), (t == LILY ? "lily pad" : "flower pot") + " ready for lane " + r);
      }
    }
    // Look ahead: simulate each lane ~30 s and let the moves that actually stop a breach outrank everything else.
    var baseOut = rows.ToDictionary(r => r, r => SimLane(r, s, spd));
    ProSimDefence(s, usable, rows, spd, canPlace, needsBase, add, baseOut);
    var breaking = rows.Where(r => baseOut[r].Breach).ToList();
    ProStatus += breaking.Count == 0 ? " | sim: all lanes hold 30s" : " | sim: lane " + string.Join(",", breaking.Select(r => r + "@" + baseOut[r].BreachT.ToString("0") + "s")) + " breaking";
    ProBreachT = breaking.Count == 0 ? 9999 : breaking.Min(r => baseOut[r].BreachT);
    // Conveyor levels: you play what the belt gives you. A backed-up belt stops new plants arriving, so use what is waiting.
    // Same for dropped packets (vases, rewards): they fade, so every one gets a sensible spot instead of disappearing.
    if ((belt && usable.Count(u => !u.FromCoin) >= 5) || usable.Any(u => u.FromCoin)) {
      foreach (var p in usable.Where(u => u.FromCoin || (belt && usable.Count(v => !v.FromCoin) >= 5)).GroupBy(u => u.Type).Select(g => g.First())) {
        if (list.Any(cd => cd.P.Type == p.Type && cd.Score >= 150)) continue;
        int t = p.Type; int br = -1, bc = -1; string why = null;
        if (t == TORCH) {   // in front of whichever lane has the most pea fire
          var lane = rows.Select(r => new { r, n = s.Plants.Count(q => q.Row == r && (q.Type == PEA || q.Type == REPEAT || q.Type == GATLING || q.Type == THREE)) })
                         .Where(x => x.n > 0 && !s.Plants.Any(q => q.Row == x.r && q.Type == SNOW) && s.Plants.Count(q => q.Row == x.r && q.Type == TORCH) < 3).OrderByDescending(x => x.n).FirstOrDefault();
          if (lane != null) { br = lane.r; bc = s.Plants.Where(q => q.Row == lane.r && (ProIsShooter(q.Type) || q.Type == TORCH)).Max(q => q.Col) + 1; why = "belt: torchwood in front of lane " + br + "'s peas"; }
        } else if (t == SPIKE) {   // where zombies will walk over it
          var r2 = rows.Where(r => !isWater(r, 0)).OrderByDescending(r => live.Count(z => z.Row == r)).ThenBy(r => s.Plants.Count(q => q.Row == r && q.Type == SPIKE)).FirstOrDefault();
          br = r2; bc = new[] { 6, 5, 7, 4 }.FirstOrDefault(cc => canPlace(SPIKE, r2, cc)); why = "belt: spikeweed under the zombies' path in lane " + br;
        } else if (ProIsShooter(t)) {
          // Vasebreaker (no sun makers): shooters at the far left for the longest firing time; one per lane first, and
          // extra shooters only where a tougher-than-normal zombie is (tips from the mod author).
          bool vb = s.Vases.Count > 0;
          var r3 = rows.OrderByDescending(r => live.Any(z => z.Row == r) && dps[r] < 14 ? live.Where(z => z.Row == r).Sum(z => (double)z.Total) : 0)   // a zombie walking down an undefended lane first
                       .ThenBy(r => shooters[r] == 0 ? 0 : 1)
                       .ThenByDescending(r => live.Where(z => z.Row == r && Mem.ZomName(z.Type) != "NORM").Sum(z => (double)z.Total))
                       .ThenBy(r => dps[r]).First();
          br = r3; var cols3 = vb ? new[] { 0, 1, 2, 3, 4 } : shootCols;
          bc = cols3.FirstOrDefault(cc => canPlace(t, r3, cc) || needsBase(r3, cc)); why = (vb ? "vasebreaker: " : "belt: ") + "shooter for lane " + br;
        } else if (t == SQUASH && live.Count == 0 && s.Vases.Count > 0 && p.FromCoin && Learn.PacketTimeLeft(p.CoinId) >= 0 && Learn.PacketTimeLeft(p.CoinId) < 3) {   // about to fade: ambush in front of the vases
          var vl = s.Vases.Where(k => !s.Plants.Any(q => q.Row == k / 10 && q.Type == SQUASH) && !live.Any(zz => zz.Row == k / 10 && zz.X + 30 < CellX(k % 10 - 1))).OrderBy(k => k % 10).Select(k => (int?)k).FirstOrDefault();
          if (vl != null) { br = vl.Value / 10; bc = s.Vases.Where(k => k / 10 == br).Min(k => k % 10) - 1; why = "packet: squash parked in front of the vases in lane " + br + " (ambush)"; }
        } else if ((t == SQUASH || t == CHOMP || t == HYPNO || t == CHERRY || t == JALA || t == DOOM || t == PMINE || t == KELP) && live.Count > 0) {
          // Vasebreaker: a squash is reserved for tough zombies (peas handle the normal ones).
          var pool = (t == SQUASH && PrecisionLevel && VaseRound >= 2 && !SquashFree(s)) ? live.Where(zz => zz.Total >= 600 || Mem.ZomName(zz.Type) == "FOOT" || Mem.ZomName(zz.Type) == "REGEN").ToList() : live;
          if (pool.Count == 0 && t == SQUASH && PrecisionLevel && VaseRound >= 2 && !SquashFree(s) && !(p.FromCoin && Learn.PacketTimeLeft(p.CoinId) >= 0 && Learn.PacketTimeLeft(p.CoinId) < 3)) continue;   // hold it for the football
          if (pool.Count == 0) pool = live;
          if (t == HYPNO) { var hp2 = live.Where(zz => { var nm = Mem.ZomName(zz.Type); return nm == "DANCER" || nm == "BDANCE" || nm == "PAIL" || nm == "FOOT" || nm == "REGEN"; }).ToList(); if (hp2.Count > 0) pool = hp2.OrderByDescending(zz => Mem.ZomName(zz.Type) == "DANCER" ? 3 : Mem.ZomName(zz.Type) == "BDANCE" ? 2 : 1).ThenBy(zz => zz.X).Take(1).ToList(); }
          var z = pool.OrderBy(zz => zz.X).First();   // the most pressing zombie: drop it in its path
          // A squash misses fast zombies on the move: hold it until the target stops to eat, unless the packet is about to fade.
          string zn = Mem.ZomName(z.Type);
          bool zFast = ProSpeedOf(zn) >= 25 || zn == "REGEN";
          bool zEating = s.Plants.Any(q => q.Row == z.Row && !ProIsPad(q.Type) && Math.Abs(CellX(q.Col) - (z.X + 30)) < 45);
          double fade = p.FromCoin ? Learn.PacketTimeLeft(p.CoinId) : -1;
          bool toughZ = z.Total >= 600 || zn == "FOOT" || zn == "REGEN";
          if (t == SQUASH && PrecisionLevel && VaseRound >= 2 && !SquashFree(s) && !toughZ && !(fade >= 0 && fade < 3)) continue;
          if (t == SQUASH && zFast && !zEating && !(fade >= 0 && fade < 3)) {
            // Hold it for a football that stops to eat; only in its last seconds park it in front of a vase (tips from the mod author).
            if (!(fade >= 0 && fade < 3)) continue;
            var vl = s.Vases.Where(k => !s.Plants.Any(q => q.Row == k / 10 && q.Type == SQUASH) && !live.Any(zz => zz.Row == k / 10 && zz.X + 30 < CellX(k % 10 - 1))).OrderBy(k => k % 10).Select(k => (int?)k).FirstOrDefault();
            if (vl == null) continue;
            br = vl.Value / 10; bc = s.Vases.Where(k => k / 10 == br).Min(k => k % 10) - 1;
            if (bc < 0 || !canPlace(t, br, bc)) continue;
            why = "packet: squash parked in front of the vases in lane " + br + " (ambush)";
          } else {
          br = z.Row; bc = Math.Max(0, Math.Min(8, ColOf(z.X) - (t == SQUASH || t == CHERRY || t == JALA || t == DOOM ? 0 : 1)));
          if (!canPlace(t, br, bc)) bc = Math.Max(0, bc - 1);
          why = "packet: " + Mem.SeedName(t).ToLower() + " in the path of the nearest zombie (lane " + br + ")";
          }
        } else if (ProSun.Contains(t)) {
          var r5 = landRows.FirstOrDefault(rr => Enumerable.Range(0, 3).Any(cc => canPlace(t, rr, cc)));
          br = r5; bc = Enumerable.Range(0, 3).FirstOrDefault(cc => canPlace(t, r5, cc)); why = "packet: sun maker at the back";
        } else if (ProWall.Contains(t)) {
          var r4 = rows.OrderByDescending(r => dps[r]).First(); br = r4; bc = Math.Min(7, s.Plants.Where(q => q.Row == r4).Select(q => q.Col).DefaultIfEmpty(3).Max() + 1); why = "belt: wall in front of lane " + br;
        }
        if (br >= 0 && bc >= 0 && bc <= 8) add(p, br, bc, 260, why);
      }
    }
    // Keep a bomb's worth of sun just before flag/final waves unless something is urgent.
    int bombCost = s.Packets.Where(p => (p.Type == CHERRY || p.Type == JALA || p.Type == DOOM) && p.Cd == 0).Select(p => p.Cost).DefaultIfEmpty(0).Min();
    if (bombCost > 0 && ((toFlag >= 1 && toFlag <= 2) || (s.Waves > 0 && s.Waves - s.Wave <= 2)))
      foreach (var cd in list) if (cd.P.Cost > 0 && cd.Score < 600 && s.Sun - cd.P.Cost < bombCost && cd.P.Type != CHERRY && cd.P.Type != JALA && cd.P.Type != DOOM) cd.Score *= 0.3;
    return list.OrderByDescending(c => c.Score).ToList();
  }

  // Pro decision: act on the best affordable move; save up only for something clearly better and only when safe.
  static Cand ProDecide(List<Cand> cands, State s, out string saving) {
    saving = null;
    var best = cands.FirstOrDefault();
    var ready = cands.FirstOrDefault(c => c.Affordable);
    if (best == null) return null;
    if (best.Affordable) return best;
    // The simulator says a lane breaks: take a ready move that holds it; otherwise save for the fix if income gets there in time.
    if (best.Why != null && best.Why.StartsWith("SIM")) {
      var readyFix = cands.FirstOrDefault(c => c.Affordable && c.Why != null && c.Why.StartsWith("SIM") && c.Why.Contains("holds it"));
      if (readyFix != null) return readyFix;
      int flowers = s.Plants.Count(p => ProSun.Contains(p.Type));
      double income = flowers * 25 / 24.0 + 1.5;
      double secs = (best.P.Cost - s.Sun) / income;
      if (secs < ProBreachT - 2) { saving = "saving for " + best.Label + " - it holds the lane; affordable in ~" + secs.ToString("0") + "s, breach in " + ProBreachT.ToString("0") + "s"; return cands.FirstOrDefault(c => c.Affordable && c.P.Cost == 0); }
      var readySim = cands.FirstOrDefault(c => c.Affordable && c.Why != null && c.Why.StartsWith("SIM"));
      if (readySim != null) return readySim;
    }
    bool urgent = UnderThreat && ready != null && ready.Score >= 500;
    if (!urgent && (ready == null || best.Score > ready.Score + 90) && best.P.Cost - s.Sun <= 200) {
      saving = "saving for " + best.Label + " (" + best.P.Cost + " sun, need " + (best.P.Cost - s.Sun) + " more)";
      var freebie = cands.FirstOrDefault(c => c.Affordable && c.P.Cost == 0);
      return freebie;
    }
    return ready;
  }

  // Panel helpers for pro mode (cached ~5 s: they read small files).
  static List<string> proKnow = new List<string>(); static string nemesisTop = "none yet"; static DateTime proKnowAt = DateTime.MinValue;
  public static string SuperNemesis() { RefreshProKnow(); return nemesisTop; }
  public static List<string> ProKnowledge() { RefreshProKnow(); return proKnow; }
  public static int lastVaseCount;
  static void RefreshProKnow() {
    if ((DateTime.Now - proKnowAt).TotalSeconds < 5) return;
    proKnowAt = DateTime.Now;
    var lines = new List<string>();
    try {
      var rec = File.Exists(@"C:\cbot\pro_record.txt") ? File.ReadAllLines(@"C:\cbot\pro_record.txt") : new string[0];
      var byLvl = rec.Select(l => System.Text.RegularExpressions.Regex.Match(l, @"PRO lvl (\d+) (WON|DIED)")).Where(m => m.Success)
                     .GroupBy(m => int.Parse(m.Groups[1].Value)).OrderBy(g => g.Key).ToList();
      Func<int, string> ln = id => ((id - 1) / 10 + 1) + "-" + ((id - 1) % 10 + 1);
      lines.Add("record: " + ProWins + " wins, " + ProDeaths + " deaths");
      lines.Add("deaths per level: " + string.Join("  ", byLvl.Select(g => ln(g.Key) + ":" + g.Count(m => m.Groups[2].Value == "DIED") + (g.Any(m => m.Groups[2].Value == "WON") ? "W" : ""))));
      var nem = File.Exists(@"C:\cbot\nemesis.txt") ? File.ReadAllLines(@"C:\cbot\nemesis.txt").Select(l => l.Split(' ').Last()).GroupBy(x => x).OrderByDescending(g => g.Count()).Take(5).ToList() : null;
      if (nem != null && nem.Count > 0) { nemesisTop = nem[0].Key + " (x" + nem[0].Count() + ")"; lines.Add("super nemesis: " + string.Join("  ", nem.Select(g => g.Key + " x" + g.Count()))); }
      if (PrecisionLevel) lines.Add("DARTH'S GAMBIT: vase round " + VaseRound + (wasHunting ? "  |  FOOTBALL HUNT ON" : "") + "  |  vases left " + lastVaseCount);
      lines.Add("plays from: zombie hp/speed tables, counters, area playbooks, 40s lane simulator - nothing is learned or saved");
    } catch (Exception e) { lines.Add("(panel: " + e.Message + ")"); }
    proKnow = lines;
  }

  // Pro results are kept apart from the learning bot's records.
  // Pro keeps its own tally (learned stats are deliberately not saved in pro mode, so Learn.Deaths resets on every restart).
  public static int ProDeaths = -1, ProWins = -1;
  public static void ProTally() {
    try { var lines = File.Exists(@"C:\cbot\pro_record.txt") ? File.ReadAllLines(@"C:\cbot\pro_record.txt") : new string[0];
          ProDeaths = lines.Count(l => l.Contains(" DIED")); ProWins = lines.Count(l => l.Contains(" WON")); } catch { }
  }
  static void ProRecord(string line) {
    try { File.AppendAllText(@"C:\cbot\pro_record.txt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine); } catch { }
    ProTally();
  }
}
