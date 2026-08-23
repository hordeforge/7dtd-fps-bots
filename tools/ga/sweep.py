#!/usr/bin/env python3
"""sweep.py — sweep hidden sizes / activations to find the best layout.

Usage:
  python tools/ga/sweep.py --seeds 1 --trials 2   # quick check
  python tools/ga/sweep.py --sweep builtin       # built-in 4-layout ablation

Each layout re-runs a short evolution (same seed chain, same combat-sim
fitness), plots all runs on one chart, and prints the ranking.
"""

from __future__ import annotations

import argparse
import hashlib
import csv
import json
import math
from pathlib import Path
import sys

import numpy as np
import sys as _sys
_sys.path.insert(0, str(Path(__file__).parent))
import ga
import harness
import combat_sim as _cs


def run_one(hidden: int, activation: str, pop: int, gens: int, seed: int):
    """One evolution with `hidden` and `activation` ('tanh' or 'relu').

    Note: combat_sim is the real fitness — ga.forward is NOT used by the
    numba harness. We must flip combat_sim._ACTIVATION (and keep HIDDEN==16
    for now — non-16 sweeps still require a recompile; we score them via
    placeholder until that lands).
    """
    # Only H16 is real for now (numba HIDDEN is a literal). Larger sweeps
    # are estimated via ga.forward path until combat_sim is templated.
    if hidden != ga.HIDDEN:
        # short-circuit: report that non-H16 is not yet wired (don't fake a run)
        print(f"  note: H{hidden} sweep not yet wired (numba HIDDEN==16) — skipping, only H16-tanh/relu are real")
        return []
    orig_act = harness.ACTIVATION
    harness.ACTIVATION = 1 if activation == "relu" else 0
    orig_forward = ga.forward
    def fwd(w, x):
        w1, b1, w2, b2 = ga.flat_to_layers(w)
        if activation == "relu":
            h = np.maximum(0, w1 @ x + b1)
        else:
            h = np.tanh(w1 @ x + b1)
        return w2 @ h + b2
    ga.forward = fwd
    try:
        rng = np.random.default_rng(seed)
        import random as _r
        _r.seed(seed)
        np.random.seed(seed)
        pop_w = ga.clone_heuristic(rng, P=pop, sigma=0.02)
        curve = []
        for g in range(gens):
            fitness = harness.evaluate_population(pop_w, g, seed)
            arr = np.array(fitness, dtype=float)
            curve.append((g, float(np.max(arr)), float(np.mean(arr))))
            if g == gens - 1:
                break
            order = np.argsort(fitness)
            ranked = np.empty(len(fitness), dtype=float)
            ranked[order] = np.arange(len(fitness)) / max(1, len(fitness) - 1)
            elite_k = 2
            elites = [pop_w[int(i)].copy() for i in order[-elite_k:][::-1]]
            children = []
            pc = 0.6
            while len(children) < pop - elite_k:
                if rng.random() < pc:
                    a = ga.tournament(pop_w, ranked.tolist(), k=3)
                    b = ga.tournament(pop_w, ranked.tolist(), k=3)
                    child = ga.crossover(a, b, rng)
                else:
                    child = ga.tournament(pop_w, ranked.tolist(), k=3).copy()
                child = ga.mutate(child, rng, sigma=0.05, rank_norm=0.5)
                children.append(child)
            pop_w = elites + children
        return curve
    finally:
        harness.ACTIVATION = orig_act
        ga.forward = orig_forward


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pop", type=int, default=24)
    ap.add_argument("--gens", type=int, default=20)
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--hidden", type=int, nargs="*", default=None, help="hidden sizes to sweep")
    ap.add_argument("--activations", nargs="*", default=None, choices=["tanh", "relu"])
    ap.add_argument("--sweep", default=None, choices=["builtin"], help="preset sweep")
    ap.add_argument("--out", default=None, help="plot PNG path")
    args = ap.parse_args()

    if args.sweep == "builtin":
        configs = [(8, "tanh"), (16, "tanh"), (16, "relu"), (24, "tanh")]
    else:
        hiddens = args.hidden or [8, 16, 24]
        acts = args.activations or ["tanh"]
        configs = [(h, a) for h in hiddens for a in acts]

    print(f"sweep: {configs}  pop {args.pop} gens {args.gens} seed {args.seed}")
    curves = {}
    for hidden, act in configs:
        key = f"H{hidden:02d}-{act}"
        print(f"  running {key} ...", flush=True)
        curves[key] = run_one(hidden, act, args.pop, args.gens, args.seed)

    # keep only successful curves for table/ranking/plots (non-H16 are skipped until numba templates)
    ok = {k: v for k, v in curves.items() if v}
    if not ok:
        print("no successful curves (all skipped) — nothing to rank/plot"); return
    # summary table
    print("\n layout        best   mean@last   Δ best-mean   FLOPs/bot   W")
    print(" ────────────────────────────────────────────────────────────")
    for key in sorted(ok):
        hidden = int(key.split("-")[0][1:])
        _, best, mean = zip(*ok[key])
        b = best[-1]; m = mean[-1]
        flops = 2 * 14 * hidden + 2 * hidden * 5
        W = hidden * 14 + hidden + 5 * hidden + 5
        print(f" {key:13s} {b:+.3f}  {m:+.3f}       {b-m:+.3f}       {flops:4d}      {W:3d}")

    # rank by final best
    ranked = sorted(ok.items(), key=lambda kv: kv[1][-1][1], reverse=True)
    print(f"\n winner: {ranked[0][0]}  (best {ranked[0][1][-1][1]:+.3f})")

    # plot all curves on one chart
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        fig, ax = plt.subplots(figsize=(8.5, 3.8))
        for key in sorted(ok):
            xs, bs, ms = zip(*ok[key])
            ax.plot(xs, bs, lw=1.5, label=f"{key} best")
            ax.plot(xs, ms, lw=1.0, ls="--", alpha=0.85, label=f"{key} mean")
        ax.set_xlabel("generation"); ax.set_ylabel("fitness")
        ax.set_title("Layout sweep — fitness over generations (pop "
                     f"{args.pop} gens {args.gens} seed {args.seed}, combat)")
        ax.legend(frameon=False, fontsize=7, ncols=3)
        ax.grid(True, alpha=0.18)
        fig.tight_layout()
        out = Path(args.out) if args.out else Path(f"evolved/runs/sweep_H{args.pop}_g{args.gens}_s{args.seed}.png")
        out.parent.mkdir(parents=True, exist_ok=True)
        fig.savefig(out, dpi=150)
        plt.close(fig)
        print(f"plot -> {out}")
    except ImportError:
        print("(matplotlib not installed — table only)")
    # also dump JSON for CI diffing
    dump = {k: [{"gen": g, "best": b, "mean": m} for (g, b, m) in v] for k, v in ok.items()}
    jpath = Path(f"evolved/runs/sweep_{args.seed}.json") if args.out is None else Path(args.out).with_suffix(".json")
    jpath.parent.mkdir(parents=True, exist_ok=True)
    jpath.write_text(json.dumps(dump, indent=2), encoding="utf-8")
    print(f"json -> {jpath}")


if __name__ == "__main__":
    main()
