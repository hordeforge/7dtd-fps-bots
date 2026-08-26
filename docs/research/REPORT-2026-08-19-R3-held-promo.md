# R3: Held-Promo: 11.83 Reinstated as Best, Pop80 Overfit Noted (2026-08-19)

*What changed:* patched `harness.py` to dual-seed evaluate (mean of 999 and 999^GOLDEN, 18 matches) so train fitness punishes single-seed overfit; `ga.py` sigma now cosine-anneals (1.0→0.45) and `evolve.py` keeps an 8-wide HOF. Re-ran pop80×120 ×2 (seeds 42, 777) and a combat_sim layout sweep H16/H24/H32×tanh/relu.

*Sweep:* sweep activation patch is dead code: `combat_sim.forward_numba` is fixed tanh, so relu==tanh scores were a bug to fix before any layout claim. Size sweep with harness shows H16 tanh still top (pop24×22 seed42: H16 +12.16, H32 +11.99, H24 +11.80), not a promotion signal.

*Big runs (dual-seed train):* pop80×120 s42 best-train g20 +16.17 held60 +11.69; s777 g74 +16.22 held60 +11.62, both below the single-seed-era hist **g37 pop40×80 s42 +18.89 train / +11.83 held60** (the most general so far). Hist beats every fresh run on held60 (s42 s777 s99 baselines all 11.62–11.69).

*Action:* repromoted hist `evolved/runs/2026-08-19_013411_pop40_g80_s42/gen_037.json` → `evolved/best.json` (325w, H16 tanh, gen37 +18.89). `dist/BotMod/evolved/best.json` and installed `Mods/BotMod/evolved/best.json` updated; `make build` ships it. Viz `evolved/sweeps/viz_best_hist_11.83.png` and `fitness_hist_11.83.png`.

*Held repro:* `python3 tools/ga/eval.py evolved/best.json --matches 40` → held40 +11.82±0.89 vs random −0.16±0.11 (delta +11.98, 100% win). Per-arena tight harness unchanged: FFA/Horde strongest, 1v1 +4.5.

*Next:* wire `combat_sim.forward_numba` activation switch properly, then a real H24/H32 sweep; until held breaks 12.5, don't chase bigger nets.

