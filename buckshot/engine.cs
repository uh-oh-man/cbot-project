// Buckshot Roulette decision core (game-independent). Tracks what a player can know: shells loaded (live/blank counts
// shown at the start of each load), shells fired so far, shells revealed by items, and health - then picks the move
// with the best odds. Item support is basic for now; refine once the game is on the VM and its exact rules are confirmed.
using System;
using System.Collections.Generic;
using System.Linq;

public class Table {
  public int Live, Blank;              // shells still in the gun (counts known to every player)
  public bool? NextKnown;              // magnifying glass etc.: true = next is live, false = blank, null = unknown
  public int MyHp, DealerHp, MaxHp = 4;
  public List<string> MyItems = new List<string>();
  public double PLiveNext { get { if (NextKnown.HasValue) return NextKnown.Value ? 1 : 0; int n = Live + Blank; return n == 0 ? 0 : (double)Live / n; } }
}

public static class Brain {
  // One move: use an item, shoot the dealer, or shoot yourself (a blank on yourself keeps your turn).
  public static string Decide(Table t, out string why) {
    double p = t.PLiveNext;
    if (t.Live + t.Blank == 0) { why = "gun empty - wait for reload"; return "WAIT"; }
    // Unknown next shell and a glass in hand: look first.
    if (!t.NextKnown.HasValue && t.MyItems.Contains("magnifier") && p > 0 && p < 1) { why = "next shell unknown (" + (p * 100).ToString("0") + "% live) - check it"; return "USE magnifier"; }
    // Low on health and a cigarette/medicine: heal before risking anything.
    if (t.MyHp <= 1 && t.MyItems.Contains("cigarettes") && t.MyHp < t.MaxHp) { why = "1 HP - heal first"; return "USE cigarettes"; }
    // Certain live: make it hurt more if possible, then shoot the dealer.
    if (p >= 0.999) {
      if (t.MyItems.Contains("handsaw")) { why = "next is LIVE - saw for double damage"; return "USE handsaw"; }
      why = "next is LIVE - shoot dealer"; return "SHOOT DEALER";
    }
    // Certain blank: shoot yourself, keep the turn for free.
    if (p <= 0.001) { why = "next is BLANK - shoot self, keep the turn"; return "SHOOT SELF"; }
    // Beer ejects the current shell: worth it when the odds are bad for both choices (~50/50).
    if (t.MyItems.Contains("beer") && Math.Abs(p - 0.5) < 0.15) { why = "coin flip (" + (p * 100).ToString("0") + "% live) - rack it out with beer"; return "USE beer"; }
    // Otherwise: more likely live -> shoot the dealer; more likely blank -> shoot self (a blank on self keeps the turn).
    if (p >= 0.5) { why = (p * 100).ToString("0") + "% live - shoot dealer"; return "SHOOT DEALER"; }
    why = (p * 100).ToString("0") + "% live - shoot self (likely blank, keeps the turn)"; return "SHOOT SELF";
  }
  // After a shot is seen, update what is known.
  public static void Fired(Table t, bool wasLive) { if (wasLive) t.Live--; else t.Blank--; t.NextKnown = null; }
}

static class EngineTest {
  static void Main() {
    var t = new Table { Live = 2, Blank = 3, MyHp = 3, DealerHp = 3 };
    string why;
    Console.WriteLine(Brain.Decide(t, out why) + "  // " + why);           // 40% live -> shoot self
    t.MyItems.Add("magnifier"); Console.WriteLine(Brain.Decide(t, out why) + "  // " + why);
    t.NextKnown = true; t.MyItems.Add("handsaw"); Console.WriteLine(Brain.Decide(t, out why) + "  // " + why);
    var t2 = new Table { Live = 3, Blank = 0, MyHp = 2, DealerHp = 4 }; Console.WriteLine(Brain.Decide(t2, out why) + "  // " + why);
    var t3 = new Table { Live = 0, Blank = 2, MyHp = 2, DealerHp = 2 }; Console.WriteLine(Brain.Decide(t3, out why) + "  // " + why);
  }
}
