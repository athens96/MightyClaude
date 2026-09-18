import Foundation

/// Korean composition falling apart into jamo: the input method commits one
/// compatibility jamo per key instead of building syllables. Pure helpers so
/// the detector can be tested without AppKit.
public enum InputMethodSymptom {
    /// U+3131…U+318E (Hangul compatibility jamo) or U+1100…U+11FF (conjoining jamo).
    public static func isLoneJamo(_ text: String) -> Bool {
        let scalars = text.unicodeScalars
        guard scalars.count == 1, let scalar = scalars.first else { return false }
        return (0x3131...0x318E).contains(scalar.value) || (0x1100...0x11FF).contains(scalar.value)
    }
    public static func isKoreanInputSource(_ identifier: String?) -> Bool {
        guard let identifier else { return false }
        let lowered = identifier.lowercased()
        // Apple's Korean modes, Gureum (han2/han3 modes), and other Hangul IMEs.
        return ["korean", "hangul", "gureum", ".han2", ".han3", "2setkorean", "3setkorean"].contains { lowered.contains($0) }
    }

    /// Counts lone-jamo commits that arrived without a preceding marked-text
    /// update in the same key; two within `window` seconds is the symptom.
    public struct Detector: Sendable, Equatable {
        public var window: TimeInterval
        public var threshold: Int
        private var stamps: [TimeInterval] = []
        public init(window: TimeInterval = 5, threshold: Int = 2) { self.window = window; self.threshold = threshold }
        /// Returns true when the symptom is confirmed by this event.
        public mutating func observeCommit(_ text: String, composedThisKey: Bool, koreanSource: Bool, at time: TimeInterval) -> Bool {
            guard koreanSource, !composedThisKey, isLoneJamo(text) else { return false }
            stamps = stamps.filter { time - $0 <= window } + [time]
            return stamps.count >= threshold
        }
        public mutating func reset() { stamps.removeAll() }
    }
}
