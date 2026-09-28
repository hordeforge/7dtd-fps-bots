#!/usr/bin/env bash
# Build BotMod.dll plus the mod payload into dist/BotMod.
#
# Backends (SEVENDTD_BUILD_BACKEND=auto|mcs|dotnet):
#   dotnet  SDK-style build per Source/BotMod/BotMod.csproj (preferred)
#   mcs     mono compiler against the game's Managed DLLs (fallback)
# Both compile the same sources against the Steam dedicated server (or client)
# Managed directory, then assemble the identical payload below.
set -euo pipefail
export LC_ALL=C TZ=UTC

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STEAM_COMMON="${XDG_DATA_HOME:-$HOME/.local/share}/Steam/steamapps/common"
SRV="${SEVENDTD_DS_DIR:-$STEAM_COMMON/7 Days to Die Dedicated Server}"
CLIENT="${SEVENDTD_GAME_DIR:-$STEAM_COMMON/7 Days To Die}"
if [[ -f "$SRV/7DaysToDieServer_Data/Managed/Assembly-CSharp.dll" ]]; then
  MANAGED="$SRV/7DaysToDieServer_Data/Managed"
  HARMONY="$SRV/Mods/0_TFP_Harmony/0Harmony.dll"
elif [[ -f "$CLIENT/7DaysToDie_Data/Managed/Assembly-CSharp.dll" ]]; then
  MANAGED="$CLIENT/7DaysToDie_Data/Managed"
  HARMONY="$CLIENT/Mods/0_TFP_Harmony/0Harmony.dll"
else
  echo "ERROR: Assembly-CSharp.dll not found; looked in:" >&2
  echo "  $SRV/7DaysToDieServer_Data/Managed" >&2
  echo "  $CLIENT/7DaysToDie_Data/Managed" >&2
  echo "Install the 7 Days to Die Dedicated Server via Steam, or point" >&2
  echo "SEVENDTD_DS_DIR (dedicated server) or SEVENDTD_GAME_DIR (client) at its" >&2
  echo "install root, e.g.: SEVENDTD_DS_DIR=/path/to/'7 Days to Die Dedicated Server' make build" >&2
  exit 1
fi
OUT="$ROOT/dist/BotMod"
SRC="$ROOT/Source/BotMod"

# bun compiles the web panel; without it the C# build below would run to
# completion and then fail on a `bunx: command not found` after the work.
if ! command -v bun >/dev/null 2>&1; then
  echo "ERROR: bun not found; the web panel compiles through bunx (https://bun.sh)" >&2
  exit 1
fi

