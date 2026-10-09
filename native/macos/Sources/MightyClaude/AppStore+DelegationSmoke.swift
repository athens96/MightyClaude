import AppKit
import Darwin
import Foundation
import MightyCore

extension AppStore {
    /// The `claude` the delegation smoke runs: the scripted FakeClaude CI
    /// builds, named by `--fake-claude <path>`, and only in that smoke.
    nonisolated static func delegationSmokeBinaryOverrides(_ arguments: [String] = ProcessInfo.processInfo.arguments) -> [String: URL] {
        guard arguments.contains("--delegation-smoke-test"), let index = arguments.firstIndex(of: "--fake-claude"), index + 1 < arguments.count else { return [:] }
        return ["claude": URL(fileURLWithPath: arguments[index + 1])]
    }

    /// The close paths of parent → child delegation in a disposable profile,
    /// through the store's actions and the phone's routes: a merged child
    /// closed on the Mac and one closed from the phone are cleaned up, a
    /// phone send releases a held notice, a workspace with an open child or
    /// a kept child worktree is refused with `workspace_has_children`, and a
    /// parent with an open child asks first on the Mac and closes from the
    /// phone, its child kept under a 'parent closed' node. No model runs: the
    /// parent's run is FakeClaude's (`--fake-claude`). The result goes to
    /// `delegation-smoke-result.json` in the profile.
    func runDelegationSmokeTest() async {
        let arguments = ProcessInfo.processInfo.arguments
        var result: [String: Any] = ["passed": false]
        var checks: [String: Any] = [:]
        var stage = "prepare"
        do {
            guard arguments.contains("--profile") else { throw MightyError("The delegation smoke needs a disposable --profile.") }
            result["fakeClaude"] = Self.delegationSmokeBinaryOverrides()["claude"] != nil
            await refreshRuntime()
            let base = dataDirectory.appendingPathComponent("Delegation Smoke", isDirectory: true)
            let repo = base.appendingPathComponent("repo", isDirectory: true)
            try FileManager.default.createDirectory(at: repo, withIntermediateDirectories: true)
            try await delegationSmokeGit(["init", "-q", "-b", "main"], in: repo)
            try Data("# Delegation smoke\n".utf8).write(to: repo.appendingPathComponent("README.md"))
            try await delegationSmokeGit(["add", "-A"], in: repo)
            try await delegationSmokeCommit("Start", in: repo)
            let workspace = try await repository.approveWorkspace(Workspace(name: "Delegation Smoke", path: repo.path))
            addWorkspace(workspace)
            var parentId = snapshot.sessions.first(where: { $0.workspaceId == workspace.id && $0.kind == "claude" && $0.provider == "claude" })?.id
            if parentId == nil { parentId = addSession(kind: "claude", provider: "claude", workspaceId: workspace.id) }
            guard let parent = parentId, let mode = snapshot.sessions.first(where: { $0.id == parent })?.settings.permissionMode else { throw MightyError("No parent Claude pane.") }

            // Three reported children in worktrees made the way delegate makes
            // them. B starts at A's head, so both merge by fast-forward.
            stage = "children"
            let maker = ChildWorktreeMaker(root: dataDirectory.appendingPathComponent("worktrees", isDirectory: true), freeBytes: { _ in .max })
            guard case .ready(let start) = await maker.check(workspace: repo.path) else { throw MightyError("The smoke repository is not ready for worktrees.") }
            let ids = (0 ..< 3).map { _ in UUID().uuidString.lowercased() }
            var records: [ChildRecord] = [], folders: [String: String] = [:], heads: [String: String] = [:]
            for (index, id) in ids.enumerated() {
                var from = start
                if index == 1 { from.baseCommit = heads[ids[0]] ?? start.baseCommit }
                let task = "Smoke child \(index + 1): write note \(index + 1)."
                guard case .created(let made) = await maker.make(sessionId: id, base: from, task: task) else { throw MightyError("Child \(index + 1)'s worktree was not made.") }
                let worktree = URL(fileURLWithPath: made.worktreePath, isDirectory: true)
                try Data("Note \(index + 1)\n".utf8).write(to: worktree.appendingPathComponent("note-\(index + 1).txt"))
                try await delegationSmokeGit(["add", "-A"], in: worktree)
                try await delegationSmokeCommit("Note \(index + 1)", in: worktree)
                try Data("# Report\n\nChild \(index + 1) wrote its note.\n".utf8).write(to: URL(fileURLWithPath: ChildWorktree.reportFile(worktreePath: made.worktreePath)))
                let head = try await delegationSmokeGit(["rev-parse", "HEAD"], in: worktree)
                heads[id] = head; folders[id] = made.workingFolder
                records.append(ChildRecord(id: id, parentSessionId: parent, worktreePath: made.worktreePath, parentBranch: made.parentBranch, baseCommit: made.baseCommit,
                                           startingMode: mode, requestKey: DelegationRequestKey.make(parentSessionId: parent, parentRunId: "smoke", task: task, startingMode: mode),
                                           reportRevision: 1, reportHead: head, state: .reported, parentCheckout: repo.path))
            }
            let (a, b, c) = (ids[0], ids[1], ids[2])
            // C's notice was pending when the app last quit, so this launch holds it.
            let notice = Notice(id: UUID().uuidString.lowercased(), childId: c, reportRevision: 1, kind: .reported, lane: .pending)
            try DelegationFileStore(directory: dataDirectory).save(DelegationFile(children: records, notices: [notice]))
            delegationChildWatch?.cancel(); delegationHeldWatch?.cancel()
            await delegationRunEvents?.finish()
            delegation = nil; delegationRunEvents = nil
            startDelegation()
            guard let delegation else { throw MightyError("Delegation did not load the smoke's file.") }
            for id in ids {
                guard await createPane(DelegationChildPane(sessionId: id, parentSessionId: parent, mode: mode, folder: folders[id] ?? "")) else { throw MightyError("A child pane was not made.") }
            }
            try await waitForSmoke(timeout: 10) { self.delegationChildren.count == 3 && self.delegationHeld[parent]?.map(\.id) == [notice.id] }
            checks["childrenListed"] = true

            // 1. A merged child closed on the Mac is cleaned up: its worktree
            //    and branch go, its merge record stays.
            stage = "mac-close-merged-child"
            try await delegationSmokeMerge(a)
            closeSession(a)
            checks["macCloseMergedChild"] = try await delegationSmokeCleaned(a, delegation: delegation, repo: repo)

            // 2. So is one closed from the phone.
            stage = "phone-close-merged-child"
            try await delegationSmokeMerge(b)
            try mobileClose(b)
            checks["phoneCloseMergedChild"] = try await delegationSmokeCleaned(b, delegation: delegation, repo: repo)
            let main = try await delegationSmokeGit(["rev-parse", "main"], in: repo)
            guard main == heads[b] else { throw MightyError("main is not at child B's head after both merges.") }

            // 3. A phone send to the idle parent releases C's held notice ahead
            //    of its text, in one run.
            stage = "phone-send-releases-held-notice"
            let text = "Carry on with what the third child reported."
            let outcome: SubmitOutcome
            switch try mobileSubmit(parent, text: text) {
            case .immediate(let value): outcome = value
            case .steering(let task): outcome = await task.value
            }
            guard outcome == .started else { throw MightyError("The phone send was \(outcome.rawValue), not started.") }
            let delivered = try await delegationSmokeAwait("C's notice delivered", timeout: 60) { () -> Notice? in
                let file = await delegation.file
                return file.notices.first { $0.id == notice.id && $0.lane == .delivered && $0.receipt?.runId.isEmpty == false }
            }
            try await waitForSmoke(timeout: 60) { self.snapshot.sessions.first(where: { $0.id == parent })?.status != "running" && !self.pendingRuns.contains(parent) }
            let sent = snapshot.sessions.first(where: { $0.id == parent })?.logs.last(where: { $0.kind == "user" })?.text ?? ""
            // The notice is a pointer naming itself and its child, ahead of the human's text.
            guard delivered.receipt?.route == .queue, let pointer = sent.range(of: "[Mighty Claude notice \(notice.id)]"), sent.contains(c),
                  let human = sent.range(of: text), pointer.upperBound <= human.lowerBound,
                  delegationHeld[parent]?.isEmpty != false else { throw MightyError("The held notice did not go ahead of the phone's text in one run.") }
            checks["phoneSendReleasedHeldNotice"] = ["route": delivered.receipt?.route.rawValue ?? "", "runId": delivered.receipt?.runId ?? ""]

            // 4. A workspace with an open child is not removed.
            stage = "remove-workspace-open-child"
            guard await removeWorkspace(workspace).value == .workspaceHasChildren, snapshot.workspaces.contains(where: { $0.id == workspace.id }),
                  snapshot.sessions.contains(where: { $0.id == c }) else { throw MightyError("A workspace with an open child was removed.") }
            checks["removeRefusedOpenChild"] = DelegationReasonCode.workspaceHasChildren.rawValue

            // 5. Closing the parent on the Mac asks first and closes nothing yet.
            stage = "mac-parent-close-asks"
            requestCloseSession(parent)
            guard pendingParentClose?.id == parent, pendingParentClose?.openChildren == 1, snapshot.sessions.contains(where: { $0.id == parent }) else { throw MightyError("The Mac close of a parent with an open child did not ask first.") }
            pendingParentClose = nil
            checks["macParentCloseAsks"] = true

            // 6. From the phone it closes only the parent: C stays open, listed
            //    under a 'parent closed' node.
            stage = "phone-parent-close"
            try mobileClose(parent)
            try await waitForSmoke(timeout: 15) { !self.snapshot.sessions.contains(where: { $0.id == parent }) }
            let tree = DelegationSidebar.tree(workspaceId: workspace.id, workspaces: snapshot.workspaces, sessions: snapshot.sessions, children: delegationChildren)
            let afterParent = await delegation.file
            guard snapshot.sessions.contains(where: { $0.id == c }), afterParent.children.first(where: { $0.id == c })?.state == .reported,
                  tree.parentClosed.first(where: { $0.id == parent })?.children.contains(where: { $0.id == c }) == true else {
                throw MightyError("Closing the parent did not keep its open child under 'parent closed'.")
            }
            checks["phoneParentCloseKeepsChild"] = true

            // 7. Closing the unmerged child keeps its worktree, and the
            //    workspace stays refused while it is on disk.
            stage = "remove-workspace-kept-worktree"
            closeSession(c)
            let kept = try await delegationSmokeAwait("C's close", timeout: 30) { self.delegationCloseOutcomes[c] }
            guard kept == .kept(.notMerged), FileManager.default.fileExists(atPath: records[2].worktreePath),
                  await removeWorkspace(workspace).value == .workspaceHasChildren, snapshot.workspaces.contains(where: { $0.id == workspace.id }) else {
                throw MightyError("A workspace with a child worktree not cleaned up was removed, or C's close removed its work.")
            }
            checks["removeRefusedKeptWorktree"] = true

            // 8. Once a human discards C, the workspace goes.
            stage = "discard-then-remove"
            let nested = await delegation.nestedWorktrees(of: c)
            guard await delegation.discardFromCard(c, confirmedNested: nested ?? []) == .discarded else { throw MightyError("C was not discarded.") }
            guard await removeWorkspace(workspace).value == nil else { throw MightyError("The workspace was refused after its children were gone.") }
            try await waitForSmoke(timeout: 15) { !self.snapshot.workspaces.contains(where: { $0.id == workspace.id }) }
            checks["removedAfterDiscard"] = true

            stage = "done"
            result["passed"] = true
        } catch {
            result["error"] = error.localizedDescription
            result["failedStage"] = stage
        }
        result["checks"] = checks
        let url = dataDirectory.appendingPathComponent("delegation-smoke-result.json")
        if let data = try? JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys]) { try? data.write(to: url, options: .atomic) }
        if arguments.contains("--smoke-exit") { await shutdown(); Darwin.exit(result["passed"] as? Bool == true ? 0 : 1) }
    }

    /// The human's merge of `id` from its card, which must succeed.
    private func delegationSmokeMerge(_ id: String) async throws {
        mergeChildFromCard(id)
        try await waitForSmoke(timeout: 30) { self.delegationCardNotes[id] != nil }
        guard delegationCardNotes[id]?.result == .done else { throw MightyError("The card merge of \(id) was \(String(describing: delegationCardNotes[id]?.result)).") }
    }

    /// Waits for the close of the merged child `id` to clean it up, and checks
    /// what it left: no worktree, no branch, a closed record and its merge.
    private func delegationSmokeCleaned(_ id: String, delegation: DelegationCoordinator, repo: URL) async throws -> Bool {
        let outcome = try await delegationSmokeAwait("\(id)'s cleanup", timeout: 60) { self.delegationCloseOutcomes[id] }
        guard outcome == .cleaned else { throw MightyError("Closing merged child \(id) gave \(outcome).") }
        let file = await delegation.file
        guard let child = file.children.first(where: { $0.id == id }), child.state == .closed, file.merges.contains(where: { $0.childId == id }),
              !FileManager.default.fileExists(atPath: child.worktreePath),
              (try? await delegationSmokeGit(["rev-parse", "-q", "--verify", "refs/heads/\(child.branch)"], in: repo)) == nil,
              !snapshot.sessions.contains(where: { $0.id == id }) else { throw MightyError("Merged child \(id)'s close left its pane, worktree or branch, or lost its record.") }
        return true
    }

    private func delegationSmokeAwait<T>(_ what: String, timeout: TimeInterval, _ probe: () async -> T?) async throws -> T {
        let deadline = Date().addingTimeInterval(timeout)
        while true {
            if let found = await probe() { return found }
            guard Date() < deadline else { throw MightyError("Timed out waiting for \(what).") }
            try await Task.sleep(for: .milliseconds(50))
        }
    }

    private func delegationSmokeCommit(_ message: String, in folder: URL) async throws {
        try await delegationSmokeGit(["-c", "user.name=Mighty Smoke", "-c", "user.email=smoke@example.invalid", "commit", "-q", "-m", message], in: folder)
    }

    /// Git in `folder`; its trimmed output, or a throw when it fails.
    @discardableResult
    private func delegationSmokeGit(_ arguments: [String], in folder: URL) async throws -> String {
        try await Task.detached {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/git")
            process.arguments = ["-C", folder.path] + arguments
            let output = Pipe()
            process.standardOutput = output
            process.standardError = FileHandle.nullDevice
            process.standardInput = FileHandle.nullDevice
            try process.run()
            let data = output.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            guard process.terminationStatus == 0 else { throw MightyError("git \(arguments.joined(separator: " ")) exited \(process.terminationStatus).") }
            return String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        }.value
    }
}
