import AppKit
import Observation
import SwiftUI
import MightyCore

/// The live `TextAppearance` (Settings → General → 글자, View → ⌘= / ⌘− / ⌘0).
///
/// Every font the app draws goes through `Font.ui` or `NSFont.ui` (the interface),
/// `Font.transcript` or `NSFont.uiTranscript` (the conversation) or the monospaced
/// forms (code), which read `current`. A SwiftUI body that does so is observed
/// (Observation, written out by hand since Command Line Tools may lack the macro
/// plugin), and `AppStore` sends a change as well, so open views redraw at once;
/// AppKit text (the transcript, the composer, the terminals) is rebuilt by its owner
/// on `didChange`.
final class TextSettings: Observable, @unchecked Sendable {
    static let shared = TextSettings()
    static let didChange = Notification.Name("MightyTextAppearanceDidChange")

    /// The help capture and the smoke runs draw at the standard size whatever the user chose.
    private let fixed: Bool
    private let registrar = ObservationRegistrar()
    private let lock = NSLock()
    private var stored: TextAppearance

    private init() {
        _ = Self.fontSetObserver
        fixed = ProcessInfo.processInfo.arguments.contains { $0 == HelpCapture.argument || ($0.hasPrefix("--") && $0.hasSuffix("smoke-test")) }
        stored = fixed ? .standard : TextAppearance(defaults: .standard)
    }

    var appearance: TextAppearance {
        registrar.access(self, keyPath: \.appearance)
        return current
    }

    /// The value without registering an observation, for AppKit code.
    var current: TextAppearance { lock.withLock { stored } }

    static var current: TextAppearance { shared.appearance }

    func set(_ value: TextAppearance) {
        guard value != current else { return }
        registrar.withMutation(of: self, keyPath: \.appearance) { lock.withLock { stored = value } }
        if !fixed { value.save(to: .standard) }
        NotificationCenter.default.post(name: Self.didChange, object: nil)
    }

    /// The installed font families for the Settings pickers, in the system's order: only
    /// those whose regular face draws Latin letters, so a symbol font (Webdings…) that
    /// would turn every label, the reset button included, into pictures is not offered.
    @MainActor static var families: [String] { fontLists.families }

    /// The families the code and terminal picker offers (`TextAppearance.codeFamilies`):
    /// of `families`, those whose regular face is fixed-pitch, and the well-known
    /// monospaced ones.
    @MainActor static var codeFamilies: [String] { fontLists.codeFamilies }

    private struct FontLists {
        let families: [String]
        let codeFamilies: [String]
    }

    /// Both picker lists, from one pass over the installed families; made again after
    /// fonts are installed or removed.
    @MainActor private static var fontLists: FontLists {
        if let cachedFontLists { return cachedFontLists }
        let letters = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ")
        let manager = NSFontManager.shared
        var fonts: [(family: String, fixedPitch: Bool)] = []
        for family in manager.availableFontFamilies where !family.hasPrefix(".") {
            guard let font = manager.font(withFamily: family, traits: [], weight: 5, size: 13),
                  font.coveredCharacterSet.isSuperset(of: letters) else { continue }
            fonts.append((family, font.isFixedPitch || manager.traits(of: font).contains(.fixedPitchFontMask)))
        }
        let lists = FontLists(families: fonts.map(\.family), codeFamilies: TextAppearance.codeFamilies(fonts))
        cachedFontLists = lists
        return lists
    }
    @MainActor private static var cachedFontLists: FontLists?

    /// Fonts installed or removed while the app runs (AppKit's notice, and Core Text's for
    /// fonts registered by an app): the lists and lookups are made again.
    private static let fontSetObserver: Void = {
        let names = [NSFont.fontSetChangedNotification, Notification.Name(kCTFontManagerRegisteredFontsChangedNotification as String)]
        for name in names {
            NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { _ in
                MainActor.assumeIsolated { cachedFontLists = nil }
                NSFont.clearFamilyFonts()
            }
        }
    }()
}

extension Font {
    /// `Font.system(size:weight:design:)` at the user's text size, in the interface's family
    /// for the default design. Other designs, the monospaced one for paths, versions and
    /// counts included, keep the system face; code takes `code(size:weight:)`.
    static func ui(size: CGFloat, weight: Font.Weight = .regular, design: Font.Design = .default) -> Font {
        let appearance = TextSettings.current
        return styled(design == .default ? appearance.family : nil, size: size, weight: weight, design: design, appearance: appearance)
    }

    /// Code, tool output and terminal-like text at the user's text size, in the code
    /// family, else the system monospaced font.
    static func code(size: CGFloat, weight: Font.Weight = .regular) -> Font {
        let appearance = TextSettings.current
        return styled(appearance.codeFamily, size: size, weight: weight, design: .monospaced, appearance: appearance)
    }

    /// The conversation's words (replies, the user's turns, Mighty's prose) at the user's
    /// text size, in the conversation's family, else the interface's.
    static func transcript(size: CGFloat, weight: Font.Weight = .regular) -> Font {
        let appearance = TextSettings.current
        return styled(appearance.resolvedTranscriptFamily, size: size, weight: weight, design: .default, appearance: appearance)
    }

