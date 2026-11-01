PvZ Recorder - records your play so the bot can learn it
=========================================================
1. Start Plants vs. Zombies Extended as usual.
2. Run PvZRecorder.exe (a small console window opens).
3. Play normally (e.g. beat 4-5). Close the window when done.
4. A file "pvz_recording_<date>_<time>.txt" appears next to the exe - send that file in the Discord channel.

What it does / does NOT do
- It ONLY READS the game's memory (it opens LawnProject.exe with read-only rights).
- It never clicks, never types, never writes to the game, never touches other programs or files.
- It uses NO network. Nothing is sent anywhere - the text file stays on your PC until you share it yourself.
- It records: level, waves, sun, every plant you place / lose (with where the zombies were at that moment),
  vases you break (and whether they looked green/skull/?), zombies that appear, Dave's lines,
  and a small snapshot of the board every 2 seconds.

Requirements: Windows with .NET Framework 4 (built into Windows 10/11). Your antivirus may warn because
the exe is unsigned - the full source code (rec.cs + memro.cs) is included so anyone can check it.
