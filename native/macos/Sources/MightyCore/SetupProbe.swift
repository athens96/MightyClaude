import Foundation

/// Why an item that is there cannot be used yet.
public enum SetupAttention: String, Sendable, Equatable {
    /// The selected Xcode's license has not been accepted, so git refuses to run.
    case xcodeLicense
    /// The command line tools are selected, but git does not answer.
    case gitUnusable
}

/// What a probe found. The probe, never an exit code, decides whether an item is there.
public enum SetupProbeResult: Sendable, Equatable {
    case installed(version: String?)
    case missing
    /// There, but below the catalog's minimum (or a prerelease of it).
    case outdated(version: String?, minimum: String)
    /// There, but no copy on the PATH said its version, so the minimum is unchecked.
    case unverified(minimum: String)
    /// There, but not usable yet.
    case needsAttention(SetupAttention)

    /// Counts as there: installed, or there with its version unread.
    public var isInstalled: Bool {
        switch self {
        case .installed, .unverified: return true
        case .missing, .outdated, .needsAttention: return false
        }
    }
    /// The text `--version` printed, when the item is there at all.
    public var version: String? {
        switch self {
        case .installed(let version), .outdated(let version, _): return version
        case .missing, .unverified, .needsAttention: return nil
        }
    }
}

/// Reads whether each catalog item is on this Mac, fresh on every call: the
/// login shell's PATH it is given, `--version`, the Claude plugin registry, the
/// skills folder, `xcode-select -p` and git, and the applications folders.
/// Nothing is installed or changed. Paths and the command runners are injected
/// so tests use fixtures.
public struct SetupProber: Sendable {
    /// Written in a skill's folder by the setup screen: `installing` when it placed
    /// the folder, `done` once the skill's own setup script succeeded.
    public static let skillInstallingMarker = ".mighty-setup"
    public static let skillDoneMarker = ".mighty-setup-ok"

    public var environment: [String: String]
    public var home: URL
    public var versionTimeout: TimeInterval
    public var commandLineToolsPath: @Sendable () async -> String?
    /// `git --version`'s exit code and output, or nil when it did not end in time.
    public var gitVersion: @Sendable () async -> (exitCode: Int32, output: String)?

    public init(environment: [String: String], home: URL = FileManager.default.homeDirectoryForCurrentUser, versionTimeout: TimeInterval = 4,
                commandLineToolsPath: @escaping @Sendable () async -> String? = SetupProber.xcodeSelectPath,
                gitVersion: @escaping @Sendable () async -> (exitCode: Int32, output: String)? = SetupProber.systemGitVersion) {
        self.environment = environment; self.home = home; self.versionTimeout = versionTimeout
        self.commandLineToolsPath = commandLineToolsPath; self.gitVersion = gitVersion
    }

    public func probe(_ probe: SetupProbe) async -> SetupProbeResult {
        switch probe {
        case .executable(let name, let minimum):
            // Like ProviderService: every copy on the PATH in order, the first that says its version counts.
            let copies = Self.executables(name, environment: environment)
            guard !copies.isEmpty else { return .missing }
            var version: String?
            for copy in copies where !Task.isCancelled {
                if let text = await versionText(copy) { version = text; break }
            }
            guard let minimum else { return .installed(version: version) }
            guard let version else { return .unverified(minimum: minimum) }
            return Self.meets(version, minimum: minimum) ? .installed(version: version) : .outdated(version: version, minimum: minimum)
        case .claudePlugin(let prefix):
            return ToolkitProbe.userScopePluginInstalled(home: home, matching: { $0.hasPrefix(prefix) }) ? .installed(version: nil) : .missing
        case .skill(let name):
            return Self.skillInstalled(home.appendingPathComponent(".claude/skills/\(name)", isDirectory: true)) ? .installed(version: nil) : .missing
        case .commandLineTools:
            guard let path = await commandLineToolsPath(), Self.isDirectory(path) else { return .missing }
            // Only now: on a Mac without the tools, /usr/bin/git itself offers to install them.
            guard let git = await gitVersion() else { return .needsAttention(.gitUnusable) }
            if git.exitCode == 0 { return .installed(version: nil) }
            return git.output.lowercased().contains("license") ? .needsAttention(.xcodeLicense) : .needsAttention(.gitUnusable)
        case .appBundle(let name, let executable):
            let folders = [URL(fileURLWithPath: "/Applications", isDirectory: true), home.appendingPathComponent("Applications", isDirectory: true)]
            if folders.contains(where: { Self.isDirectory($0.appendingPathComponent(name).path) }) { return .installed(version: nil) }
            if let executable, !Self.executables(executable, environment: environment).isEmpty { return .installed(version: nil) }
            return .missing
        }
    }

    /// Every item that has an entry for `platform` (only those in `only`, when
    /// given), probed one after another.
    public func probeAll(_ catalog: SetupCatalog, platform: SetupPlatform, only: Set<String>? = nil) async -> [String: SetupProbeResult] {
        var results: [String: SetupProbeResult] = [:]
        for item in catalog.items where only?.contains(item.id) ?? true {
            guard let entry = item.platforms[platform], !Task.isCancelled else { continue }
            results[item.id] = await probe(entry.probe)
        }
        return results
    }

    /// A skill counts when its SKILL.md is there. A folder the setup screen placed
    /// counts only once its setup script finished; a skill the user put there
    /// themselves has no marker and counts as it is.
    public static func skillInstalled(_ folder: URL) -> Bool {
        let fileManager = FileManager.default
        guard fileManager.fileExists(atPath: folder.appendingPathComponent("SKILL.md").path) else { return false }
        guard fileManager.fileExists(atPath: folder.appendingPathComponent(skillInstallingMarker).path) else { return true }
        return fileManager.fileExists(atPath: folder.appendingPathComponent(skillDoneMarker).path)
    }

