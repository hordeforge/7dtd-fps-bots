#!/usr/bin/env bash
# Resolves the SHA-256 command as a one-element command array named SHA.
# Source this file and call "${SHA[@]}"; do not pick the tool again in
# another script.
#
#   source "$(dirname "$0")/digest.sh"   -> $SHA
#
# sha256sum on Linux, shasum on macOS. Callers verify or write manifests that
# are checked on the other side of the same trip, so a missing tool is a hard
# error here rather than an unchecked fallback.

# shellcheck disable=SC2034 # sourced library: callers read SHA, this file does not
if command -v sha256sum > /dev/null; then
  SHA=(sha256sum)
elif command -v shasum > /dev/null; then
  SHA=(shasum -a 256)
else
  echo "ERROR: need sha256sum or shasum" >&2
  exit 1
fi
