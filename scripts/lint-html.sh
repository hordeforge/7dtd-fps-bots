#!/usr/bin/env bash
# vnu (Nu HTML Checker) over the HTML this repo ships. Same convention as zdtd
# and 7dtd-server-apm: vnu runs through bunx pinned by VNU_VERSION in
# scripts/tool-versions.sh; java is required.
#
# The file list comes from `git ls-files`, not a tree walk: the checked set is
# then exactly what a clone gets, so a local `evolved/runs/<ts>/report.html`
# left over from training cannot fail a gate CI never sees. vnu-filter.txt
# drops deliberate deviations; --Werror makes warnings fail like errors.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$root/scripts/tool-versions.sh"

mapfile -t -d '' html_files < <(git -C "$root" ls-files -z '*.html')

if [ "${#html_files[@]}" -eq 0 ]; then
  echo "clanker: lint-html: no tracked HTML files under $root" >&2
  exit 1
fi

echo "vnu: checking ${#html_files[@]} HTML documents"
bunx "vnu-jar@$VNU_VERSION" --filterfile "$root/vnu-filter.txt" --also-check-css --Werror \
  "${html_files[@]/#/$root/}"
echo "vnu: OK"
