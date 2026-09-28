#!/usr/bin/env python3
"""The `--version` every GA CLI answers, read from the mod's own constants.

A genome is only loadable by the mod build that declares the same shape and
activation, so "which mod were these weights written for" is a question the
artifacts must answer. evolved/best.json records the activation and the CHANGELOG
records a shape bump, but nothing printed the version the tooling implemented.

BotModVersion.Number is the single source of truth (see the C# file, the
build.sh drift guard and ModInfo.xml, which the build keeps in step). The same
sed extraction runs in scripts/package.sh, so the two readers cannot disagree
on what the constant says. A copy of tools/ without Source/ beside it has no
version to report, and says so rather than failing --version.
"""

from __future__ import annotations

import argparse
import re
from pathlib import Path

VERSION_CS = (Path(__file__).resolve().parent.parent.parent
              / "Source" / "BotMod" / "Core" / "BotModVersion.cs")
UNKNOWN_VERSION = "unknown"


def mod_version() -> str:
    """BotModVersion.Number, or `unknown` when the constant cannot be read."""
    try:
        text = VERSION_CS.read_text(encoding="utf-8")
    except OSError:
        return UNKNOWN_VERSION
    # The trailing .* matters: without it the match takes the longest one, so a
    # `;` comment or any text after the constant leaks into the version.
    m = re.search(r'const string Number = "([^"]*)"', text)
    return m.group(1) if m else UNKNOWN_VERSION


def add_version_argument(parser: argparse.ArgumentParser) -> argparse.ArgumentParser:
    """`--version` on a GA CLI: the mod version and the genome shape a weight
    file has to match. `import ga` is deferred so `--version` and `--help` do
    not pay for NumPy and numba."""
    parser.add_argument(
        "--version", action="version",
        version=f"clanker-ga (mod {mod_version()}, "
                f"genome {ga_shape()})")
    return parser


def ga_shape() -> str:
    """The 14-16-5 network shape the genome contract in ga.py fixes."""
    import ga
    return f"{ga.INPUTS}-{ga.HIDDEN}-{ga.OUTPUTS}"
