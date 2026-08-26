# R4: Activation Wired, Env Diversity On, Relu Loses: Hist Still Best (2026-08-19)

*Sweep (real now):* wired `combat_sim.simulate_match_relu` + `harness.ACTIVATION` dispatch + `evolve --activation`. Pop24×22 seed42 H16 tanh vs relu with env diversity (L/cross/open/corridor by seed&3, dual-seed 18 matches, annealed sigma+HOF): **relu +13.88 best vs tanh +13.62 best (train)**, but harness is dual-seed so train≠held. Pushed to `evolved/sweeps/sweep_H16_tanh_vs_relu_pop24_g22_s42.png`.

*Big runs (with env diversity):* pop64×90 tanh s42 g70 +15.91 train held60 +11.56; pop64×90 relu s99 g11 +15.96 train held60 +11.17; fresh pop80×100 s123/s777 still spinning. All below hist **g37 pop40×80 s42 +18.89 train held60 +11.73 (was +11.83 pre-env; regression is the cost of generalizing to 4 maps, but still ahead)**.

*Why 12.2 not broken:* env diversity lowers the single-map ceiling; relu is +0.26 train but −0.39 held vs tanh, it overfits walls. GA operator tweaks (annealed 1.0→0.45, HOF8, dual-seed) help but headless fidelity is now the cap. Next real gain is `ZBS2` headless ticks (docs/research/04 §Z), same GA on real bot ticks.

*Kept:* `evolved/best.json` g37 tanh (325w) repromoted (best.meta updated) since no new run beats its held60. Viz `evolved/sweeps/viz_best_g37_R4.png`.

*Repro:* `python3 tools/ga/evolve.py --pop 64 --gens 90 --seed 42 --activation tanh` and `python3 tools/ga/eval.py evolved/best.json --matches 60`.

