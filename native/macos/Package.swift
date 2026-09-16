// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "MightyClaude",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "MightyCore", targets: ["MightyCore"]),
        .executable(name: "MightyClaude", targets: ["MightyClaude"]),
    ],
    dependencies: [
        .package(url: "https://github.com/Lakr233/libghostty-spm.git", exact: "1.5.20260906"),
    ],
    targets: [
        .target(name: "MightyCore"),
        .executableTarget(name: "MightyClaude", dependencies: [
            "MightyCore",
            .product(name: "GhosttyTerminal", package: "libghostty-spm"),
        ]),
        .testTarget(name: "MightyCoreTests", dependencies: ["MightyCore"]),
    ]
)
