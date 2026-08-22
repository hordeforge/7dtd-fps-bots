#!/usr/bin/env bash
# Compile and run the idempotency-ledger tests (tests/BotMod.Web.Tests) with
# mcs + mono. The ledger under test (Source/BotMod/Web/IdempotencyLedger.cs)
# is pure BCL, so no game DLL references are needed. Not part of `make check`
# (CI runners have no mono); run locally: bash scripts/test-idempotency.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

mcs -out:"$work/tests.exe" \
  "$root/Source/BotMod/Web/IdempotencyLedger.cs" \
  "$root/tests/BotMod.Web.Tests/IdempotencyLedgerTests.cs"

mono "$work/tests.exe"
