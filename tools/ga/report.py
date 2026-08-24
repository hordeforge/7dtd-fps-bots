#!/usr/bin/env python3
"""report.py — generate an HTML evolution report from one or more runs.

Usage:
  python tools/ga/report.py --runs evolved/runs/2026-08-19_011136_pop32_g30_s42 --out evolved/runs/2026-08-19_.../report.html
  python tools/ga/report.py --runs run1 run2 --out compare.html

Produces one HTML file that embeds palette-optimized base64 PNGs (lazy-loaded,
sized via width/height; no external deps) plus JSON for programmatic checks.
"""

from __future__ import annotations

import argparse
import base64
import csv
import html
import io
import json
from pathlib import Path

try:
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    import numpy as np
    HAS_MPL = True
except ImportError:
    HAS_MPL = False
    np = None  # type: ignore[assignment]

# One accent hue (sky) for the whole GA tooling family; stops per role:
# CAB sky-700 main series, ACCENT sky-600 secondary, best line stays near-black.
CAB = "#0369a1"
ACCENT = "#0284c7"

# Charts are flat-color line/bar/heatmap figures: an adaptive-palette PNG runs
# ~3-4x smaller than matplotlib's default RGBA at identical visual quality, and
# these base64 blobs are the entire weight of the generated report.
def optimized_png_bytes(fig) -> bytes:
    bio = io.BytesIO()
    fig.savefig(bio, format="png", dpi=150, bbox_inches="tight")
    plt.close(fig)
    raw = bio.getvalue()
    try:
        from PIL import Image
    except ImportError:
        return raw  # palette optimization unavailable; ship the plain PNG
    im = Image.open(io.BytesIO(raw))
    if im.mode != "RGBA":
        im = im.convert("RGBA")
    q = im.quantize(colors=256, method=Image.Quantize.FASTOCTREE, dither=Image.Dither.NONE)
    out = io.BytesIO()
    q.save(out, "PNG", optimize=True)
    return out.getvalue()


def png_dimensions(data: bytes) -> tuple[int, int]:
    """Intrinsic pixel size straight from the PNG IHDR chunk (no image lib)."""
    import struct
    w, h = struct.unpack(">II", data[16:24])
    return w, h


def img_tag(data: bytes, alt: str) -> str:
    """Self-contained <img>: width/height reserve layout space before decode,
    loading=lazy keeps off-screen charts from decoding on open."""
    b64 = base64.b64encode(data).decode()
    w, h = png_dimensions(data)
    return (f"<img src='data:image/png;base64,{b64}' alt='{alt}' width='{w}' height='{h}'"
            f" loading='lazy' decoding='async'"
            f" style='max-width:100%;height:auto;border:1px solid #e2e8f0;border-radius:10px' />")


def load_csv(path: Path):
    gens, best, mean, median, q25, q75 = [], [], [], [], [], []
    with open(path, encoding="utf-8") as f:
        r = csv.DictReader(f)
        for row in r:
            gens.append(int(row["gen"]))
            best.append(float(row["best"]))
            mean.append(float(row["mean"]))
            median.append(float(row["median"]))
            q25.append(float(row.get("q25", row["mean"])) )
            q75.append(float(row.get("q75", row["mean"])) )
    return gens, best, mean, median, q25, q75


def fitness_band(gens, best, mean, median, q25, q75) -> bytes:
    fig, ax = plt.subplots(figsize=(9, 4))
    ax.fill_between(gens, q25, q75, color=CAB, alpha=0.14, label="IQR (q25–q75)")
    ax.plot(gens, mean, color=CAB, lw=1.1, alpha=0.9, label="mean")
    ax.plot(gens, median, color=ACCENT, lw=1.4, ls="--", label="median")
    ax.plot(gens, best, color="#111827", lw=1.7, label="best")
    ax.set_xlabel("generation"); ax.set_ylabel("fitness")
    ax.set_title("Evolution — fitness over generations")
    ax.legend(frameon=False, ncols=4, fontsize=8)
    ax.grid(True, alpha=0.18)
    fig.tight_layout()
    return optimized_png_bytes(fig)


