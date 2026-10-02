import Foundation
import Testing
@testable import MightyCore

/// The pieces of the documented data-channel format and signalling that need no
/// session at all: key combos, committed-text chunking, the strict clipboard
/// frame, the background notice, the injection tag and the hotkey rule.
struct ScreenShareWireTests {
    // MARK: Key combos

    @Test func aComboIsModifiersThenOneNamedKey() throws {
        let copy = try #require(ScreenShareKeyCombo.parse("cmd+c"))
        #expect(copy.code == 8 && copy.modifiers == [.command])
        let redo = try #require(ScreenShareKeyCombo.parse("shift+cmd+z"))
        #expect(redo.code == 6 && redo.modifiers == [.shift, .command])
        let word = try #require(ScreenShareKeyCombo.parse("opt+left"))
        #expect(word.code == 123 && word.modifiers == [.option])
        #expect(ScreenShareKeyCombo.parse("return")?.code == 36)
        #expect(ScreenShareKeyCombo.parse("ctrl+c")?.modifiers == [.control])
        #expect(ScreenShareKeyCombo.parse("0")?.code == 29)
    }

    @Test func anythingOutsideTheVocabularyIsRefused() {
        for combo in ["", "cmd", "cmd+", "+c", "cmd+cmd+c", "c+cmd", "cmd+C", "hyper+c", "cmd+f5",
                      "cmd+shift+opt+ctrl+fn+c", String(repeating: "cmd+", count: 20) + "c"] {
            #expect(ScreenShareKeyCombo.parse(combo) == nil, "\(combo) parsed")
        }
    }

    // MARK: Committed text

    @Test func committedTextTravelsInChunksOfAtMostTwentyUnits() {
        let ascii = String(repeating: "a", count: 45)
        let chunks = ScreenShareTextChunks.split(ascii)
        #expect(chunks.map(\.count) == [20, 20, 5])
        #expect(String(utf16CodeUnits: chunks.flatMap { $0 }, count: 45) == ascii)
    }

    @Test func aChunkNeverCutsAHangulSyllableOrAnEmojiInHalf() {
        // 21 composed syllables: one unit each, so twenty and one.
        let korean = String(repeating: "한", count: 21)
        #expect(ScreenShareTextChunks.split(korean).map(\.count) == [20, 1])
        // A family emoji is 11 UTF-16 units; two would be 22 and must not share.
        let family = "👨‍👩‍👧‍👦"
        let pair = ScreenShareTextChunks.split(family + family)
        #expect(pair.count == 2)
        #expect(pair.allSatisfy { String(utf16CodeUnits: $0, count: $0.count) == family })
        // Decomposed Hangul (jamo plus combining marks) stays one grapheme too.
        let decomposed = "\u{1112}\u{1161}\u{11AB}"
        let mixed = String(repeating: "x", count: 18) + decomposed
        #expect(ScreenShareTextChunks.split(mixed).map(\.count) == [18, 3])
        #expect(ScreenShareTextChunks.split("").isEmpty)
    }

    // MARK: Clipboard frames

    @Test func aPhoneClipboardFrameNeedsEveryField() {
        let complete: [String: Any] = ["t": "clipboard", "dir": "to-mac", "enc": "raw", "bytes": 3,
                                       "id": "t1", "seq": 0, "total": 1, "data": Data("abc".utf8).base64EncodedString()]
        if case .failure = ScreenShareDataChannel.clipboardFrame(complete) { Issue.record("a full frame was refused") }
        for missing in ["dir", "enc", "bytes", "id", "seq", "total", "data"] {
            var frame = complete
            frame.removeValue(forKey: missing)
            #expect(Self.rejection(frame) == .malformed, "a frame without \(missing) was accepted")
        }
        // A frame the Mac itself wrote cannot be played back to it.
        var mirrored = complete
        mirrored["dir"] = "to-phone"
        #expect(Self.rejection(mirrored) == .malformed)
        var tooMany = complete
        tooMany["total"] = ScreenShareClipboardLimits.maximumChunks + 1
        #expect(Self.rejection(tooMany) == .malformed)
        var outOfRange = complete
        outOfRange["seq"] = 1
        #expect(Self.rejection(outOfRange) == .malformed)
    }

