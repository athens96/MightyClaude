import AppKit
import ImageIO
import MightyCore

struct CompanionPet: Identifiable {
    let id: String
    let name: String
    let pixelWidth: Int
    let pixelHeight: Int
    let source: URL
    private final class FrameSet: NSObject {
        let rows: [[NSImage]]
        init(_ rows: [[NSImage]]) { self.rows = rows }
    }
    private static let frameCache: NSCache<NSString, FrameSet> = {
        let cache = NSCache<NSString, FrameSet>()
        cache.countLimit = 2
        cache.totalCostLimit = 32 * 1024 * 1024
        return cache
    }()
    static func clearCache() { frameCache.removeAllObjects() }
    var frames: [[NSImage]] {
        let key = source.path as NSString
        if let cached = Self.frameCache.object(forKey: key) { return cached.rows }
        guard let input = CGImageSourceCreateWithURL(source as CFURL, nil),
              let sheet = CGImageSourceCreateImageAtIndex(input, 0, nil),
              sheet.width == pixelWidth, sheet.height == pixelHeight else { return Array(repeating: [], count: 9) }
        let rows = Self.frameCounts.enumerated().map { row, count in
            (0..<count).compactMap { column -> NSImage? in
                guard let image = sheet.cropping(to: CGRect(x: column * 192, y: row * 208, width: 192, height: 208)) else { return nil }
                return NSImage(cgImage: image, size: NSSize(width: 192, height: 208))
            }
        }
        Self.frameCache.setObject(FrameSet(rows), forKey: key, cost: pixelWidth * pixelHeight * 4)
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
    func frame(row: Int, elapsed: TimeInterval, reducedMotion: Bool) -> NSImage? {
        let row = min(max(0, row), 8)
        guard !frames[row].isEmpty else { return nil }
        if reducedMotion { return frames[row][0] }
        let timing = Self.durations[row]
        var phase = max(0, elapsed).truncatingRemainder(dividingBy: timing.reduce(0, +))
        for (index, duration) in timing.enumerated() {
            if phase < duration { return frames[row][min(index, frames[row].count - 1)] }
            phase -= duration
        }
        return frames[row][0]
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
            guard size > 0, size <= 65_536 else { throw MightyError("펫 설명 파일은 64 KiB 이하여야 합니다.") }
            guard let json = try JSONSerialization.jsonObject(with: Data(contentsOf: manifest)) as? [String: Any],
                  let path = json["spritesheetPath"] as? String, !path.isEmpty, !path.hasPrefix("/") else { throw MightyError("pet.json에 올바른 spritesheetPath가 필요합니다.") }
            let root = manifest.deletingLastPathComponent().resolvingSymlinksInPath().standardizedFileURL
            source = root.appendingPathComponent(path).resolvingSymlinksInPath().standardizedFileURL
            guard source.path.hasPrefix(root.path + "/") else { throw MightyError("펫 이미지는 선택한 펫 폴더 안에 있어야 합니다.") }
            name = String((json["displayName"] as? String ?? name).prefix(80))
            declaredVersion = json["spriteVersionNumber"] as? Int
        }
        guard ["png", "webp"].contains(source.pathExtension.lowercased()) else { throw MightyError("PNG 또는 WebP 펫 이미지를 선택하세요.") }
        let size = try source.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
        guard size.isRegularFile == true, let bytes = size.fileSize, bytes > 0, bytes <= 20 * 1024 * 1024 else { throw MightyError("펫 이미지는 20 MiB 이하의 일반 파일이어야 합니다.") }
        let data = try Data(contentsOf: source, options: .mappedIfSafe)
        guard let imageSource = CGImageSourceCreateWithData(data as CFData, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(imageSource, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int,
              width == 1536, [1872, 2288, 2496].contains(height) else { throw MightyError("Codex 펫 크기는 1536×1872, 1536×2288 또는 1536×2496이어야 합니다.") }
        if let declaredVersion {
            guard [1: 1872, 2: 2288, 3: 2496][declaredVersion] == height else { throw MightyError("펫 버전과 이미지 크기가 일치하지 않습니다.") }
        }
        guard let sheet = CGImageSourceCreateImageAtIndex(imageSource, 0, nil),
              [.first, .last, .premultipliedFirst, .premultipliedLast].contains(sheet.alphaInfo) else { throw MightyError("투명 배경이 있는 펫 이미지가 필요합니다.") }
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
