# Changelog

All notable changes to BotMod are documented here. The project is 0.x: minor
releases may add features and behavior changes (including breaking ones),
patch releases are fixes only. Upgrade by replacing `Mods/BotMod/` with the
new build; your operator state in `Config/botmod.json` is preserved and
missing keys fall back to defaults.

The version lives in `Source/BotMod/Core/BotModVersion.cs` (canonical) and
must match `<Version>` in `Source/BotMod/ModInfo.xml`; `scripts/build.sh`
fails on drift between them.

Cutting a release, in this order:

1. Rename `## [Unreleased]` to `## [<version>]` with the date, and fix the
   `###` grouping for what actually shipped. Anything left in `[Unreleased]`
   is still on the next one.
2. Set `BotModVersion.Number` and the `ModInfo.xml` `<Version>` to that
   version.
3. `make ci`, then tag `v<version>` and attach `make package`'s zip.
   `.github/workflows/release.yml` rejects a tag that disagrees with
   `ModInfo.xml`, that has no notes in this file, that carries a second
   release tag on one commit, or that moves a tag already published. A
   published version is immutable, so a rebuild that has to change anything
   is a new version, not a moved tag.

## [Unreleased]

### Breaking

Under the 0.x policy above, these change what an existing consumer sees.

- `AimJitterDegrees`, `BurstMin`, `BurstMax` and `BurstPauseSec` are gone from
  `botmod.json`. No code path read them: aim tightness comes from the
  character's `AimAccuracy` (`Bot.AdoptTarget`, `Bot.AttackInRange`) and burst
  shape, spread, damage and magazine pacing from the per-weapon
  `WeaponProfile`, so a tuned value read as a live combat setting and changed
  nothing. `Normalize` clamped them, the difficulty preset authored
  `AimJitterDegrees`, and `bot skill` reported the new jitter as a consequence,
  so the file, the startup dump and the console all described a knob with no
  effect behind it. A `botmod.json` that still carries the keys logs the
  existing unknown-key warning on load and is otherwise unaffected; the
  effective dump no longer lists them. The difficulty preset still moves
  reaction time, headshot chance and the vision/attack ranges, and still
  lerps the per-character traits on the next spawn.
- `POST /api/bot` now binds an idempotency `requestId` to the request it was
  issued for. Before: a retry under a live key whose body differed was answered
  with the earlier response and the new operation was dropped. Now: the
  mismatch is `409 REQUEST_ID_REUSED`, and the request that owns the key still
  replays. A client that reused one key per session for a varying body has to
  send a fresh key per logical request.
- Every `/api/bot` response, on both verbs and on 200/400/409/500, now carries
  `Cache-Control: no-store` and `X-Content-Type-Options: nosniff` (the body
  contract is documented in the README). Before: the cache header was absent,
  so an intermediary or the browser HTTP cache could hand back a stale roster,
  and no security header was sent at all, so a browser pointed at `/api/bot`
  could sniff a body as HTML. Nothing in the shipped panel depended on
  caching, but an external consumer reading the scoreboard through a shared
  cache has to revalidate.
- `BotNeuralBrain.TryLoad` now rejects any champion whose `activation` is not
  `tanh`, and a champion declaring a hidden width outside 1..4096. Before: such
  a file loaded and every network output was wrong, with nothing logged. An
  operator whose `evolved/best.json` came from a `--activation relu` run gets
  the rejection message and the heuristic brain until a tanh champion is
  promoted into that slot.

### Changed

- `make install` renames the running mod dir aside instead of deleting it, and
  puts it back if the swap fails, so a failed rename no longer leaves the
  server with no `Mods/BotMod`. `make install` and `make uninstall` also take a
  shared deploy lock (`Mods/.botmod-deploy.lock`, dot-prefixed so the mod
  loader skips it), so the two cannot rewrite the same dir at once; a lock
  left by a killed run is taken over rather than wedging the next one.
- `make restore SNAPSHOT=...` now snapshots the live config before it
  overwrites it (same `backup-state.sh` path, same `BOTMOD_STATE_BACKUP_DIR`),
  so a restore that fails part way does not destroy the state it was replacing.
- `combat_sim.lcg01` and `combat_sim.loadout_pick` dropped their leading
  underscore. `determinism_check.py` already called both as a second consumer
  (the loadout draw exists to be checked outside the kernel), and the SLF
  rule group in `ruff.toml` made those three calls a red `make lint-python`.
- `BotBrain.Strafe` and `BotBrain.Backpedal` share one `LateralStep` helper
  that takes the forward/side blend, and `BotCombat`'s two kill-log labels go
  through one `Label` helper. Same moves, same messages, one definition each.
- The byte-level mutant generator and its `ExtremeValues` table moved from the
  three JSON config fuzzers into `tests/BotMod.Tests/MutantBytes.cs`, wired
  into the `neuralfuzz`, `configfuzz` and `charfuzz` compiles. The three
  copies had already drifted (one used a named local where two inlined it).
- The build, package, test and HTML-lint scripts collect their file lists with
  a `while read` loop instead of `mapfile`. `mapfile` needs bash 4, and macOS
  ships 3.2, so `make build`, `make package`, `make lint-html` and
  `make test` all failed there with `mapfile: command not found` even though
  the scripts carry deliberate BSD branches (`shasum` in `digest.sh`, `date -r`
  in `package.sh`). No output changes: both forms split on newlines only, and
  the lists hold no filename with a newline in it.
- `Makefile` resolves its recipe shell through `PATH` (`command -v bash`)
  rather than hardcoding `/bin/bash`, which is absent wherever bash is
  installed under a prefix (Nix, Homebrew, some conda layouts). Every script
  here is bash, so a host with no bash still fails, now with make's own
  missing-shell error rather than a wrong-shell one.
- CI gained a `macos-15` leg running `make lint-shell` and `make test-recovery`.
  Those BSD branches existed but no job ran them, so a BSD-only regression in
  the digest, install or backup paths could not be caught. The C# suites are
  left out of the leg: they need mcs and mono, which the macOS runners do not
  ship, and every suite they would run there self-skips.
- The dashboard's in-flight state is a boolean rather than a composed label
  string that nothing displayed; every consumer only ever tested it for
  emptiness. The composed label and its `optNum` helper are gone, and
  `aria-busy` now gets the boolean the ARIA attribute is defined to take.
  Alongside it, the `numOr(x, 0)` calls that duplicated the existing `num`
  helper read as `num(x)`. Together these brought `WebMod/bundle.js` from
  14489 bytes to 14307, back under the 14336 wire budget the freshness gate
  enforces; at 14489 `make check` was failing on every run.
- The canonical body text an idempotency `requestId` is bound to is now
  length-prefixed per field instead of `key=value` lines. A value containing
  the separator could spell a second body exactly (`{"player":"b\nc=d"}`
  rendered like `{"player":"b","c":"d"}`), and the ledger reads a matching
  fingerprint as the same request, so the second body would have been
  answered with the first one's recorded response.
- `evolve.py --resume` now refuses a checkpoint that was measured on a
  different stick (`activation`, `curriculum`, `seed`, scalarization mix) and
  exits 2 instead of merging it. Checkpoints carry those four fields from now
  on; a checkpoint predating them is carried with a warning. Before: a resumed
  run compared its new fitness against a `best_fitness` from another
  activation or mix, so `improved` never fired again (no further checkpoint,
  a permanent stagnation plateau) and the carried pre-resume `fitness.csv` rows
  were plotted on an axis they were never scored on. The stick is read before
  the run dir is created, so a refused resume leaves nothing behind.
