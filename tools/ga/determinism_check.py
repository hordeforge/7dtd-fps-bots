#!/usr/bin/env python3
"""Replay check for the training stack: same seed, same bytes.

Every layer between `--seed` and a fitness number has to be a pure function of
that seed, or a run cannot be replayed from the seed alone. This drives the
real entry points (ga operators, the numba match kernel, the threaded harness)
twice and diffs the results, and drives a near-miss (a different seed) to prove
the equality is not a frozen stream comparing equal to itself.

    python tools/ga/determinism_check.py

Exits non-zero on the first divergence and names the layer that leaked.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))

import combat_sim
import ga
import harness

SEED = 4242
GENS = 4
POP = 12


def _evolution(seed: int) -> list[bytes]:
    """POP genomes evolved GENS generations; returns each generation's bytes."""
    rng = np.random.default_rng(seed)
    pop = ga.init_population(rng, P=POP, sigma=0.02)
    frames = []
    for g in range(GENS):
        fitness = [float(np.sum(w.astype(np.float64))) for w in pop]
        order = np.argsort(fitness)
        ranked = np.empty(POP, dtype=float)
        ranked[order] = np.arange(POP) / max(1, POP - 1)
        frames.append(b"".join(w.tobytes() for w in pop))
        pop = ga.next_generation(pop, ranked, order, rng, generation=g, total_gens=GENS)
    return frames


def check_evolution() -> None:
    a, b = _evolution(SEED), _evolution(SEED)
    for g, (x, y) in enumerate(zip(a, b)):
        if x != y:
            _fail(f"ga operators, generation {g}: same seed diverged "
                  f"({len(x)} vs {len(y)} bytes)")
    other = _evolution(SEED + 1)
    if other == a:
        _fail("ga operators: seed 4243 produced the seed 4242 population; "
              "the stream is not reaching selection/mutation")
    print(f"  ok  ga operators: {GENS} generations byte-identical on seed {SEED}")


def check_rng_checkpoint() -> None:
    """A checkpoint must restore the draw sequence, not just the population."""
    rng = np.random.default_rng(SEED)
    rng.normal(size=8)
    restored = np.random.default_rng(SEED)
    restored.bit_generator.state = json.loads(json.dumps(ga.rng_state(rng)))
    want = rng.normal(size=8)
    got = restored.normal(size=8)
    if not np.array_equal(want, got):
        _fail("rng_state: the checkpoint did not restore the draw sequence")
    print("  ok  rng_state: checkpoint round-trip continues the same stream")


def check_match_kernel() -> None:
    w = ga.he_init(np.random.default_rng(SEED))
    a = combat_sim.simulate_match(w, 0xDEADBEEF, 4, 2, 600, 3)
    b = combat_sim.simulate_match(w, 0xDEADBEEF, 4, 2, 600, 3)
    if a != b:
        _fail("combat_sim.simulate_match: same (weights, seed) gave a different match")
    c = combat_sim.simulate_match(w, 0xDEADBEEF + 1, 4, 2, 600, 3)
    if c == a:
        _fail("combat_sim.simulate_match: a different seed replayed the same match")
    print("  ok  combat_sim: same seed replays bit-for-bit, a new seed does not")


def check_threaded_harness() -> None:
    """evaluate_many fans matches across cores; the scores must not care."""
    w = ga.he_init(np.random.default_rng(SEED))
    threaded = harness.evaluate_many(w, 7, SEED, 6)
    sequential = [harness.evaluate(w, 7, m, SEED) for m in range(6)]
    if threaded != sequential:
        _fail("harness.evaluate_many: threaded scores differ from the sequential ones")
    if harness.evaluate_many(w, 7, SEED, 6) != threaded:
        _fail("harness.evaluate_many: not reproducible across calls")
    print("  ok  harness: threaded scores match the sequential ones")


def check_canonical_stick() -> None:
    """canonical_scores pins the measuring stick, scores on it, and puts the
    caller's training knobs back: a second call on the same genome must repeat,
    and a probe must not leave DRAWS_PER_CONFIG pinned for the training loop."""
    w = ga.he_init(np.random.default_rng(SEED))
    before = harness.DRAWS_PER_CONFIG
    first = harness.canonical_scores(w, 7, SEED, 4)
    if harness.DRAWS_PER_CONFIG != before:
        _fail("harness.canonical_scores: left DRAWS_PER_CONFIG pinned after the probe")
    if harness.canonical_scores(w, 7, SEED, 4) != first:
        _fail("harness.canonical_scores: not reproducible across calls")
    print("  ok  harness: the canonical stick is reproducible and restores its knobs")


def _fail(msg: str) -> None:
    print(f"FAIL  {msg}")
    raise SystemExit(1)


if __name__ == "__main__":
    for step in (check_evolution, check_rng_checkpoint, check_match_kernel,
                 check_threaded_harness, check_canonical_stick):
        step()
    print("determinism: every layer replays from the seed")
