import Foundation

// MARK: - Zoom

/// The region of the display the phone pinched into, in normalized 0–1
/// coordinates of the captured display.
public struct ScreenShareZoomRegion: Codable, Sendable, Equatable {
    public var x: Double
    public var y: Double
    public var width: Double
    public var height: Double

    public init(x: Double, y: Double, width: Double, height: Double) {
        self.x = x; self.y = y; self.width = width; self.height = height
    }

    /// The whole display: what a session starts at and falls back to.
    public static let full = ScreenShareZoomRegion(x: 0, y: 0, width: 1, height: 1)

    /// Clamps the rectangle inside the display and refuses a degenerate one.
    /// A region smaller than this cannot be read anyway and would ask the
    /// encoder for an absurd scale factor.
    public static let minimumSide = 0.02

    public var normalized: ScreenShareZoomRegion? {
        func clamp(_ value: Double) -> Double { value.isFinite ? min(1, max(0, value)) : 0 }
        var region = ScreenShareZoomRegion(x: clamp(x), y: clamp(y),
                                           width: clamp(width), height: clamp(height))
        guard region.width >= Self.minimumSide, region.height >= Self.minimumSide else { return nil }
        region.width = min(region.width, 1 - region.x)
        region.height = min(region.height, 1 - region.y)
        guard region.width >= Self.minimumSide, region.height >= Self.minimumSide else { return nil }
        return region
    }

    public var isFullScreen: Bool { x <= 0 && y <= 0 && width >= 1 && height >= 1 }

    public var json: [String: Any] { ["x": x, "y": y, "width": width, "height": height] }
}

// MARK: - Clipboard frames

public enum ScreenShareClipboardEncoding: String, Sendable, Equatable {
    case zstd, raw
}

/// One clipboard frame on the data channel. A payload larger than one frame is
/// split: `seq` counts from 0 and `total` says how many frames the transfer has,
/// so neither side ever has to put a megabyte in a single message.
public struct ScreenShareClipboardFrame: Sendable, Equatable {
    public var id: String
    public var encoding: ScreenShareClipboardEncoding
    /// Plaintext byte count of the whole transfer, before compression.
    public var bytes: Int
    public var seq: Int
    public var total: Int
    /// This frame's slice of the (possibly compressed) payload.
    public var data: Data
    /// Set by the Mac for a pasteboard item marked concealed; such an item is
    /// never read in the first place, so the flag only ever travels empty.
    public var concealed: Bool

    public init(id: String, encoding: ScreenShareClipboardEncoding, bytes: Int,
                seq: Int, total: Int, data: Data, concealed: Bool = false) {
        self.id = id; self.encoding = encoding; self.bytes = bytes
        self.seq = seq; self.total = total; self.data = data; self.concealed = concealed
    }
}

/// The ceilings the clipboard path obeys.
public enum ScreenShareClipboardLimits {
    /// Both directions, as the contract says: at most 1 MB per transfer.
    public static let maximumBytes = 1_024 * 1_024
    /// Raw bytes per frame before base64, chosen so one JSON message —
    /// base64 plus the envelope — stays under the 64 KiB data-channel ceiling.
    public static let chunkBytes = 32 * 1_024
    /// A transfer may not claim more frames than the ceiling allows.
    public static var maximumChunks: Int { (maximumBytes / chunkBytes) + 2 }
    /// An unfinished transfer is dropped after this long rather than held.
    public static let assemblyTimeout: TimeInterval = 30
}

// MARK: - Messages

/// Everything the phone may send down the peer connection's data channel. The
/// relay never sees any of it, so the relay's frame limits do not apply — but
/// the Mac still decides whether a single byte of it is acted on.
public enum ScreenShareDataMessage: Sendable, Equatable {
    /// A pointer, scroll, text or key event for `CGEventPost`.
    case input(ScreenShareInputEvent)
    /// Stream this region at full resolution over the low-res overview.
    case zoom(displayId: UInt32, region: ScreenShareZoomRegion)
    /// Capture another display from now on.
    case display(displayId: UInt32)
    /// One frame of a clipboard transfer from the phone.
    case clipboard(ScreenShareClipboardFrame)
    /// The phone pressed "Mac에서 가져오기": send the Mac's pasteboard once.
    case clipboardRequest
    /// The phone went to (or came back from) the background. The host runs the
    /// 30 s rule; the phone only reports the state.
    case background(Bool)
}

