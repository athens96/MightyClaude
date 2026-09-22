import Testing
@testable import MightyCore

struct ClipboardShortcutTests {
    private let command: UInt = 1 << 20

    @Test func koreanAndMissingCharactersUsePhysicalKey() {
        #expect(ClipboardShortcut.match(modifiers: command, characters: "ㅊ", keyCode: 8) == .copy)
        #expect(ClipboardShortcut.match(modifiers: command, characters: "ㅍ", keyCode: 9) == .paste)
        #expect(ClipboardShortcut.match(modifiers: command, characters: nil, keyCode: 8) == .copy)
        #expect(ClipboardShortcut.match(modifiers: command, characters: "", keyCode: 9) == .paste)
        #expect(ClipboardShortcut.match(modifiers: command, characters: "ㅁ", keyCode: 0) == nil)
    }

    @Test func characterMappingWinsOverPhysicalPosition() {
        #expect(ClipboardShortcut.match(modifiers: command, characters: "v", keyCode: 8) == .paste)
        #expect(ClipboardShortcut.match(modifiers: command, characters: "C", keyCode: 9) == .copy)
        #expect(ClipboardShortcut.match(modifiers: command, characters: "c", keyCode: 42) == .copy)
        for text in ["j", ".", "é"] {
            #expect(ClipboardShortcut.match(modifiers: command, characters: text, keyCode: 8) == nil)
            #expect(ClipboardShortcut.match(modifiers: command, characters: text, keyCode: 9) == nil)
        }
    }

    @Test func extraModifiersKeepTheirOwnShortcuts() {
        for extra in [UInt(1 << 17), UInt(1 << 18), UInt(1 << 19)] {
            #expect(ClipboardShortcut.match(modifiers: command | extra, characters: "ㅊ", keyCode: 8) == nil)
            #expect(ClipboardShortcut.match(modifiers: command | extra, characters: "ㅍ", keyCode: 9) == nil)
        }
        #expect(ClipboardShortcut.match(modifiers: 0, characters: "c", keyCode: 8) == nil)
        #expect(ClipboardShortcut.match(modifiers: command | 1 << 16 | 1 << 21 | 1 << 23, characters: "ㅍ", keyCode: 9) == .paste)
    }
}
