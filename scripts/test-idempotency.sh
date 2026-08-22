#!/usr/bin/env bash
# Compile and run the pure-BCL unit tests (tests/BotMod.Web.Tests) with
# mcs + mono. The suites under test (IdempotencyLedger, AtomicTextFile) are
# pure BCL, so no game DLL references are needed. Not part of `make check`
# (CI runners have no mono); run locally: bash scripts/test-idempotency.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

run_suite() { # <name> <sources...>
  local name="$1"; shift
  mcs -warnaserror -out:"$work/$name.exe" "$@" > /dev/null
  mono "$work/$name.exe"
}

run_suite idempotency \
  "$root/Source/BotMod/Web/IdempotencyLedger.cs" \
  "$root/tests/BotMod.Web.Tests/IdempotencyLedgerTests.cs"

run_suite atomictextfile \
  "$root/Source/BotMod/Config/AtomicTextFile.cs" \
  "$root/tests/BotMod.Web.Tests/AtomicTextFileTests.cs"
