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

    /// The installed font families for the Settings picker, in the system's order: only
    /// those whose regular face draws Latin letters, so a symbol font (Webdings…) that
    /// would turn every label, the reset button included, into pictures is not offered.
    static var families: [String] {
        listLock.lock(); defer { listLock.unlock() }
        if let cachedFamilies { return cachedFamilies }
        let letters = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ")
        let list = NSFontManager.shared.availableFontFamilies.filter { family in
            guard !family.hasPrefix("."), let font = NSFontManager.shared.font(withFamily: family, traits: [], weight: 5, size: 13) else { return false }
            return font.coveredCharacterSet.isSuperset(of: letters)
        }
        cachedFamilies = list
        return list
    }
    nonisolated(unsafe) private static var cachedFamilies: [String]?

    /// The families the code and terminal picker offers (`TextAppearance.codeFamilies`):
    /// of `families`, those whose regular face is fixed-pitch, and the well-known
    /// monospaced ones.
    static var codeFamilies: [String] {
        let all = families
        listLock.lock(); defer { listLock.unlock() }
        if let cachedCodeFamilies { return cachedCodeFamilies }
        let manager = NSFontManager.shared
        let list = TextAppearance.codeFamilies(all.map { family in
            let font = manager.font(withFamily: family, traits: [], weight: 5, size: 13)
            return (family, font.map { $0.isFixedPitch || manager.traits(of: $0).contains(.fixedPitchFontMask) } ?? false)
        })
        cachedCodeFamilies = list
        return list
    }
    nonisolated(unsafe) private static var cachedCodeFamilies: [String]?
    private static let listLock = NSLock()
    /// Fonts installed or removed while the app runs: the lists and lookups are made again.
    private static let fontSetObserver: Void = {
        NotificationCenter.default.addObserver(forName: NSFont.fontSetChangedNotification, object: nil, queue: nil) { _ in
            listLock.lock(); cachedFamilies = nil; cachedCodeFamilies = nil; listLock.unlock()
            NSFont.clearFamilyFonts()
        }
    }()
}

extension Font {
    /// `Font.system(size:weight:design:)` at the user's text size: the default design in
    /// the interface's family, the monospaced one in the code family.
    static func ui(size: CGFloat, weight: Font.Weight = .regular, design: Font.Design = .default) -> Font {
        let appearance = TextSettings.current
        switch design {
        case .default: return styled(appearance.family, size: size, weight: weight, design: design, appearance: appearance)
        case .monospaced: return styled(appearance.codeFamily, size: size, weight: weight, design: design, appearance: appearance)
        default: return styled(nil, size: size, weight: weight, design: design, appearance: appearance)
        }
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

    /// `monospacedSystemFont(ofSize:weight:)` at the user's text size, in the code family.
    static func uiMonospaced(ofSize size: CGFloat, weight: NSFont.Weight = .regular) -> NSFont {
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
    nonisolated(unsafe) private static var familyFonts: [String: NSFont] = [:]
    nonisolated(unsafe) private static var missingFamilies = Set<String>()

    fileprivate static func clearFamilyFonts() {
        familyLock.lock(); defer { familyLock.unlock() }
        familyFonts.removeAll(); missingFamilies.removeAll()
    }

    /// `family` at `size` and the nearest weight it has; nil when it is not installed.
    fileprivate static func familyFont(_ family: String, size: CGFloat, weight: NSFont.Weight) -> NSFont? {
        let key = "\(family)|\(size)|\(weight.rawValue)"
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