/// Why a data-channel frame was dropped. Nothing here names a key or a
/// character: a rejection must not become a keystroke log.
public enum ScreenShareDataRejection: Error, Sendable, Equatable {
    case notJSON
    case unknownType
    case malformed
    case textTooLong
    case clipboardTooLarge
}

public enum ScreenShareDataChannel {
    /// Longest single committed string; anything larger belongs on the clipboard.
    public static let maximumTextBytes = 4_096
    /// One data-channel JSON message. Matches the phone's signalling ceiling so
    /// both sides agree without a second constant.
    public static let maximumMessageBytes = 64 * 1_024

    /// The two shortcuts the beta sends, both for the clipboard. Anything else
    /// is refused rather than guessed at: a remote phone must not be able to
    /// synthesize an arbitrary key combination.
    public static let shortcuts: [String: (code: UInt16, modifiers: ScreenShareModifiers)] = [
        "cmd+c": (8, [.command]),   // kVK_ANSI_C
        "cmd+v": (9, [.command]),   // kVK_ANSI_V
    ]

    /// Reads one data-channel frame. The Mac is the party that enforces the
    /// grant, so this only establishes *what* was asked for.
    public static func decode(_ data: Data) -> Result<ScreenShareDataMessage, ScreenShareDataRejection> {
        guard data.count <= maximumMessageBytes else { return .failure(.malformed) }
        guard let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
            return .failure(.notJSON)
        }
        return decode(object)
    }

    public static func decode(_ object: [String: Any]) -> Result<ScreenShareDataMessage, ScreenShareDataRejection> {
        guard let type = object["t"] as? String else { return .failure(.malformed) }

        func point() -> ScreenShareNormalizedPoint? {
            guard let display = (object["displayId"] as? NSNumber)?.uint32Value,
                  let x = (object["x"] as? NSNumber)?.doubleValue,
                  let y = (object["y"] as? NSNumber)?.doubleValue,
                  x.isFinite, y.isFinite
            else { return nil }
            return ScreenShareNormalizedPoint(displayId: display, x: x, y: y)
        }

        switch type {
        case "tap":
            guard let where_ = point() else { return .failure(.malformed) }
            let button: ScreenShareMouseButton = (object["button"] as? String) == "right" ? .right : .left
            return .success(.input(.click(where_, button: button, clickCount: 1)))
        case "drag":
            guard let where_ = point(), let raw = object["phase"] as? String,
                  let phase = ScreenShareDragPhase(rawValue: raw)
            else { return .failure(.malformed) }
            return .success(.input(.drag(where_, phase: phase)))
        case "scroll":
            guard let where_ = point(),
                  let dx = (object["dx"] as? NSNumber)?.doubleValue,
                  let dy = (object["dy"] as? NSNumber)?.doubleValue,
                  dx.isFinite, dy.isFinite
            else { return .failure(.malformed) }
            return .success(.input(.scroll(where_, deltaX: lines(dx), deltaY: lines(dy))))
        case "text":
            guard let text = object["text"] as? String, !text.isEmpty else { return .failure(.malformed) }
            guard text.utf8.count <= maximumTextBytes else { return .failure(.textTooLong) }
            return .success(.input(.text(text)))
        case "key":
            guard let combo = object["combo"] as? String, let key = shortcuts[combo] else {
                return .failure(.unknownType)
            }
            return .success(.input(.key(code: key.code, modifiers: key.modifiers)))
        case "zoom":
            guard let display = (object["displayId"] as? NSNumber)?.uint32Value,
                  let raw = object["region"] as? [String: Any],
                  let region = zoomRegion(raw)
            else { return .failure(.malformed) }
            return .success(.zoom(displayId: display, region: region))
        case "display":
            guard let display = (object["displayId"] as? NSNumber)?.uint32Value else { return .failure(.malformed) }
            return .success(.display(displayId: display))
        case "clipboard":
            return clipboardFrame(object).map(ScreenShareDataMessage.clipboard)
        case "clipboard-request":
            return .success(.clipboardRequest)
        case "background":
            guard let value = object["background"] as? Bool else { return .failure(.malformed) }
            return .success(.background(value))
        default:
            return .failure(.unknownType)
        }
    }

    /// Normalized scroll deltas become line counts. The phone sends fractions of
    /// a screen; a line is about 1 % of the display, which lands close to a
    /// trackpad's own feel without needing pixel-accurate scrolling.
    static func lines(_ delta: Double) -> Int32 {
        let scaled = (delta * 100).rounded()
        return Int32(max(-120, min(120, scaled)))
    }

    static func zoomRegion(_ raw: [String: Any]) -> ScreenShareZoomRegion? {
        guard let x = (raw["x"] as? NSNumber)?.doubleValue, let y = (raw["y"] as? NSNumber)?.doubleValue,
              let width = (raw["width"] as? NSNumber)?.doubleValue,
              let height = (raw["height"] as? NSNumber)?.doubleValue
        else { return nil }
        return ScreenShareZoomRegion(x: x, y: y, width: width, height: height).normalized
    }

    static func clipboardFrame(_ object: [String: Any]) -> Result<ScreenShareClipboardFrame, ScreenShareDataRejection> {
        guard let base64 = object["data"] as? String, let data = Data(base64Encoded: base64),
              let rawEncoding = object["enc"] as? String,
              let encoding = ScreenShareClipboardEncoding(rawValue: rawEncoding)
        else { return .failure(.malformed) }
        let bytes = (object["bytes"] as? NSNumber)?.intValue ?? data.count
        guard bytes > 0 else { return .failure(.malformed) }
        // The declared plaintext size is checked before a byte is buffered, so a
        // phone cannot make the Mac hold a gigabyte by claiming it will send one.
        guard bytes <= ScreenShareClipboardLimits.maximumBytes else { return .failure(.clipboardTooLarge) }
        let total = (object["total"] as? NSNumber)?.intValue ?? 1
        let seq = (object["seq"] as? NSNumber)?.intValue ?? 0
        guard total >= 1, total <= ScreenShareClipboardLimits.maximumChunks, seq >= 0, seq < total else {
            return .failure(.malformed)
        }
        let id = (object["id"] as? String) ?? "single"
        guard !id.isEmpty, id.utf8.count <= 64 else { return .failure(.malformed) }
        return .success(ScreenShareClipboardFrame(
            id: id, encoding: encoding, bytes: bytes, seq: seq, total: total, data: data,
            concealed: object["concealed"] as? Bool == true))
    }

    /// Splits one clipboard payload into frames the phone can read back. Called
    /// for the Mac → phone direction, where the Mac has already decided the
    /// pasteboard item is not concealed.
    public static func clipboardFrames(
        id: String, encoding: ScreenShareClipboardEncoding, plaintextBytes: Int, payload: Data
    ) -> [[String: Any]] {
        let chunk = ScreenShareClipboardLimits.chunkBytes
        var slices: [Data] = []
        var index = payload.startIndex
        while index < payload.endIndex {
            let end = payload.index(index, offsetBy: min(chunk, payload.distance(from: index, to: payload.endIndex)))
            slices.append(payload[index..<end])
            index = end
        }
        if slices.isEmpty { slices = [Data()] }
        return slices.enumerated().map { offset, slice in
            [
                "t": "clipboard", "dir": "to-phone", "enc": encoding.rawValue,
                "bytes": plaintextBytes, "id": id, "seq": offset, "total": slices.count,
                "data": slice.base64EncodedString(),
            ]
        }
    }
}

