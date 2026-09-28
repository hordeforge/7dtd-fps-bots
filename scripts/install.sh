#!/usr/bin/env bash
# Deploy dist/BotMod into the dedicated server's Mods dir.
#
# The payload is staged in a sibling of Mods/BotMod and swapped in with a
# single rename, so a copy that fails part way leaves the running install
# untouched instead of replacing it with a half-written mod dir. The swap
# itself holds the deploy lock uninstall.sh also takes and renames the old mod
# dir aside rather than deleting it, so a failure between the two renames puts
# the running install back instead of leaving the server with no mod dir.
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
# Harmony is a runtime dependency, not a build-time one: the mod ships no
# 0Harmony.dll of its own, and every patch in Source/BotMod/Patches types off
# HarmonyLib, so a server without the TFP Harmony mod loads a mod that cannot
# patch. Fail before the swap, while the running install is still intact.
if [[ ! -f "$DS/Mods/0_TFP_Harmony/0Harmony.dll" ]]; then
  echo "ERROR: '$DS/Mods/0_TFP_Harmony/0Harmony.dll' is missing; BotMod needs" >&2
  echo "the TFP Harmony mod at runtime (same file scripts/build.sh links against)." >&2
  echo "Install it into the server's Mods dir and retry." >&2
  exit 1
fi

# The same list scripts/package.sh refuses to archive: a payload missing one of
# these installs cleanly and then fails at runtime, one server start later.
# shellcheck source=scripts/required-payload.sh
source "$ROOT/scripts/required-payload.sh"
missing=()
for required in "${REQUIRED_PAYLOAD[@]}"; do
  [[ -f "$SRC/$required" ]] || missing+=("$required")
done
if ((${#missing[@]})); then
  echo "ERROR: payload $SRC is missing:" >&2
  printf '  %s\n' "${missing[@]}" >&2
  echo "nothing was installed; rebuild with scripts/build.sh (or re-extract the release zip)." >&2
  exit 1
fi

# The release zip carries MANIFEST.sha256 over every payload file. Verifying it
# here turns a tampered or half-extracted package into a refused install rather
# than a live one; a payload built in place (no manifest) is not checked.
if [[ -f "$SRC/MANIFEST.sha256" ]]; then
  source "$ROOT/scripts/digest.sh"
  # -c only: --quiet is a coreutils option that the shasum digest.sh picks on
  # macOS does not take, so keep the report and echo it when the check fails.
  if ! manifest_report="$(cd "$SRC" && "${SHA[@]}" -c MANIFEST.sha256 2>&1)"; then
    echo "$manifest_report" >&2
    echo "ERROR: $SRC fails MANIFEST.sha256; nothing was installed" >&2
    echo "Re-extract the release zip or rebuild with scripts/build.sh." >&2
    exit 1
  fi
fi

# One install at a time: the swap below renames the live mod dir, and
# uninstall.sh deletes it, so the two have to exclude each other.
source "$ROOT/scripts/deploy-lock.sh"
acquire_deploy_lock

# Staged beside the target so the swap is a same-filesystem rename.
STAGE="$DS/Mods/.BotMod.staging.$$"
PREVIOUS="$DS/Mods/.BotMod.previous.$$"
rm -rf "$STAGE"
mkdir -p "$STAGE"
# Releasing the lock is the first thing either exit path does, so a failed
# install never blocks the retry that fixes it.
cleanup() {
  rm -rf "$STAGE"
  DEPLOY_LOCK_CLEANUP
}
trap cleanup EXIT
# Runs once the old install has been renamed aside (PREVIOUS exists): put it
# back rather than leaving the server with no mod dir, whether the failure
# landed on the rename itself or on the clean-up after it.
rollback() {
  local rc=$?
  if [[ -d "$PREVIOUS" ]]; then
    rm -rf "$DST"
    if mv "$PREVIOUS" "$DST"; then
      echo "the swap failed; the previous install was put back at '$DST'" >&2
    else
      echo "ERROR: the swap failed and '$PREVIOUS' could not be put back either." >&2
      echo "       it is still intact there; move it to '$DST' by hand." >&2
    fi
  fi
  exit "$rc"
}
trap rollback ERR
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

# The old install is renamed aside, not deleted, so a failure anywhere in the
# second rename puts it straight back (rollback above). A `rm -rf` here would
# leave a server with no mod dir for the window between the two commands and
# for good if the second one failed.
if [[ -d "$DST" ]]; then
  mv "$DST" "$PREVIOUS"
fi
mv "$STAGE" "$DST"
rm -rf "$PREVIOUS"
trap - ERR
cleanup
trap - EXIT
if [[ "$kept" == 1 ]]; then
  echo "Preserved operator config across reinstall: $DST/Config/botmod.json(.bak)"
fi
echo "Installed -> $DST"
ls -la "$DST"
ls -la "$DST/Config" 2>&1 || true
