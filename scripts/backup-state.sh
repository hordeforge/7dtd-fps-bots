#!/usr/bin/env bash
# Snapshot every piece of mutable BotMod state into a timestamped directory
# that can be restored by scripts/restore-state.sh.
#
# What is mutable and host-local: the deployed operator config
# (<server>/Mods/BotMod/Config/botmod.json + .bak, or the file BOTMOD_CONFIG
# names when the deployment mounts its config elsewhere) and the champion
# weights in evolved/. Everything else the mod writes is process memory (see
# docs/recovery.md), so this is the whole recovery surface.
#
# Destination: $BOTMOD_STATE_BACKUP_DIR, else <repo>/backups (git-ignored).
# Point the variable at a path that does NOT live on the dedi host (external
# disk, NAS, second machine) to get protection against host loss; the repo
# default only protects against `make uninstall` and a fat-fingered rm.
#
# Usage:
#   bash scripts/backup-state.sh [destination-root]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

case "${1:-}" in
  -h | --help)
    sed -n '2,17p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    exit 0
    ;;
  -*)
    echo "ERROR: unknown option '$1' (see --help)" >&2
    echo "usage: bash scripts/backup-state.sh [destination-root]" >&2
    exit 2
    ;;
esac
if (($# > 1)); then
  echo "ERROR: takes at most one argument (destination-root), got $#" >&2
  echo "usage: bash scripts/backup-state.sh [destination-root]" >&2
  exit 2
fi

source "$ROOT/scripts/server-dir.sh"

DEST_ROOT="${1:-${BOTMOD_STATE_BACKUP_DIR:-$ROOT/backups}}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
SNAP="$DEST_ROOT/$STAMP"

# The MANIFEST is verified on restore, so the digest tool has to exist on both
# sides of that trip; scripts/digest.sh picks it and fails when neither is there.
source "$ROOT/scripts/digest.sh"

mkdir -p "$SNAP/evolved"
copied=0

# copy_state <source> <name-in-snapshot>: one file, recorded in the MANIFEST.
copy_state() {
  local src="$1" name="$2"
  [[ -f "$src" ]] || return 0
  cp "$src" "$SNAP/$name"
  copied=$((copied + 1))
}

copy_state "$DS/Mods/BotMod/Config/botmod.json" "botmod.json"
copy_state "$DS/Mods/BotMod/Config/botmod.json.bak" "botmod.json.bak"
copy_state "$ROOT/evolved/best.json" "evolved/best.json"
copy_state "$ROOT/evolved/best.meta.json" "evolved/best.meta.json"

# A deployment that redirects the config with BOTMOD_CONFIG keeps every
# persisted admin decision outside the mod dir, so the server-relative copies
# above are not it. Snapshot the named file under its own name; without this
# the backup silently omits the live config and `make restore` restores a file
# the server never reads. The MANIFEST header records where it came from.
CONFIG_PATH="${BOTMOD_CONFIG:-}"
if [[ -n "$CONFIG_PATH" ]]; then
  copy_state "$CONFIG_PATH" "botmod.config-path.json"
  copy_state "$CONFIG_PATH.bak" "botmod.config-path.json.bak"
fi

if [[ "$copied" == 0 ]]; then
  rmdir "$SNAP/evolved" "$SNAP" 2>/dev/null || true
  echo "ERROR: no BotMod state found to back up" >&2
  echo "  server config: $DS/Mods/BotMod/Config/botmod.json (is the mod installed?)" >&2
  echo "  champions:     $ROOT/evolved/best.json" >&2
  echo "Set SEVENDTD_DS_DIR to the server install root and retry." >&2
  exit 1
fi

# MANIFEST header lines start with '#', digests are plain `<hash>  <name>` so
# restore can verify with the standard tools. Written last: a MANIFEST that
# exists means the snapshot is complete.
{
  echo "# BotMod state snapshot $STAMP"
  echo "# server=$DS"
  if [[ -n "$CONFIG_PATH" ]]; then echo "# config-path=$CONFIG_PATH"; fi
  echo "# restore: bash scripts/restore-state.sh '$SNAP'"
  (cd "$SNAP" && find . -type f ! -name MANIFEST | sed 's|^\./||' | sort | xargs "${SHA[@]}")
} > "$SNAP/MANIFEST"

echo "Backed up $copied file(s) -> $SNAP"
sed 's/^/# /' "$SNAP/MANIFEST"
echo "Restore: bash scripts/restore-state.sh '$SNAP'"
