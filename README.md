# 7DTD Bot - Dedicated FPS Bots (like Quake 3)

Server-side mod that spawns real FPS bots. They spawn with weapons, pathfind, hunt and shoot players, zombies and each other. Vanilla clients need no mod.

## What it does

- Keeps `TargetBotCount` bots alive (auto-respawns one per second until full).
- Each bot is a `zombieSoldier` reskinned as a bot (dedi-safe `ModelType Standard`; our AI drives it, no `NpcUMA` needed) with a gun from `BotWeapon`. Our loop scans inside a vision cone, checks line of sight (physics ray + voxel fallback), pathfinds via `MoveEntityHeaded` + periodic `FindPath` to the A* queue, and shoots with hitscan `DamageSourceEntity → DamageEntity`. Bots fight each other, zombies and players per `BotVs*` toggles. They wander when idle and jump/strafe when stuck.

## Install

```bash
make build        # mcs (or dotnet SDK) against your Steam Dedicated Managed DLLs
make install      # copies to Dedicated Server Mods/BotMod
# then restart the dedicated server (EAC must be off for code mods)
```

Config is `Mods/BotMod/Config/botmod.json` (repo default is `config/botmod.json`). Edit on the server and run `bot reload` live. EAC off is required: set `<property name="EACEnabled" value="false"/>` in `serverconfig.xml` or pass `-configfile` as in the smoke tests.

## Console commands

```
bot help
bot status            # show config + alive count
bot list              # list bots (id, pos, state, target, hp)
bot spawn [n] [x z]   # spawn n bots (optionally at x,z)
bot remove all        # remove all bots
bot remove <id>       # remove one bot
bot count <n>         # set TargetBotCount live
bot reload            # reload Config/botmod.json
bot enable | bot disable
```

## Tuning (`config/botmod.json`)

- `BotEntityClass` - entity to spawn (default `zombieSoldier`; dedi-safe). Any other valid class works too.
- `BotWeapon` / `BotAmmo` / `BotAmmoCount` - e.g. `gunMGT1AK47`, `gunHandgunT1Pistol`, `gunShotgunT1DoubleBarrel` + `ammo762mmBulletBall`.
- `VisionRange` / `AttackRange` / `FireRateSec` / `DamagePerShot` / `HeadshotChance` - combat.
- `BotVsBot`, `BotVsZombie`, `BotVsPlayer` - who bots will attack.
- `PathRecalcIntervalSec` / `StuckTimeoutSec` / `RandomWanderRadius` / `RandomWanderIntervalSec` - movement.
- `TargetBotCount` / `MaxBots` / `SpawnRadius` / `SpawnNearPlayerChance` - population.
- `SpawnProtectionSec` / `RespawnDelaySec` / `AnnounceSpawns` / `DropLootOnDeath` - lifecycle.

Smoke tests rename `npcSurvivor*` classes: vanilla V3.1 has them inside an HTML comment, so they resolve to -1 and don't spawn. Bots default to `zombieSoldier` + our AI; earlier `botSurvivorRanged` attempts failed on dedi with `Model class 'NpcUMA' not found`.

## Validation

```bash
make build && make install
# dedicated smoke (EAC off, Navezgane):
./7DaysToDieServer.x86_64 -logfile /tmp/bot.log -quit -batchmode -nographics -dedicated -configfile /tmp/serverconfig.eacoff.xml
# expect:
# [BotMod] BotMod v0.1.0 loading...
# [BotMod] BotManager ready. TargetBots=4
# [BotMod] Bot spawned: Bot_Foxtrot_xx id=xxxx ...
# [BotMod] Bots alive: 4/4
# bot help|status|list via telnet/RCON when TelnetEnabled=true
```

## Notes

- Server-side only, dedicated. Requires `0_TFP_Harmony` (stock).
- Bots use the game's entity + A* + physics systems, so they respect blocks/doors and are visible to vanilla clients.
- Damage goes through `DamageEntity(EnumDamageTypes.Piercing)` so armor/buffs apply. Headshots are a dice roll (`HeadshotChance`).
- No `Config/entityclasses.xml` modlet is shipped; zombieSoldier is vanilla and never needs XML patching on dedi.
