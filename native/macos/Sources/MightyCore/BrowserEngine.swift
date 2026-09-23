import Foundation

public struct BrowserEngineLock: Codable, Sendable, Equatable {
    public struct Entry: Codable, Sendable, Equatable {
        public var version: String
        public var url: String
        public var sha256: String
        public init(version: String, url: String, sha256: String) {
            self.version = version; self.url = url; self.sha256 = sha256
        }
    }
    public var arch: String
    public var cef: Entry
    public var node: Entry
    public init(arch: String, cef: Entry, node: Entry) {
        self.arch = arch; self.cef = cef; self.node = node
    }

    public static func load(from url: URL) throws -> BrowserEngineLock {
        let data = try Data(contentsOf: url)
        return try JSONDecoder().decode(BrowserEngineLock.self, from: data)
    }

    public static func bundledLock() -> BrowserEngineLock? {
        let candidates: [URL] = [
            URL(fileURLWithPath: "native/macos/BrowserEngine.lock"),
            Bundle.main.bundleURL.appendingPathComponent("Contents/Resources/BrowserEngine.lock"),
        ]
        for url in candidates {
            if let lock = try? load(from: url) { return lock }
        }
        return nil
    }
}

public enum BrowserEngineStatus: Sendable {
    case available(frameworkPath: URL)
    case missing(reason: String)
}

public struct BrowserEngineLocator: Sendable {
    public static func locate() -> BrowserEngineStatus {
        let frameworkName = "Chromium Embedded Framework.framework"
        let candidates: [URL] = [
            Bundle.main.bundleURL.appendingPathComponent("Contents/Frameworks/\(frameworkName)"),
        ]
        for url in candidates {
            if FileManager.default.fileExists(atPath: url.path) {
                return .available(frameworkPath: url)
            }
        }
        return .missing(reason: L("browser.engine.missing"))
    }

    public static func reportMissing() -> String { L("browser.engine.missing") }
}

public struct BrowserProfileSupport: Sendable {
    public static func profilePath(workspaceProfileKey: String) -> URL {
        guard let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first else {
            return URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("MightyClaude/browser-profiles/\(workspaceProfileKey)")
        }
        return support.appendingPathComponent("MightyClaude/browser-profiles/\(workspaceProfileKey)")
    }
}

public protocol BrowserEngine: AnyObject, Sendable {
    var isAvailable: Bool { get }
    var canGoBack: Bool { get }
    var canGoForward: Bool { get }
    func loadURL(_ url: URL)
    func goBack()
    func goForward()
    func reload()
}
