#!/usr/bin/env python3
"""viz.py: neural net topology + weight visualization.

Usage:
  python tools/ga/viz.py --best evolved/best.json            # -> evolved/net.png
  python tools/ga/viz.py --run evolved/runs/<ts>             # -> <run>/net.png
  python tools/ga/viz.py --best evolved/best.json --out x.png

Renders: layered topology (14→16→5) with edge opacity by |weight|, bias as node
rings, and an activation trace on 3 canonical inputs (healthy duelist, wounded
retreater, camping opportunist). No game binary needed.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import numpy as np

import ga
import theme

# Obs semantics follow combat_sim._simulate (docs/research/01 §2): slot 4 is
# the sustained-fire spread (the old lose-timer placeholder) and slot 12 is the
# rounds-left fraction (the old velocity placeholder).
INPUT_LABELS = ["hp", "eHp", "dist", "see", "sprd", "wpRg", "pel", "acc", "skill", "aggr", "selfP", "camp", "ammo", "stuck"]
OUT_LABELS = ["camp", "retreat", "aim", "fire", "strafe"]

try:
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    HAS_MPL = True
except ImportError:
    HAS_MPL = False


def load_best(path: Path):
    w, obj = ga.load_best(path)
    hidden = int(obj.get("hidden", 16))
    inputs = int(obj.get("inputs", 14))
    return w, hidden, inputs, obj


def split(w, hidden, inputs, outputs=5):
    W1 = w[: hidden * inputs].reshape(hidden, inputs)
    off = hidden * inputs
    b1 = w[off: off + hidden]; off += hidden
    W2 = w[off: off + outputs * hidden].reshape(outputs, hidden); off += outputs * hidden
    b2 = w[off: off + outputs]
    return W1, b1, W2, b2


def _sigmoid(x) -> float:
    """Clamped logistic, the same -8/+8 cutoff the sim kernels use."""
    return 1.0 / (1.0 + math.exp(-float(np.clip(x, -8, 8))))


def draw(w, hidden, inputs, title: str, out: Path, traces=None, dpi=theme.CHART_DPI):
    if not HAS_MPL:
        print("matplotlib not available")
        return
    W1, b1, W2, b2 = split(w, hidden, inputs)
    fig = plt.figure(figsize=(13.5, 5.2))
    gs = fig.add_gridspec(2, 3, width_ratios=[1.45, 1.05, 0.95], height_ratios=[1, 1],
                          wspace=0.22, hspace=0.38)
    # Row 0 col 0-1: topology layers as scatter with bias ring
    ax = fig.add_subplot(gs[0, 0])
    layers = [inputs, hidden, 5]
    xs = [0, 1, 2]
    max_n = max(layers)
    # node positions per layer, centered against the tallest layer
    yss = [np.linspace(0.08 + (max_n - n) * 0.04, 0.92 - (max_n - n) * 0.04, n) for n in layers]
    for li, (n, x) in enumerate(zip(layers, xs, strict=True)):
        ys = yss[li]
        color = theme.CHART_LAYERS[li]
        ax.scatter([x] * n, ys, s=68, c=color, alpha=0.92, edgecolors="white", linewidths=1.0, zorder=3)
        labels = [INPUT_LABELS, [f"h{i}" for i in range(hidden)], OUT_LABELS][li]
        for y, lab in zip(ys, labels, strict=True):
            ax.text(x + 0.06, y, lab, fontsize=6.2, va="center", color=theme.CHART_INK_MUTED)
        # bias ring (size by |b|)
        if li in (1, 2):
            bias, ring = (b1, theme.CHART_RING) if li == 1 else (b2, theme.CHART_INK_MUTED)
            for y, b in zip(ys, bias, strict=True):
                s = float(np.clip(abs(b) * 22, 0, 18))
                if s > 3:
                    ax.scatter([x], [y], s=s * 10, facecolors="none", edgecolors=ring, alpha=0.85, zorder=2)
    # edges: subsample so it isn't a hairball (show top-|weight| edges)
    rng = np.random.default_rng(0)
    for li in range(2):
        W = W1 if li == 0 else W2
        flat = np.abs(W).ravel()
        thr = np.percentile(flat, 65)
        for r in range(W.shape[0]):
            for c in range(W.shape[1]):
                if abs(W[r, c]) < thr and rng.random() < 0.65:
                    continue
                x0, x1 = xs[li], xs[li + 1]
                # W1 is hidden×inputs: r=hidden, c=inputs; W2 is outputs×hidden: r=out, c=hidden
                y0 = yss[li][c]
                y1 = yss[li + 1][r]
                col = theme.WEIGHT_NEG if W[r, c] < 0 else theme.WEIGHT_POS
                ax.plot([x0, x1], [y0, y1], color=col, alpha=float(np.clip(abs(W[r, c]) * 0.95, 0.12, 0.92)), lw=float(np.clip(abs(W[r,c])*2.1, 0.45, 2.8)), zorder=1)
    ax.set_xlim(-0.12, 2.28); ax.set_ylim(0, 1)
    ax.set_xticks([]); ax.set_yticks([])
    ax.set_title(f"{title}\n14→16(tanh)→5  ·  W1 {W1.size}+b1 {len(b1)}  ·  W2 {W2.size}+b2 {len(b2)}  (total {len(w)})", fontsize=9)
    ax.set_frame_on(False)

    # mats (reuse report's style)
    ax1 = fig.add_subplot(gs[0, 1])
    im = ax1.imshow(W1, aspect="auto", cmap="RdBu", vmin=-0.9, vmax=0.9)
    ax1.set_title("W1  hidden×inputs  16×14", fontsize=8)
    ax1.set_xlabel("inputs 0..13", fontsize=7); ax1.set_ylabel("hidden", fontsize=7)
    fig.colorbar(im, ax=ax1, fraction=0.046, pad=0.02)

    ax2 = fig.add_subplot(gs[0, 2])
    im2 = ax2.imshow(W2, aspect="auto", cmap="RdBu", vmin=-0.75, vmax=0.75)
    ax2.set_title("W2  outputs×hidden  5×16", fontsize=8)
    ax2.set_xlabel("hidden 0..15", fontsize=7); ax2.set_ylabel("outputs", fontsize=7)
    ax2.set_yticks(range(5)); ax2.set_yticklabels(OUT_LABELS, fontsize=7)
    fig.colorbar(im2, ax=ax2, fraction=0.046, pad=0.02)

    # traces row: 3 canonical observations → bar chart of 5 outputs (sigmoid/tanh)
    ax3 = fig.add_subplot(gs[1, :])
    if traces is None:
        traces = []
        def mk(hp, ehp, dist, see, vel): return np.array([hp, ehp, dist, see, 0.0, 0.6, 0.1, 0.75, 0.75, 0.5, 0.5, 0.2, vel, 0.0], dtype=float)
        traces = [("healthy duelist", mk(0.9, 0.8, 0.25, 1, 0.3)),
                  ("wounded / losing LOS", mk(0.22, 0.9, 0.55, 0, 0.7)),
                  ("camp opportunist", mk(0.82, 1.0, 0.72, 1, 0.05))]
    outs = []
    for name, x in traces:
        h = np.tanh(W1 @ x + b1)
        y = W2 @ h + b2
        # aim is the one tanh output; the rest are logistic
        outs.append((name, [_sigmoid(y[0]), _sigmoid(y[1]), math.tanh(float(y[2])),
                            _sigmoid(y[3]), _sigmoid(y[4])]))
    X = np.arange(5)
    wbar = 0.23
    # The three canonical traces are the ramp's three steps. A caller-supplied
    # longer list cycles rather than inventing a fourth hue.
    for i, (name, vals) in enumerate(outs):
        off = (i - 1) * wbar
        ax3.bar(X + off, vals, width=wbar, label=name,
                color=theme.CHART_LAYERS[i % len(theme.CHART_LAYERS)],
                alpha=0.88, edgecolor="white", linewidth=0.7)
    ax3.set_xticks(X); ax3.set_xticklabels(OUT_LABELS, fontsize=8)
    ax3.set_ylabel("output (sigmoid/tanh)"); ax3.set_ylim(0, 1)
    ax3.set_title("Activation traces: canonical observations (healthy / wounded / camp) · aim is tanh ([-1,1] shown clipped)", fontsize=8)
    ax3.legend(frameon=False, fontsize=7, ncols=3, loc="upper right")
    ax3.grid(True, axis="y", alpha=0.15)

    fig.suptitle("BotNeuralBrain: topology + weights + activations", fontsize=9, color=theme.CHART_INK, y=0.995)
    fig.tight_layout(rect=[0, 0, 1, 0.97])
    fig.savefig(out, dpi=dpi)
    plt.close(fig)
    print(f"net viz -> {out}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(
        description="Render the 14-16-5 net: edge opacity by |weight|, bias rings, "
                    "and an activation trace on 3 canonical inputs.",
        epilog="""examples:
  %(prog)s --best evolved/best.json            # -> evolved/net.png
  %(prog)s --run evolved/runs/<ts>             # -> <run>/net.png (last gen)
  %(prog)s --best evolved/best.json --out x.png

