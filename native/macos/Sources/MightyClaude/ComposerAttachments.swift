import AppKit
import ImageIO
import SwiftUI
import UniformTypeIdentifiers
import MightyCore

struct AttachmentChip: View {
    let attachment: RunAttachment
    let remove: () -> Void
    @ViewState private var thumbnail: NSImage?

    var body: some View {
        HStack(spacing: 7) {
            Group {
                if let thumbnail { Image(nsImage: thumbnail).resizable().scaledToFill() }
                else { Image(systemName: attachment.mediaType == "application/pdf" ? "doc.richtext" : "doc").font(.system(size: 17)).foregroundStyle(Palette.accent) }
            }
            .frame(width: 34, height: 34).clipped()
            .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 5))
            .clipShape(RoundedRectangle(cornerRadius: 5))
            VStack(alignment: .leading, spacing: 2) {
                Text(attachment.name).font(.system(size: 11, weight: .medium)).lineLimit(1).truncationMode(.middle)
                Text(AttachmentImport.sizeLabel(attachment)).font(.system(size: 10)).foregroundStyle(.secondary)
            }.frame(width: 106, alignment: .leading)
            Button(action: remove) { Image(systemName: "xmark").font(.system(size: 9, weight: .semibold)).frame(width: 20, height: 26).contentShape(Rectangle()) }
                .buttonStyle(.plain).foregroundStyle(.secondary)
                .help(L("composer.attachment.removeHelp")).accessibilityLabel(L("composer.attachment.remove", ["name": attachment.name]))
                .accessibilityIdentifier("remove-attachment-\(attachment.id)")
        }
        .padding(6)
        .background(Color.primary.opacity(0.045), in: RoundedRectangle(cornerRadius: 9))
        .overlay { RoundedRectangle(cornerRadius: 9).stroke(Palette.border, lineWidth: 0.5).allowsHitTesting(false) }
        .help(L("composer.attachment.chipHelp", ["name": attachment.name, "size": AttachmentImport.sizeLabel(attachment)]))
        .accessibilityElement(children: .contain).accessibilityIdentifier("attachment-\(attachment.id)")
        .task(id: attachment.id) { thumbnail = AttachmentImport.thumbnail(attachment) }
    }
}

enum AttachmentImport {
    static func sizeLabel(_ attachment: RunAttachment) -> String {
        ByteCountFormatter.string(fromByteCount: Int64(byteCount(attachment)), countStyle: .file)
    }

    static func byteCount(_ attachment: RunAttachment) -> Int {
        let padding = attachment.dataBase64.suffix(2).filter { $0 == "=" }.count
        return attachment.dataBase64.utf8.count / 4 * 3 - padding
    }

    static func readFile(_ url: URL) throws -> RunAttachment {
        let data = try boundedFileData(url)
        return try AttachmentSupport.make(name: url.lastPathComponent, data: data)
    }

    // Check the file before opening and cap reads as well, since a selected file
    // may grow between metadata lookup and reading its contents.
    static func boundedFileData(_ url: URL) throws -> Data {
        guard url.isFileURL else { throw MightyError(L("composer.attachment.localOnly")) }
        let scoped = url.startAccessingSecurityScopedResource()
        defer { if scoped { url.stopAccessingSecurityScopedResource() } }
        let metadata = try url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey])
        guard metadata.isRegularFile == true else { throw MightyError(L("composer.attachment.folderChosen")) }
        guard (metadata.fileSize ?? 0) <= AttachmentSupport.maximumFileBytes else { throw MightyError(L("composer.attachment.fileTooLarge")) }
        let file = try FileHandle(forReadingFrom: url)
        defer { try? file.close() }
        var data = Data()
        while let chunk = try file.read(upToCount: min(65_536, AttachmentSupport.maximumFileBytes + 1 - data.count)), !chunk.isEmpty {
            data.append(chunk)
            guard data.count <= AttachmentSupport.maximumFileBytes else { throw MightyError(L("composer.attachment.fileTooLarge")) }
        }
        return data
    }

    static func load(_ provider: NSItemProvider) async throws -> RunAttachment {
        if provider.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) {
            return try await withCheckedThrowingContinuation { continuation in
                provider.loadItem(forTypeIdentifier: UTType.fileURL.identifier, options: nil) { item, error in
                    do {
                        if let error { throw error }
                        let url: URL?
                        if let value = item as? URL { url = value }
                        else if let value = item as? Data { url = URL(dataRepresentation: value, relativeTo: nil) }
                        else if let value = item as? String { url = URL(string: value) }
                        else { url = nil }
                        guard let url else { throw MightyError(L("composer.attachment.locationUnreadable")) }
                        continuation.resume(returning: try readFile(url))
                    } catch { continuation.resume(throwing: error) }
                }
            }
        }
        let preferred = [UTType.png.identifier, UTType.jpeg.identifier, UTType.gif.identifier, UTType.webP.identifier]
        let identifier = preferred.first { provider.hasItemConformingToTypeIdentifier($0) }
            ?? provider.registeredTypeIdentifiers.first { UTType($0)?.conforms(to: .image) == true }
        guard let identifier else { throw MightyError(L("composer.attachment.nothingToPaste")) }
        let name = provider.suggestedName
        return try await withCheckedThrowingContinuation { continuation in
            // The temporary representation is valid only during this callback.
            // Read it here with the same size bound as a user-selected file.
            provider.loadFileRepresentation(forTypeIdentifier: identifier) { url, error in
                do {
                    if let error { throw error }
                    guard let url else { throw MightyError(L("composer.attachment.pastedUnreadable")) }
                    var data = try boundedFileData(url)
                    var extensionName = UTType(identifier)?.preferredFilenameExtension ?? "png"
                    let converted = !preferred.contains(identifier)
                    if converted {
                        data = try pngData(data)
                        extensionName = "png"
                    }
                    var filename = name.flatMap { $0.isEmpty ? nil : $0 } ?? L("composer.attachment.pastedImageName", ["ext": extensionName])
                    if converted { filename = (filename as NSString).deletingPathExtension + ".png" }
                    continuation.resume(returning: try AttachmentSupport.make(name: filename, data: data))
                } catch { continuation.resume(throwing: error) }
            }
        }
    }

    static func thumbnail(_ attachment: RunAttachment) -> NSImage? {
        guard attachment.mediaType.hasPrefix("image/"), let data = Data(base64Encoded: attachment.dataBase64),
              let source = CGImageSourceCreateWithData(data as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
              let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceThumbnailMaxPixelSize: 96,
                kCGImageSourceShouldCacheImmediately: true
              ] as CFDictionary) else { return nil }
        return NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))
    }

    private static func pngData(_ data: Data) throws -> Data {
        guard let source = CGImageSourceCreateWithData(data as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int,
              width > 0, height > 0, width <= 16_384, height <= 16_384, width * height <= 40_000_000,
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { throw MightyError(L("composer.attachment.pastedFormat")) }
        let output = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(output, UTType.png.identifier as CFString, 1, nil) else { throw MightyError(L("composer.attachment.pngConversion")) }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination), output.length <= AttachmentSupport.maximumFileBytes else { throw MightyError(L("composer.attachment.convertedTooLarge")) }
        return output as Data
    }
}
