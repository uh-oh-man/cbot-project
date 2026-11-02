using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

public static class Pvz {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte sc, uint f, UIntPtr e);

  [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  // The window handle is cached: a process scan on every click / screen sample cost several ms each.
  // Re-validated on every use (still a window, still owned by the same game process), rescanned otherwise.
  static IntPtr hwndCache = IntPtr.Zero; static uint hwndPid; static bool dpiSet;
  static readonly object hwndGate = new object();   // main loop and the light sampler both look the window up
  static IntPtr Hwnd() { lock (hwndGate) return HwndLocked(); }
  static IntPtr HwndLocked() {
    IntPtr h = hwndCache; uint wp;
    if (h != IntPtr.Zero && IsWindow(h) && GetWindowThreadProcessId(h, out wp) != 0 && wp == hwndPid) return h;
    if (!dpiSet) { SetProcessDPIAware(); dpiSet = true; }
    hwndCache = IntPtr.Zero;
    foreach (var p in Process.GetProcessesByName("LawnProject"))
      using (p) if (hwndCache == IntPtr.Zero && p.MainWindowHandle != IntPtr.Zero) { hwndPid = (uint)p.Id; hwndCache = p.MainWindowHandle; }
    if (hwndCache == IntPtr.Zero) throw new Exception("game window not found");
    return hwndCache;
  }

  static POINT Origin(IntPtr h) { var o = new POINT(); ClientToScreen(h, ref o); return o; }

  // Click at game client coordinates (800x600 space).
  public static void Click(int x, int y, bool right = false) {
    var h = Hwnd(); SetForegroundWindow(h); Thread.Sleep(60);
    var o = Origin(h); SetCursorPos(o.X + x, o.Y + y); Thread.Sleep(60);
    mouse_event(right ? 0x0008u : 0x0002u, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(50);
    mouse_event(right ? 0x0010u : 0x0004u, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(80);
  }

  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

  // When true (after a successful live self-test), gameplay clicks go straight to the window as messages.
  public static bool UsePost;

  // Click without the settle delays; refocuses the game only if needed.
  public static void FastClick(int x, int y, bool right = false) {
    if (UsePost) { PostClick(x, y, right); Thread.Sleep(2); return; }
    var h = Hwnd();
    if (GetForegroundWindow() != h) { SetForegroundWindow(h); Thread.Sleep(80); }
    var o = Origin(h); SetCursorPos(o.X + x, o.Y + y); Thread.Sleep(8);
    mouse_event(right ? 0x0008u : 0x0002u, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(12);
    mouse_event(right ? 0x0010u : 0x0004u, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(10);
  }

  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  // Click by posting mouse messages straight to the game window: no cursor movement, no waiting.
  public static void PostClick(int x, int y, bool right = false) {
    var h = Hwnd(); var lp = (IntPtr)((y << 16) | (x & 0xFFFF));
    PostMessage(h, 0x0200, IntPtr.Zero, lp);                                         // WM_MOUSEMOVE
    PostMessage(h, right ? 0x0204u : 0x0201u, (IntPtr)(right ? 2 : 1), lp);          // button down
    PostMessage(h, right ? 0x0205u : 0x0202u, IntPtr.Zero, lp);                      // button up
  }

  public static void Move(int x, int y) {
    var h = Hwnd(); var o = Origin(h); SetCursorPos(o.X + x, o.Y + y);
  }

  public static void Key(byte vk) {
    var h = Hwnd(); SetForegroundWindow(h); Thread.Sleep(60);
    keybd_event(vk, 0, 0, UIntPtr.Zero); Thread.Sleep(50); keybd_event(vk, 0, 2, UIntPtr.Zero);
  }

  public static Color Pixel(int x, int y) {
    var h = Hwnd(); var o = Origin(h);
    using (var bmp = new Bitmap(1, 1)) {
      using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(o.X + x, o.Y + y, 0, 0, new Size(1, 1));
      return bmp.GetPixel(0, 0);
    }
  }

  // Mean brightness (R+G+B) of a screen region in client coordinates.
  public static int RegionBrightness(int x0, int y0, int w, int h) {
    var hw = Hwnd(); var o = Origin(hw); long sum = 0; int n = 0;
    using (var bmp = new Bitmap(w, h)) {
      using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(o.X + x0, o.Y + y0, 0, 0, bmp.Size);
      for (int y = 0; y < h; y += 3) for (int x = 0; x < w; x += 3) { var c = bmp.GetPixel(x, y); sum += c.R + c.G + c.B; n++; }
    }
    return (int)(sum / Math.Max(1, n));
  }

  // Mean brightness of a seed packet (centre x); drops sharply while it recharges after planting.
  public static int PacketBrightness(int cx) {
    var h = Hwnd(); var o = Origin(h); long sum = 0;
    using (var bmp = new Bitmap(40, 50)) {
      using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(o.X + cx - 20, o.Y + 15, 0, 0, bmp.Size);
      for (int y = 0; y < 50; y += 2) for (int x = 0; x < 40; x += 2) { var c = bmp.GetPixel(x, y); sum += c.R + c.G + c.B; }
    }
    return (int)(sum / 500);
  }

  // The pause dialog is a dark slate panel around (400,250); click Resume if it is up.
  public static bool Unpause() {
    var h = Hwnd(); SetForegroundWindow(h); Thread.Sleep(150);
    var c = Pixel(260, 300);
    if (c.R < 50 && c.G < 50 && c.B < 70) { Click(390, 448); Thread.Sleep(300); return true; }
    return false;
  }

  // Returns centers of sun-coloured blobs below the seed bank, as "x,y" strings.
  public static System.Collections.Generic.List<string> FindSun() {
    var h = Hwnd(); RECT r; GetClientRect(h, out r); var o = Origin(h);
    var hits = new System.Collections.Generic.List<int[]>();
    using (var bmp = new Bitmap(r.R, r.B)) {
      using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(o.X, o.Y, 0, 0, bmp.Size);
      for (int y = 95; y < 565; y += 3)
        for (int x = 30; x < 775; x += 3) {
          var c = bmp.GetPixel(x, y);
          // Sun glow is bright yellow-white: beige stone has R-B < 60, sunflower petals have G < 228.
          if (c.R > 225 && c.G > 228 && c.B < 200 && c.R - c.B > 60) hits.Add(new[] { x, y });
        }
    }
    var clusters = new System.Collections.Generic.List<int[]>(); // sx, sy, n
    foreach (var p in hits) {
      int[] best = null;
      foreach (var cl in clusters)
        if (Math.Abs(cl[0] / cl[2] - p[0]) < 40 && Math.Abs(cl[1] / cl[2] - p[1]) < 40) { best = cl; break; }
      if (best == null) clusters.Add(new[] { p[0], p[1], 1 });
      else { best[0] += p[0]; best[1] += p[1]; best[2]++; }
    }
    var res = new System.Collections.Generic.List<string>();
    foreach (var cl in clusters) if (cl[2] >= 12) res.Add((cl[0] / cl[2]) + "," + (cl[1] / cl[2]));
    return res;
  }

  // Capture the client area; optional downscale for cheaper viewing.
  public static string Shot(string path, double scale = 1.0) {
    var h = Hwnd(); RECT r; GetClientRect(h, out r); var o = Origin(h);
    int w = r.R, ht = r.B;
    using (var bmp = new Bitmap(w, ht)) {
      using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(o.X, o.Y, 0, 0, bmp.Size);
      if (scale == 1.0) bmp.Save(path, ImageFormat.Png);
      else using (var s = new Bitmap(bmp, (int)(w * scale), (int)(ht * scale))) s.Save(path, ImageFormat.Png);
    }
    return w + "x" + ht;
  }
}
