# R5 — Ammo-Paced Sim, Islands + Curriculum, Held Ceiling Holds at 11.68 (2026-08-19)

*Sim parity:* added `WEAPON_MAG/WEAPON_RELOAD` to `tools/ga/combat_sim.py` and `ammo/reload_cd` per-bot in both `simulate_match` (tanh) and `simulate_match_relu` (relu). Matches `Source/BotMod/Core/Bot.cs` ammo pacing (`MagSize 5..32`, reload 1.2..2.6 s). Held60 on hist g37 slipped **11.73 → 11.67 ±0.64** — the cost of honest fire discipline, still > random −0.17.

*Evolve wiring:* `tools/ga/ga.py` stagnant burst (sigma 1.8× after 8 plateau gens, pReset/pSwap up) + `island_mix` helper. `tools/ga/evolve.py` gains `--islands 1..8` (ring-migrate 2 per 10 gens, per-island elitism 2) + `--curriculum mixed|pvp_first|horde_first` (early third gates harness AR configs) + HOF8 + held20 probe every 5 gens (csv has held column). `harness.CURRICULUM` gates arena mix.

*Sweep R5 (ammo-aware):* pop24×22 seed42 H16 tanh vs relu — **relu +13.60 best (train) vs tanh +13.47** — gap narrowed vs R4 (+13.88 vs +13.62) now that ammo constrains burst spammers; tanh still wins held (11.67 vs 11.51 on same hist w). Plot `evolved/sweeps/sweep_R5_H16_tanh_vs_relu_ammo_pop24_g22_s42.png`. Tanh stays canonical.

*Islands:* pop80×110 s42 is2 mixed g92 +16.06 train held60 **+11.67 ±0.51** (held30 best at g14 11.71→11.75 on 60, 11.79 on 120 — 55% head-to-head vs hist, not significant). Pop80×110 s99 is2 pvp_first g69 +14.37 train held60 +11.57. Neither beats hist g37 on held60 (+11.67) or held120 (+11.78). Islands help train exploration but headless diversity (4 walls × dual-seed × curriculum) is maxed.

*Held table (ammo, env L/cross/open/corridor, dual-seed):*

- hist g37 (pop40×80 s42 tanh) **held60 +11.67 ±0.64, held120 +11.78 ±0.65** — **kept as best.json gen37 +18.89 train**
- is2 s42 g92 held60 +11.67 ±0.51 (tie), g14 held120 +11.79 ±0.57 (33/60 wins, 55%)
- is2 s99 pvp_first held60 +11.57, non-island tanh s42 held60 +11.56

*Kept:* `evolved/best.json` g37 tanh (325w) repromoted after island bg clobber (best.meta updated; file hash 8fa6c5b8b7a8). Viz `evolved/sweeps/viz_best_g37_R5.png`.

*Repro:* `python3 tools/ga/evolve.py --pop 80 --gens 110 --seed 42 --activation tanh --islands 2` and `python3 tools/ga/eval.py evolved/best.json --matches 60`.

*Ceiling:* to break 12.2 will need `ZBS2` headless ticks (docs/research/04 §Z) — same GA on real bot ticks with vision/physics — the sim is at its fidelity cap.

