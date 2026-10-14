import AppKit
import ImageIO
import MightyCore

struct CompanionPet: Identifiable {
    let id: String
    let name: String
    let pixelWidth: Int
    let pixelHeight: Int
    let source: URL
    /// Decoded once per pet and kept for the app's life (at most four pets), so an
    /// animation tick never decodes the sheet again, not even a broken or mis-sized one
    /// (kept as nine empty rows); `clearCache` drops them on reload.
    @MainActor private static var frameStore: [String: [[NSImage]]] = [:]
    @MainActor private static var frameStoreOrder: [String] = []
    @MainActor static func clearCache() { frameStore.removeAll(); frameStoreOrder.removeAll() }
    @MainActor var frames: [[NSImage]] {
        let key = source.path
        if let stored = Self.frameStore[key] { return stored }
        let rows: [[NSImage]]
        if let sheet = PetSpriteFrames.decode(source), sheet.width == pixelWidth, sheet.height == pixelHeight {
            rows = PetSpriteFrames.slice(sheet, frameCounts: Self.frameCounts, cell: CGSize(width: 192, height: 208))
                .map { row in row.map { NSImage(cgImage: $0, size: NSSize(width: 192, height: 208)) } }
        } else { rows = Array(repeating: [], count: 9) }
        Self.frameStore[key] = rows
        Self.frameStoreOrder.append(key)
        while Self.frameStoreOrder.count > 4 { Self.frameStore[Self.frameStoreOrder.removeFirst()] = nil }
        return rows
    }
    static let frameCounts = [6, 8, 8, 4, 5, 8, 6, 6, 6]
    static let durations: [[Double]] = [
        [0.28, 0.11, 0.11, 0.14, 0.14, 0.32],
        Array(repeating: 0.12, count: 7) + [0.22], Array(repeating: 0.12, count: 7) + [0.22],
        [0.14, 0.14, 0.14, 0.28], [0.14, 0.14, 0.14, 0.14, 0.28],
        Array(repeating: 0.14, count: 7) + [0.24], Array(repeating: 0.15, count: 5) + [0.26],
        Array(repeating: 0.12, count: 5) + [0.22], Array(repeating: 0.15, count: 5) + [0.28],
    ]
    @MainActor func frame(row: Int, elapsed: TimeInterval, reducedMotion: Bool) -> NSImage? {
        let row = min(max(0, row), 8)
        let images = frames[row]
        guard !images.isEmpty else { return nil }
        if reducedMotion { return images[0] }
        let timing = Self.durations[row]
        var phase = max(0, elapsed).truncatingRemainder(dividingBy: timing.reduce(0, +))
        for (index, duration) in timing.enumerated() {
            if phase < duration { return images[min(index, images.count - 1)] }
            phase -= duration
        }
        return images[0]
    }
    static func loadAvailable(dataDirectory: URL?) -> [CompanionPet] {
        var result: [CompanionPet] = []
        let cwd = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        let builtins = [Bundle.main.resourceURL?.appendingPathComponent("pets/mighty-raccoon"),
            cwd.appendingPathComponent("assets/pets/mighty-raccoon"),
            cwd.deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("assets/pets/mighty-raccoon")].compactMap { $0 }
        for url in builtins {
            if let pet = try? load(from: url, id: "mighty-raccoon") { result.append(pet); break }
        }
        if !result.contains(where: { $0.id == "mighty-raccoon" }) {
            ResourceHealthChecker.logWarning(ResourceHealthChecker.checkDefaultPet())
        }
        let codexHome = ProcessInfo.processInfo.environment["CODEX_HOME"].map { URL(fileURLWithPath: $0) }
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex")
        for root in [dataDirectory?.appendingPathComponent("pets"), codexHome.appendingPathComponent("pets")].compactMap({ $0 }) {
            let directories = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles])) ?? []
            for url in directories.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }).prefix(40) {
                let id = root == codexHome.appendingPathComponent("pets") ? "codex:\(url.lastPathComponent)" : "local:\(url.lastPathComponent)"
                if let pet = try? load(from: url, id: id) { result.append(pet) }
            }
        }
        return result
    }
    static func load(from selected: URL, id: String) throws -> CompanionPet {
        var directoryFlag: ObjCBool = false
        FileManager.default.fileExists(atPath: selected.path, isDirectory: &directoryFlag)
        let manifest = directoryFlag.boolValue ? selected.appendingPathComponent("pet.json") : selected
        var source = selected
        var name = selected.deletingPathExtension().lastPathComponent
        var declaredVersion: Int?
        if manifest.pathExtension.lowercased() == "json" {
            let size = try manifest.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
            guard size > 0, size <= 65_536 else { throw MightyError(L("companion.error.descriptionTooLarge")) }
            guard let json = try JSONSerialization.jsonObject(with: Data(contentsOf: manifest)) as? [String: Any],
                  let path = json["spritesheetPath"] as? String, !path.isEmpty, !path.hasPrefix("/") else { throw MightyError(L("companion.error.spritesheetPath")) }
            let root = manifest.deletingLastPathComponent().resolvingSymlinksInPath().standardizedFileURL
            source = root.appendingPathComponent(path).resolvingSymlinksInPath().standardizedFileURL
            guard source.path.hasPrefix(root.path + "/") else { throw MightyError(L("companion.error.outsideFolder")) }
            name = String((json["displayName"] as? String ?? name).prefix(80))
            declaredVersion = json["spriteVersionNumber"] as? Int
        }
        guard ["png", "webp"].contains(source.pathExtension.lowercased()) else { throw MightyError(L("companion.error.format")) }
        let size = try source.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
        guard size.isRegularFile == true, let bytes = size.fileSize, bytes > 0, bytes <= 20 * 1024 * 1024 else { throw MightyError(L("companion.error.fileTooLarge")) }
        let data = try Data(contentsOf: source, options: .mappedIfSafe)
        guard let imageSource = CGImageSourceCreateWithData(data as CFData, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(imageSource, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int,
              width == 1536, [1872, 2288, 2496].contains(height) else { throw MightyError(L("companion.error.size")) }
        if let declaredVersion {
            guard [1: 1872, 2: 2288, 3: 2496][declaredVersion] == height else { throw MightyError(L("companion.error.versionMismatch")) }
        }
        guard let sheet = CGImageSourceCreateImageAtIndex(imageSource, 0, nil),
              [.first, .last, .premultipliedFirst, .premultipliedLast].contains(sheet.alphaInfo) else { throw MightyError(L("companion.error.transparency")) }
        return CompanionPet(id: id, name: name, pixelWidth: width, pixelHeight: height, source: source)
    }
    static func install(from source: URL, into directory: URL) throws -> CompanionPet {
        let id = UUID().uuidString.lowercased()
        let pet = try load(from: source, id: "local:\(id)")
        let destination = directory.appendingPathComponent(id)
        try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
        do {
            let filename = "spritesheet.\(pet.source.pathExtension.lowercased())"
            try FileManager.default.copyItem(at: pet.source, to: destination.appendingPathComponent(filename))
            let manifest: [String: Any] = ["id": id, "displayName": pet.name, "spritesheetPath": filename]
            try JSONSerialization.data(withJSONObject: manifest, options: [.sortedKeys]).write(to: destination.appendingPathComponent("pet.json"), options: .atomic)
            return try load(from: destination, id: "local:\(id)")
        } catch { try? FileManager.default.removeItem(at: destination); throw error }
    }
}
