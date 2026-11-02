# 5-2 roof fix spec (from the read-only replay diagnosis, 2026-11-01 19:20) - apply to C:\cbot\pro.cs, match on text

Cause: cabbage-pult measured ~7 dps on the roof (1=7-8, 2=14, 3=20, 4=26, linear) vs ProDps 28; no roof cleaners in this mod.

A. before `  static double ProDps(int t) {` add
   `  static bool proRoof;   // set at the top of ProScore: on the roof the cabbage-pult does ~7 dps (measured), not 28`
B. `      case CABBAGE: return 28;` -> `      case CABBAGE: return proRoof ? 7 : 28;   // lvl 42 replays: 1 cabbage 7-8 dps, 2 = 14, 3 = 20, 4 = 26 (linear)`
C. after `    bool roof = bg == 4 || bg == 5, night = bg == 1 || bg == 3 || bg == 5;` add `    proRoof = roof;`
D. `      danger[r] = dps[r] > 0 ? hp / (dps[r] * secs + 1) : hp / (14 * secs + 1) * 0.8;`
   -> `      danger[r] = dps[r] > 0 ? hp / (dps[r] * secs + 1) : hp / ((roof ? 7 : 14) * secs + 1) * 0.8;   // roof: one cabbage is 7 dps`
E. `    Func<int, double> target = r => 14 * (1 + 4.5 * progress) + (flagSoon || finalSoon ? 28 : 0) + (s.Mowers[r] != 1 && s.Mowers[r] != 0 ? 28 : 0);`
   -> roof ? 7 * (1 + 4.5 * progress) + (flagSoon || finalSoon ? 14 : 0) : <old expression>
F. before `          if (sc <= 0) continue;` / `          double val = ProDps(t) / 14.0;` in the firepower block:
   roof && ProLobbed.Contains(t) && shooters[r] == 0 -> first cabbage score 950 (zombie visible) / 600 (wave>=1 and sunCount >= landRows.Count);
   after val: `if (roof && ProLobbed.Contains(t)) val = Math.Max(1, val);`
Unverified: kernel-pult (18) / melon-pult (56) likely overrated on the roof too (~5 / ~14) - measure when unlocked.
