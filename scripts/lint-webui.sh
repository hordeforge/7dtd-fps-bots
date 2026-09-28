#!/usr/bin/env bash
# Lint the BotMod WebMod TypeScript (Source/BotMod/WebMod/bundle.ts) with tsc
# and oxlint against the anti-slop + strict rule set in .oxlintrc.jsonc, then
# check the committed bundle.js is fresh (a .ts edit that was not compiled
# fails the gate). Part of `make check` (target: lint-webui).
#
#   1. tsc --noEmit: the type gate (per WebMod/tsconfig.json, strict).
#   2. oxlint over bundle.ts with the anti-slop rule set in .oxlintrc.jsonc
#      (warnings fail via --deny-warnings).
#   3. Freshness: the committed bundle.js must equal a fresh compilation
#      minified through scripts/webmod-minify.sh, so a .ts edit that was not
#      compiled and committed fails the gate.
#   4. Wire budget: bundle.js and styling.css must each stay under their
#      budget (14 KiB for the bundle, 12 KiB for the stylesheet), because the
#      stock webserver serves both uncompressed.
#
# tsc/oxlint run through bunx pinned by the versions in scripts/tool-versions.sh
# (sourced below; environment overrides win). That file is the single source
# of truth, so build.sh and this freshness gate cannot drift apart: the gate
# compares the committed bundle.js against a compile with the exact tsc that
# built the shipped artifact.
# Override locally: TSC_VERSION=5.9.3 OXLINT_VERSION=1.79.0 bash scripts/lint-webui.sh
#
# Requires: bun (bunx).

set -euo pipefail
# The freshness check below compares bytes against a committed bundle.js that
# scripts/build.sh compiled under LC_ALL=C; keep this gate in the same locale
# and timezone so a locale-dependent emit cannot read as a stale bundle.
export LC_ALL=C TZ=UTC

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$root/scripts/tool-versions.sh"
source "$root/scripts/digest.sh"
cache_dir="${XDG_CACHE_HOME:-$HOME/.cache}/clanker/oxlint-standards"
webmod_dir="$root/Source/BotMod/WebMod"

# 1. Type check (per WebMod/tsconfig.json, strict).
bunx -p "typescript@$TSC_VERSION" tsc -p "$webmod_dir/tsconfig.json" --noEmit

# 2. Lint the source with oxlint. The @rikalabs plugin, the vendored
#    dmmulroy/anti-slop plugin source (pinned by ANTI_SLOP_SHA and checked
#    against ANTI_SLOP_SHA256; the project is vendored source, not an npm
#    package), and oxlint-tsgolint (the type-aware
#    backend, see options.typeAware in .oxlintrc.jsonc) are fetched into the
#    cache (no-op when the pinned versions are already present) and oxlint runs
#    next to them because jsPlugins resolve relative to the config file's
#    directory; a copy of the config is placed there each run. The pinned
#    packages are installed with one additive `bun add` invocation: it merges
#    the pins into the cache manifest and never prunes what a sibling script
#    installed. @oxlint/plugins is the plugin API the anti-slop source
#    imports; without it the plugin cannot load.
mkdir -p "$cache_dir"
archive="$cache_dir/anti-slop.tar.gz"
# The archive is the one fetched dependency with no registry behind it, so the
# commit pin is checked against a digest (ANTI_SLOP_SHA256, tool-versions.sh).
# Verified on fetch and on every later run that still has the archive cached, so
# a tampered cache fails the gate too rather than linting against whatever is
# on disk.
archive_digest() {
  "${SHA[@]}" "$1" | cut -d' ' -f1
}
verify_archive() {
  local actual
  actual="$(archive_digest "$1")"
  if [ "$actual" != "$ANTI_SLOP_SHA256" ]; then
    echo "BotMod: lint-webui: anti-slop archive sha256 mismatch, refusing to lint against unverified plugin source" >&2
    echo "  file:     $1" >&2
    echo "  expected: $ANTI_SLOP_SHA256 (anti-slop commit $ANTI_SLOP_SHA)" >&2
    echo "  actual:   $actual" >&2
    echo "  Delete the file to re-fetch, or re-pin ANTI_SLOP_SHA256 in scripts/tool-versions.sh once the bytes match the pinned commit." >&2
    return 1
  fi
}
if [ ! -f "$archive" ]; then
  curl -fsSL "https://github.com/dmmulroy/anti-slop/archive/$ANTI_SLOP_SHA.tar.gz" -o "$archive.part"
  if ! verify_archive "$archive.part"; then
    rm -f "$archive.part"
    exit 1
  fi
  mv "$archive.part" "$archive"
