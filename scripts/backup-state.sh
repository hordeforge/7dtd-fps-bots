#!/usr/bin/env bash
# Snapshot every piece of mutable BotMod state into a timestamped directory
# that can be restored by scripts/restore-state.sh.
#
# What is mutable and host-local: the deployed operator config
# (<server>/Mods/BotMod/Config/botmod.json + .bak, or the file BOTMOD_CONFIG
# names when the deployment mounts its config elsewhere, or the container
# path ModApi.PersistConfigField writes to when it exists) and the champion
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

# The MANIFEST is verified on restore, so the digest tool has to exist on both
# sides of that trip; scripts/digest.sh picks it and fails when neither is there.
source "$ROOT/scripts/digest.sh"

mkdir -p "$DEST_ROOT"
# The stamp has one-second resolution, so a second backup in the same second
# (a retried `make backup`, two hosts sharing a NAS destination) names the same
# directory. Reusing it would blend two states: this run overwrites the files it
# found and leaves the earlier run's files it did not, and the MANIFEST written
# last describes that mix, so restore verifies it and hands back a torn
# snapshot. Exclusive mkdir plus a numeric suffix gives every run its own
# directory, as tools/ga/evolve.py does for its run dirs.
SNAP="$DEST_ROOT/$STAMP"
n=1
while true; do
  if mkdir "$SNAP" 2>/dev/null; then break; fi
  if [[ ! -d "$SNAP" ]]; then
    echo "ERROR: cannot create snapshot directory '$SNAP'" >&2
    exit 1
  fi
  n=$((n + 1))
  SNAP="$DEST_ROOT/${STAMP}_$n"
done
mkdir -p "$SNAP/evolved"
copied=0

# copy_state <source> <name-in-snapshot>: one file, recorded in the MANIFEST.
copy_state() {
  local src="$1" name="$2"
  [[ -f "$src" ]] || return 0
  cp "$src" "$SNAP/$name"
  copied=$((copied + 1))
}

# copy_config <primary> <name-in-snapshot>: the operator config, with the
# last-known-good standing in for a primary that is gone or empty. A zero-byte
# primary is a state a torn write or a full disk leaves behind, and copying it
# verbatim would make the snapshot's recovery point the one state that is worse
# than no config at all: verification passes, restore installs it, and the
# server resets every persisted setting to defaults. BotConfig.Load already
# falls back to the .bak in that situation, so the snapshot does too, and
# records which copy served it so an operator reading the MANIFEST knows the
# live primary was not what got backed up. An empty primary with no .bak either
# is copied as the empty file it is: there is nothing better to hold, and the
# header line below says so.
copy_config() {
  local primary="$1" name="$2" bak="$1.bak"
  local use="$primary" from=""
  if [[ ! -s "$primary" && -s "$bak" ]]; then
    use="$bak"
    from="$bak"
  fi
  copy_state "$use" "$name"
  [[ -n "$from" ]] || return 0
  if [[ -s "$primary" ]]; then
    echo "WARNING: '$primary' is empty; snapshotting '$from' as $name instead." >&2
  else
    echo "WARNING: '$primary' is missing; snapshotting '$from' as $name instead." >&2
  fi
  fallbacks="$fallbacks$name=$from"$'\n'
}

fallbacks=""

copy_config "$DS/Mods/BotMod/Config/botmod.json" "botmod.json"
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
  copy_config "$CONFIG_PATH" "botmod.config-path.json"
  copy_state "$CONFIG_PATH.bak" "botmod.config-path.json.bak"
fi

# The container deployment writes here too: ModApi.ConfigWritePaths lists
# /mods/BotMod/Config/botmod.json alongside the mod-dir copy whenever the
# file exists, so in that deployment this is where the admin decisions live.
# Omitting it from the snapshot would leave the one config that can be written
# outside the server root unbacked. The mod drops this candidate when
# BOTMOD_CONFIG is set, so the snapshot drops it too. BOTMOD_CONTAINER_CONFIG
# overrides the path for a mount somewhere else; absent file, nothing copied.
CONTAINER_CONFIG="${BOTMOD_CONTAINER_CONFIG:-/mods/BotMod/Config}"
if [[ -z "$CONFIG_PATH" && -n "$CONTAINER_CONFIG" ]]; then
  copy_config "$CONTAINER_CONFIG/botmod.json" "botmod.container.json"
  copy_state "$CONTAINER_CONFIG/botmod.json.bak" "botmod.container.json.bak"
fi

if [[ "$copied" == 0 ]]; then
  rmdir "$SNAP/evolved" "$SNAP" 2>/dev/null || true
  echo "ERROR: no BotMod state found to back up" >&2
  echo "  server config: $DS/Mods/BotMod/Config/botmod.json (is the mod installed?)" >&2
  echo "  container config: $CONTAINER_CONFIG/botmod.json (set BOTMOD_CONTAINER_CONFIG if mounted elsewhere)" >&2
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
  if [[ -z "$CONFIG_PATH" && ( -f "$SNAP/botmod.container.json" || -f "$SNAP/botmod.container.json.bak" ) ]]; then
    echo "# container-config=$CONTAINER_CONFIG"
  fi
  if [[ -n "$fallbacks" ]]; then
    # One line per substituted file: <name-in-snapshot>=<path it was read from>.
    while IFS= read -r line; do echo "# fallback=$line"; done <<< "$fallbacks"
  fi
  echo "# restore: bash scripts/restore-state.sh '$SNAP'"
  (cd "$SNAP" && find . -type f ! -name MANIFEST | sed 's|^\./||' | sort | xargs "${SHA[@]}")
} > "$SNAP/MANIFEST"

echo "Backed up $copied file(s) -> $SNAP"
sed 's/^/# /' "$SNAP/MANIFEST"
echo "Restore: bash scripts/restore-state.sh '$SNAP'"
