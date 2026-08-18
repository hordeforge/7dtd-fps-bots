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
import random
import time
from pathlib import Path

import numpy as np

import ga
import harness

DEFAULT_FITNESS = {"elo": 0.55, "econ": 0.25, "survival": 0.15, "stuck": 0.05}


def run(pop: int, gens: int, seed: int, dry_run: bool = False, resume: str | None = None):
    rng = np.random.default_rng(seed)
    random.seed(seed)
    np.random.seed(seed)

    ts = time.strftime("%Y-%m-%d_%H%M%S")
    run_dir = Path(f"evolved/runs/{ts}_pop{pop}_g{gens}_s{seed}")
    run_dir.mkdir(parents=True, exist_ok=True)

    config = {"pop": pop, "gens": gens, "seed": seed, "fitness": DEFAULT_FITNESS, "dry_run": dry_run}
    (run_dir / "config.json").write_text(json.dumps(config, indent=2))

    if resume:
        # TODO: load checkpoint and continue (deterministic replay)
        raise SystemExit(f"resume from {resume} not yet wired — rerun from gen 0 for now")

    pop_w = ga.clone_heuristic(rng, P=pop, sigma=0.02)
    best_w = None
    best_f = float("-inf")
    hof = []

    csv_path = run_dir / "fitness.csv"
    with open(csv_path, "w", newline="") as cf:
        writer = csv.writer(cf)
        writer.writerow(["gen", "best", "mean", "median", "q25", "q75"])

        for g in range(gens):
            # evaluate
            fitness = harness.evaluate_population(pop_w, g, seed)
            order = np.argsort(fitness)
            ranked = np.empty(len(fitness), dtype=float)
            ranked[order] = np.arange(len(fitness)) / max(1, len(fitness) - 1)

            best_idx = int(np.argmax(fitness))
            f = float(fitness[best_idx])
            if f > best_f:
                best_f = f
                best_w = pop_w[best_idx].copy()
                # checkpoint top-3 of this gen
                top3 = [pop_w[int(i)] for i in order[-3:][::-1]]
                ckpt = {
                    "gen": g,
                    "best_fitness": f,
                    "top3": [w.astype(float).tolist() for w in top3],
                    "fitness": fitness,
                }
                (run_dir / f"gen_{g:03d}.json").write_text(json.dumps(ckpt, indent=2))

            # log per-gen CSV
            arr = np.array(fitness, dtype=float)
            writer.writerow([g, float(np.max(arr)), float(np.mean(arr)), float(np.median(arr)),
                              float(np.percentile(arr, 25)), float(np.percentile(arr, 75))])
            cf.flush()
            print(f"gen {g:03d}  best {f:+.4f}  mean {np.mean(arr):+.4f}  median {np.median(arr):+.4f}")

            if g == gens - 1:
                break

            # selection + reproduction (elitism 2)
            elite_k = 2
            elite_idx = order[-elite_k:][::-1]
            elites = [pop_w[int(i)].copy() for i in elite_idx]
            children = []
            pc = 0.6
            # tournament size 3 on ranked fitness
            while len(children) < pop - elite_k:
                # one or two parents
                if rng.random() < pc and pop - elite_k >= 2:
                    a = ga.tournament(pop_w, ranked.tolist(), k=3)
                    b = ga.tournament(pop_w, ranked.tolist(), k=3)
                    child = ga.crossover(a, b, rng)
                else:
                    # mutate a copy of a single parent
                    p = ga.tournament(pop_w, ranked.tolist(), k=3)
                    child = p.copy()
                # rank-norm of the parent to scale sigma
                # find its rank (approx: use the tournament winner's rank)
                rn = 0.5  # default mid; refine when we track parent index
                child = ga.mutate(child, rng, sigma=0.05, rank_norm=rn)
                children.append(child)
            pop_w = elites + children
            hof = elites[:8]  # stub HOF

    # promote best
    if best_w is not None:
        best_path = Path("evolved/best.json")
        ga.save_best(best_path, best_w, generation=gens - 1, fitness=best_f, config=config)
        print(f"best -> {best_path}  gen {gens-1}  fitness {best_f:+.4f}")

    print(f"run dir: {run_dir}")
    (run_dir / "leaderboards.jsonl").write_text("")  # placeholder


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--pop", type=int, default=32)
    ap.add_argument("--gens", type=int, default=40)
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--dry-run", action="store_true", help="synthetic fitness stub (no sim)")
    ap.add_argument("--resume", type=str, default=None)
    args = ap.parse_args()
    run(args.pop, args.gens, args.seed, args.dry_run, args.resume)
