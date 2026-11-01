// PRO MODE lane simulator: plays one lane forward ~30 game-seconds from what is on screen, the way a strong player
// does the arithmetic in their head (who reaches what, when, how much damage lands before that). Pure prediction:
// nothing here is stored or learned.
using System;
using System.Collections.Generic;
using System.Linq;

public static partial class Bot {
  public class SimOut { public double Cost, BreachT = 9999, HpLeft, Lost; public bool Breach { get { return BreachT < 9000; } } }
  class SZ { public double X, Hp, Spd, Hp0; public bool Slow, Eating; public string N; }
  class SP { public int Col, Type; public double Hp, Cost; }

  const double SIM_H = 40, SIM_DT = 0.25;
  // Where the simulator gets its numbers: expert stats (Pro) or the learning bot's own measurements.
  public class SimModel {
    public Func<int, double> Dps, Range, PlantHp; public Func<int, bool> Lob; public Func<int, string> Effect;   // AREA | LANE | SINGLE | MINE | ""
    public Func<Zom, double> ZSpeed, ZHp; public double MineArm = 15; public bool ZombieLore;
  }
  static SimModel proModel;
  static SimModel ProModel(Dictionary<uint, double> spd) {
    if (proModel == null) proModel = new SimModel { Dps = ProDps, Range = ProRange, PlantHp = ProPlantHp, Lob = t => ProLobbed.Contains(t), ZombieLore = true, MineArm = 15,
      Effect = t => t == CHERRY || t == DOOM ? "AREA" : t == JALA ? "LANE" : t == SQUASH || t == KELP ? "SINGLE" : t == PMINE ? "MINE" : "", ZHp = z => z.Total };
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
      ZSpeed = z => Learn.Speed(Learn.KeyOf(z)), ZHp = z => Perceived(z)
    };
  }
  public static void SeePlants(State s) { foreach (var p in s.Plants) { int h; if (p.MaxHp > 0 && (!seenPlantHp.TryGetValue(p.Type, out h) || p.MaxHp > h)) seenPlantHp[p.Type] = p.MaxHp; } }
  static SimOut SimLane(int r, State s, Dictionary<uint, double> spd, int addType = -1, int addCol = -1) { return SimLane(r, s, ProModel(spd), addType, addCol); }
  static SimOut SimLane(int r, State s, SimModel m, int addType = -1, int addCol = -1) {
    var zs = s.Zombies.Where(z => z.Row == r && z.X < VISIBLE_X + 40)
                      .Select(z => new SZ { X = z.X, Hp = m.ZHp(z), Hp0 = m.ZHp(z), Spd = Math.Max(4, m.ZSpeed(z)), N = m.ZombieLore ? Mem.ZomName(z.Type) : "" }).ToList();
    var ps = s.Plants.Where(p => (p.Row == r || (m.ZombieLore && p.Type == THREE && Math.Abs(p.Row - r) == 1)) && !ProIsPad(p.Type))
                     .Select(p => new SP { Col = p.Col, Type = p.Type, Hp = p.Row == r ? Math.Max(1, p.Hp) : 1e9, Cost = Math.Max(25, Mem.Cost(p.Type)) }).ToList();
    var o = new SimOut();
    if (zs.Count == 0) return o;
    string eff = addType >= 0 ? m.Effect(addType) : "";
    bool instant = eff == "AREA" || eff == "LANE" || eff == "SINGLE";
    double armAt = eff == "MINE" ? m.MineArm : -1;
    if (addType >= 0 && !instant) ps.Add(new SP { Col = addCol, Type = eff == "MINE" ? -2 : addType, Hp = m.PlantHp(addType), Cost = Math.Max(25, Mem.Cost(addType)) });
    for (double t = 0; t < SIM_H; t += SIM_DT) {
      // Instants go off almost at once.
      if (instant && t >= 0.5 && t < 0.5 + SIM_DT) {
        double cx = CellX(addCol);
        if (eff == "LANE") zs.Clear();
        else if (eff == "AREA") zs.RemoveAll(z => Math.Abs(z.X + 30 - cx) <= (addType == DOOM ? 250 : 115));
        else { var tgt = zs.Where(z => Math.Abs(z.X + 30 - cx) <= 100).OrderBy(z => z.X).FirstOrDefault(); if (tgt != null) zs.Remove(tgt); }
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
          int torches = m.ZombieLore && (p.Type == PEA || p.Type == REPEAT || p.Type == GATLING) ? Math.Min(3, ps.Count(q => q.Type == TORCH && q.Col > p.Col && q.Col * 80 < ahead[0].X)) : 0;
          ahead[0].Hp -= d * SIM_DT * (1 + torches);
          if (m.ZombieLore && p.Type == SNOW) ahead[0].Slow = true;
        }
      }
      // Spikeweed: hurts whatever walks over it; a zomboni driving onto it is destroyed (and so is the spike).
      if (m.ZombieLore) foreach (var sp in ps.Where(q => q.Type == SPIKE || q.Type == SPIKEROCK).ToList())
        foreach (var z in zs.Where(z => Math.Abs(z.X + 30 - CellX(sp.Col)) < 40)) {
          if (z.N == "ZAMB") { z.Hp = 0; if (sp.Type == SPIKE) sp.Hp = 0; } else z.Hp -= 20 * SIM_DT;
        }
      zs.RemoveAll(z => z.Hp <= 0);
      // A planned mine arms after ~15 s and blows the first zombie that walks onto it.
      if (armAt >= 0 && t >= armAt) {
        var hit = zs.FirstOrDefault(z => Math.Abs(z.X + 30 - CellX(addCol)) < 40);
        if (hit != null) { zs.Remove(hit); armAt = -1; }
      }
      // Zombies walk, or stop and eat the plant in front of them.
      foreach (var z in zs) {
        double slow = z.Slow ? 0.5 : 1;
        var block = ps.Where(p => p.Hp > 0 && p.Hp < 1e8 && p.Type >= 0 && m.Effect(p.Type) != "MINE" && p.Type != SPIKE && p.Type != SPIKEROCK && Math.Abs(z.X + 30 - CellX(p.Col)) < 35).OrderByDescending(p => p.Col).FirstOrDefault();
        z.Eating = block != null;
        if (block != null) {
          if ((z.N == "POLE" || z.N == "DOLPH") && block.Type != TALL) { z.X -= 80; z.N = "WALKED"; continue; }   // vaults over once
          // Mod: the DIRTY zombie instantly kills any non-wall plant (tip from the mod author); walls are eaten normally.
          bool wallBlock = block.Type == WALLNUT || block.Type == TALL || block.Type == PUMPKIN;
          double bite = z.N == "GARG" || z.N == "ZAMB" || (z.N == "DIRTY" && !wallBlock) ? 4000 : 100;
          block.Hp -= bite * SIM_DT * slow;
          if (block.Hp <= 0) { o.Lost += block.Cost; if (z.N == "REGEN") z.Hp = Math.Max(z.Hp, z.Hp0); }   // mod: green football regrows its helmet on every plant it kills
        } else z.X -= z.Spd * SIM_DT * slow;
        if (z.X < -60 && !o.Breach) o.BreachT = t;
      }
      ps.RemoveAll(p => p.Hp <= 0);
      if (o.Breach || zs.Count == 0) break;
    }
    o.HpLeft = zs.Sum(z => Math.Max(0, z.Hp));
    // Zombies still alive at the end are a debt, worse the closer they got: walls only delay, shooters pay it off.
    double debt = zs.Sum(z => Math.Max(0, z.Hp) * (1 + Math.Max(0, Math.Min(1, (400 - z.X) / 400))));
    o.Cost = (o.Breach ? 5000 + 120 * (SIM_H - o.BreachT) : 0) + debt + o.Lost;
    return o;
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
      foreach (var p in usable) {
        if (!defenders.Contains(p.Type) || !tried.Add(p.Type)) continue;
        // Area bombs reach neighbouring lanes, so they may be fired from an adjacent (e.g. land) lane - no pad needed.
        int[] lanes = p.Type == CHERRY ? new[] { r, r - 1, r + 1 } : p.Type == DOOM ? new[] { r, r - 1, r + 1, r - 2, r + 2 } : new[] { r };
        bool isBomb = p.Type == CHERRY || p.Type == JALA || p.Type == SQUASH || p.Type == DOOM;
        // A zomboni / gargantuar crushes whatever stands in its path: only its killers are worth placing there.
        bool crusher = s.Zombies.Any(z => z.Row == r && z.X < VISIBLE_X && (Mem.ZomName(z.Type) == "ZAMB" || Mem.ZomName(z.Type) == "GARG"));
        if (crusher && !isBomb && p.Type != SPIKE) continue;
        if (p.Type == SPIKE && !crusher) continue;   // spikeweed is the zomboni answer, not a general defence
        if (p.Type == SQUASH && PrecisionLevel && VaseRound >= 2 && !s.Zombies.Any(z => z.Row == r && z.X < VISIBLE_X && (z.Total >= 600 || Mem.ZomName(z.Type) == "FOOT"))) continue;   // vasebreaker: keep squashes for tough zombies
        // A squash only lands on a fast zombie while it stands still eating (mod author's solution for footballs).
        if (p.Type == SQUASH && s.Zombies.Any(z => z.Row == r && z.X < VISIBLE_X && (ProSpeedOf(Mem.ZomName(z.Type)) >= 25 || Mem.ZomName(z.Type) == "REGEN")
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
