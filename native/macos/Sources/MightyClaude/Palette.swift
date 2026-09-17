import SwiftUI
import AppKit

// Keep using the macOS 14 property wrapper when an SDK also exports a State macro.
// Command Line Tools ship SwiftUI itself but may not include its macro plugin.
typealias ViewState<Value> = SwiftUI.State<Value>

enum Palette {
    static let accent = Color(red: 0.86, green: 0.65, blue: 0.55)
    static let canvas = Color(nsColor: .windowBackgroundColor)
    static let panel = Color(nsColor: .controlBackgroundColor)
    static let border = Color.primary.opacity(0.10)
    static let subtle = Color.primary.opacity(0.035)

    static func symbol(_ provider: String) -> String {
        switch provider { case "codex": return "hexagon"; case "gemini": return "sparkle"; default: return "asterisk" }
    }

    static func name(_ provider: String) -> String {
        switch provider { case "codex": return "Codex"; case "gemini": return "Gemini"; default: return "Claude" }
    }

    static func status(_ value: String) -> String {
        switch value { case "running": return "실행 중"; case "completed": return "완료"; case "error": return "오류"; case "stopped": return "중지됨"; default: return "준비됨" }
    }
}

struct StatusDot: View {
    let status: String
    var body: some View {
        Circle().fill(status == "running" || status == "completed" ? Color.green.opacity(0.8) : status == "error" ? Color.red : Color.secondary.opacity(0.7)).frame(width: 5, height: 5)
    }
}