- `tools/ga/dashboard.py` reads the champion's generation, fitness and config
  hash from `evolved/best.json` instead of `evolved/best.meta.json`, and uses
  the meta file only for the run seed, and only when its `configHash` matches
  the champion's. Before: the two are written as two atomic replacements, so a
  crash between them (or a meta left from an older promote) showed the
  previous champion's numbers and highlighted the wrong run.
- Developer path: `make test SUITE="lcg bottext"` (the form the README
  documented) failed with `No rule to make target 'bottext'`, because make
  read the second suite as a target. Recipes now run under bash explicitly, and
  `make preflight` fails when the installed ruff or yamllint is not the version
  CI pins in `scripts/tool-versions.sh`, so a lint run that disagrees with CI
  is caught before a push. Added `CONTRIBUTING.md`: prerequisites, the loop,
  how a new suite is registered in `scripts/test-idempotency.sh`, and what a
  pull request has to carry.
- The R0/R1 sweep artifacts (`sweep_42.json`, `sweep_H24_g20_s42.png`,
  `sweep_H32_g30_s42.png`, `sweep_H40_g40_s42.png`,
  `sweep_combat_H40_g40_s42.json`) moved from `tools/ga/sweeps/` to
  `evolved/sweeps/`, the location `evolved/README.md` documents and every
  later report cites. Two directories held the same kind of tool output; the
  one inside the tooling source tree was the outlier, and `sweep.py` no longer
  writes there. The four R0..R2 reports were repointed.
- `scripts/lint-webui.sh` picked its own SHA-256 command
  (`sha256sum`, else `shasum -a 256`) for the anti-slop archive digest. It now
  sources `scripts/digest.sh` like the four other digest callers, so the pick
  exists once and a host with neither tool fails loud instead of reaching a
  confusing digest mismatch.
- `Source/BotMod/Config/` held four engine-free primitives that have nothing
  to do with configuration and are used by every layer (`Lcg`, `BotText`,
  `LogSanitizer`, `AtomicTextFile`). They moved to `Source/BotMod/Foundation/`
  under namespace `BotMod.Foundation`, which sits below `Config/` (nothing in
  it references a config type). `Config/` is now only the operator-config
  layer: `BotConfig`, `BotCharacter`, `WeaponProfile`, `CombatGates`. No
  behavior change.
- `tests/BotMod.Web.Tests/` was renamed `tests/BotMod.Tests/`: it holds
  suites for every layer (Config, Foundation, AI, Web, Commands), not just the
  web API.
- `Core/BotManager.cs`, `Patches/BotPatches.cs`, `ModApi.cs` and two test
  files were reaching into `BotMod.Config` with fully qualified names instead
  of a `using`; they now match the rest of the tree.
- The shipped web dashboard panel (`Mods/BotMod/WebMod/bundle.js`) is minified
  at build time by `scripts/webmod-minify.sh` (terser, pinned in
  `scripts/tool-versions.sh`): 24,583 -> 13,844 bytes. The stock webserver
  serves it uncompressed, so that is the download every admin panel open paid;
  the panel now fits one initial congestion window instead of needing a second
  round trip. `make lint-webui` compares the committed bundle against a fresh
  compile run through the same minifier, and holds both `bundle.js` (14 KiB)
  and `styling.css` (12 KiB) to a wire budget.

### Added

- Every `POST /api/bot` audit line names the caller: `user@ip` for a browser
  session, `api-token <name>` for a token-authenticated call (which has no
  connection at all), and the line survives every outcome, so 200, 400, 409
  and 500 all say who. Before: the lines carried the request tag and the
  response, never the principal, so a stolen webtoken's use was
  unattributable and a shared admin account left no trace.
- Every `bot` console subcommand outside the read-only set logs its issuer
  (`bot cmd remove by entity 42`) before dispatch, including every alias
  (`rm`, `kick`, `clear`). Before: only `bot spawn` and `bot player` logged,
  so removals, team and config changes, `bot reload` and the enable/disable
  toggles left a mutation with no record of who made it. The audit line is
  emitted above the dispatch switch, so a subcommand added later is logged by
  default; only naming one read-only silences it. An operator watching the
  server log now sees one extra line per console mutation.
- `make coverage` runs `scripts/coverage-cs.sh` (line coverage of the
  pure-BCL suites into `coverage.cobertura.xml`). The script and its badge
  renderer were committed with no entry point and no documentation; it needs
  the dotnet SDK plus `dotnet-coverage` and self-skips with a message when
  either is missing.
- `BOTMOD_CONFIG` overrides the config path for both the read and the persist
  side, so a deployment that mounts `botmod.json` outside the mod directory
  (a container config map, a read-only image with a writable copy elsewhere)
  no longer has to also mount it into `Config/`. Unset or blank means the path
  beside the assembly, as before. `characters.json` is looked up next to
  whichever `botmod.json` is in use, and a `BOTMOD_CONFIG` that resolves to
  nothing logs a WARN naming every path tried instead of silently running on
  the C# property defaults.
- `bot config` (alias `cfg`) prints the file the server read and the effective
  values after `Normalize` clamped them, including fields the file never set
  and values the difficulty preset moved. The same dump is logged at startup
  and on `bot reload`. `bot status` still answers the six fields an admin
  watches per tick.
- `make backup` snapshots the recovery surface (deployed `botmod.json` and its
  `.bak`, or the `BOTMOD_CONFIG` file, plus the champion weights) into
  `$BOTMOD_STATE_BACKUP_DIR/<utc-timestamp>/`, git-ignored by default at
  `backups/`. `make verify-snapshot` checks the `MANIFEST` digests and writes
  nothing; `make restore` verifies first, then writes. RPO/RTO per failure
  mode is in `docs/recovery.md`.
- `bot players` lists the online players as `name#id`, the identifiers
  `bot player` matches on. It replaces the roster the failed-lookup message
  used to print: a mistyped name now discloses nothing about who else is
  connected, and asking for the roster is a separate, deliberate command.
- `make test SUITE=<name>` (names from `make test-list`, or
  `scripts/test-idempotency.sh <name> ...`) runs a single C# suite instead of
  all of them, and `scripts/test-idempotency.sh` now names the missing tool
  when mcs or mono is absent. The script fails if its suite list drifts from
  the `run_suite` calls.
- `make ci` runs the full local gate (`make check` plus `make test`), and
  `make preflight` names the tools `make check` needs.
- `tests/BotMod.Tests/BotTextFuzzTests.cs` fuzzes the identity-text layer
  (`Canon`, `WithoutInvisible`, `IdentityKey`, `BaseName`, `NameMatches`) with
  lone surrogates, hostile UTF-8, invisible and combining characters, seeded
  with the string literals of the shipped config files, and asserts the
  contract (totality, fixed points, NFC output, NFC/NFD key equality) rather
  than only the absence of a throw.
- `tests/BotMod.Tests/BotAdminSettersFuzzTests.cs` fuzzes the admin setters
  the web API and console share: every `setTeam` write must be readable back
  under each spelling of that name, an unknown `vs` target must change no
  flag, and the difficulty and team-count setters must clamp at both ends of
  their ranges.
