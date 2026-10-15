import Foundation

/// The Mac's text setting (Settings → General → Display, and View → ⌘+ / ⌘− / ⌘0):
/// one size scale for every word the app draws, and an optional font family for
/// its proportional text. Code, terminals and other monospaced text keep the
/// system monospaced face and follow only the scale. Saved in the app's defaults,
/// not in the workspace state, so the phone and the Windows app never see it.
public struct TextAppearance: Equatable, Hashable, Sendable {
    /// The sizes the menu and Settings step through; 1 is the size the app was drawn at.
    public static let scales: [Double] = [0.85, 0.9, 1, 1.1, 1.2, 1.3, 1.4, 1.5]
    public static let standard = TextAppearance()
    public static let scaleKey = "textScale"
    public static let familyKey = "textFontFamily"
    /// The terminal's size at scale 1.
    public static let terminalBaseSize: Double = 12

    /// One of `scales`.
    public let scale: Double
    /// An installed font family; nil is the system font.
    public let family: String?

    public init(scale: Double = 1, family: String? = nil) {
        self.scale = Self.normalized(scale)
        let trimmed = family?.trimmingCharacters(in: .whitespacesAndNewlines)
        self.family = trimmed?.isEmpty == false ? trimmed : nil
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
    public var percent: Int { Int((scale * 100).rounded()) }
    public var isStandard: Bool { self == .standard }

    private var index: Int { Self.scales.firstIndex(of: scale) ?? Self.scales.firstIndex(of: 1) ?? 0 }
    public var canGrow: Bool { index < Self.scales.count - 1 }
    public var canShrink: Bool { index > 0 }

    /// `steps` sizes larger (or smaller when negative), held to the ends of `scales`.
    public func stepped(_ steps: Int) -> TextAppearance {
        let next = min(Self.scales.count - 1, max(0, index + steps))
        return TextAppearance(scale: Self.scales[next], family: family)
    }

    public func with(family: String?) -> TextAppearance { TextAppearance(scale: scale, family: family) }

    /// The saved setting; a missing or broken value reads as the standard one.
    public init(defaults: UserDefaults) {
        let scale = defaults.object(forKey: Self.scaleKey) as? Double ?? 1
        self.init(scale: scale, family: defaults.string(forKey: Self.familyKey))
    }

    /// Writes the setting; the standard values are removed rather than stored.
    public func save(to defaults: UserDefaults) {
        if scale == 1 { defaults.removeObject(forKey: Self.scaleKey) } else { defaults.set(scale, forKey: Self.scaleKey) }
        if let family { defaults.set(family, forKey: Self.familyKey) } else { defaults.removeObject(forKey: Self.familyKey) }
    }
}
