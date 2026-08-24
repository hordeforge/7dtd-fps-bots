ROOT := $(CURDIR)
# Same override as scripts/install.sh: SEVENDTD_DS_DIR wins over the default.
DS ?= $(if $(SEVENDTD_DS_DIR),$(SEVENDTD_DS_DIR),$(HOME)/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server)
SCRIPTS := $(ROOT)/scripts
.DEFAULT_GOAL := help
.PHONY: help build build-mcs test package install uninstall clean lint-html lint-webui lint-shell lint-python check

# build needs the game's Managed DLLs (see scripts/build.sh for the two paths
# it probes and the SEVENDTD_DS_DIR / SEVENDTD_GAME_DIR overrides).
define HELP
Targets:
  make build        compile BotMod.dll + web bundle into dist/BotMod (needs game DLLs or dotnet SDK)
  make build-mcs    same, forcing the mono mcs backend
  make test         run tests/BotMod.Web.Tests via scripts/test-idempotency.sh (needs mcs + mono; CI runs it after installing mono)
  make package      reproducible zip of dist/BotMod -> dist/BotMod-<version>.zip (needs zip; run build first)
  make check        what CI runs: shellcheck + vnu HTML lint + tsc/oxlint/bundle freshness
  make lint-shell   shellcheck over scripts/*.sh
  make lint-python  ruff defect-class gate over tools/ga + scripts (config: ruff.toml)
  make lint-html    Nu HTML checker over shipped/generated HTML (needs java; tools via npx)
  make lint-webui   tsc strict type-check, oxlint, committed-bundle freshness gate (needs node/npm)
  make install      copy dist/BotMod into the dedicated server's Mods dir
  make uninstall    remove Mods/BotMod from the server
  make clean        remove dist/ and C# obj/bin intermediates
Overrides: SEVENDTD_DS_DIR (server root), SEVENDTD_GAME_DIR (client root),
SEVENDTD_BUILD_BACKEND=auto|mcs|dotnet, SOURCE_DATE_EPOCH (package zip
timestamps; defaults to the HEAD commit time). CI runs `make check` plus
`scripts/test-idempotency.sh` (mono installed in the workflow; ruff via pipx
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
	bash "$(SCRIPTS)/test-idempotency.sh"
package:
	bash "$(SCRIPTS)/package.sh"
lint-html:
	bash "$(SCRIPTS)/lint-html.sh"
lint-webui:
	bash "$(SCRIPTS)/lint-webui.sh"
lint-shell:
	shellcheck "$(SCRIPTS)"/*.sh
lint-python:
	ruff check .
check: lint-shell lint-html lint-webui lint-python
install:
	bash "$(SCRIPTS)/install.sh"
uninstall:
	rm -rf "$(DS)/Mods/BotMod"
clean:
	rm -rf "$(ROOT)/dist" "$(ROOT)/Source/BotMod/bin" "$(ROOT)/Source/BotMod/obj"
