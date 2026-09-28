#!/usr/bin/env bash
# Minify the tsc output of the BotMod WebMod bundle.
#
#   scripts/webmod-minify.sh <bundle.js> <out.js>
#
# The stock 7DTD webserver serves /webmods/BotMod/bundle.js uncompressed, so
# the shipped bytes are the download cost of every dashboard load. tsc's
# emit keeps indentation, long property names and IIFE boilerplate, which put
# the panel over the ~14 KB initial congestion window; one extra round trip
# before the panel can register. Minifying once at build time puts it back
# under one round trip.
#
# One script, two callers (scripts/build.sh, scripts/lint-webui.sh): they
# cannot drift apart, and the freshness gate compares the committed
# bundle.js against exactly this transformation of a fresh tsc run.
#
# terser comes from bunx, pinned by TERSER_VERSION in scripts/tool-versions.sh
# (same contract as tsc/oxlint/vnu). Requires: bun (bunx).

set -euo pipefail
# This step produces the shipped bundle.js, and its bytes are compared against
# the committed copy by scripts/lint-webui.sh, which does not go through
# scripts/build.sh (where LC_ALL=C is already exported). Pin the same locale
# and timezone here or the two callers can compute different bytes from one
# input under two environments.
export LC_ALL=C TZ=UTC

if [ "$#" -ne 2 ]; then
  echo "usage: webmod-minify.sh <in.js> <out.js>" >&2
  exit 2
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=scripts/tool-versions.sh
source "$root/scripts/tool-versions.sh"

in="$1"
out="$2"
# --ecma 2017 matches WebMod/tsconfig.json's target: terser parses the modern
# input regardless, and pinning the output level keeps the emitted syntax the
# dashboard's browser already runs. -c/-m are the default passes; they are
# spelled out because the wire budget depends on them.
bunx -p "terser@$TERSER_VERSION" terser "$in" --ecma 2017 -c -m --comments false --output "$out"
