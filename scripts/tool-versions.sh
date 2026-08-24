#!/usr/bin/env bash
# Single source of truth for the pinned external tool versions used by
# scripts/build.sh, scripts/lint-webui.sh, and scripts/lint-html.sh. Source
# this file; do not duplicate the defaults elsewhere. Environment overrides
# win over the defaults below (same override contract as before).
#
# The repo deliberately tracks no package.json/node_modules (.gitignore);
# these pins fetched via npx ARE the dependency manifest (same policy as
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
: "${VNU_VERSION:=26.8.20}"
# Python analysis gate (make lint-python / .github/workflows/ci.yml). Keep in
# lockstep with the locally installed ruff so local runs and CI enforce the
# same rule set and fixes (ruff.toml documents the selected rules).
: "${RUFF_VERSION:=0.16.4}"

export TSC_VERSION OXLINT_VERSION OXLINT_STANDARDS_VERSION \
  OXLINT_TSGOLINT_VERSION OXLINT_PLUGINS_VERSION ANTI_SLOP_SHA VNU_VERSION \
  RUFF_VERSION
