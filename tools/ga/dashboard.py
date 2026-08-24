#!/usr/bin/env python3
"""dashboard.py — one polished, self-contained training dashboard for the GA evolutions.

Assembles, into a single HTML file:
  1. Evolution curves — every run overlaid (best/mean) with IQR band + current champion
     highlighted, plus a champion-is-best callout.
  2. Held-out stability strip — per-run final held (seed 999) ranked.
  3. Neural-net controller viz (reuse viz.py's diagram, embedded as a PNG).
  4. Arena replays — top-down canvas matches of the champion on multiple seeds/arenas
     (reuse replay.py; embedded as inline HTML frames).
  5. Per-run summary table (pop/gen/curriculum/islands/held/verdict).

Usage:
  python tools/ga/dashboard.py [--runs run1 run2 ... | --all] --out docs/ga-dashboard.html
"""

from __future__ import annotations

import argparse
import base64
import csv
import html
import io
import json
import tempfile
from pathlib import Path

import numpy as np

# matplotlib is optional if no PNGs are wanted, but the dashboard is much richer with it.
try:
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    HAS_MPL = True
except Exception:  # pragma: no cover
    HAS_MPL = False

TOOLS = Path(__file__).resolve().parent          # repo/tools/ga
REPO = TOOLS.parent.parent                        # repo root
RUNS_DIR = REPO / "evolved"                       # repo/evolved
import sys as _sys
_sys.path.insert(0, str(TOOLS))
from replay import record_match, render_html  # noqa: E402
from viz import draw as draw_net  # noqa: E402


def fig_b64(fig) -> str:
    """Palette-optimized PNG as base64. Charts are flat-color figures, so an
    adaptive 256-color palette keeps them visually identical at ~3-4x smaller
    (the embedded charts are nearly all of this file's weight)."""
    bio = io.BytesIO()
    fig.savefig(bio, format="png", dpi=150, bbox_inches="tight")
    plt.close(fig)
    raw = bio.getvalue()
    try:
        from PIL import Image
    except ImportError:
        return base64.b64encode(raw).decode()
    im = Image.open(io.BytesIO(raw))
    if im.mode != "RGBA":
        im = im.convert("RGBA")
    q = im.quantize(colors=256, method=Image.Quantize.FASTOCTREE, dither=Image.Dither.NONE)
    out = io.BytesIO()
    q.save(out, "PNG", optimize=True)
    return base64.b64encode(out.getvalue()).decode()


def png_dimensions(data_b64: str) -> tuple[int, int]:
    """Intrinsic pixel size from the base64 PNG's IHDR chunk (no image lib).
    The IHDR ends at byte 24, i.e. base64 char 32."""
    import struct
    raw = base64.b64decode(data_b64[:32])
    w, h = struct.unpack(">II", raw[16:24])
    return w, h


def chart_card(data_b64: str, alt: str) -> str:
    w, h = png_dimensions(data_b64)
    return (f'<div class="card"><img alt="{alt}" src="data:image/png;base64,{data_b64}"'
            f' width="{w}" height="{h}" loading="lazy" decoding="async"'
            f' style="max-width:100%;height:auto"></div>')


