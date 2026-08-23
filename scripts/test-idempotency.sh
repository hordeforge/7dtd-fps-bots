#!/usr/bin/env bash
# Compile and run the pure-BCL unit + fuzz suites (tests/BotMod.Web.Tests)
# with mcs + mono. IdempotencyLedger and AtomicTextFile are pure BCL, so no
# game DLL references are needed. The BotNeuralBrain weights-file fuzzer also
# parses JSON, so it additionally needs Newtonsoft.Json.dll from the game
# install (probed like scripts/build.sh) and is skipped when that is absent.
# Not part of `make check` (CI runners have no mono); run locally:
#
#   bash scripts/test-idempotency.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

run_suite() { # <name> <sources...>
  local name="$1"; shift
  mcs -warnaserror -out:"$work/$name.exe" "$@" > /dev/null
  mono "$work/$name.exe"
}

run_suite idempotency \
  "$root/Source/BotMod/Web/IdempotencyLedger.cs" \
  "$root/tests/BotMod.Web.Tests/IdempotencyLedgerTests.cs"

run_suite atomictextfile \
  "$root/Source/BotMod/Config/AtomicTextFile.cs" \
  "$root/tests/BotMod.Web.Tests/AtomicTextFileTests.cs"

# Differential model fuzzer over the untrusted requestId surface.
run_suite idempotencyfuzz \
  "$root/Source/BotMod/Web/IdempotencyLedger.cs" \
  "$root/tests/BotMod.Web.Tests/IdempotencyLedgerFuzzTests.cs"

# Weights-file parser fuzzer: needs the game install's Newtonsoft.Json.dll,
# copied beside the exe so mono resolves the reference at runtime.
srv="${SEVENDTD_DS_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
client="${SEVENDTD_GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days To Die}"
managed=""
if [[ -f "$srv/7DaysToDieServer_Data/Managed/Assembly-CSharp.dll" ]]; then
  managed="$srv/7DaysToDieServer_Data/Managed"
elif [[ -f "$client/7DaysToDie_Data/Managed/Assembly-CSharp.dll" ]]; then
  managed="$client/7DaysToDie_Data/Managed"
fi
if [[ -n "$managed" && -f "$managed/Newtonsoft.Json.dll" && -f "$managed/netstandard.dll" ]]; then
  cp "$managed/Newtonsoft.Json.dll" "$managed/netstandard.dll" "$work/"
  mcs -warnaserror -langversion:latest -r:"$work/Newtonsoft.Json.dll" -r:"$work/netstandard.dll" \
    -out:"$work/neuralfuzz.exe" \
    "$root/Source/BotMod/AI/BotNeuralBrain.cs" \
    "$root/tests/BotMod.Web.Tests/BotNeuralBrainFuzzTests.cs" > /dev/null
  # Repo root as argv[1] so the fuzzer can find evolved/best.json.
  mono "$work/neuralfuzz.exe" "$root"
else
  echo "skip neuralfuzz (Newtonsoft.Json.dll not found; set SEVENDTD_DS_DIR or SEVENDTD_GAME_DIR to a game install)"
fi

# Team-map concurrency hammer: BotConfig pulls ModApi -> engine types, so this
# compiles the FULL mod source against the game DLLs (same reference set as
# scripts/build.sh) and is skipped without a game install. Harmony lives in
# the server's (or client's) Mods dir, where build.sh finds it too.
need_refs=(netstandard.dll System.Runtime.dll UnityEngine.CoreModule.dll UnityEngine.PhysicsModule.dll UnityEngine.AIModule.dll Assembly-CSharp.dll Newtonsoft.Json.dll Utf8Json.dll System.Xml.dll LogLibrary.dll)
have_all=true
for dll in "${need_refs[@]}"; do [[ -f "$managed/$dll" ]] || have_all=false; done
harmony=""
if [[ -n "$managed" ]]; then
  candidate="$(dirname "$(dirname "$managed")")/Mods/0_TFP_Harmony/0Harmony.dll"
  if [[ -f "$candidate" ]]; then harmony="$candidate"; fi
fi
if $have_all && [[ -n "$harmony" ]]; then
  mapfile -d '' sources < <(find "$root/Source/BotMod" -type f -name '*.cs' -print0 | sort -z)
  refs=()
  for dll in "${need_refs[@]}"; do refs+=(-r:"$managed/$dll"); done
  refs+=(-r:"$managed/mscorlib.dll" -r:"$managed/System.dll" -r:"$managed/System.Core.dll" -r:"$harmony")
  mcs -nostdlib -sdk:4.7.2 -warnaserror -langversion:latest "${refs[@]}" \
    -out:"$work/teamshammer.exe" "${sources[@]}" \
    "$root/tests/BotMod.Web.Tests/TeamAssignmentsConcurrencyTests.cs" > /dev/null
  mono "$work/teamshammer.exe"
  # Config load/validation: unknown-key detection, range clamping, .bak recovery.
  mcs -nostdlib -sdk:4.7.2 -warnaserror -langversion:latest "${refs[@]}" \
    -out:"$work/botconfig.exe" "${sources[@]}" \
    "$root/tests/BotMod.Web.Tests/BotConfigLoadTests.cs" > /dev/null
  mono "$work/botconfig.exe"
else
  echo "skip teamshammer (game DLLs or 0_TFP_Harmony not found; set SEVENDTD_DS_DIR to a dedicated-server install)"
fi
