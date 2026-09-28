#!/usr/bin/env bash
# Serializes the scripts that rewrite <server>/Mods/BotMod. Source this file,
# call acquire_deploy_lock, and let DEPLOY_LOCK_CLEANUP run on EXIT; do not
# take the lock by hand in another script.
#
#   source "$(dirname "$0")/deploy-lock.sh"   -> acquire_deploy_lock, DEPLOY_LOCK
#
# install.sh and uninstall.sh both rename or delete the live mod dir, so two
# runs at once (a `make install` racing a `make uninstall`, an operator piping
# one into the other) can leave the server with one script's files and the
# other's deletion. mkdir is the lock because it is atomic on every filesystem
# the server can live on, and the PID inside it keeps a run killed mid-install
# from wedging the next one forever.
#
# The lock directory is dot-prefixed, like the staging dirs beside it, so the
# game's mod loader skips it.

# shellcheck disable=SC2034 # sourced library: callers run acquire_deploy_lock
DEPLOY_LOCK="$DS/Mods/.botmod-deploy.lock"

# acquire_deploy_lock: take the lock or exit(1) saying who holds it. Registers
# DEPLOY_LOCK_CLEANUP as the EXIT trap, which the caller may replace with its
# own cleanup (it must then call DEPLOY_LOCK_CLEANUP itself).
acquire_deploy_lock() {
  if mkdir "$DEPLOY_LOCK" 2>/dev/null; then
    echo "$$" > "$DEPLOY_LOCK/pid"
    return 0
  fi
  local owner
  owner="$(cat "$DEPLOY_LOCK/pid" 2>/dev/null || echo 0)"
  if ! kill -0 "$owner" 2>/dev/null; then
    echo "note: taking over the deploy lock left by dead pid $owner" >&2
    rm -rf "$DEPLOY_LOCK"
    if mkdir "$DEPLOY_LOCK" 2>/dev/null; then
      echo "$$" > "$DEPLOY_LOCK/pid"
      return 0
    fi
  fi
  echo "ERROR: another deploy holds '$DEPLOY_LOCK' (pid $owner); wait for it to finish" >&2
  exit 1
}

DEPLOY_LOCK_CLEANUP() { rm -rf "$DEPLOY_LOCK"; }
