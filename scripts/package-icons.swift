import Foundation
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers

enum IconError: Error { case invalidSource, renderFailed, encodeFailed }
guard CommandLine.arguments.count == 4 else {
    fatalError("Usage: package-icons.swift source.png output.iconset output.ico")
}
let sourceURL = URL(fileURLWithPath: CommandLine.arguments[1])
let iconsetURL = URL(fileURLWithPath: CommandLine.arguments[2], isDirectory: true)
let icoURL = URL(fileURLWithPath: CommandLine.arguments[3])
guard let source = CGImageSourceCreateWithURL(sourceURL as CFURL, nil),
      CGImageSourceGetType(source) as String? == UTType.png.identifier,
      let image = CGImageSourceCreateImageAtIndex(source, 0, nil),
      image.width == image.height, image.width >= 1024 else {
    throw IconError.invalidSource
}

func png(size: Int) throws -> Data {
    guard let color = CGColorSpace(name: CGColorSpace.sRGB),
          let context = CGContext(data: nil, width: size, height: size,
                                  bitsPerComponent: 8, bytesPerRow: size * 4, space: color,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
        throw IconError.renderFailed
    }
    context.interpolationQuality = .high
    context.draw(image, in: CGRect(x: 0, y: 0, width: size, height: size))
    guard let resized = context.makeImage() else { throw IconError.renderFailed }
    let data = NSMutableData()
    guard let destination = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil) else {
        throw IconError.encodeFailed
    }
    CGImageDestinationAddImage(destination, resized, nil)
    guard CGImageDestinationFinalize(destination) else { throw IconError.encodeFailed }
    return data as Data
}

for points in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let suffix = scale == 2 ? "@2x" : ""
        try png(size: points * scale).write(to: iconsetURL.appendingPathComponent("icon_\(points)x\(points)\(suffix).png"))
    }
}

// Windows 10/11 accept PNG-compressed ICO entries, including their alpha channel.
let sizes = [16, 24, 32, 48, 64, 128, 256]
let entries = try sizes.map { try png(size: $0) }
var ico = Data()
func append16(_ value: UInt16) { ico.append(contentsOf: [UInt8(value & 255), UInt8(value >> 8)]) }
func append32(_ value: UInt32) {
    ico.append(contentsOf: [UInt8(value & 255), UInt8((value >> 8) & 255), UInt8((value >> 16) & 255), UInt8(value >> 24)])
}
append16(0); append16(1); append16(UInt16(sizes.count))
var offset = UInt32(6 + sizes.count * 16)
for (size, entry) in zip(sizes, entries) {
    let dimension = size == 256 ? UInt8(0) : UInt8(size)
    ico.append(contentsOf: [dimension, dimension, 0, 0])
    append16(1); append16(32); append32(UInt32(entry.count)); append32(offset)
    offset += UInt32(entry.count)
}
for entry in entries { ico.append(entry) }
try ico.write(to: icoURL, options: .atomic)
