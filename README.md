# 7DTD Bot - Dedicated FPS Bots (like Quake 3)

Server-side mod that spawns real FPS bots. They spawn with weapons, pathfind, hunt and shoot players, zombies and each other. Vanilla clients need no mod. Bots obey the same physics/collision/move caps as a normal player (no godmode, no no-clip, same weight/drag/capsule bounds) — only spawn loadout differs. Default 6 mixed-loadout bots, DM spawnpoints, difficulty 0-4.

## What it does

- Keeps `TargetBotCount` bots alive (auto-respawns one per second).
- Each bot is a vanilla `zombieSoldier` driven by our FPS loop (dedi-safe `Standard` mesh; no `NpcUMA` XML needed). Weapons come from `BotWeapon=mixed` → random from `LoadoutPool` (pistol/shotgun/AK/sniper/auto-shotgun/SMG) with per-weapon `WeaponProfile` (fire rate, burst 2-9 shots, spread, damage, effective range, pellets).
- Loop: scan in wide `VisionAngle` cone → `Physics.Raycast` + voxel fallback LOS → leading aim (`LeadAimPoint` = velocity prediction scaled by difficulty/weapon) → burst fire with `ReactionTimeSec` delay and `BurstMin/Max` + `BurstPauseSec` gaps; pellets/headshots via `DamageSourceEntity` → `DamageEntity(EnumDamageTypes.Piercing)`.
- Movement: `MoveEntityHeaded` + periodic `FindPath` to A* queue; continuous `Strafe`/`Backpedal` circling in `Attack`, dodge on being hit (`DodgeOnHitChance`), `Jump` unstuck.
- DM spawns: reads `Data/Worlds/<World>/spawnpoints.xml` (far-from-players farthest spawn, bot/bot avoidance), falls back to radius jitter near spawn point.
- Per-bot difficulty preset (`Difficulty 0-4`) scales aim jitter, reaction, vision, headshot rate like Q3 bot `skill`.

## Install

```bash
make build        # mcs (or dotnet SDK) against your Steam Dedicated Managed DLLs
make install      # copies to Dedicated Server Mods/BotMod
# then restart the dedicated server (EAC must be off for code mods)
```

Config is `Mods/BotMod/Config/botmod.json` (repo default `config/botmod.json`). Edit and `bot reload` live. EAC off: `<property name="EACEnabled" value="false"/>`.

## Console commands

```
bot help
bot status            # config + alive (class/weapon/diff/vision/attack/BotVs)
bot list              # id, weapon, state, pos, target, hp, burst
bot spawn [n] [x z] [weapon] | bot player <name|id> [n] [weapon]  # e.g. bot spawn 2 gunShotgunT1DoubleBarrel
bot player Kira              # 1 bot near Kira (12-30m, out-of-sight preferred)
bot player Kira 3 gunMGT1AK47 # 3 AK bots near Kira
bot player me               # from in-game console, spawns near you
bot weapon <gunId|mixed>      # default for next spawns
bot skill <0-4>               # 0 bot, 1 easy, 2 normal, 3 hard, 4 nightmare
bot count <n>                 # keep n alive
bot remove all | bot remove <id>
bot reload | bot enable | bot disable
```

## Tuning (`config/botmod.json`)

- `Difficulty` 0-4 drives `AimJitterDegrees`, `ReactionTimeSec`, `HeadshotChance`, `VisionRange/AttackRange` (see `BotConfig.ApplyDifficulty`).
- `BotEntityClass` (default `zombieSoldier`), `BotWeapon`/`LoadoutPool`/`BotAmmo`, `BotHealth=100` armor-like.
- `VisionRange/VisionAngle/LoseTargetRange/Time`, `AttackRange` per weapon, `StrafeChance/DodgeOnHitChance`.
- `PathRecalcIntervalSec/StuckTimeoutSec/RandomWanderRadius/Interval`, `SpawnRadius/NearPlayerChance/UseSpawnpoints`, `RespawnDelaySec=3/SpawnProtectionSec`.
- `TargetBotCount=6 MaxBots=16`.

Quake-style names by default: `Grunt/Ranger/Phobos/Dozer/...` (12).

## Validation

```bash
make build && make install
./7DaysToDieServer.x86_64 -logfile /tmp/bot.log -quit -batchmode -nographics -dedicated -configfile /tmp/serverconfig.eacoff.xml
# expect:
# [BotMod] BotMod v0.1.0 loading... diff=2 weapon=mixed
# [BotMod] DM spawns: 8 from .../Data/Worlds/Navezgane/spawnpoints.xml (world=Navezgane)
# [BotMod] Bot spawned: Grunt_42 [gunMGT1AK47] id=xxxx at (163,62,818) ...
# [BotMod] Bots alive: 6/6
```

Damage hook on `EntityAlive.DamageEntity` gates `BotVs*` and also routes hits on bots to `Bot.OnDamaged` for dodge/aggro swap. Bots respect blocks/doors and are visible to vanilla clients.
