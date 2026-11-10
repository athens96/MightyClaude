import AppKit
import Darwin
import MightyCore
import SwiftUI

/// One workspace's work map window: what it shows (the model git and the saved summaries made) and what the
/// user picked. Kept until quit, so a reopened pane shows what it showed; the picks (version, topic, lane chips)
/// are remembered only while the app runs.
@MainActor
final class WorkMapPaneModel: ObservableObject {
    let workspaceId: String
    @Published var map: WorkMapModel?
    @Published var loading = false
    @Published var selectedKey: String?
    @Published var selectedTopic: Int?
    @Published var enabledLanes = Set(WorkMap.lanes)
    @Published var onlyWithRequests = false
    @Published var commitsShown = Set<String>()
    @Published var copiedSha: String?
    /// A version key the list should scroll to (the overview's column was clicked); the list clears it.
    @Published var scrollTarget: String?
    /// The words of the last failed summary by version key; the card shows them until the next try.
    var errors: [String: String] = [:]
    /// The version being summarized; one at a time per workspace.
    var summarizingKey: String?
    var generation = 0
    var loadTask: Task<Void, Never>?
    var summaryTask: Task<Void, Never>?

    init(workspaceId: String) { self.workspaceId = workspaceId }

    func apply(_ model: WorkMapModel) {
        map = model
        loading = false
        if selectedKey == nil || !model.versions.contains(where: { $0.key == selectedKey }) {
            selectedKey = model.versions.first?.key
            selectedTopic = nil
        }
    }

    func select(version key: String) {
        guard selectedKey != key else { return }
        selectedKey = key
        selectedTopic = nil
    }

    var selectedVersion: WorkMapVersion? { map?.versions.first { $0.key == selectedKey } }
}

extension AppStore {
    /// The workspace's work map state, made on first use.
    func workMapModel(for workspaceId: String) -> WorkMapPaneModel {
        if let model = workMapPanes[workspaceId] { return model }
        let model = WorkMapPaneModel(workspaceId: workspaceId)
        workMapPanes[workspaceId] = model
        return model
    }

    /// Why the work map cannot be opened for `workspace`, in words, or nil. `isGit` is the live reading of its folder.
    func workMapUnavailableReason(_ workspace: Workspace, isGit: Bool) -> String? {
        if workspace.remote != nil { return L("workMap.unavailable.remote") }
        return isGit ? nil : L("workMap.unavailable.notGit")
    }

    /// Show the workspace's one work map: focus it when it is open, otherwise put it beside the current pane as a
    /// tab. Like the files pane it is never saved, so it does not come back after a restart.
    func openWorkMapPane(workspaceId: String? = nil) {
        guard !hasModal, let workspace = workspaceId.flatMap({ id in snapshot.workspaces.first { $0.id == id } }) ?? activeWorkspace else { return }
        guard workspace.remote == nil else { error = L("workMap.unavailable.remote"); return }
        let id = WorkMapPaneKind.paneId(workspaceId: workspace.id)
        guard CoreValidation.identifier(id) else { return }
        insertReadOnlyPane(id: id, title: L("workMap.title"), kind: WorkMapPaneKind.kind, in: workspace, placement: "tab")
        refreshWorkMap(workspaceId: workspace.id)
    }

    /// The map as git, the saved summaries and the open panes' requests give it now. Read off the main thread.
    func readWorkMap(workspaceId: String) async -> WorkMapModel? {
        guard let workspace = snapshot.workspaces.first(where: { $0.id == workspaceId }), workspace.remote == nil else { return nil }
        let state = workMapModel(for: workspaceId)
        let root = URL(fileURLWithPath: workspace.path, isDirectory: true)
        let sessions = snapshot.sessions.filter { $0.workspaceId == workspaceId && !FilePaneKind.isReadOnlyPane($0.kind) }
        let originals = fileOriginals, summaries = workMapSummaries, errors = state.errors, summarizing = state.summarizingKey
        return await Task.detached(priority: .userInitiated) { () async -> WorkMapModel in
            let requests = WorkMap.requestInputs(sessions: sessions, store: originals)
            return await WorkMap.read(root: root, requests: requests, cached: summaries.load(root: root), errors: errors, summarizing: summarizing)
        }.value
    }