    @Test func everyFrameTheMacWritesFitsOneDataChannelMessage() throws {
        let payload = Data((0..<ScreenShareClipboardLimits.maximumBytes).map { UInt8($0 % 251) })
        let frames = ScreenShareDataChannel.clipboardFrames(
            id: UUID().uuidString, encoding: .raw, plaintextBytes: payload.count, payload: payload)
        #expect(frames.count <= ScreenShareClipboardLimits.maximumChunks)
        for frame in frames {
            let data = try JSONSerialization.data(withJSONObject: frame)
            #expect(data.count <= ScreenShareDataChannel.maximumMessageBytes)
        }
    }

    @Test func aSecondTransferAbandonsTheFirstAndATimedOutOneIsDropped() {
        var assembler = ScreenShareClipboardAssembler()
        let start = Date(timeIntervalSinceReferenceDate: 0)
        func frame(_ id: String, _ seq: Int, _ total: Int, _ text: String) -> ScreenShareClipboardFrame {
            ScreenShareClipboardFrame(id: id, encoding: .raw, bytes: 4, seq: seq, total: total, data: Data(text.utf8))
        }
        #expect(assembler.accept(frame("a", 0, 2, "ab"), now: start) == .waiting(received: 1, total: 2))
        // A new id starts over; the half of "a" is gone.
        #expect(assembler.accept(frame("b", 0, 2, "cd"), now: start) == .waiting(received: 1, total: 2))
        #expect(assembler.accept(frame("b", 1, 2, "ef"), now: start)
                == .complete(encoding: .raw, declaredBytes: 4, payload: Data("cdef".utf8)))
        // Thirty seconds without the rest and the transfer is dropped.
        _ = assembler.accept(frame("c", 0, 2, "gh"), now: start)
        let late = start.addingTimeInterval(ScreenShareClipboardLimits.assemblyTimeout + 1)
        #expect(assembler.accept(frame("c", 1, 2, "ij"), now: late) == .waiting(received: 1, total: 2))
        // A frame that contradicts its own transfer is refused.
        let lying = ScreenShareClipboardFrame(id: "c", encoding: .zstd, bytes: 4, seq: 0, total: 2, data: Data())
        #expect(assembler.accept(lying, now: late) == .rejected(.malformed))
    }

    // MARK: Background notice and injection tag

    @Test func theBackgroundNoticeIsSignallingTheMacReads() throws {
        let parsed = ScreenShareSignal.inbound(["type": "screen-background", "sessionId": "s1", "background": true])
        #expect(parsed == .background(sessionId: "s1", background: true))
        #expect(ScreenShareSignal.inbound(["type": "screen-background", "sessionId": "s1"]) == nil)
        let encoded = try #require(ScreenShareSignal.background(sessionId: "s1", background: false).encoded())
        let object = try #require(try JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        #expect(object["type"] as? String == "screen-background")
        #expect(object["background"] as? Bool == false)
        // The background notice is not a data-channel message.
        #expect(ScreenShareDataChannel.decode(["t": "background", "background": true]).isFailure)
        // And the host's own end reason for it.
        #expect(ScreenShareWireReason.sessionEnd(.background) == "background")
        #expect(ScreenShareWireReason.sessionEnd(.displayGone) == "display-gone")
    }

    @Test func eventsTheMacInjectedAreNotLocalActivity() {
        #expect(!ScreenShareInjectionTag.isLocalActivity(eventSourceUserData: ScreenShareInjectionTag.marker))
        #expect(ScreenShareInjectionTag.isLocalActivity(eventSourceUserData: 0))
    }

    @Test func theKillHotkeyIsHeldOnlyWhileASessionIsLive() {
        let idle = ScreenShareIndicatorState(sessions: [], controlling: false, framesPaused: false)
        let live = ScreenShareIndicatorState(
            sessions: [ScreenShareLiveSession(sessionId: "s", deviceId: "p", mode: .view, startedAt: Date())],
            controlling: false, framesPaused: false)
        #expect(!ScreenShareKillHotkey.isRegistered(for: idle))
        #expect(ScreenShareKillHotkey.isRegistered(for: live))
    }

    private static func rejection(_ frame: [String: Any]) -> ScreenShareDataRejection? {
        if case .failure(let rejection) = ScreenShareDataChannel.clipboardFrame(frame) { return rejection }
        return nil
    }
}

private extension Result where Success == ScreenShareDataMessage, Failure == ScreenShareDataRejection {
    var isFailure: Bool { if case .failure = self { return true }; return false }
}
