#!/usr/bin/env bash
# Lint the BotMod WebMod TypeScript (Source/BotMod/WebMod/bundle.ts) with tsc
# and oxlint against the anti-slop + strict rule set in .oxlintrc.jsonc, then
# check the committed bundle.js is fresh (a .ts edit that was not compiled
# fails the gate). Part of `make check` (target: lint-webui).
#
#   1. tsc --noEmit: the type gate (per WebMod/tsconfig.json, strict).
#   2. oxlint over bundle.ts with the anti-slop rule set in .oxlintrc.jsonc
#      (warnings fail via --deny-warnings).
#   3. Freshness: the committed bundle.js must equal a fresh compilation, so a
#      .ts edit that was not compiled and committed fails the gate.
#   4. Wire budget: bundle.js must stay under BUNDLE_MAX_BYTES (default 32 KiB).
#
# tsc/oxlint run through npx pinned by the versions in scripts/tool-versions.sh
# (sourced below; environment overrides win). That file is the single source
# of truth, so build.sh and this freshness gate cannot drift apart: the gate
# compares the committed bundle.js against a compile with the exact tsc that
# built the shipped artifact.
# Override locally: TSC_VERSION=5.9.3 OXLINT_VERSION=1.79.0 bash scripts/lint-webui.sh
#
# Requires: node/npm (npx).

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$root/scripts/tool-versions.sh"
cache_dir="${XDG_CACHE_HOME:-$HOME/.cache}/clanker/oxlint-standards"
webmod_dir="$root/Source/BotMod/WebMod"

# 1. Type check (per WebMod/tsconfig.json, strict).
npx --yes -p "typescript@$TSC_VERSION" tsc -p "$webmod_dir/tsconfig.json" --noEmit

# 2. Lint the source with oxlint. The @rikalabs plugin, the vendored
#    dmmulroy/anti-slop plugin source (pinned by ANTI_SLOP_SHA; the project is
#    vendored source, not an npm package), and oxlint-tsgolint (the type-aware
#    backend, see options.typeAware in .oxlintrc.jsonc) are fetched into the
#    cache (no-op when the pinned versions are already present) and oxlint runs
#    next to them because jsPlugins resolve relative to the config file's
#    directory; a copy of the config is placed there each run. All npm packages
#    are installed in one invocation: a later separate --no-save install would
#    prune the others. @oxlint/plugins is the plugin API the anti-slop source
#    imports; without it the plugin cannot load.
mkdir -p "$cache_dir"
if [ ! -d "$cache_dir/anti-slop-src" ]; then
  curl -fsSL "https://github.com/dmmulroy/anti-slop/archive/$ANTI_SLOP_SHA.tar.gz" -o "$cache_dir/anti-slop.tar.gz"
  mkdir -p "$cache_dir/anti-slop-src"
  tar xzf "$cache_dir/anti-slop.tar.gz" -C "$cache_dir/anti-slop-src" --strip-components=2 "anti-slop-$ANTI_SLOP_SHA/src"
fi
npm install --prefix "$cache_dir" --no-audit --no-fund --no-save --no-package-lock \
  "@rikalabs/oxlint-standards@$OXLINT_STANDARDS_VERSION" \
  "oxlint-tsgolint@$OXLINT_TSGOLINT_VERSION" \
  "@oxlint/plugins@$OXLINT_PLUGINS_VERSION" >/dev/null 2>&1 || {
  echo "BotMod: lint-webui: could not install @rikalabs/oxlint-standards@$OXLINT_STANDARDS_VERSION + oxlint-tsgolint@$OXLINT_TSGOLINT_VERSION + @oxlint/plugins@$OXLINT_PLUGINS_VERSION into $cache_dir (offline?)" >&2
  exit 1
}
cp "$root/.oxlintrc.jsonc" "$cache_dir/oxlintrc.jsonc"
(
  cd "$cache_dir"
  # tsgolint is not on the user's PATH; oxlint finds it via PATH lookup.
  PATH="$cache_dir/node_modules/.bin:$PATH" \
    npx --yes "oxlint@$OXLINT_VERSION" --config oxlintrc.jsonc --deny-warnings "$webmod_dir/bundle.ts"
)

# 3. Freshness: the committed bundle.js must equal a fresh compilation.
#    tsc versions differ on whether they emit a leading "use strict" for this
#    classic script (both forms are equivalent), so the check strips it.
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
npx --yes -p "typescript@$TSC_VERSION" tsc -p "$webmod_dir/tsconfig.json" --outDir "$tmp" >/dev/null
if ! diff -q <(sed '1{/^"use strict";$/d}' "$tmp/bundle.js") \
             <(sed '1{/^"use strict";$/d}' "$webmod_dir/bundle.js") >/dev/null; then
  echo "BotMod: lint-webui: committed bundle.js is stale (bundle.ts changed without regeneration). Run: make build" >&2
  exit 1
fi

# 4. Wire budget: the stock dashboard loads bundle.js as a plain <script> tag
#    and its webserver serves it uncompressed, so every panel open pays the
#    full byte cost. Keep the delivered weight bounded (~1.5x current size).
max_bytes="${BUNDLE_MAX_BYTES:-32768}"
size="$(wc -c <"$webmod_dir/bundle.js")"
if [ "$size" -gt "$max_bytes" ]; then
  echo "BotMod: lint-webui: bundle.js is $size bytes, over the $max_bytes wire budget. Trim or lazy-load before adding." >&2
  exit 1
fi
echo "BotMod: lint-webui: tsc type-check, oxlint, bundle freshness, and wire budget ($size/$max_bytes bytes) ok"
