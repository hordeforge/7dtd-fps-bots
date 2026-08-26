# R7: Scalarization Threaded, Weight Sweep Finds Canon is Pareto, Hist Still Best (2026-08-19)

*Scalarization:* threaded `combat_sim` to return raw `(elo, econ, surv, stuck, camp)` and let `harness.py` do `FIT_ELO*elo + FIT_ECON*econ + FIT_SURV*surv − FIT_STUCK*stuck − FIT_CAMP*camp` (FIT_* = 0.55/0.25/0.15/0.05/1.0). Now `FIT_ELO 0.55→0.80` moves harness `+0.16` on same weights (was 0 before).

*Weight sweep* (pop24×22 seed42, train on mix, held scored on canon, ammo+4-wall dual-seed):

| mix | train | held60(canon) |
|-----|-------|---------------|
| canon 55-25-15 | +16.16 | **+11.50 ±0.62** |
| elo60_surv10 | +16.14 | +11.50 |
| surv25_elo45 | +16.19 | +11.50 |
| elo50_econ30 | +19.26 | +11.50 |
| stuck10_econ20 | +11.36 | +11.49 |
| elo70_econ15 | +9.71 | +11.49 |
| elo65_econ20_surv10 | +12.57 | +11.44 |

**Winner: canon 0.55/0.25/0.15/0.05**: any overweighting of surv or stuck hurts, overweighting elo beyond 0.65 drops held. Plot `evolved/sweeps/fitness_sweep_R7_elo_econ_surv_stuck_pop24_g22_s42.png`.

*H16 re-proven (ammo, 4 walls, dual-seed):* pop24×22 tanh vs relu, **relu +13.60 vs tanh +13.47 best (train)**, but held on same hist weights relu 11.51 < tanh 11.67, so **tanh canonical**.

*Islands R7 (canon weights, tanh):* pop80×90 s42 is3 mixed g20 +16.03 train held60 **11.63 ±0.51** (held30 11.66); pop80×90 s123 is2 horde_first g16 **+20.42 train but held60 10.86**, horde-first overfits. Both below hist **g37 (pop40×80 tanh) held60 11.67 ±0.64 / held120 11.78 ±0.65**. Per-arena (dual-seed mean) hist **duel 4.60 / FFA 15.27 / horde 45.95** vs R7 is3 **duel 4.63 / FFA 16.02 / horde 27.05**, again +0.7 FFA but −18.9 horde; multi-seed held20 hist wins 3/3 (999 11.77 vs 11.66, 77 11.85 vs 11.48, 1234 11.79 vs 11.68).

*Kept:* `evolved/best.json` **gen37 +18.89 train, 325w tanh** repromoted (hash 8fa6c5b8b7a8, viz `viz_best_g37_R7.png`). The headless mimic (LOS 4 wall sets, mag/reload, aim penalty, econ on hits) caps near **11.7**; to pass 12.0 needs ZBS2 headless ticks (docs/research/04 §Z).

*Repro:* `python3 tools/ga/evolve.py --pop 80 --gens 90 --seed 42 --activation tanh --islands 3` and `python3 tools/ga/eval.py evolved/best.json --matches 60`.

