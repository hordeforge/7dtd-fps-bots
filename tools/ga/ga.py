"""Genetic operators — no framework, NumPy only.

Matches docs/research/03-genetic-algorithm.md.
Flat float[] genome with W ≈ 325 (14*16 + 16 + 16*5 + 5).
"""

from __future__ import annotations

import hashlib
import json
import math
import random
from pathlib import Path
from typing import List, Optional, Tuple

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

# Deterministic LCG matching clanker/zdtd_bot (state -> state*1103515245+12345)
def _lcg(state: int) -> int:
    return (state * 1103515245 + 12345) & 0xFFFFFFFF


def _lcg01(state: int) -> Tuple[float, int]:
    state = _lcg(state)
    v = ((state >> 8) & 0x00FFFFFF) / 16777216.0
    return v, state


def flat_to_layers(w: np.ndarray):
    off = 0
    w1 = w[off: off + W1_LEN].reshape(HIDDEN, INPUTS); off += W1_LEN
    b1 = w[off: off + B1_LEN];                        off += B1_LEN
    w2 = w[off: off + W2_LEN].reshape(OUTPUTS, HIDDEN); off += W2_LEN
    b2 = w[off: off + B2_LEN]
    return w1, b1, w2, b2


def forward(w: np.ndarray, x: np.ndarray) -> np.ndarray:
    w1, b1, w2, b2 = flat_to_layers(w)
    h = np.tanh(w1 @ x + b1)
    y = w2 @ h + b2
    return y  # caller applies sigmoid/tanh per head


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


def mutate(w: np.ndarray, rng: np.random.Generator, sigma: float = 0.05, rank_norm: float = 0.5, generation: int = 0, total_gens: int = 80) -> np.ndarray:
    """Gaussian + sparse-reset + swap per docs/research/03 §2.3. Annealed sigma."""
    # cosine anneal: strong early, surgical late (helps generalization)
    t = float(min(1.0, generation / max(1, total_gens - 1)))
    anneal = 0.45 + 0.55 * math.cos(t * math.pi * 0.5)  # 1.0 → 0.45
    s = sigma * anneal * (0.6 + 0.9 * (1.0 - rank_norm))
    # Gaussian on all weights
    if rng.random() < 0.92:
        w = w + rng.normal(0, s, W).astype(np.float32)
    # Sparse reset 1-3 weights (macro mutation)
    if rng.random() < 0.14:
        n = rng.integers(1, 4)
        idxs = rng.choice(W, n, replace=False)
        w[idxs] = rng.uniform(-0.7, 0.7, n).astype(np.float32)
    # Block swap (hidden-unit permutation) — helps escape local optima when one unit is dead
    if rng.random() < 0.06:
        a, b = rng.choice(HIDDEN, 2, replace=False)
        # Swap hidden unit a/b in W1+b1 and the corresponding W2 column
        w1a = w[a * INPUTS:(a + 1) * INPUTS].copy()
        w1b = w[b * INPUTS:(b + 1) * INPUTS].copy()
        w[a * INPUTS:(a + 1) * INPUTS] = w1b
        w[b * INPUTS:(b + 1) * INPUTS] = w1a
        b1a = w[W1_LEN + a]; b1b = w[W1_LEN + b]
        w[W1_LEN + a] = b1b; w[W1_LEN + b] = b1a
        # W2 columns (outputs × hidden, row-major)
        for o in range(OUTPUTS):
            row = W1_LEN + B1_LEN + o * HIDDEN
            w[row + a], w[row + b] = w[row + b], w[row + a]
    w = np.clip(w, -8.0, 8.0).astype(np.float32)
    return w


def config_hash(obj: dict) -> str:
    return hashlib.sha256(json.dumps(obj, sort_keys=True).encode()).hexdigest()[:16]


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
    path.write_text(json.dumps(payload, indent=2))
    (path.parent / "best.meta.json").write_text(json.dumps({
        "generation": generation, "fitness": float(fitness),
        "configHash": config_hash(config),
    }, indent=2))
