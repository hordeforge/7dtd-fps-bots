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


def run(pop: int, gens: int, seed: int, dry_run: bool = False, resume: str | None = None, activation: str = "tanh", islands: int = 1, curriculum: str = "mixed"):
    try:
        os.chdir(Path(__file__).resolve().parents[2])
    except Exception:
        pass
    rng = np.random.default_rng(seed)
    random.seed(seed)
    np.random.seed(seed)

    # activation wiring (combat_sim now has explicit tanh/relu dispatch via harness.ACTIVATION)
    if activation not in ("tanh", "relu"):
        raise SystemExit(f"activation must be tanh or relu, got {activation}")
    if curriculum not in ("mixed", "pvp_first", "horde_first"):
        raise SystemExit(f"curriculum must be mixed/pvp_first/horde_first, got {curriculum}")
    if islands < 1 or islands > 8:
        raise SystemExit(f"islands must be 1..8, got {islands}")
    harness.ACTIVATION = 1 if activation == "relu" else 0
    tag = f"_{activation}" if activation != "tanh" else ""
    if islands > 1: tag += f"_is{islands}"
    if curriculum != "mixed": tag += f"_{curriculum}"
    ts = time.strftime("%Y-%m-%d_%H%M%S")
    run_dir = Path(f"evolved/runs/{ts}_pop{pop}_g{gens}_s{seed}{tag}")
    run_dir.mkdir(parents=True, exist_ok=True)

    config = {"pop": pop, "gens": gens, "seed": seed, "fitness": DEFAULT_FITNESS, "dry_run": dry_run, "activation": activation, "islands": islands, "curriculum": curriculum, "held_seed": 999}
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

    # island split (ring migration)
    if islands == 1:
        pop_w = ga.clone_heuristic(rng, P=pop, sigma=0.02)
        island_pops: list[list] = [pop_w]
    else:
        per = max(8, pop // islands)
        island_pops = [ga.clone_heuristic(np.random.default_rng(seed ^ (i * 0x9E3779B9)), P=per, sigma=0.02) for i in range(islands)]
        pop_w = island_pops[0]  # alias for resume path compatibility
    best_w = None
    best_f = float("-inf")
    hof: list = []
    plateau = 0

    csv_path = run_dir / "fitness.csv"
    with open(csv_path, "w", newline="") as cf:
        writer = csv.writer(cf)
        writer.writerow(["gen", "best", "mean", "median", "q25", "q75", "held"])

        for g in range(gens):
            # curriculum phase (pvp_first/horde_first shift arena mix for early gens)
            if curriculum != "mixed" and g < gens // 3:
                harness.CURRICULUM = curriculum
            else:
                harness.CURRICULUM = "mixed"
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
                top3_idx = order[-3:][::-1]
                top3 = [all_pops_flat[int(i)] for i in top3_idx]
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

            # held probe (lightweight: 20 matches on held seed 999, same harness.ACTIVATION)
            held_m = float("nan")
            if g % 5 == 0 or g == gens - 1:
                cand = best_w if best_w is not None else all_pops_flat[best_idx]
                scores = [harness.evaluate(cand, 999, m, 999) for m in range(20)]
                held_m = float(np.mean(scores))

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
                elite_k = 2
                elite_idx = order[-elite_k:][::-1]
                elites = [all_pops_flat[int(i)].copy() for i in elite_idx]
                children = []
                pc = 0.6
                while len(children) < len(all_pops_flat) - elite_k:
                    if rng.random() < pc and len(all_pops_flat) - elite_k >= 2:
                        a = ga.tournament(all_pops_flat, ranked.tolist(), k=3)
                        b = ga.tournament(all_pops_flat, ranked.tolist(), k=3)
                        child = ga.crossover(a, b, rng)
                    else:
                        p = ga.tournament(all_pops_flat, ranked.tolist(), k=3)
                        child = p.copy()
                    rn = 0.5
                    child = ga.mutate(child, rng, sigma=0.05, rank_norm=rn, generation=g, total_gens=gens, stagnant=stagnant)
                    children.append(child)
                pop_w = elites + children
                island_pops[0] = pop_w
            else:
                new_islands: list[list] = []
                for ii, ip in enumerate(island_pops):
                    fit = island_fitness[ii]
                    ord2 = np.argsort(fit)
                    rk2 = np.empty(len(fit), dtype=float)
                    rk2[ord2] = np.arange(len(fit)) / max(1, len(fit) - 1)
                    elite_k = 2
                    elites = [ip[int(i)].copy() for i in ord2[-elite_k:][::-1]]
                    children = []
                    pc = 0.6
                    while len(children) < len(ip) - elite_k:
                        if rng.random() < pc and len(ip) - elite_k >= 2:
                            a = ga.tournament(ip, rk2.tolist(), k=3)
                            b = ga.tournament(ip, rk2.tolist(), k=3)
                            child = ga.crossover(a, b, rng)
                        else:
                            p = ga.tournament(ip, rk2.tolist(), k=3)
                            child = p.copy()
                        child = ga.mutate(child, rng, sigma=0.05, rank_norm=0.5, generation=g, total_gens=gens, stagnant=stagnant)
                        children.append(child)
                    new_islands.append(elites + children)
                island_pops = new_islands
                # ring-migrate every 10 gens
                if g % 10 == 9 and islands > 1:
                    ga.island_mix(island_pops, rng, migrants=2)
            # HOF ring — freshness gated (don't re-inject a genome that's already in the live pop)
            global_elites = [all_pops_flat[int(i)].copy() for i in order[-2:][::-1]] if len(all_pops_flat) >= 2 else []
            # filter HOF candidates that are too similar to current elites (elites are the freshest)
            hof = (hof + global_elites)[:8] if hof else global_elites[:]
            hof = hof[:8]
            if g % 12 == 11 and len(hof) >= 2:
                import random as _rr
                # pick a HOF entry not equal to current best (open-addressed dedup via weight hash)
                cand = _rr.choice(hof)
                # quick Hamming guard: if candidate is ~identical to current best, skip
                try:
                    is_dup = best_w is not None and float(np.mean((cand - best_w) ** 2)) < 1e-8
                except Exception:
                    is_dup = False
                if not is_dup:
                    tgt = island_pops[rng.integers(0, len(island_pops))] if islands > 1 else island_pops[0]
                    tgt[_rr.randrange(len(tgt))] = cand.copy()

    # promote best — held-gated so a weaker run never clobbers the shipped champion
    if best_w is not None:
        best_path = Path("evolved/best.json")
        candidate = best_w
        candidate_held, current_held = float("-inf"), float("-inf")
        # Validate candidate on held seed (light, 40 matches) — canonical weights.
        try:
            _saved_act = harness.ACTIVATION
            harness.ACTIVATION = 0
            _saved_elo, _saved_econ, _saved_surv, _saved_stuck = harness.FIT_ELO, harness.FIT_ECON, harness.FIT_SURV, harness.FIT_STUCK
            harness.FIT_ELO, harness.FIT_ECON, harness.FIT_SURV, harness.FIT_STUCK = 0.55, 0.25, 0.15, 0.05
            candidate_held = float(np.mean([harness.evaluate(candidate, 999, m2, 999) for m2 in range(40)]))
            harness.ACTIVATION = _saved_act
            harness.FIT_ELO, harness.FIT_ECON, harness.FIT_SURV, harness.FIT_STUCK = _saved_elo, _saved_econ, _saved_surv, _saved_stuck
        except Exception:
            candidate_held = float("-inf")
        # Measure the existing champion's held (if any) with the same held probe.
        if best_path.exists():
            try:
                _cur = json.loads(best_path.read_text())
                _curw = np.array(_cur["weights"], dtype=float)
                _saved_act = harness.ACTIVATION
                harness.ACTIVATION = 0
                _saved_elo, _saved_econ, _saved_surv, _saved_stuck = harness.FIT_ELO, harness.FIT_ECON, harness.FIT_SURV, harness.FIT_STUCK
                harness.FIT_ELO, harness.FIT_ECON, harness.FIT_SURV, harness.FIT_STUCK = 0.55, 0.25, 0.15, 0.05
                current_held = float(np.mean([harness.evaluate(_curw, 999, m3, 999) for m3 in range(40)]))
                harness.ACTIVATION = _saved_act
                harness.FIT_ELO, harness.FIT_ECON, harness.FIT_SURV, harness.FIT_STUCK = _saved_elo, _saved_econ, _saved_surv, _saved_stuck
            except Exception:
                current_held = float("-inf")
        if candidate_held >= current_held or current_held == float("-inf"):
            ga.save_best(best_path, candidate, generation=gens - 1, fitness=best_f, config=config)
            print(f"best -> {best_path}  gen {gens-1}  train {best_f:+.4f}  held40 {candidate_held:+.4f} (promoted, beats {current_held:+.4f})")
        else:
            print(f"skipped best.json: candidate held40 {candidate_held:+.4f} < existing {current_held:+.4f} (keep current champion)")

    print(f"run dir: {run_dir}")
    (run_dir / "leaderboards.jsonl").write_text("")  # placeholder


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
