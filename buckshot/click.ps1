# Click (or just move) inside the Buckshot window, then screenshot. Usage: click.ps1 -X 510 -Y 480 [-Wait 3] [-Key space]
param([int]$X = -1, [int]$Y = -1, [double]$Wait = 2, [string]$Key = "", [string]$Out = "C:\cbot\buckshot\s.png")
Add-Type @"
using System; using System.Runtime.InteropServices;
public class BsIn {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@ -ErrorAction SilentlyContinue
$p = Get-Process | ? { $_.ProcessName -eq 'Buckshot Roulette' } | select -First 1
if ($p) { [BsIn]::SetForegroundWindow($p.MainWindowHandle) | Out-Null; Start-Sleep -Milliseconds 200 }
if ($X -ge 0) {
  [BsIn]::SetCursorPos($X, $Y) | Out-Null; Start-Sleep -Milliseconds 600
  [BsIn]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 350; [BsIn]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
}
if ($Key) { Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait($Key) }
Start-Sleep -Milliseconds ([int]($Wait * 1000))
& C:\cbot\shot.ps1 -Out $Out | Out-Null
