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
import os
import time
from pathlib import Path

import numpy as np

import ga
import harness

DEFAULT_FITNESS = {"elo": 0.55, "econ": 0.25, "survival": 0.15, "stuck": 0.05}


def run(pop: int, gens: int, seed: int, dry_run: bool = False, resume: str | None = None):
    try:
        os.chdir(Path(__file__).resolve().parents[2])
    except Exception:
        pass
    rng = np.random.default_rng(seed)
    random.seed(seed)
    np.random.seed(seed)

    ts = time.strftime("%Y-%m-%d_%H%M%S")
    run_dir = Path(f"evolved/runs/{ts}_pop{pop}_g{gens}_s{seed}")
    run_dir.mkdir(parents=True, exist_ok=True)

    config = {"pop": pop, "gens": gens, "seed": seed, "fitness": DEFAULT_FITNESS, "dry_run": dry_run}
    (run_dir / "config.json").write_text(json.dumps(config, indent=2))

        # ensure evolved/ resolves to clanker root regardless of cwd
    try:
        os.chdir(Path(__file__).resolve().parents[2])
    except Exception:
        pass
    if resume and resume != "auto":
        # Seeded continuation: load last checkpoint's top population from the resumed run and continue.
        # Resume dir can be evolved/runs/<ts>_... or a path with gen_*.json inside.
        try:
            import re as _re
            resume_path = Path(resume)
            if resume_path.is_file():
                ckpts = [resume_path]
            else:
                ckpts = sorted(resume_path.glob("gen_*.json"))
            if ckpts:
                last = ckpts[-1]
                ckpt = json.loads(last.read_text())
                # Reconstitute population from checkpoint's top3 + jitter for diversity
                import pathlib as _pl
                # Load config from resume dir if available
                rconfig = {}
                cfg_path = resume_path / "config.json" if resume_path.is_dir() else resume_path.parent / "config.json"
                if cfg_path.exists():
                    rconfig = json.loads(cfg_path.read_text())
                saved_fitness = float(ckpt.get("best_fitness", float("-inf")))
                # Recreate pop from top3 with jitter to fill P
                top3_raw = ckpt.get("top3") or []
                top3 = [__import__("numpy").array(w, dtype=float) for w in top3_raw]
                if top3:
                    pop_w = []
                    rng2 = __import__("numpy").random.default_rng(seed ^ 0x9E3779B9)
                    for i in range(pop):
                        base = top3[i % len(top3)]
                        if i < len(top3):
                            pop_w.append(base.copy())
                        else:
                            pop_w.append(base + rng2.normal(0, 0.03, base.size).astype(float))
                    best_w = top3[0].copy()
                    best_f = saved_fitness
                    print(f"resume: loaded {last} gen {ckpt.get('gen')} fit {saved_fitness:.3f}, seeded next gen")
                    # Continue loop by re-entering main loop from next gen
                    # We do this by prepending a synthetic gens offset: run the remaining gens
                    # Instead of complicating, just continue the main loop below already primed — break out to it
                    # by jumping to loop start (we store resume flag and handle inside loop)
                    resume_ckpts = ckpts
                else:
                    print(f"resume: {last} has no top3, starting fresh")
                    pop_w = ga.clone_heuristic(rng, P=pop, sigma=0.02)
                    best_w = None; best_f = float("-inf"); resume_ckpts = []
            else:
                print(f"resume: no checkpoints in {resume}, starting fresh")
                pop_w = ga.clone_heuristic(rng, P=pop, sigma=0.02)
                best_w = None; best_f = float("-inf"); resume_ckpts = []
        except Exception as ex:
            print(f"resume failed ({ex}), starting fresh")
            pop_w = ga.clone_heuristic(rng, P=pop, sigma=0.02)
            best_w = None; best_f = float("-inf"); resume_ckpts = []
    elif resume == "auto":
        # Auto-resume is not requested
        if False: pass
    if resume and resume != "auto" and resume_ckpts is not None:
        # We already primed pop_w/best_w — need to run the remaining gens explicitly.
        # Replace the simple for-loop with an explicit continuation that appends generations.
        start_gen = int(ckpt.get("gen", -1)) + 1
        remaining = gens - start_gen
        if remaining <= 0:
            print(f"resume: already at gen {ckpt.get('gen')}, nothing to do")
        else:
            import numpy as _np
            import csv as _csv
            # Append to existing fitness.csv
            csv_path2 = run_dir / "fitness.csv"
            # Copy old fitness.csv header + rows if resuming across run dirs
            # (we keep new run_dir's csv separate for now; don't merge)
            csv_path = run_dir / "fitness.csv"
            with open(csv_path, "w", newline="") as cf:
                writer = _csv.writer(cf)
                writer.writerow(["gen", "best", "mean", "median", "q25", "q75"])
                for g in range(start_gen, start_gen + remaining):
                    fitness = harness.evaluate_population(pop_w, g, seed)
                    order = _np.argsort(fitness)
                    ranked = _np.empty(len(fitness), dtype=float)
                    ranked[order] = _np.arange(len(fitness)) / max(1, len(fitness) - 1)
                    best_idx = int(_np.argmax(fitness))
                    f = float(fitness[best_idx])
                    if f > best_f:
                        best_f = f; best_w = pop_w[best_idx].copy()
                        top3 = [pop_w[int(i)] for i in order[-3:][::-1]]
                        ckpt2 = {"gen": g, "best_fitness": f, "top3": [w.astype(float).tolist() for w in top3], "fitness": fitness}
                        (run_dir / f"gen_{g:03d}.json").write_text(json.dumps(ckpt2, indent=2))
                    arr = _np.array(fitness, dtype=float)
                    writer.writerow([g, float(_np.max(arr)), float(_np.mean(arr)), float(_np.median(arr)), float(_np.percentile(arr, 25)), float(_np.percentile(arr, 75))])
                    cf.flush()
                    print(f"gen {g:03d}  best {f:+.4f}  mean {_np.mean(arr):+.4f}  median {_np.median(arr):+.4f}")
                    if g == start_gen + remaining - 1: break
                    elite_k = 2; elite_idx = order[-elite_k:][::-1]; elites = [pop_w[int(i)].copy() for i in elite_idx]
                    children = []; pc = 0.6
                    while len(children) < pop - elite_k:
                        if rng.random() < pc and pop - elite_k >= 2:
                            a = ga.tournament(pop_w, ranked.tolist(), k=3); b = ga.tournament(pop_w, ranked.tolist(), k=3)
                            child = ga.crossover(a, b, rng)
                        else:
                            child = ga.tournament(pop_w, ranked.tolist(), k=3).copy()
                        child = ga.mutate(child, rng, sigma=0.05, rank_norm=0.5, generation=g, total_gens=gens)
                        children.append(child)
                    pop_w = elites + children
            best_path = Path("evolved/best.json")
            import ga as _ga
            _ga.save_best(best_path, best_w, generation=start_gen + remaining - 1, fitness=best_f, config=config)
            print(f"best -> {best_path}  gen {start_gen + remaining -1}  fitness {best_f:+.4f}")
            print(f"run dir: {run_dir}")
            (run_dir / "leaderboards.jsonl").write_text("")
            return
    if resume and resume != "auto": pass  # already handled
    if False: pass
    if resume == "auto":
        raise SystemExit(f"resume from {resume} not yet wired — rerun from gen 0 for now")
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
                child = ga.mutate(child, rng, sigma=0.05, rank_norm=rn, generation=g, total_gens=gens)
                children.append(child)
            pop_w = elites + children
            # HOF ring: keep top-8 elites history, re-inject one every 12 gens for diversity
            hof = (hof + elites)[:8] if hof else elites[:]
            hof = hof[:8]
            if g % 12 == 11 and len(hof) >= 2:
                import random as _rr
                pop_w[_rr.randrange(len(pop_w))] = _rr.choice(hof).copy()

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
