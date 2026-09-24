import Foundation

// MARK: - Plan

public struct ToolkitPlanItem: Sendable, Equatable {
    public enum Action: Sendable, Equatable {
        case run(commands: [[String]])
        case skip
    }
    public let entry: ToolkitEntry
    public let action: Action

    public init(entry: ToolkitEntry, action: Action) {
        self.entry = entry; self.action = action
    }
}

// MARK: - Run result

public struct ToolkitRunItem: Sendable, Equatable {
    public enum Verdict: String, Sendable, Equatable { case installed, failed, skipped }
    public let entryId: String
    public let verdict: Verdict

    public init(entryId: String, verdict: Verdict) {
        self.entryId = entryId; self.verdict = verdict
    }
}

// MARK: - Executor

public struct ToolkitCommandOutput: Sendable {
    public let exitCode: Int32
    public let output: String
    public init(exitCode: Int32 = 0, output: String = "") {
        self.exitCode = exitCode; self.output = output
    }
    public static var success: ToolkitCommandOutput { .init(exitCode: 0) }
    public static func failure(output: String = "", exitCode: Int32 = 1) -> ToolkitCommandOutput {
        .init(exitCode: exitCode, output: output)
    }
}

public protocol ToolkitRunnerExecutor: Sendable {
    func run(_ argv: [String]) -> ToolkitCommandOutput
}

// MARK: - Runner

