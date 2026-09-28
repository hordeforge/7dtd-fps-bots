#!/usr/bin/env bash
# Compile and run the pure-BCL unit + fuzz suites (tests/BotMod.Tests)
# with mcs + mono. IdempotencyLedger and AtomicTextFile are pure BCL, so no
# game DLL references are needed. The BotNeuralBrain weights-file fuzzer also
# parses JSON, so it additionally needs Newtonsoft.Json.dll from the game
# install (probed like scripts/build.sh) and is skipped when that is absent.
# Not a `make check` target; CI installs mono and runs this script directly
# after `make check`. Run locally:
#
#   bash scripts/test-idempotency.sh                 # every suite
#   bash scripts/test-idempotency.sh lcg bottext     # only the named suites
#   bash scripts/test-idempotency.sh --list          # suite names
# `make test SUITE=lcg` passes the same argument.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Every suite the script can run, in execution order. `want` and the drift
# check at the end keep this list and the actual run_suite calls in step.
all_suites=(
  idempotency atomictextfile idempotencyfuzz mainthreaddispatch logsanitize
  spawnpointxmlfuzz logsanitizerfuzz logredact requestfields combatgates bottext lcg
  botclock botargparser botargparserfuzz neuralfuzz neuraleval configfuzz charfuzz
  adminsettersfuzz botchararith weaponprofile weaponprofilefuzz teamshammer
  botconfig webapiauthz botarith
)
# The suites compiled against the game DLLs (Newtonsoft and/or the full mod
# source), which self-skip without a game install.
game_suites=(neuralfuzz neuraleval configfuzz charfuzz adminsettersfuzz botchararith weaponprofile weaponprofilefuzz teamshammer botconfig webapiauthz botarith)

filter=()
while (($#)); do
  case "$1" in
    --list)
      printf '%s\n' "${all_suites[@]}"
      exit 0
      ;;
    -h | --help)
      sed -n '2,13p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    -*)
      echo "error: unknown option $1 (see --help)" >&2
      exit 2
      ;;
  esac
  known=false
  for suite in "${all_suites[@]}"; do [[ "$suite" == "$1" ]] && known=true; done
  if ! $known; then
    echo "error: no suite named '$1'; run '$0 --list' for the names" >&2
    exit 2
  fi
  filter+=("$1")
  shift
done

# The toolchain probe runs after the argument parse: --list and --help answer
# without compiling anything, so a box without mono must still be able to ask
# what the suites are.
for tool in mcs mono; do
  if ! command -v "$tool" > /dev/null; then
    echo "error: $tool not found; the C# suites need mono (apt: mono-mcs mono-runtime)" >&2
    exit 127
  fi
done

# Suite names each run_suite* call declares, for the drift check against
# all_suites below.
declared_suites=()

