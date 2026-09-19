import Foundation

/// What the app asks a terminal pane to type. `autoRun` is the app's own
/// decision and is never true for a string that came from a manifest (§1.5).
public struct TerminalInput: Sendable, Equatable {
    public var text: String
    public var autoRun: Bool
    public init(text: String, autoRun: Bool) { self.text = text; self.autoRun = autoRun }
}

public protocol TerminalPasteSink: AnyObject {
    func paste(text: String) -> Bool
    func sendEnter() -> Bool
}

/// The paste and the Enter live here rather than in the pane so the promise
/// "a manifest never presses Enter" can be asserted at the sink itself (§5.7).
public enum TerminalInputPolicy {
    public enum Outcome: Sendable, Equatable { case pasted, pastedAndRan, refused(String), failed }
    public static let newlineRefusal = "명령에 줄바꿈이 있어 터미널에 넣지 않았습니다."

    /// A second layer over §1.11's banned characters: whatever the source, a
    /// line break means nothing is pasted at all.
    public static func hasLineBreak(_ text: String) -> Bool {
        text.unicodeScalars.contains { ["\u{000A}", "\u{000D}", "\u{2028}", "\u{2029}"].contains($0) }
    }

    @discardableResult
    public static func apply(_ input: TerminalInput, to sink: TerminalPasteSink) -> Outcome {
        guard !hasLineBreak(input.text) else { return .refused(newlineRefusal) }
        guard sink.paste(text: input.text) else { return .failed }
        guard input.autoRun else { return .pasted }
        return sink.sendEnter() ? .pastedAndRan : .failed
    }
}