    /// Reads the map again for the window; a newer read supersedes an older one still running.
    func refreshWorkMap(workspaceId: String) {
        let state = workMapModel(for: workspaceId)
        state.generation += 1
        let generation = state.generation
        state.loading = true
        state.loadTask?.cancel()
        state.loadTask = Task { [weak self, weak state] in
            let model = await self?.readWorkMap(workspaceId: workspaceId)
            guard let state, state.generation == generation, !Task.isCancelled else { return }
            if let model { state.apply(model) } else { state.loading = false }
        }
    }

    /// Starts the topic summary of `version` (one at a time per workspace). The words of a failure stay on the
    /// version's card; a success is saved and the map read again.
    func startWorkMapSummary(workspaceId: String, version: WorkMapVersion) {
        guard let workspace = snapshot.workspaces.first(where: { $0.id == workspaceId }), workspace.remote == nil else { return }
        let state = workMapModel(for: workspaceId)
        guard state.summarizingKey == nil else { return }
        state.summarizingKey = version.key
        state.errors[version.key] = nil
        if var map = state.map { map.summarizing = version.key; state.map = map }
        let root = URL(fileURLWithPath: workspace.path, isDirectory: true)
        let summaries = workMapSummaries, providers = providers
        state.summaryTask = Task { [weak self, weak state] in
            var failure: String?
            do {
                let topics = try await WorkMapSummary.summarize(version: version, root: root, providers: providers)
                try summaries.save(WorkMapSummaryEntry(rangeEnd: version.rangeEnd, topics: topics), key: version.key, root: root)
            } catch is CancellationError {
                failure = nil
            } catch let error as WorkMapSummaryError {
                failure = error.message
            } catch {
                failure = WorkMapSummaryError.output.message
            }
            guard let state else { return }
            state.summarizingKey = nil
            if let failure { state.errors[version.key] = failure }
            self?.refreshWorkMap(workspaceId: workspaceId)
        }
    }

    func summarizeWorkMap(workspaceId: String, key: String) {
        guard let version = workMapModel(for: workspaceId).map?.versions.first(where: { $0.key == key }) else { return }
        startWorkMapSummary(workspaceId: workspaceId, version: version)
    }

    /// Copies a commit's short sha; the chip says so for a moment.
    func copyWorkMapSha(_ sha: String, workspaceId: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(sha, forType: .string)
        let state = workMapModel(for: workspaceId)
        state.copiedSha = sha
        Task { [weak state] in
            try? await Task.sleep(for: .seconds(1.5))
            if state?.copiedSha == sha { state?.copiedSha = nil }
        }
    }

    /// Opens `path` (relative to the workspace root) in the workspace's files pane.
    func openWorkMapFile(workspaceId: String, path: String) {
        openFilePane(workspaceId: workspaceId)
        filePaneModel(for: workspaceId)?.open(relativePath: path)
    }

    // MARK: Phone

    /// `GET /m1/workspaces/{id}/workmap`: the model without the commits' files; nil for a workspace this Mac does not have.
    func mobileWorkMap(workspaceId: String) async -> Data? {
        guard let model = await readWorkMap(workspaceId: workspaceId) else { return nil }
        return model.phoneBody()
    }

    /// `POST /m1/workspaces/{id}/workmap/summarize`: starts the summary of version `key`.
    func mobileWorkMapSummarize(workspaceId: String, key: String) async throws {
        guard let workspace = snapshot.workspaces.first(where: { $0.id == workspaceId }), workspace.remote == nil else {
            throw MobileHostError.notFound(L("remote.error.workspaceNotFound"))
        }
        let state = workMapModel(for: workspaceId)
        guard state.summarizingKey == nil else { throw MobileHostError.conflict(L("workMap.error.busy")) }
        guard let map = await readWorkMap(workspaceId: workspaceId), map.available, let version = map.versions.first(where: { $0.key == key }) else {
            throw MobileHostError.badRequest(L("remote.error.badBody"))
        }
        // The read above let other work run: it may have started a summary.
        guard state.summarizingKey == nil else { throw MobileHostError.conflict(L("workMap.error.busy")) }
        startWorkMapSummary(workspaceId: workspaceId, version: version)
    }

    // MARK: Smoke test

