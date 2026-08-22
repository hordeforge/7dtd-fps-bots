#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DS="${SEVENDTD_DS_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
SRC="$ROOT/dist/BotMod"
DST="$DS/Mods/BotMod"
if [[ ! -f "$SRC/BotMod.dll" ]]; then echo "Run scripts/build.sh first"; exit 1; fi
if [[ ! -f "$DS/7DaysToDieServer_Data/Managed/Assembly-CSharp.dll" ]]; then
  echo "ERROR: '$DS' does not look like a 7 Days to Die Dedicated Server install" >&2
  echo "(missing 7DaysToDieServer_Data/Managed/Assembly-CSharp.dll)." >&2
  echo "Set SEVENDTD_DS_DIR to the server install root and retry:" >&2
  echo "  SEVENDTD_DS_DIR='/path/to/7 Days to Die Dedicated Server' make install" >&2
  exit 1
fi
rm -rf "$DST"
mkdir -p "$DST"
cp -r "$SRC/"* "$DST/"
echo "Installed -> $DST"
ls -la "$DST"
ls -la "$DST/Config" 2>&1 || true
