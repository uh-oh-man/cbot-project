// Buckshot Roulette window I/O: find the game window, capture its client area, click at client coordinates.
// Screen-only: reads pixels and sends mouse input, never touches the game's memory or files.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

public static class Win {
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  static Win() { SetProcessDPIAware(); }

  public static IntPtr Handle() {
    foreach (var p in Process.GetProcessesByName("Buckshot Roulette")) if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
    return IntPtr.Zero;
  }
  // Client area in screen coordinates.
  public static Rectangle Client() {
    var h = Handle(); if (h == IntPtr.Zero) return Rectangle.Empty;
    RECT r; GetClientRect(h, out r); var o = new POINT(); ClientToScreen(h, ref o);
    return new Rectangle(o.X, o.Y, r.R - r.L, r.B - r.T);
  }
  public static Bitmap Shot() {
    var c = Client(); if (c.IsEmpty || c.Width <= 0) return null;
    var bmp = new Bitmap(c.Width, c.Height, PixelFormat.Format24bppRgb);
    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(c.X, c.Y, 0, 0, c.Size);
    return bmp;
  }
  public static void Focus() { var h = Handle(); if (h != IntPtr.Zero) SetForegroundWindow(h); }
  // Coordinates are fractions of the client area (0..1) so they survive window resizes.
  public static void Move(double fx, double fy) { var c = Client(); SetCursorPos(c.X + (int)(fx * c.Width), c.Y + (int)(fy * c.Height)); }
  public static void Click(double fx, double fy) {
    Focus(); Thread.Sleep(150); Move(fx, fy); Thread.Sleep(400);   // ~20 FPS: let the hover register
    mouse_event(2, 0, 0, 0, IntPtr.Zero); Thread.Sleep(250); mouse_event(4, 0, 0, 0, IntPtr.Zero);
  }
}

static class BsTool {
  // bstool shot [out.png] | bstool click fx fy [waitSec] [out.png] | bstool move fx fy
  static void Main(string[] a) {
    string cmd = a.Length > 0 ? a[0] : "shot";
    if (cmd == "click" || cmd == "move") {
      double fx = double.Parse(a[1]), fy = double.Parse(a[2]);
      if (cmd == "click") Win.Click(fx, fy); else { Win.Focus(); Win.Move(fx, fy); }
      Thread.Sleep((int)(1000 * (a.Length > 3 ? double.Parse(a[3]) : 2)));
    }
    string outp = a.Length > 4 ? a[4] : (cmd == "shot" && a.Length > 1 ? a[1] : @"C:\cbot\buckshot\w.png");
    using (var b = Win.Shot()) { if (b == null) { Console.WriteLine("no window"); return; } b.Save(outp, ImageFormat.Png); Console.WriteLine(Win.Client() + " -> " + outp); }
  }
}
