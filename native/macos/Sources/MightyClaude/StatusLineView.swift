import MightyCore
import SwiftUI

/// The rendered `statusLine` under the composer, one row per output line,
/// with terminal colours mapped onto the app's palette.
struct StatusLineView: View {
    let sessionID: String
    let state: AppStore.StatusLineState
    let padding: Int
    var onTrust: (StatusLineConfig) -> Void = { _ in }
    var onDismiss: () -> Void = {}

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            if let untrusted = state.untrusted {
                VStack(alignment: .leading, spacing: 4) {
                    Label("\(untrusted.source)에 statusLine 명령이 있습니다. 이 워크스페이스에서 실행할까요?", systemImage: "terminal")
                        .font(.system(size: 11)).foregroundStyle(.secondary)
                    Text(untrusted.command).font(.system(size: 11, design: .monospaced)).lineLimit(3).textSelection(.enabled)
                        .padding(6).frame(maxWidth: .infinity, alignment: .leading).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 6))
                    HStack(spacing: 8) {
                        Button("이 워크스페이스에서 허용") { onTrust(untrusted) }.controlSize(.small).accessibilityIdentifier("status-line-trust-\(sessionID)")
                        Button("지금은 안 함") { onDismiss() }.controlSize(.small)
                        Text("저장소가 바꾼 명령은 다시 묻습니다.").font(.system(size: 10)).foregroundStyle(.tertiary)
                    }
                }
                .padding(.bottom, 4)
                .accessibilityIdentifier("status-line-untrusted-\(sessionID)")
            }
            if let result = state.result, state.config != nil {
                ForEach(Array(result.lines.enumerated()), id: \.offset) { _, line in
                    Text(Self.attributed(line)).lineLimit(1).truncationMode(.tail)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                if let error = result.error, result.lines.isEmpty {
                    Label(error, systemImage: "exclamationmark.triangle").foregroundStyle(.secondary).lineLimit(1)
                }
            }
        }
        .font(.system(size: 11, design: .monospaced))
        .textSelection(.enabled)
        .padding(.leading, CGFloat(padding) * 6)
        .padding(.horizontal, 12).padding(.top, 2).padding(.bottom, 8)
        .help((state.config.map { "statusLine · " + $0.source + " · " + $0.command } ?? "statusLine") + (state.updatedAt.map { " · " + $0.formatted(date: .omitted, time: .standard) } ?? ""))
        .accessibilityElement(children: .combine)
        .accessibilityLabel("상태 줄")
        .accessibilityValue(state.result?.plainText ?? "")
        .accessibilityIdentifier("status-line-\(sessionID)")
    }

    static func attributed(_ segments: [ANSISegment]) -> AttributedString {
        var result = AttributedString()
        for segment in segments {
            var piece = AttributedString(segment.text)
            var color = segment.foreground.map(Self.color) ?? Color.primary
            if segment.dim { color = color.opacity(0.55) }
            piece.foregroundColor = color
            if let background = segment.background { piece.backgroundColor = Self.color(background).opacity(0.35) }
            if segment.bold { piece.font = .system(size: 11, weight: .semibold, design: .monospaced) }
            if segment.italic { piece.font = (segment.bold ? Font.system(size: 11, weight: .semibold, design: .monospaced) : Font.system(size: 11, design: .monospaced)).italic() }
            if segment.underline { piece.underlineStyle = .single }
            result.append(piece)
        }
        return result
    }

    /// Terminal colours chosen to stay legible on both themes.
    static func color(_ value: ANSISegment.Color) -> Color {
        switch value {
        case .standard(let index):
            switch index % 8 {
            case 0: return index >= 8 ? .secondary : .primary
            case 1: return Color(red: 0.86, green: 0.30, blue: 0.30)
            case 2: return Color(red: 0.30, green: 0.66, blue: 0.40)
            case 3: return Color(red: 0.80, green: 0.62, blue: 0.20)
            case 4: return Color(red: 0.36, green: 0.55, blue: 0.90)
            case 5: return Color(red: 0.70, green: 0.45, blue: 0.85)
            case 6: return Color(red: 0.25, green: 0.65, blue: 0.70)
            default: return .primary
            }
        case .palette(let index):
            if index < 16 { return color(.standard(index)) }
            if index >= 232 { let level = Double(index - 232) / 23; return Color(white: 0.25 + level * 0.6) }
            let cube = index - 16
            let steps: [Double] = [0, 0.37, 0.53, 0.68, 0.84, 1]
            return Color(red: steps[cube / 36], green: steps[(cube / 6) % 6], blue: steps[cube % 6])
        case .rgb(let r, let g, let b):
            return Color(red: Double(r) / 255, green: Double(g) / 255, blue: Double(b) / 255)
        }
    }
}
