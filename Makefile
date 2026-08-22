ROOT := $(CURDIR)
# Same override as scripts/install.sh: SEVENDTD_DS_DIR wins over the default.
DS ?= $(if $(SEVENDTD_DS_DIR),$(SEVENDTD_DS_DIR),$(HOME)/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server)
SCRIPTS := $(ROOT)/scripts
.PHONY: build build-mcs test install uninstall run clean lint-html lint-webui lint-shell check
build:
	bash $(SCRIPTS)/build.sh
build-mcs:
	SEVENDTD_BUILD_BACKEND=mcs bash $(SCRIPTS)/build.sh
test:
	bash $(SCRIPTS)/test-idempotency.sh
lint-html:
	bash $(SCRIPTS)/lint-html.sh
lint-webui:
	bash $(SCRIPTS)/lint-webui.sh
lint-shell:
	shellcheck $(SCRIPTS)/*.sh
check: lint-shell lint-html lint-webui
install:
	bash $(SCRIPTS)/install.sh
uninstall:
	rm -rf "$(DS)/Mods/BotMod"
clean:
	rm -rf $(ROOT)/dist $(ROOT)/Source/BotMod/bin $(ROOT)/Source/BotMod/obj
