"""Headless harness: now backed by combat_sim (real PvP+zombie loop).

Still deterministic (LCG seed chain), still cheap, but now every tick runs
LOS, move caps, burst fire, hit chance, and zombie pressure, so evolved
weights actually matter for combat, not just logits.

Matches docs/research/02-environment-and-fitness.md.
"""

from __future__ import annotations

import hashlib
import os
import threading
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from typing import List

import numpy as np

import combat_sim as _cs
_simulate = _cs.simulate_match
_simulate_relu = _cs.simulate_match_relu
# harness activation flag (0 tanh, 1 relu): set by sweep/evolve
ACTIVATION = 0
# curriculum flag ("mixed" canonical; pvp_first/horde_first gate early gens via evolve.py)
CURRICULUM = "mixed"
# scalarization weights (tunable: fitness sweep + evolve.py --fit-* thread via these)
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

# Canonical held-out measuring stick (docs/research/03/04): tanh forward,
# default scalarization, mixed curriculum, DRAWS_PER_CONFIG=1 (F=18).
# Snapshot at import so training-time mutations of the module knobs above
# cannot leak into held-out scoring; canonical_scores() pins these values for
# the duration of its evaluation and restores the caller's knobs afterwards.
CANONICAL_STICK = (0, FIT_ELO, FIT_ECON, FIT_SURV, FIT_STUCK, "mixed", 1)

# The seven knobs above are process globals, and evaluate() reads them from
# the pool threads _pmap spawns. Anything that swaps them has to hold this
# lock for the whole window: two overlapping pins otherwise interleave their
# save/restore and a worker can end up scoring one arena with another
# caller's curriculum and draws-per-config, or with a half-restored scalarization.
# It is a leaf lock (held across ThreadPoolExecutor work, never acquired from
# inside a worker), so it cannot deadlock against the pool.
_STICK_LOCK = threading.Lock()

KNOB_NAMES = ("ACTIVATION", "FIT_ELO", "FIT_ECON", "FIT_SURV", "FIT_STUCK",
              "CURRICULUM", "DRAWS_PER_CONFIG")


@contextmanager
def _stick_window(overrides: dict):
    """The one place a knob is read, written or restored. The snapshot is
    taken under _STICK_LOCK and the restore is the same one, so every caller
    goes through this window and a caller never acts on globals it read
    outside the lock."""
    globals_ = globals()
    _STICK_LOCK.acquire()
    try:
        saved = tuple(globals_[name] for name in KNOB_NAMES)
        for name, value in overrides.items():
            globals_[name] = value
        yield
    finally:
        try:
            for name, value in zip(KNOB_NAMES, saved, strict=True):
                globals_[name] = value
        finally:
            _STICK_LOCK.release()


@contextmanager
def pinned_stick(values: tuple):
    """Install the module knobs named in `values` (parallel to KNOB_NAMES)
    for the duration of the block, then put back whatever was there.

    The one way to change a knob from a caller: evaluate() fans out across
    threads, so a bare assign is only safe while no other caller is in flight,
    and a bare restore is a check-then-act that loses a concurrent caller's
    pin. The window is exclusive instead."""
    # dict() before the window: a length or name mismatch raises with no
    # knob touched.
    with _stick_window(dict(zip(KNOB_NAMES, values, strict=True))):
        yield


@contextmanager
def pinned_knobs(**overrides):
    """pinned_stick for a partial override: the named knobs take the given
    values, the rest keep whatever the caller had set.

    The partial window is the same exclusive one: the knobs left alone are
    snapshotted under the lock, not read before it, so a caller that entered
    while another had the canonical stick pinned restores that caller's
    training knobs on the way out instead of stranding the canonical stick as
    the new baseline."""
    for name in overrides:
        if name not in KNOB_NAMES:
            raise KeyError(f"unknown harness knob {name!r}; known: {', '.join(KNOB_NAMES)}")
    with _stick_window(overrides):
        yield


def canonical_scores(w: np.ndarray, gen_key: int, run_seed: int, matches: int) -> List[float]:
    """Score `w` on the shared held-out measuring stick: canonical tanh +
    default scalarization + DRAWS_PER_CONFIG=1 (F=18), `matches` draws keyed
    by (gen_key, m, run_seed). One definition for every consumer of held-out
    numbers (evolve's promotion gate and its eval / static-vs-neural
    subcommands), so they cannot drift apart again; training knobs set by
    a caller are pinned for the duration (pinned_stick, so a probe running
    beside another caller cannot have its stick swapped mid-flight) and
    restored afterwards."""
    with pinned_stick(CANONICAL_STICK):
        return evaluate_many(w, gen_key, run_seed, matches)


# Thread cap for evaluate_population/evaluate_many: one worker per genome (or
# per match) up to the core count (each sim is CPU-bound; oversubscribing only
# adds context switches).
_MAX_WORKERS = max(1, os.cpu_count() or 1)
# fixed opponent policy for mixed arenas: all-zero weights = always-firing no-brain
_OPP_STATIC = np.zeros(325, dtype=np.float32)
# duel arena pins (R11 rework): 50-unit spawn gap, open env, equal AKs so the
# fight develops and the elo term rewards winning duels
DUEL_SPAWN_GAP = 50.0
DUEL_ENV = 2
DUEL_WEAPON = 2