def weight_hist(run_dir: Path) -> str | None:
    """Weight histogram of the final best (or first gen ckpt if no best yet)."""
    import numpy as np
    import ga
    cand = list(sorted(run_dir.glob("gen_*.json"), key=ga.gen_ckpt_key))
    if not cand:
        return None
    # pick the best genome of the last generation file
    last = json.loads(cand[-1].read_text(encoding="utf-8"))
    # top3 is [w, ...] flat arrays
    w = None
    if "top3" in last and last["top3"]:
        w = np.array(last["top3"][0], dtype=float)
    else:
        return None
    fig, ax = plt.subplots(figsize=(9, 2.6))
    ax.hist(w, bins=28, color=CAB, alpha=0.88, edgecolor="white", lw=0.6)
    ax.set_xlabel("weight value"); ax.set_ylabel("count")
    ax.set_title(f"Weight histogram — best of gen {last.get('gen', '?')}  (W={len(w)}, mean {np.mean(w):+.3f} σ {np.std(w):.3f})")
    ax.grid(True, axis="y", alpha=0.18)
    fig.tight_layout()
    return optimized_png_bytes(fig)


def best_net(run_dir: Path) -> str | None:
    """Tiny net topology image: 14 inputs → 16 hidden (tanh) → 5 outputs.
    Colors encode the final best's weights; gives a quick “is it dead units”
    scan without opening JSON.
    """
    import numpy as np
    import ga
    cand = list(sorted(run_dir.glob("gen_*.json"), key=ga.gen_ckpt_key))
    if not cand:
        return None
    last = json.loads(cand[-1].read_text(encoding="utf-8"))
    if "top3" not in last or not last["top3"]:
        return None
    w = np.array(last["top3"][0], dtype=float)
    H, IN, OUT = 16, 14, 5
    W1 = w[: H * IN].reshape(H, IN)
    fig, axes = plt.subplots(1, 2, figsize=(11, 3.4), gridspec_kw={"width_ratios": [1.05, 1]})
    im0 = axes[0].imshow(W1, aspect="auto", cmap="RdBu", vmin=-0.9, vmax=0.9)
    axes[0].set_title("W1  (hidden × inputs)  16×14"); axes[0].set_xlabel("inputs 0..13"); axes[0].set_ylabel("hidden 0..15")
    axes[0].set_xticks(range(IN)); axes[0].set_xticklabels(range(IN), fontsize=7)
    axes[0].set_yticks(range(H))
    fig.colorbar(im0, ax=axes[0], fraction=0.046, pad=0.04, label="weight")
    # second: W2
    off = H * IN + H
    W2 = w[off: off + OUT * H].reshape(OUT, H)
    im1 = axes[1].imshow(W2, aspect="auto", cmap="RdBu", vmin=-0.7, vmax=0.7)
    axes[1].set_title("W2  (outputs × hidden)  5×16"); axes[1].set_xlabel("hidden 0..15"); axes[1].set_ylabel("outputs 0..4")
    axes[1].set_xticks(range(H)); axes[1].set_xticklabels(range(H), fontsize=7)
    axes[1].set_yticks(range(OUT)); axes[1].set_yticklabels(["camp", "retreat", "aim", "fire", "strafe"], fontsize=7)
    fig.colorbar(im1, ax=axes[1], fraction=0.046, pad=0.04, label="weight")
    axes[1].set_title("W2  (outputs × hidden)  5×16", pad=10)
    fig.tight_layout()
    return optimized_png_bytes(fig)


