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
        // Bundled libwebrtc (Google's build, M154) for the BETA screen-share
        // feature. Pinned exactly: a WebRTC bump is a deliberate decision, not a
        // resolver's.
        .package(url: "https://github.com/stasel/WebRTC.git", exact: "154.0.0"),
        // zstd for the screen-share clipboard, the format the phone writes.
        .package(url: "https://github.com/facebook/zstd.git", exact: "1.5.7"),
    ],
    targets: [
        .target(name: "MightyCore", dependencies: [
            .product(name: "libzstd", package: "zstd"),
        ], resources: [.copy("Resources/Styles"), .copy("Resources/Locales"), .copy("Resources/Help")]),
        .executableTarget(name: "MightyClaude", dependencies: [
            "MightyCore",
            .product(name: "GhosttyTerminal", package: "libghostty-spm"),
            .product(name: "WebRTC", package: "WebRTC"),
        ]),
        .testTarget(name: "MightyCoreTests", dependencies: ["MightyCore"]),
    ]
)
