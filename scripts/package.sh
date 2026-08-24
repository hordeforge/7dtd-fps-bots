#!/usr/bin/env bash
# Zip the built mod payload (dist/BotMod) into dist/BotMod-<version>.zip,
# reproducibly. Run scripts/build.sh first.
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
SRC="$ROOT/dist/BotMod"

if [[ ! -f "$SRC/BotMod.dll" ]]; then
  echo "ERROR: $SRC/BotMod.dll missing; run scripts/build.sh first" >&2
  exit 1
fi

# Same canonical source as scripts/build.sh's drift guard.
VERSION="$(sed -n 's/.*const string Number = "\([^"]*\)";/\1/p' \
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
  mapfile -d '' files < <(find . -type f ! -name MANIFEST.sha256 -print0 | sort -z)
  : > MANIFEST.sha256
  for f in "${files[@]}"; do
    sha256sum "${f#./}" >> MANIFEST.sha256
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
find "$STAGE/BotMod" -exec touch -h -d "@$EPOCH" {} +

OUT="$ROOT/dist/BotMod-$VERSION.zip"
rm -f "$OUT"
(
  cd "$STAGE"
  find BotMod -type f | LC_ALL=C sort | zip -X -q -9 "$OUT" -@
)
echo "Packaged -> $OUT"
sha256sum "$OUT"