- `tests/BotMod.Tests/WeaponProfileFuzzTests.cs` fuzzes the gun-id classifier
  whose answer is every bot's fire rate, burst shape, damage, range and
  magazine pacing. It asserts that a client-supplied id (web `spawnNear`,
  `bot player`, `bot weapon`) never throws, never comes back as a different
  gun than the one asked for, never yields a non-finite or non-positive stat,
  and that the `mixed` expansion stays inside the `LoadoutPool` and is a pure
  function of the seed. The fixed-vector `WeaponProfileTests.cs` had no
  `run_suite` call behind it and had not been running; it runs as
  `make test SUITE=weaponprofile` now.
- `Source/BotMod/Foundation/SpawnPointXml.cs` is the world `spawnpoints.xml`
  reader, extracted from `BotSpawner` so the parse is testable without a
  running server, with
  `tests/BotMod.Tests/SpawnPointXmlFuzzTests.cs` fuzzing it (seed corpus,
  byte-level mutants, entity-expansion and billion-laughs documents, a
  comma-decimal host culture). A coordinate the reader accepted as `NaN` or
  `Infinity` would have placed a bot at an undefined position; those are now
  rejected like any other unusable coordinate.
- `make lint-yaml` runs `yamllint --strict` over `.github/workflows` and joins
  `make check`, so the CI definitions are held to the same blocking bar as the
  shell, Python, TypeScript and HTML sources. Config: `.yamllint.yml`; the
  version is pinned in `scripts/tool-versions.sh` next to ruff's.
- `ruff` now selects `B` (flake8-bugbear) in `ruff.toml`. The eight `zip()`
  calls over parallel-length sequences pass `strict=True`, so a length
  mismatch raises instead of silently truncating, and the two loop counters
  that were never read are retired.
- `ruff` selects six more whole categories in `ruff.toml`, all proven clean
  over `tools/ga` and `scripts` in the same change: `S` (flake8-bandit, the
  security group, previously never switched on), `PTH` (`open()` and
  `os.replace()` go through `Path`), `PERF`, `C4`, `ERA` (no commented-out
  code) and the five groups that had no findings at all (`ASYNC`, `DTZ`,
  `SLF`, `T10`, `G`). The three `S` findings are rule-scoped `noqa`s with
  their reason: two asserts that are numba kernel guards, and one
  `ElementTree.parse` of this repo's own Cobertura output. `line-length` is
  now written down as the 100-column cap the debt count in `ruff.toml` is
  measured against.
- `make lint-python` passes again. Selecting `SLF` in the same change that
  promoted it left `tools/ga/determinism_check.py` red on three
  `combat_sim._lcg01` / `combat_sim._loadout_pick` calls, so `ruff check .`
  failed on a clean tree. Reaching into the kernel is what that probe is for,
  so each site now carries a rule-scoped `noqa` saying so.
- `ruff` selects four more whole categories in `ruff.toml`, each proven clean
  over `tools/ga` and `scripts` in the same change: `EXE` (flake8-executable),
  `PT` (flake8-pytest-style), `FLY` (flynt) and `FURB` (refurb). `EXE` is why
  the six shebanged `tools/ga` entry points that sat at mode 644 next to
  `evolve.py`'s 755 are now all executable.
- `ruff.toml` records the rule groups that are still out, each with its
  measured finding count (`PL` 109, `ANN` 207, `D` 115, `E7` 145, `T20` 58,
  `TRY` 19, `N` 18, `EM` 16, `UP` 16, `FBT` 14, `CPY` 12, `SIM` 9, `I` 9,
  `BLE` 8, `RUF` 13, `E501` 98), so a rule cannot be promoted with the debt
  silently growing back behind it.

### Performance

- The GA dashboard's arena replays are decoded and handed to their iframes
  when the card scrolls into view, not all four at parse time: a
  `--replays` build spent ~343 KB of `atob` on the main thread and started
  four canvas animations nobody had scrolled to. The payload script also
  moved to the end of the document, so the run table above it is reached
  after ~119 KB instead of ~463 KB. Iframe ids are the frame's position
  rather than `abs(hash(label)) % 9999`, which was salted per process, so
  the same runs produced different ids on every build and a label collision
  left one card blank.

### Fixed

- The shipped `WebMod/bundle.js` was 14489 bytes, over the 14336-byte wire
  budget `scripts/lint-webui.sh` enforces, so `make check` failed on a clean
  tree. The read-only note span that ends each control row and the localized
  number formatters now live in one helper each, which brings the bundle to
  14289 bytes with the same output.
- `scripts/lint-webui.sh` checked the anti-slop plugin archive against its
  pinned SHA-256 but extracted it only when the extracted tree was missing, so
  an edited or truncated `anti-slop-src` in the tool cache was linted against
  without a digest check. The tree is now rebuilt from the verified archive on
  every run and swapped in only once the extraction succeeded.
- Editing `LoadoutPool` shifted every bot name and spawn point picked after
  it. `BotSpawner.PickWeapon` expanded the "mixed" literal against the
  spawner's own LCG, the same stream that picks the name and the spot, so a
  gun pick consumed a draw those two later reads depended on, and
  `WeaponProfile`'s salted mixed-loadout counter (reseeded per world for
  exactly this) was never reached from the spawn path. The expansion now has
  one owner, `WeaponProfile.ForGun`, and the salted counter is the live one.
- `BotManager` kept two structures over the same ids, a `List<Bot>` and a
  `HashSet<int>` beside a `Dictionary<int, Bot>`, and every add and clear had
  to update all three. Membership (`IsBotEntity`, read on every damage event,
  trigger pull and target candidate) now reads the dictionary's key set, which
  has nothing to keep in step.
- Three Unicode characters that reorder or hide text in a terminal reached the
  audit trail and split identity keys, because the invisible-character table
  listed Unicode's bidi embeddings and overrides but not the rest of its
  `Bidi_Control` set: U+061C (ARABIC LETTER MARK) and the isolates U+2066
  to U+2069. `Gr\u2066unt` was a second team assignment next to `Grunt`, and a
  `requestId` carrying an isolate was logged verbatim. All of them are scrubbed
  now, next to the ones that already were.
- A `requestId` retry that differed from the original only in normalization
  re-executed the request instead of replaying it. The ledger compares keys
  ordinally, and the key was stored raw, so a client that re-serialized the
  key from an NFD name, or pasted one carrying a zero-width character, missed
  the entry and ran the action a second time (two spawns, two persisted
  writes). Keys now go through `BotText.IdentityKey` at ingestion, the same
  policy as team assignments and character names.
- The same retry pair, with the key spelled identically but the body field
  decomposed, was answered `409 REQUEST_ID_REUSED` and the client re-ran a
  spawn the server had already performed. The request fingerprint
  canonicalizes field names and values to NFC before the ledger compares
  them, so `"Kíra"` (U+00ED) and `"Kíra"` (U+004B U+0069 U+0301) are one
  request rather than two.
- A world `spawnpoints.xml` could place a bot at a non-finite position. The
  DM spawn reader accepted the `NaN`, `Infinity` and `-Infinity` spellings
  `float.TryParse` allows, so a corrupt or crafted world file fed an
  undefined coordinate into the spawn position and from there into every
  later combat tick. Non-finite coordinates are rejected now, alongside the
  malformed ones the reader already dropped.
- The web panel reported `done` for any 200, including the ones the API
  answers when nothing happened: `spawn` and `spawnNear` return
  `{"spawned":0}` at the bot cap (and `{"found":false}` when the named player
  left between polls), `removeOne` returns `{"removed":false}` for an id that
  is already gone, and `neural` returns `{"loaded":false,"reason":...}` when
  the weights file did not load. The result line now names each outcome
  (failed, partial, or the count that spawned) instead of claiming success.