/// Coordinates the "install missing tools" flow for the toolkit list.
///
/// `plan()` re-probes every entry and returns only missing ones.
/// Verdict after `run()` comes from re-probing, never from exit codes.
public actor ToolkitRunner {
    private let store: ToolkitStore
    private let probeContext: ToolkitProbeContext

    public init(store: ToolkitStore, probeContext: ToolkitProbeContext) {
        self.store = store; self.probeContext = probeContext
    }

    // MARK: - Plan

    /// Returns items for entries that are currently missing.
    /// Bundled and user+approved entries get `.run(commands:)`;
    /// unapproved user entries get `.skip`.
    public func plan() async -> [ToolkitPlanItem] {
        let (entries, _) = await store.list()
        var items: [ToolkitPlanItem] = []
        for entry in entries {
            let approval = await store.approval(for: entry)
            guard ToolkitProbe.probe(entry: entry, approval: approval, context: probeContext) == .missing else { continue }
            if entry.source == .user && approval == nil {
                items.append(ToolkitPlanItem(entry: entry, action: .skip))
            } else {
                let commands = Self.installCommands(for: entry, approval: approval, context: probeContext)
                guard !commands.isEmpty else { continue }
                items.append(ToolkitPlanItem(entry: entry, action: .run(commands: commands)))
            }
        }
        return items
    }

    // MARK: - Run

    /// Executes a plan built by `plan()`. Items run in order; failure never stops the run.
    /// Fetch steps are retried once when the output matches a network-error pattern.
    /// Verdict is decided by re-probing, not by exit codes.
    public func run(plan items: [ToolkitPlanItem], executor: any ToolkitRunnerExecutor) async -> [ToolkitRunItem] {
        var results: [ToolkitRunItem] = []
        for item in items {
            switch item.action {
            case .skip:
                results.append(ToolkitRunItem(entryId: item.entry.entryId, verdict: .skipped))
            case .run(let commands):
                await executeEntry(entry: item.entry, commands: commands, executor: executor)
                let approval = await store.approval(for: item.entry)
                let probeResult = ToolkitProbe.probe(entry: item.entry, approval: approval, context: probeContext)
                let verdict: ToolkitRunItem.Verdict = probeResult == .installed ? .installed : .failed
                results.append(ToolkitRunItem(entryId: item.entry.entryId, verdict: verdict))
            }
        }
        return results
    }

    // MARK: - Execution

    private func executeEntry(entry: ToolkitEntry, commands: [[String]], executor: any ToolkitRunnerExecutor) async {
        var scriptMarker: URL? = nil
        let isRepoScript: Bool
        if case .repoScript = entry.install {
            isRepoScript = true
            let approval = await store.approval(for: entry)
            if let sha = approval?.resolvedCommit {
                scriptMarker = ToolkitProbe.repoScriptMarker(
                    appDataDir: probeContext.appDataDir, resolvedCommit: sha)
            }
        } else {
            isRepoScript = false
        }

        for (index, cmd) in commands.enumerated() {
            // The script step runs only if its resolved path stays inside the clone.
            if isRepoScript && index == commands.count - 1 && !Self.scriptStaysInClone(cmd.first ?? "", commands: commands) { return }
            let isFetch = Self.isFetchStep(cmd)
            var result = executor.run(cmd)
            // Retry fetch steps once on network-pattern errors
            if result.exitCode != 0 && isFetch && Self.isNetworkError(result.output) {
                result = executor.run(cmd)
            }
            // RepoScript: write completion marker when script step (last command) exits 0
            if isRepoScript && index == commands.count - 1 && result.exitCode == 0 {
                if let marker = scriptMarker {
                    try? FileManager.default.createDirectory(
                        at: marker.deletingLastPathComponent(), withIntermediateDirectories: true)
                    try? Data().write(to: marker)
                }
            }
            // A repoScript never runs its script on a failed clone or checkout;
            // other kinds go on (e.g. install after an already-known marketplace).
            if isRepoScript && result.exitCode != 0 { return }
        }
    }

    /// The clone directory is the last argument of the first (clone) command.
    static func scriptStaysInClone(_ script: String, commands: [[String]]) -> Bool {
        guard let cloneDir = commands.first?.last else { return false }
        let root = URL(fileURLWithPath: cloneDir).resolvingSymlinksInPath().standardizedFileURL.path
        let resolved = URL(fileURLWithPath: script).resolvingSymlinksInPath().standardizedFileURL.path
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: resolved, isDirectory: &isDirectory), !isDirectory.boolValue else { return false }
        return resolved.hasPrefix(root + "/")
    }

    // MARK: - Command building

    /// The one place that turns an entry into argv arrays. A repoScript entry
    /// needs its approval (the resolved commit); without it nothing is built.
    public static func installCommands(for entry: ToolkitEntry, approval: ToolkitApproval?, context probeContext: ToolkitProbeContext) -> [[String]] {
        switch entry.install {
        case .plugin(let source, let pluginID):
            // `marketplace add` has no name option: the marketplace names itself
            // from its own manifest, which the plugin ID's suffix must match.
            return [
                ["claude", "plugin", "marketplace", "add", "--scope", "user", source],
                ["claude", "plugin", "install", pluginID, "--scope", "user", "--json"],
            ]
        case .mcp(let name, let executable, let args):
            return [["claude", "mcp", "add", "--scope", "user", name, "--"] + [executable] + args]
        case .skill(let url):
            var component = URL(string: url)?.lastPathComponent ?? ""
            if component.hasSuffix(".git") { component = String(component.dropLast(4)) }
            guard !component.isEmpty else { return [] }
            let dest = probeContext.home.appendingPathComponent(".claude/skills/\(component)").path
            return [["git", "clone", url, dest]]
        case .package(let manager, let name):
            switch manager {
            case .brew: return [["brew", "install", name]]
            case .npm: return [["npm", "install", "-g", name]]
            }
        case .repoScript(let url, _, let scriptPath):
            guard let sha = approval?.resolvedCommit else { return [] }
            let cloneDir = probeContext.appDataDir
                .appendingPathComponent("toolkit-clones/\(sha)").path
            return [
                ["git", "clone", "--no-checkout", url, cloneDir],
                ["git", "-C", cloneDir, "checkout", sha],
                ["\(cloneDir)/\(scriptPath)"],
            ]
        }
    }

    // MARK: - Static helpers

    /// Returns true for fetch commands eligible for network-error retry.
    nonisolated public static func isFetchStep(_ argv: [String]) -> Bool {
        guard let exe = argv.first else { return false }
        let rest = Array(argv.dropFirst())
        switch exe {
        case "git":
            return rest.first == "clone" || rest.first == "ls-remote"
        case "brew":
            return rest.first == "install"
        case "npm":
            return rest.first == "install"
        case "claude":
            if rest.starts(with: ["plugin", "marketplace", "add"]) { return true }
            if rest.starts(with: ["plugin", "install"]) { return true }
            return false
        default:
            return false
        }
    }

    /// Returns true when the output string matches a known network-failure pattern.
    nonisolated public static func isNetworkError(_ output: String) -> Bool {
        let patterns = [
            "could not resolve host",
            "connection refused",
            "network is unreachable",
            "could not connect",
            "timed out",
            "ssl handshake",
            "could not download",
            "network error",
            "failed to connect",
            "no route to host",
        ]
        let lower = output.lowercased()
        return patterns.contains { lower.contains($0) }
    }
}
