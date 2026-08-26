# R12: Duel Arena Rework: Discriminative Duels, Champion 11.91 (2026-08-21)

*R11 diagnosed why duels could not discriminate: bots spawned 8-26 units apart,
drew random weapons, and fights were decided in ~3 seconds of close-range fire
before any kiting developed. This rework gives duels a real structure. Status:
`verified`.*

## 1. Sim pins (`tools/ga/combat_sim.py`, both njit variants)

Three optional parameters, all default-off (existing behavior unchanged):

- `spawn_gap` (float): bots spawn at a fixed separation on a line through the
  arena center instead of the random 8-26 ring.
- `env_pin` (int): force the arena env (2 = open) instead of `seed % 5`.
- `wep_pin` (int): force an equal loadout for both sides instead of random draws.

## 2. Harness (`tools/ga/harness.py`)

The mixed curriculum now includes three fixed-opponent duels per genome
(champion vs the static no-brain, `n_evolved=1`) using the pins:
50-unit spawn gap, open env, equal AKs (`DUEL_SPAWN_GAP=50`, `DUEL_ENV=2`,
`DUEL_WEAPON=2`). The elo term finally rewards winning fights: the evolved side
scores `kills_ev - deaths_ev` in duels, while the static baseline gets 0 in its
own self-duels.

## 3. Results

- Duel discrimination: champion 1v1 win rate vs static went **17% -> 54-70%**
  (open-env pinned duels with equal loadouts).
- Re-evolution (F=36, pop 64 x 200 gens, champion warm start, run
  `2026-08-21_110155_pop64_g200_s42`): train peaked +16.45.
- Canonical gate:

| seed | champion | static | margin |
|------|----------|--------|--------|
| 999  | 11.694   | 4.518  | +7.176 |
| 1234 | 12.331   | 4.584  | +7.747 |
| 4242 | 11.695   | 4.472  | +7.223 |

`GOAL MET: True`. Champion held **11.91 avg** (was 11.11 after the guardrail
fix, 10.93 at R10): +0.80.

## 4. Notes

- The new champion's head-to-head duel rate (54%) is below the pre-rework
  champion's (70%): the GA optimized the full scalarization (duels are one
  rewarded component among horde/FFA/econ), which raised the aggregate more
  than pure duel win rate. Duels are now discriminative and rewarded, which
  was the goal.
- Static baseline dropped slightly (4.96 -> 4.53) under the new arena, so
  margins grew faster than the champion's absolute score.
- The C# live port from R10 is unaffected (sim-only changes; the live brain
  still uses the R10 movement semantics).

## 5. Follow-up: 400-gen attempt overfit (not promoted)

Resuming the duel-arena run for 200 more generations (run
`2026-08-21_110740_pop64_g400_s42`, train peaked +18.20) produced a champion
that holds **11.04 avg, below the 200-gen champion's 11.91**, the extra
training overfit the seed-42 draw chain (train/held gap widened). Per the
held-gated promotion rule it was NOT promoted; the 200-gen champion
(11.91, margins +7.18/+7.75/+7.22) remains the shipped genome.
