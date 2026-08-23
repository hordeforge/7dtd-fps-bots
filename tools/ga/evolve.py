#!/usr/bin/env python3
"""Offline neuroevolution loop — phase R0/R1.

Usage:
  python tools/ga/evolve.py --pop 32 --gens 40 --seed 42
  python tools/ga/evolve.py --resume evolved/runs/2026-08-18_foo
  python tools/ga/evolve.py --dry-run --pop 8 --gens 3

Outputs evolved/runs/<ts>/ + evolved/best.json (see docs/research/04).
"""

from __future__ import annotations

import argparse
import csv
import json
import os
import random
import sys
import time
from pathlib import Path

import numpy as np

import ga
import harness

DEFAULT_FITNESS = {"elo": 0.55, "econ": 0.25, "survival": 0.15, "stuck": 0.05}
# Held-out probe: seed 999 never feeds training fitness; it is the promotion
# measuring stick (see docs/research/04).
HELD_SEED = 999
HELD_MATCHES = 40


def _load_resume(resume: str, seed: int, pop: int):
    """Prime (start_gen, population, best_weights, best_fitness) from the newest
    gen_*.json checkpoint in `resume` (or the single file itself). Returns a
    fresh start (gen 0, None, None, -inf) when nothing usable is there."""

    def _fresh(reason: str):
        print(reason)
        return 0, None, None, float("-inf")

    resume_path = Path(resume)
    ckpts = sorted(resume_path.glob("gen_*.json"), key=ga.gen_ckpt_key) if resume_path.is_dir() else [resume_path]
    try:
        if not ckpts or not ckpts[-1].is_file():
            return _fresh(f"resume: no checkpoints in {resume}, starting fresh")
        last = ckpts[-1]
        ckpt = json.loads(last.read_text())
        top3_raw = ckpt.get("top3") or []
        if not top3_raw:
            return _fresh(f"resume: {last} has no top3, starting fresh")
        top3 = [np.array(w, dtype=float) for w in top3_raw]
    except Exception as ex:
        return _fresh(f"resume failed ({ex}), starting fresh")

    # Rebuild the population from the checkpoint's top-3; copies past the third
    # get jitter for diversity (seeded, so resumed runs stay reproducible).
    rng2 = np.random.default_rng(seed ^ 0x9E3779B9)
    pop_w = []
    for i in range(pop):
        base = top3[i % len(top3)]
        if i < len(top3):
            pop_w.append(base.copy())
        else:
            pop_w.append(base + rng2.normal(0, 0.03, base.size).astype(float))
    best_f = float(ckpt.get("best_fitness", float("-inf")))
    start_gen = int(ckpt.get("gen", -1)) + 1
    print(f"resume: loaded {last} gen {ckpt.get('gen')} fit {best_f:.3f}, seeded next gen")
    return start_gen, pop_w, top3[0].copy(), best_f


def _next_generation(pop_w, ranked, order, rng, generation, total_gens, stagnant):
    """Elitism-2 reproduction: keep the top-2 genomes, fill the rest with
    crossover+mutate children (or plain tournament copies) per docs/research/03 §3."""
    ranked = ranked.tolist()
    elite_k = 2
    elites = [pop_w[int(i)].copy() for i in order[-elite_k:][::-1]]
    children = []
    pc = 0.6
    while len(children) < len(pop_w) - elite_k:
        if rng.random() < pc and len(pop_w) - elite_k >= 2:
            a = ga.tournament(pop_w, ranked, k=3)
            b = ga.tournament(pop_w, ranked, k=3)
            child = ga.crossover(a, b, rng)
        else:
            child = ga.tournament(pop_w, ranked, k=3).copy()
        children.append(ga.mutate(child, rng, sigma=0.05, rank_norm=0.5,
                                  generation=generation, total_gens=total_gens,
                                  stagnant=stagnant))
    return elites + children


def _held_probe(weights, matches: int = HELD_MATCHES) -> float:
    """Held-out score on HELD_SEED under canonical tanh + DEFAULT_FITNESS
    scalarization (the promotion measuring stick). Harness globals are restored
    afterwards; any failure scores -inf so the run can never promote — and says
    why on stderr, so a broken probe is visible instead of silently gating or
    silently waving every candidate through (-inf >= -inf)."""
    saved = (harness.ACTIVATION, harness.FIT_ELO, harness.FIT_ECON,
             harness.FIT_SURV, harness.FIT_STUCK)
    try:
        harness.ACTIVATION = 0
        harness.FIT_ELO = DEFAULT_FITNESS["elo"]
        harness.FIT_ECON = DEFAULT_FITNESS["econ"]
        harness.FIT_SURV = DEFAULT_FITNESS["survival"]
        harness.FIT_STUCK = DEFAULT_FITNESS["stuck"]
        return float(np.mean([harness.evaluate(weights, HELD_SEED, m, HELD_SEED)
                              for m in range(matches)]))
    except Exception as ex:
        print(f"held probe failed ({ex.__class__.__name__}: {ex}); scored -inf",
              file=sys.stderr)
        return float("-inf")
    finally:
        (harness.ACTIVATION, harness.FIT_ELO, harness.FIT_ECON,
         harness.FIT_SURV, harness.FIT_STUCK) = saved


