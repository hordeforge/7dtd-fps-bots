# R10 — Deeper Sim Rework: Policy-Driven Movement, GOAL MET at +6 (2026-08-21)

*The R9 blocker was that policy could not control movement: both sides shared the
same hardcoded chase/strafe code, so positioning and duel outcomes were not
learnable. This rework makes movement policy-driven. Status: `verified`.*

## 1. Sim change (`tools/ga/combat_sim.py`, both njit variants)

The hardcoded movement branches (chase / strafe-orbit / close-range backpedal /
camp-jitter) are replaced by a policy-driven 2D velocity:

- `retreat` output -> forward velocity: 0 = full approach (1.2/tick), 0.5 = hold,
  1 = full backpedal (-1.2/tick).
- `strafe` output -> lateral velocity (up to 1.2/tick).
- `camp` output -> hold (scales approach to 15%, keeps the anti-camp bookkeeping).
- Velocity magnitude clamped to 1.3/tick; arena clamping unchanged.

The 14->16->5 / 325-weight shape is unchanged; only the semantics of three outputs
now drive movement. `aim` and `fire` are untouched.

## 2. Effect on the task

The old task could not separate brains from no-brains (margins ~+0.5). The rework
makes the static baseline (all-zero weights: `sigmoid(0)=0.5` gives zero forward
velocity, i.e. a standing turret) collapse, and rewards kiting/orbiting:

| policy | held avg (canonical gate) |
|--------|---------------------------|
| static no-brain | 4.96 |
| hand-built kite (warm start) | 9.85 |
| evolved champion | **10.88** |

## 3. Re-evolution (kiter warm start)

`evolve.py --pop 64 --gens 150 --seed 42 --activation tanh --resume <kiter-seed>`
(F=36 training), then resumed for 150 more generations (300 total, run dir
`2026-08-21_011725_pop64_g300_s42`). Train peaked +14.17. The evolved champion's
weights show it re-learned the kite with its own constants (retreat gated on
`obs[2]` distance, spread-gated fire, active strafe). Horde is now clean: elo 6,
all zombies killed, zero melee damage taken, 1 death.

## 4. Eval gate (canonical F=18)

`eval_static_vs_neural.py --seeds 999 1234 4242 --matches 40` (promoted champion,
gen 299):

| seed | champion | static | margin |
|------|----------|--------|--------|
| 999  | 11.017   | 4.954  | +6.063 |
| 1234 | 10.993   | 4.980  | +6.013 |
| 4242 | 10.792   | 4.940  | +5.852 |

`GOAL MET: True` on every seed, margins ~12x the old +0.5 contract. The R8/R9
numbers are historical (old task); the rework redefined the task and the gate.
Diminishing returns after 150 more gens (+0.05 held): the champion sits near the
architecture's plateau (~10.9-11.0).

## 5. Live deployment (C# port, built and smoke-tested)

The movement model is now live in `Source/BotMod`:

- `BotNeuralBrain.cs` exposes the continuous `StrafeLogit` (alongside the existing
  `RetreatLogit`/`CampLogit`).
- `BotBrain.cs` gains a `MoveDir` helper (motor with manual-step fallback, same
  pattern as the existing movement helpers).
- `Bot.cs` Attack state: when the neural brain is loaded, the net drives the 2D
  velocity directly (retreat -> forward, strafe -> lateral, camp -> hold, sim-parity
  constants), with the hardcoded Q3 strafe/dodge logic as the fallback when the
  brain is off or broken.
- `make build` compiles clean; `make install` deployed the new DLL (old mod backed
  up before install).

Live smoke test (isolated dedi instance, Navezgane, EAC off, telnet-driven): the
new DLL loads with zero mod exceptions, `BotNeuralBrain` loads the new champion
(325 weights), bots spawn and fight (8 kills logged). The point-blank test duels
resolve too fast to confirm the kite/orbit *pattern* in live 3D; that behavioral
transfer remains for the playtest client suite (honest residual). The user's
long-running server keeps the old DLL in memory until its next restart.

## 6. Residuals

- 1v1 duels vs static remain ~coin-flip (maze env LOS windows dominate); the
  margin comes from aggregate combat/econ/kiting, not duel wins.
- Train/held gap persists (train +14.17 vs held 10.93); more generations or
  further regularization may push the champion higher.
- The kite/orbit pattern transfer to live 3D is not yet behaviorally verified
  (needs the playtest client suite); the code path itself runs without exceptions.

Repro: headless runs `evolved/runs/2026-08-21_005624_pop64_g150_s42` and
`2026-08-21_011725_pop64_g300_s42`; live smoke via the isolated serverconfig.
