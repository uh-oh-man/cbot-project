// PRO MODE lane simulator: plays one lane forward ~40 game-seconds from what is on screen, the way a strong player
// does the arithmetic in their head (who reaches what, when, how much damage lands before that). Pure prediction:
// nothing here is stored or learned. Pro's HP numbers come only from Seen (what the screen shows).
using System;
using System.Collections.Generic;
using System.Linq;

public static partial class Bot {
  public class SimOut { public double Cost, BreachT = 9999, HpLeft, Lost; public bool Breach { get { return BreachT < 9000; } } }
  class SZ { public uint Id; public double X, Hp, Spd, Hp0; public bool Slow, Eating; public string N; }
  // Mine: a potato mine (eaten while unarmed, blows up once armed); Trap: a waiting squash / tangle kelp (one shot); Busy: chomper chewing until.
  class SP { public int Col, Type; public double Hp, Cost, ArmAt = -1, Busy; public bool Mine, Trap; }

  const double SIM_H = 40, SIM_DT = 0.25;
  // Where the simulator gets its numbers: expert stats (Pro) or the learning bot's own measurements.
  public class SimModel {
    public Func<int, double> Dps, Range, PlantHp; public Func<int, bool> Lob; public Func<int, string> Effect;   // AREA | LANE | SINGLE | MINE | ""
    public Func<Zom, double> ZSpeed, ZHp; public Func<Plt, double> PHp; public double MineArm = 15; public bool ZombieLore;   // PHp: HP of a plant already on the lawn
  }
  static SimModel proModel;
  static SimModel ProModel(Dictionary<uint, double> spd) {
    if (proModel == null) proModel = new SimModel { Dps = ProDps, Range = ProRange, PlantHp = ProPlantHp, Lob = t => ProLobbed.Contains(t), ZombieLore = true, MineArm = 15,
      Effect = t => t == CHERRY || t == DOOM ? "AREA" : t == JALA ? "LANE" : t == SQUASH || t == KELP ? "SINGLE" : t == PMINE ? "MINE" : "",
      ZHp = z => Seen.Hp(z), PHp = p => Seen.PlantHp(p) };   // visible estimates only: health bars when on, damage states otherwise
    proModel.ZSpeed = z => spd.ContainsKey(z.Id) ? spd[z.Id] : 17;
    return proModel;
  }

