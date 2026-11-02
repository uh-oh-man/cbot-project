# Rebuilds C:\cbot\pvz.dll from all sources when any changed, then loads it.
# If the running bot holds pvz.dll, the fresh build is loaded from its temp name instead.
$dll = "C:\cbot\pvz.dll"
$src = "C:\cbot\pvz.cs", "C:\cbot\mem.cs", "C:\cbot\bot.cs", "C:\cbot\viz.cs", "C:\cbot\learn.cs", "C:\cbot\quips.cs", "C:\cbot\strategy.cs", "C:\cbot\roles.cs", "C:\cbot\diag.cs", "C:\cbot\pro.cs", "C:\cbot\prosim.cs", "C:\cbot\seen.cs", "C:\cbot\replay.cs", "C:\cbot\outcome.cs", "C:\cbot\vaseplan.cs"
# Guard against a past mistake: a "// comment" pasted mid-line silently swallows the code after it.
$swallowed = Select-String $src -Pattern '//[^"]*;\s*(public|private|static|var|if|return|s\.|[A-Z][a-z]+\.)' | ? { $_.Line -notmatch '^\s*//' }
if ($swallowed) { $swallowed | % { "SWALLOWED CODE? $($_.Filename):$($_.LineNumber): $($_.Line.Trim())" }; throw "fix the lines above before building" }
$newest = ($src | % { (Get-Item $_).LastWriteTime } | Measure-Object -Maximum).Maximum
$load = $dll
if (-not (Test-Path $dll) -or $newest -gt (Get-Item $dll).LastWriteTime) {
  $out = "C:\cbot\pvz_$(Get-Date -f HHmmssfff).dll"
  Add-Type -Path $src -ReferencedAssemblies System.Drawing, System.Core, System.Windows.Forms -OutputAssembly $out -OutputType Library
  try { Copy-Item $out $dll -Force -ErrorAction Stop; Remove-Item $out -ErrorAction SilentlyContinue }
  catch { $load = $out }
}
Add-Type -Path $load
