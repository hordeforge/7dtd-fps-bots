#!/usr/bin/env python3
"""Repo-root discovery for the GA tools.

Every relative path these tools write (`evolved/runs/...`, `evolved/best.json`,
`docs/ga-dashboard.html`) is anchored here, so the answer must not depend on
where the script was invoked from or on how deep under the root it lives.
"""

from __future__ import annotations

from pathlib import Path

# Present only at the repo root, and in a source export as well as a git clone.
ROOT_MARKER = "Makefile"


def repo_root(start: Path | None = None) -> Path:
    """Nearest ancestor of `start` holding ROOT_MARKER.

    Raises instead of guessing: a wrong root would scatter training output
    under an arbitrary directory rather than fail.
    """
    here = (start or Path(__file__)).resolve()
    for candidate in (here, *here.parents):
        if (candidate / ROOT_MARKER).is_file():
            return candidate
    raise RuntimeError(f"no {ROOT_MARKER} in {here} or any parent; cannot locate the repo root")


def demo() -> None:
    root = repo_root()
    assert (root / ROOT_MARKER).is_file()
    assert (root / "tools" / "ga" / "paths.py").is_file()
    assert repo_root(Path(__file__).resolve().parent) == root
    try:
        repo_root(Path(Path.home().anchor))
    except RuntimeError:
        pass
    else:
        raise AssertionError("repo_root must raise when no marker is found")
    print(f"paths: ok ({root})")


if __name__ == "__main__":
    demo()
