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
import threading
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
        ranked, order = ga.rank_fitness(fitness)
        frames.append(b"".join(w.tobytes() for w in pop))
        pop = ga.next_generation(pop, ranked, order, rng, generation=g, total_gens=GENS)
    return frames


def check_evolution() -> None:
    a, b = _evolution(SEED), _evolution(SEED)
    # strict: both walks run the same fixed GENS, so a length mismatch is a
    # real divergence, not a shorter run to zip past silently.
    for g, (x, y) in enumerate(zip(a, b, strict=True)):
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


def check_loadout_draw() -> None:
    """A mixed loadout must draw independently per bot, on every spawn branch.

    The spawn loop's weapon roll used to re-read the LCG state without advancing
    it. On the pinned-position branch (spawn_gap > 0) no position draw happens,
    so the state never moved and every bot in the match drew the SAME weapon:
    a "mixed" arena was really a six-way identical-loadout match, silently. A
    determinism probe cannot see that (the match still replayed exactly), so it
    needs its own check on the draw itself, not on the match bytes.
    """
    # Replay the spawn loop's roll for both branches, from the same seed the
    # sim uses, and require the picks to spread over the weapon table. The
    # stream is threaded across bots exactly as the kernel threads it: a fresh
    # seed per bot would pass this check even if the roll never advanced.
    for gap in (0.0, 50.0):
        picks = []
        rng = SEED & 0xFFFFFFFF
        for _ in range(12):
            if gap <= 0.0:
                # The kernel's own draws, so this check fails if the roll
                # regresses rather than re-asserting a correct copy of it here.
                _, rng = combat_sim._lcg01(rng)  # noqa: SLF001 -- the kernel's own stream
                _, rng = combat_sim._lcg01(rng)  # noqa: SLF001 -- the kernel's own stream
            pick, rng = combat_sim._loadout_pick(rng, 6)  # noqa: SLF001 -- the kernel's own draw
            picks.append(pick)
        if len(set(picks)) < 2:
            _fail(f"combat_sim spawn loop: spawn_gap={gap} drew one weapon "
                  f"({picks[0]}) for every bot; the loadout roll is not "
                  f"advancing the LCG stream")
    print("  ok  combat_sim: mixed loadout draws independently on both spawn branches")


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


def check_concurrent_canonical_stick() -> None:
    """canonical_scores pins the stick for the whole threaded evaluation, so
    two probes at once must not steal each other's pin: each has to return the
    canonical score, and the training knobs outside must survive intact."""
    w = ga.he_init(np.random.default_rng(SEED))
    want = harness.canonical_scores(w, 7, SEED, 4)
    # evolve sets the training knobs this way before it starts a generation;
    # reproduce that here so a probe has a live non-canonical stick to stomp.
    training = {"ACTIVATION": 1, "FIT_ELO": 0.9, "FIT_ECON": 0.3, "FIT_SURV": 0.2,
                "FIT_STUCK": 0.4, "CURRICULUM": "horde_first", "DRAWS_PER_CONFIG": 3}
    for name, value in training.items():
        setattr(harness, name, value)
    results: list[list[float]] = []
    errors: list[BaseException] = []
    barrier = threading.Barrier(4)

    def probe() -> None:
        try:
            barrier.wait(timeout=30)  # every probe starts inside the same window
            results.append(harness.canonical_scores(w, 7, SEED, 4))
        except BaseException as ex:  # reported by the driver, not swallowed
            errors.append(ex)

    threads = [threading.Thread(target=probe) for _ in range(4)]
    for t in threads:
        t.start()
    for t in threads:
        t.join(timeout=120)
    if errors:
        _fail(f"harness.canonical_scores: a concurrent probe raised {errors[0]!r}")
    if any(t.is_alive() for t in threads):
        _fail("harness.canonical_scores: a concurrent probe did not finish (deadlock)")
    if any(r != want for r in results):
        _fail("harness.canonical_scores: a concurrent probe scored on another "
              "caller's measuring stick")
    after = {name: getattr(harness, name) for name in training}
    if after != training:
        _fail(f"harness.canonical_scores: concurrent probes lost the training "
              f"knobs ({after} != {training})")
    print("  ok  harness: concurrent canonical probes each kept the canonical stick")


def _fail(msg: str) -> None:
    # stderr: the passing checks are the report on stdout, a failure is status.
    print(f"FAIL  {msg}", file=sys.stderr)
    raise SystemExit(1)


if __name__ == "__main__":
    if set(sys.argv[1:]) & {"-h", "--help"}:
        print(__doc__.strip())
        raise SystemExit(0)
    if sys.argv[1:]:
        print(f"error: unexpected argument {sys.argv[1]!r}; this check takes none "
              f"(see --help)", file=sys.stderr)
        raise SystemExit(2)
    for step in (check_evolution, check_rng_checkpoint, check_match_kernel,
                 check_loadout_draw, check_threaded_harness,
                 check_canonical_stick, check_concurrent_canonical_stick):
        step()
    print("determinism: every layer replays from the seed")
