# Live b18 acceptance: load and spawn pass, hits and pathing do not (2026-11-05)

Ran BotMod 0.9.0 in the stock V3.3.0 b18 dedicated server on this machine.
The install was not modified: the run used a btrfs reflink copy under
`.scratch/server/game`, its own `-configfile` and `UserDataFolder`, EAC off,
Navezgane, telnet 8087, game port 26971.

Install identity: steamcmd app 294420, buildid `25661908`,
`7DaysToDieServer_Data/Managed/Assembly-CSharp.dll` sha256
`e16ff9d539b8fbf64904c0bc090d502286a62464742a9396f0455936ad0beb87`,
11697152 bytes.

```bash
make build SEVENDTD_DS_DIR=$PWD/.scratch/server/game
make install SEVENDTD_DS_DIR=$PWD/.scratch/server/game
cd .scratch/server/game
./7DaysToDieServer.x86_64 -logfile ../logs/botmod-server.log \
  -quit -batchmode -nographics -dedicated \
  -configfile=/home/maci/Desktop/hordeforge/7dtd-fps-bots/.scratch/server/serverconfig.xml
```

## What passed

The mod loads, patches, and starts:

```
INF [MODS]     Loaded Mod: BotMod (0.9.0)
INF [BotMod] BotMod v0.9.0 loading. ... Enabled=True DedicatedOnly=True AuthBypass=False
INF [BotMod] Harmony patches applied.
INF [BotMod] BotMod init OK. Commands: bot help
INF [BotMod] BotManager ready. TargetBots=6 diff=4 weapon=mixed seed=0x00C0FFEE
INF [BotMod] BotNeuralBrain: loaded ok: .../Mods/BotMod/evolved/best.json (325 weights, hidden=16)
INF [BotMod] GameStartDone -> BotManager started.
INF [BotMod] DM spawns: 8 from .../Data/Worlds/Navezgane/spawnpoints.xml (world=Navezgane)
```

It spawns the configured 6 bots at DM spawnpoints, one per second, with
weapons and loadouts:

```
INF [BotMod] Bot spawned: [Bot] Slash_91 [gunMGT1AK47] id=171 at (850.00, 62.00, 642.00) (1/6)
INF [BotMod] Bot spawned: [Bot] Sorlag_17 [gunShotgunT3AutoShotgun] id=172 at (163.00, 62.00, 818.00) (2/6)
INF [BotMod] Bot spawned: [Bot] Grunt_20 [gunHandgunT3SMG5] id=173 at (-273.00, 62.00, 449.00) (3/6)
INF [BotMod] Bot spawned: [Bot] Grunt_78 [gunShotgunT1DoubleBarrel] id=174 at (1041.00, 62.00, 1059.00) (4/6)
INF [BotMod] Bot spawned: [Bot] Dozer_37 [gunShotgunT1DoubleBarrel] id=175 at (323.00, 59.00, 505.00) (5/6)
INF [BotMod] Bot spawned: [Bot] Visor_51 [gunMGT1AK47] id=176 at (512.00, 62.00, 942.00) (6/6)
```

The console surface works over telnet: `bot status`, `bot list`,
`bot neural status`, `bot spawn`, `bot remove all`. Target acquisition works;
bots reach `Attack` and `Chase` with each other as targets, and the burst
counters cycle:

```
Bot [Bot] Dozer_64 [gunHandgunT1Pistol] id=180 state=Attack pos=(512.00, 62.00, 942.00) tgt=181 hp=50 burst=2
Bot [Bot] Grunt_66 [gunMGT1AK47] id=181 state=Attack pos=(512.00, 64.86, 942.00) tgt=182 hp=50 burst=3
Bot [Bot] Hunter_65 [gunHandgunT1Pistol] id=182 state=Attack pos=(512.00, 64.86, 942.00) tgt=181 hp=50 burst=0
```

## What failed

### No hit lands: DamageEntity returns -1 for bot fire

No target ever lost health. An instrumented build that logged every trigger
pull (two log conditions made unconditional, then reverted) recorded 1108
blocked attempts in one short run, every one a `-1` return from
`EntityAlive.DamageEntity`, and zero applied-damage lines:

```
INF [BotMod] [Bot] Visor_51 shot 205 blocked dmg=16 res=-1 weap=gunMGT1AK47 burst=3
INF [BotMod] [Bot] TankJr_43 shot 205 blocked dmg=16 res=-1 weap=gunHandgunT1Pistol burst=1
INF [BotMod] [Bot] Grunt_24 shot 205 blocked dmg=9 res=-1 weap=gunShotgunT3AutoShotgun burst=1
```

Kill lines: 0 across the run. Chat frag announcements: 0. Bot `hp` stayed 50
in every `bot list` sample.

