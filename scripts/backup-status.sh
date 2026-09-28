#!/usr/bin/env bash
# Is the most recent BotMod snapshot both restorable and recent enough to be
# the recovery point? Scheduled backups live outside this repo (cron, systemd,
# a host backup), so nothing here can tell whether the schedule is still
# running. This answers that from the artifacts: the newest snapshot is
# verified against its own MANIFEST and its age is compared to a threshold, and
# either a failed verification or a snapshot older than the threshold exits
# non-zero so a monitoring check has something to alert on. An old backup
# never alerts while it still verifies, which is why both conditions are
# reported separately.
#
# Usage:
#   bash scripts/backup-status.sh
#
# Environment:
#   BOTMOD_STATE_BACKUP_DIR      where `make backup` writes (default: ./backups)
#   BOTMOD_BACKUP_MAX_AGE_HOURS  staleness threshold, default 48
#
# Exit status:
#   0  the newest snapshot verified and is within the age threshold
#   1  no snapshot at all, verification failed, or the newest is too old
#   2  bad command line
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

usage() {
  cat >&2 <<EOF
usage: bash scripts/backup-status.sh

Verifies the newest snapshot under BOTMOD_STATE_BACKUP_DIR (default: ./backups)
and fails when it is older than BOTMOD_BACKUP_MAX_AGE_HOURS (default: 48).
No game install and no network: nothing is written.

exit status:
  0  the newest snapshot verified and is within the age threshold
  1  no snapshot at all, verification failed, or the newest is too old
  2  bad command line
EOF
}

case "${1:-}" in
  -h | --help) usage; exit 0 ;;
  "") ;;
  *)
    echo "ERROR: takes no arguments, got: $*" >&2
    usage
    exit 2
    ;;
esac

DEST_ROOT="${BOTMOD_STATE_BACKUP_DIR:-$ROOT/backups}"
MAX_AGE_HOURS="${BOTMOD_BACKUP_MAX_AGE_HOURS:-48}"
if ! [[ "$MAX_AGE_HOURS" =~ ^[0-9]+$ ]]; then
  echo "ERROR: BOTMOD_BACKUP_MAX_AGE_HOURS must be a whole number of hours, got '$MAX_AGE_HOURS'" >&2
  exit 2
fi

if [[ ! -d "$DEST_ROOT" ]]; then
  echo "STALE: no backup directory at '$DEST_ROOT'; no snapshot has ever been taken there." >&2
  echo "  run 'make backup' (point BOTMOD_STATE_BACKUP_DIR off-host for host-loss protection)" >&2
  exit 1
fi

# Newest by name: snapshot directories are UTC stamps (with a _2 suffix on a
# same-second collision), so they sort lexicographically into run order.
newest="$(find "$DEST_ROOT" -mindepth 1 -maxdepth 1 -type d | sort | tail -1)"
if [[ -z "$newest" ]]; then
  echo "STALE: '$DEST_ROOT' holds no snapshot; run 'make backup'." >&2
  exit 1
fi

# Snapshot directory timestamps are the run time to the second, so the age is
# read from the directory rather than from file mtimes, which a later restore
# or an rsync would rewrite.
now="$(date -u +%s)"
taken="$(stat -c %Y "$newest")"
age_hours=$(((now - taken) / 3600))

# Verification is restore-state.sh's job (one place implements the MANIFEST
# rules); it is verify-only here, so no server root is needed and a throwaway
# path is passed when the operator has not set one, since the script refuses a
# missing server root before it does anything.
verify_root="${SEVENDTD_DS_DIR:-/nonexistent-botmod-verify-root}"
if SEVENDTD_DS_DIR="$verify_root" bash "$ROOT/scripts/restore-state.sh" "$newest"; then
  echo "Verified newest snapshot: $newest"
else
  echo "UNVERIFIABLE: the newest snapshot does not verify; an older one may still be good." >&2
  echo "  check the rest under '$DEST_ROOT' with: make verify-snapshot SNAPSHOT=<dir>" >&2
  exit 1
fi

if ((age_hours > MAX_AGE_HOURS)); then
  echo "STALE: newest snapshot $newest is ${age_hours}h old, over the ${MAX_AGE_HOURS}h threshold." >&2
  echo "  the backup schedule has not run recently, so this is not a usable recovery point" >&2
  exit 1
fi
echo "Newest snapshot is ${age_hours}h old, within the ${MAX_AGE_HOURS}h threshold."
