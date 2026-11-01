// The bot's inner monologue. Every line is built from real event details plus random fragments,
// and no line is ever said twice (said.txt keeps the history across runs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public static class Quips {
  const string SAID = @"C:\cbot\said.txt";
  static readonly Random R = new Random();
  static HashSet<string> said;
  static DateTime lastSmall = DateTime.MinValue, lastBig = DateTime.MinValue, lastBrag = DateTime.MinValue, levelStart = DateTime.Now;
  static readonly List<DateTime> recentKills = new List<DateTime>();
  static double bestReaction = 9999;
  static bool hoarding;
  public static readonly Dictionary<string, int> PlantsEatenBy = new Dictionary<string, int>();
  public static string Nemesis = "none yet";
  public static int Wave, Sun;

  static string P(string[] a) { return a[R.Next(a.Length)]; }
  static string Mins { get { var t = DateTime.Now - levelStart; return t.TotalSeconds < 60 ? (int)t.TotalSeconds + "s" : t.Minutes + "m" + t.Seconds.ToString("00") + "s"; } }

  // Try a few random combinations; speak the first one never said before, or stay quiet.
  static void Say(bool big, Func<string> build, string prefix = "SAYS") {
    var now = DateTime.Now;
    if (prefix == "FAITH") { if ((now - lastFaith).TotalSeconds < 45) return; }
    else if (big ? (now - lastBig).TotalSeconds < 12 : (now - lastSmall).TotalSeconds < 40 || (now - lastBig).TotalSeconds < 12) return;
    if (said == null) { said = new HashSet<string>(); try { foreach (var l in File.ReadAllLines(SAID)) said.Add(l); } catch { } }
    for (int i = 0; i < 25; i++) {
      string line = build();
      if (said.Contains(line)) continue;
      said.Add(line); try { File.AppendAllText(SAID, line + Environment.NewLine); } catch { }
      if (prefix == "FAITH") lastFaith = now; else if (big) lastBig = now; else lastSmall = now;
      Viz.AddEvent(prefix + " \"" + line + "\"");
      return;
    }
  }

  static readonly string[] Open = { "", "ok so", "update:", "fun fact:", "for the record,", "note to self:", "breaking news:", "dear diary,", "honestly", "listen," };
  static readonly string[] Shrug = { "", "anyway.", "moving on.", "it is what it is.", "i'm fine.", "no further questions.", "this is fine.", "carry on.", "big if true.", "classic." };
  static string J(params string[] parts) { return string.Join(" ", parts.Where(p => p.Length > 0)).Trim(); }

  static DateTime lastFaith = DateTime.MinValue;
  public static string Threat = "zombie";   // kind of the zombie closest to the house, set by the bot
  static readonly string[] Deity = { "the Sun God", "Saint Wall-nut", "the Holy Lawnmower", "Crazy Dave", "the Great Sunflower", "the Pea Pope", "whoever invented potatoes", "the brain-free afterlife", "the Tallnut of Destiny", "the patron saint of lane " };

  public static void Pray(int tier, int lane, double faith) {
    Say(true, () => {
      string god = P(Deity); if (god.EndsWith("lane ")) god += lane;
      string plea;
      if (tier <= 3) plea = P(new[] { "dear " + god + ", 25 more sun would be lovely", "lights a small candle for lane " + lane, "whispers a quick prayer to " + god + " about that " + Threat, "asks " + god + " for a gentle wave " + Wave });
      else if (tier == 4) plea = P(new[] { "lane " + lane + " is now in the hands of " + god, "has joined the church of " + god + " (faith " + faith.ToString("0") + "%)", "is bargaining with " + god + " over a " + Threat, "promises " + god + " to plant more sunflowers if lane " + lane + " holds" });
      else if (tier == 5) plea = P(new[] { "speaks in tongues at the " + Threat + " in lane " + lane, "offers " + Sun + " sun as tribute to " + god, "swears to " + god + " it will never skip a sunflower again", "is chanting peashooter psalms at " + faith.ToString("0") + "% faith" });
      else plea = P(new[] { "has become a full prophet of " + god + ". the light is a " + Threat + "'s mouth", "sees " + god + " in the lawn. it is lane " + lane + ". it is on fire", "has ascended to " + faith.ToString("0") + "% faith and is writing scripture about lane " + lane });
      return J(plea + ".", P(new[] { "", "", "amen.", "pls.", "(it's wave " + Wave + ")", "no atheists in foxholes.", "" }));
    }, "FAITH");
  }
  public static void Answered(double peak) {
    Say(true, () => J(P(new[] { "prayers answered after peaking at " + peak.ToString("0") + "% faith.", "survived. faith drops from " + peak.ToString("0") + "% to casual.", "crisis over. quietly unsubscribes from " + P(Deity).Replace("lane ", "lawns") + "." }),
      P(new[] { "back to science.", "was never worried.", "deleting the prayer app.", "i owe somebody a candle." })), "FAITH");
  }
  public static void Forsaken(int lane, double faith) {
    lastFaith = DateTime.MinValue;   // always mourn a death
    Say(true, () => J(P(new[] { P(Deity).Replace("lane ", "lane " + lane) + " has forsaken lane " + lane + ".", "faith was " + faith.ToString("0") + "% and it still died.", "lane " + lane + " prayed and the " + Threat + " answered instead." }),
      P(new[] { "converting to agnosticism.", "filing a complaint.", "theology is hard.", "the brains are gone. so is the faith." })), "FAITH");
  }

  public static void Bought(string what) {
    Say(true, () => J(P(new[] { "bought something from Demon Dave.", "did business with a man wearing a pot.", "spent coins at Dave's totally legal shop." }), P(new[] { "no refunds, apparently.", "worth it.", "he smelled like tacos.", "the receipt is a napkin." })));
  }

  public static void Warned() {
    Say(true, () => J(P(new[] { "the game itself just told me my loadout is bad.", "got a warning from the game about my plant picks.", "the seed chooser judged me." }), P(new[] { "rude, but fair.", "fine. I'll try something new.", "humbling.", "noted in the plant journal." })));
  }

  public static void LevelStart() { levelStart = DateTime.Now; recentKills.Clear(); }

  public static void Planted(string seed, int row) {
    if (R.NextDouble() > 0.12) return;
    Say(false, () => {
      string core;
      switch (seed) {
        case "SUNF": core = P(new[] { "row " + row + " is getting a solar panel with a face", "planted sunflower at wave " + Wave + ", the economy thanks me", "invested 50 sun into a flower at " + Mins + " in", "row " + row + " is now officially 'eco friendly'" }); break;
        case "PEA": core = P(new[] { "row " + row + " just hired a legume with a gun", "armed row " + row + " with peas at " + Sun + " sun left", "row " + row + " peashooter installed, " + Mins + " into this mess", "pea count in row " + row + " went up. zombie morale went down" }); break;
        case "WALL": core = P(new[] { "row " + row + " got a nut that believes in itself", "placed a wall-nut in row " + row + " and wished it luck", "row " + row + " now has a very round bodyguard" }); break;
        case "MINE": core = P(new[] { "buried a potato in row " + row + ". it has trust issues", "row " + row + " has a secret now", "potato deployed in row " + row + " at wave " + Wave }); break;
        default: core = "planted " + seed.ToLower() + " in row " + row; break;
      }
      return J(P(Open), core + ".", P(Shrug));
    });
  }

  public static void Kill() {
    var now = DateTime.Now; recentKills.Add(now); recentKills.RemoveAll(t => (now - t).TotalSeconds > 2);
    int n = recentKills.Count;
    if (n < 3) return;   // only real multi-kills
    string[] names = { "", "", "", "TRIPLE KILL", "QUAD KILL", "PENTA-PEA KILL", "HEXA-HARVEST", "ULTRA SALAD" };
    string name = n < names.Length ? names[n] : "LAWN APOCALYPSE x" + n;
    Say(true, () => J(name + "!", P(new[] { n + " zombies down in 2 seconds at " + Mins + ".", "wave " + Wave + " just lost " + n + " employees.", "the peas did " + n + " at once. unionizing next.", n + " brains no longer in service." }), P(Shrug)));
  }

  public static void Reaction(double ms) {
    if (ms <= 0 || ms > bestReaction * 0.85 || (DateTime.Now - lastBrag).TotalMinutes < 3) return;
    bestReaction = ms; lastBrag = DateTime.Now;
    Say(true, () => J(P(new[] { "grabbed sun in " + ms.ToString("0") + " ms.", "sun collected " + ms.ToString("0") + " ms after it appeared.", ms.ToString("0") + " ms sun grab." }),
      P(new[] { "new personal best.", "humans could never.", "the sun didn't even land.", "i am the flash, but leafy." })));
  }

  public static void Hoard(int sun) {
    Sun = sun;
    if (sun >= 500 && !hoarding) { hoarding = true; Say(false, () => J(P(Open), "sitting on " + sun + " sun at wave " + Wave + ".", P(new[] { "scrooge mode.", "should probably spend that.", "saving for retirement.", "dragon hoard energy." }))); }
    if (sun < 300) hoarding = false;
  }

  public static void PlantEaten(string seed, string byKind) {
    if (byKind == null) return;
    int n; PlantsEatenBy.TryGetValue(byKind, out n); PlantsEatenBy[byKind] = ++n;
    var top = PlantsEatenBy.OrderByDescending(kv => kv.Value).First();
    if (top.Key != Nemesis && top.Value >= 2) {
      Nemesis = top.Key;
      Say(true, () => J("NEW NEMESIS:", top.Key + ".", "it has eaten " + top.Value + " of my plants.", P(new[] { "this is personal now.", "i will remember this.", "i've started a journal about it.", "we are enemies forever." })));
    } else Say(false, () => J(P(Open), "a " + byKind + " ate my " + seed.ToLower() + " at " + Mins + ".", P(new[] { "rude.", "thoughts and prayers.", "it had a family. of peas.", "it will be replanted. probably.", "adding that to the list." })));
  }

  public static void NewKind(string kind) {
    Say(true, () => J(P(new[] { "first sighting of a " + kind + ".", "new enemy unlocked: " + kind + ".", "wave " + Wave + " brought a " + kind + "." }),
      P(new[] { "the mod devs are unwell.", "studying it now.", "what in Dave's name.", "adding it to the notebook.", "it looks expensive to kill." })));
  }
  public static void Mower(int row) {
    Say(true, () => J("lane " + row + " mower used at wave " + Wave + ".", P(new[] { "we do not talk about that.", "humbling.", "the lawnmower did my job.", "learning from this. painfully." })));
  }
  public static void Mine(bool boom, double age) {
    Say(true, () => boom ? J("SPUDOW.", "a " + age.ToString("0") + "-second-old potato just ended a zombie.", P(Shrug))
                         : J("the potato was " + age.ToString("0") + "s old and not ready.", P(new[] { "lesson learned.", "it was eaten mid-nap.", "noted in the potato file." })));
  }
  public static void Won(int streak, int level) {
    Say(true, () => J("level 1-" + level + " done in " + Mins + ".", streak > 1 ? "streak " + streak + "." : "", P(new[] { "not even close. (it was close)", "gg.", "the lawn is safe. for now.", "zero regrets.", "Dave owes me a taco." })));
  }
  public static void Died(int level, int deaths) {
    Say(true, () => J("died on 1-" + level + " after " + Mins + ".", "that's death #" + deaths + ".", P(new[] { "skill issue.", "the brains were delicious, apparently.", "retrying with notes.", "i'll be back. literally, in 8 seconds." })));
  }
}