    /// `--workmap-smoke-test --profile <dir>`: a git repository of three commits and a tag under the profile, its
    /// work map opened in the real window with a saved (not AI-made) summary shown as topic cards. Writes
    /// workmap-smoke-result.json and a screenshot. Only the fixture under the profile is read; no AI request is sent.
    func runWorkMapSmokeTest() async {
        let args = ProcessInfo.processInfo.arguments
        var report: [String: Any] = ["passed": false, "aiRequestSent": false]
        do {
            guard args.contains("--profile") else { throw MightyError("work map smoke test needs a temporary --profile") }
            guard let git = WorkspaceGitInfo.gitExecutable() else { throw MightyError("git is not installed") }
            let directory = dataDirectory.appendingPathComponent("workmap-smoke-workspace", isDirectory: true)
            try? FileManager.default.removeItem(at: directory)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            func run(_ arguments: [String]) async throws {
                let settings = ["-c", "user.name=Smoke", "-c", "user.email=smoke@example.com", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false", "-C", directory.path]
                let result = try await ProcessCapture.run(executable: URL(fileURLWithPath: git), arguments: settings + arguments, timeout: 20)
                guard result.exitCode == 0 else { throw MightyError("git \(arguments.joined(separator: " ")) failed: \(String(decoding: result.stderr, as: UTF8.self))") }
            }
            func commit(_ message: String, file: String) async throws {
                try Data(message.utf8).write(to: directory.appendingPathComponent(file))
                try await run(["add", "-A"])
                try await run(["commit", "-m", message])
            }
            try await run(["init", "-b", "main"])
            try await commit("(mac) first change", file: "a.txt")
            try await run(["tag", "v0.1.0"])
            try await commit("(windows) second change", file: "b.txt")
            try await commit("(phone, mac) third change", file: "c.txt")

            let workspace = try await repository.approveWorkspace(Workspace(name: "Work Map", path: directory.path))
            addWorkspace(workspace)
            openWorkMapPane(workspaceId: workspace.id)
            let paneId = WorkMapPaneKind.paneId(workspaceId: workspace.id)
            guard snapshot.activeSessionId == paneId else { throw MightyError("the work map did not open") }
            report["paneOpened"] = true
            let state = workMapModel(for: workspace.id)
            try await waitForSmoke(timeout: 20) { state.map?.available == true && state.map?.versions.count == 2 }
            report["versions"] = state.map?.versions.map(\.key) ?? []
            let next = state.map?.versions.first
            report["nextCommits"] = next?.total ?? 0

            // A saved summary stands in for the AI's answer.
            let commits = next?.commits ?? []
            let topics = [WorkMapTopic(title: "Smoke topic", summary: "Two commits grouped by a saved summary.", lanes: ["mac", "windows", "phone"], commits: commits.map(\.sha), requests: [])]
            try workMapSummaries.save(WorkMapSummaryEntry(rangeEnd: next?.rangeEnd ?? "", topics: topics), key: "next", root: URL(fileURLWithPath: directory.path, isDirectory: true))
            refreshWorkMap(workspaceId: workspace.id)
            try await waitForSmoke(timeout: 20) { state.map?.versions.first?.topics?.count == 1 }
            state.selectedTopic = 0
            report["topicsShown"] = true
            try await Task.sleep(for: .milliseconds(500))
            if let window = NSApp.windows.first(where: { $0.isVisible && $0.contentView != nil }) {
                report["screenshot"] = try captureSmokeWindow(window, filename: "workmap-smoke.png").path
            }
            openWorkMapPane(workspaceId: workspace.id)
            report["reopenFocusesSamePane"] = snapshot.sessions.filter { $0.id == paneId }.count == 1 && snapshot.activeSessionId == paneId
            report["hiddenFromPhone"] = !FilePaneKind.phoneVisible(snapshot.sessions).contains { $0.id == paneId }
            report["phoneBodyHasNoFiles"] = state.map.map { String(decoding: $0.phoneBody(), as: UTF8.self).contains("\"files\":[\"") == false } ?? false
            report["passed"] = report["reopenFocusesSamePane"] as? Bool == true && report["hiddenFromPhone"] as? Bool == true
                && report["phoneBodyHasNoFiles"] as? Bool == true && report["nextCommits"] as? Int == 2
        } catch { report["error"] = error.localizedDescription }
        do {
            try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true)
            try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys])
                .write(to: dataDirectory.appendingPathComponent("workmap-smoke-result.json"), options: .atomic)
        } catch { report["passed"] = false; self.error = error.localizedDescription }
        if args.contains("--smoke-exit") {
            await shutdown()
            Darwin.exit(report["passed"] as? Bool == true ? 0 : 1)
        }
    }
}
