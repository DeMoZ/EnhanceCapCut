// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "EnhanceCapCut",
    platforms: [.macOS(.v15)],
    products: [
        .executable(name: "EnhanceCapCut", targets: ["EnhanceCapCut"]),
        .executable(name: "ecc", targets: ["ecc"]),
        .library(name: "EnhanceCapCutCore", targets: ["EnhanceCapCutCore"]),
    ],
    dependencies: [
        // App updates: "Check for Updates…", EdDSA-signed appcast published with each release.
        .package(url: "https://github.com/sparkle-project/Sparkle", exact: "2.10.0"),
    ],
    targets: [
        .target(name: "EnhanceCapCutCore", dependencies: ["whisper"]),
        // Speech recognition with Whisper (whisper.cpp v1.9.5, the same model as the Windows version).
        .binaryTarget(
            name: "whisper",
            url: "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-b5454-xcframework.zip",
            checksum: "e57f8c48933000acabc13bb913fbe82805d483a2deff692cc6738836bd92393b"),
        .executableTarget(name: "ecc", dependencies: ["EnhanceCapCutCore"]),
        .executableTarget(name: "EnhanceCapCut", dependencies: ["EnhanceCapCutCore", .product(name: "Sparkle", package: "Sparkle")]),
        .testTarget(name: "EnhanceCapCutCoreTests", dependencies: ["EnhanceCapCutCore"]),
    ],
    swiftLanguageModes: [.v5]
)