/// Reassembles a chunked clipboard transfer. One transfer at a time per
/// session: a phone that starts a second one abandons the first, so a stream of
/// opening frames can never grow the Mac's memory without bound.
public struct ScreenShareClipboardAssembler: Sendable {
    private var id: String?
    private var encoding: ScreenShareClipboardEncoding = .raw
    private var declaredBytes = 0
    private var expected = 0
    private var slices: [Int: Data] = [:]
    private var startedAt: Date?

    public init() {}

    public enum Outcome: Sendable, Equatable {
        /// More frames are needed; `received` of `total` are in.
        case waiting(received: Int, total: Int)
        /// The transfer is complete. `payload` is still compressed if `encoding`
        /// says so; the caller decompresses and checks the plaintext size.
        case complete(encoding: ScreenShareClipboardEncoding, declaredBytes: Int, payload: Data)
        case rejected(ScreenShareDataRejection)
    }

    public mutating func accept(_ frame: ScreenShareClipboardFrame, now: Date = Date()) -> Outcome {
        if let startedAt, now.timeIntervalSince(startedAt) > ScreenShareClipboardLimits.assemblyTimeout {
            reset()
        }
        if id != frame.id {
            reset()
            id = frame.id
            encoding = frame.encoding
            declaredBytes = frame.bytes
            expected = frame.total
            startedAt = now
        }
        guard frame.total == expected, frame.bytes == declaredBytes, frame.encoding == encoding else {
            reset(); return .rejected(.malformed)
        }
        slices[frame.seq] = frame.data
        // Compressed bytes can exceed the plaintext ceiling only for an attacker
        // (a compressor never does), so the buffered size is capped too.
        let buffered = slices.values.reduce(0) { $0 + $1.count }
        guard buffered <= ScreenShareClipboardLimits.maximumBytes else {
            reset(); return .rejected(.clipboardTooLarge)
        }
        guard slices.count == expected else { return .waiting(received: slices.count, total: expected) }
        var payload = Data()
        for index in 0..<expected {
            guard let slice = slices[index] else { reset(); return .rejected(.malformed) }
            payload.append(slice)
        }
        let outcome = Outcome.complete(encoding: encoding, declaredBytes: declaredBytes, payload: payload)
        reset()
        return outcome
    }

