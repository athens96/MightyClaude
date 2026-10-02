import Foundation
import Testing
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
}
