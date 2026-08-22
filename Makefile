ROOT := $(CURDIR)
DS ?= $(HOME)/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server
.PHONY: build build-mcs test install uninstall run clean lint-html lint-webui check
build:
	$(ROOT)/scripts/build.sh
build-mcs:
	SEVENDTD_BUILD_BACKEND=mcs $(ROOT)/scripts/build.sh
lint-html:
	chmod +x $(ROOT)/scripts/lint-html.sh
	$(ROOT)/scripts/lint-html.sh
lint-webui:
	bash $(ROOT)/scripts/lint-webui.sh
check: lint-html lint-webui
install:
	$(ROOT)/scripts/install.sh
uninstall:
	rm -rf "$(DS)/Mods/BotMod"
clean:
	rm -rf $(ROOT)/dist $(ROOT)/Source/BotMod/bin $(ROOT)/Source/BotMod/obj
