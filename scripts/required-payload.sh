#!/usr/bin/env bash
# The files a built mod payload cannot be shipped or installed without.
# Source this file and read REQUIRED_PAYLOAD; do not re-list them elsewhere.
#
#   source "$(dirname "$0")/required-payload.sh"   -> $REQUIRED_PAYLOAD
#
# Two callers need the list, for the same reason and against the same bytes.
# scripts/package.sh refuses to archive a half-built payload, and
# scripts/install.sh refuses to deploy one. The payload is installed
# server-side, so every file the runtime reads is required, not optional: a
# missing one installs cleanly and then fails at runtime, one server start
# later. That made a list a second caller re-derived shorter a real gap rather
# than a shorter policy, so it is declared once, here.

# shellcheck disable=SC2034 # sourced library: callers read REQUIRED_PAYLOAD
REQUIRED_PAYLOAD=(
  BotMod.dll
  ModInfo.xml
  Config/botmod.json
  Config/entityclasses.xml
  WebMod/bundle.js
  WebMod/styling.css
)
