#!/usr/bin/env python3
"""sweep.py: sweep activations (tanh vs relu) at the baked H16 hidden size.

numba combat_sim bakes HIDDEN==16 as a compile-time literal (H16), so the
hidden size is not sweepable: every non-16 layout is skipped before any
evolution. What remains a real, discriminative knob is the activation, so this
sweep runs H16-tanh vs H16-relu and nothing else.

Usage:
  python tools/ga/sweep.py --seeds 1 --trials 2      # quick check
  python tools/ga/sweep.py                           # H16 tanh+relu ablation

Each activation re-runs a short evolution (same seed chain, same combat-sim
fitness), plots both runs on one chart, and prints the ranking.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

import ga
import harness

ROOT = Path(__file__).resolve().parent.parent.parent


def run_one(activation: str, pop: int, gens: int, seed: int):
    """One evolution with `activation` ('tanh' or 'relu'). The other 16 are
    skip branches; H16 is the only real size. harness.ACTIVATION selects
    simulate_match (canonical tanh) or simulate_match_relu."""
    orig_act = harness.ACTIVATION
    harness.ACTIVATION = 1 if activation == "relu" else 0
    try:
        rng = np.random.default_rng(seed)
        import random as _r
        _r.seed(seed)
        np.random.seed(seed)
        pop_w = ga.init_population(rng, pop, sigma=0.02)
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
            # Defaults keep the historical short-sweep behavior: constant sigma
            # (generation=0 -> no anneal), no stagnant burst.
            pop_w = ga.next_generation(pop_w, ranked, order, rng)
        return curve
    finally:
        harness.ACTIVATION = orig_act


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pop", type=int, default=24)
    ap.add_argument("--gens", type=int, default=20)
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--activations", nargs="*", default=None, choices=["tanh", "relu"])
    ap.add_argument("--out", default=None, help="plot PNG path")
    args = ap.parse_args()

    acts = args.activations or ["tanh", "relu"]

    print(f"sweep: H16 x {acts}  pop {args.pop} gens {args.gens} seed {args.seed}")
    curves = {}
    for act in acts:
        key = f"H16-{act}"
        print(f"  running {key} ...", flush=True)
        curves[key] = run_one(act, args.pop, args.gens, args.seed)

    ok = {k: v for k, v in curves.items() if v}
    if not ok:
        print("no successful curves: nothing to rank/plot"); return
    # summary table
    print("\n layout           best   mean@last   Δ best-mean   FLOPs/bot   W")
    print(" ────────────────────────────────────────────────────────────")
    for key in sorted(ok):
        hidden = 16
        _, best, mean = zip(*ok[key])
        b = best[-1]; m = mean[-1]
        flops = 2 * 14 * hidden + 2 * hidden * 5
        W = hidden * 14 + hidden + 5 * hidden + 5
        print(f" {key:15s} {b:+.3f}  {m:+.3f}       {b-m:+.3f}       {flops:4d}      {W:3d}")

    # rank by final best
    ranked = sorted(ok.items(), key=lambda kv: kv[1][-1][1], reverse=True)
    print(f"\n winner: {ranked[0][0]}  (best {ranked[0][1][-1][1]:+.3f})")

    # plot all curves on one chart (relative to repo root, like every other
    # evolved/ artifact)
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
        ax.set_title("Activation sweep: fitness over generations (pop "
                     f"{args.pop} gens {args.gens} seed {args.seed}, combat)")
        ax.legend(frameon=False, fontsize=7, ncols=3)
        ax.grid(True, alpha=0.18)
        fig.tight_layout()
        out = Path(args.out) if args.out else ROOT / f"evolved/runs/sweep_H{args.pop}_g{args.gens}_s{args.seed}.png"
        if args.out is not None:
            out = Path(args.out)
        else:
            out = ROOT / f"evolved/runs/sweep_H{args.pop}_g{args.gens}_s{args.seed}.png"
        out.parent.mkdir(parents=True, exist_ok=True)
        fig.savefig(out, dpi=150)
        plt.close(fig)
        print(f"plot -> {out}")
    except ImportError:
        print("(matplotlib not installed: table only)")
    # also dump JSON for CI diffing (atomic like every other evolved/ artifact;
    # the diff consumer must never read a half-written file)
    dump = {k: [{"gen": g, "best": b, "mean": m} for (g, b, m) in v] for k, v in ok.items()}
    jpath = (ROOT / f"evolved/runs/sweep_{args.seed}.json" if args.out is None
             else Path(args.out).with_suffix(".json"))
    jpath.parent.mkdir(parents=True, exist_ok=True)
    ga.atomic_write_text(jpath, json.dumps(dump, indent=2))
    print(f"json -> {jpath}")


if __name__ == "__main__":
    main()
