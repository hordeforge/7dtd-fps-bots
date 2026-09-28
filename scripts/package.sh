#!/usr/bin/env bash
# Zip the built mod payload (dist/BotMod) into dist/BotMod-<version>.zip,
# reproducibly. Run scripts/build.sh first.
#
# scripts/build.sh stages dist/BotMod from scratch, but a build that fails
# part way leaves the earlier steps' output behind; the completeness check
# below refuses to archive such a payload.
#
# Reproducibility contract (verify by running twice and comparing sha256):
#   - entry order is sorted (LC_ALL=C), never readdir order
#   - every timestamp is SOURCE_DATE_EPOCH, defaulting to the HEAD commit time
#   - permissions are normalized (dirs 755, files 644); uid/gid and extended
#     attributes are stripped via zip -X
#   - compression level fixed (-9) so deflate output is stable
# The zip also carries MANIFEST.sha256 (sha256 of every payload file except
# itself, `sha256sum -c` format), so an extracted package can be verified
# offline. Its content depends only on payload bytes in sorted order, which
# keeps the archive byte-stable.
set -euo pipefail
export LC_ALL=C TZ=UTC

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/digest.sh"
SRC="$ROOT/dist/BotMod"

# A build that dies after the C# compile (the bunx tsc emit, a missing config
# file) leaves a BotMod.dll behind in a half-populated dist/BotMod, and this
# script would happily archive that as a release. The payload is installed
# server-side, so every file the runtime reads is required here, not optional.
required=(
  BotMod.dll
  ModInfo.xml
  Config/botmod.json
  Config/entityclasses.xml
  WebMod/bundle.js
  WebMod/styling.css
)
missing=()
for f in "${required[@]}"; do
  [[ -f "$SRC/$f" ]] || missing+=("$f")
done
if ((${#missing[@]})); then
  echo "ERROR: payload incomplete, refusing to package a half-built mod:" >&2
  printf '  %s\n' "${missing[@]}" >&2
  echo "Re-run scripts/build.sh and read its error; it stages dist/BotMod from" >&2
  echo "scratch and the last failing step leaves the payload partial." >&2
  exit 1
fi

# Same canonical source as scripts/build.sh's drift guard. The trailing .*
# matters: without it sed takes the longest overall match, so a `;` comment or
# any text after the constant leaks into the version and into the archive name.
VERSION="$(sed -n 's/.*const string Number = "\([^"]*\)".*/\1/p' \
  "$ROOT/Source/BotMod/Core/BotModVersion.cs")"
if [[ -z "$VERSION" ]]; then
  echo "ERROR: could not parse version from Source/BotMod/Core/BotModVersion.cs" >&2
  exit 1
fi

if [[ -n "${SOURCE_DATE_EPOCH:-}" ]]; then
  EPOCH="$SOURCE_DATE_EPOCH"
elif EPOCH="$(git -C "$ROOT" log -1 --format=%ct 2>/dev/null)"; then
  : # HEAD commit time keeps repeated releases of one commit byte-stable
else
  echo "ERROR: not a git checkout; set SOURCE_DATE_EPOCH explicitly" >&2
  exit 1
fi

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
cp -r "$SRC" "$STAGE/BotMod"

# Integrity manifest inside the archive: sha256 of every payload file except
# itself, in `sha256sum -c` format (run it inside the extracted directory).
(
  cd "$STAGE/BotMod"
  # LC_ALL=C sort, newline-delimited: sort -z is a GNU extension and the
  # payload holds no file whose name carries a newline (the whitespace guard
  # below refuses the zip step on one). Collected with a read loop, not
  # mapfile: mapfile needs bash 4 and macOS ships 3.2.
  files=()
  while IFS= read -r f; do files+=("$f"); done \
    < <(find . -type f ! -name MANIFEST.sha256 | LC_ALL=C sort)
  : > MANIFEST.sha256
  for f in "${files[@]}"; do
    "${SHA[@]}" "${f#./}" >> MANIFEST.sha256
  done
)

# zip -@ splits names on whitespace; refuse anything it would mangle.
while IFS= read -r -d '' f; do
  if [[ "$f" == *[[:space:]]* ]]; then
    echo "ERROR: whitespace in payload path would corrupt the archive: $f" >&2
    exit 1
  fi
done < <(cd "$STAGE" && find BotMod -print0)

find "$STAGE/BotMod" -type d -exec chmod 755 {} +
find "$STAGE/BotMod" -type f -exec chmod 644 {} +
# touch -d "@epoch" is a GNU extension; POSIX touch -t is everywhere, so probe
# the date tool for the epoch-to-stamp conversion instead of the OS name (GNU
# spells it -d, BSD -r). TZ=UTC above makes touch -t read the stamp in UTC, so
# the archive timestamp is the epoch instant on either tool.
if STAMP_TIME="$(date -u -d "@$EPOCH" +%Y%m%d%H%M.%S 2>/dev/null)" ||
  STAMP_TIME="$(date -u -r "$EPOCH" +%Y%m%d%H%M.%S 2>/dev/null)"; then
  :
else
  echo "ERROR: need GNU date (-d @epoch) or BSD date (-r epoch) to stamp the payload" >&2
  exit 1
fi
find "$STAGE/BotMod" -exec touch -h -t "$STAMP_TIME" {} +

OUT="$ROOT/dist/BotMod-$VERSION.zip"
rm -f "$OUT"
(
  cd "$STAGE"
  find BotMod -type f | LC_ALL=C sort | zip -X -q -9 "$OUT" -@
)
echo "Packaged -> $OUT"
"${SHA[@]}" "$OUT"