# Arena shapes, weighted per curriculum in evaluate().
_DUEL = (2, 1, 0, 1200)
_SKIRMISH = (2, 2, 0, 1200)
_FFA = (6, 6, 0, 1800)
_HORDE_SHORT = (4, 4, 6, 1200)
_HORDE_LONG = (4, 4, 6, 1800)


def _pmap(fn, n: int):
    """Run fn(0..n-1) across cores, sequential below two items. ex.map collects
    in index order, so the result is byte-identical to a sequential loop."""
    workers = min(_MAX_WORKERS, n)
    if workers <= 1:
        return [fn(i) for i in range(n)]
    with ThreadPoolExecutor(max_workers=workers) as ex:
        return list(ex.map(fn, range(n)))


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
    # Read the process knobs once, into locals. This body runs on a pool
    # thread while the caller thread owns pinned_stick, so re-reading the
    # globals inside the loop could score a genome's 36 sims under two
    # different measuring sticks (a swap lands between arenas) and the arena
    # mix would no longer match the config that was asked for.
    activation, fit_elo, fit_econ, fit_surv = ACTIVATION, FIT_ELO, FIT_ECON, FIT_SURV
    fit_stuck, fit_camp, curriculum, draws = FIT_STUCK, FIT_CAMP, CURRICULUM, DRAWS_PER_CONFIG

    total = 0.0
    n = 0
    # Arena configs: (n_bots, n_evolved, n_zombies, max_ticks). When n_evolved <
    # n_bots the remaining bots fight with the fixed opponent policy (_OPP_STATIC,
    # an always-fire no-brain), so duels/FFA actually differentiate combat skill
    # instead of pitting the policy against itself (kills == deaths, elo pinned).
    # A curriculum is a weighting of these shapes: one entry per occurrence, so
    # a shape that outweighs another is a longer list, not a different tuple.
    if curriculum == "pvp_first":
        configs = [_DUEL] * 5 + [_SKIRMISH, _FFA, _FFA, _HORDE_SHORT]
    elif curriculum == "horde_first":
        configs = [_DUEL, _SKIRMISH, _FFA, _FFA, _HORDE_LONG, _HORDE_LONG, _HORDE_LONG, _HORDE_LONG, _FFA]
    else:
        configs = [_DUEL] * 3 + [_SKIRMISH, _FFA, _FFA, _FFA, _HORDE_LONG, _HORDE_LONG]
    # dual-seed regularizer: training fitness = mean over two seed streams
    fn = _simulate_relu if activation == 1 else _simulate
    seeds = (run_seed, run_seed ^ 0x9E3779B9)
    for rs in seeds:
        for m, (n_bots, n_evolved, n_zombies, max_ticks) in enumerate(configs):
            for rep in range(draws):
                seed = _seed_for(generation, genome_idx, m * draws + rep, rs)
                skill = _skill_for_match(m)
                if n_evolved < n_bots:
                    # fixed-opponent duel arena (R11 rework): spawn gap + open env + equal AKs
                    r = fn(w, seed, n_bots, n_zombies, max_ticks, skill,
                           _OPP_STATIC, n_evolved, DUEL_SPAWN_GAP, DUEL_ENV, DUEL_WEAPON)
                else:
                    r = fn(w, seed, n_bots, n_zombies, max_ticks, skill)
                fitness = (fit_elo * r[0] + fit_econ * r[1] + fit_surv * r[2]
                           - fit_stuck * r[3] - fit_camp * r[4])
                total += fitness; n += 1
    return total / max(1, n)


def evaluate_many(w: np.ndarray, gen_key: int, run_seed: int, matches: int) -> List[float]:
    """Score one genome over `matches` draws (match index 0..matches-1), in
    match order. Same kernel and seed chain as a sequential
    `[evaluate(w, gen_key, m, run_seed)]` loop; threads only overlap the
    independent sims (the numba body releases the GIL) and ex.map collects in
    index order, so scores stay byte-identical to the sequential version.
    This is the single definition behind canonical_scores and evolve's per-gen
    held probe: both used to run these sims strictly sequentially on the
    training loop's critical path."""
    return _pmap(lambda m: evaluate(w, gen_key, m, run_seed), matches)


def evaluate_population(pop: List[np.ndarray], generation: int, run_seed: int = 42) -> List[float]:
    """Evaluate genomes across OS threads. combat_sim._simulate releases the
    GIL (nogil), so threads give true multi-core parallelism on the dominant
    training cost (pop x arena sims). Scores stay byte-identical to the
    sequential order: each genome's fitness depends only on its own
    deterministic seed chain (evaluate(generation, idx, run_seed)), nothing
    is shared between sims, and ex.map collects results in index order.
    """
    return _pmap(lambda i: evaluate(pop[i], generation, i, run_seed), len(pop))