Exactly one of --best / --run is required.

exit status:
  0  the PNG was written
  1  the --best / --run path is missing or unusable
  2  bad command line""",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--best", default=None, metavar="BEST_JSON",
                    help="champion weights (e.g. evolved/best.json)")
    ap.add_argument("--run", default=None, help="evolved/runs/<ts> (uses best of last gen)")
    ap.add_argument("--out", default=None,
                    help="output PNG (default: net.png beside the --best file or inside the --run dir)")
    args = ap.parse_args()
    out = Path(args.out) if args.out else None
    if args.run and args.best:
        ap.error("--best and --run are mutually exclusive; pass exactly one")
    if args.run:
        run_dir = Path(args.run)
        if not run_dir.is_dir():
            raise SystemExit(f"--run dir not found: {run_dir} (e.g. evolved/runs/2026-08-19_011136_pop32_g30_s42)")
        cands = sorted(run_dir.glob("gen_*.json"), key=ga.gen_ckpt_key)
        path = Path(cands[-1]) if cands else None
        if path is None:
            raise SystemExit(f"no gen_*.json in {args.run}")
        try:
            obj = json.loads(path.read_text(encoding="utf-8"))
            w = np.array(obj["top3"][0], dtype=float)
        except (json.JSONDecodeError, KeyError, IndexError, TypeError, ValueError) as ex:
            raise SystemExit(f"{path} is not a gen_*.json checkpoint: {ex.__class__.__name__}: {ex}") from None
        hidden, inputs = 16, 14
        meta_title = f"{run_dir.name} gen {obj.get('gen','?')}  best {obj.get('best_fitness',0):+.3f}"
        draw(w, hidden, inputs, title=meta_title, out=out or (run_dir / "net.png"))
    elif args.best:
        best_path = Path(args.best)
        if not best_path.is_file():
            raise SystemExit(f"--best not found: {best_path} (e.g. evolved/best.json)")
        w, hidden, inputs, obj = load_best(best_path)
        meta_title = f"best.json  gen {obj.get('generation','?')}  fit {obj.get('fitness',0):+.3f}"
        draw(w, hidden, inputs, title=meta_title, out=out or (best_path.parent / "net.png"))
    else:
        ap.error("need --best or --run")
