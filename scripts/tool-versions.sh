#!/usr/bin/env bash
# Single source of truth for the pinned external tool versions used by
# scripts/build.sh, scripts/lint-webui.sh, and scripts/lint-html.sh. Source
# this file; do not duplicate the defaults elsewhere. Environment overrides
# win over the defaults below (same override contract as before).
#
# The repo deliberately tracks no package.json/node_modules (.gitignore);
# these pins fetched via bunx ARE the dependency manifest (same policy as
# ../7dtd-server-apm/scripts/lint-webui.sh).
#
# TSC_VERSION is load-bearing in two places: scripts/build.sh compiles the
# shipped WebMod/bundle.js with it and lint-webui.sh's freshness gate
# re-compiles with the same version to detect a stale bundle. Both read the
# one variable here, so they cannot drift apart.

: "${TSC_VERSION:=5.9.3}"
: "${OXLINT_VERSION:=1.79.0}"
: "${OXLINT_STANDARDS_VERSION:=0.8.1}"
: "${OXLINT_TSGOLINT_VERSION:=7.0.2001}"
: "${OXLINT_PLUGINS_VERSION:=1.79.0}"
: "${ANTI_SLOP_SHA:=6d538555cb151d4121ed51a27db81890eacf8ae9}"
# SHA-256 of the GitHub source archive for ANTI_SLOP_SHA, verified by
# lint-webui.sh on fetch and on every cached run. The commit pin says which
# revision is intended; the digest says the bytes on disk are that revision.
# A mismatch is a tampered mirror or cache, never a formatting change to
# update around: re-fetch, compare against the pinned commit, and only then
# replace this value. The npm pins above resolve through the registry's own
# per-version sha512 integrity fields (bun.lock); this archive has no registry,
# so the digest is the only integrity control it has.
: "${ANTI_SLOP_SHA256:=a720663fd2562e22e3da670769faa88dc34c9a761fdd9a7d285e20d92871848e}"
: "${VNU_VERSION:=26.8.20}"
# Python analysis gate (make lint-python / .github/workflows/ci.yml). Keep in
# lockstep with the locally installed ruff so local runs and CI enforce the
# same rule set and fixes (ruff.toml documents the selected rules).
: "${RUFF_VERSION:=0.16.4}"
# CI workflow lint gate (make lint-yaml / .github/workflows/ci.yml). Same
# lockstep contract as ruff above: CI installs the pin, the config that shapes
# the finding set is .yamllint.yml.
: "${YAMLLINT_VERSION:=1.38.0}"

export TSC_VERSION OXLINT_VERSION OXLINT_STANDARDS_VERSION \
  OXLINT_TSGOLINT_VERSION OXLINT_PLUGINS_VERSION ANTI_SLOP_SHA \
  ANTI_SLOP_SHA256 VNU_VERSION \
  RUFF_VERSION YAMLLINT_VERSION
