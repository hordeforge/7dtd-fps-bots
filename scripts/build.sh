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
SRV="${SEVENDTD_DS_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
CLIENT="${SEVENDTD_GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days To Die}"
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

# Version drift guard: BotModVersion.Number is canonical. ModInfo.xml is what
# the engine's mod listing shows and cannot reference the C# constant, so the
# build fails when they disagree instead of shipping mismatched versions.
CS_VERSION="$(sed -n 's/.*const string Number = "\([^"]*\)";/\1/p' "$SRC/Core/BotModVersion.cs" || true)"
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
  cp "$ROOT/config/botmod.json" "$OUT/Config/botmod.json"
  if [ -f "$ROOT/config/characters.json" ]; then cp "$ROOT/config/characters.json" "$OUT/Config/characters.json"; fi
  if [ -f "$ROOT/evolved/best.json" ]; then mkdir -p "$OUT/evolved" && cp "$ROOT/evolved/best.json" "$OUT/evolved/best.json"; fi
  cp "$ROOT/config/entityclasses.xml" "$OUT/Config/entityclasses.xml"
  echo "patch -> $OUT/Config/entityclasses.xml"
}

build_webmod() {
  # Compile the TypeScript panel to bundle.js (dashboard loads
  # /webmods/BotMod/bundle.js); emit lands next to bundle.ts per
  # WebMod/tsconfig.json, then bundle.js + styling.css ship in the payload.
  bunx -p "typescript@$TSC_VERSION" tsc -p "$SRC/WebMod/tsconfig.json"
  mkdir -p "$OUT/WebMod"
  cp "$SRC/WebMod/bundle.js" "$OUT/WebMod/bundle.js"
  cp "$SRC/WebMod/styling.css" "$OUT/WebMod/styling.css"
}

BUILD_BACKEND="${SEVENDTD_BUILD_BACKEND:-auto}"
if [[ "$BUILD_BACKEND" != "mcs" ]] && command -v dotnet >/dev/null 2>&1 && [[ -n "$(dotnet --list-sdks 2>/dev/null)" ]]; then
  echo "Building with dotnet SDK against: $MANAGED"
  dotnet build "$SRC/BotMod.csproj" -c Release \
    -p:GameManagedDir="$MANAGED" -p:HarmonyPath="$HARMONY" \
    -p:BotModOutput="$OUT/"
  copy_payload
  build_webmod
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
)
# sort -z: deterministic compile order regardless of readdir order.
mapfile -d '' sources < <(find "$SRC" -type f -name '*.cs' -print0 | sort -z)
# -warnaserror: the tree compiles warning-free; keep it that way.
mcs -nostdlib -sdk:4.7.2 -target:library -optimize+ -langversion:7.2 -warnaserror \
  -out:"$OUT/BotMod.dll" "${refs[@]}" "${sources[@]}"
copy_payload
build_webmod
echo "OK -> $OUT/BotMod.dll"
ls -la "$OUT"
