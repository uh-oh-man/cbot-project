param([string]$Out = "$PSScriptRoot\shot.png", [switch]$Window)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public class W32 {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[W32]::SetProcessDPIAware() | Out-Null
if ($Window) {
  $p = Get-Process LawnProject -ErrorAction Stop | Select-Object -First 1
  $r = New-Object W32+RECT
  [W32]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
  $x = $r.L; $y = $r.T; $w = $r.R - $r.L; $h = $r.B - $r.T
} else {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $x = $b.X; $y = $b.Y; $w = $b.Width; $h = $b.Height
}
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
"$x,$y ${w}x$h -> $Out"