def build(runs: list[Path], out: Path):
    parts: list[str] = []
    for run_dir in runs:
        csv_path = run_dir / "fitness.csv"
        if not csv_path.exists():
            parts.append(f"<p><b>{html.escape(run_dir.name)}</b>: no fitness.csv</p>")
            continue
        gens, best, mean, median, q25, q75 = load_csv(csv_path)
        cfg = {}
        cfg_path = run_dir / "config.json"
        if cfg_path.exists():
            try: cfg = json.loads(cfg_path.read_text(encoding="utf-8"))
            except Exception: pass

        # headline stats
        rel = (best[-1] - best[0]) / max(1e-9, abs(best[0])) * 100 if best else 0
        # pop/seed come from the run's config.json; a hand-edited file may hold
        # arbitrary text, so they are escaped like every other external string.
        headline = (
            f"gens {len(gens)} · pop {html.escape(str(cfg.get('pop','?')))} · seed {html.escape(str(cfg.get('seed','?')))} · "
            f"best {best[-1]:+.3f} (g{gens[best.index(max(best))]} peak {max(best):+.3f}) · "
            f"Δ vs g0 {rel:+.1f}% · mean {mean[-1]:+.3f}"
        )

        if HAS_MPL:
            band = fitness_band(gens, best, mean, median, q25, q75)
            wh = weight_hist(run_dir)
            topo = best_net(run_dir)
        else:
            band = wh = topo = None

        # Run dir names and paths are filesystem text (an --runs argument or a
        # shared run directory), so they must be HTML-escaped before they land
        # in the report: a name like "<img src=x onerror=...>" would otherwise
        # execute in the browser of whoever opens the generated file.
        sec = [f"<h2 style='margin:18px 0 4px'>{html.escape(run_dir.name)}</h2>",
               f"<p style='color:#334155;font-size:13px'>{headline}</p>",
               f"<p style='color:#64748b;font-size:11px'>source: <code>{html.escape(str(csv_path))}</code> · {len(gens)} rows</p>"]
        if band: sec.append(f"<div style='margin:10px 0'>{img_tag(band, 'fitness band over generations')}</div>")
        if wh:   sec.append(f"<div style='margin:10px 0'>{img_tag(wh, 'weight histogram')}</div>")
        if topo: sec.append(f"<div style='margin:10px 0'>{img_tag(topo, 'best network topology')}</div>")
        if not HAS_MPL:
            sec.append("<p style='color:#b45309'>matplotlib not installed — showing headline only. <code>uv pip install matplotlib</code></p>")
        parts.append("\n".join(sec))

    page = f"""<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Bot Evolution Report</title>
<style>
 body{{font-family: ui-sans-serif, system-ui, -apple-system, Segoe UI, Roboto, Arial; max-width: 980px; margin: 28px auto; padding: 0 18px; color:#0f172a}}
 h1{{font-size:22px; margin: 6px 0}}
 h2{{font-size:15px; color:#0f172a}}
 code{{background:#f1f5f9; padding:1px 5px; border-radius:6px; font-size:12px}}
 .muted{{color:#64748b; font-size:12px}}
</style>
<h1>Clanker — Evolution Report</h1>
<p class="muted">Generated {html.escape(str(Path.cwd()))} · {__import__('datetime').datetime.now().astimezone().isoformat(timespec='seconds')} · docs/research 00..06 · evolved/runs → best.json</p>
{"<hr style='border:none;border-top:1px solid #e2e8f0;margin:14px 0'/>".join(parts) if parts else "<p>No runs.</p>"}
<footer class="muted" style="margin-top:22px">Charts score the headless combat sim (tools/ga/harness.py).</footer>
"""
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(page, encoding="utf-8")
    print(f"report -> {out}")
    return out

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs", nargs="+", required=True, help="evolved/runs/<ts> dirs")
    ap.add_argument("--out", default=None, help="output HTML path")
    args = ap.parse_args()
    runs = [Path(p) for p in args.runs]
    out = Path(args.out) if args.out else (runs[0] / "report.html" if len(runs) == 1 else Path("evolved/report.html"))
    build(runs, out)
