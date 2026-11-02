# Compile-check the bot sources WITHOUT touching C:\cbot\pvz.dll or the running bot.
# Uses the same compiler as build.ps1 (Add-Type / CodeDom, C# 5) and the same $src list.
# Usage: powershell -NoProfile -File C:\cbot\v2\check.ps1     -> prints "BUILD OK" or the compiler errors.
$line = (Get-Content C:\cbot\build.ps1 | ? { $_ -match '^\$src\s*=' } | select -First 1)
$src = Invoke-Expression ($line -replace '^\$src\s*=\s*', '')
$missing = $src | ? { -not (Test-Path $_) }; if ($missing) { "MISSING SOURCE: $missing"; exit 1 }
$out = "C:\cbot\v2\check_$(Get-Date -f HHmmssfff).dll"
try {
  Add-Type -Path $src -ReferencedAssemblies System.Drawing, System.Core, System.Windows.Forms -OutputAssembly $out -OutputType Library -ErrorAction Stop
  "BUILD OK ($($src.Count) files)"
} catch {
  "BUILD FAILED"; $_.Exception.Message
  if ($_.Exception.Errors) { $_.Exception.Errors | select -First 40 | % { "{0}({1}): {2}" -f (Split-Path $_.FileName -Leaf), $_.Line, $_.ErrorText } }
  exit 1
} finally { Remove-Item $out -EA SilentlyContinue }
