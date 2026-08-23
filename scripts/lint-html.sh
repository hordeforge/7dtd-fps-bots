#!/usr/bin/env bash
# vnu (Nu HTML Checker) over the HTML this repo ships (GA dashboards, evolved
# reports). Same convention as zdtd and 7dtd-server-apm: vnu runs through npx pinned
# by VNU_VERSION; java is required. vnu-filter.txt drops deliberate deviations;
# warnings do not fail, errors do.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
vnu_version="${VNU_VERSION:-26.8.20}"

mapfile -t html_files < <(
  find "$root" -name '*.html' \
    -not -path '*/.*' \
    -not -path '*/node_modules/*' \
    -not -path '*/zig-pkg/*' \
    -not -path '*/zig-out/*' \
    | sort
)

if [ "${#html_files[@]}" -eq 0 ]; then
  echo "clanker: lint-html: no HTML files found under $root" >&2
  exit 1
fi

echo "vnu: checking ${#html_files[@]} HTML documents"
npx --yes "vnu-jar@$vnu_version" --filterfile "$root/vnu-filter.txt" --also-check-css "${html_files[@]}"
echo "vnu: OK"