    /// Each executable copy of `name` on the PATH, in order, once.
    public static func executables(_ name: String, environment: [String: String]) -> [URL] {
        var seen = Set<String>()
        return (environment["PATH"] ?? "").split(separator: ":").prefix(64).compactMap { directory in
            let url = URL(fileURLWithPath: String(directory)).appendingPathComponent(name)
            guard seen.insert(url.path).inserted, FileManager.default.isExecutableFile(atPath: url.path) else { return nil }
            return url
        }
    }

    private static func isDirectory(_ path: String) -> Bool {
        var directory: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &directory) && directory.boolValue
    }

    /// `--version`'s output, trimmed as ProviderService keeps it, or nil.
    private func versionText(_ executable: URL) async -> String? {
        guard let result = try? await ProcessCapture.run(executable: executable, arguments: ["--version"], environment: environment, timeout: versionTimeout, maximumBytes: 16_384),
              result.exitCode == 0 else { return nil }
        let text = String(String(decoding: result.stdout, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines).prefix(160))
        return text.isEmpty ? nil : text
    }

    /// The first `1.2.3` (or `1.2`, `1`) in a version line.
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

    /// Whether `version` reaches `minimum`. A prerelease (`2.1.300-beta`) never
    /// does, as `ProviderService.supportsMods` reads it, so the setup screen and
    /// the panes agree on what can run.
    public static func meets(_ version: String, minimum: String) -> Bool {
        if let range = version.range(of: "[0-9]+\\.[0-9]+\\.[0-9]+", options: .regularExpression),
           range.upperBound < version.endIndex, version[range.upperBound] == "-" { return false }
        guard let found = versionNumbers(version), let required = versionNumbers(minimum) else { return false }
        return isAtLeast(found, required)
    }

    /// `xcode-select -p`, read only. A Mac without the tools prints nothing and fails.
    public static let xcodeSelectPath: @Sendable () async -> String? = {
        guard let result = try? await ProcessCapture.run(executable: URL(fileURLWithPath: "/usr/bin/xcode-select"), arguments: ["-p"], timeout: 4, maximumBytes: 4096),
              result.exitCode == 0 else { return nil }
        let path = String(decoding: result.stdout, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        return path.isEmpty ? nil : path
    }

    /// `/usr/bin/git --version` with a deadline. An Xcode whose license is not
    /// accepted makes it fail with a message that says so.
    public static let systemGitVersion: @Sendable () async -> (exitCode: Int32, output: String)? = {
        guard let result = try? await ProcessCapture.run(executable: URL(fileURLWithPath: "/usr/bin/git"), arguments: ["--version"], timeout: 6, maximumBytes: 16_384) else { return nil }
        return (result.exitCode, String(decoding: result.stdout + result.stderr, as: UTF8.self))
    }
}

/// What a row shows.
public enum SetupRowState: String, Sendable, Equatable {
    case checking, installed, missing, outdated, attention, installing, failed

    /// `inRun`: the item is part of the install that is running, or was part of
    /// the last one. `runFinished`: that install's script has ended. An item the
    /// probe still does not see once the script ended failed, whether its command
    /// finished it or only started it (a system window).
    public static func derive(_ result: SetupProbeResult?, inRun: Bool, runFinished: Bool) -> SetupRowState {
        guard let result else { return .checking }
        switch result {
        case .installed, .unverified: return .installed
        case .needsAttention: return .attention
        case .outdated: return inRun && !runFinished ? .installing : .outdated
        case .missing:
            guard inRun else { return .missing }
            return runFinished ? .failed : .installing
        }
    }

    /// Whether the row's checkbox may be ticked: what is missing or failed, and an
    /// out-of-date tool that is not an agent CLI (those get the CLI update instead).
    public static func isSelectable(_ state: SetupRowState, item: SetupItem, platform: SetupPlatform) -> Bool {
        guard item.platforms[platform]?.install.runsInTerminal == true else { return false }
        switch state {
        case .missing, .failed: return true
        case .outdated: return item.provider == nil
        case .checking, .installed, .attention, .installing: return false
        }
    }
}

/// Whether the screen opens by itself at launch.
public enum SetupFirstRun {
    /// True when no agent CLI can run, or a required item (the command line
    /// tools) is not ready, unless the user chose never to see it again. Results
    /// still missing count as not known yet.
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

    /// What the screen ticks the first time it shows: every required item that is
    /// missing, and Claude Code when no agent CLI is there at all.
    public static func preselection(catalog: SetupCatalog, results: [String: SetupProbeResult], platform: SetupPlatform) -> Set<String> {
        var picked = Set<String>()
        for item in catalog.items where item.required && results[item.id] == .missing && item.platforms[platform]?.install.runsInTerminal == true {
            picked.insert(item.id)
        }
        let agents = catalog.items.filter { $0.group == .agent && $0.platforms[platform] != nil }
        if !agents.contains(where: { results[$0.id]?.isInstalled == true }), let first = agents.first, results[first.id] == .missing {
            picked.insert(first.id)
        }
        return picked
    }
}

/// Whether the app's cached CLI runtime disagrees with what the probe just
/// saw, so the runtime (and with it every blocked pane) must be read again.
public enum SetupRuntimeSync {
    public static func isStale(runtime: [ProviderRuntime], catalog: SetupCatalog, results: [String: SetupProbeResult]) -> Bool {
        for item in catalog.items {
            guard let provider = item.provider, let result = results[item.id] else { continue }
            if result.version != runtime.first(where: { $0.id == provider })?.version { return true }
        }
        return false
    }
}