fi
verify_archive "$archive" || exit 1
# The digest covers the archive, not the tree oxlint loads, so the tree is
# rebuilt from the verified archive on every run: a hand-edited, truncated or
# otherwise tampered anti-slop-src is replaced instead of linted against.
# Extracting next to the live directory and swapping it in keeps a failed
# extraction from replacing good source with a half-written tree.
rm -rf "$cache_dir/anti-slop-src.new"
mkdir -p "$cache_dir/anti-slop-src.new"
tar xzf "$archive" -C "$cache_dir/anti-slop-src.new" --strip-components=2 "anti-slop-$ANTI_SLOP_SHA/src"
rm -rf "$cache_dir/anti-slop-src"
mv "$cache_dir/anti-slop-src.new" "$cache_dir/anti-slop-src"
# type module: the vendored anti-slop plugin source is ESM; without the field
# node reparses it with a MODULE_TYPELESS_PACKAGE_JSON warning.
[ -f "$cache_dir/package.json" ] || printf '{"type":"module"}\n' > "$cache_dir/package.json"
( cd "$cache_dir" && bun add --silent \
    "@rikalabs/oxlint-standards@$OXLINT_STANDARDS_VERSION" \
    "oxlint-tsgolint@$OXLINT_TSGOLINT_VERSION" \
    "@oxlint/plugins@$OXLINT_PLUGINS_VERSION" ) >/dev/null 2>&1 || {
  echo "BotMod: lint-webui: could not install @rikalabs/oxlint-standards@$OXLINT_STANDARDS_VERSION + oxlint-tsgolint@$OXLINT_TSGOLINT_VERSION + @oxlint/plugins@$OXLINT_PLUGINS_VERSION into $cache_dir (offline?)" >&2
  exit 1
}
cp "$root/.oxlintrc.jsonc" "$cache_dir/oxlintrc.jsonc"
(
  cd "$cache_dir"
  # tsgolint is not on the user's PATH; oxlint finds it via PATH lookup.
  PATH="$cache_dir/node_modules/.bin:$PATH" \
    bunx "oxlint@$OXLINT_VERSION" --config oxlintrc.jsonc --deny-warnings "$webmod_dir/bundle.ts"
)

# 3. Freshness: the committed bundle.js must equal a fresh compile run through
#    the same minifier the build applies (scripts/webmod-minify.sh), so a .ts
#    edit that was not compiled and committed fails the gate.
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
bunx -p "typescript@$TSC_VERSION" tsc -p "$webmod_dir/tsconfig.json" --outDir "$tmp" >/dev/null
bash "$root/scripts/webmod-minify.sh" "$tmp/bundle.js" "$tmp/bundle.min.js" >/dev/null
if ! diff -q "$tmp/bundle.min.js" "$webmod_dir/bundle.js" >/dev/null; then
  echo "BotMod: lint-webui: committed bundle.js is stale (bundle.ts changed without regeneration). Run: make build" >&2
  exit 1
fi

# 4. Wire budget: the stock dashboard loads bundle.js as a plain <script> tag
#    and its webserver serves it uncompressed, so every panel open pays the
#    full byte cost. 14 KiB is the initial TCP congestion window: over it the
#    panel needs a second round trip before it can register, on the admin
#    connections that are the slowest thing in its path. styling.css is
#    render-blocking for the same page and ships uncompressed too, so it gets
#    a budget of its own.
js_max_bytes="${BUNDLE_MAX_BYTES:-14336}"
css_max_bytes="${STYLESHEET_MAX_BYTES:-12288}"
js_size="$(wc -c <"$webmod_dir/bundle.js")"
css_size="$(wc -c <"$webmod_dir/styling.css")"
if [ "$js_size" -gt "$js_max_bytes" ]; then
  echo "BotMod: lint-webui: bundle.js is $js_size bytes, over the $js_max_bytes wire budget. Trim, defer, or split before adding." >&2
  exit 1
fi
if [ "$css_size" -gt "$css_max_bytes" ]; then
  echo "BotMod: lint-webui: styling.css is $css_size bytes, over the $css_max_bytes wire budget. Trim before adding." >&2
  exit 1
fi
echo "BotMod: lint-webui: tsc type-check, oxlint, bundle freshness, and wire budget (bundle.js $js_size/$js_max_bytes, styling.css $css_size/$css_max_bytes bytes) ok"
