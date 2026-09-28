#!/usr/bin/env bash
# Restore a snapshot taken by scripts/backup-state.sh.
#
# The snapshot is verified against its own MANIFEST before anything is
# written: a snapshot whose digests do not match is worse than no snapshot,
# because it restores corruption. Default is verify-only, so a snapshot can
# be checked on a host that is not the server; pass --apply to copy.
#
# Usage:
#   bash scripts/restore-state.sh <snapshot-dir> [--apply]
#
# Restored config lands in <server>/Mods/BotMod/Config/ (created if the mod
# is not installed), or in the file BOTMOD_CONFIG names when this snapshot was
# taken from such a deployment, or in the container path it was taken from
# when that path exists here. Champion weights are printed, not copied: they
# are committed in the repo, so a restore of weights means a git checkout, and
# this script's job is to say what the snapshot holds.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

usage() {
  cat >&2 <<EOF
usage: bash scripts/restore-state.sh <snapshot-dir> [--apply]

  <snapshot-dir>  directory written by scripts/backup-state.sh
  --apply         write the config back; without it the snapshot is only verified
  -h, --help      print this help

exit status:
  0  the snapshot verified (and was written, with --apply)
  1  the snapshot failed verification, or nothing could be restored
  2  bad command line
EOF
}

# Options parse before the server-root guard: --help and a bad flag name are
# answered the same way whatever SEVENDTD_DS_DIR says.
SNAP=""
apply=0
for arg in "$@"; do
  case "$arg" in
    --apply) apply=1 ;;
    -h | --help) sed -n '2,16p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*)
      echo "ERROR: unknown option '$arg' (see --help)" >&2
      usage
      exit 2
      ;;
    *) SNAP="$arg" ;;
  esac
done
if [[ -z "$SNAP" ]]; then
  usage
  exit 2
fi

source "$ROOT/scripts/server-dir.sh"

if [[ -z "$DS" || "$DS" == "/" ]]; then
  echo "ERROR: refusing to restore into server root '$DS'" >&2
  echo "Set SEVENDTD_DS_DIR to the server install root and retry." >&2
  exit 1
fi

if [[ ! -d "$SNAP" ]]; then
  echo "ERROR: snapshot dir not found: '$SNAP' (see 'make backup' for the path)" >&2
  exit 1
fi
MANIFEST="$SNAP/MANIFEST"
if [[ ! -f "$MANIFEST" ]]; then
  echo "ERROR: '$SNAP' has no MANIFEST; not a complete snapshot" >&2
  exit 1
fi

source "$ROOT/scripts/digest.sh"

# Verify every listed file, and every file present, so both a truncated
# snapshot and a tampered one fail here instead of at the next server start.
missing=0
while read -r digest name; do
  [[ -n "$name" ]] || continue
  if [[ ! -f "$SNAP/$name" ]]; then
    echo "MISSING: $name (listed in MANIFEST, absent in snapshot)" >&2
    missing=1
    continue
  fi
  if ! (cd "$SNAP" && "${SHA[@]}" "$name" | grep -q "^$digest "); then
    echo "CORRUPT: $name (digest mismatch)" >&2
    missing=1
  fi
done < <(grep -v '^#' "$MANIFEST")
extra="$(cd "$SNAP" && find . -type f ! -name MANIFEST | sed 's|^\./||' | sort | comm -23 - <(grep -v '^#' "$MANIFEST" | awk '{print $2}' | sort))"
if [[ -n "$extra" ]]; then
  echo "ERROR: files not listed in MANIFEST: $extra" >&2
  missing=1
fi
if [[ "$missing" == 1 ]]; then
  echo "Snapshot failed verification; nothing was restored." >&2
  exit 1
fi
# Digests only prove the snapshot is the one that was taken, not that what was
# taken was restorable state. A zero-byte config is the state a torn write or a
# full disk leaves on the server, and restoring it replaces a good config with
# one BotConfig.Load cannot parse, so every persisted operator setting resets
# to defaults at the next start. backup-state.sh substitutes the .bak for a
# blank primary, so a snapshot this script wrote is never blank; an old one,
# or one edited by hand, has to be refused rather than installed.
blank=0
for f in botmod.json botmod.config-path.json botmod.container.json; do
  if [[ -f "$SNAP/$f" && ! -s "$SNAP/$f" ]]; then
    echo "EMPTY: $f is a zero-byte config; restoring it would reset the operator config" >&2
    blank=1
  fi
