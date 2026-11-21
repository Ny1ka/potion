# Potion

Native desktop apps for converting files on your own computer. Nothing is uploaded.

- **Potion**: converts video, audio, images, documents and PDFs. macOS and Windows.
- **Potion Dropper**: downloads media from supported sites and converts it. macOS and Windows.

Both apps are in early development. See `docs/PROGRESS.md` for status.

## Building (macOS)

Requires Xcode 15+ and [XcodeGen](https://github.com/yonaskolb/XcodeGen) (`brew install xcodegen`).

```bash
make setup             # enables git hooks, checks tools
make test-core         # PotionCore unit tests
make run-potion-macos  # builds and launches Potion
```

## Repository

| Path | Contents |
|---|---|
| `design-system/` | Logos, colors, typography, components and generated platform assets |
| `core/` | Shared processing logic and the cross-platform spec |
| `apps/` | The four app projects |
| `docs/` | Architecture, progress, legal review |

## License

MIT. See `LICENSE`. The Potion and Potion Dropper names and logos are not covered by the license.
Third-party components are listed in `legal/THIRD_PARTY_NOTICES.md`.