- The dashboard's envelope unwrap is shared with the POST path rather than
  duplicated, and the team-bucket clamp is defined once (`teamCountOf`,
  `teamSlot`) instead of repeated in the teams card and the scoreboard.
- `evolve.py` exited 1 for a flag value outside its documented range
  (`--islands`, `--activation`, `--curriculum`, `--resume auto`). The exit
  table in `tools/ga/README.md` and the CLI epilog reserve 2 for a bad
  command line and 1 for a run that started and could not finish, so a typo
  read as a training failure to any caller branching on the code. The range
  checks now print to stderr and exit 2.
- The GA dashboard never highlighted the champion run: `ga.save_best` wrote
  `best.meta.json` without the run `seed` that `dashboard.build` matches
  against each run's `config.json`, so the comparison was `None == <int>` for
  every real run. The seed now travels with the champion.
- The web panel's `requestId` fallback (used when `crypto.randomUUID` is
  missing) built its idempotency key from `Math.random()`, which is seeded per
  context and predictable, so two clicks could land on the same key and a retry
  would replay a different command's response. It now draws 16 bytes from
  `crypto.getRandomValues`.
- The difficulty preset and its bounds were recomputed from whatever the
  previous `Normalize` had written, so the preset's own output was fed back in
  as its input and every `bot skill` change was one-way. `bot skill 0` then
  `bot skill 2` left the easy 0.42s reaction in place, `bot skill 4` then
  `bot skill 2` never lifted the 0.04 headshot cap, and difficulty 4's 120m
  vision survived the drop. `Normalize` now recomputes the five preset-driven
  fields from the values the loaded `botmod.json` carried, so it is idempotent
  and a difficulty change is reversible in both directions. Deciding whether
  the operator tuned `ReactionTimeSec` or `AimJitterDegrees` also moved from a
  proximity test against the live value to a comparison against the stock value
  at load: a config that spells out the stock value (the shipped
  `botmod.json` does) still follows `Difficulty`, and a tuned one is now left
  alone consistently rather than only when it happened to sit far from stock.
- `BotText.BaseName` reached its result through `Split('_')[0]`, which
  allocates the whole string array behind the discarded tail. It runs on the
  per-damage-event ally check and on every character lookup, so it now uses
  `IndexOf` with the same result and no array.
- The dashboard panel rendered every number it displays as raw `String(v)`,
  so digit shapes, grouping separators and the meter unit were en-US for every
  viewer: a de-DE or ar-EG admin read `12,345` and `1250 m` where their locale
  writes `12.345` and `١٬٢٥٠ مترًا`, and a bot count past 999 read as an
  undifferentiated run of digits. Displayed counts, distances and the
  scoreboard cells go through `Intl.NumberFormat` now. Posted values, `<input>`
  values and `<select>` values stay on the plain form: those are protocol
  tokens, and a localized digit must never reach the server.
- The panel chose its noun form with `n === 1`, an English rule. It picks the
  plural category through `Intl.PluralRules` now, so a locale with more than
  one/other selects its own category. The panel's labels are still English, so
  the visible text is unchanged until a translated label set supplies the
  forms its language needs.
- The online-players list in the panel header rendered player names in the
  same run as the fixed English around them, and the Team and Remove cells
  carried a bot name inside an `aria-label` with no direction of its own. An
  Arabic or Hebrew name there resolved against the panel's base direction
  instead of its own. The names get their own `dir=auto` run, like the
  scoreboard's name cell already had.
- The Team palette kept its labels in a parallel `TEAM_LABELS` array and clamped
  the team index twice (`TEAM_COLORS.length - 1` for a color, a literal 8 for a
  count). A team past the palette rendered `undefined` as a chip color. The
  labels are derived from one `clampTeam` and the cap is the named `TEAM_LIMIT`
  the +/- buttons use.
- `makeArmedBtn` and `makeBtn` built the same button element twice, differing
  only in whether a click armed first. One factory takes the arming state as an
  optional argument, and the "activate again within 4 seconds" announcement
  reads `ARM_TIMEOUT_MS` rather than repeating the number the timeout already
  had.
- `IdempotencyLedger.cs` and `IdempotencyLedgerFuzzTests.cs` imported
  `BotMod.Config` for the `BotText` character count they call, which lives in
  `BotMod.Foundation`. The suites compile a reduced source set without
  `Config/`, so mcs failed on the unknown namespace and the `idempotency` and
  `idempotencyfuzz` suites had not run since.
- The web `spawn` and `spawnNear` actions clamped their `count` with a second
  copy of the console parser's 1..16 range. They call
  `BotArgParser.ClampCount` now, so the two surfaces cannot drift apart on the
  bound.
- `vs` and `setTeam` validated their optional/flag fields before the field
  naming the target, so a body missing both `target` and `on` (or `name` and
  `team`) was told the flag was missing. Both check the identifying field
  first, as `spawnNear` and `removeOne` already did.
- The dashboard reported every rejected command as "the server rejected the
  command" and dropped the server's `meta.errorCode`, so a mistyped field was
  indistinguishable from any other rejection. The result line now names the
  code.
- The developer CLIs disagreed about what a bad command line is. The shell
  scripts used exit 1 for usage errors (test-idempotency.sh used 2), so a
  script could not tell "you typed it wrong" from "it failed", and
  `restore-state.sh` answered `--help` with `ERROR: unknown option`. Bad flags
  and a missing argument now exit 2 everywhere,
  `backup-state.sh` and `restore-state.sh` take `-h`/`--help` and print their
  usage and exit statuses, `backup-state.sh` rejects a second argument instead
  of ignoring it, and both answer before the `SEVENDTD_DS_DIR` guard so help
  works on any host.
- `scripts/test-idempotency.sh --list` and `--help` probed for `mcs`/`mono`
  before parsing arguments, so on a host without mono they exited 127 with a
  toolchain error instead of printing. The probe now runs after the parse.
- `evolve.py static-vs-neural` with an empty `--seeds` (or `--matches 0`)
  reported `GOAL MET: True` and exited 0: the gate is `all()` over the rows it
  measured, and it had measured none. An empty or 0-sized selection is now a
  usage error (exit 2), as are `--pop` below 1, `--gens` below 0 and
  `--islands` outside 1..8, which previously failed deeper in with a numpy
  traceback (or, for `--islands`, a different exit code from the same check).
- `dashboard.py --all --runs <dir>` silently ignored `--runs` and built over
  every run. The two are now mutually exclusive and say so.
- `determinism_check.py` printed its `FAIL` line to stdout, where a caller
  collecting the run's report would read a failure as a passing check's
  output. Failures go to stderr; `--help` is accepted and an unexpected
  argument exits 2.
- `make package` archived whatever was in `dist/BotMod` as long as
  `BotMod.dll` was there, so a `make build` that failed after the C# compile
  (the `bunx tsc` emit, a missing config file) still produced a release zip,
  missing `WebMod/`. The payload's required files are now checked and a
  partial one is refused by name.
- `make verify-reproducible` copied `Source/BotMod/obj` and `bin` into its
  second build tree, so the dotnet backend could answer the cross-path
  comparison out of intermediates written at the first tree's path. The
  mirror is now source only, and leg 1 starts from no intermediates either.
- `scripts/webmod-minify.sh` and `scripts/lint-webui.sh` did not pin
  `LC_ALL`/`TZ` the way `scripts/build.sh` does, so the bundle freshness gate
  computed its reference bytes under the runner's locale while the build
  computed the shipped bytes under `LC_ALL=C`.
