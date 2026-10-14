import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// Pet frames are copied out of the spritesheet into their own bitmaps, in the
/// same cells `cropping(to:)` picks (top-left origin, rows top to bottom).
struct PetSpriteFramesTests {
    private static let cell = CGSize(width: 192, height: 208)
    private static let colours: [[(UInt8, UInt8, UInt8)]] = [
        [(255, 0, 0), (0, 255, 0), (0, 0, 255)],
        [(255, 255, 0), (0, 255, 255), (255, 0, 255)],
    ]
    private static let marker: (UInt8, UInt8, UInt8) = (10, 20, 30)

    /// 3 × 2 cells of solid colours; every cell has one marker pixel at (2, 3) from its top-left.
    private func sheet() -> CGImage {
        let width = 3 * 192, height = 2 * 208
        var bytes = [UInt8](repeating: 0, count: width * height * 4)
        for y in 0..<height {
            for x in 0..<width {
                let inCell = (x % 192, y % 208)
                let (r, g, b) = inCell == (2, 3) ? Self.marker : Self.colours[y / 208][x / 192]
                let offset = (y * width + x) * 4
                bytes[offset] = r; bytes[offset + 1] = g; bytes[offset + 2] = b; bytes[offset + 3] = 255
            }
        }
        let provider = CGDataProvider(data: Data(bytes) as CFData)!
        return CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                       space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
                       provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)!
    }

    /// RGBA of the pixel at (x, y) from the image's top-left, read through a fresh bitmap.
    private func pixel(_ image: CGImage, x: Int, y: Int) -> (UInt8, UInt8, UInt8, UInt8) {
        var bytes = [UInt8](repeating: 0, count: image.width * image.height * 4)
        bytes.withUnsafeMutableBytes { buffer in
            let context = CGContext(data: buffer.baseAddress, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: image.width * 4,
                                    space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
            context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        }
        let offset = (y * image.width + x) * 4
        return (bytes[offset], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3])
    }

    @Test func slicesEachCellIntoItsOwnFrame() {
        let frames = PetSpriteFrames.slice(sheet(), frameCounts: [3, 2], cell: Self.cell)
        #expect(frames.map(\.count) == [3, 2])
        for (row, images) in frames.enumerated() {
            for (column, image) in images.enumerated() {
                #expect(image.width == 192 && image.height == 208)
                #expect(image.bytesPerRow == 192 * 4)
                let (r, g, b) = Self.colours[row][column]
                let body = pixel(image, x: 100, y: 100)
                #expect(body.0 == r && body.1 == g && body.2 == b && body.3 == 255, "row \(row) column \(column)")
                let mark = pixel(image, x: 2, y: 3)
                #expect(mark.0 == Self.marker.0 && mark.1 == Self.marker.1 && mark.2 == Self.marker.2, "marker at top-left in row \(row) column \(column)")
                let mirrored = pixel(image, x: 2, y: 208 - 1 - 3)
                #expect(mirrored.0 == r && mirrored.1 == g && mirrored.2 == b, "no vertical flip in row \(row) column \(column)")
            }
        }
    }

    @Test func framesOutliveTheSheet() {
        var frames: [[CGImage]] = []
        autoreleasepool { frames = PetSpriteFrames.slice(sheet(), frameCounts: [1, 1], cell: Self.cell) }
        let second = pixel(frames[1][0], x: 50, y: 50)
        #expect(second.0 == 255 && second.1 == 255 && second.2 == 0)
        #expect(frames[1][0].dataProvider != nil)
    }

    @Test func emptyRowsBeyondTheSheet() {
        let frames = PetSpriteFrames.slice(sheet(), frameCounts: [1, 1, 2], cell: Self.cell)
        #expect(frames.map(\.count) == [1, 1, 0])
    }
}