  // Add = a plant we might place now (type, col); Remove = an instant used now (type, col).
  // The learning bot's model: only numbers it measured itself (no expert stats, no zombie lore).
  static readonly Dictionary<int, int> seenPlantHp = new Dictionary<int, int>();
  static SimModel LearnedModel() {
    return new SimModel {
      Dps = t => Roles.Is(t, "SHOOT") ? Learn.Dps(t) : 0,
      Range = t => { double rr = Learn.Range(t); return rr >= 800 ? 0 : rr; },
      PlantHp = t => { int h; return seenPlantHp.TryGetValue(t, out h) ? h : 300; },
      Lob = t => false, ZombieLore = false, MineArm = Learn.MineArmTrusted,
      Effect = t => Roles.Is(t, "INSTANT") ? "AREA" : Roles.Is(t, "MINE") ? "MINE" : "",
      ZSpeed = z => Learn.Speed(Learn.KeyOf(z)), ZHp = z => Perceived(z), PHp = p => Seen.ExactPlantHp(p)
    };
  }
  public static void SeePlants(State s) { foreach (var p in s.Plants) { int h; if (p.MaxHp > 0 && (!seenPlantHp.TryGetValue(p.Type, out h) || p.MaxHp > h)) seenPlantHp[p.Type] = p.MaxHp; } }
  static SimOut SimLane(int r, State s, Dictionary<uint, double> spd, int addType = -1, int addCol = -1) { return SimLane(r, s, ProModel(spd), addType, addCol); }
  // probe (calibration only): filled with where each zombie is at t = SimCal.PROBE_T; ids missing = the sim killed it by then.
  static SimOut SimLane(int r, State s, SimModel m, int addType = -1, int addCol = -1, Dictionary<uint, double> probe = null) {
    Seen.Frame(s);   // idempotent: plant ages / chomper state for this frame
    double gt = s.GameTime;
    Func<Plt, double> phLive = m.PHp ?? (Func<Plt, double>)Seen.ExactPlantHp;
    // Same visibility as Pro's live list: nothing at or right of VISIBLE_X; hypnotized zombies fight for us (allies below).
    var zs = new List<SZ>(); var allies = new List<SZ>();
    foreach (var z in s.Zombies) {
      if (z.Row != r || z.X >= VISIBLE_X) continue;
      double hp = m.ZHp(z);
      var q = new SZ { Id = z.Id, X = z.X, Hp = hp, Hp0 = hp, Spd = Math.Max(4, m.ZSpeed(z)), Eating = z.Eating, N = m.ZombieLore ? Mem.ZomName(z.Type) : "" };
      if (z.Charmed) allies.Add(q); else zs.Add(q);
    }
    var o = new SimOut();
    if (zs.Count == 0) return o;
    var ps = new List<SP>();
    foreach (var p in s.Plants) {
      if (!(p.Row == r || (m.ZombieLore && p.Type == THREE && Math.Abs(p.Row - r) == 1)) || ProIsPad(p.Type)) continue;
      var q = new SP { Col = p.Col, Type = p.Type, Hp = p.Row == r ? Math.Max(1, phLive(p)) : 1e9, Cost = Math.Max(25, Mem.Cost(p.Type)) };
      if (p.Row == r) {
        if (m.Effect(p.Type) == "MINE") { q.Mine = true; q.ArmAt = Math.Max(0, m.MineArm - Seen.PlantAge(p, gt)); }   // a mine planted 15 s ago is up
        if (m.ZombieLore && (p.Type == SQUASH || p.Type == KELP)) q.Trap = true;
        if (m.ZombieLore && p.Type == CHOMP) q.Busy = Seen.ChompBusy(p, gt);
      }
      ps.Add(q);
    }
    // Bombs already on the lawn (just planted, fuse burning) go off too - otherwise the sim would plant a second one.
    bool night = s.Background == 1 || s.Background == 3 || s.Background == 5;
    var bombs = m.ZombieLore ? s.Plants.Where(p => (p.Type == CHERRY && Math.Abs(p.Row - r) <= 1) || (p.Type == JALA && p.Row == r) || (p.Type == DOOM && night && Math.Abs(p.Row - r) <= 2)).ToList() : null;
    string eff = addType >= 0 ? m.Effect(addType) : "";
    bool instant = eff == "AREA" || eff == "LANE" || eff == "SINGLE";
    if (addType >= 0 && !instant) {
      var q = new SP { Col = addCol, Type = addType, Hp = m.PlantHp(addType), Cost = Math.Max(25, Mem.Cost(addType)) };
      if (eff == "MINE") { q.Mine = true; q.ArmAt = m.MineArm; }
      ps.Add(q);
    }
    bool probed = false;
    for (double t = 0; t < SIM_H; t += SIM_DT) {
      if (probe != null && !probed && t >= SimCal.PROBE_T) { foreach (var z in zs) probe[z.Id] = z.X; probed = true; }
      // Instants go off almost at once.
      if (t >= 0.5 && t < 0.5 + SIM_DT) {
        if (bombs != null) foreach (var bp in bombs) SimBlast(zs, bp.Type == JALA ? "LANE" : "AREA", bp.Type == DOOM, CellX(bp.Col));
        if (instant) SimBlast(zs, eff, addType == DOOM, CellX(addCol));
      }
      // Shooters hit the first zombie ahead of them within reach (fume hits everything in reach).
      foreach (var p in ps) {
        if (p.Type < 0) continue;
        double d = m.Dps(p.Type);
        if (d <= 0) continue;
        double px = CellX(p.Col), rng = m.Range(p.Type);
        // Snorkel zombies swim under straight shots until they surface to eat; lobbed shots still land.
        bool lob = m.Lob(p.Type);
        var ahead = zs.Where(z => z.X + 30 > px - 20 && z.X < 780 && (rng <= 0 || z.X + 30 - px <= rng) && (lob || z.N != "SNORK" || z.Eating)).OrderBy(z => z.X).ToList();
        if (ahead.Count == 0) continue;
        if (m.ZombieLore && (p.Type == FUME || p.Type == GLOOM)) foreach (var z in ahead) z.Hp -= d * SIM_DT;
        else {
          // Stacked torchwoods (this mod): each one a pea passes through raises its damage (x2, x3, x4).
          int torches = m.ZombieLore && (p.Type == PEA || p.Type == REPEAT || p.Type == GATLING) ? Math.Min(3, ps.Count(q => q.Type == TORCH && q.Col > p.Col && CellX(q.Col) < ahead[0].X + 30)) : 0;
          ahead[0].Hp -= d * SIM_DT * (1 + torches);
          if (m.ZombieLore && p.Type == SNOW) ahead[0].Slow = true;
        }
      }
      // Spikeweed: hurts whatever walks over it; a zomboni driving onto it is destroyed (and so is the spike).
      if (m.ZombieLore) foreach (var sp in ps.Where(q => q.Type == SPIKE || q.Type == SPIKEROCK).ToList())
        foreach (var z in zs.Where(z => Math.Abs(z.X + 30 - CellX(sp.Col)) < 40)) {
          if (z.N == "ZAMB") { z.Hp = 0; if (sp.Type == SPIKE) sp.Hp = 0; } else z.Hp -= 20 * SIM_DT;
        }
      // Hypnotized zombies walk right and brawl with the first enemy they meet.
      foreach (var a in allies) {
        var foe = zs.Where(z => Math.Abs(z.X - a.X) < 40).OrderBy(z => z.X).FirstOrDefault();
        if (foe != null) foe.Hp -= 100 * SIM_DT; else a.X += a.Spd * SIM_DT;
      }
      zs.RemoveAll(z => z.Hp <= 0);
      // Plants that act on contact: an armed mine blows its tile, a waiting squash / kelp takes the first zombie in reach, a free chomper eats one.
      foreach (var p in ps) {
        if (p.Hp <= 0 || p.Hp >= 1e8) continue;
        double px = CellX(p.Col);
        if (p.Mine && t >= p.ArmAt) {
          if (zs.Any(z => Math.Abs(z.X + 30 - px) < 40)) { zs.RemoveAll(z => Math.Abs(z.X + 30 - px) < 40); p.Hp = 0; }
        } else if (p.Trap) {
          var tg = zs.Where(z => z.X + 30 - px > -50 && z.X + 30 - px < 90).OrderBy(z => z.X).FirstOrDefault();
          if (tg == null) continue;
          // Mod author: a fast zombie on the move dodges a squash (it lands behind); one stopped to eat does not.
          bool dodge = p.Type == SQUASH && (ProSpeedOf(tg.N) >= 25 || tg.N == "REGEN") && !tg.Eating;
          if (!dodge) zs.Remove(tg);
          p.Hp = 0;
        } else if (m.ZombieLore && p.Type == CHOMP && t >= p.Busy) {
          var tg = zs.Where(z => z.X + 30 - px > -30 && z.X + 30 - px < 90 && z.N != "GARG" && z.N != "ZAMB").OrderBy(z => z.X).FirstOrDefault();
          if (tg != null) { zs.Remove(tg); p.Busy = t + Seen.CHOMP_BUSY; }
        }
      }
      // Zombies walk, or stop and eat the plant (or brawl with the ally) in front of them.
      foreach (var z in zs) {
        double slow = z.Slow ? 0.5 : 1;
        bool crush = z.N == "GARG" || z.N == "ZAMB";
        var ally = allies.FirstOrDefault(a => a.Hp > 0 && Math.Abs(a.X - z.X) < 40);
        var block = ally != null ? null : ps.Where(p => p.Hp > 0 && p.Hp < 1e8 && p.Type >= 0 && !(p.Mine && t >= p.ArmAt) && !p.Trap && p.Type != SPIKE && p.Type != SPIKEROCK && Math.Abs(z.X + 30 - CellX(p.Col)) < 35).OrderByDescending(p => p.Col).FirstOrDefault();
        z.Eating = ally != null || block != null;
        if (ally != null) ally.Hp -= (crush ? 4000 : 100) * SIM_DT * slow;
        else if (block != null) {
          if ((z.N == "POLE" || z.N == "DOLPH") && block.Type != TALL) { z.X -= 80; z.N = "WALKED"; z.Eating = false; }   // vaults over once (and may land past the house)
          else {
            // Mod: the DIRTY zombie instantly kills any non-wall plant (tip from the mod author); walls are eaten normally.
            bool wallBlock = block.Type == WALLNUT || block.Type == TALL || block.Type == PUMPKIN;
            double bite = crush || (z.N == "DIRTY" && !wallBlock) ? 4000 : 100;
            block.Hp -= bite * SIM_DT * slow;
            if (block.Hp <= 0) { o.Lost += block.Cost; if (z.N == "REGEN") z.Hp = Math.Max(z.Hp, z.Hp0); }   // mod: green football regrows its helmet on every plant it kills
          }
        } else {
          z.X -= z.Spd * SIM_DT * slow;
          if (m.ZombieLore && z.N == "BDANCE") z.Hp += VasePlanner.BDANCE_GROW * SIM_DT;   // mod: backups grow while they walk (not while chewing)
        }
        if (z.X < -60 && !o.Breach) o.BreachT = t;
      }
      ps.RemoveAll(p => p.Hp <= 0);
      allies.RemoveAll(a => a.Hp <= 0 || a.X > 800);
      if (o.Breach || zs.Count == 0) break;
    }
    if (probe != null && !probed) foreach (var z in zs) probe[z.Id] = z.X;
    o.HpLeft = zs.Sum(z => Math.Max(0, z.Hp));
    // Zombies still alive at the end are a debt, worse the closer they got: walls only delay, shooters pay it off.
    double debt = zs.Sum(z => Math.Max(0, z.Hp) * (1 + Math.Max(0, Math.Min(1, (400 - z.X) / 400))));
    o.Cost = (o.Breach ? 5000 + 120 * (SIM_H - o.BreachT) : 0) + debt + o.Lost;
    return o;
  }
  // One instant going off at x = cx: LANE clears the lane, AREA hits around it (doom much wider), SINGLE takes the nearest.
  static void SimBlast(List<SZ> zs, string eff, bool wide, double cx) {
    if (eff == "LANE") zs.Clear();
    else if (eff == "AREA") zs.RemoveAll(z => Math.Abs(z.X + 30 - cx) <= (wide ? 250 : 115));
    else if (eff == "SINGLE") { var tgt = zs.Where(z => Math.Abs(z.X + 30 - cx) <= 100).OrderBy(z => z.X).FirstOrDefault(); if (tgt != null) zs.Remove(tgt); }
  }
  static double ProPlantHp(int t) { return t == WALLNUT ? 4000 : t == TALL ? 8000 : t == PUMPKIN ? 4000 : 300; }

