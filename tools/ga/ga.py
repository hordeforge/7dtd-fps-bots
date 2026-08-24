"""Genetic operators — no framework, NumPy only.

Matches docs/research/03-genetic-algorithm.md.
Flat float[] genome with W ≈ 325 (14*16 + 16 + 16*5 + 5).
"""

from __future__ import annotations

import hashlib
import json
import math
import os
import random
from pathlib import Path
from typing import List

import numpy as np

# Canonical shape — keep in sync with BotNeuralBrain.cs + docs/research/01 §4
INPUTS = 14
HIDDEN = 16
OUTPUTS = 5
W1_LEN = HIDDEN * INPUTS
B1_LEN = HIDDEN
W2_LEN = OUTPUTS * HIDDEN
B2_LEN = OUTPUTS
W = W1_LEN + B1_LEN + W2_LEN + B2_LEN  # 325

def he_init(rng: np.random.Generator, inputs: int = INPUTS, hidden: int = HIDDEN) -> np.ndarray:
    """He-ish init scaled so the initial policy roughly matches heuristic."""
    w = np.empty(W, dtype=np.float32)
    off = 0
    # W1
    fan_in = inputs
    lim = math.sqrt(6.0 / fan_in)
    w[off: off + W1_LEN] = rng.uniform(-lim, lim, W1_LEN).astype(np.float32); off += W1_LEN
    w[off: off + B1_LEN] = rng.uniform(-0.1, 0.1, B1_LEN).astype(np.float32);  off += B1_LEN
    fan_in = hidden
    lim = math.sqrt(6.0 / fan_in)
    w[off: off + W2_LEN] = rng.uniform(-lim, lim, W2_LEN).astype(np.float32); off += W2_LEN
    w[off: off + B2_LEN] = rng.uniform(-0.1, 0.1, B2_LEN).astype(np.float32)
    return w


def clone_heuristic(rng: np.random.Generator, P: int = 32, sigma: float = 0.02):
    """Behavioral-cloning warm-start stub: generation-0 population.
    Phase 1 replaces this with a fitted clone; for now He init + jitter.
    """
    base = he_init(rng)
    pop = [base]
    for _ in range(P - 1):
        pop.append(base + rng.normal(0, sigma, W).astype(np.float32))
    return pop


def tournament(pop: List[np.ndarray], norm_fitness: List[float], k: int = 3) -> np.ndarray:
    idxs = [random.randrange(len(pop)) for _ in range(k)]
    best = max(idxs, key=lambda i: norm_fitness[i])
    return pop[best]


def crossover(a: np.ndarray, b: np.ndarray, rng: np.random.Generator) -> np.ndarray:
    mask = rng.random(W) < 0.5
    return np.where(mask, a, b).astype(np.float32)


def mutate(w: np.ndarray, rng: np.random.Generator, sigma: float = 0.05, rank_norm: float = 0.5, generation: int = 0, total_gens: int = 80, stagnant: bool = False) -> np.ndarray:
    """Gaussian + sparse-reset + swap per docs/research/03 §2.3. Annealed sigma.
    When `stagnant` the explorer burst fires (heavier tails to escape plateau)."""
    # cosine anneal: strong early, surgical late (helps generalization)
    t = float(min(1.0, generation / max(1, total_gens - 1)))
    anneal = 0.45 + 0.55 * math.cos(t * math.pi * 0.5)  # 1.0 → 0.45
    burst = 1.8 if stagnant else 1.0
    s = sigma * anneal * burst * (0.6 + 0.9 * (1.0 - rank_norm))
    # Gaussian on all weights
    if rng.random() < 0.92:
        w = w + rng.normal(0, s, W).astype(np.float32)
    # Sparse reset 1-3 weights (macro mutation)
    p_reset = 0.22 if stagnant else 0.14
    if rng.random() < p_reset:
        n = rng.integers(1, 4)
        idxs = rng.choice(W, n, replace=False)
        w[idxs] = rng.uniform(-0.7, 0.7, n).astype(np.float32)
    # Block swap (hidden-unit permutation) — helps escape local optima when one unit is dead
    p_swap = 0.12 if stagnant else 0.06
    if rng.random() < p_swap:
        a, b = rng.choice(HIDDEN, 2, replace=False)
        w1a = w[a * INPUTS:(a + 1) * INPUTS].copy()
        w1b = w[b * INPUTS:(b + 1) * INPUTS].copy()
        w[a * INPUTS:(a + 1) * INPUTS] = w1b
        w[b * INPUTS:(b + 1) * INPUTS] = w1a
        b1a = w[W1_LEN + a]; b1b = w[W1_LEN + b]
        w[W1_LEN + a] = b1b; w[W1_LEN + b] = b1a
        for o in range(OUTPUTS):
            row = W1_LEN + B1_LEN + o * HIDDEN
            w[row + a], w[row + b] = w[row + b], w[row + a]
    w = np.clip(w, -8.0, 8.0).astype(np.float32)
    return w


