# R9: Draw Regularization + Opponent-Arena Exploration, Plateau Below the Hand-Built Pacer (2026-08-21)

*Goal: improve the GA bots beyond R8. Outcome: the GA's evolved champion improved
measurably (9.76 -> 10.11 avg held) via draw regularization, but four evolution runs
all plateau below the hand-built spread-gated pacer (A2, 10.30). Status: `verified`
for the numbers, `blocked` for "evolution beats A2" without deeper sim changes.*

## 1. Draw regularization (F=36): kept, training-time only

`harness.DRAWS_PER_CONFIG = 2`: every arena config is sampled twice per seed-stream,
so a genome cannot overfit the exact arena-draw chain. The best evolved champion to
date came from the F=36 160-gen warm-started run: **10.11 avg held on the canonical
gate** vs ~9.76 for every pre-F=36 evolved champion (+0.35).

The gate is pinned to the canonical sample: `eval_static_vs_neural.py` sets
`harness.DRAWS_PER_CONFIG = 1` in `held()`, so training-time knobs never leak into
the measuring stick. R8's contract reproduces exactly (GOAL MET, +0.587/+0.504/+0.606).

## 2. Fixed-opponent arenas: built, tested, reverted

Implemented the arena promised in `docs/research/02` §2.1 (bot vs a fixed opponent
pool): both `combat_sim` njit variants accept `w_opp`/`n_evolved` and return per-side
stats (kills/deaths/damage/shots/hits for the evolved side only). Also fixed a latent
relu-variant bug where zombie melee never killed bots.

Finding: **1v1 duels vs the static no-brain do not discriminate policy quality in
this sim.** With random weapons A2 (which beats static by a large margin on aggregate
metrics) wins only ~45-60% of duels; with equal AKs on both sides it is still a coin
flip (40-60%) on every env. Both sides share the same hardcoded movement code, so the
policy only modulates fire timing and the spammer's DPS is comparable to the pacer's.
A duel arena cannot produce a usable fight-winning gradient without deeper changes
(per-bot movement control, fixed loadouts). The harness was reverted to the canonical
all-same arenas; the sim's opponent machinery is retained, backward-compatible and
dormant.

Side discovery: the sim's aggregate `damage_taken` only counts zombie melee; bot-vs-
bot damage never increments it (FFA econ ~58 = dd/10). A side-aware `damage_taken`
that counts all damage received is a better efficiency metric and is available for
future work, but it changes the fitness scale, so it was not shipped.

## 3. Evolution attempts vs A2 (held avg on the canonical F=18 gate)

| run | train | held avg | vs A2 (10.30) |
|-----|-------|----------|---------------|
| fresh pop80x110 s42 (F=18) | +12.64 | 9.6-10.0 | below |
| warm-start pop80x160 s42 (F=18) | +14.27 | 9.76 | below |
| warm-start pop80x160 s42 (F=36) | +12.83 | 10.11 | below |
| warm-start pop64x200 s42 (F=36) | +12.70 | 9.97 | below |

The longer run overfit harder (train 12.70 -> held 9.97): F=36 reduces the train/held
gap but does not close it. The GA optimizes the seed-42 draw chain; the held seeds
(999/1234/4242) reward slightly different constants, and A2's exact gate (fires only
below 0.107 spread) happens to be the held optimum. Every evolved champion is the
pacer family with a wider gate and mutation noise.

## 4. Verdict and next steps

Shipped champion remains A2 (10.30, margins +0.59/+0.50/+0.61). The GA's output is
genuinely improved (+0.35 by F=36), but beating A2 requires one of:

- per-bot movement control in the sim (policy drives navigation, not just fire
  timing), which would make positioning and duel outcomes learnable; or
- the real-map/ZBS2 headless harness (`docs/research/04`) for spatial decisions; or
- validation-selected elites (hold a candidate on the held seeds during evolution).

Repro: `python3 tools/ga/evolve.py --pop 64 --gens 200 --seed 42 --activation tanh
--resume <warmstart-ckpt>` (F=36 training) then
`python3 tools/ga/eval_static_vs_neural.py --seeds 999 1234 4242 --matches 40` (pinned F=18 gate).
