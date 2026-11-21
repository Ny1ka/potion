// swift-tools-version:5.9
// PotionCore: shared processing logic for the macOS apps (Potion and Potion Dropper).
// Data that both platforms share (format signatures, conversion matrix, presets) lives in
// core/spec and is copied into this package's resources; see docs/ARCHITECTURE.md.
import PackageDescription

let package = Package(
    name: "PotionCore",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "PotionCore", targets: ["PotionCore"]),
    ],
    targets: [
        .target(name: "PotionCore"),
        .testTarget(name: "PotionCoreTests", dependencies: ["PotionCore"]),
    ]
)