    private static func styled(_ family: String?, size: CGFloat, weight: Font.Weight, design: Font.Design, appearance: TextAppearance) -> Font {
        let scaled = CGFloat(appearance.size(Double(size)))
        guard let family, let font = NSFont.familyFont(family, size: scaled, weight: weight.nsWeight) else {
            return .system(size: scaled, weight: weight, design: design)
        }
        return Font(font as CTFont)
    }
}

extension NSFont {
    /// `systemFont(ofSize:weight:)` at the user's text size, in the chosen family.
    static func ui(ofSize size: CGFloat, weight: NSFont.Weight = .regular) -> NSFont {
        let appearance = TextSettings.shared.current
        let scaled = CGFloat(appearance.size(Double(size)))
        if let family = appearance.family, let font = familyFont(family, size: scaled, weight: weight) { return font }
        return .systemFont(ofSize: scaled, weight: weight)
    }

    /// The conversation's words at the user's text size, in the conversation's family,
    /// else the interface's.
    static func uiTranscript(ofSize size: CGFloat, weight: NSFont.Weight = .regular) -> NSFont {
        let appearance = TextSettings.shared.current
        let scaled = CGFloat(appearance.size(Double(size)))
        if let family = appearance.resolvedTranscriptFamily, let font = familyFont(family, size: scaled, weight: weight) { return font }
        return .systemFont(ofSize: scaled, weight: weight)
    }

    /// `monospacedSystemFont(ofSize:weight:)` at the user's text size.
    static func uiMonospaced(ofSize size: CGFloat, weight: NSFont.Weight = .regular) -> NSFont {
        .monospacedSystemFont(ofSize: CGFloat(TextSettings.shared.current.size(Double(size))), weight: weight)
    }

    /// Code, tool output and terminal-like text at the user's text size, in the code
    /// family, else the system monospaced font.
    static func uiCode(ofSize size: CGFloat, weight: NSFont.Weight = .regular) -> NSFont {
        let appearance = TextSettings.shared.current
        let scaled = CGFloat(appearance.size(Double(size)))
        if let family = appearance.codeFamily, let font = familyFont(family, size: scaled, weight: weight) { return font }
        return .monospacedSystemFont(ofSize: scaled, weight: weight)
    }

    /// `monospacedDigitSystemFont(ofSize:weight:)` at the user's text size.
    static func uiMonospacedDigit(ofSize size: CGFloat, weight: NSFont.Weight = .regular) -> NSFont {
        .monospacedDigitSystemFont(ofSize: CGFloat(TextSettings.shared.current.size(Double(size))), weight: weight)
    }

    private static let familyLock = NSLock()
    private struct FamilyFontKey: Hashable {
        let family: String
        let size: CGFloat
        let weight: CGFloat
    }
    nonisolated(unsafe) private static var familyFonts: [FamilyFontKey: NSFont] = [:]
    nonisolated(unsafe) private static var missingFamilies = Set<String>()

    fileprivate static func clearFamilyFonts() {
        familyLock.lock(); defer { familyLock.unlock() }
        familyFonts.removeAll(); missingFamilies.removeAll()
    }

    /// `family` at `size` and the nearest weight it has; nil when it is not installed.
    fileprivate static func familyFont(_ family: String, size: CGFloat, weight: NSFont.Weight) -> NSFont? {
        let key = FamilyFontKey(family: family, size: size, weight: weight.rawValue)
        familyLock.lock(); defer { familyLock.unlock() }
        if let font = familyFonts[key] { return font }
        if missingFamilies.contains(family) { return nil }
        let manager = NSFontManager.shared
        let wanted = weight.managerWeight
        guard let font = manager.font(withFamily: family, traits: [], weight: wanted, size: size)
                ?? manager.font(withFamily: family, traits: wanted >= 8 ? .boldFontMask : [], weight: 5, size: size)
                ?? manager.font(withFamily: family, traits: [], weight: 5, size: size) else {
            missingFamilies.insert(family)
            return nil
        }
        familyFonts[key] = font
        return font
    }
}

private extension Font.Weight {
    var nsWeight: NSFont.Weight {
        switch self {
        case .ultraLight: .ultraLight
        case .thin: .thin
        case .light: .light
        case .medium: .medium
        case .semibold: .semibold
        case .bold: .bold
        case .heavy: .heavy
        case .black: .black
        default: .regular
        }
    }
}

private extension NSFont.Weight {
    /// NSFontManager's 0–15 weight scale (5 regular, 9 bold).
    var managerWeight: Int {
        switch rawValue {
        case ..<(-0.7): 2
        case ..<(-0.5): 3
        case ..<(-0.2): 4
        case ..<0.1: 5
        case ..<0.27: 6
        case ..<0.35: 8
        case ..<0.5: 9
        case ..<0.6: 10
        default: 11
        }
    }
}