def run(pop: int, gens: int, seed: int, dry_run: bool = False, resume: str | None = None, activation: str = "tanh", islands: int = 1, curriculum: str = "mixed"):
    try:
        os.chdir(Path(__file__).resolve().parents[2])
    except Exception:
        pass
    rng = np.random.default_rng(seed)
    random.seed(seed)
    np.random.seed(seed)

    if activation not in ("tanh", "relu"):
        raise SystemExit(f"activation must be tanh or relu, got {activation}")
    if curriculum not in ("mixed", "pvp_first", "horde_first"):
        raise SystemExit(f"curriculum must be mixed/pvp_first/horde_first, got {curriculum}")
    if islands < 1 or islands > 8:
        raise SystemExit(f"islands must be 1..8, got {islands}")
    if resume == "auto":
        raise SystemExit("--resume needs a run dir (e.g. evolved/runs/<ts>); 'auto' is not supported")
    harness.ACTIVATION = 1 if activation == "relu" else 0

    tag = f"_{activation}" if activation != "tanh" else ""
    if islands > 1: tag += f"_is{islands}"
    if curriculum != "mixed": tag += f"_{curriculum}"
    ts = time.strftime("%Y-%m-%d_%H%M%S")
    run_dir = Path(f"evolved/runs/{ts}_pop{pop}_g{gens}_s{seed}{tag}")
    run_dir.mkdir(parents=True, exist_ok=True)

    config = {"pop": pop, "gens": gens, "seed": seed, "fitness": DEFAULT_FITNESS, "dry_run": dry_run, "activation": activation, "islands": islands, "curriculum": curriculum, "held_seed": HELD_SEED}
    (run_dir / "config.json").write_text(json.dumps(config, indent=2))

    start_gen = 0
    best_w = None
    best_f = float("-inf")
    if resume:
        start_gen, resumed_pop, best_w, best_f = _load_resume(resume, seed, pop)
        if resumed_pop is not None:
            # A resumed pool continues as a single population: the checkpoint has
            # no island layout, and splitting a primed pool would scatter the
            # loaded elites.
            islands = 1
            pop_w = resumed_pop
        else:
            pop_w = None
    else:
        pop_w = None

    # island split (ring migration)
    if islands == 1:
        if pop_w is None:
            pop_w = ga.clone_heuristic(rng, P=pop, sigma=0.02)
        island_pops: list[list] = [pop_w]
    else:
        per = max(8, pop // islands)
        island_pops = [ga.clone_heuristic(np.random.default_rng(seed ^ (i * 0x9E3779B9)), P=per, sigma=0.02) for i in range(islands)]
        pop_w = island_pops[0]  # alias for the single-pool bookkeeping below

    hof: list = []
    plateau = 0

    csv_path = run_dir / "fitness.csv"
    with open(csv_path, "w", newline="") as cf:
        writer = csv.writer(cf)
        writer.writerow(["gen", "best", "mean", "median", "q25", "q75", "held"])

        for g in range(start_gen, gens):
            # curriculum phase (pvp_first/horde_first shift arena mix for early gens)
            harness.CURRICULUM = curriculum if curriculum != "mixed" and g < gens // 3 else "mixed"

            # island eval: each island evaluates its subpop, global best is over all islands
            if islands == 1:
                fitness = harness.evaluate_population(island_pops[0], g, seed)
                all_fitness = fitness
                all_pops_flat = island_pops[0]
            else:
                island_fitness: list[list[float]] = []
                for ii, ip in enumerate(island_pops):
                    island_fitness.append(harness.evaluate_population(ip, g, seed ^ (ii * 7919)))
                all_fitness = [v for lst in island_fitness for v in lst]
                all_pops_flat = [w for lst in island_pops for w in lst]
            order = np.argsort(all_fitness)
            ranked = np.empty(len(all_fitness), dtype=float)
            ranked[order] = np.arange(len(all_fitness)) / max(1, len(all_fitness) - 1)

            best_idx = int(np.argmax(all_fitness))
            f = float(all_fitness[best_idx])
            improved = f > best_f + 1e-6
            if improved:
                best_f = f
                best_w = all_pops_flat[best_idx].copy()
                plateau = 0
                # checkpoint top-3 of this gen (global)
                top3 = [all_pops_flat[int(i)] for i in order[-3:][::-1]]
                ckpt = {
                    "gen": g,
                    "best_fitness": f,
                    "top3": [w.astype(float).tolist() for w in top3],
                    "fitness": all_fitness,
                    "activation": activation,
                    "curriculum": curriculum,
                    "islands": islands,
                }
                (run_dir / f"gen_{g:03d}.json").write_text(json.dumps(ckpt, indent=2))
            else:
                plateau += 1
            stagnant = plateau >= 8

            # held probe (lightweight: 20 matches on held seed, same harness.ACTIVATION)
            held_m = float("nan")
            if g % 5 == 0 or g == gens - 1:
                cand = best_w if best_w is not None else all_pops_flat[best_idx]
                held_m = float(np.mean([harness.evaluate(cand, HELD_SEED, m, HELD_SEED) for m in range(20)]))

            # log per-gen CSV
            arr = np.array(all_fitness, dtype=float)
            writer.writerow([g, float(np.max(arr)), float(np.mean(arr)), float(np.median(arr)),
                              float(np.percentile(arr, 25)), float(np.percentile(arr, 75)), held_m])
            cf.flush()
            print(f"gen {g:03d}  best {f:+.4f}  mean {np.mean(arr):+.4f}  median {np.median(arr):+.4f}  held20 {held_m:+.4f}  stag {plateau}")

            if g == gens - 1:
                break

            # selection + reproduction per island (elitism 2 each)
            if islands == 1:
                pop_w = _next_generation(all_pops_flat, ranked, order, rng, g, gens, stagnant)
                island_pops[0] = pop_w
            else:
                new_islands: list[list] = []
                for ii, ip in enumerate(island_pops):
                    fit = island_fitness[ii]
                    ord2 = np.argsort(fit)
                    rk2 = np.empty(len(fit), dtype=float)
                    rk2[ord2] = np.arange(len(fit)) / max(1, len(fit) - 1)
                    new_islands.append(_next_generation(ip, rk2, ord2, rng, g, gens, stagnant))
                island_pops = new_islands
                # ring-migrate every 10 gens
                if g % 10 == 9 and islands > 1:
                    ga.island_mix(island_pops, rng, migrants=2)

            # HOF ring — freshness gated (don't re-inject a genome already in the live pop)
            global_elites = [all_pops_flat[int(i)].copy() for i in order[-2:][::-1]] if len(all_pops_flat) >= 2 else []
            hof = (hof + global_elites)[:8] if hof else global_elites[:]
            hof = hof[:8]
            if g % 12 == 11 and len(hof) >= 2:
                # pick a HOF entry not equal to current best (weight-hash dedup)
                cand = random.choice(hof)
                try:
                    is_dup = best_w is not None and float(np.mean((cand - best_w) ** 2)) < 1e-8
                except Exception:
                    is_dup = False
                if not is_dup:
                    # NB: keep the rng draw inside the multi-island arm so the
                    # single-island rng stream stays byte-identical to history.
                    tgt = island_pops[rng.integers(0, len(island_pops))] if islands > 1 else island_pops[0]
                    tgt[random.randrange(len(tgt))] = cand.copy()

    # promote best — held-gated so a weaker run never clobbers the shipped champion
    if best_w is not None:
        best_path = Path("evolved/best.json")
        candidate_held = _held_probe(best_w)
        current_held = float("-inf")
        if best_path.exists():
            try:
                current = json.loads(best_path.read_text())
                current_held = _held_probe(np.array(current["weights"], dtype=float))
            except Exception as ex:
                print(f"current champion (evolved/best.json) unreadable or unevaluable "
                      f"({ex.__class__.__name__}: {ex}); promotion gate treats it as unmatched",
                      file=sys.stderr)
                current_held = float("-inf")
        if candidate_held >= current_held or current_held == float("-inf"):
            ga.save_best(best_path, best_w, generation=gens - 1, fitness=best_f, config=config)
            print(f"best -> {best_path}  gen {gens-1}  train {best_f:+.4f}  held40 {candidate_held:+.4f} (promoted, beats {current_held:+.4f})")
        else:
            print(f"skipped best.json: candidate held40 {candidate_held:+.4f} < existing {current_held:+.4f} (keep current champion)")

    print(f"run dir: {run_dir}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--pop", type=int, default=32)
    ap.add_argument("--gens", type=int, default=40)
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--dry-run", action="store_true", help="synthetic fitness stub (no sim)")
    ap.add_argument("--resume", type=str, default=None)
    ap.add_argument("--activation", type=str, default="tanh", choices=["tanh", "relu"])
    ap.add_argument("--islands", type=int, default=1, help="island count 1..8 (ring migrate every 10 gens)")
    ap.add_argument("--curriculum", type=str, default="mixed", choices=["mixed", "pvp_first", "horde_first"])
    args = ap.parse_args()
    run(args.pop, args.gens, args.seed, args.dry_run, args.resume, activation=args.activation, islands=args.islands, curriculum=args.curriculum)
