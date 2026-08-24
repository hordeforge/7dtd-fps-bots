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

These are the references for `BotCharacter`, `WeaponProfile`, `BotBrain.LeadAimPoint`
(the aim model, plus the per-engagement `_aimBiasYaw` roll), `BotBrain` goal
selection and the AAS shims (`FindCover`, voxel LOS).

## Cross-pollinated with zdtd_bot (the sibling Zig Wasm server)

Improvements travel both ways with `../zdtd-server-server`'s `mods/zdtd_bot` Wasm brain
(`docs/q3-inspiration-notes.md` there). Everything stays deterministic via the
per-bot LCG (`Config.Lcg`, held as `Bot._rng` and drawn through `Rng01()`/
`RngSym()`, seeded from entity id).

- **Lost-sight combat memory (from zdtd_bot).** While a target stays retained
  but out of sight, chase where it was last SEEN instead of its live position
  (`Bot._lastKnownTargetPos` / `_hasLastKnownTarget`), so a bot corners/flanks
  around cover; zdtd's `BOT_MEMORY_TICKS` equivalent is `LoseTargetTimeSec`.
- **Per-engagement aim bias (from zdtd_bot skill_aimerr).** On target
  acquisition, roll a fixed skill-scaled yaw bias
  (`Bot._aimBiasYaw = RngSym() * (1 - AimAccuracy) * 0.45`) and rotate the lead
  aim by it each attack tick: imperfect but stable shots, not perfect aimers.
- **Grudge / vengeance memory (from zdtd_bot retaliation).** Being hit records
  the attacker as a grudge for 15 s (`Bot._grudgeId` / `_grudgeUntil`);
  `FindTarget` out-scores the grudged id (0.6x) so the bot keeps re-acquiring
  whoever shot it even after they leave LOS (zdtd's `GRUDGE_TICKS` +
  `GRUDGE_SCORE`). A heavy hit (>25 damage, ~2x the pistol floor) staggers the
  dodge longer (`OnDamaged(strength)`), so snipers genuinely daze bots.
- **Ammo pacing (from zdtd_bot).** `WeaponProfile.MagSize`/`ReloadSec` per gun;
  an empty magazine starts a reload (`TryShootBurst` holds fire, movement
  continues) — every trigger pull consumes a round whether it hits or misses
  (zdtd `weapon_mag`/`weapon_reload` parity).
- **Already shared:** per-slot LCG + deterministic burst cadence (ported from
  zdtd_bot in `cross:` commits), headshot chance/multiplier, dodge-on-hit,
  player-preference targeting (0.82), backpedal, low-hp retreat, lead-fire,
  weapon-profile range/burst tactics, cover-seeking retreat (`FindCover`).