  // For every lane the simulator says will break, try each plant we can place now and score what actually fixes it.
  static void ProSimDefence(State s, List<Pkt> usable, List<int> rows, Dictionary<uint, double> spd, Func<int, int, int, bool> canPlace,
                            Func<int, int, bool> needsBase, Action<Pkt, int, int, double, string> add, Dictionary<int, SimOut> baseOut) {
    var defenders = new HashSet<int> { PEA, REPEAT, SNOW, THREE, FUME, PUFF, SCAREDY, SEASH, CABBAGE, KERNEL, MELON, CACTUS, SPLIT, STAR, GATLING, GLOOM,
                                       WALLNUT, TALL, PUMPKIN, CHERRY, JALA, SQUASH, KELP, PMINE, DOOM, SPIKE };   // torchwood is placed by its own rule: in front of pea shooters
    // Opening: until there is one sun maker per land lane, only a breach that is close in time is worth reacting to.
    var rtypes = Mem.RowTypes();
    bool opening = s.Plants.Count(q => ProSun.Contains(q.Type)) < rows.Count(rr => rtypes[rr] != 2);
    foreach (var r in rows) {
      var b = baseOut[r];
      if (!b.Breach && b.HpLeft < 300) continue;   // this lane holds by itself
      if (opening && (!b.Breach || b.BreachT > 20)) continue;
      var tried = new HashSet<int>();
      // Of two dropped packets of one kind, try the one nearer to fading (its disappear counter) so the younger one is kept.
      foreach (var p in usable.OrderByDescending(u => { if (!u.FromCoin) return -1; var cn = s.Suns.FirstOrDefault(x => x.Id == u.CoinId); return cn != null ? cn.Disappear : -1; })) {
        if (!defenders.Contains(p.Type) || !tried.Add(p.Type)) continue;
        // Area bombs reach neighbouring lanes, so they may be fired from an adjacent (e.g. land) lane - no pad needed.
        int[] lanes = p.Type == CHERRY ? new[] { r, r - 1, r + 1 } : p.Type == DOOM ? new[] { r, r - 1, r + 1, r - 2, r + 2 } : new[] { r };
        bool isBomb = p.Type == CHERRY || p.Type == JALA || p.Type == SQUASH || p.Type == DOOM;
        // A zomboni / gargantuar crushes whatever stands in its path: only its killers are worth placing there.
        bool crusher = s.Zombies.Any(z => z.Row == r && z.X < VISIBLE_X && !z.Charmed && (Mem.ZomName(z.Type) == "ZAMB" || Mem.ZomName(z.Type) == "GARG"));
        if (crusher && !isBomb && p.Type != SPIKE) continue;
        if (p.Type == SPIKE && !crusher) continue;   // spikeweed is the zomboni answer, not a general defence
        if (p.Type == SQUASH && PrecisionLevel && !s.Zombies.Any(z => z.Row == r && z.X < VISIBLE_X && VasePlanner.SpendSquashOn(s, z, false))) continue;   // vasebreaker: the planner's squash reserve (footballs first)
        // A squash only lands on a fast zombie while it stands still eating (mod author's solution for footballs).
        if (p.Type == SQUASH && s.Zombies.Any(z => z.Row == r && z.X < VISIBLE_X && !z.Charmed && (ProSpeedOf(Mem.ZomName(z.Type)) >= 25 || Mem.ZomName(z.Type) == "REGEN")
              && !s.Plants.Any(q => q.Row == r && !ProIsPad(q.Type) && Math.Abs(CellX(q.Col) - (z.X + 30)) < 45))) continue;
        // A wall only buys time for shooters behind it; with none in the lane it just delays the same breach.
        bool wall = p.Type == WALLNUT || p.Type == TALL || p.Type == PUMPKIN;
        if (wall && !s.Plants.Any(q => q.Row == r && ProIsShooter(q.Type))) continue;
        double bestGain = 0; int bestRow = -1, bestCol = -1; SimOut bestO = null;
        foreach (int pr in lanes) {
          if (!rows.Contains(pr)) continue;
          for (int c = 0; c < 9; c++) {
            if (!canPlace(p.Type, pr, c) && (isBomb || !needsBase(pr, c))) continue;   // a bomb never waits for a pad
            var o = SimLane(r, s, spd, p.Type, c);
            double gain = b.Cost - o.Cost;
            if (gain > bestGain + 1) { bestGain = gain; bestRow = pr; bestCol = c; bestO = o; }
          }
        }
        if (bestO == null || bestGain < 50) continue;
        bool fixes = b.Breach && !bestO.Breach;
        // Bombs are for real emergencies or big piles - not a lone zombie a shooter could handle.
        if (isBomb && !crusher && !((b.Breach && b.BreachT < 18) || b.HpLeft >= 1200)) continue;
        double sc = 900 + Math.Min(700, bestGain / 8) + (fixes ? 500 : 0) - 0.8 * p.Cost + (b.Breach ? 300 * Math.Max(0, 1 - b.BreachT / SIM_H) : 0) - (isBomb && !crusher && b.BreachT >= 10 ? 250 : 0) + (crusher ? 400 : 0) + (b.Breach && s.Mowers[r] != 1 && s.Mowers[r] != 0 ? 300 : 0);   // no mower/cleaner there: a breach is game over
        string why = "SIM lane " + r + (b.Breach ? " breaks in " + b.BreachT.ToString("0") + "s" : " leaks " + b.HpLeft.ToString("0") + " hp")
                   + (fixes ? " -> " + Mem.SeedName(p.Type) + " here holds it" : " -> " + Mem.SeedName(p.Type) + " here helps (" + (bestO.Breach ? "breaks at " + bestO.BreachT.ToString("0") + "s" : "holds") + ")");
        add(p, bestRow, bestCol, sc, why);
      }
    }
  }
}
