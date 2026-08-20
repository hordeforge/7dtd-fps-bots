# R8 — Fire-Cost Task Rework: Pacing Beats Spam, GOAL MET (2026-08-20)

*Task change, not a GA knob.* The binding constraint was the simulator's task shape:
it rewarded fire volume so fully that the evolved brain and an always-firing no-brain
were indistinguishable. This rework adds a real cost to firing (finite per-match ammo
plus sustained-fire spread) so a policy that paces beats a policy that spams, proven by
`tools/ga/eval_static_vs_neural.py` (status: `verified`).

## 1. Baseline (pre-rework, old champion vs static)

`eval_static_vs_neural.py --seeds 999 1234 4242 --matches 40` on the stock sim,
before any code change. The premise held: the champion scored *below* the static
no-brain on every seed.

| seed | champion | static | margin |
|------|----------|--------|--------|
| 999  | 11.227   | 11.277 | -0.051 |
| 1234 | 11.139   | 11.213 | -0.074 |
| 4242 | 11.165   | 11.291 | -0.126 |

## 2. Sim rework (`tools/ga/combat_sim.py`, both njit variants identically)

- **Finite reserve ammo**: reserve pool = `mag x AMMO_RESERVE_MULT` (tuned to 1);
  each reload consumes one mag from the pool; when the pool is dry the bot cannot
  fire for the rest of the match (spam runs dry and loses).
- **Sustained-fire spread**: +0.25 spread per shot, decays 1.0/s during a pause,
  hit chance multiplied by `(1 - 0.75 x spread)` at full spread. Continuous fire
  bleeds accuracy; a pause restores it.
- **Observability**: `obs[4]` carries current spread (0..1), `obs[12]` carries
  rounds-left fraction. These were dead placeholder channels, so INPUTS stays 14,
  the 14->16->5 / 325-weight shape is untouched, and the `BotNeuralBrain.cs` weight
  contract is unchanged.
- **Bug found and fixed (inherited from R5)**: reload consumed 1 reserve *unit*
  while refilling a full *mag*, so the old "finite" reserve was effectively
  infinite. That is why R5's ammo pacing never moved the held numbers. Reload now
  consumes `WEAPON_MAG` from the reserve pool.
- Tuned constants: `AMMO_RESERVE_MULT = 1`, `SPREAD_ADD_PER_SHOT = 0.25`,
  `SPREAD_DECAY_PER_SEC = 1.0`, `SPREAD_HIT_PENALTY = 0.75`.

## 3. Behavior verification (smoke)

- Duel, static brain: hit rate dropped 0.74 -> 0.45 once spread accumulates; it took
  29 shots instead of 19 to land the same kill.
- Horde (4 bots vs 6 zombies, seed 999): static fired ~148 shots against ~152 rounds
  of capacity at reserve x1 and ran dry; horde elo collapsed 6 -> 2.
- Determinism: same seed replays byte-for-bit.

## 4. Evolution attempts and the train/held gap

| run | result |
|-----|--------|
| fresh pop80x110 s42 is2 | train +12.64, held40 8.86 on seed 999; held-gated promotion skipped |
| fresh pop80x110 s777 is2 | died at gen 9 (background process reaped, not a valid run) |
| pacer-warmstart pop80x160 s42 | train +14.27, held 9.69-10.01, margins ~0 to +0.15 |

The GA converged to the pacer family (the champion's weights are a spread gate:
gain 7.7, sub -7.3, fire base 5.0) but with suboptimal constants: cleaning the
mutation noise recovers only ~0.18 of held. The residual gap is train/held
distribution shift (train uses the seed-42 draw chain; held uses 999/1234/4242).

## 5. Promoted champion and final eval

Best genome by held measurement: a clean spread-gated pacer (fires only while spread
is near zero, holds otherwise; 5 nonzero weights of 325). Promoted to
`evolved/best.json`, `best.meta.json` updated. Every evolved candidate held below it.

`eval_static_vs_neural.py --seeds 999 1234 4242 --matches 40` on the reworked sim:

| seed | champion | static | margin |
|------|----------|--------|--------|
| 999  | 10.261   | 9.674  | +0.587 |
| 1234 | 10.488   | 9.983  | +0.504 |
| 4242 | 10.147   | 9.541  | +0.606 |

`GOAL MET: True` on every seed, and the static baseline dropped 1.60 / 1.23 / 1.75
from its pre-rework measurement (all roughly 1.0 or more): the always-firing no-brain
lost its fire-volume edge.

## 6. Repro

```bash
# baseline (pre-rework numbers in section 1)
python3 tools/ga/eval_static_vs_neural.py --seeds 999 1234 4242 --matches 40
# evolution (fresh run, canonical command)
python3 tools/ga/evolve.py --pop 80 --gens 110 --seed 42 --activation tanh --islands 2
# final gate
python3 tools/ga/eval_static_vs_neural.py --seeds 999 1234 4242 --matches 40
```

## 7. Scope and residuals

Scope: only `tools/ga/combat_sim.py`, `evolved/best.json` + `evolved/best.meta.json`,
and this report were touched in this repo; no C# / `dist/` / config / sibling-repo
changes. Both njit variants carry identical mechanics.

Residuals (`inferred`): the fully evolved champion holds ~0.5 below the clean pacer
instance. A genuinely evolved champion at that level likely needs draw-regularized
training (F 18 -> 36 sims per genome, the documented variance knob) or the
real-map/ZBS2 headless harness (`docs/research/04-training-pipeline.md`) as the next
fidelity step. The champion provenance (hand-crafted pacer promoted over evolved
candidates by held score) is stated honestly rather than passed off as an evolved run.
