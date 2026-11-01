Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public class FG {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
"@
Add-Type -Path C:\cbot\pvz.dll
[void][Pvz]::Unpause()
for ($i = 0; $i -lt 6; $i++) {
  $h = [FG]::GetForegroundWindow(); $sb = New-Object Text.StringBuilder 200; [void][FG]::GetWindowText($h, $sb, 200)
  $pid2 = 0; [void][FG]::GetWindowThreadProcessId($h, [ref]$pid2)
  "$i fg='$sb' pid=$pid2 ($((Get-Process -Id $pid2).Name))"
  Start-Sleep 1
}
