ROOT := $(CURDIR)
SCRIPTS := $(ROOT)/scripts
.DEFAULT_GOAL := help
.PHONY: help build build-mcs test test-list ci package verify-reproducible install uninstall backup restore verify-snapshot clean lint-html lint-webui lint-shell lint-python lint-yaml check preflight

# build needs the game's Managed DLLs (see scripts/build.sh for the two paths
# it probes and the SEVENDTD_DS_DIR / SEVENDTD_GAME_DIR overrides).
define HELP
Targets:
  make build        compile BotMod.dll + web bundle into dist/BotMod (needs game DLLs or dotnet SDK)
  make build-mcs    same, forcing the mono mcs backend
  make test         run tests/BotMod.Web.Tests via scripts/test-idempotency.sh (needs mcs + mono; CI runs it after installing mono)
  make test SUITE=x run one suite by name (make test-list prints the names)
  make test-list    print the C# suite names SUITE= accepts
  make ci           everything CI runs: make check then make test
  make package      reproducible zip of dist/BotMod -> dist/BotMod-<version>.zip (needs zip; run build first)
  make verify-reproducible  build the payload twice (second time from another path) and package twice, then compare bytes
  make check        what CI runs: shellcheck + yamllint + vnu HTML lint + tsc/oxlint/bundle freshness
  make preflight    name the tools `make check` needs (shellcheck, yamllint, java, bun, ruff)
  make lint-shell   shellcheck over scripts/*.sh
  make lint-python  ruff defect-class gate over tools/ga + scripts (config: ruff.toml)
  make lint-yaml    yamllint over the CI workflows (config: .yamllint.yml, --strict)
  make lint-html    Nu HTML checker over shipped/generated HTML (needs java; tools via bunx)
  make lint-webui   tsc strict type-check, oxlint, committed-bundle freshness gate (needs bun/bunx)
  make install      copy dist/BotMod into the dedicated server's Mods dir
  make uninstall    remove Mods/BotMod from the server (snapshots operator config first)
  make backup       snapshot operator config + champion weights into backups/<utc>/ (make restore SNAPSHOT=... puts config back)
  make clean        remove dist/ and C# obj/bin intermediates
Overrides: SEVENDTD_DS_DIR (server root), SEVENDTD_GAME_DIR (client root),
SEVENDTD_BUILD_BACKEND=auto|mcs|dotnet, SOURCE_DATE_EPOCH (package zip
timestamps; defaults to the HEAD commit time). BOTMOD_STATE_BACKUP_DIR (where
`make backup` writes; defaults to ./backups, point it off-host for host-loss
protection), SNAPSHOT (directory passed to restore-state.sh). CI runs `make check` plus
`scripts/test-idempotency.sh` (mono installed in the workflow; ruff via uv tool
for lint-python); `make build`
additionally needs the game install locally.
endef
export HELP
help:
	@echo "$$HELP"
build:
	bash "$(SCRIPTS)/build.sh"
build-mcs:
	SEVENDTD_BUILD_BACKEND=mcs bash "$(SCRIPTS)/build.sh"
test:
	bash "$(SCRIPTS)/test-idempotency.sh" $(SUITE)
test-list:
	bash "$(SCRIPTS)/test-idempotency.sh" --list
ci: check test
package:
	bash "$(SCRIPTS)/package.sh"
verify-reproducible:
	bash "$(SCRIPTS)/verify-reproducible.sh"
lint-html:
	bash "$(SCRIPTS)/lint-html.sh"
lint-webui:
	bash "$(SCRIPTS)/lint-webui.sh"
lint-shell:
	shellcheck "$(SCRIPTS)"/*.sh
lint-python:
	ruff check .
lint-yaml:
	yamllint -c "$(ROOT)/.yamllint.yml" --strict "$(ROOT)/.github/workflows"

preflight:
	@missing=""; \
	for tool in shellcheck yamllint java bun ruff; do \
	  command -v "$$tool" > /dev/null || missing="$$missing $$tool"; \
	done; \
	if [ -n "$$missing" ]; then \
	  echo "make check needs these on PATH:$$missing" >&2; \
	  echo "Pinned versions for the fetched ones live in scripts/tool-versions.sh" >&2; \
	  exit 1; \
	fi
check: preflight lint-shell lint-yaml lint-html lint-webui lint-python
install:
	bash "$(SCRIPTS)/install.sh"
uninstall:
	bash "$(SCRIPTS)/uninstall.sh"
backup:
	bash "$(SCRIPTS)/backup-state.sh"
restore:
	@test -n "$(SNAPSHOT)" || { echo "usage: make restore SNAPSHOT=backups/<utc-stamp>" >&2; exit 1; }
	bash "$(SCRIPTS)/restore-state.sh" "$(SNAPSHOT)" --apply
verify-snapshot:
	@test -n "$(SNAPSHOT)" || { echo "usage: make verify-snapshot SNAPSHOT=backups/<utc-stamp>" >&2; exit 1; }
	bash "$(SCRIPTS)/restore-state.sh" "$(SNAPSHOT)"
clean:
	rm -rf "$(ROOT)/dist" "$(ROOT)/Source/BotMod/bin" "$(ROOT)/Source/BotMod/obj"
