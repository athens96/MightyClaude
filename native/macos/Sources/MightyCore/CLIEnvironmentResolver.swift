import Foundation

public enum CLIEnvironmentSource: String, Sendable { case loginShell, processFallback, provided }

/// Credentials stay in memory and are never included in diagnostic descriptions.
public struct CLIEnvironmentSnapshot: Sendable, CustomStringConvertible, CustomDebugStringConvertible {
    public var values: [String: String]
    public var source: CLIEnvironmentSource
    public init(values: [String: String], source: CLIEnvironmentSource) { self.values = values; self.source = source }
    public var description: String { "CLIEnvironmentSnapshot(source: \(source.rawValue))" }
    public var debugDescription: String { description }
    public var fallbackDetail: String? {
        source == .processFallback ? L("cliEnvironment.processFallback") : nil
    }
}

public actor CLIEnvironmentResolver {
    public static let shared = CLIEnvironmentResolver()
    private struct Entry {
        var id: UUID
        var created: Date
        var task: Task<CLIEnvironmentSnapshot, Never>
        var inFlight: Bool
        var started: Bool
    }
    private let baseEnvironment: [String: String]?
    private let shell: URL
    private let home: URL
    private let timeout: TimeInterval
    private let cacheTTL: TimeInterval
    private let appendedPath: [String]
    private var entries: [String: Entry] = [:]

    /// `appendedPath` nil appends `wellKnownFolders` for the real environment and
    /// nothing for a given one (tests), so a fixture never sees this Mac's tools.
    public init(baseEnvironment: [String: String]? = nil, shell: URL = URL(fileURLWithPath: "/bin/zsh"), home: URL = FileManager.default.homeDirectoryForCurrentUser, timeout: TimeInterval = 4, cacheTTL: TimeInterval = 10, appendedPath: [String]? = nil) {
        self.baseEnvironment = baseEnvironment; self.shell = shell; self.home = home
        self.timeout = timeout; self.cacheTTL = cacheTTL
        self.appendedPath = appendedPath ?? (baseEnvironment == nil ? Self.wellKnownFolders(home: home) : [])
    }

    /// Where the official installers put the CLIs (Claude, Codex, Antigravity, uv:
    /// ~/.local/bin; Bun: ~/.bun/bin; Homebrew and its formulae: /opt/homebrew/bin
    /// or /usr/local/bin). A shell profile that does not list them yet still finds
    /// them: they go at the end of the login shell's PATH, after everything the
    /// profile chose. The setup script appends the same list (`SetupScript.preamble`).
    public static func wellKnownFolders(home: URL) -> [String] {
        [home.path + "/.local/bin", home.path + "/.npm-global/bin", home.path + "/.bun/bin", "/opt/homebrew/bin", "/usr/local/bin"]
    }

    /// `path` with each of `folders` it does not hold yet added at the end.
    public static func appending(_ folders: [String], to path: String) -> String {
        var entries = path.split(separator: ":").map(String.init)
        for folder in folders where !entries.contains(folder) { entries.append(folder) }
        return entries.joined(separator: ":")
    }

    public func resolve(workspacePath: String? = nil, forceRefresh: Bool = false) async -> CLIEnvironmentSnapshot {
        let cwd = workspacePath.map { URL(fileURLWithPath: $0, isDirectory: true) } ?? home
        let key = cwd.standardizedFileURL.path
        let base = baseEnvironment ?? ProviderService.runtimeEnvironment()
        let fallback = CLIEnvironmentSnapshot(values: base, source: .processFallback)
        let entry: Entry
        if let cached = entries[key], forceRefresh ? cached.inFlight && !cached.started : cached.inFlight || Date().timeIntervalSince(cached.created) < cacheTTL {
            entry = cached
        } else {
            entries[key]?.task.cancel()
            if entries[key] == nil, entries.count >= 64, let oldest = entries.min(by: { $0.value.created < $1.value.created }) {
                entries.removeValue(forKey: oldest.key)?.task.cancel()
            }
            let shell = shell, timeout = timeout, appended = appendedPath
            let id = UUID()
            let task = Task<CLIEnvironmentSnapshot, Never> {
                // Coalesce requests from one UI refresh before reading any
                // credentials. Once capture starts, a force request always
                // supersedes it so an authentication change cannot join stale work.
                do { try await Task.sleep(nanoseconds: 50_000_000) } catch { return fallback }
                guard self.entries[key]?.id == id, !Task.isCancelled else { return fallback }
                self.entries[key]?.started = true
                let nonce = UUID().uuidString
                let start = "MIGHTY_ENV_" + nonce, end = "MIGHTY_END_" + nonce
                // Only frame markers occur in argv. Shell startup chatter is
                // excluded from parsing; neither stream is logged or persisted.
                let command = "printf '\\000\(start)\\000'; /usr/bin/env -0; printf '\\000\(end)\\000'"
                guard let result = try? await ProcessCapture.run(executable: shell, arguments: ["-ilc", command], environment: base, cwd: cwd, timeout: timeout, maximumBytes: 1_048_576),
                      result.exitCode == 0, !Task.isCancelled,
                      var values = Self.parse(result.stdout, start: start, end: end) else { return fallback }
                values["PATH"] = Self.appending(appended, to: values["PATH"] ?? "")
                return CLIEnvironmentSnapshot(values: values, source: .loginShell)
            }
            entry = Entry(id: id, created: Date(), task: task, inFlight: true, started: false)
            entries[key] = entry
        }
        let result = await entry.task.value
        guard entries[key]?.id == entry.id else { return await resolve(workspacePath: workspacePath) }
        entries[key]?.inFlight = false
        return result
    }

    public func invalidate(workspacePath: String? = nil) {
        let key = (workspacePath.map { URL(fileURLWithPath: $0, isDirectory: true) } ?? home).standardizedFileURL.path
        entries.removeValue(forKey: key)?.task.cancel()
    }

    static func parse(_ data: Data, start: String, end: String) -> [String: String]? {
        let opening = Data(([0] + Array(start.utf8) + [0]))
        let closing = Data(([0] + Array(end.utf8) + [0]))
        guard let from = data.range(of: opening), let to = data.range(of: closing, in: from.upperBound..<data.endIndex) else { return nil }
        let payload = data[from.upperBound..<to.lowerBound]
        var environment: [String: String] = [:]
        for row in payload.split(separator: 0) {
            guard let separator = row.firstIndex(of: 61), separator != row.startIndex,
                  let key = String(data: row[..<separator], encoding: .utf8),
                  let value = String(data: row[row.index(after: separator)...], encoding: .utf8),
                  !key.contains("="), environment[key] == nil else { return nil }
            environment[key] = value
        }
        // A truncated/empty capture must not silently strip the runtime PATH.
        guard environment["PATH"]?.isEmpty == false else { return nil }
        return environment
    }
}
