"""Headless harness bridge — stub for R0.

The real harness spawns the zdtd headless binary and exchanges ZBS2 sense
bytes + `bot <verb>` commands. For now this module exposes the *protocol*
and a synthetic deterministic fitness stub so `evolve.py` can be dry-run
without a game binary. When the headless binary is ready, replace
`evaluate()`'s body and keep the rest of the pipeline unchanged.

Matches docs/research/02-environment-and-fitness.md and 04-training-pipeline.md.
"""

from __future__ import annotations

import hashlib
import random
from typing import List

import numpy as np

from ga import W, forward

# Synthetic fitness stub: deterministic, cheap, and sensitive to the weights
# so the GA has a non-trivial landscape even without the sim.
# Replace with real sim ticks when the harness is live; keep the same seed
# chain (generation, genomeIdx, matchIdx) so results stay reproducible.


def _lcg(seed: int) -> int:
    return (seed * 1103515245 + 12345) & 0xFFFFFFFF


def _seed_for(generation: int, genome_idx: int, match_idx: int, run_seed: int = 42) -> int:
    h = hashlib.sha256(f"{run_seed}:{generation}:{genome_idx}:{match_idx}".encode()).digest()
    return int.from_bytes(h[:4], "little")


def synthetic_fitness(w: np.ndarray, seed: int) -> float:
    """Toy: sum of projected outputs on a handful of canonical observations.
    Not game truth — just enough to make evolve.py testable end-to-end.
    """
    rng = np.random.default_rng(seed & 0xFFFFFFFF)
    total = 0.0
    for _ in range(5):
        x = rng.uniform(-1, 1, 14).astype(np.float32)
        y = forward(w, x)
        # Reward moderate camp/retreat (not always) + fire when not retreating
        camp = 1.0 / (1.0 + np.exp(-float(np.clip(y[0], -8, 8))))
        retreat = 1.0 / (1.0 + np.exp(-float(np.clip(y[1], -8, 8))))
        fire = 1.0 / (1.0 + np.exp(-float(np.clip(y[3], -8, 8))))
        total += 0.4 * (0.5 - abs(camp - 0.5))  # prefer 0.5 over extremes
        total += 0.4 * (0.5 - abs(retreat - 0.5))
        total += 0.2 * fire
    # Weight norm penalty so bloated weights lose
    total -= 0.01 * float(np.mean(np.abs(w)))
    return float(total)


def evaluate(w: np.ndarray, generation: int, genome_idx: int, run_seed: int = 42, matches: int = 9) -> float:
    """Evaluate one genome across `matches` deterministic draws; returns mean.
    Swap this body for the real headless sim; keep the seed chain.
    """
    scores: List[float] = []
    for m in range(matches):
        seed = _seed_for(generation, genome_idx, m, run_seed)
        scores.append(synthetic_fitness(w, seed))
    return float(np.mean(scores))


def evaluate_population(pop: List[np.ndarray], generation: int, run_seed: int = 42) -> List[float]:
    return [evaluate(w, generation, i, run_seed) for i, w in enumerate(pop)]
