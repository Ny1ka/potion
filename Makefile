# Common tasks. Run `make setup` once after cloning.
.PHONY: setup scan test-core potion-macos-project potion-macos run-potion-macos clean

DERIVED := build/DerivedData
POTION_MAC_APP := $(DERIVED)/Build/Products/Debug/Potion.app

setup:
	scripts/setup.sh

scan:
	scripts/forbidden-scan.sh --tree

test-core:
	swift test --package-path core/swift

potion-macos-project:
	cd apps/potion-macos && xcodegen generate --quiet

potion-macos: potion-macos-project
	xcodebuild -project apps/potion-macos/Potion.xcodeproj -scheme Potion -configuration Debug \
		-derivedDataPath $(DERIVED) -quiet build

run-potion-macos: potion-macos
	open "$(POTION_MAC_APP)"

clean:
	rm -rf build apps/potion-macos/Potion.xcodeproj core/swift/.build