def next_generation(pop_w: List[np.ndarray], ranked, order, rng: np.random.Generator,
                    elite_k: int = 2, pc: float = 0.6,
                    sigma: float = 0.05, rank_norm: float = 0.5,
                    generation: int = 0, total_gens: int = 80,
                    stagnant: bool = False) -> List[np.ndarray]:
    """Elitism-N reproduction shared by evolve/sweep/fitness_sweep: keep the
    top-elite_k genomes, fill the rest with crossover+mutate children (or
    plain tournament copies) per docs/research/03 §3."""
    ranks = ranked.tolist()
    elites = [pop_w[int(i)].copy() for i in order[-elite_k:][::-1]]
    children: List[np.ndarray] = []
    while len(children) < len(pop_w) - elite_k:
        if rng.random() < pc and len(pop_w) - elite_k >= 2:
            a = tournament(pop_w, ranks, k=3)
            b = tournament(pop_w, ranks, k=3)
            child = crossover(a, b, rng)
        else:
            child = tournament(pop_w, ranks, k=3).copy()
        children.append(mutate(child, rng, sigma=sigma, rank_norm=rank_norm,
                               generation=generation, total_gens=total_gens,
                               stagnant=stagnant))
    return elites + children


def island_mix(islands: list[list[np.ndarray]], rng: np.random.Generator, migrants: int = 2) -> None:
    """Ring-migrate `migrants` random genomes between neighboring islands in place."""
    if len(islands) < 2:
        return
    for i in range(len(islands)):
        nxt = (i + 1) % len(islands)
        for _ in range(migrants):
            a = rng.integers(0, len(islands[i]))
            b = rng.integers(0, len(islands[nxt]))
            islands[i][a], islands[nxt][b] = islands[nxt][b].copy(), islands[i][a].copy()


def config_hash(obj: dict) -> str:
    return hashlib.sha256(json.dumps(obj, sort_keys=True).encode()).hexdigest()[:16]


def atomic_write_text(path: Path, text: str) -> None:
    """Replace `path` with `text` via temp file + os.replace so a crash
    mid-write can never tear the file: readers see either the old or the new
    complete content (same contract as the C# mod's AtomicTextFile). Consumers
    of these files (--resume, the promotion gate, eval/report/viz/replay, and
    BotNeuralBrain.TryLoad on the live server) would otherwise read a
    truncated JSON and silently discard the training state it holds."""
    tmp = path.with_name(path.name + ".tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(text)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)


def gen_ckpt_key(path: Path) -> int:
    """Numeric generation for gen_NNN.json checkpoints (-1 if unparsable).
    Order checkpoints with this key: the %03d filename padding stops sorting
    correctly at gen 1000 ('gen_1000' < 'gen_101' lexicographically)."""
    tail = path.stem.rsplit("_", 1)[-1]
    return int(tail) if tail.isdigit() else -1


def save_best(path: Path, w: np.ndarray, generation: int, fitness: float, config: dict):
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "version": 1,
        "inputs": INPUTS, "hidden": HIDDEN, "outputs": OUTPUTS,
        "weights": w.astype(float).tolist(),
        "configHash": config_hash(config),
        "fitness": float(fitness),
        "generation": generation,
    }
    # Atomic: best.json is the shipped champion. A torn write would make the
    # next evolve's promotion gate score the current champion -inf (a weaker
    # candidate could then clobber it) and break TryLoad on the server.
    atomic_write_text(path, json.dumps(payload, indent=2))
    atomic_write_text(path.parent / "best.meta.json", json.dumps({
        "generation": generation, "fitness": float(fitness),
        "configHash": config_hash(config),
    }, indent=2))
