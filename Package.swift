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
    targets: [
        .target(name: "EnhanceCapCutCore"),
        .executableTarget(name: "ecc", dependencies: ["EnhanceCapCutCore"]),
        .executableTarget(name: "EnhanceCapCut", dependencies: ["EnhanceCapCutCore"]),
        .testTarget(name: "EnhanceCapCutCoreTests", dependencies: ["EnhanceCapCutCore"]),
    ],
    swiftLanguageModes: [.v5]
)
