#!/usr/bin/env bash
# Prove the shipped bytes are a function of the source tree alone: build the
# payload twice (once from a different absolute path) and package it twice,
# then compare. scripts/package.sh already documents the archive contract
# (sorted entries, SOURCE_DATE_EPOCH, normalized modes, -X); this is the leg
# that checks the contract still holds, including against the C# compile and
# the tsc emit, which nothing else re-runs.
#
# Needs the same prerequisites as `make build` (game install + bun). A build
# that cannot run at all fails here rather than reporting a green check.
set -euo pipefail
export LC_ALL=C TZ=UTC

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$root/scripts/digest.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Pin the epoch here so the two packages differ only if the payload does.
export SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-$(git -C "$root" log -1 --format=%ct 2>/dev/null || echo 0)}"

echo "verify-reproducible: leg 1/3, build in place"
# Same reason as the mirror excludes below: a stale obj from an earlier local
# build would make leg 1 short-circuit, and leg 2 (clean mirror) would then
# differ for a reason that has nothing to do with the build path.
rm -rf "$root/Source/BotMod/obj" "$root/Source/BotMod/bin"
bash "$root/scripts/build.sh" > /dev/null
cp -a "$root/dist/BotMod" "$work/first"

echo "verify-reproducible: leg 2/3, rebuild from a different absolute path"
mirror="$work/a/much/deeper/path/clanker"
mkdir -p "$mirror"
# The C# intermediates are excluded with dist: carrying leg 1's
# Source/BotMod/obj into the mirror lets the dotnet backend short-circuit leg
# 2 off a cache written at the other absolute path, so the diff would compare
# leg 1's own output against itself and pass on a non-reproducible build. The
# mirror must hold source only. backups/ and coverage.cobertura.xml are
# git-ignored local state that no build step reads, excluded to keep the copy
# to what a clone would have.
tar -c -C "$root" \
  --exclude=.git --exclude=dist --exclude=.scratch --exclude=backups \
  --exclude=coverage.cobertura.xml \
  --exclude=./Source/BotMod/obj --exclude=./Source/BotMod/bin \
  . | tar -x -C "$mirror"
bash "$mirror/scripts/build.sh" > /dev/null
if ! diff -r "$work/first" "$mirror/dist/BotMod" > "$work/diff.txt" 2>&1; then
  echo "ERROR: payload differs when built from a different path (build path or timestamp leaked into an artifact):" >&2
  cat "$work/diff.txt" >&2
  exit 1
fi

echo "verify-reproducible: leg 3/3, package twice and compare the archives"
bash "$root/scripts/package.sh" > /dev/null
first_zip="$("${SHA[@]}" "$root/dist/BotMod-"*.zip | cut -d' ' -f1)"
bash "$root/scripts/package.sh" > /dev/null
second_zip="$("${SHA[@]}" "$root/dist/BotMod-"*.zip | cut -d' ' -f1)"
if [ "$first_zip" != "$second_zip" ]; then
  echo "ERROR: two packages of the same payload differ: $first_zip vs $second_zip" >&2
  exit 1
fi

echo "verify-reproducible: ok (payload byte-identical across paths, package $second_zip)"