The early-out is in the b18 assembly. `EntityAlive.DamageEntity` resolves
the attacker from `DamageSource.getEntityId()` (which returns
`ownerEntityId`, `il/full-v3.3.0/_global/DamageSource.il.txt:647`), passes it
to `FriendlyFireCheck`, then refuses the damage when both entities carry the
`Zombie` entity flag (`il/full-v3.3.0/_global/EntityAlive.il.txt:8389`,
returning `-1` at `:8402`). The bots are `zombieSoldier` bodies
(`EntityFlags.Zombie`), and the mod passes the firing bot as the damage
owner through `DamageSourceEntity` (`Source/BotMod/Core/Bot.cs:808`), so
every bot-vs-bot attempt is refused before armor or health is touched. A
bot firing at a zombie body has the same pair of flags and would be refused
too. No code was changed for this: the fix is a design choice (plain
`DamageSource` with no owner, a damage type that bypasses the flag check, or
a mod-side raw-damage path for zombie victims), and the same path exists in
the sibling zdtd wasm servant.

### Ammo insertion fails on b18

Every bot logs that its ammo stack was refused:

```
WRN [BotMod] ammo 'ammo762mmBulletBall' x300 added to neither bag nor inventory; bot [Bot] Slash_91 cannot fire
WRN [BotMod] ammo 'ammo762mmBulletBall' x300 added to neither bag nor inventory; bot [Bot] Slash_49 cannot fire (+ 5 suppressed)
```

`BotSpawner` sets the `added` flag only when `Bag.AddItem` does not throw and
never reads the return of `Inventory.AddItem`
(`Source/BotMod/Core/BotSpawner.cs:600`), so the message itself can be a
false alarm. The fire loop does not read inventory ammo, so this warning is
not the reason for the blocked hits above. It is still a b18 inventory
observation: on the hand-reworked build neither container accepted the
stack in this run.

### No horizontal movement observed

Across `bot list` samples 8 to 70 seconds apart, every x/z stayed identical
while state was `Wander`, `Attack` or `Chase`:

```
16:10:04  Slash_91  id=171 state=Wander pos=(850.00, 59.55, 642.00)
16:11:54  Slash_49  id=177 state=Wander pos=(163.00, 56.38, 818.00)
```

The y coordinate drifted down instead (62.00 at spawn, 59.55 after a few
seconds, 52.83 after about two minutes; one manual `bot spawn 1 600 1000`
placed a body at y=14.28 and it kept dropping). The run had no player in the
world, so the chunks around the DM spawnpoints were not streamed, and the
bodies appear to be falling through inactive terrain rather than patrolling.
This means pathing remains unproven on b18, not proven broken: repeating it
with loaded chunks (a real client or the playtest suite) is the way to
separate the mod's locomotion from the unloaded world.

## What remains unverified on b18

- Bot damage and kills: blocked by the zombie flag check described above.
- Pathing and shoot-on-the-move: no player, no streamed chunks.
- The negative `EntityClass` ids for appended classes and the trader-body
  render failure from the V3.1.0 b14 notes: this run used the shipped
  `zombieSoldier` path only, and the render half needs a client.
- Web dashboard endpoints (the run used the console only).
- The same acceptance repeated with a real player in the world.

## Evidence

All paths relative to the repo root; logs are the game's own output and stay
under git-ignored `.scratch/`.

| File | sha256 | What it shows |
|---|---|---|
| `.scratch/server/logs/botmod-server.log` | `7ac6c74516a2073505ed387c041f14ca0ec7695bca5b7213c9c4701abf81784d` | load, patches, neural load, 6 DM spawns, ammo warnings, no kills |
| `.scratch/server/logs/bot-sample1.txt` | `9f73b2df346b88aaf655dd371d6534f457422bf65d1d9d7b238b0b8a69845069` | `bot status`, `bot list`, `bot neural status` at t=0 |
| `.scratch/server/logs/bot-sample2.txt` | `b127c6eb42eb214a6236a9629ca9394e76e5129266537ee886bf16dba6aa05a3` | clustered bots in Attack, hp 50 |
| `.scratch/server/logs/bot-sample3.txt` | `5d552dc85749e49d5873a0ec7e2b858518cf2c00988c4206e9ecb0b82dfd23c8` | same ids, same x/z, hp 50 |
| `.scratch/server/logs/bot-sample4.txt` | `70f5da6ee9b7d9a2c2006288840fb263729f17fb42f5fd7bf117c99a84495e62` | same after `bot neural off` |
| `.scratch/server/logs/bot-cluster.txt` | `a5976f663a3b6b390bc097abbcb13e3009610c0128619a5064dc1fbdcbc4319b` | `bot remove all`, `bot spawn 4 512 942` |
| `.scratch/server/logs/bot-neural-off.txt` | `1bf7cb0a1d4c3453798378bfb8ad765fb60a6a7f57f3f75619940ef03e82f29e` | `bot neural off` reply, no damage change |
| `.scratch/server/logs/diag2-bot.log` | `3929382f517915005e26bcd0a44c26479342d17b8cdaf290b32e97791628e3dd` | instrumented run: 1108 `DamageEntity` calls, all `res=-1`, 0 kills |

The instrumented build differs from the shipped one only in two logging
conditions in `Source/BotMod/Core/Bot.cs`; the source was reverted before
the gate, and its log is kept as diagnosis, not as the acceptance artifact.
