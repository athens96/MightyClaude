import Foundation

/// Recognizes unmodified Command-C/V without depending on an input method's
/// Korean characters. A printable Latin layout always keeps its own mapping.
public enum ClipboardShortcut: Equatable, Sendable {
    case copy
    case paste

    public static func match(modifiers: UInt, characters: String?, keyCode: UInt16) -> Self? {
        let meaningful = modifiers & 0xFFFF_0000 & ~UInt((1 << 16) | (1 << 21) | (1 << 23))
        guard meaningful == 1 << 20 else { return nil }
        let characters = characters?.lowercased() ?? ""
        if characters == "c" { return .copy }
        if characters == "v" { return .paste }
        // Dvorak and other Latin layouts may put another letter or punctuation
        // at the physical C/V positions. Do not shadow that character shortcut.
        if characters.unicodeScalars.contains(where: {
            (0x20...0x024F).contains($0.value) || (0x1E00...0x1EFF).contains($0.value)
        }) { return nil }
        switch keyCode {
        case 8: return .copy
        case 9: return .paste
        default: return nil
        }
    }
}
