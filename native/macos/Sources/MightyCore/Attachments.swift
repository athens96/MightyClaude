import Foundation
import Darwin

public struct RunAttachment: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var name: String
    public var mediaType: String
    public var dataBase64: String
    public init(id: String = UUID().uuidString, name: String, mediaType: String, dataBase64: String) {
        self.id = id; self.name = name; self.mediaType = mediaType; self.dataBase64 = dataBase64
    }
}

public enum AttachmentSupport {
    /// The line that points the model at one attached copy. Fixed English on every
    /// path, whatever the app language: it is prompt text for the CLI, not copy.
    public static func reference(_ file: PreparedAttachment) -> String {
        let name = String(data: try! JSONEncoder().encode(file.attachment.name), encoding: .utf8)!
        let path = String(data: try! JSONEncoder().encode(file.url.path), encoding: .utf8)!
        return "Attached file \(name): \(path) (read this copy as needed)."
    }
    public static let maximumCount = 8
    public static let maximumFileBytes = 5 * 1024 * 1024
    public static let maximumTotalBytes = 8 * 1024 * 1024
    public static let maximumRequestBytes = 12 * 1024 * 1024
    public static let mediaTypes = ["image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf", "text/plain", "application/octet-stream"]

    public static func make(name: String, data: Data) throws -> RunAttachment {
        guard data.count <= maximumFileBytes else { throw MightyError(L("attachments.error.fileTooLarge")) }
        let basename = name.replacingOccurrences(of: "\\", with: "/").split(separator: "/").last.map(String.init) ?? "attachment"
        let clean = String(String.UnicodeScalarView(basename.unicodeScalars.map { $0.properties.generalCategory == .control ? UnicodeScalar(95)! : $0 })).trimmingCharacters(in: .whitespacesAndNewlines)
        var safeName = String((clean.isEmpty || clean == "." || clean == ".." ? "attachment" : clean).prefix(180))
        while safeName.utf16.count > 180 { safeName.removeLast() }
        let attachment = RunAttachment(name: safeName, mediaType: sniff(data), dataBase64: data.base64EncodedString())
        try validate([attachment]); return attachment
    }
    public static func validate(_ attachments: [RunAttachment]) throws {
        guard attachments.count <= maximumCount, Set(attachments.map(\.id)).count == attachments.count else { throw MightyError(L("attachments.error.tooMany")) }
        var total = 0
        for item in attachments {
            guard CoreValidation.identifier(item.id), !item.name.isEmpty, item.name.utf16.count <= 180, item.name != ".", item.name != "..", !item.name.contains("/"), !item.name.contains("\\"), !item.name.unicodeScalars.contains(where: { $0.properties.generalCategory == .control }), mediaTypes.contains(item.mediaType), item.dataBase64.utf8.count <= ((maximumFileBytes + 2) / 3) * 4, let bytes = Data(base64Encoded: item.dataBase64), bytes.count <= maximumFileBytes, bytes.base64EncodedString() == item.dataBase64 else { throw MightyError(L("attachments.error.invalid")) }
            guard sniff(bytes) == item.mediaType else { throw MightyError(L("attachments.error.typeMismatch")) }
            total += bytes.count
            guard total <= maximumTotalBytes else { throw MightyError(L("attachments.error.totalTooLarge")) }
        }
    }
    public static func sniff(_ data: Data) -> String {
        let prefix = Array(data.prefix(12))
        if prefix.starts(with: [137, 80, 78, 71, 13, 10, 26, 10]) { return "image/png" }
        if prefix.starts(with: [255, 216, 255]) { return "image/jpeg" }
        if prefix.starts(with: Array("GIF87a".utf8)) || prefix.starts(with: Array("GIF89a".utf8)) { return "image/gif" }
        if prefix.count >= 12, prefix[0..<4].elementsEqual("RIFF".utf8), prefix[8..<12].elementsEqual("WEBP".utf8) { return "image/webp" }
        if prefix.starts(with: Array("%PDF-".utf8)) { return "application/pdf" }
        if let text = String(data: data, encoding: .utf8), !text.unicodeScalars.contains(where: { $0.properties.generalCategory == .control && ![9, 10, 13].contains($0.value) }) { return "text/plain" }
        return "application/octet-stream"
    }
}

public struct PreparedAttachment: Sendable {
    public let attachment: RunAttachment
    public let url: URL
}

