// Live game-state reader for LawnProject.exe (32-bit), offsets from LawnProject.pdb via pdbdump.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

public class Zom { public uint Id; public int Row, Type, Hp, MaxHp, Helm, Shield, Phase; public float X, Y; public int Total { get { return Hp + Helm + Shield; } }
  public bool Eating;    // Zombie::mIsEating (+0x51): visible as the chewing animation
  public bool Charmed;   // Zombie::mMindControlled (+0xBC): hypnotized, fights FOR us - never a threat or a target
  public int Fly;        // Zombie::mFlyingHealth (+0xEC): > 0 while a balloon zombie still has its balloon (visible: the balloon is drawn)
}
public class Plt { public uint Id; public int Row, Col, Type, Hp, MaxHp; }
// Sun.Motion: 0/1 fell from the sky, 2 popped out of a plant
public class Sun { public uint Id; public int X, Y, Type, Seed, Motion; public bool IsPacket { get { return Type == 23 || Type == 24; } }
  public int Age, Disappear;   // Coin::mCoinAge (+0x4C), mDisappearCounter (+0x54): drive the visible blink before a drop fades
}
public class Pkt {
  public int Slot, Type, Cost, X, Y, W, H, OffsetX; public bool Ready, Active; public int Cd;
  public bool FromCoin; public int CoinX, CoinY; public uint CoinId;   // a seed packet dropped on the lawn
  // On-screen centre; conveyor packets slide in, shifted by OffsetX.
  public int CX { get { return FromCoin ? CoinX : Mem.BankX + X + OffsetX + W / 2; } }
  public int CY { get { return FromCoin ? CoinY : Mem.BankY + Y + H / 2; } }
}

public class State {
  public int Scene, Sun, Wave, Waves, Level, Background, Tutorial, FogHidden; public double GameTime; public bool Paused, HasBoard, Complete;   // GameTime: game seconds (Board::mMainCounter / 100)
  public List<Zom> Zombies = new List<Zom>();
  public List<Zom> FogZombies = new List<Zom>();   // hidden in fog: bookkeeping ONLY (so they don't count as kills), never for decisions
  public List<Plt> Plants = new List<Plt>();
  public List<Sun> Suns = new List<Sun>();
  public List<Pkt> Packets = new List<Pkt>();
  public HashSet<int> Blocked = new HashSet<int>();   // row*10+col cells with a gravestone or crater
  public HashSet<int> Graves = new HashSet<int>();    // just the gravestones
  public HashSet<int> LeafVases = new HashSet<int>(), SkullVases = new HashSet<int>();   // visible look: green leaf / skull
  public HashSet<int> Vases = new HashSet<int>();     // Vasebreaker vases (contents are hidden from the player, so not read)
  public int[,] Square = new int[6, 9];               // Board::mGridSquareType: 1 grass, 2 dirt, 3 pool (water), 4 high ground
  // Lawn mower state per row: -1 none/gone, 0 rolling in, 1 ready, 2 triggered, 3 squished
  public int[] Mowers = { -1, -1, -1, -1, -1, -1 };

  public string Text() {
    var sb = new StringBuilder();
    sb.AppendFormat("scene={0} sun={1} wave={2}/{3} lvl={4}{5}{6}", Scene, Sun, Wave, Waves, Level, Paused ? " PAUSED" : "", Complete ? " DONE" : "");
    sb.Append(" | P:");
    foreach (var p in Packets) sb.AppendFormat(" {0}={1}${2}{3}", p.Slot, Mem.SeedName(p.Type), p.Cost, p.Ready ? "" : (p.Cost > Sun ? "-" : "~"));
    sb.Append(" | Z:");
    foreach (var z in Zombies) sb.AppendFormat(" r{0}@{1:0}:{2}:{3}", z.Row, z.X, Mem.ZomName(z.Type), z.Hp + z.Helm + z.Shield);
    sb.Append(" | Pl:");
    foreach (var p in Plants) sb.AppendFormat(" {0}{1}:{2}", p.Row, p.Col, Mem.SeedName(p.Type));
    sb.AppendFormat(" | coins={0}", Suns.Count);
    return sb.ToString();
  }
}

