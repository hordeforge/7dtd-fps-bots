#!/usr/bin/env bash
# Remove the mod from the dedicated server's Mods dir.
#
# This deletes operator state (Config/botmod.json, team assignments, bot
# enable/disable), so it snapshots it first via scripts/backup-state.sh and
# refuses to delete if the snapshot cannot be written. Set
# BOTMOD_SKIP_BACKUP=1 to delete anyway (the state is then gone; see
# docs/recovery.md). It only requires a Mods dir, not a valid server
# install, so a half-installed server can still be cleaned up.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/server-dir.sh"
DST="$DS/Mods/BotMod"

if [[ -z "$DS" || "$DS" == "/" ]]; then
  echo "ERROR: refusing to delete from server root '$DS'" >&2
  echo "Set SEVENDTD_DS_DIR to the server install root and retry:" >&2
  echo "  SEVENDTD_DS_DIR='/path/to/7 Days to Die Dedicated Server' make uninstall" >&2
  exit 1
fi
if [[ ! -d "$DS/Mods" ]]; then
  echo "ERROR: '$DS/Mods' does not exist; '$DS' is not a server install" >&2
  echo "Set SEVENDTD_DS_DIR to the server install root and retry." >&2
  exit 1
fi
if [[ ! -d "$DST" ]]; then
  echo "Not installed -> $DST"
  exit 0
fi

if [[ ! -f "$DST/Config/botmod.json" && ! -f "$DST/Config/botmod.json.bak" ]]; then
  echo "No operator config in $DST/Config; nothing to snapshot."
elif [[ "${BOTMOD_SKIP_BACKUP:-0}" == 1 ]]; then
  echo "WARNING: BOTMOD_SKIP_BACKUP=1, deleting $DST/Config/botmod.json unrecoverably" >&2
else
  bash "$ROOT/scripts/backup-state.sh"
fi

rm -rf "$DST"
echo "Removed -> $DST"
