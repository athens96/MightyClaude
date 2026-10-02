import Foundation
import Testing
import libzstd
@testable import MightyCore

/// zstd for the screen-share clipboard: the format the phone writes, and a
/// decompressor that stops at the 1 MB ceiling instead of expanding a bomb.
struct ScreenShareZstdTests {
    @Test func zstdRoundTripsAndCompressesText() throws {
        let zstd = ZstdScreenShareCompressor()
        let text = Data(String(repeating: "터미널 출력 line\n", count: 2_000).utf8)
        let packed = try #require(zstd.compress(text))
        #expect(packed.count < text.count / 10)
        #expect(zstd.decompress(packed, limit: ScreenShareClipboardLimits.maximumBytes) == text)
        // Through the clipboard codec: the tag says zstd and the text comes back.
        let encoded = try #require(ScreenShareClipboardCodec.encode(text: "붙여넣기 " + String(repeating: "x", count: 500),
                                                                    compressor: zstd))
        #expect(encoded.encoding == .zstd)
        guard case .success(let decoded) = ScreenShareClipboardCodec.decode(
            encoding: encoded.encoding, declaredBytes: encoded.plaintextBytes, payload: encoded.payload,
            compressor: zstd) else { Issue.record("zstd clipboard did not decode"); return }
        #expect(decoded.hasPrefix("붙여넣기 "))
    }

    @Test func zstdStopsAtTheLimitInsteadOfExpandingABomb() throws {
        let zstd = ZstdScreenShareCompressor()
        // 8 MB of zeros packs into a few hundred bytes.
        let bomb = try #require(zstd.compress(Data(count: 8 * 1_024 * 1_024)))
        #expect(bomb.count < 4_096)
        #expect(zstd.decompress(bomb, limit: ScreenShareClipboardLimits.maximumBytes) == nil)
        // Garbage and a truncated frame are refused, not guessed at.
        #expect(zstd.decompress(Data("not zstd".utf8), limit: 1_024) == nil)
        let good = try #require(zstd.compress(Data(String(repeating: "abc", count: 1_000).utf8)))
        #expect(zstd.decompress(good.prefix(good.count / 2), limit: 1_000_000) == nil)
    }

    @Test func aFrameThatDeclaresMoreThanTheLimitIsRefusedBeforeDecoding() throws {
        let zstd = ZstdScreenShareCompressor()
        // A one-shot frame records its content size in the header.
        let text = Data(String(repeating: "z", count: 5_000).utf8)
        let packed = try #require(zstd.compress(text))
        #expect(ZSTD_getFrameContentSize([UInt8](packed), packed.count) == 5_000)
        #expect(zstd.decompress(packed, limit: 4_999) == nil)
        #expect(zstd.decompress(packed, limit: 5_000) == text)
    }

    @Test func aFrameAskingForAHugeWindowIsRefused() throws {
        // A frame header whose window descriptor asks for 2^27 bytes (128 MiB),
        // with no content size: the decoder must refuse it, not allocate it.
        // Magic, frame header descriptor (single segment off, no checksum, no
        // content size, no dictionary id), window descriptor (exponent 17 →
        // windowLog 27), then an empty last raw block.
        let frame = Data([0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x88, 0x01, 0x00, 0x00])
        #expect(ZstdScreenShareCompressor().decompress(frame, limit: ScreenShareClipboardLimits.maximumBytes) == nil)
        // The same frame with a 1 MiB window (exponent 10 → windowLog 20) is fine
        // and simply empty — which the codec then refuses as no text at all.
        let small = Data([0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x50, 0x01, 0x00, 0x00])
        #expect(ZstdScreenShareCompressor().decompress(small, limit: ScreenShareClipboardLimits.maximumBytes) == Data())
    }
}