- `scripts/coverage-cs.sh` piped each suite's `dotnet build` into
  `/dev/null`, so a compile failure surfaced as a bare exit with no
  diagnostics; the log is now printed on failure.
- `make preflight` did not name `curl`, which `make lint-webui` needs to fetch
  the pinned anti-slop archive, so a host without it failed mid-gate instead
  of at preflight.
- The CI cache in `.github/workflows/ci.yml` saved `~/.npm/_npx`, which is
  npx's cache. Nothing in this repo runs npm, so every `make check` re-fetched
  typescript, terser, oxlint, tsgolint, oxlint-standards and vnu-jar from the
  registry. The path is now bun's package cache (`~/.bun/install/cache`), where
  bunx actually puts them.
- `scripts/verify-reproducible.sh` hashed `dist/BotMod-*.zip`, so with a zip
  from an earlier release still in `dist/` the two sides of the comparison were
  multi-line strings and the digest it reported was not the archive's. It now
  hashes the one archive `scripts/package.sh` wrote, named from
  `BotModVersion.Number` the same way.
- The `actions/checkout` pin in `ci.yml` was commented `v4.2.2` while
  `release.yml` commented the same commit `v7.0.1`. The commit is the v7.0.1
  release prep, so `ci.yml` was wrong.
- The `mixed` weapon literal was matched case-sensitively in
  `WeaponProfile.ForGun` and `BotSpawner.PickWeapon` while every surface that
  accepts it (`BotArgParser.LooksLikeWeapon`, `bot weapon`, the web `spawnNear`
  weapon field) matches case-insensitively. `bot weapon MIXED` persisted a
  value that then resolved to the pistol default with `GunId="MIXED"`: no such
  item, so later bots held no gun while running pistol stats. Both spellings
  now expand to a `LoadoutPool` entry, and
  `tests/BotMod.Tests/WeaponProfileTests.cs` pins it.
- Every `best.json` reader in `tools/ga` rebuilt the weights array with its
  own dtype: `evolve.py eval` used float32, while the promotion gate,
  `replay.py`, `viz.py` and `dashboard.py` used float64. A float64 array runs
  a second numba specialization of `combat_sim._forward` whose `tanh` rounds
  differently, so the incumbent champion in the promotion gate was scored on
  a stick the candidate was never measured on. `ga.load_best` is now the one
  reader (float32, size-checked) and all five call sites go through it; a
  wrong-size `best.json` fails verification there instead of failing inside
  the sim, being caught as an evaluation error, and being scored `-inf` so any
  candidate promoted over it.
- `BotText.IdentityKey` normalized before stripping invisible characters, so
  it was not a fixed point: an invisible character between two combining marks
  (a soft hyphen or ZWNJ inside a pasted name) blocks their composition, and
  the second pass over the same name composed what the first had left apart.
  A team assignment stored under the first key was then unreachable through
  `GetTeamAssignment`, which derived a different one. It strips first and
  normalizes second, which is a fixed point because NFC never introduces a
  character the strip removes.
- `BotConfig.GetTeamAssignment` derived its lookup key with
  `BotText.IdentityKey` while `SetTeamAssignment` stored under
  `BotText.BaseName`, so a full spawned name (`[Bot] Grunt_42`) missed the
  entry the write had created. Both sides now derive the key the same way.
- The `requestId` limit (documented as 128 chars) was enforced in UTF-16 code
  units, so a key of 100 emoji measured 200 and was rejected
  `400 INVALID_REQUEST_ID`. `IdempotencyLedger.IsValidKey` now counts
  characters (a surrogate pair is one) via the new `BotText.CharCount`.
- Text fields read from a POST body (`action`, `requestId`, `player`,
  `weapon`, `target`, `name`) were converted with `Convert.ToString` under the
  host culture, so a de-DE server turned the JSON number 1234.5 into the
  ledger key `"1234,5"`. They go through the invariant
  `RequestFields.OptString` the other body readers already use.
- U+2028/U+2029 (line and paragraph separator) are not control characters, so
  a requestId or name carrying one passed the log scrub intact and could still
  split a single-line audit entry. Both are now in the shared invisible set
  (`BotText.IsInvisible`), so identity keys drop them too.
- `scripts/coverage_badge.py` wrote its SVG through the platform default
  encoding, the one Python text boundary in the repo without an explicit one.
- `tools/ga/determinism_check.py`'s determinism check zipped its two run
  records without `strict=True`, the one `zip()` the B905 entry under Added
  claimed was covered. A truncated run compared unequal lengths silently
  instead of raising.
- A champion trained with `evolve.py --activation relu` could be promoted into
  `evolved/best.json`, which the mod's tanh-only forward pass then evaluated
  with the wrong hidden activation: the file loaded, every output was wrong,
  and no surface reported a reason. `activation` is now part of the artifact
  (`ga.save_best` writes it, `best.meta.json` carries it),
  `BotNeuralBrain.TryLoad` rejects any value but `tanh`, and a relu run writes
  `runs/<ts>/best_relu.json` instead of touching the shipped slot. An artifact
  written before the field existed reads as `tanh`.
- `ModApi.PersistConfigField` wrote the same config file twice on any install
  where the assembly sits under `/mods/BotMod`, because the hardcoded server
  path and `DefaultPathBesideAssembly()` resolve to the same file. The second
  pass staged the first pass's output as the `.bak`, so the last-known-good
  recovery copy no longer predated the live value. Candidate paths are now
  de-duplicated by normalized full path.
- `IdempotencyLedger`'s hard-cap eviction sink swallowed a throwing sink
  silently. It now reports through a `Warn` sink wired to `ModApi.Warn`, the
  same contract as the other engine-free layers.
- `tools/ga/report.py`'s `weight_hist` and `best_net` were annotated
  `-> str | None` while returning PNG bytes, so every caller that embedded the
  result in an `<img>` was mistyped. Both now say `bytes | None`.
- `tools/ga/evolve.py` rebound its `fitness` parameter (the mix dict) to a
  per-generation list of scores inside the training loop, and resumed from a
  checkpoint that set `top3` without narrowing `ckpt`. The loop now uses
  `all_fitness` directly, and the resume guard checks the whole triple it
  depends on.
- The `requestId` idempotency key is now bound to the request it was issued
  for: a retry whose body differs under a live key is rejected
  `409 REQUEST_ID_REUSED` instead of replaying the earlier response. Reusing a
  key across requests answered the new one with the old response and silently
  dropped the operation it asked for. The original claim is untouched, so a
  retry of the request that owns the key still replays.
- `tools/ga/evolve.py static-vs-neural` exits 1 when the promotion gate is not
  met instead of printing `GOAL MET: False` and exiting 0, and `tools/ga/sweep.py`
  exits 1 when no activation produced a curve. A CI step reading either exit code
  was treating both failures as passes.
- A best.json that is missing, malformed, or the wrong genome size now fails with
  a one-line message naming the file in `evolve.py`, `replay.py` and `viz.py`
  instead of a JSON `KeyError` traceback. `replay.py` range-checks `--n-bots`,
  `--n-zombies`, `--max-ticks` and `--env` before recording.
- `tools/ga/sweep.py`'s docstring showed `--seeds` / `--trials` usage examples
  that no longer exist; they now show `--pop` / `--gens` / `--seed`.
