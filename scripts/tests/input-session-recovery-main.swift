import AppKit
import Foundation

/// Stands in for MightyCore's locale lookup, which this harness does not link.
func L(_ key: String, _ subs: [String: String] = [:]) -> String { key }

@main
struct InputSessionRecoveryMain {
    @MainActor
    static func main() throws {
        _ = NSApplication.shared
        let report = InputSessionRecoveryDiagnostics.run()
        let output = try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys])
        print(String(decoding: output, as: UTF8.self))
        guard report.values.allSatisfy({ $0 }) else { exit(1) }
        print("PASS: \(report.count) input-session recovery contracts; no real activation or input-source changes")
    }
}