def load_run_csv(run: Path):
    # Bounded open: this helper runs three times per run per build (curves,
    # held strip, run table), so each read must release its own descriptor.
    with open(run / "fitness.csv", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        return [], [], [], [], [], []
    gens = [int(r["gen"]) for r in rows]
    best = [float(r["best"]) for r in rows]
    mean = [float(r["mean"]) for r in rows]
    q25 = [float(r.get("q25") or r["mean"]) for r in rows]
    q75 = [float(r.get("q75") or r["mean"]) for r in rows]
    held = []
    for r in rows:
        hv = r.get("held")
        if hv is None or hv == "" or hv == "nan":
            held.append(float("nan"))
        else:
            try:
                held.append(float(hv))
            except ValueError:
                held.append(float("nan"))
    return gens, best, mean, q25, q75, held


def curves_b64(runs, best_run_name: str | None):
    fig, ax = plt.subplots(figsize=(12, 5.2))
    for run in runs:
        gens, best, mean, q25, q75, _ = load_run_csv(run)
        if not gens:
            continue
        label = run.name.replace("evolved/runs/", "")
        if run.name == best_run_name:
            ax.plot(gens, best, color="#0ea5e9", lw=2.2, label=f"{label} (BEST)")
            ax.fill_between(gens, q25, q75, color="#0ea5e9", alpha=0.10)
        else:
            ax.plot(gens, best, color="#64748b", lw=1.0, alpha=0.75, label=label)
    ax.set_xlabel("generation", fontsize=10)
    ax.set_ylabel("fitness (scalar)", fontsize=10)
    ax.set_title("Evolution — best fitness per generation (all runs)", fontsize=13)
    ax.grid(True, alpha=0.15)
    ax.legend(fontsize=7, ncols=3, frameon=False, loc="lower right")
    fig.tight_layout()
    return fig_b64(fig)


def held_strip_b64(runs):
    # final held per run (seed 999, last non-nan entry)
    fig, ax = plt.subplots(figsize=(12, 2.8))
    labels, helds = [], []
    for run in runs:
        _, _, _, _, _, held = load_run_csv(run)
        vals = [v for v in held if v == v]  # drop nan
        if not vals:
            continue
        labels.append(run.name.replace("evolved/runs/", ""))
        helds.append(vals[-1])
    if not helds:
        return ""
    order = np.argsort(helds)[::-1]
    labels = [labels[i] for i in order]
    helds = [helds[i] for i in order]
    cols = ["#0ea5e9"] + ["#94a3b8"] * (len(helds) - 1)
    ax.bar(range(len(helds)), helds, color=cols, alpha=0.9)
    ax.set_xticks(range(len(helds)))
    ax.set_xticklabels(labels, rotation=45, ha="right", fontsize=7)
    ax.set_ylabel("held (seed999)")
    ax.set_title("Held-out stability — final held per run (champion on the left)")
    ax.grid(True, axis="y", alpha=0.2)
    fig.tight_layout()
    return fig_b64(fig)


def best_net_b64():
    best = json.loads((RUNS_DIR / "best.json").read_text(encoding="utf-8"))
    w = np.array(best["weights"], dtype=float)
    hidden = int(best.get("hidden", 16))
    png = RUNS_DIR / "sweeps" / "viz_champion_dashboard.png"
    png.parent.mkdir(parents=True, exist_ok=True)
    # render to a matplotlib figure via viz.draw (saves PNG); embed that PNG as b64.
    draw_net(w, hidden, 14, title="Champion controller", out=png)
    return base64.b64encode(png.read_bytes()).decode()


def build(runs, out: Path, replays):
    best_meta = json.loads((RUNS_DIR / "best.meta.json").read_text(encoding="utf-8"))
    best_run_name = None
    # best.run is the run hash; we mark whichever run we think produced best.json
    for run in runs:
        cfg = json.loads((run / "config.json").read_text(encoding="utf-8"))
        if cfg.get("seed") == best_meta.get("seed"):
            best_run_name = run.name

    chunks = []
    chunks.append("""<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Bot Evolution Dashboard</title>
<style>
 body{font-family:ui-sans-serif, system-ui, Segoe UI, Roboto, Arial;background:#0b1220;color:#e2e8f0;margin:0}
 .wrap{max-width:1100px;margin:24px auto;padding:0 16px}
 h1{font-size:22px} h2{font-size:16px;margin-top:34px;color:#38bdf8}
 .chip{display:inline-block;background:#1e293b;border:1px solid #334155;border-radius:20px;padding:4px 12px;font-size:12px;margin:2px}
 .grid{display:grid;grid-template-columns:1fr 1fr;gap:16px}
 .card{background:#111827;border:1px solid #1f2937;border-radius:12px;padding:14px}
 .card img,.card iframe{width:100%;border-radius:8px}
 iframe{border:0;background:#0f172a}
 table{width:100%;border-collapse:collapse;font-size:12px}
 th,td{text-align:left;padding:6px 8px;border-bottom:1px solid #1f2937}
 th{color:#38bdf8}
 .b{color:#38bdf8;font-weight:700}
</style></head><body><div class="wrap">
<h1>Bot Evolution Dashboard</h1>
<p style="color:#94a3b8;font-size:13px">Neuroevolution of the FPS bot controller — 14&rarr;16&rarr;5 MLP, genetic algorithm, held-gated promotion.</p>
<div>
  <span class="chip">Champion gen <b class="b">""")
    # best.meta.json travels via git (whitelisted in evolved/.gitignore), so
    # its values are untrusted text from the dashboard's perspective: escape
    # before they land in the page.
    chunks.append(html.escape(str(best_meta.get("generation", "?"))))
    chunks.append("""</b></span>
  <span class="chip">train <b class="b">""")
    chunks.append(f"{best_meta.get('fitness',0):.1f}")
    chunks.append("""</b></span>
  <span class="chip">hash <b class="b">""")
    chunks.append(html.escape(str(best_meta.get("configHash", "?"))[:8]))
    chunks.append("""</b></span>
</div>
""")

    if HAS_MPL:
        cd = curves_b64(runs, best_run_name)
        hs = held_strip_b64(runs)
        net = best_net_b64()
        # Each chart degrades independently: held_strip_b64 returns "" when no
        # run recorded held-out scores, and feeding "" through chart_card would
        # crash the whole dashboard on the PNG header parse.
        if cd:
            chunks.append(f"""<h2>1 · Evolution curves</h2>{chart_card(cd, f'Line chart of the best fitness per generation across {len(runs)} runs; the champion run is highlighted')}""")
        if hs:
            chunks.append(f"""<h2>2 · Held-out stability</h2>{chart_card(hs, 'Bar chart of the final held-out score per run, champion run on the left')}""")
        if net:
            chunks.append(f"""<h2>3 · Champion controller (14&rarr;16&rarr;5)</h2>{chart_card(net, 'Diagram of the champion controller neural network: 14 inputs, 16 hidden units, 5 outputs')}""")

    # Arena replays
    if replays:
        chunks.append('<h2>4 · Arena replays (top-down, live)</h2>')
        chunks.append('<div class="grid">')
        encoded = {label: base64.b64encode(html.encode()).decode() for label, html in replays.items()}
        for label in replays:
            chunks.append(f'<div class="card"><div style="font-size:13px;margin-bottom:6px;color:#38bdf8">{label}</div><iframe id="f{abs(hash(label))%9999}" title="Arena replay {label}" style="width:100%" height="430"></iframe></div>')
        chunks.append('</div>')
        # Set srcdoc via JS so large embedded HTML/payloads don't need escaping in attributes.
        chunks.append("<script>")
        chunks.append("const fr = {};")
        for label, b64 in encoded.items():
            chunks.append(f'fr["{abs(hash(label))%9999}"] = "{b64}";')
        chunks.append("""function deb64(s){ const bin=atob(s); const u8=new Uint8Array(bin.length); for(let i=0;i<bin.length;i++) u8[i]=bin.charCodeAt(i); return new TextDecoder().decode(u8); }
for(const k in fr){ const el=document.getElementById('f'+k); if(el) el.srcdoc=deb64(fr[k]); }
</script>""")

    # Run table
    rows = []
    for run in runs:
        cfg = json.loads((run / "config.json").read_text(encoding="utf-8"))
        _, _, _, _, _, held = load_run_csv(run)
        heldv = [v for v in held if v == v]
        rows.append((run.name.replace("evolved/runs/", ""),
                     cfg.get("pop"), cfg.get("gens"), cfg.get("curriculum"),
                     cfg.get("islands"), f"{heldv[-1]:.2f}" if heldv else "—"))
    rows.sort(key=lambda r: float(r[5]) if r[5] != "—" else 0, reverse=True)
    chunks.append("""<h2>5 · Runs</h2><div class="card"><table><caption style="text-align:left">Summary of every GA run: population, generations, curriculum, islands, final held-out score</caption><thead><tr><th scope="col">run</th><th scope="col">pop</th><th scope="col">gens</th><th scope="col">curriculum</th><th scope="col">islands</th><th scope="col">held</th></tr></thead><tbody>""")
    # Every cell is filesystem/config text (run dir names, hand-editable
    # config.json values), so it is HTML-escaped before it lands in the page:
    # a crafted run name must not execute in the browser of whoever opens the
    # generated dashboard.
    for r in rows:
        cells = "".join(f"<td>{html.escape(str(c))}</td>" for c in r)
        chunks.append(f"<tr>{cells}</tr>")
    chunks.append("</tbody></table></div>")
    # Footer note in #94a3b8 (not the dimmer #64748b): it must keep 4.5:1
    # contrast on the dark page background.
    chunks.append(f"""<p style="color:#94a3b8;font-size:11px;margin-top:30px">Dashboard generated for {len(runs)} runs. Replays are deterministic (same seed == same match) and follow the pre-R10 sim rules.</p></div></body></html>""")

    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text("".join(chunks), encoding="utf-8")
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs", nargs="*", default=None)
    ap.add_argument("--all", action="store_true", help="use every run in evolved/runs")
    ap.add_argument("--out", default="docs/ga-dashboard.html")
    ap.add_argument("--replays", action="store_true", help="include arena replays (deterministic, ~seconds)")
    args = ap.parse_args()

    if args.all or not args.runs:
        runs = sorted(RUNS_DIR.glob("runs/*"))
    else:
        runs = [RUNS_DIR / r for r in args.runs]
    missing = [str(r) for r in runs if not r.is_dir()]
    if missing:
        raise SystemExit(f"--runs dir not found: {missing[0]} (paths are relative to {RUNS_DIR}, e.g. runs/<ts>)")
    for req in ("best.json", "best.meta.json"):
        if not (RUNS_DIR / req).is_file():
            raise SystemExit(f"{RUNS_DIR / req} not found (run tools/ga/evolve.py first)")

    w = np.array(json.loads((RUNS_DIR / "best.json").read_text(encoding="utf-8"))["weights"], dtype=float)
    replays = {}
    import replay as _rp
    if args.replays:
        # Scratch replay HTML lives only long enough to be read back into the
        # dashboard string; the temp dir is removed on success and on failure.
        with tempfile.TemporaryDirectory(prefix="ga-dashboard-replay-") as tmp:
            for label, (seed, nb, nz, envf) in {
                "seed 1 · cross": (1, 4, 3, 1),
                "seed 777 · maze": (777, 5, 2, 4),
                "seed 1234 · maze": (1234, 4, 3, 4),
                "seed 42 · corridor": (42, 4, 3, 3),
            }.items():
                summary, frames = record_match(w, seed, nb, nz, 1200, 3, 0, envf)
                walls = _rp.WALLS[envf]
                html_path = Path(tmp) / f"replay_seed{seed}.html"
                render_html(summary, frames, walls, html_path, label)
                replays[label] = html_path.read_text(encoding="utf-8")

    out = build(runs, Path(args.out), replays)
    print(f"dashboard -> {out}  ({len(runs)} runs, {len(replays)} replays)")


if __name__ == "__main__":
    main()
