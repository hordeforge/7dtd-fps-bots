"""Headless harness — now backed by combat_sim (real PvP+zombie loop).

Still deterministic (LCG seed chain), still cheap, but now every tick runs
LOS, move caps, burst fire, hit chance, and zombie pressure — so evolved
weights actually matter for combat, not just logits.

Matches docs/research/02-environment-and-fitness.md.
"""

from __future__ import annotations

import hashlib
from typing import List

import numpy as np

import combat_sim as _cs
import ga
_simulate = _cs.simulate_match
_simulate_relu = _cs.simulate_match_relu
# harness activation flag (0 tanh, 1 relu) — set by sweep/evolve
ACTIVATION = 0
# curriculum flag ("mixed" canonical; pvp_first/horde_first gate early gens via evolve.py)
CURRICULUM = "mixed"
# scalarization weights (tunable — fitness sweep + evolve.py --fit-* thread via these)
FIT_ELO = 0.55
FIT_ECON = 0.25
FIT_SURV = 0.15
FIT_STUCK = 0.05
FIT_CAMP = 1.0  # multiplier on camp_pen (already 1.6/0; 0 disables)
# draw regularization: each arena config sampled twice per seed-stream (F=36).
# One draw per config let genomes overfit the exact arena-draw chain; two draws
# make train fitness track held-out performance. The eval gate pins this back
# to 1 (F=18) so the measuring stick is stable.
DRAWS_PER_CONFIG = 2

# weapon sampling pool (BotConfig.LoadoutPool indices into combat_sim WEAPON_*)
# 0 pistol, 2 AK, 3 sniper, 5 SMG — keep it mixed per match
_POOL = [0, 2, 3, 5, 0, 2]
# fixed opponent policy for mixed arenas: all-zero weights = always-firing no-brain
_OPP_STATIC = np.zeros(325, dtype=np.float32)
# duel arena pins (R11 rework): 50-unit spawn gap, open env, equal AKs so the
# fight develops and the elo term rewards winning duels
DUEL_SPAWN_GAP = 50.0
DUEL_ENV = 2
DUEL_WEAPON = 2


def _seed_for(generation: int, genome_idx: int, match_idx: int, run_seed: int = 42) -> int:
    h = hashlib.sha256(f"{run_seed}:{generation}:{genome_idx}:{match_idx}".encode()).digest()
    return int.from_bytes(h[:4], "little")


def _skill_for_match(m: int) -> int:
    # cycle skill 1..4 across matches so fitness isn't just "best vs weak"
    return [1, 2, 4, 3, 2, 1, 4, 2, 3][m % 9]


def evaluate(w: np.ndarray, generation: int, genome_idx: int, run_seed: int = 42) -> float:
    """One genome over the deterministic arena set: 9 configs x two seed
    streams x DRAWS_PER_CONFIG draws each (36 sims at the defaults).
    Returns scalarized fitness (higher is better).

    R1: dual-seed averaging (run_seed and run_seed ^ GOLDEN) to punish
    single-seed overfit.
    Curriculum gates the mix: pvp_first emphasizes duels early, horde_first
    emphasizes horde (set by evolve.py per gen, default mixed).
    """
    total = 0.0
    n = 0
    # Arena configs: (n_bots, n_evolved, n_zombies, max_ticks). When n_evolved <
    # n_bots the remaining bots fight with the fixed opponent policy (_OPP_STATIC,
    # an always-fire no-brain), so duels/FFA actually differentiate combat skill
    # instead of pitting the policy against itself (kills == deaths, elo pinned).
    if CURRICULUM == "pvp_first":
        configs = [
            (2, 1, 0, 1200), (2, 1, 0, 1200), (2, 1, 0, 1200), (2, 1, 0, 1200),
            (2, 1, 0, 1200), (2, 2, 0, 1200), (6, 6, 0, 1800), (6, 6, 0, 1800),
            (4, 4, 6, 1200),
        ]
    elif CURRICULUM == "horde_first":
        configs = [
            (2, 1, 0, 1200), (2, 2, 0, 1200), (6, 6, 0, 1800), (6, 6, 0, 1800),
            (4, 4, 6, 1800), (4, 4, 6, 1800), (4, 4, 6, 1800), (4, 4, 6, 1800),
            (6, 6, 0, 1800),
        ]
    else:
        configs = [
            (2, 1, 0, 1200), (2, 1, 0, 1200), (2, 1, 0, 1200),
            (2, 2, 0, 1200), (6, 6, 0, 1800), (6, 6, 0, 1800), (6, 6, 0, 1800),
            (4, 4, 6, 1800), (4, 4, 6, 1800),
        ]
    # dual-seed regularizer: training fitness = mean over two seed streams
    fn = _simulate_relu if ACTIVATION == 1 else _simulate
    seeds = (run_seed, run_seed ^ 0x9E3779B9)
    for rs in seeds:
        for m, (n_bots, n_evolved, n_zombies, max_ticks) in enumerate(configs):
            for rep in range(DRAWS_PER_CONFIG):
                seed = _seed_for(generation, genome_idx, m * DRAWS_PER_CONFIG + rep, rs)
                weapon = _POOL[m % len(_POOL)]
                skill = _skill_for_match(m)
                if n_evolved < n_bots:
                    # fixed-opponent duel arena (R11 rework): spawn gap + open env + equal AKs
                    r = fn(w, seed, n_bots, n_zombies, max_ticks, skill, weapon,
                           _OPP_STATIC, n_evolved, DUEL_SPAWN_GAP, DUEL_ENV, DUEL_WEAPON)
                else:
                    r = fn(w, seed, n_bots, n_zombies, max_ticks, skill, weapon)
                fitness = FIT_ELO * r[0] + FIT_ECON * r[1] + FIT_SURV * r[2] - FIT_STUCK * r[3] - FIT_CAMP * r[4]
                total += fitness; n += 1
    return total / max(1, n)


def evaluate_population(pop: List[np.ndarray], generation: int, run_seed: int = 42) -> List[float]:
    return [evaluate(w, generation, i, run_seed) for i, w in enumerate(pop)]
