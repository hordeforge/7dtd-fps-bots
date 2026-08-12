# Quake 3 / Doom 3 inspiration for 7DTD bots

Derived from GPL sources `/tmp/ioq3` (ioquake3) and `/tmp/d3` (DOOM 3).
See `chars.h`, `ai_main.c:BotChangeViewAngles/BotAimAtEnemy/BotCheckAttack`,
`ai_dmq3.c` goals, `botlib/be_aas*`, `botlib/botlib.h` constraints, and
`d3/neo/game/ai/AAS.*`.

## What we borrowed

- **Character file (80 entries)**: Q3 `bots/<name>_c.c` has per-skill blocks
  with `ATTACK_SKILL`, `AIM_SKILL/ACCURACY`, `AIM_ACCURACY_MACHINEGUN/...`
  per weapon, `REACTIONTIME`, `VIEW_FACTOR/MAXCHANGE`, `FIRETHROTTLE`,
  `CROUCHER/JUMPER/WALKER`, `AGGRESSION/SELFPRESERVATION/VENGEFULNESS`,
  `CAMPER`, `ALERTNESS`, `EASY_FRAGGER`. Ported as
  `Source/BotMod/Config/BotCharacter.cs` + `config/characters.json`.

- **Aim model (BotAimAtEnemy)**: reaction gate `enemysight_time + 0.5*reaction`,
  per-weapon accuracy/skill, `crandom()*(1-acc)` jitter on origin (20) and
  angles (`6*vspread*(1-acc)`), linear leading (`dist/speed * vel`) when
  `aim_skill>0.4`, exact `AAS_PredictClientMovement` when `>0.8`, ground-splash
  for radial damage when `>0.6`.

- **Attack model (BotCheckAttack)**: `firethrottle` flip-flop timers,
  FOV 120 close / 50 far, weapon offset trace with bbox `{-8..8}`, teammate
  abort, radial damage radius check.

- **View angles (BotChangeViewAngles)**: challenge mode is clamped smooth
  (`factor*diff`), normal is under-damped spring (`viewanglespeed += speed-diff`,
  damped `0.45*(1-factor)`) — ported as `ChallengeAim` toggle.

- **Movement / AAS**: Q3 `TFL_*` flags (WALK/JUMP/CROUCH/LADDER/SWIM/TELEPORT)
  mapped onto 7DTD `Block`/`Chunk` checks; Doom3 `AAS.FindCover/OutOfRange/
  AttackPosition` evaluators via PVS → emulated with voxel ray + high-ground scan.

- **Decisions**: `BotWantsToRetreat/Chase/Help/Camp` based on health/weapon vs
  `aggression/selfpreservation/camper/alertness/easy_fragger`.

These are the references for `BotCharacter`, `WeaponProfile`, `Bot.AimModel`,
`BotBrain` goal selection and the AAS shims.
