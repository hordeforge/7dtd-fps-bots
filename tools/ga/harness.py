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

from combat_sim import simulate_match as _simulate
import ga

# weapon sampling pool (BotConfig.LoadoutPool indices into combat_sim WEAPON_*)
# 0 pistol, 2 AK, 3 sniper, 5 SMG — keep it mixed per match
_POOL = [0, 2, 3, 5, 0, 2]


def _seed_for(generation: int, genome_idx: int, match_idx: int, run_seed: int = 42) -> int:
    h = hashlib.sha256(f"{run_seed}:{generation}:{genome_idx}:{match_idx}".encode()).digest()
    return int.from_bytes(h[:4], "little")


def _skill_for_match(m: int) -> int:
    # cycle skill 1..4 across matches so fitness isn't just "best vs weak"
    return [1, 2, 4, 3, 2, 1, 4, 2, 3][m % 9]


def evaluate(w: np.ndarray, generation: int, genome_idx: int, run_seed: int = 42) -> float:
    """One genome, 9 deterministic matches across 3 arenas.
    Returns scalarized fitness (higher is better).
    """
    total = 0.0
    # Arena mix per docs/research/02 §2: 1v1 duels 40% (4/9), FFA 40% (4/9), Horde 20% (1-2/9)
    # We encode as 9 matches:
    #  0-3: 1v1 duel  (1 bot vs 1 bot fixed heuristic — we simulate as 2-bot FFA, scoring the genome's side)
    #  4-7: FFA DM    (6 bots: 1 evolved slot vs 5 heur clones, all share w for now — intro diversity via jitter)
    #  8  : Horde    (4 bots vs 6 zombies)
    configs = [
        # (n_bots, n_zombies, max_ticks)
        (2, 0, 1200), (2, 0, 1200), (2, 0, 1200), (2, 0, 1200),
        (6, 0, 1800), (6, 0, 1800), (6, 0, 1800), (6, 0, 1800),
        (4, 6, 1800),
    ]
    for m, (n_bots, n_zombies, max_ticks) in enumerate(configs):
        seed = _seed_for(generation, genome_idx, m, run_seed)
        weapon = _POOL[m % len(_POOL)]
        skill = _skill_for_match(m)
        fitness, *_ = _simulate(w, seed, n_bots, n_zombies, max_ticks, skill, weapon)
        total += fitness
    return total / len(configs)


def evaluate_population(pop: List[np.ndarray], generation: int, run_seed: int = 42) -> List[float]:
    return [evaluate(w, generation, i, run_seed) for i, w in enumerate(pop)]