public static class Mem {
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int a, bool i, int pid);
  [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int written);
  [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int read);
  [DllImport("psapi.dll")] static extern bool EnumProcessModulesEx(IntPtr h, IntPtr[] mods, int cb, out int need, int filter);

  static IntPtr hProc = IntPtr.Zero; static int pid = -1; static uint baseAddr;
  static Dictionary<int, int> costCache = new Dictionary<int, int>();

  const uint RVA_gLawnApp = 0x474860, RVA_gPlantDefs = 0x467C60;

  [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  static readonly object attachGate = new object();
  // Cheap when attached: the open handle is checked for STILL_ACTIVE instead of scanning every process each call
  // (one tick made ~8 scans). A restarted game closes the old handle and attaches to the new process.
  public static void Attach() {
    lock (attachGate) {
      uint code;
      if (hProc != IntPtr.Zero && GetExitCodeProcess(hProc, out code) && code == 259) return;   // STILL_ACTIVE
      if (hProc != IntPtr.Zero) { CloseHandle(hProc); hProc = IntPtr.Zero; pid = -1; }
      foreach (var p in Process.GetProcessesByName("LawnProject")) using (p) {
        if (hProc != IntPtr.Zero || p.MainWindowHandle == IntPtr.Zero) continue;
        pid = p.Id; hProc = OpenProcess(0x0438, false, pid);   // query + read + write + vm operation
        var mods = new IntPtr[1]; int need;
        EnumProcessModulesEx(hProc, mods, IntPtr.Size, out need, 0x01 /*32-bit*/);
        baseAddr = (uint)mods[0].ToInt64(); costCache.Clear();
      }
      if (hProc == IntPtr.Zero) throw new Exception("game not running");
    }
  }

  static byte[] Read(uint addr, int n) {
    var b = new byte[n]; int r;
    if (!ReadProcessMemory(hProc, new IntPtr(addr), b, n, out r) || r != n) throw new Exception("read fail @" + addr.ToString("X"));
    return b;
  }
  static uint U32(uint a) { return BitConverter.ToUInt32(Read(a, 4), 0); }
  static int I32(uint a) { return BitConverter.ToInt32(Read(a, 4), 0); }

  public static int Cost(int seed) {
    if (seed < 0 || seed >= 59) return 0;
    int c; if (costCache.TryGetValue(seed, out c)) return c;
    c = I32(baseAddr + RVA_gPlantDefs + (uint)seed * 0x24 + 0x10); costCache[seed] = c; return c;
  }

  public static State Get() {
    Attach();
    var s = new State();
    uint app = U32(baseAddr + RVA_gLawnApp);
    s.Scene = I32(app + 0x780);
    uint board = U32(app + 0x6F0);
    if (board == 0) return s;
    s.HasBoard = true;
    var bh = Read(board + 0x31468, 0xC4);  // mLevel .. mLevelAwardSpawned
    s.Level = BitConverter.ToInt32(bh, 0); s.Background = I32(board + 0x31464); s.Sun = BitConverter.ToInt32(bh, 0x10);   // Background: 0 day, 1 night, 2 pool, 3 fog, 4 roof
    s.Waves = BitConverter.ToInt32(bh, 0x14); s.Wave = BitConverter.ToInt32(bh, 0x2C);
    s.Complete = bh[0xB0] != 0;
    s.Tutorial = BitConverter.ToInt32(bh, 0x34);   // Board::mTutorialState (0x3149C)
    s.GameTime = BitConverter.ToInt32(bh, 0x18) / 100.0;   // mMainCounter at 0x31480
    s.Paused = Read(board + 0x15C, 1)[0] != 0;
    var sq = Read(board + 0x160, 216);   // GridSquareType[9][6]: column-major (x first), int each
    for (int r = 0; r < 6; r++) for (int c = 0; c < 9; c++) s.Square[r, c] = BitConverter.ToInt32(sq, (c * 6 + r) * 4);

    // DataArray: mBlock, mMaxUsedCount ... items = T + uint mID (high 16 bits nonzero when live)
    ForEach(board + 0x84, 0x18C, 0x188, (it, id) => {
      if (it[0xF4] != 0) return;
      s.Zombies.Add(new Zom { Id = id, Row = BitConverter.ToInt32(it, 0x1C), Type = BitConverter.ToInt32(it, 0x24),
        X = BitConverter.ToSingle(it, 0x2C), Y = BitConverter.ToSingle(it, 0x30), Hp = BitConverter.ToInt32(it, 0xCC), MaxHp = BitConverter.ToInt32(it, 0xD0),
        Helm = BitConverter.ToInt32(it, 0xD4), Shield = BitConverter.ToInt32(it, 0xE4),
        Phase = BitConverter.ToInt32(it, 0x28), Eating = it[0x51] != 0, Charmed = it[0xBC] != 0, Fly = BitConverter.ToInt32(it, 0xEC) });
    });
    // Fog (fog levels): a zombie standing in dense fog is invisible to a player, so it is invisible to the bots too.
    // Board::mGridCelFog int[9][7] (column-major, density 0-255) at +0x4C0, slid right by mFogOffset (+0x5C8, grows when blown away).
    if (s.Background == 3) {
      var fog = Read(board + 0x4C0, 9 * 7 * 4);
      float fogOff = BitConverter.ToSingle(Read(board + 0x5C8, 4), 0);
      int before = s.Zombies.Count;
      s.Zombies.RemoveAll(z => {
        if (z.Row < 0 || z.Row > 5) return false;
        int col = (int)Math.Floor((z.X + 40 - 40 - fogOff) / 80f);
        if (col < 0) return false;          // left of where the fog starts
        if (col > 8) col = 8;
        bool hidden = BitConverter.ToInt32(fog, (col * 7 + z.Row) * 4) >= 200;   // hidden in thick fog
        if (hidden) s.FogZombies.Add(z);
        return hidden;
      });
      s.FogHidden = before - s.Zombies.Count;
    }
    ForEach(board + 0xA0, 0x150, 0x14C, (it, id) => {
      if (it[0x141] != 0) return;
      s.Plants.Add(new Plt { Id = id, Row = BitConverter.ToInt32(it, 0x1C), Col = BitConverter.ToInt32(it, 0x28),
        Type = BitConverter.ToInt32(it, 0x24), Hp = BitConverter.ToInt32(it, 0x40), MaxHp = BitConverter.ToInt32(it, 0x44) });
    });
    ForEach(board + 0xD8, 0xE0, 0xD8, (it, id) => {
      if (it[0x38] != 0 || it[0x50] != 0) return;   // dead or already being collected
      int w = BitConverter.ToInt32(it, 0x10), h = BitConverter.ToInt32(it, 0x14);
      s.Suns.Add(new Sun { Id = id, X = (int)BitConverter.ToSingle(it, 0x24) + w / 2, Y = (int)BitConverter.ToSingle(it, 0x28) + h / 2,
        Type = BitConverter.ToInt32(it, 0x58), Seed = BitConverter.ToInt32(it, 0x68), Motion = BitConverter.ToInt32(it, 0x5C),   // Seed: plant pictured on a dropped packet
        Age = BitConverter.ToInt32(it, 0x4C), Disappear = BitConverter.ToInt32(it, 0x54) });
    });

    ForEach(board + 0xF4, 0x48, 0x44, (it, id) => {
      if (it[0x30] != 0) return;
      int row = BitConverter.ToInt32(it, 0x14);
      if (row >= 0 && row < 6) s.Mowers[row] = BitConverter.ToInt32(it, 0x2C);
    });

    ForEach(board + 0x110, 0xEC, 0xE8, (it, id) => {   // grid items: 1 gravestone, 2 crater
      int t = BitConverter.ToInt32(it, 8);
      int cell = BitConverter.ToInt32(it, 0x14) * 10 + BitConverter.ToInt32(it, 0x10);
      if (it[0x20] == 0 && (t == 1 || t == 2)) s.Blocked.Add(cell);
      if (it[0x20] == 0 && t == 1) s.Graves.Add(cell);
      if (it[0x20] == 0 && t == 7) { s.Vases.Add(cell); s.Blocked.Add(cell); }   // an unbroken vase occupies its cell: nothing can be planted there
      // What the vase LOOKS like (what a player sees): 4 green leaf = a plant inside, 5 skull = a zombie. (Never the hidden contents.)
      int look = BitConverter.ToInt32(it, 0xC);
      if (it[0x20] == 0 && t == 7 && look == 4) s.LeafVases.Add(cell);
      if (it[0x20] == 0 && t == 7 && look == 5) s.SkullVases.Add(cell);
    });

    uint bank = U32(board + 0x138);
    if (bank != 0) {
      var bk = Read(bank, 0x350);
      int bx = BitConverter.ToInt32(bk, 8), by = BitConverter.ToInt32(bk, 12), n = BitConverter.ToInt32(bk, 0x24);
      for (int i = 0; i < n && i < 10; i++) {
        int o = 0x28 + i * 0x50;
        var p = new Pkt { Slot = i + 1, X = BitConverter.ToInt32(bk, o + 8), Y = BitConverter.ToInt32(bk, o + 12),
          W = BitConverter.ToInt32(bk, o + 0x10), H = BitConverter.ToInt32(bk, o + 0x14),
          Type = BitConverter.ToInt32(bk, o + 0x34), Active = bk[o + 0x48] != 0, OffsetX = BitConverter.ToInt32(bk, o + 0x30) };
        bool refreshing = bk[o + 0x49] != 0;
        p.Cost = Cost(p.Type);
        p.Ready = p.Active && !refreshing && p.Cost <= s.Sun;
        p.Cd = refreshing ? 1 : 0;
        s.Packets.Add(p);
      }
      // packet coords may be bank-relative; expose bank origin through packet 0 debug
      BankX = bx; BankY = by;
    }
    return s;
  }
  public static int BankX, BankY;

  // SexyAppBase::mUpdateMultiplier (0x458): the engine's own time scale. Only changes how fast time runs.
  public static double TimeScale {
    get { Attach(); return BitConverter.ToDouble(Read(U32(baseAddr + RVA_gLawnApp) + 0x458, 8), 0); }
    set { Attach(); int w; WriteProcessMemory(hProc, new IntPtr(U32(baseAddr + RVA_gLawnApp) + 0x458), BitConverter.GetBytes(value), 8, out w); }
  }

  // Seed chooser cards: seed -> (state, screen x, y). State 0 flying to bank, 1 in bank, 2 flying back, 3 in chooser, 4 hidden/locked.
  public class Card { public int Seed, State, X, Y; }
  public static List<Card> ChooserCards(out int inBank, out int toChoose, out int inFlight) {
    Attach(); inBank = toChoose = inFlight = 0; var list = new List<Card>();
    uint sc = U32(U32(baseAddr + RVA_gLawnApp) + 0x700);
    if (sc == 0) return list;
    var b = Read(sc, 0xEB8);
    toChoose = BitConverter.ToInt32(b, 0xE74); inFlight = BitConverter.ToInt32(b, 0xE7C); inBank = BitConverter.ToInt32(b, 0xE80);
    for (int i = 0; i < 59; i++) {
      int o = 0x98 + i * 0x3C;
      list.Add(new Card { X = BitConverter.ToInt32(b, o), Y = BitConverter.ToInt32(b, o + 4), Seed = BitConverter.ToInt32(b, o + 0x20), State = BitConverter.ToInt32(b, o + 0x24) });
    }
    return list;
  }

  // Crazy Dave: LawnApp::mCrazyDaveState (0 off, 1 entering, 2 leaving, 3 idle, 4 talking, 5/6 handing) and his speech bubble text.
  public static int DaveState() { Attach(); return I32(U32(baseAddr + RVA_gLawnApp) + 0x7C8); }
  // PlayerInfo (LawnApp+0x7B0): mCoins +0x24 (shown on screen x10 as money), mPurchases int[80] at +0x1BC (StoreItem ids: 21 seed slot, 22 pool cleaner, 23 roof cleaner).
  public static int PlayerCoins() { Attach(); uint pi = U32(U32(baseAddr + RVA_gLawnApp) + 0x7B0); return pi == 0 ? -1 : I32(pi + 0x24); }
  public static int Purchase(int item) { Attach(); uint pi = U32(U32(baseAddr + RVA_gLawnApp) + 0x7B0); return pi == 0 ? -1 : I32(pi + 0x1BC + (uint)item * 4); }
  public static string DaveText() {
    Attach(); uint a = U32(baseAddr + RVA_gLawnApp) + 0x7D8;
    var b = Read(a, 24); int len = BitConverter.ToInt32(b, 16), cap = BitConverter.ToInt32(b, 20);
    if (len <= 0 || len > 400) return "";
    var txt = cap >= 16 ? Read(BitConverter.ToUInt32(b, 0), len) : b.Take(len).ToArray();   // MSVC small-string buffer
    return System.Text.Encoding.ASCII.GetString(txt).Replace("{", "").Replace("}", "");
  }

  static string Str(uint a) {   // MSVC std::string: 16-byte inline buffer, size at +16, capacity at +20
    var b = Read(a, 24); int len = BitConverter.ToInt32(b, 16), cap = BitConverter.ToInt32(b, 20);
    if (len <= 0 || len > 600) return "";
    var txt = cap >= 16 ? Read(BitConverter.ToUInt32(b, 0), len) : b.Take(len).ToArray();
    return System.Text.Encoding.ASCII.GetString(txt);
  }
  // Text of the front-most open dialog (header + lines): what the player is reading on screen.
  public static string DialogText() {
    Attach(); uint app = U32(baseAddr + RVA_gLawnApp);
    if (I32(app + 0x318) <= 0) return "";
    uint head = U32(app + 0x314); uint node = U32(head);   // list head -> first node {next, prev, value}
    uint dlg = U32(node + 8); if (dlg == 0) return "";
    return (Str(dlg + 0x94) + " " + Str(dlg + 0xC4)).Replace("{", "").Replace("}", "");
  }
  // The game's own display name for a plant (PlantDefinition::mPlantName).
  public static string PlantName(int seed) {
    if (seed < 0 || seed >= 59) return "";
    Attach(); uint p = U32(baseAddr + RVA_gPlantDefs + (uint)seed * 0x24 + 0x20);
    if (p == 0) return ""; var b = Read(p, 32); int n = Array.IndexOf(b, (byte)0); return System.Text.Encoding.ASCII.GetString(b, 0, n < 0 ? 32 : n);
  }

  // Board::mCursorObject (0x12C) -> CursorObject::mCursorType (0x30): 0 normal, 1 holding a bank seed, 2 holding a dropped packet, 6 shovel.
  public static int CursorType() {
    Attach(); uint board = U32(U32(baseAddr + RVA_gLawnApp) + 0x6F0); if (board == 0) return -1;
    uint co = U32(board + 0x12C); return co == 0 ? -1 : I32(co + 0x30);
  }

  // Mod option "Zombie Healthbars" (SexyAppBase::mZombieHealthbars, 0x57A): when on, every zombie's health is drawn on screen.
  public static bool ZombieHealthbars() { Attach(); return Read(U32(baseAddr + RVA_gLawnApp) + 0x57A, 1)[0] != 0; }

  // SexyAppBase::mDialogList (std::list at 0x314; its size is the second dword) = how many dialogs are open.
  public static int OpenDialogs() { Attach(); return I32(U32(baseAddr + RVA_gLawnApp) + 0x318); }

  // LawnApp::mSeedChooserScreen (0x700) is non-null while the seed picker is up.
  public static bool SeedChooserOpen() {
    Attach();
    return U32(U32(baseAddr + RVA_gLawnApp) + 0x700) != 0;
  }

  // Board::mPlantRow[6] (PlantRowType: 0 dirt, 1 normal, 2 pool, 3 high ground)
  public static int[] RowTypes() {
    Attach();
    uint board = U32(U32(baseAddr + RVA_gLawnApp) + 0x6F0);
    if (board == 0) return new int[6];   // board just went away (level end): no lanes, not a read error
    var b = Read(board + 0x5D0, 24); var r = new int[6];
    for (int i = 0; i < 6; i++) r[i] = BitConverter.ToInt32(b, i * 4);
    return r;
  }

  static void ForEach(uint arr, int stride, int idOff, Action<byte[], uint> f) {
    var a = Read(arr, 0x1C);
    uint block = BitConverter.ToUInt32(a, 0); int used = BitConverter.ToInt32(a, 4);
    if (block == 0 || used <= 0) return;
    var all = Read(block, used * stride);
    for (int i = 0; i < used; i++) {
      uint id = BitConverter.ToUInt32(all, i * stride + idOff);
      if ((id & 0xFFFF0000) == 0) continue;     // free slot
      var it = new byte[stride]; Buffer.BlockCopy(all, i * stride, it, 0, stride); f(it, id);
    }
  }

  static readonly string[] Seeds = ("PEA SUNF CHERRY WALL MINE SNOW CHOMP REPEAT PUFF SUNSH FUME GRAVE HYPNO SCAREDY ICE DOOM LILY SQUASH THREE KELP JALA SPIKE TORCH TALL SEASH LANTERN CACTUS BLOVER SPLIT STAR PUMPKIN MAGNET CABBAGE POT KERNEL COFFEE GARLIC UMBRELLA MARIGOLD MELON GATLING TWINSUN GLOOM CATTAIL WMELON GMAGNET SPIKEROCK COB EXPLNUT SPROUT LEFTPEA HARDWOOD PERFUME IMITM IMIT GIANTNUT CHEAT").Split(' ');
  public static string SeedName(int t) { return t >= 0 && t < Seeds.Length ? Seeds[t] : "s" + t; }
  static readonly string[] Zs = ("NORM FLAG CONE PFOOD POLE LJUMP PAIL DIRTY NEWS DOOR FOOT REGEN DANCER BDANCE DUCK SNORK ZAMB BOB DOLPH FLIP TANK JACK BALLOON DIGGER CARNI POGO YETI BUNGEE LADDER CAT CAT2 CAT3 GARG IMP BOSS").Split(' ');
  public static string ZomName(int t) { return t >= 0 && t < Zs.Length ? Zs[t] : "z" + t; }
}
