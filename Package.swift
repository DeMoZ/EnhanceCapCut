// swift-tools-version:6.0
import PackageDescription

// CapCutDraft (draft model, timeline, export settings) is plain Foundation and also builds on Windows.
// The renderer, the app and the CLI use Apple frameworks, so they exist only on macOS.
var products: [Product] = [
    .library(name: "CapCutDraft", targets: ["CapCutDraft"]),
]
var targets: [Target] = [
    .target(name: "CapCutDraft"),
    .testTarget(name: "CapCutDraftTests", dependencies: ["CapCutDraft"]),
]

#if os(macOS)
products += [
    .executable(name: "EnhanceCapCut", targets: ["EnhanceCapCut"]),
    .executable(name: "ecc", targets: ["ecc"]),
    .library(name: "EnhanceCapCutCore", targets: ["EnhanceCapCutCore"]),
]
targets += [
    .target(name: "EnhanceCapCutCore", dependencies: ["CapCutDraft"]),
    .executableTarget(name: "ecc", dependencies: ["EnhanceCapCutCore"]),
    .executableTarget(name: "EnhanceCapCut", dependencies: ["EnhanceCapCutCore"]),
    .testTarget(name: "EnhanceCapCutCoreTests", dependencies: ["EnhanceCapCutCore"]),
]
#endif

let package = Package(
    name: "EnhanceCapCut",
    platforms: [.macOS(.v15)],
    products: products,
    targets: targets,
    swiftLanguageModes: [.v5]
)