# Version drift guard: BotModVersion.Number is canonical. ModInfo.xml is what
# the engine's mod listing shows and cannot reference the C# constant, so the
# build fails when they disagree instead of shipping mismatched versions.
CS_VERSION="$(sed -n 's/.*const string Number = "\([^"]*\)".*/\1/p' "$SRC/Core/BotModVersion.cs" || true)"
XML_VERSION="$(sed -n 's/.*<Version value="\([^"]*\)".*/\1/p' "$SRC/ModInfo.xml" || true)"
if [[ -z "$CS_VERSION" || "$CS_VERSION" != "$XML_VERSION" ]]; then
  echo "ERROR: version drift: Source/BotMod/Core/BotModVersion.cs=$CS_VERSION vs Source/BotMod/ModInfo.xml=$XML_VERSION" >&2
  echo "Bump both together (single commit) and add a CHANGELOG.md entry." >&2
  exit 1
fi

# Pinned external tool versions (tsc etc.), shared with lint-webui.sh so the
# shipped bundle and its freshness gate always compile with the same tsc.
source "$ROOT/scripts/tool-versions.sh"

# Stage from scratch: leftover files from removed/renamed sources must not
# survive into the installed mod.
rm -rf "$OUT"
mkdir -p "$OUT/Config"

copy_payload() {
  cp "$SRC/ModInfo.xml" "$OUT/ModInfo.xml"
  # The mod is redistributed as a zip to server operators; the license travels
  # with it rather than staying only in the repo it was built from.
  cp "$ROOT/LICENSE" "$OUT/LICENSE"
  cp "$ROOT/config/botmod.json" "$OUT/Config/botmod.json"
  # Both files fall back to defaults at runtime, so their absence is not a
  # build failure, but a silently thinner payload is a shipped surprise.
  if [ -f "$ROOT/config/characters.json" ]; then
    cp "$ROOT/config/characters.json" "$OUT/Config/characters.json"
  else
    echo "WARNING: config/characters.json not found; payload ships with default characters" >&2
  fi
  if [ -f "$ROOT/evolved/best.json" ]; then
    mkdir -p "$OUT/evolved" && cp "$ROOT/evolved/best.json" "$OUT/evolved/best.json"
  else
    echo "WARNING: evolved/best.json not found; payload ships without GA champion weights" >&2
  fi
  cp "$ROOT/config/entityclasses.xml" "$OUT/Config/entityclasses.xml"
  echo "patch -> $OUT/Config/entityclasses.xml"
}

build_webmod() {
  # Compile the TypeScript panel to bundle.js (dashboard loads
  # /webmods/BotMod/bundle.js); emit lands next to bundle.ts per
  # WebMod/tsconfig.json, then the minified bundle.js + styling.css ship in
  # the payload. The webserver serves them uncompressed, so the shipped bytes
  # are the whole download; scripts/webmod-minify.sh is the same step the
  # lint-webui freshness gate runs.
  bunx -p "typescript@$TSC_VERSION" tsc -p "$SRC/WebMod/tsconfig.json"
  bash "$ROOT/scripts/webmod-minify.sh" "$SRC/WebMod/bundle.js" "$SRC/WebMod/bundle.js"
  mkdir -p "$OUT/WebMod"
  cp "$SRC/WebMod/bundle.js" "$OUT/WebMod/bundle.js"
  cp "$SRC/WebMod/styling.css" "$OUT/WebMod/styling.css"
}

# The payload is installed on servers as-is, so a compiler default that
# silently adds a file (the SDK's portable pdb is the one that has bitten us)
# must fail the build rather than ship.
assert_payload_clean() {
  local stray
  stray="$(find "$OUT" -type f \( -name '*.pdb' -o -name '*.mdb' \) -print)"
  if [ -n "$stray" ]; then
    echo "ERROR: debug symbols in the payload (both backends must ship none):" >&2
    echo "$stray" >&2
    exit 1
  fi
}

BUILD_BACKEND="${SEVENDTD_BUILD_BACKEND:-auto}"
if [[ "$BUILD_BACKEND" != "mcs" ]] && command -v dotnet >/dev/null 2>&1 && [[ -n "$(dotnet --list-sdks 2>/dev/null)" ]]; then
  echo "Building with dotnet SDK against: $MANAGED"
  dotnet build "$SRC/BotMod.csproj" -c Release \
    -p:GameManagedDir="$MANAGED" -p:HarmonyPath="$HARMONY" \
    -p:BotModOutput="$OUT/"
  copy_payload
  build_webmod
  assert_payload_clean
  echo "OK -> $OUT/BotMod.dll"
  ls -la "$OUT"
  exit 0
fi
if [[ "$BUILD_BACKEND" == "dotnet" ]]; then echo "ERROR: dotnet backend requested but no SDK" >&2; exit 1; fi
command -v mcs >/dev/null 2>&1 || { echo "ERROR: mcs not found" >&2; exit 1; }
echo "Building with mcs against: $MANAGED"
refs=(
  -r:"$MANAGED/mscorlib.dll"
  -r:"$MANAGED/netstandard.dll"
  -r:"$MANAGED/System.dll"
  -r:"$MANAGED/System.Core.dll"
  -r:"$MANAGED/System.Runtime.dll"
  -r:"$MANAGED/Assembly-CSharp.dll"
  -r:"$MANAGED/UnityEngine.CoreModule.dll"
  -r:"$MANAGED/UnityEngine.PhysicsModule.dll"
  -r:"$HARMONY"
  -r:"$MANAGED/Newtonsoft.Json.dll"
  -r:"$MANAGED/Utf8Json.dll"
  -r:"$MANAGED/System.Xml.dll"
  -r:"$MANAGED/LogLibrary.dll"
  -r:"$MANAGED/SpaceWizards_HttpListener.dll"
)
# LC_ALL=C sort: deterministic compile order regardless of readdir order.
# A newline-delimited list, not find -print0 | sort -z: sort -z is a GNU
# extension, and the payload ships no file whose name holds a newline.
mapfile -t sources < <(find "$SRC" -type f -name '*.cs' | LC_ALL=C sort)
# -warnaserror: the tree compiles warning-free; keep it that way.
mcs -nostdlib -sdk:4.7.2 -target:library -optimize+ -langversion:7.2 -warnaserror \
  -out:"$OUT/BotMod.dll" "${refs[@]}" "${sources[@]}"
copy_payload
build_webmod
assert_payload_clean
echo "OK -> $OUT/BotMod.dll"
ls -la "$OUT"
