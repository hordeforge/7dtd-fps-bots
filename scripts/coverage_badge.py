#!/usr/bin/env python3
"""Render the line-coverage badge SVG from a Cobertura XML report.

Only product sources count: classes whose filename contains the FILTER
substring (default "/Source/") are summed; harness, stub, and test-driver
lines stay out of the denominator.
"""
from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

# shields.io flat-badge threshold colours, highest band first
BANDS = ((90, "#4c1"), (75, "#97ca00"), (60, "#dfb317"), (40, "#fe7d37"))


def colour(pct: int) -> str:
    for floor, fill in BANDS:
        if pct >= floor:
            return fill
    return "#e05d44"


def badge(pct: int, fill: str) -> str:
    lw, vw = 64, 36
    w = lw + vw
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="20"'
        f' role="img" aria-label="coverage: {pct}%">\n'
        f"<title>coverage: {pct}%</title>\n"
        '<linearGradient id="s" x2="0" y2="100%">'
        '<stop offset="0" stop-color="#bbb" stop-opacity=".1"/>'
        '<stop offset="1" stop-opacity=".1"/></linearGradient>\n'
        f'<clipPath id="r"><rect width="{w}" height="20" rx="3" fill="#fff"/></clipPath>\n'
        f'<g clip-path="url(#r)"><rect width="{lw}" height="20" fill="#555"/>'
        f'<rect x="{lw}" width="{vw}" height="20" fill="{fill}"/>'
        f'<rect width="{w}" height="20" fill="url(#s)"/></g>\n'
        "<g fill=\"#fff\" text-anchor=\"middle\""
        ' font-family="Verdana,Geneva,DejaVu Sans,sans-serif" font-size="11">'
        f'<text x="{lw / 2}" y="14">coverage</text>'
        f'<text x="{lw + vw / 2}" y="14">{pct}%</text></g>\n'
        "</svg>\n"
    )


def rate(xmls: list[str], filt: str) -> int:
    hit = total = 0
    for x in xmls:
        for cls in ET.parse(x).getroot().iter("class"):
            if filt not in cls.get("filename", ""):
                continue
            for ln in cls.iter("line"):
                total += 1
                hit += 1 if ln.get("hits", "0") != "0" else 0
    return round(100 * hit / total) if total else 0


def main(argv: list[str]) -> int:
    min_args = 4
    if len(argv) < min_args:
        print(f"usage: {argv[0]} OUTPUT.svg FILTER COBERTURA_XML...", file=sys.stderr)
        return 2
    out, filt, xmls = argv[1], argv[2], argv[3:]
    pct = rate(xmls, filt)
    Path(out).write_text(badge(pct, colour(pct)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
