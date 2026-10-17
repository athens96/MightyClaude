import Foundation

/// The Mac's text setting (Settings → General → 글자, and View → ⌘= / ⌘− / ⌘0): one
/// size scale for every word the app draws, and three optional font families: the
/// interface's (the app's chrome), the conversation's (replies, the user's turns, the
/// composer and Mighty's prose) and the code's (code, tool output and the terminals).
/// Saved in the app's defaults, not in the workspace state, so the phone and the
/// Windows app never see it.
public struct TextAppearance: Equatable, Hashable, Sendable {
    /// The sizes the menu and Settings step through; 1 is the size the app was drawn at.
    public static let scales: [Double] = [0.85, 0.9, 1, 1.1, 1.2, 1.3, 1.4, 1.5]
    public static let standard = TextAppearance()
    public static let scaleKey = "textScale"
    /// The interface font's key, kept from when there was one font.
    public static let familyKey = "textFontFamily"
    public static let transcriptFamilyKey = "textTranscriptFontFamily"
    public static let codeFamilyKey = "textCodeFontFamily"
    /// The terminal's size at scale 1.
    public static let terminalBaseSize: Double = 12
    /// Monospaced families offered for code even when their fonts do not set the
    /// fixed-pitch flag (D2Coding's wide Hangul clears it, for one).
    public static let knownCodeFamilies: Set<String> = [
        "Menlo", "Monaco", "SF Mono", "JetBrains Mono", "D2Coding", "D2Coding ligature", "Courier", "Courier New",
        "Andale Mono", "PT Mono", "Fira Code", "Fira Mono", "Source Code Pro", "Cascadia Code", "Cascadia Mono",
        "IBM Plex Mono", "Hack", "Inconsolata", "Roboto Mono", "Ubuntu Mono", "Consolas",
    ]

    /// One of `scales`.
    public let scale: Double
    /// The interface's installed font family; nil is the system font.
    public let family: String?
    /// The conversation's family; nil is the interface's.
    public let transcriptFamily: String?
    /// Code's and the terminals' family; nil is the system monospaced font.
    public let codeFamily: String?

    public init(scale: Double = 1, family: String? = nil, transcriptFamily: String? = nil, codeFamily: String? = nil) {
        self.scale = Self.normalized(scale)
        self.family = Self.normalizedFamily(family)
        self.transcriptFamily = Self.normalizedFamily(transcriptFamily)
        self.codeFamily = Self.normalizedFamily(codeFamily)
    }

    /// A family name without its surrounding blanks; empty is nil.
    public static func normalizedFamily(_ family: String?) -> String? {
        let trimmed = family?.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed?.isEmpty == false ? trimmed : nil
    }

    /// The nearest step in `scales`; anything unreadable is 1.
    public static func normalized(_ value: Double) -> Double {
        guard value.isFinite, value > 0 else { return 1 }
        return scales.min { abs($0 - value) < abs($1 - value) } ?? 1
    }

    /// `base` points at this scale, on the half point; at scale 1 exactly `base`.
    public func size(_ base: Double) -> Double {
        scale == 1 ? base : (base * scale * 2).rounded() / 2
    }

    public var terminalSize: Double { size(Self.terminalBaseSize) }
    /// The family the conversation draws in: its own, else the interface's; nil is the system font.
    public var resolvedTranscriptFamily: String? { transcriptFamily ?? family }
    public var percent: Int { Int((scale * 100).rounded()) }
    public var isStandard: Bool { self == .standard }

    private var index: Int { Self.scales.firstIndex(of: scale) ?? Self.scales.firstIndex(of: 1) ?? 0 }
    public var canGrow: Bool { index < Self.scales.count - 1 }
    public var canShrink: Bool { index > 0 }

    /// `steps` sizes larger (or smaller when negative), held to the ends of `scales`.
    public func stepped(_ steps: Int) -> TextAppearance {
        let next = min(Self.scales.count - 1, max(0, index + steps))
        return with(scale: Self.scales[next])
    }

    public func with(scale: Double) -> TextAppearance {
        TextAppearance(scale: scale, family: family, transcriptFamily: transcriptFamily, codeFamily: codeFamily)
    }

    public func with(family: String?) -> TextAppearance {
        TextAppearance(scale: scale, family: family, transcriptFamily: transcriptFamily, codeFamily: codeFamily)
    }

    public func with(transcriptFamily: String?) -> TextAppearance {
        TextAppearance(scale: scale, family: family, transcriptFamily: transcriptFamily, codeFamily: codeFamily)
    }

    public func with(codeFamily: String?) -> TextAppearance {
        TextAppearance(scale: scale, family: family, transcriptFamily: transcriptFamily, codeFamily: codeFamily)
    }

    /// The families the code picker offers, in the order given: those whose fonts are
    /// fixed-pitch and the well-known monospaced ones, without hidden (".") or repeated names.
    public static func codeFamilies(_ fonts: [(family: String, fixedPitch: Bool)]) -> [String] {
        var seen = Set<String>()
        return fonts.compactMap { font in
            guard !font.family.hasPrefix("."), font.fixedPitch || knownCodeFamilies.contains(font.family),
                  seen.insert(font.family).inserted else { return nil }
            return font.family
        }
    }

    /// The saved setting; a missing or broken value reads as the standard one.
    public init(defaults: UserDefaults) {
        let scale = defaults.object(forKey: Self.scaleKey) as? Double ?? 1
        self.init(scale: scale, family: defaults.string(forKey: Self.familyKey),
                  transcriptFamily: defaults.string(forKey: Self.transcriptFamilyKey), codeFamily: defaults.string(forKey: Self.codeFamilyKey))
    }

    /// Writes the setting; the standard values are removed rather than stored.
    public func save(to defaults: UserDefaults) {
        if scale == 1 { defaults.removeObject(forKey: Self.scaleKey) } else { defaults.set(scale, forKey: Self.scaleKey) }
        for (value, key) in [(family, Self.familyKey), (transcriptFamily, Self.transcriptFamilyKey), (codeFamily, Self.codeFamilyKey)] {
            if let value { defaults.set(value, forKey: key) } else { defaults.removeObject(forKey: key) }
        }
    }
}