- `IdempotencyLedger`'s monotonic clock no longer reads a low-resolution
  `Stopwatch` counter as `DateTime` ticks. On a host without a
  high-resolution counter that ran the elapsed clock about 10000x slow, so
  replay-window entries never aged out and only the capacity cap retired
  them. `Stopwatch.GetTimestamp` always counts in `Stopwatch.Frequency`
  units, so the single frequency division is now the only conversion.
- `BotCharacterDB.Load` builds the character table privately and publishes it
  with one reference store. Before, the map was mutated in place while the game
  tick read it per bot, so a concurrent `bot reload` could hand a reader a
  half-filled table or a character whose traits were half difficulty-lerped.
  The DM spawnpoint memo is now dropped with the rest of the per-world state on
  shutdown and on a manager start, instead of being keyed on the world name and
  handing a new world the previous world's coordinates.
- `WeaponProfile.ForGun` matched the machine-gun branch on `pipe` alone in
  some paths and `"pipe" && "machine"` in others, so `gunPipeRifle` resolved to
  the pistol default (fire rate, damage, range of a pistol, holding a rifle).
  The test is parenthesized once and `pipe` joins the rifle branch.
- The scoreboard's `health`, `score` and `nearestPlayerDist` truncated their
  float instead of rounding (a 99.9 hp bot read as 99), and a value outside
  `int` range, NaN or Infinity went through an undefined float-to-int cast.
  They now round and clamp, with NaN as 0.
- `BotNeuralBrain.TryLoad` multiplied the file-declared hidden width without
  bounding it, so a large `hidden` wrapped the size check's own arithmetic.
  The width is now range-checked (1..4096) before it is used, and the reason is
  logged, like every other artifact rejection.
- The synthetic-auth-bypass audit line logged the client's SteamId and IP. The
  SteamId is one value out of a fixed block the bypass matches on, so it
  carried no diagnostic information, and the IP is personal data. The line now
  names the connection's in-world `entityId`, the key `lp` and every other
  session line already uses, so the bypassed join still correlates.
- `tools/ga/replay.py` wrote caller-supplied text (a run label, a seed caption)
  into the generated page unescaped, and embedded its frame and wall payloads
  in a `<script>` element where a `</script>` inside a string value ended the
  element and turned the rest of the JSON into markup. The title is escaped and
  the two payloads escape `</`. `dashboard.py` escaped the replay label the same
  way. A crafted run-directory name could otherwise inject markup into a
  report the operator opens in a browser.
- `evolve.py` wrote `config.json` before the island split and recorded the
  requested `--pop`: `--pop 8 --islands 4` trained 32 genomes and logged 8, so
  replaying the run's own logged config produced a different run. It now
  records the population actually evolved, with `pop_requested` and
  `pop_per_island` beside it.
- `tools/ga/report.py`'s CSV loader accepted `nan` as a fitness value. It
  parses fine, then poisons every `max()`, mean and delta downstream (`max()`
  over a list holding NaN returns NaN) and the chart silently dropped the
  series. Non-finite values are now treated like any other torn row: that
  generation is skipped and counted.
- `tools/ga/dashboard.py`'s held-strip figure was closed on the success path
  and leaked on the empty-data path, keeping the figure, its axes and its
  canvas in pyplot's global registry for the rest of the process.
- Entity ids render into kill lines, the shot-failure warning, the death-patch
  warning and the `bot player <id>` lookup under the server's current culture.
  On a culture whose digit or negative sign differs from the invariant one, a
  player named by id on the console missed the prefix match and the log lines
  carried a spelling the rest of the session does not use. Those surfaces now
  render invariantly, matching the invariant `int.TryParse` every id surface
  already used. `WeaponProfile.ForGun`'s machine-gun test is parenthesized the
  same commit: `smg || pipe && machine` bound tighter than it read, so a gun id
  containing both spelled differently from what the branch intended.

### Changed

- `scripts/lint-webui.sh` verifies the vendored dmmulroy/anti-slop source
  archive against a pinned SHA-256 (`ANTI_SLOP_SHA256` in
  `scripts/tool-versions.sh`) on fetch and on every cached run, so the one
  fetched dependency with no registry behind it cannot be swapped or tampered
  with unnoticed. The npm pins it installs alongside resolve through the
  registry's own per-version integrity fields and are unchanged.
- `tools/ga/requirements.txt` caps numpy below 2.0. NumPy 2 moved the
  `Generator` bit streams the trainer is seeded from, so an install that
  resolved to 2.x reproduced neither the committed `evolved/best.json` nor the
  byte-identical seeded runs `tools/ga/README.md` promises. Pillow is now a
  declared, capped entry instead of a comment: `report.py` and `dashboard.py`
  import it for the palette-PNG optimization, and the code is correct without
  it.
- Every `tools/ga` CLI (`evolve.py`, `sweep.py`, `report.py`, `dashboard.py`,
  `viz.py`, `replay.py`) now has a `description`, per-flag help, worked examples
  and an exit-status section in `--help`, all following the same 0/1/2
  convention as argparse itself.
- Web dashboard panel: a command that the server rejects now says so in a
  result line under the header instead of silently springing the button back.
  Enable/Disable acts on one click like every other toggle (only the actions
  that remove bots still confirm), "Spawn near" is disabled while no player
  is online, a non-auth API failure offers Retry rather than Log in, the
  scoreboard scrolls sideways instead of widening the sidebar, and a live row
  no longer remounts under the user's cursor.
- The `AtomicTextFile` suite cleans up only the temp directories its own
  process created. The previous sweep of every `/tmp/botmod-atomictest-*`
  directory deleted concurrent instances' in-flight writes, which showed up
  as spurious "concurrent writes complete without errors" and torn-write
  failures when two checkouts ran the suite at once.
- `make install` stages the whole payload in a sibling of `Mods/BotMod`,
  copies the live `Config/botmod.json(.bak)` into the staged copy, and swaps it
  in with a single rename, so a failed copy leaves the running install
  untouched. `make uninstall` takes its own backup first and aborts if it
  cannot write one (`BOTMOD_SKIP_BACKUP=1` overrides, and then the operator
  state is gone).
- `WeaponProfile.SpreadDeg` is gone. The field was never read by the aim or
  fire path, so no behavior changes; the per-shot spread bots actually apply
  comes from the neural aim bias and `AimJitterDegrees`. `characters.json` is
  unaffected: it binds `BotCharacter`, not `WeaponProfile`.
- `tools/ga`'s duplicated rank, burst and logistic code now lives once, in
  `ga.py` and `harness.py`, and every entry point calls it. The sim's stuck
  accounting changed with the dedup: `stuck_ticks` now counts from the first
  tick a bot is detected stuck, where it previously skipped that first tick, so
  a run's fitness values are not comparable with a pre-dedup run of the same
  seed. Seeded runs remain byte-identical run to run on the current code.
- `BotBrain.FindTarget` rejects a candidate on a distance-only lower bound
  before its line-of-sight `Physics.Raycast` (plus up to 64 voxel `GetBlock`
  calls) runs, and the `hpFrac` divisor is hoisted out of the scan. The bound
  is `dist * minMult`, computed with slack for the float rounding that the
  health term and the grudge multiplier introduce, so it only ever skips a
  candidate that cannot out-score the incumbent: every gate is a skip, so the
  pick is unchanged. `Bot.Status(EntityAlive)` is the overload the dashboard
  uses, which no longer re-runs the entity lookup per bot per poll.

## [0.7.1] - 2026-09-21

