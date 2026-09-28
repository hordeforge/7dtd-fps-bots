# Contributing

Clanker is a 7 Days to Die dedicated-server mod in C# (`net48`, `LangVersion
7.2`) with a TypeScript web panel. Everything runs from the Makefile; `make
help` lists every target and the README "Development" section explains what
they do.

## Prerequisites

`make preflight` names what `make check` needs and fails with the missing ones:
shellcheck, yamllint, java, bun, curl, and ruff at the version pinned in
`scripts/tool-versions.sh`. Install the pins the way CI does:

```bash
source scripts/tool-versions.sh
uv tool install "ruff==$RUFF_VERSION" "yamllint==$YAMLLINT_VERSION"
```

`make test` additionally needs mono (`mcs` and `mono`). `make build` needs the
game's Managed DLLs, from a Steam install or via `SEVENDTD_DS_DIR` /
`SEVENDTD_GAME_DIR`; it fails with that message rather than a compiler error
when neither is found. No contributor step needs a global install or any state
outside the clone.

## The loop

```bash
make test SUITE=lcg          # one suite while iterating (SUITE="a b" for two)
make test-list               # the suite names SUITE= accepts
make test                    # every suite, about a minute
make ci                      # the full gate: make check then make test
```

CI runs exactly that pair, with mono installed for the C# suites, so a green
`make ci` is what the pull request check will be.

## Tests

A suite is one `run_suite <name> <sources...>` block in
`scripts/test-idempotency.sh`, compiling the engine-free sources it needs plus
a file from `tests/BotMod.Tests/`. A new suite needs two edits in that script:
the name in `all_suites` (which `--list` and the `SUITE=` filter read) and the
`run_suite` call. The script fails the run when one is there without the
other, in either direction, so a half-registered suite is caught locally. The per-file conventions (plain assertions, fuzz suites over a
seeded generator) are visible in any existing `*FuzzTests.cs`.

Suites that need the game DLLs self-skip without a game install and say so. A
skip is not a pass: run them where the game is installed before a change that
touches them lands.

## Generated files

`Source/BotMod/WebMod/bundle.js` is committed and checked by the freshness gate
in `make check`. After editing `bundle.ts`, run `make build` and commit the
regenerated bundle. A version bump is two files in one commit,
`Source/BotMod/Core/BotModVersion.cs` and `Source/BotMod/ModInfo.xml`; the
build fails on drift between them.

## Commits and pull requests

One subject per change, prefixed with its type, as the history does: `fix:`,
`feat:`, `perf:`, `refactor:`, `test:`, `docs:`, `build:`, `ci:`, `chore:`.
Say what changed and why in the body, not what the diff already shows.

Every user-visible change needs a `CHANGELOG.md` entry under `## [Unreleased]`,
filed under the heading that matches its effect. Releases are cut from that
file, in the order `CHANGELOG.md` documents.

Before opening a pull request, `make ci` must be green on your clone. CI
re-runs the same targets on the pull request, so a failure that only appears
there means the local gate was not run.