    public mutating func reset() {
        id = nil; encoding = .raw; declaredBytes = 0; expected = 0; slices = [:]; startedAt = nil
    }
}

// MARK: - Clipboard payload codec

/// How a clipboard payload is compressed before it is encrypted. zstd on both
/// sides when both have it; a build without a compressor sends `raw`, and the
/// receiver reads the tag rather than guessing.
///
/// A payload is always compressed alone — one clipboard read, one call — so no
/// shared dictionary can ever leak the length of a secret next to attacker-
/// chosen bytes.
public protocol ScreenShareCompressor: Sendable {
    func compress(_ data: Data) -> Data?
    /// - Parameter limit: the largest plaintext the caller will accept; a frame
    ///   that decompresses past it is refused rather than expanded.
    func decompress(_ data: Data, limit: Int) -> Data?
}

public enum ScreenShareClipboardCodec {
    /// Turns a finished transfer back into text.
    public static func decode(
        encoding: ScreenShareClipboardEncoding, declaredBytes: Int, payload: Data,
        compressor: ScreenShareCompressor?
    ) -> Result<String, ScreenShareDataRejection> {
        guard declaredBytes <= ScreenShareClipboardLimits.maximumBytes else { return .failure(.clipboardTooLarge) }
        let plaintext: Data
        switch encoding {
        case .raw:
            plaintext = payload
        case .zstd:
            guard let compressor,
                  let decoded = compressor.decompress(payload, limit: ScreenShareClipboardLimits.maximumBytes)
            else { return .failure(.malformed) }
            plaintext = decoded
        }
        guard plaintext.count <= ScreenShareClipboardLimits.maximumBytes else { return .failure(.clipboardTooLarge) }
        guard let text = String(data: plaintext, encoding: .utf8), !text.isEmpty else { return .failure(.malformed) }
        return .success(text)
    }

    /// Packs the Mac's pasteboard for the phone. zstd when there is a
    /// compressor and it actually helps; `raw` otherwise.
    public static func encode(
        text: String, compressor: ScreenShareCompressor?
    ) -> (encoding: ScreenShareClipboardEncoding, plaintextBytes: Int, payload: Data)? {
        let plaintext = Data(text.utf8)
        guard !plaintext.isEmpty, plaintext.count <= ScreenShareClipboardLimits.maximumBytes else { return nil }
        if let compressor, let compressed = compressor.compress(plaintext), compressed.count < plaintext.count {
            return (.zstd, plaintext.count, compressed)
        }
        return (.raw, plaintext.count, plaintext)
    }
}
