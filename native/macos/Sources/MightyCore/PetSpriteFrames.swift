import CoreGraphics
import Foundation
import ImageIO

/// Cuts a pet spritesheet into frames. Each frame is drawn into its own bitmap
/// rather than `cropping(to:)`, because a cropped image keeps the whole decoded
/// sheet alive: once the frames exist the sheet and its ImageIO buffer can go.
public enum PetSpriteFrames {
    public static func decode(_ url: URL) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        return CGImageSourceCreateImageAtIndex(source, 0, [kCGImageSourceShouldCacheImmediately: false] as CFDictionary)
    }

    /// Row r, column c is the cell at (c * width, r * height) from the sheet's top-left corner.
    public static func slice(_ sheet: CGImage, frameCounts: [Int], cell: CGSize) -> [[CGImage]] {
        let width = Int(cell.width), height = Int(cell.height)
        guard width > 0, height > 0, let space = CGColorSpace(name: CGColorSpace.sRGB) else { return frameCounts.map { _ in [] } }
        return frameCounts.enumerated().map { row, count in
            (0..<count).compactMap { column -> CGImage? in
                guard let cropped = sheet.cropping(to: CGRect(x: column * width, y: row * height, width: width, height: height)),
                      cropped.width == width, cropped.height == height,
                      let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4,
                                              space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
                context.interpolationQuality = .none
                context.draw(cropped, in: CGRect(x: 0, y: 0, width: width, height: height))
                return context.makeImage()
            }
        }
    }
}