want() { # <name>: true when no filter is set or the name is in it
  ((${#filter[@]} == 0)) && return 0
  local name="$1" pick
  for pick in "${filter[@]}"; do [[ "$pick" == "$name" ]] && return 0; done
  return 1
}

want_any() { # <name...>: true when at least one of them is selected
  ((${#filter[@]} == 0)) && return 0
  local name
  for name in "$@"; do want "$name" && return 0; done
  return 1
}

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

run_suite() { # <name> <sources...>
  local name="$1"
  declared_suites+=("$name")
  if ! want "$name"; then
    printf 'skip %s (not selected)\n' "$name"
    return 0
  fi
  shift
  printf '==> %s\n' "$name"
  mcs -warnaserror -out:"$work/$name.exe" "$@" > /dev/null
  mono "$work/$name.exe"
}

# Engine-free sources from Config/ and Foundation/ shared by every suite that
# compiles the config layer headless (one list so a new dependency is added
# once).
config_src=(
  "$root/Source/BotMod/Config/BotConfig.cs"
  "$root/Source/BotMod/Foundation/BotText.cs"
  "$root/Source/BotMod/Foundation/AtomicTextFile.cs"
  "$root/Source/BotMod/Foundation/Lcg.cs"
  "$root/Source/BotMod/Config/WeaponProfile.cs"
)
character_src=(
  "$root/Source/BotMod/Config/BotCharacter.cs"
  "${config_src[@]}"
)

run_suite idempotency \
  "$root/Source/BotMod/Web/IdempotencyLedger.cs" \
  "$root/Source/BotMod/Foundation/BotText.cs" \
  "$root/tests/BotMod.Tests/IdempotencyLedgerTests.cs"

run_suite atomictextfile \
  "$root/Source/BotMod/Foundation/AtomicTextFile.cs" \
  "$root/tests/BotMod.Tests/AtomicTextFileTests.cs"

# Differential model fuzzer over the untrusted requestId surface.
run_suite idempotencyfuzz \
  "$root/Source/BotMod/Web/IdempotencyLedger.cs" \
  "$root/Source/BotMod/Foundation/BotText.cs" \
  "$root/tests/BotMod.Tests/IdempotencyLedgerFuzzTests.cs"

# Web -> main-thread dispatch lifecycle: wait handle released on every exit
# path, abandoned (timed-out) dispatch signals a disposed event safely.
run_suite mainthreaddispatch \
  "$root/Source/BotMod/Web/MainThreadDispatch.cs" \
  "$root/tests/BotMod.Tests/MainThreadDispatchTests.cs"

# Log-injection guard: request-supplied requestId/action must reach server log
# lines with control characters (CRLF, ANSI escapes) scrubbed. Clean delegates
# its invisible-character table to BotText, so both sources compile here.
run_suite logsanitize \
  "$root/Source/BotMod/Foundation/LogSanitizer.cs" \
  "$root/Source/BotMod/Foundation/BotText.cs" \
  "$root/tests/BotMod.Tests/LogSanitizerTests.cs"

# Randomized fuzzing of the same guard: arbitrary request-supplied strings
# must sanitize without throwing, keep length, leave nothing scrubbable, and
# stay idempotent.
run_suite logsanitizerfuzz \
  "$root/Source/BotMod/Foundation/LogSanitizer.cs" \
  "$root/Source/BotMod/Foundation/BotText.cs" \
  "$root/tests/BotMod.Tests/LogSanitizerFuzzTests.cs"

# Personal-data guard for the /api/bot audit line: a response body carrying a
# connected player's name (spawnNear's "player") keeps its outcome fields and
# loses the name, so the log records what the action did and not who it was
# about. Distinct from logsanitize above, which answers whether a value can
# forge a line, not whether it identifies a person.
run_suite logredact \
  "$root/Source/BotMod/Foundation/LogRedactor.cs" \
  "$root/tests/BotMod.Tests/LogRedactorTests.cs"

# POST /api/bot body-field readers: absent vs present-but-garbage triage for
# untrusted JSON values (named 400s instead of silent defaults), plus a
# shape fuzzer over adversarial value types. Fingerprint canonicalizes
# through BotText (same NFC policy every name comparison in the mod uses), so
# that source compiles here too.
run_suite requestfields \
  "$root/Source/BotMod/Web/RequestFields.cs" \
  "$root/Source/BotMod/Foundation/BotText.cs" \
  "$root/tests/BotMod.Tests/RequestFieldsTests.cs"

# vs-class combat gate: bot identity overrides body class (zombieSoldier
# bodies are EntityZombie), so the vs-zombie/vs-player toggles must never
# block bot targets or bot-on-bot damage.
run_suite combatgates \
  "$root/Source/BotMod/Config/CombatGates.cs" \
  "$root/tests/BotMod.Tests/CombatGatesTests.cs"

# World spawnpoints.xml reader: the document parse is fuzzed separately from
# the world-file lookup, so the untrusted half of the path (a shared world
# file) is testable without a running server.
run_suite spawnpointxmlfuzz \
  "$root/Source/BotMod/Foundation/SpawnPointXml.cs" \
  "$root/tests/BotMod.Tests/SpawnPointXmlFuzzTests.cs"

# Unicode identity contract: NFC canonicalization and ordinal case folding
# for bot/player name lookups and team-assignment keys.
run_suite bottext \
  "$root/Source/BotMod/Foundation/BotText.cs" \
  "$root/tests/BotMod.Tests/BotTextTests.cs"

# Randomized fuzzing of the identity-text layer: arbitrary names (lone
# surrogates, hostile UTF-8, invisible and combining characters) through
# Canon/WithoutInvisible/IdentityKey/BaseName/NameMatches must stay total,
# idempotent and canonical. Seeded with the string literals of the shipped
# config/characters.json and config/botmod.json.
mcs -warnaserror -out:"$work/bottextfuzz.exe" \
  "$root/Source/BotMod/Foundation/BotText.cs" \
  "$root/tests/BotMod.Tests/BotTextFuzzTests.cs" > /dev/null
# Repo root as argv[1] so the fuzzer can seed from the shipped config files.
mono "$work/bottextfuzz.exe" "$root"

# Deterministic LCG parity pins: the tap constants are mirrored by hand in
# tools/ga (combat_sim.py, replay.py), so a changed multiplier/shift/mask
# would silently desync the GA simulation from in-game rolls. Exact
# sequences from the documented formula plus Index/Range boundaries.
run_suite lcg \
  "$root/Source/BotMod/Foundation/Lcg.cs" \
  "$root/tests/BotMod.Tests/LcgTests.cs"

# Injected-time contract: the mod reads sim time through BotClock, so a
# harness can drive the bot timers from a virtual clock and replay a run.
run_suite botclock \
  "$root/Source/BotMod/Foundation/BotClock.cs" \
  "$root/tests/BotMod.Tests/BotClockTests.cs"

# Positional grammar of `bot spawn` / `bot player`: strict parse, named
# errors for leftover tokens (see Source/BotMod/Commands/BotArgParser.cs).
run_suite botargparser \
  "$root/Source/BotMod/Commands/BotArgParser.cs" \
  "$root/tests/BotMod.Tests/BotArgParserTests.cs"

# Randomized token fuzzing of the same grammar: never throws, clamped counts,
# named usage errors, deterministic re-parse.
run_suite botargparserfuzz \
  "$root/Source/BotMod/Commands/BotArgParser.cs" \
  "$root/tests/BotMod.Tests/BotArgParserFuzzTests.cs"

# Weights-file parser fuzzer: needs the game install's Newtonsoft.Json.dll,
# copied beside the exe so mono resolves the reference at runtime.
run_game_suite() { # <name> <sources...>   (references: Newtonsoft from the game install)
  local name="$1"
  declared_suites+=("$name")
  if ! want "$name"; then
    printf 'skip %s (not selected)\n' "$name"
    return 0
  fi
  shift
  printf '==> %s\n' "$name"
  mcs -warnaserror -langversion:7.2 -r:"$work/Newtonsoft.Json.dll" -r:"$work/netstandard.dll" \
    -out:"$work/$name.exe" "$@" > /dev/null
  # Repo root as argv[1] so the fuzzers can find evolved/best.json and the
  # committed config files.
  mono "$work/$name.exe" "$root"
}

# Team-map concurrency hammer: BotConfig pulls ModApi -> engine types, so this
# compiles the FULL mod source against the game DLLs (same reference set as
# scripts/build.sh) and is skipped without a game install. Harmony lives in
# the server's (or client's) Mods dir, where build.sh finds it too.
run_mod_suite() { # <name> <sources...>   (references: the full mod + game DLLs)
  local name="$1"
  declared_suites+=("$name")
  if ! want "$name"; then
    printf 'skip %s (not selected)\n' "$name"
    return 0
  fi
  shift
  printf '==> %s\n' "$name"
  mcs -nostdlib -sdk:4.7.2 -warnaserror -langversion:7.2 "${mod_refs[@]}" \
    -out:"$work/$name.exe" "${mod_sources[@]}" "$@" > /dev/null
  mono "$work/$name.exe"
}

if ! want_any "${game_suites[@]}"; then
  printf 'skip game-DLL suites (none selected)\n'
else
  steam_common="${XDG_DATA_HOME:-$HOME/.local/share}/Steam/steamapps/common"
  srv="${SEVENDTD_DS_DIR:-$steam_common/7 Days to Die Dedicated Server}"
  client="${SEVENDTD_GAME_DIR:-$steam_common/7 Days To Die}"
  managed=""
  if [[ -f "$srv/7DaysToDieServer_Data/Managed/Assembly-CSharp.dll" ]]; then
    managed="$srv/7DaysToDieServer_Data/Managed"
  elif [[ -f "$client/7DaysToDie_Data/Managed/Assembly-CSharp.dll" ]]; then
    managed="$client/7DaysToDie_Data/Managed"
  fi
  if [[ -n "$managed" && -f "$managed/Newtonsoft.Json.dll" && -f "$managed/netstandard.dll" ]]; then
    cp "$managed/Newtonsoft.Json.dll" "$managed/netstandard.dll" "$work/"

    run_game_suite neuralfuzz \
      "$root/Source/BotMod/AI/BotNeuralBrain.cs" \
      "$root/tests/BotMod.Tests/MutantBytes.cs" \
      "$root/tests/BotMod.Tests/BotNeuralBrainFuzzTests.cs"

    # Forward-pass correctness pins for the same brain (needs only Newtonsoft):
    # input packing order, sigmoid/tanh head math, decision thresholds, eval
    # purity.
    run_game_suite neuraleval \
      "$root/Source/BotMod/AI/BotNeuralBrain.cs" \
      "$root/tests/BotMod.Tests/BotNeuralBrainEvalTests.cs"

    # Config-file parser fuzzer: mutated botmod.json documents must never throw
    # and always land inside Normalize's documented ranges (same Newtonsoft
    # gate as above; compiles only the engine-free Config sources).
    run_game_suite configfuzz \
      "${config_src[@]}" \
      "$root/tests/BotMod.Tests/MutantBytes.cs" \
      "$root/tests/BotMod.Tests/BotConfigFuzzTests.cs"

    # Character-file parser fuzzer: the same trust boundary as botmod.json
    # (operator hand-edited JSON at every startup) gets the same treatment:
    # byte-level and structure-aware mutants must fail cleanly behind Warn or
    # land inside the Normalize+difficulty-lerp contract with canonical keys.
    run_game_suite charfuzz \
      "${character_src[@]}" \
      "$root/tests/BotMod.Tests/MutantBytes.cs" \
      "$root/tests/BotMod.Tests/BotCharacterFuzzTests.cs"

    # Admin-setter fuzzer: the web API and console hand caller-supplied target
    # names, team numbers and difficulty/team-count levels straight to these
    # setters, so they are the untrusted-input path the loader fuzzer does not
    # cover. A write must be readable back under every spelling of the name, an
    # unknown "vs" target must change nothing, and the config must stay inside
    # the Normalize contract whatever the sequence.
    run_game_suite adminsettersfuzz \
      "${config_src[@]}" \
      "$root/tests/BotMod.Tests/BotAdminSettersFuzzTests.cs"

    # Character-file ingestion pins: NaN/Infinity literals and out-of-range
    # traits in hand-edited characters.json must land finite and in range
    # (BotCharacter.Normalize) instead of reaching the neural obs vector and
    # the aim-bias rotation.
    run_game_suite botchararith \
      "${character_src[@]}" \
      "$root/tests/BotMod.Tests/BotCharacterArithTests.cs"

    # Gun-id classification, fixed vectors: the "mixed" literal expands
    # case-insensitively, a named id passes through verbatim, an empty pool
    # falls back to the default gun instead of an out-of-range index.
    run_game_suite weaponprofile \
      "${config_src[@]}" \
      "$root/tests/BotMod.Tests/WeaponProfileTests.cs"

    # The same classifier under fuzzing: client-supplied gun ids (web
    # spawnNear, `bot player`, `bot weapon`) decide every bot's fire rate,
    # burst, damage, range and magazine pacing, so an arbitrary id must
    # never throw, never come back as a different gun than the one asked
    # for, and never yield a non-finite or non-positive stat.
    run_game_suite weaponprofilefuzz \
      "${config_src[@]}" \
      "$root/tests/BotMod.Tests/WeaponProfileFuzzTests.cs"
  else
    declared_suites+=(neuralfuzz neuraleval configfuzz charfuzz adminsettersfuzz botchararith weaponprofile weaponprofilefuzz)
    echo "skip neuralfuzz, neuraleval, configfuzz, charfuzz, adminsettersfuzz, botchararith, weaponprofile, weaponprofilefuzz (Newtonsoft.Json.dll not found; set SEVENDTD_DS_DIR or SEVENDTD_GAME_DIR to a game install)"
  fi

  need_refs=(netstandard.dll System.Runtime.dll UnityEngine.CoreModule.dll UnityEngine.PhysicsModule.dll Assembly-CSharp.dll Newtonsoft.Json.dll Utf8Json.dll System.Xml.dll LogLibrary.dll SpaceWizards_HttpListener.dll)
  have_all=true
  for dll in "${need_refs[@]}"; do [[ -f "$managed/$dll" ]] || have_all=false; done
  harmony=""
  if [[ -n "$managed" ]]; then
    candidate="$(dirname "$(dirname "$managed")")/Mods/0_TFP_Harmony/0Harmony.dll"
    if [[ -f "$candidate" ]]; then harmony="$candidate"; fi
  fi
  if $have_all && [[ -n "$harmony" ]]; then
    # LC_ALL=C sort, newline-delimited: sort -z is a GNU extension and the
    # mod tree holds no file whose name carries a newline. Collected with a
    # read loop, not mapfile: mapfile needs bash 4 and macOS ships 3.2.
    mod_sources=()
    while IFS= read -r src; do mod_sources+=("$src"); done \
      < <(find "$root/Source/BotMod" -type f -name '*.cs' | LC_ALL=C sort)
    mod_refs=()
    for dll in "${need_refs[@]}"; do mod_refs+=(-r:"$managed/$dll"); done
    mod_refs+=(-r:"$managed/mscorlib.dll" -r:"$managed/System.dll" -r:"$managed/System.Core.dll" -r:"$harmony")
    # The exes reference the game's enum/handler types; Assembly-CSharp plus
    # UnityEngine.CoreModule (base-class field types) and Unity.Burst (custom
    # attributes mono resolves while JIT-ing game methods) are copied beside
    # them for mono's runtime probe (same pattern as the Newtonsoft copies).
    cp "$managed/Assembly-CSharp.dll" "$managed/UnityEngine.CoreModule.dll" "$managed/Unity.Burst.dll" "$work/"

    run_mod_suite teamshammer \
      "$root/tests/BotMod.Tests/TeamAssignmentsConcurrencyTests.cs"

    # Config load/validation: unknown-key detection, range clamping, .bak recovery.
    run_mod_suite botconfig \
      "$root/tests/BotMod.Tests/BotConfigLoadTests.cs"

    # Authorization matrix (deny side): the web API must declare permission
    # level 0 for every request-method slot and the console command must keep
    # its default level 0. Handlers cannot be constructed outside a running
    # server (the ctor registers with the live AdminTools singleton), so the
    # suite asserts the constant-returning declarations on ctor-less instances.
    run_mod_suite webapiauthz \
      "$root/tests/BotMod.Tests/WebApiAuthzTests.cs"

    # Numeric-correctness pins for BotBrain's hash arithmetic (int*uint sign
    # promotion made negative-id bots pass the camper gate every time).
    run_mod_suite botarith \
      "$root/tests/BotMod.Tests/BotBrainArithTests.cs"
  else
    declared_suites+=(teamshammer botconfig webapiauthz botarith)
    echo "skip teamshammer, botconfig, webapiauthz, botarith (game DLLs or 0_TFP_Harmony not found; set SEVENDTD_DS_DIR to a dedicated-server install)"
  fi
fi

# all_suites is the contract for `--list` and for the SUITE= filter, so a
# run_suite call missing from it, or a listed suite with no call behind it, is
# a bug in this script rather than in the tests.
is_declared() { # <name>
  local name="$1" seen
  for seen in "${declared_suites[@]}"; do [[ "$seen" == "$name" ]] && return 0; done
  return 1
}
for suite in "${declared_suites[@]}"; do
  known=false
  for listed in "${all_suites[@]}"; do [[ "$listed" == "$suite" ]] && known=true; done
  if ! $known; then
    echo "error: suite '$suite' runs but is not in all_suites; add it there and to --list" >&2
    exit 1
  fi
done
# Unfiltered, every listed suite must have run (or declared, for the game
# suites that self-skip without a game install). Filtered, the check is
# per-request so a listed suite with no call behind it cannot hide.
if ((${#filter[@]} == 0)); then
  for suite in "${all_suites[@]}"; do
    if ! is_declared "$suite"; then
      echo "error: suite '$suite' is listed in all_suites but no run_suite call declares it" >&2
      exit 1
    fi
  done
else
  for suite in "${filter[@]}"; do
    if ! is_declared "$suite"; then
      echo "error: suite '$suite' is listed in all_suites but no run_suite call declares it" >&2
      exit 1
    fi
  done
fi

# The mirror of that check on the other side: every file in tests/ must be
# named by a run_suite call above. A test file no suite compiles never runs
# and never fails, so the coverage it appears to provide is fictional.
while IFS= read -r test_file; do
  name="${test_file##*/}"
  if ! grep -qF "$name" "${BASH_SOURCE[0]}"; then
    echo "error: $test_file is in tests/ but no run_suite call compiles it" >&2
    exit 1
  fi
done < <(find "$root/tests" -type f -name '*.cs' | sort)