### Changed

- Internal refactor only: the single-implementation `IBotRegistry` /
  `BotRegistry` install shim is gone. `BotBrain` and `BotCombat` query
  `BotManager.Instance` directly, with the cycle-break note now living as a
  comment on `BotManager`. No operator-visible behavior changed.

## [0.7.0] - 2026-09-20

### Added

- `tools/ga/evolve.py` gains `--fit-elo/--fit-econ/--fit-surv/--fit-stuck`
  flags to override the fitness scalarization mix (any one of them disables
  the defaults), and `eval` / `static-vs-neural` subcommands that carry the
  former `eval.py` and `eval_static_vs_neural.py`. All scoring paths share
  the one canonical measuring stick in `harness.canonical_scores`.

### Changed

- `tools/ga` tooling cleanup: `clone.py`, `paths.py`, `plot.py`, and
  `fitness_sweep.py` are deleted, the population seeder is renamed
  `init_population`, and `sweep.py` now runs the H16 tanh-vs-relu ablation
  only (numba bakes the hidden size). No in-game behaviour change.
- `AtomicTextFile.TryRead` loses its unused two-argument overload; callers
  use the variant that names the file the content came from.

## [0.6.0] - 2026-09-11

### Changed

- `AGENTS.md` states what this repository owns and does not own: server-side
  FPS bots here, LiteNetLib demand bots in `7dtd-loadgen`, stock-fidelity
  suites in `7dtd-playtest`. Mod-only scenarios stay local via
  `IScenarioProvider`. No behaviour change.

## [0.5.0] - 2026-08-26

### Added
- `make test` now also pins the deny side of the authorization matrix
  (`tests/BotMod.Tests/WebApiAuthzTests.cs`): `GET/POST /api/bot` must
  keep declaring permission level 0 for every request-method slot, and the
  `bot` console command must keep its default level 0. A change that widens
  either declaration fails the suite instead of silently handing bot control
  to lower-privileged callers.
- `make package` (scripts/package.sh) builds the release zip
  (`dist/BotMod-<version>.zip`) reproducibly: sorted entry order, all
  timestamps pinned to `SOURCE_DATE_EPOCH` (default: HEAD commit time),
  uid/gid stripped and permissions normalized, so two builds of the same
  commit produce identical bytes.
- The release zip carries `MANIFEST.sha256` (sha256 of every payload file,
  `sha256sum -c` format), so an extracted package can be verified offline.
- Unknown keys in `Config/botmod.json` are reported as a WARN naming the key
  at load instead of being silently ignored (a misspelled key used to keep
  the built-in default with no signal).
- A missing `characters.json` logs a WARN instead of silently falling back to
  built-in bot characteristics.
- Optional idempotency key for `POST /api/bot`: send `"requestId":"<unique>"`.
  Retries reusing the key replay the recorded response within the ledger
  retention window instead of executing twice; a concurrent duplicate gets
  `409 REQUEST_IN_PROGRESS`; failures are not cached and may be retried.
  Requests without `requestId` behave exactly as before.
- Fuzz suites in `tests/BotMod.Tests`, run by `scripts/test-idempotency.sh`
  (`make test`): a differential model fuzzer hammering the idempotency ledger
  with adversarial `requestId` shapes, clock jitter and capacity/retention
  pressure, a mutation fuzzer for the `evolved/best.json` weights-file parser,
  and `BotConfigLoadTests` pinning unknown-key detection, range clamping and
  `.bak` recovery (the latter two need the game install's Newtonsoft.Json.dll
  and are skipped when it is absent).

### Changed
- The engine-free Config and AI layers no longer reference the `ModApi`
  entry-point type: `BotCharacterDB` warnings flow through the existing
  `BotConfig.Warn` sink, and neural weight-path resolution reads
  `BotNeuralBrain.ModRoot`, wired once during init. The headless unit suites
  compile without their previous test-only `ModApi` shims.
- Pinned npx tool versions (typescript, oxlint + plugin packages, vnu-jar,
  anti-slop commit) now live in `scripts/tool-versions.sh`, sourced by
  `scripts/build.sh`, `scripts/lint-webui.sh`, and `scripts/lint-html.sh`;
  previously the tsc pin was duplicated between build and lint with only a
  comment keeping them equal. Local env overrides behave as before.
- **The synthetic-id auth bypass is now opt-in** via the new config key
  `"AllowSyntheticAuthBypass"` (default `false`). In every release up to and
  including 0.4.0 this bypass was always on: offline LAN/loadgen clients with
  synthetic Steam ids (76561199000000000..10000) could join an EAC-off server
  running the mod without Steam authentication. After upgrading, such clients
  are rejected until you set `"AllowSyntheticAuthBypass": true` in
  `Config/botmod.json`. Leave it off on any publicly reachable server; the id
  range is predictable.
- `bot enable|disable` persists `Enabled` back to `Config/botmod.json`
  (previously a restart reverted the toggle).
- All admin mutations now persist, closing the same revert-on-restart gap for
  the rest of the surface: `bot count`, `bot skill`, `bot weapon`,
  `bot neural on/off`, plus web API actions `skill` and `neural` write
  `TargetBotCount`, `Difficulty`, `BotWeapon` and `UseNeuralBrain` back to
  `Config/botmod.json`.
- The startup log line names the `AllowSyntheticAuthBypass` state
  (`AuthBypass=True|False`) so an insecure config is visible in the log.
- Config writes are atomic (temp file + rename) so a crash mid-persist cannot
  tear `botmod.json`; if the primary file is unreadable at load, the mod
  restores the `.bak` last-known-good copy and logs a WARN instead of silently
  resetting all persisted settings to defaults.
- Reinstalling or upgrading via `make install` preserves your operator state:
  `Config/botmod.json` (and its `.bak`) are staged out and restored rather
  than overwritten by the shipped default.
- Server log lines use WARN for degraded-but-running problems and ERR for
  broken functionality; each web API mutation logs one audit line.
- Every `characters.json` load failure mode now reports the reason and the
  file path it probed: an unparseable file logs `parse failed (<path>): ...`
  and a bare JSON `null` body is reported instead of passing silently.
- Misspelled trait keys inside `characters.json` entries (e.g. `"Acuraccy"`)
  are reported as a WARN naming the entry and the key, same contract as the
  botmod.json unknown-key warning; Json.NET silently ignores them otherwise,
  leaving the built-in default in place with no signal.
- `bot spawn <x> <z>` now spawns one bot at those coordinates. Previously the
  first coordinate was misread as a bot count (so `bot spawn 163 818` spawned
  up to 16 bots at a random position) and a trailing token that was neither
  count, coordinate pair nor gun id was silently ignored (`bot spawn 2 abc`,
  `bot player Kira xyz`); every leftover argument is now rejected with a
  usage error naming the offending token.
- A null entry value in `characters.json` (`{"Grunt": null}`) is dropped
  instead of failing the whole file: previously one such entry threw during
  ingestion and every custom personality in the file silently fell back to
  built-in defaults behind a generic parse warning.
