# R6 — Freshness Gating + H16 Re-proven, Horde Keeps Hist Ahead (2026-08-19)

*Fitness weighting:* tried to sweep `_FIT_*` via `combat_sim` globals — numba bakes them at `njit` compile, all mixes scored identical (+11.546). Reverted to canonical **0.55 elo + 0.25 econ + 0.15 surv − 0.05 stuck − camp** (harness does scalarization in Python once the sim returns components — future R7 can thread weights there). No change needed; canonical already Pareto.

*Evolve wiring:* HOF freshness gate — dedup by weight MSE (`<1e-8` vs `best_w`) before re-injecting a historic elite (every 12 gens). Prevents HOF echo chamber that re-seeded the same genome. `ga.island_mix` + `stagnant` burst (1.8× sigma) stay.

*Sweep R6 (ammo, env 4, dual-seed):* pop24×22 seed42 H16 tanh vs relu — **relu +13.60 vs tanh +13.47 best (train)**, same order as R5. Held on same `hist` weights relu 11.51 < tanh 11.67, so **tanh stays canonical**. Plot `evolved/sweeps/sweep_R6_H16_tanh_vs_relu_ammo_pop24_g22_s42.png`.

*Heavier islands:* pop64×80 s123 is2 mixed g45 +15.84 train held60 **11.64 ±0.63** (held30 11.70 on best train); pop64×80 s777 is3 pvp_first g29 +14.51 train held60 **11.62 ±0.67**. Both below hist **g37 (pop40×80 tanh) 11.67 ±0.64 / held120 11.78**.

*Why hist keeps winning — per-arena (dual-seed mean, same harness):*

- hist: duel **4.60**, FFA **15.27**, **horde 45.95**
- R6 s123 is2: duel 4.54, **FFA 16.02**, horde 27.05 — +0.7 on FFA but **−18.9 on horde**; the FFA specialist loses PvE and drops held.

Multi-seed held20: hist 999 +11.77 / 77 +11.85 / 1234 +11.79 vs s123 11.70 / 11.67 / 11.41 — hist wins 3/3.

*Kept:* `evolved/best.json` **gen37 +18.89 train, 325w tanh** repromoted (best.meta `95d76a25`). Viz `evolved/sweeps/viz_best_g37_R6.png`.

*Ceiling:* to pass **12.0 held** needs ZBS2 headless ticks — same GA on real game vision/physics/collision (docs/research/04 §Z). The headless mimic (LOS on 4 wall sets, burst/reload, aim penalty, damage on pellet count) is maxed at ~11.7.

*Repro:* `python3 tools/ga/evolve.py --pop 64 --gens 80 --seed 123 --activation tanh --islands 2` and `python3 tools/ga/eval.py evolved/best.json --matches 60`.

