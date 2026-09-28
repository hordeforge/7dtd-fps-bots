#!/usr/bin/env bash
# Resolves the 7 Days to Die dedicated-server install root. Source this file
# and read DS; do not repeat the default path in another script.
#
#   source "$(dirname "$0")/server-dir.sh"   -> $DS
#
# SEVENDTD_DS_DIR wins over the Steam default (the Makefile documents the same
# override). The scripts that need more than a path (install.sh) verify the
# result is a server install; uninstall.sh verifies the Mods dir it deletes
# from, so a half-installed server can still be cleaned up.

# shellcheck disable=SC2034 # sourced library: callers read DS, this file does not
DS="${SEVENDTD_DS_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
