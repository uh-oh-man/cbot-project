# Rough on-screen frame rate: count how often a small animated region changes.
param([int]$X = 60, [int]$Y = 200, [int]$Secs = 3)
Add-Type -AssemblyName System.Drawing
Add-Type -Path C:\cbot\pvz.dll
[void][Pvz]::Unpause()
$p = Get-Process LawnProject | ? MainWindowHandle -ne 0 | select -First 1
Add-Type @"
using System; using System.Runtime.InteropServices;
public class CO { [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref P p); }
"@
$o = New-Object CO+P; [void][CO]::ClientToScreen($p.MainWindowHandle, [ref]$o)
$bmp = New-Object Drawing.Bitmap 40, 40; $g = [Drawing.Graphics]::FromImage($bmp)
$last = ''; $changes = 0; $samples = 0; $sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt $Secs) {
  $g.CopyFromScreen($o.X + $X, $o.Y + $Y, 0, 0, $bmp.Size)
  $sig = ''; for ($i = 0; $i -lt 40; $i += 5) { $sig += $bmp.GetPixel($i, $i).ToArgb() }
  if ($sig -ne $last) { $changes++; $last = $sig }; $samples++
}
"samples/s: $([math]::Round($samples / $Secs)) changes/s: $([math]::Round($changes / $Secs))"