/// Owns copies only. A random, private directory and exclusive no-follow writes
/// prevent caller-controlled names from selecting or replacing filesystem paths.
public final class AttachmentPreparation {
    public private(set) var directory: URL?
    public private(set) var files: [PreparedAttachment] = []
    public init(_ attachments: [RunAttachment], parent: URL = FileManager.default.temporaryDirectory) throws {
        try AttachmentSupport.validate(attachments)
        guard !attachments.isEmpty else { return }
        var template = Array(parent.appendingPathComponent("mighty-attachments-XXXXXX").path.utf8CString)
        guard let result = mkdtemp(&template) else { throw MightyError(L("attachments.error.tempFolderCreate")) }
        let root = URL(fileURLWithPath: String(cString: result), isDirectory: true)
        directory = root
        let directoryFD = open(root.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW)
        guard directoryFD >= 0 else { cleanup(); throw MightyError(L("attachments.error.tempFolderOpen")) }
        defer { close(directoryFD) }
        do {
            guard fchmod(directoryFD, 0o700) == 0 else { throw MightyError(L("attachments.error.folderPermission")) }
            for (index, item) in attachments.enumerated() {
                let fixed = ["image/png": "png", "image/jpeg": "jpg", "image/gif": "gif", "image/webp": "webp", "application/pdf": "pdf", "text/plain": "txt", "application/octet-stream": "bin"]
                let suffix = URL(fileURLWithPath: item.name).pathExtension.lowercased()
                let generic = item.mediaType == "text/plain" || item.mediaType == "application/octet-stream"
                let ext = generic && suffix.range(of: "^[a-z0-9]{1,12}$", options: .regularExpression) != nil ? suffix : (fixed[item.mediaType] ?? "bin")
                let name = "\(index)-\(UUID().uuidString).\(ext)"
                let fd = openat(directoryFD, name, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, 0o600)
                guard fd >= 0 else { throw MightyError(L("attachments.error.copyCreate")) }
                let bytes = Data(base64Encoded: item.dataBase64)!
                let written: Bool = bytes.withUnsafeBytes { buffer in
                    guard let base = buffer.baseAddress else { return true }
                    var offset = 0
                    while offset < bytes.count {
                        let result = Darwin.write(fd, base.advanced(by: offset), bytes.count - offset)
                        if result < 0 && errno == EINTR { continue }
                        guard result > 0 else { return false }
                        offset += result
                    }
                    return true
                }
                close(fd)
                guard written else { throw MightyError(L("attachments.error.copyWrite")) }
                files.append(PreparedAttachment(attachment: item, url: root.appendingPathComponent(name)))
            }
        } catch { cleanup(); throw error }
    }
    public func cleanup() {
        if let directory { try? FileManager.default.removeItem(at: directory) }
        directory = nil; files = []
    }
    deinit { cleanup() }
}

public struct ProviderInput {
    public let arguments: [String]
    public let standardInput: Data
    /// One stdin frame for a Claude run started with stream-json input. The
    /// first frame carries the prompt; later frames are mid-turn follow-ups.
    public static func claudeUserMessage(_ content: Any) throws -> Data {
        var data = try JSONSerialization.data(withJSONObject: ["type": "user", "message": ["role": "user", "content": content], "parent_tool_use_id": NSNull()], options: [.sortedKeys, .withoutEscapingSlashes])
        data.append(10); return data
    }
    public static func prepare(_ request: StartRunRequest, pluginDirectory: URL, attachments: AttachmentPreparation, allowPermissionPrompts: Bool = false, paneMCPBinding: PaneMCPBinding? = nil, codexHome: URL? = nil) throws -> ProviderInput {
        guard request.provider != "codex" || request.settings.permissionMode != "onRequest" else {
            throw MightyError(L("attachments.error.codexOnRequest"))
        }
        var arguments = try ProviderService.arguments(request, pluginDirectory: pluginDirectory, allowPermissionPrompts: allowPermissionPrompts, paneMCPBinding: paneMCPBinding, codexHome: codexHome)
        guard !request.attachments.isEmpty else {
            if request.provider == "claude", allowPermissionPrompts {
                return ProviderInput(arguments: arguments, standardInput: try claudeUserMessage(request.input))
            }
            return ProviderInput(arguments: arguments, standardInput: Data(request.input.utf8))
        }
        guard attachments.files.map(\.attachment) == request.attachments, let directory = attachments.directory else { throw MightyError(L("attachments.error.notPrepared")) }
        let prompt = request.input.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? L("attachments.prompt.default") : request.input
        switch request.provider {
        case "claude":
            if !allowPermissionPrompts { arguments += ["--input-format", "stream-json"] }
            let generic = attachments.files.filter { !$0.attachment.mediaType.hasPrefix("image/") && $0.attachment.mediaType != "application/pdf" }
            if !generic.isEmpty { arguments += ["--add-dir", directory.path] }
            var content: [[String: Any]] = [["type": "text", "text": ([prompt] + generic.map(AttachmentSupport.reference)).joined(separator: "\n\n")]]
            for file in attachments.files where !generic.contains(where: { $0.attachment.id == file.attachment.id }) {
                content.append(["type": file.attachment.mediaType == "application/pdf" ? "document" : "image", "source": ["type": "base64", "media_type": file.attachment.mediaType, "data": file.attachment.dataBase64]])
            }
            return ProviderInput(arguments: arguments, standardInput: try claudeUserMessage(content))
        case "codex":
            let images = attachments.files.filter { $0.attachment.mediaType.hasPrefix("image/") }
            let extra = images.flatMap { ["--image", $0.url.path] }
            arguments.insert(contentsOf: extra, at: max(0, arguments.count - 1))
            let references = attachments.files.filter { !$0.attachment.mediaType.hasPrefix("image/") }.map(AttachmentSupport.reference)
            return ProviderInput(arguments: arguments, standardInput: Data(([prompt] + references).joined(separator: "\n\n").utf8))
        case "gemini":
            arguments += ["--include-directories", directory.path]
            let references = attachments.files.map { file -> String in
                // Gemini's POSIX parser unescapes backslashes; quotes are only
                // stripped on Windows. Escape delimiters in this Mac path.
                let delimiters = CharacterSet(charactersIn: " \t\n\r,;!?()[]{}\"'\\|*?$`#&<>~")
                let path = file.url.path.unicodeScalars.map { delimiters.contains($0) ? "\\\($0)" : String($0) }.joined()
                return "@\(path)\n\(AttachmentSupport.reference(file))"
            }
            return ProviderInput(arguments: arguments, standardInput: Data(([prompt] + references).joined(separator: "\n\n").utf8))
        default: throw MightyError(L("attachments.error.unsupportedProvider"))
        }
    }
}
