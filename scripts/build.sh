#!/usr/bin/env bash
set -euo pipefail
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
  echo "ERROR: Assembly-CSharp.dll not found" >&2; exit 1
fi
OUT="$ROOT/dist/BotMod"
SRC="$ROOT/Source/BotMod"
mkdir -p "$OUT/Config"

BUILD_BACKEND="${SEVENDTD_BUILD_BACKEND:-auto}"
if [[ "$BUILD_BACKEND" != "mcs" ]] && command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q .; then
  echo "Building with dotnet SDK against: $MANAGED"
  dotnet build "$SRC/BotMod.csproj" -c Release \
    -p:GameManagedDir="$MANAGED" -p:HarmonyPath="$HARMONY" \
    -p:BotModOutput="$OUT/"
  cp "$SRC/ModInfo.xml" "$OUT/ModInfo.xml"
  cp "$ROOT/config/botmod.json" "$OUT/Config/botmod.json"
  echo "OK -> $OUT/BotMod.dll"
  ls -la "$OUT"
  exit 0
fi
if [[ "$BUILD_BACKEND" == "dotnet" ]]; then echo "ERROR: dotnet backend requested but no SDK"; exit 1; fi
command -v mcs >/dev/null 2>&1 || { echo "ERROR: mcs not found"; exit 1; }
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
  -r:"$MANAGED/UnityEngine.AIModule.dll"
  -r:"$HARMONY"
  -r:"$MANAGED/Newtonsoft.Json.dll"
  -r:"$MANAGED/LogLibrary.dll"
)
mapfile -d '' sources < <(find "$SRC" -type f -name '*.cs' -print0)
mcs -nostdlib -sdk:4.7.2 -target:library -optimize+ -langversion:7.2 \
  -out:"$OUT/BotMod.dll" "${refs[@]}" "${sources[@]}"
cp "$SRC/ModInfo.xml" "$OUT/ModInfo.xml"
cp "$ROOT/config/botmod.json" "$OUT/Config/botmod.json"
echo "OK -> $OUT/BotMod.dll"
ls -la "$OUT"
