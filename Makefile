ROOT := $(CURDIR)
DS ?= $(HOME)/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server
.PHONY: build build-mcs test install uninstall run clean
build:
	$(ROOT)/scripts/build.sh
build-mcs:
	SEVENDTD_BUILD_BACKEND=mcs $(ROOT)/scripts/build.sh
install:
	$(ROOT)/scripts/install.sh
uninstall:
	rm -rf "$(DS)/Mods/BotMod"
clean:
	rm -rf $(ROOT)/dist $(ROOT)/Source/BotMod/bin $(ROOT)/Source/BotMod/obj
