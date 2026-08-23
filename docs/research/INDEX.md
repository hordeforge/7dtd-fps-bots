# docs/research — Index

The neuroevolution and bot-AI research for the 7dtd-fps-bots bot mod. Two layers:

- **Design docs `00..06`** — the architecture, environment/fitness, GA, training
  pipeline, integration, and roadmap as originally planned.
- **Experiment log `REPORT-*.md`** — every run/experiment as it happened, R0-R13.
  The R-series is the authoritative record; the design docs describe intent.

## Design docs

| Doc | Role |
|---|---|
| [`00-overview.md`](00-overview.md) | Project overview |
| [`01-neuroevolution-architecture.md`](01-neuroevolution-architecture.md) | Net shape 14-16-5, obs vector, sense/act contract |
| [`02-environment-and-fitness.md`](02-environment-and-fitness.md) | Arenas, scalarized fitness, determinism |
| [`03-genetic-algorithm.md`](03-genetic-algorithm.md) | Operators, islands, curriculum |
| [`04-training-pipeline.md`](04-training-pipeline.md) | Train/eval/promote pipeline, cost knobs |
| [`05-integration.md`](05-integration.md) | Live `BotNeuralBrain` integration |
| [`06-experiments-and-roadmap.md`](06-experiments-and-roadmap.md) | Ablations, roadmap |

## Experiment log (R-series)

| Report | What happened |
|---|---|
| [`REPORT-2026-08-19-R0.md`](REPORT-2026-08-19-R0.md) | R0 synthetic scaffold, harness smoke |
| [`REPORT-2026-08-19-R1-combat-GA.md`](REPORT-2026-08-19-R1-combat-GA.md) | First combat GA |
| [`REPORT-2026-08-19-R1b-layout-sweep-combat.md`](REPORT-2026-08-19-R1b-layout-sweep-combat.md) | Arena-layout sweep on the combat harness |
| [`REPORT-2026-08-19-R1-final.md`](REPORT-2026-08-19-R1-final.md) | Crazy-good net from scratch |
| [`REPORT-2026-08-19-R1-live-dedi.md`](REPORT-2026-08-19-R1-live-dedi.md) | Neural brain proven on the live dedi |
| [`REPORT-2026-08-19-R2-harness-ceiling.md`](REPORT-2026-08-19-R2-harness-ceiling.md) | Harness ceiling + zero-brain smoke test |
| [`REPORT-2026-08-19-R2b-held-repro.md`](REPORT-2026-08-19-R2b-held-repro.md) | Held-out repro check |
| [`REPORT-2026-08-19-R2c-pop60x120.md`](REPORT-2026-08-19-R2c-pop60x120.md) | Bigger effort does not beat the held champ |
| [`REPORT-2026-08-19-R2-final.md`](REPORT-2026-08-19-R2-final.md) | Final R2: holds at 500 |
| [`REPORT-2026-08-19-R3-held-promo.md`](REPORT-2026-08-19-R3-held-promo.md) | Held-gated promotion, overfit noted |
| [`REPORT-2026-08-19-R4-activation-env.md`](REPORT-2026-08-19-R4-activation-env.md) | Activation wired, env diversity, relu loses |
| [`REPORT-2026-08-19-R5-islands-ammo.md`](REPORT-2026-08-19-R5-islands-ammo.md) | Ammo pacing, islands, curriculum |
| [`REPORT-2026-08-19-R6-freshness-horde.md`](REPORT-2026-08-19-R6-freshness-horde.md) | Freshness gating, horde pressure |
| [`REPORT-2026-08-19-R7-scalarization-weight-sweep.md`](REPORT-2026-08-19-R7-scalarization-weight-sweep.md) | Canon scalarization proven Pareto |
| [`REPORT-2026-08-20-R8-fire-cost-pacing.md`](REPORT-2026-08-20-R8-fire-cost-pacing.md) | Fire-cost task rework: pacing beats spam, GOAL MET |
| [`REPORT-2026-08-21-R9-regularization-and-arena.md`](REPORT-2026-08-21-R9-regularization-and-arena.md) | F=36 draw regularization, opponent-arena exploration |
| [`REPORT-2026-08-21-R10-policy-movement.md`](REPORT-2026-08-21-R10-policy-movement.md) | Policy-driven movement (sim + live C#), +6 margins |
| [`REPORT-2026-08-21-R11-residual-sweep.md`](REPORT-2026-08-21-R11-residual-sweep.md) | Duel re-test, champion plateau, retreat-guardrail fix |
| [`REPORT-2026-08-21-R12-duel-arena.md`](REPORT-2026-08-21-R12-duel-arena.md) | Discriminative duels, champion 11.91, GOAL MET |
| [`REPORT-2026-08-21-R13-static-bot-parity.md`](REPORT-2026-08-21-R13-static-bot-parity.md) | Static (Q3/D3) bot parity audit vs zdtd guest |

## Current state (2026-08-21)

- **Shipped champion**: `evolved/best.json`, gen 199 (R12 run
  `2026-08-21_110155_pop64_g200_s42`, warm-started from the R11 gen-299
  champion), held **13.04 avg**
  on the canonical gate (seeds 999/1234/4242, 40 matches):
  `python3 tools/ga/eval_static_vs_neural.py --seeds 999 1234 4242 --matches 40`
  prints **GOAL MET** with margins +8.044/+8.862/+8.223 (re-measured after the
  R13 magazine alignment; before it the champion held 11.91 avg,
  margins +7.176/+7.747/+7.223).
- **Task**: fire cost (finite ammo + spread) + policy-driven movement + fixed-
  opponent duel arenas (R8/R10/R12). Training uses F=36 draw regularization;
  the gate pins F=18.
- **Live mod**: `Source/BotMod` ports the R10 movement semantics (net-driven
  velocity with Q3 fallback); build/install via `make build` / `make install`.
- **Open items**: static-bot parity alignment (R13 section 5, items 1-3 still
  need direction; item 4, magazine sizes, was executed in R13 section 6);
  live kite-pattern verification via the playtest client suite (R10/R11).

## Cross-repo

The static (Q3/Doom 3-style) heuristic bot AI is cross-pollinated with the zdtd
bot brain guest; the parity ledger is [`REPORT-2026-08-21-R13-static-bot-parity.md`](REPORT-2026-08-21-R13-static-bot-parity.md),
referenced from zdtd's `docs/BOTS_SPEC.md`.