- `POST /api/bot` rejects malformed request bodies with named `400 INVALID_*`
  codes (new: `INVALID_COUNT`, `INVALID_ENTITY_ID`, `INVALID_LEVEL`,
  `INVALID_NAME`, `INVALID_ON`, `INVALID_PLAYER`, `INVALID_REQUEST_ID`) where
  it previously silently reinterpreted them:
  - a missing `spawnNear` `player` answered `200 {"found":false}` like a
    departed player instead of flagging the bad request;
  - a missing `removeOne` `entityId` ran a lookup for id 0 and answered
    `200 {"removed":false}`;
  - absent or non-boolean `on` on `neural`/`team`/`vs` read as `false` and
    flipped the live setting with a `200` (a malformed squad-mode call could
    disband teams);
  - present-but-unparseable numeric fields (`count`, `level`, `team`)
    substituted their defaults; omitted fields still take those defaults;
  - a `requestId` that is present but empty or over 128 chars degraded to
    keyless execution, so retries re-executed while the caller believed they
    would replay; it is now rejected so the caller can fix the key.
  Range clamps are unchanged (`count` 1..16, `skill` 0..4, teams 0..8) and
  stay shared with the console command's setters.
- `make lint-html` checks the HTML this repo actually ships (`git ls-files`)
  instead of walking the tree, and warnings now fail it (`vnu --Werror`), not
  just errors. A local `evolved/runs/<ts>/report.html` left over from training
  can no longer fail a gate CI never sees, and the accessibility warnings the
  old gate printed and ignored (missing `lang`, trailing slash on void
  elements) are now build failures.
- CI installs the pinned ruff with `uv tool install` via `astral-sh/setup-uv`
  (SHA-pinned like every other action) instead of relying on the runner image
  shipping pipx.
- The GA tools resolve the repo root by walking up for the root `Makefile`
  (`tools/ga/paths.py`) instead of counting `..` from `__file__`; moving a
  script one directory deeper used to silently point `evolved/runs/...` at the
  wrong tree.
- `report.py` output no longer embeds the absolute path of the machine that
  generated it, and declares `<html lang="en">`; `dashboard.py` renders a
  missing run-config key as `n/a` rather than `None` or an em dash, so a value
  the run never recorded cannot read as a measured one.

### Fixed
- A failed `characters.json` reload (missing, unparseable, or null body) now
  rebuilds the pristine default table like the missing-file case already did.
  Previously an exception kept the previous load's instances untouched while
  a null body re-applied the difficulty lerp onto them, so every `bot reload`
  while the file stayed broken marched aim accuracy toward 1.0 and reaction
  time toward its floor; both paths now converge on the documented defaults.
- Target selection's finish-the-wounded bias now scales target health as a
  fraction of `BotHealth` like every other health fraction in the mod;
  previously it divided by a hardcoded 100, so with a tuned `BotHealth`
  (config accepts 10..10000) wounded-target preference was mis-scaled by up
  to 100x and bots effectively ignored who was hurt.
- `bot count`, `bot skill`, `bot teams` and `bot team assign` parse their
  numeric argument in the invariant culture (entity ids and coordinates
  already did): under a comma-decimal host locale these commands no longer
  depend on `int.TryParse`'s current-culture behavior.
- Neural weights resolution joins path segments through `System.IO.Path`
  instead of embedding `/` separators, so `evolved/best.json` fallback
  candidates stay the platform API's job on every OS the dedicated server
  ships for.
- Character lookup strips the `[Bot]` prefix so non-Grunt bot names resolve;
  aggro honors the `bot vs` gates; wandering skips allies and teammates.
- `evolved/best.json` files whose `inputs` differs from the frozen v1
  observation layout (14) are rejected at load with a clear reason instead of
  loading successfully and then silently failing every bot tick.
- Null or empty entries in `LoadoutPool`/`BotNames` (hand-edited JSON) are
  dropped at load; previously a `"LoadoutPool": ["gunX", null]` made every
  `mixed` weapon pick throw a NullReferenceException, so spawning and the
  auto-respawn loop failed every second until the config was fixed by hand.
- `bot remove <anything-not-all-or-an-id>` now prints a usage error instead
  of silently removing ALL live bots (a typo like `bot remove al` used to
  wipe the roster).
- The web API's `spawnNear` action rejects an off-grammar `weapon` value with
  `400 INVALID_WEAPON` instead of silently ignoring it and spawning bots with
  random loadouts (same grammar as `bot player ... [weapon]`).
- `evolved/report.html`, `evolved/sweeps/report_*.html` and the generated
  `dist/BotMod/Config/entityclasses.xml` are no longer committed (4.6 MB of
  regenerable artifacts, two of which embedded the generating machine's home
  directory). `evolved/README.md` and `tools/ga/README.md` document how to
  regenerate them; `docs/ga-dashboard.html` stays committed because the
  `evolved/runs/` data behind it does not ship.

### Performance
- Neural/LOS evaluations memoized per tick and O(1) bot lookup in
  `BotManager`.

## [0.4.0] - 2026-08-23

Tagged `v0.4.0` at commit a4a8bf3. The `v0.4.1` tag and GitHub release
point at that same commit and ship 0.4.0; no 0.4.1 build exists.

### Added
- Per-bot teams: assign bots to Team 0..N (0 = free-for-all); same-team bots
  never target or damage each other. Console: `bot team assign <name> <id>`,
  `bot team list`, `bot team clear`, `bot teams <0-8>`. Web API actions
  `setTeam`, `teamCount`, `clearTeams`; `GET /api/bot` status gains
  `teamCount` and per-bot `team`; the dashboard gets drag-and-drop team
  buckets plus a per-row team select.
- Persisted config keys `BotTeamCount` (default `2`) and `TeamAssignments`
  (map of bot base name to team id).

### Compatibility
- Existing `Config/botmod.json` files load unchanged; missing keys take
  defaults (`BotTeamCount=2`, empty assignments). No action needed to upgrade
  from 0.3.x.

## [0.3.0] - 2026-08-22

Tagged v0.3.0.

### Added
- Squad mode: `bot team <on|off>` puts all bots on one team that never fights
  itself (players and zombies unaffected). Persisted as `BotTeam`.
- Per-target toggles: `bot vs bot|zombie|player <on|off>` selects which target
  classes bots engage (all three on = free-for-all). Persisted as `BotVs*`.

## [0.2.0] - 2026-08-22

Tagged v0.2.0.

### Added
- Web dashboard sidebar entry (admin login required): enable/disable, spawn /
  remove all, spawn-near-player with online player dropdown, neural toggle,
  squad/vs toggles, scoreboard. Backed by authenticated `GET /api/bot` and
  `POST /api/bot` (permission level 0).
- `bot player <name|id> [n] [weapon]`: spawn bots 12-30m from a player,
  out-of-sight preferred (`spawnNear` web action mirrors it).
- GA-evolved neural controller: `UseNeuralBrain` + `evolved/best.json` drives
  aim-bias/fire/strafe/retreat in engagements; heuristic fallback when weights
  are absent or incompatible.
- Offline LAN/loadgen support: synthetic Steam ids in the range
  76561199000000000..10000 from loopback could join EAC-off servers without
  Steam authentication. Security note: this shipped always-on; since the
  Unreleased opt-in gate it must be enabled deliberately (see above).

### Changed
- Bot bodies render as `zombieSoldier*` models (trader/survivor bodies do not
  render reliably for clients on dedicated servers).
- Bots appear in the player list and scorecard; bot frags are broadcast to
  player chat.
- Bot names carry the `[Bot]` prefix; the mod runs on dedicated servers only
  by default (`DedicatedOnly`).

## [0.1.0] - 2026-08-12

Untagged initial release.

### Added
- Server-side FPS bots: spawn, pathfind, hunt and shoot players, zombies and
  each other; per-class targeting toggles; real ranged weapons with per-weapon
  profiles.
