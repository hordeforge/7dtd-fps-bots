#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DS="${SEVENDTD_DS_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
SRC="$ROOT/dist/BotMod"
DST="$DS/Mods/BotMod"
if [[ ! -f "$SRC/BotMod.dll" ]]; then echo "Run scripts/build.sh first" >&2; exit 1; fi
if [[ ! -f "$DS/7DaysToDieServer_Data/Managed/Assembly-CSharp.dll" ]]; then
  echo "ERROR: '$DS' does not look like a 7 Days to Die Dedicated Server install" >&2
  echo "(missing 7DaysToDieServer_Data/Managed/Assembly-CSharp.dll)." >&2
  echo "Set SEVENDTD_DS_DIR to the server install root and retry:" >&2
  echo "  SEVENDTD_DS_DIR='/path/to/7 Days to Die Dedicated Server' make install" >&2
  exit 1
fi

# The deployed Config/botmod.json accumulates operator state the repo default
# lacks (Enabled, BotVs*, squad mode, team assignments persisted by the web
# dashboard / console). The rm -rf below must not destroy it: stage it out,
# refresh the payload, put it back.
PRESERVE="$(mktemp -d)"
trap 'rm -rf "$PRESERVE"' EXIT
kept=0
for f in botmod.json botmod.json.bak; do
  if [[ -f "$DST/Config/$f" ]]; then
    mkdir -p "$PRESERVE/Config"
    cp "$DST/Config/$f" "$PRESERVE/Config/$f"
    kept=1
  fi
done

rm -rf "$DST"
mkdir -p "$DST"
cp -r "$SRC/"* "$DST/"
if [[ "$kept" == 1 ]]; then
  cp "$PRESERVE"/Config/* "$DST/Config/"
  echo "Preserved operator config across reinstall: $DST/Config/botmod.json(.bak)"
fi
echo "Installed -> $DST"
ls -la "$DST"
ls -la "$DST/Config" 2>&1 || true