done
if [[ "$blank" == 1 ]]; then
  echo "Snapshot holds no recoverable config; nothing was restored." >&2
  exit 1
fi
echo "Verified snapshot $SNAP"

for f in evolved/best.json evolved/best.meta.json; do
  if [[ -f "$SNAP/$f" ]]; then
    echo "champion weight in snapshot: $f ($(grep -m1 -o '"generation": [0-9]*' "$SNAP/$f" 2>/dev/null || echo 'generation n/a'))"
  fi
done

if [[ "$apply" == 0 ]]; then
  echo "Dry run: nothing written. Re-run with --apply to restore config."
  exit 0
fi

restored=0
# A snapshot taken with BOTMOD_CONFIG set holds the config under its own name
# (the file lives outside the mod dir on that deployment). It can only go
# back where the server will read it, so the same variable must be set here:
# writing it into Mods/BotMod/Config instead would restore a file the server
# ignores and report success.
if [[ -f "$SNAP/botmod.config-path.json" || -f "$SNAP/botmod.config-path.json.bak" ]]; then
  if [[ -z "${BOTMOD_CONFIG:-}" ]]; then
    echo "ERROR: this snapshot holds a BOTMOD_CONFIG-mounted config; set BOTMOD_CONFIG to that path and retry." >&2
    echo "  (backup host path: $(sed -n 's/^# config-path=//p' "$MANIFEST"))" >&2
    exit 1
  fi
  mkdir -p "$(dirname "$BOTMOD_CONFIG")"
  for f in botmod.config-path.json botmod.config-path.json.bak; do
    [[ -f "$SNAP/$f" ]] || continue
    cp "$SNAP/$f" "$BOTMOD_CONFIG${f#botmod.config-path.json}"
    echo "Restored -> $BOTMOD_CONFIG${f#botmod.config-path.json}"
    restored=$((restored + 1))
  done
fi

# Same rule for the container path the mod writes when it exists: restoring it
# anywhere the server does not read is a success message over a no-op, so the
# target has to exist before anything is copied.
CONTAINER_CONFIG="${BOTMOD_CONTAINER_CONFIG:-/mods/BotMod/Config}"
if [[ -f "$SNAP/botmod.container.json" || -f "$SNAP/botmod.container.json.bak" ]]; then
  if [[ ! -d "$CONTAINER_CONFIG" ]]; then
    if [[ -f "$SNAP/botmod.json" || -f "$SNAP/botmod.json.bak" || -f "$SNAP/botmod.config-path.json" || -f "$SNAP/botmod.config-path.json.bak" ]]; then
      echo "WARNING: skipping the container config, '$CONTAINER_CONFIG' does not exist on this host." >&2
      echo "  (backup host path: $(sed -n 's/^# container-config=//p' "$MANIFEST"))" >&2
      echo "  Restore it on the host that mounts that path; the other snapshot configs are still restored." >&2
    else
      echo "ERROR: this snapshot holds only the container config, and '$CONTAINER_CONFIG' does not exist here." >&2
      echo "  (backup host path: $(sed -n 's/^# container-config=//p' "$MANIFEST"))" >&2
      echo "  Set BOTMOD_CONTAINER_CONFIG to that mount and retry, or restore onto the host that has it." >&2
      exit 1
    fi
  else
    for f in botmod.container.json botmod.container.json.bak; do
      [[ -f "$SNAP/$f" ]] || continue
      cp "$SNAP/$f" "$CONTAINER_CONFIG/${f#botmod.container.json}"
      echo "Restored -> $CONTAINER_CONFIG/${f#botmod.container.json}"
      restored=$((restored + 1))
    done
  fi
fi

for f in botmod.json botmod.json.bak; do
  [[ -f "$SNAP/$f" ]] || continue
  mkdir -p "$DS/Mods/BotMod/Config"
  # A .bak from a newer run is the live file's only fallback: land the
  # snapshot's own pair together, so primary and fallback agree.
  cp "$SNAP/$f" "$DS/Mods/BotMod/Config/$f"
  echo "Restored -> $DS/Mods/BotMod/Config/$f"
  restored=$((restored + 1))
done
if [[ "$restored" == 0 ]]; then
  echo "Snapshot holds no operator config; nothing to restore into the server." >&2
  exit 1
fi
echo "Start the server, then 'bot neural reload' if the neural brain was enabled."
