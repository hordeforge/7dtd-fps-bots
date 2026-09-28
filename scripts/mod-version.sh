#!/usr/bin/env bash
# Resolves the mod version the build ships. Source this file and read
# MOD_VERSION; do not parse Source/BotMod/Core/BotModVersion.cs in another
# script.
#
#   source "$(dirname "$0")/mod-version.sh"   -> $MOD_VERSION
#
# The C# constant is canonical: ModInfo.xml is what the engine's mod listing
# shows and cannot reference it, so scripts/build.sh is the step that fails
# the build when the two disagree. Three callers need the value (build.sh,
# package.sh, verify-reproducible.sh) and they all need the same one, or the
# archive name and the reproducibility check address different files.
#
# The trailing ';' in the sed pattern is load-bearing: without it sed takes the
# longest overall match, so a ';' comment or any text after the constant leaks
# into the version and from there into the archive name.

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MOD_VERSION="$(sed -n 's/.*const string Number = "\([^"]*\)";.*/\1/p' \
  "$root/Source/BotMod/Core/BotModVersion.cs")"
if [ -z "$MOD_VERSION" ]; then
  echo "ERROR: could not parse 'const string Number = "...;"' from Source/BotMod/Core/BotModVersion.cs" >&2
  exit 1
fi
