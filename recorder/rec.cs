// PvZ Recorder: watches the game while a human plays and writes what happens to a text file, so the bot can learn the play.
// READ-ONLY: it only reads the game's memory (opened with query+read rights), never clicks, never writes, never touches
// other programs, and uses no network. The recording is a plain text file next to this exe - share it yourself if you want.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

static class Recorder {
  static StreamWriter w;
  static void Log(string s) { string line = DateTime.Now.ToString("HH:mm:ss.f") + " " + s; w.WriteLine(line); w.Flush(); Console.WriteLine(line); }
  static string Lvl(int id) { return id <= 0 ? "?" : ((id - 1) / 10 + 1) + "-" + ((id - 1) % 10 + 1); }
  static string Look(State s, int cell) { return s.LeafVases.Contains(cell) ? "green" : s.SkullVases.Contains(cell) ? "skull" : "?"; }

  static void Main() {
    Console.Title = "PvZ Recorder (read-only)";
    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pvz_recording_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
    w = new StreamWriter(path, false);
    Console.WriteLine("PvZ Recorder - READ-ONLY. Recording to:\n  " + path + "\nPlay normally. Close this window (or Ctrl+C) to stop.\n");
    Log("recorder started (read-only: reads game memory only, no clicks, no writes, no network)");

    var plants = new Dictionary<uint, Plt>(); var zombies = new HashSet<uint>(); var vases = new HashSet<int>();
    bool primed = false; int lastScene = -2, lastLevel = -1, lastWave = -1; string lastDave = ""; int lastCursor = -1; DateTime lastSnap = DateTime.MinValue;
    while (true) {
      State s = null;
      try { s = Mem.Get(); } catch { }
      if (s == null) { if (lastScene != -1) { Log("waiting for the game (LawnProject.exe)..."); lastScene = -1; } Thread.Sleep(1000); continue; }
      try {
        if (s.Scene != lastScene) { Log("SCENE " + lastScene + " -> " + s.Scene + (s.Scene == 3 ? " (playing " + Lvl(s.Level) + ")" : s.Scene == 4 ? " (GAME OVER)" : s.Scene == 5 ? " (LEVEL WON)" : "")); lastScene = s.Scene; }
        if (s.HasBoard && s.Level != lastLevel) { Log("LEVEL " + Lvl(s.Level) + " (background " + s.Background + ")"); lastLevel = s.Level; plants.Clear(); zombies.Clear(); vases.Clear(); primed = lastLevel < 0; }
        if (s.Wave != lastWave && s.HasBoard) { Log("WAVE " + s.Wave + "/" + s.Waves + "  sun " + s.Sun); lastWave = s.Wave; }
        string dave = Mem.DaveText(); if (!string.IsNullOrEmpty(dave) && dave != lastDave) { Log("DAVE: " + dave); lastDave = dave; }
        int cur = Mem.CursorType(); if (cur != lastCursor) { Log("CURSOR " + (cur == 0 ? "empty" : cur == 1 || cur == 2 ? "holding a seed" : cur == 3 ? "shovel" : cur == 7 ? "hammer" : cur.ToString())); lastCursor = cur; }

        // Vases broken (and what they looked like)
        var nowV = new HashSet<int>(s.Vases);
        foreach (var v in vases.Where(v => !nowV.Contains(v)).ToList()) Log("VASE BROKEN r" + v / 10 + "c" + v % 10);
        foreach (var v in nowV.Where(v => !vases.Contains(v)).ToList()) { }
        if (vases.Count == 0 && nowV.Count > 0) Log("VASES on lawn: " + string.Join(" ", nowV.OrderBy(v => v % 10).ThenBy(v => v / 10).Select(v => "r" + v / 10 + "c" + v % 10 + ":" + Look(s, v))));
        vases = nowV;

        // Plants placed / lost - the player's moves
        var nowP = s.Plants.ToDictionary(p => p.Id, p => p);
        if (!primed) { primed = true; plants = nowP; zombies = new HashSet<uint>(s.Zombies.Select(z => z.Id));
          Log("ALREADY ON BOARD: " + string.Join(" ", nowP.Values.Select(p => Mem.SeedName(p.Type) + "@r" + p.Row + "c" + p.Col))); }
        foreach (var p in nowP.Values.Where(p => !plants.ContainsKey(p.Id)))
          Log("PLANT " + Mem.SeedName(p.Type) + " at r" + p.Row + "c" + p.Col + "  | zombies: " + string.Join(" ", s.Zombies.OrderBy(z => z.X).Take(6).Select(z => Mem.ZomName(z.Type) + "@r" + z.Row + ":" + (int)z.X)));
        foreach (var p in plants.Values.Where(p => !nowP.ContainsKey(p.Id)))
          Log("GONE " + Mem.SeedName(p.Type) + " at r" + p.Row + "c" + p.Col);
        plants = nowP;

        // New zombies
        foreach (var z in s.Zombies.Where(z => !zombies.Contains(z.Id))) Log("ZOMBIE " + Mem.ZomName(z.Type) + " appears r" + z.Row + " x=" + (int)z.X + " hp=" + z.Total);
        zombies = new HashSet<uint>(s.Zombies.Select(z => z.Id));

        // Compact board snapshot every 2 s while playing
        if (s.Scene == 3 && (DateTime.Now - lastSnap).TotalSeconds >= 2) {
          lastSnap = DateTime.Now;
          Log("SNAP sun=" + s.Sun + " | Z: " + string.Join(" ", s.Zombies.Select(z => Mem.ZomName(z.Type) + "@r" + z.Row + ":" + (int)z.X + ":" + z.Total)) +
              " | P: " + string.Join(" ", s.Plants.Select(p => Mem.SeedName(p.Type) + "@r" + p.Row + "c" + p.Col)));
        }
      } catch (Exception e) { Log("read error: " + e.Message); Thread.Sleep(500); }
      Thread.Sleep(100);
    }
  }
}
