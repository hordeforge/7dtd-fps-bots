"""theme.py: one visual language for the generated GA HTML (report + dashboard).

The GA pages are read by the mod's own maintainers while a training run is
going on: an instrumentation console, not a product page. So the sheet is
built from three decisions and nothing else:

  - one warm near-black ramp (a warm ramp on a cold one is the difference you
    can see without naming it) with a single rust signal hue for anything the
    reader should look at first,
  - two faces (console dark, report light) from the same tokens, so a report
    and a dashboard side by side read as one family,
  - mono for every number, sans for every sentence, and no other ideas.

Matplotlib is a consumer of the same hues (report.py, dashboard.py), so a
chart and the page it sits on cannot drift apart.
"""

from __future__ import annotations

# Console dark face.
BG = "#0f0e0c"
SURFACE = "#171512"
LINE = "#2b2724"
FG = "#e8e2d9"
MUTED = "#a29a8f"

# Rust signal hue. ACCENT draws lines, rules and dots; ACCENT_TEXT carries
# small text (it is a step lighter so 12px labels keep 8:1 on BG).
ACCENT = "#e2762f"
ACCENT_TEXT = "#f2954a"

# Report face: the same ramp inverted onto warm paper.
PAPER = "#f6f3ee"
PAPER_FG = "#1a1714"
PAPER_MUTED = "#5d564d"
PAPER_LINE = "#ddd6ca"

# Chart series. The champion line is the accent, everything else is muted, so
# the reader never hunts for which run is which.
SERIES = ACCENT
SERIES_DIM = "#8c8378"
SERIES_BEST = "#3a332c"

_BASE = """
*, *::before, *::after { box-sizing: border-box; }
body { margin: 0; padding: 0 20px 56px; }
.wrap { max-width: 1080px; margin: 0 auto; }

/* Type scale: 20 / 14 / 13 / 11.5, with mono for every number. */
h1 { font-size: 20px; line-height: 1.25; font-weight: 600; margin: 0; letter-spacing: -.01em; }
h2 { font-size: 13px; margin: 0 0 12px; font-weight: 600; }
p { font-size: 14px; line-height: 1.6; max-width: 68ch; }
.lede { margin: 6px 0 0; }
.meta { font-family: var(--mono); font-size: 12px; }

/* The header is the page's one anchor: title and champion readout on the
   same baseline, so the eye lands on the champion numbers first. */
.head { display: flex; align-items: flex-end; justify-content: space-between; gap: 32px;
  flex-wrap: wrap; padding: 30px 0 20px; border-bottom: 1px solid var(--line); }
.head dl { display: grid; grid-template-columns: auto auto; gap: 3px 14px; margin: 0; text-align: end; }
.head dt { color: var(--muted); }
.head dd { margin: 0; color: var(--accent-text); }
code, .num { font-family: var(--mono); }
.foot { font-size: 11.5px; margin: 40px 0 0; }

/* Sections are separated by a rule, not by a box: a chart, a replay and a
   table are different objects and one card radius around all three says
   nothing about any of them. */
.sec { margin-top: 40px; padding-top: 18px; border-top: 1px solid var(--line); }
.sec .lede { margin-bottom: 18px; }
.sec .grid { margin-top: 18px; }
figure { margin: 0; }
.fig { border: 1px solid var(--line); border-radius: 3px; overflow: hidden; }
.fig img { display: block; width: 100%; height: auto; }
.fig iframe { display: block; width: 100%; height: 430px; border: 0; }
.fig figcaption { font-family: var(--mono); font-size: 12px; color: var(--accent-text);
  padding: 7px 10px; border-bottom: 1px solid var(--line); }
.grid { display: grid; grid-template-columns: 1fr 1fr; gap: 24px; }
@media (max-width: 820px) { .grid { grid-template-columns: 1fr; } }

table { width: 100%; border-collapse: collapse; }
caption { text-align: start; font-size: 11.5px; margin-bottom: 8px; }
th, td { text-align: start; padding: 6px 10px 6px 0; font-size: 13px; }
thead th { font-weight: 600; font-size: 11.5px; letter-spacing: .04em; }
tbody td { font-family: var(--mono); border-top: 1px solid var(--line); }
tbody td:first-child { font-family: inherit; }
"""

DARK_STYLE = f"""
:root {{ --bg: {BG}; --surface: {SURFACE}; --line: {LINE}; --fg: {FG}; --muted: {MUTED};
  --accent: {ACCENT}; --accent-text: {ACCENT_TEXT};
  --sans: ui-sans-serif, system-ui, "Segoe UI", Roboto, sans-serif;
  --mono: ui-monospace, SFMono-Regular, "Cascadia Mono", Menlo, Consolas, monospace; }}
body {{ background: {BG}; color: {FG}; font-family: var(--sans); }}
h1 {{ color: {FG}; }}
h2 {{ color: var(--accent-text); font-family: var(--mono); letter-spacing: .06em; }}
.lede, .foot, caption {{ color: var(--muted); }}
{_BASE}
thead th {{ color: var(--muted); border-bottom: 1px solid var(--line); }}
code {{ background: {SURFACE}; color: {FG}; padding: 1px 5px; border-radius: 3px; font-size: 12px; }}
/* Charts are rendered on white by matplotlib; the plate keeps them from
   floating on the dark page with nothing under them. */
.fig {{ background: #fff; }}
"""

LIGHT_STYLE = f"""
:root {{ --bg: {PAPER}; --surface: #fff; --line: {PAPER_LINE}; --fg: {PAPER_FG}; --muted: {PAPER_MUTED};
  --accent: #b4551a; --accent-text: #9c4a15;
  --sans: ui-sans-serif, system-ui, "Segoe UI", Roboto, sans-serif;
  --mono: ui-monospace, SFMono-Regular, "Cascadia Mono", Menlo, Consolas, monospace; }}
body {{ background: {PAPER}; color: {PAPER_FG}; font-family: var(--sans); }}
h1 {{ color: {PAPER_FG}; }}
h2 {{ color: var(--accent-text); font-family: var(--mono); letter-spacing: .06em; }}
.lede, .foot, caption {{ color: var(--muted); }}
{_BASE}
thead th {{ color: var(--muted); border-bottom: 1px solid var(--line); }}
code {{ background: #ece7de; color: {PAPER_FG}; padding: 1px 5px; border-radius: 3px; font-size: 12px; }}
"""
