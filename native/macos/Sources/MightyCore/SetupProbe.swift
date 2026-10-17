import Foundation

/// What a probe found. The probe, never an exit code, decides whether an item is there.
public enum SetupProbeResult: Sendable, Equatable {
    case installed(version: String?)
    case missing
    /// There, but below the catalog's minimum (`version` nil when it would not say).
    case outdated(version: String?, minimum: String)

    public var isInstalled: Bool { if case .installed = self { return true }; return false }
    /// The text `--version` printed, when the item is there at all.
    public var version: String? {
        switch self {
        case .installed(let version), .outdated(let version, _): return version
        case .missing: return nil
        }
    }
}

/// Reads whether each catalog item is on this Mac, fresh on every call: the
/// login shell's PATH it is given, `--version`, the Claude plugin registry, the
/// skills folder, `xcode-select -p` and /Applications. Nothing is installed or
/// changed. Paths and the version runner are injected so tests use fixtures.
public struct SetupProber: Sendable {
    public var environment: [String: String]
    public var home: URL
    public var versionTimeout: TimeInterval
    public var commandLineToolsPath: @Sendable () async -> String?

    public init(environment: [String: String], home: URL = FileManager.default.homeDirectoryForCurrentUser, versionTimeout: TimeInterval = 4,
                commandLineToolsPath: @escaping @Sendable () async -> String? = SetupProber.xcodeSelectPath) {
        self.environment = environment; self.home = home; self.versionTimeout = versionTimeout; self.commandLineToolsPath = commandLineToolsPath
    }

    public func probe(_ probe: SetupProbe) async -> SetupProbeResult {
        switch probe {
        case .executable(let name, let minimum):
            guard let executable = ToolkitProcess.resolveExecutable(name, environment: environment) else { return .missing }
            let version = await versionText(executable)
            guard let minimum else { return .installed(version: version) }
            guard let version, let found = Self.versionNumbers(version), let required = Self.versionNumbers(minimum) else { return .outdated(version: version, minimum: minimum) }
            return Self.isAtLeast(found, required) ? .installed(version: version) : .outdated(version: version, minimum: minimum)
        case .claudePlugin(let prefix):
            return ToolkitProbe.userScopePluginInstalled(home: home, matching: { $0.hasPrefix(prefix) }) ? .installed(version: nil) : .missing
        case .skill(let name):
            let skill = home.appendingPathComponent(".claude/skills/\(name)/SKILL.md")
            return FileManager.default.fileExists(atPath: skill.path) ? .installed(version: nil) : .missing
        case .commandLineTools:
            guard let path = await commandLineToolsPath() else { return .missing }
            var directory: ObjCBool = false
            return FileManager.default.fileExists(atPath: path, isDirectory: &directory) && directory.boolValue ? .installed(version: nil) : .missing
        case .appBundle(let path):
            var directory: ObjCBool = false
            return FileManager.default.fileExists(atPath: path, isDirectory: &directory) && directory.boolValue ? .installed(version: nil) : .missing
        }
    }

    /// Every item that has an entry for `platform`, probed one after another.
    public func probeAll(_ catalog: SetupCatalog, platform: SetupPlatform) async -> [String: SetupProbeResult] {
        var results: [String: SetupProbeResult] = [:]
        for item in catalog.items {
            guard let entry = item.platforms[platform], !Task.isCancelled else { continue }
            results[item.id] = await probe(entry.probe)
        }
        return results
    }

    /// `--version`'s first line, trimmed as ProviderService keeps it, or nil.
    private func versionText(_ executable: URL) async -> String? {
        guard let result = try? await ProcessCapture.run(executable: executable, arguments: ["--version"], environment: environment, timeout: versionTimeout, maximumBytes: 16_384),
              result.exitCode == 0 else { return nil }
        let text = String(String(decoding: result.stdout, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines).prefix(160))
        return text.isEmpty ? nil : text
    }

    /// The first `1.2.3` (or `1.2`, `1`) in a version line; a prerelease suffix is ignored.
    public static func versionNumbers(_ text: String) -> [Int]? {
        guard let range = text.range(of: "[0-9]+(\\.[0-9]+){0,2}", options: .regularExpression) else { return nil }
        let numbers = text[range].split(separator: ".").compactMap { Int($0) }
        return numbers.isEmpty ? nil : numbers
    }

    public static func isAtLeast(_ found: [Int], _ required: [Int]) -> Bool {
        let width = max(found.count, required.count)
        let a = found + Array(repeating: 0, count: width - found.count), b = required + Array(repeating: 0, count: width - required.count)
        return !a.lexicographicallyPrecedes(b)
    }

    /// `xcode-select -p`, read only. A Mac without the tools prints nothing and fails.
    public static let xcodeSelectPath: @Sendable () async -> String? = {
        guard let result = try? await ProcessCapture.run(executable: URL(fileURLWithPath: "/usr/bin/xcode-select"), arguments: ["-p"], timeout: 4, maximumBytes: 4096),
              result.exitCode == 0 else { return nil }
        let path = String(decoding: result.stdout, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        return path.isEmpty ? nil : path
    }
}

/// What a row shows.
public enum SetupRowState: String, Sendable, Equatable {
    case checking, installed, missing, outdated, installing, failed

    /// `inRun`: the item is part of the install that is running, or was part of
    /// the last one. `runFinished`: that install's script has ended.
    /// `finishesOutside`: its command only starts the install, which a system
    /// window or a download finishes, so it stays "installing" until a probe sees it.
    public static func derive(_ result: SetupProbeResult?, inRun: Bool, runFinished: Bool, finishesOutside: Bool) -> SetupRowState {
        guard let result else { return .checking }
        switch result {
        case .installed: return .installed
        case .outdated: return inRun && !runFinished ? .installing : .outdated
        case .missing:
            guard inRun else { return .missing }
            return !runFinished || finishesOutside ? .installing : .failed
        }
    }
}

/// Whether the screen opens by itself at launch.
public enum SetupFirstRun {
    /// True when no agent CLI can run, or a required item is not ready, unless the
    /// user chose never to see it again. Results still missing count as not known yet.
    public static func shouldPresent(catalog: SetupCatalog, results: [String: SetupProbeResult], platform: SetupPlatform, neverShow: Bool) -> Bool {
        guard !neverShow else { return false }
        let items = catalog.items.filter { $0.platforms[platform] != nil }
        guard items.allSatisfy({ results[$0.id] != nil }) else { return false }
        let agents = items.filter { $0.group == .agent }
        if !agents.isEmpty, !agents.contains(where: { results[$0.id]?.isInstalled == true }) { return true }
        return items.contains { $0.required && results[$0.id]?.isInstalled != true }
    }

    /// The welcome screen's button shows while this holds (the same rule, without "never show").
    public static func needsAttention(catalog: SetupCatalog, results: [String: SetupProbeResult], platform: SetupPlatform) -> Bool {
        shouldPresent(catalog: catalog, results: results, platform: platform, neverShow: false)
    }
}

/// Whether the app's cached CLI runtime disagrees with what the probe just
/// saw, so the runtime (and with it every blocked pane) must be read again.
public enum SetupRuntimeSync {
    public static func isStale(runtime: [ProviderRuntime], catalog: SetupCatalog, results: [String: SetupProbeResult]) -> Bool {
        for item in catalog.items {
            guard let provider = item.provider, let result = results[item.id] else { continue }
            let cached = runtime.first { $0.id == provider }
            if result.version != cached?.version { return true }
        }
        return false
    }
}
