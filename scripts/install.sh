#!/usr/bin/env bash
# Deploy dist/BotMod into the dedicated server's Mods dir.
#
# The payload is staged in a sibling of Mods/BotMod and swapped in with a
# single rename, so a copy that fails part way leaves the running install
# untouched instead of replacing it with a half-written mod dir.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/server-dir.sh"
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

# Staged beside the target so the swap is a same-filesystem rename.
STAGE="$DS/Mods/.BotMod.staging.$$"
rm -rf "$STAGE"
mkdir -p "$STAGE"
trap 'rm -rf "$STAGE"' EXIT
# "$SRC/." not "$SRC"/*: a dotfile in the payload is payload, not a glob miss.
cp -r "$SRC/." "$STAGE/"
if [[ ! -f "$STAGE/BotMod.dll" ]]; then
  echo "ERROR: staged payload is missing BotMod.dll; nothing was installed" >&2
  exit 1
fi

# The deployed Config/botmod.json accumulates operator state the repo default
# lacks (Enabled, BotVs*, squad mode, team assignments persisted by the web
# dashboard / console). The swap must not destroy it: copy it into the staged
# payload before the target goes away.
kept=0
for f in botmod.json botmod.json.bak; do
  if [[ -f "$DST/Config/$f" ]]; then
    mkdir -p "$STAGE/Config"
    cp "$DST/Config/$f" "$STAGE/Config/$f"
    kept=1
  fi
done

rm -rf "$DST"
mv "$STAGE" "$DST"
trap - EXIT
if [[ "$kept" == 1 ]]; then
  echo "Preserved operator config across reinstall: $DST/Config/botmod.json(.bak)"
fi
echo "Installed -> $DST"
ls -la "$DST"
ls -la "$DST/Config" 2>&1 || true
