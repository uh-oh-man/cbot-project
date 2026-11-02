# PvZ notes (PvZ paused 2026-10-31 22:56 - collecting anything said about it)

## State when paused
- Pro mode: 13 Adventure wins (3-2 .. 4-4) + darthspiker's tutorial; stuck on 4-5 (Vasebreaker), ~475 deaths there.
- 4-5 last 10 deaths: 7x round-2 football, rest round 2/3. Round 1 now solid.
- Last fix (untested): "armed lane" = has a pea/snow shooter (a parked squash no longer counts).
- DARTH'S GAMBIT implemented (round 1 squash all; round 2 shooter per lane + football hunt; round 3 hypno dancer > backups > buckets).
- Learning bot paused since 3-2 morning; its save backed up in C:\cbot\save_backup_learnbot_*.
- Ideas queued: buy items in Dave's shop automatically; mini-games; endless to flag 69.

## Notes from chat
- 2026-10-31 22:57 darthspiker: the stretched-ragdoll GIF (PVZ_MODDED_BOSS_new.gif) is a real mod zombie, 'the 4th Zombie', hidden under a different internal name in the code so people have a hard time finding it. -> Watch for an unknown zombie type id in later worlds; check the PDB ZombieType enum for an odd name.
- 2026-10-31 23:04 PvZ resumed (owner: 'continue at all cost'; Buckshot blocked on missing game files).
- 2026-11-01 17:10 darthspiker sent screenshots of his winning 3-1, 3-4, 4-1, 4-4 -> summarized in C:\cbot\human_layouts.md (snow pea col 0 every lane; repeaters behind; walls front; fog: fume back, sea-shrooms in pool).
- 2026-11-01 17:11 darthspiker: night/fog = spam free puff/sea-shrooms, then replace them with fume-shrooms over time. (Pro already beats 4-1..4-4; use for the learning bot's night strategy + worlds 4-5+.)
- 2026-11-01 17:17 darthspiker mod fact: sea-shroom recharge is extra fast in the mod -> fill the whole pool with them (night/fog pool levels).
- 2026-11-01 17:48 darthspiker Almanac card: Backup Dancer (mod) - toughness low, 'Gains +40 more health per dance move', appear in sets of four when the Dancing Zombie dances. Matches replays (~+40 hp/s); modeled as BDANCE_GROW=35 in vaseplan/prosim.
- 2026-11-01 17:49 darthspiker Almanac card: ZOMBIE TANK TEAM (mod's bobsled replacement) - teams of four, ARMED (bobsled: SMG, each zombie: pistol), random toughness low/medium, bobsled toughness low, ONLY appears on ice (after a Zomboni). Expect ranged damage to plants: walls won't stall them the usual way; kill the Zomboni / avoid ice lanes (spikeweed vs Zomboni).
- 2026-11-01 17:50 darthspiker Almanac card: DOLPHIN LEAPER (mod) - toughness TOUGH, fast then slow after the jump, JUMPS OVER MULTIPLE PLANTS then attacks from behind, pool only. Counter ideas: tall-nut at the front of pool lanes (stops jumpers in vanilla - verify in mod), keep shooters able to hit behind (it lands deep), squash/tangle kelp it before the jump.
- 2026-11-01 17:51 darthspiker Almanac card: FLAG ZOMBIE (mod) - RANDOMIZED: toughness low/medium, speed normal/fast/SPEEDY, hunger (bite rate) full/normal/hungry, helmet cone/bucket/none, may carry a shield. => never assume a flag zombie's speed/HP from its type: judge each one by what's visible (helmet/shield) and its measured speed (Pro already measures speed from X changes).
- 2026-11-01 17:52 darthspiker Almanac card: DUCKY TUBE ZOMBIE (mod) - floats on water; random helmet, shield and variant depending on the level's zombies; CAN BE A FLAG ZOMBIE. => in pool lanes judge each ducky by its visible helmet/shield/look, not a fixed type stat.
- 2026-11-01 17:54 darthspiker Almanac card: SEED CARRIER ZOMBIE (internal name 'Plantfood zombie'; green flag + green bucket look) - drops a RANDOM SEED PACKET when it dies; random toughness low/medium, helmet cone/bucket/none, may have a shield. => worth killing early (free plant); the dropped packet is handled by the dropped-packet logic. Check Mem.ZomName mapping for its type id when first seen.
  -> confirmed by darthspiker: the bot's zombie name 'PFOOD' = Seed Carrier Zombie (drops a random seed packet on death).
- 2026-11-01 17:57 darthspiker: 'DIRTY' = STINKY DIRTY ZOMBIE - ambush zombie during huge (flag) waves, toughness low/medium, hunger HUNGRY (fast biter; matches 'one-shots non-walls'). Already handled as an ambush threat in pro.cs.
- 2026-11-01 17:57:58 *** PRO BEAT 4-5 (Vasebreaker) *** after ~510 deaths, 3rd attempt after the round-3 endgame fix. Replay: C:\cbot\replays\20261101_175758_lvl35_WON.txt
