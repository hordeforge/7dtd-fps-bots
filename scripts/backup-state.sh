#!/usr/bin/env bash
# Snapshot every piece of mutable BotMod state into a timestamped directory
# that can be restored by scripts/restore-state.sh.
#
# What is mutable and host-local: the deployed operator config
# (<server>/Mods/BotMod/Config/botmod.json + .bak) and the champion weights
# in evolved/. Everything else the mod writes is process memory (see
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
source "$ROOT/scripts/server-dir.sh"

DEST_ROOT="${1:-${BOTMOD_STATE_BACKUP_DIR:-$ROOT/backups}}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
SNAP="$DEST_ROOT/$STAMP"

# sha256sum on Linux, shasum on macOS; the MANIFEST is verified on restore, so
# the digest tool has to exist on both sides of that trip.
if command -v sha256sum > /dev/null; then SHA=(sha256sum)
elif command -v shasum > /dev/null; then SHA=(shasum -a 256)
else
  echo "ERROR: need sha256sum or shasum to write a verifiable MANIFEST" >&2
  exit 1
fi

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
  echo "# restore: bash scripts/restore-state.sh '$SNAP'"
  (cd "$SNAP" && find . -type f ! -name MANIFEST | sed 's|^\./||' | sort | xargs "${SHA[@]}")
} > "$SNAP/MANIFEST"

echo "Backed up $copied file(s) -> $SNAP"
sed 's/^/# /' "$SNAP/MANIFEST"
echo "Restore: bash scripts/restore-state.sh '$SNAP'"
