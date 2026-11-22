# Common tasks. Run `make setup` once after cloning.
.PHONY: setup scan sync-spec test test-core test-core-dotnet potion-macos-project potion-macos run-potion-macos dropper-macos-project dropper-macos run-dropper-macos clean

DERIVED := build/DerivedData
POTION_MAC_APP := $(DERIVED)/Build/Products/Debug/Potion.app
DROPPER_MAC_APP := $(DERIVED)/Build/Products/Debug/Potion Dropper.app
DOTNET ?= $(shell command -v dotnet 2>/dev/null || echo $(HOME)/.dotnet/dotnet)
export DOTNET_CLI_TELEMETRY_OPTOUT := 1
export DOTNET_NOLOGO := 1

setup:
	scripts/setup.sh

scan:
	scripts/forbidden-scan.sh --tree

sync-spec:
	scripts/sync-spec.sh

test: test-core test-core-dotnet

test-core:
	scripts/sync-spec.sh --check
	swift test --package-path core/swift

# The Windows app itself only builds on Windows (see CI); the shared .NET core builds anywhere. The Windows-only
# engines (Potion.Core.Windows) compile here too, but their tests run only on Windows (CI).
test-core-dotnet:
	$(DOTNET) test core/dotnet/Potion.Core.Tests
	$(DOTNET) build core/dotnet/Potion.Core.Windows.Tests -v quiet
	@# Every bundled .NET dependency must be free of terms the source policy blocks (Markdig wasn't, 2026-11-22).
	scripts/forbidden-scan.sh --dependencies core/dotnet/Potion.Core.Windows.Tests/bin/Debug

potion-macos-project:
	cd apps/potion-macos && xcodegen generate --quiet

potion-macos: potion-macos-project
	xcodebuild -project apps/potion-macos/Potion.xcodeproj -scheme Potion -configuration Debug \
		-derivedDataPath $(DERIVED) -quiet build

run-potion-macos: potion-macos
	open "$(POTION_MAC_APP)"

dropper-macos-project:
	cd apps/dropper-macos && xcodegen generate --quiet

dropper-macos: dropper-macos-project
	xcodebuild -project "apps/dropper-macos/Potion Dropper.xcodeproj" -scheme PotionDropper -configuration Debug \
		-derivedDataPath $(DERIVED) -quiet build

run-dropper-macos: dropper-macos
	open "$(DROPPER_MAC_APP)"

clean:
	rm -rf build apps/potion-macos/Potion.xcodeproj "apps/dropper-macos/Potion Dropper.xcodeproj" core/swift/.build core/dotnet/*/bin core/dotnet/*/obj apps/potion-windows/*/bin apps/potion-windows/*/obj apps/dropper-windows/*/bin apps/dropper-windows/*/obj
