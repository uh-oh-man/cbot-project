# Runs the memory-driven bot (log: C:\cbot\bot.log).
param([int]$Secs = 300)
. C:\cbot\build.ps1
[Bot]::Run($Secs, "C:\cbot\bot.log")
