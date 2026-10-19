// swift-tools-version: 6.0
import PackageDescription

// AgentGauge for macOS. GaugeCore holds everything that is not UI (models, providers,
// credentials, scheduling, notification rules) so it builds and tests with plain
// `swift test`; the AgentGauge executable is the SwiftUI menu bar app on top of it.
// `scripts/build-app.sh` wraps the executable into AgentGauge.app.
let package = Package(
    name: "AgentGauge",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "AgentGauge", targets: ["AgentGauge"]),
    ],
    targets: [
        .target(name: "GaugeCore"),
        .executableTarget(name: "AgentGauge", dependencies: ["GaugeCore"]),
        .testTarget(name: "GaugeCoreTests", dependencies: ["GaugeCore"]),
    ],
    swiftLanguageModes: [.v5]
)
