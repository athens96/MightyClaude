import Foundation
import libzstd

/// zstd for the clipboard, the same format the phone's `react-native-zstd`
/// writes. One payload per call and no dictionary, so a secret is never
/// compressed next to bytes someone else chose.
public struct ZstdScreenShareCompressor: ScreenShareCompressor {
    /// The phone's level; fast enough that a 1 MB paste does not stall.
    public static let level: Int32 = 3

    public init() {}

    public func compress(_ data: Data) -> Data? {
        guard !data.isEmpty else { return nil }
        let bound = ZSTD_compressBound(data.count)
        var output = Data(count: bound)
        let written = output.withUnsafeMutableBytes { out in
            data.withUnsafeBytes { input in
                ZSTD_compress(out.baseAddress, bound, input.baseAddress, data.count, Self.level)
            }
        }
        guard ZSTD_isError(written) == 0 else { return nil }
        output.count = written
        return output
    }

    /// The largest window a frame may ask the decoder for: 2 MiB, twice the
    /// clipboard ceiling. zstd's own default allows 128 MiB, which a hostile
    /// frame header could make the decoder allocate before a byte comes out.
    public static let windowLogMax: Int32 = 21

    /// Streams the frame out and gives up the moment the output would pass
    /// `limit`, so a small frame that claims (or expands to) gigabytes costs at
    /// most `limit` bytes of memory. The frame's own size header is never
    /// trusted for the allocation — but a header that declares more than
    /// `limit` is refused before decoding starts.
    public func decompress(_ data: Data, limit: Int) -> Data? {
        guard !data.isEmpty, limit > 0 else { return nil }
        let declared = data.withUnsafeBytes { ZSTD_getFrameContentSize($0.baseAddress, data.count) }
        // UInt64.max is "unknown" (a streamed frame), UInt64.max - 1 a bad header.
        if declared == UInt64.max - 1 { return nil }
        if declared != UInt64.max, declared > UInt64(limit) { return nil }
        guard let stream = ZSTD_createDStream() else { return nil }
        defer { ZSTD_freeDStream(stream) }
        guard ZSTD_isError(ZSTD_initDStream(stream)) == 0,
              ZSTD_isError(ZSTD_DCtx_setParameter(stream, ZSTD_d_windowLogMax, Self.windowLogMax)) == 0
        else { return nil }
        let chunk = ZSTD_DStreamOutSize()
        var scratch = [UInt8](repeating: 0, count: chunk)
        var result = Data()
        var finished = false
        let ok: Bool = data.withUnsafeBytes { raw in
            var input = ZSTD_inBuffer(src: raw.baseAddress, size: data.count, pos: 0)
            while true {
                let (status, produced): (Int, Int) = scratch.withUnsafeMutableBytes { out in
                    var output = ZSTD_outBuffer(dst: out.baseAddress, size: chunk, pos: 0)
                    let status = ZSTD_decompressStream(stream, &output, &input)
                    return (status, output.pos)
                }
                if ZSTD_isError(status) != 0 { return false }
                guard result.count + produced <= limit else { return false }
                result.append(contentsOf: scratch[0..<produced])
                if status == 0 { finished = true }
                // All input read and nothing more buffered inside the decoder.
                if input.pos == input.size && produced < chunk { return true }
            }
        }
        return ok && finished ? result : nil
    }
}
