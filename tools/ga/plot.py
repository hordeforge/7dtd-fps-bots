#!/usr/bin/env python3
"""Plot fitness.csv (best/mean fitness per generation)."""

from __future__ import annotations

import argparse
import csv
from pathlib import Path

try:
    import matplotlib.pyplot as plt
    HAS_MPL = True
except ImportError:
    HAS_MPL = False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("csv", help="evolved/runs/<ts>/fitness.csv")
    ap.add_argument("--out", default=None)
    args = ap.parse_args()
    p = Path(args.csv)
    if not p.is_file():
        raise SystemExit(f"fitness.csv not found: {p} (usage: plot.py evolved/runs/<ts>/fitness.csv)")
    gens, best, mean = [], [], []
    with open(p, encoding="utf-8") as f:
        r = csv.DictReader(f)
        for row in r:
            gens.append(int(row["gen"]))
            best.append(float(row["best"]))
            mean.append(float(row["mean"]))
    if not HAS_MPL:
        print(f"gens {len(gens)}  best last {best[-1] if best else 'n/a'}")
        return
    out = Path(args.out) if args.out else p.parent / "plot.png"
    plt.figure()
    plt.plot(gens, best, label="best")
    plt.plot(gens, mean, label="mean")
    plt.xlabel("generation"); plt.ylabel("fitness")
    plt.legend(); plt.tight_layout()
    plt.savefig(out)
    print(f"saved {out}")

if __name__ == "__main__":
    main()
