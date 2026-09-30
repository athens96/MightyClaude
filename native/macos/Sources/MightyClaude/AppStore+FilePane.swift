import AppKit
import Darwin
import MightyCore
import SwiftUI

extension AppStore {
    /// The workspace's tree and preview state, made on first use and kept
    /// while the app runs so a reopened pane shows what it showed before.
    /// Closing the pane releases only the preview (see `closeSession`).
    func filePaneModel(for workspaceId: String) -> FilePaneModel? {
        guard let workspace = snapshot.workspaces.first(where: { $0.id == workspaceId }) else { return nil }
        let root = URL(fileURLWithPath: workspace.path, isDirectory: true)
        if let model = filePaneModels[workspaceId], model.root == root { return model }
        let model = FilePaneModel(workspaceId: workspaceId, root: root)
        filePaneModels[workspaceId] = model
        return model
    }

    /// Show the workspace's one files pane: focus it when it is open,
    /// otherwise put it left of the current pane (or as a tab when the layout
    /// has no room for a split). Like an agent's terminal pane it is never
    /// saved, so it does not come back after a restart.
    func openFilePane(workspaceId: String? = nil) {
        guard !hasModal, let workspace = workspaceId.flatMap({ id in snapshot.workspaces.first { $0.id == id } }) ?? activeWorkspace else { return }
        let id = FilePaneKind.paneId(workspaceId: workspace.id)
        guard CoreValidation.identifier(id), filePaneModel(for: workspace.id) != nil else { return }
        if snapshot.sessions.contains(where: { $0.id == id }) { selectSession(id); return }
        guard snapshot.sessions.count < PaneLayouts.maximumSessions else { error = L("files.error.noRoom"); return }
        reconcilePaneLayout(workspace.id)
        let root = layoutForWorkspace(workspace.id)
        let current = snapshot.activeWorkspaceId == workspace.id ? snapshot.activeSessionId : snapshot.paneLayoutActiveSessionIds?[workspace.id]
        let group = current.flatMap { root?.group(containing: $0) }
        var next = PaneLayouts.inserting(root: root, sessionId: id, targetGroupId: group?.id, placement: root == nil ? "tab" : "left")
        if next?.group(containing: id) == nil { next = PaneLayouts.inserting(root: root, sessionId: id, targetGroupId: group?.id, placement: "tab") }
        guard let next, next.group(containing: id) != nil else { error = L("files.error.noRoom"); return }
        let mode = paneLayoutMode(workspace.id)
        snapshot.sessions.append(RunSession(id: id, workspaceId: workspace.id, title: L("files.pane.title"), kind: FilePaneKind.kind))
        savePaneLayout(next, workspaceId: workspace.id)
        if mode != "focus" { setPaneLayoutMode(next.kind == "split" ? "custom" : "tabs", workspaceId: workspace.id) }
        selectSession(id)
    }

    /// `--files-smoke-test --profile <dir>`: open the files pane on a fixture
    /// workspace in the real window and preview a Markdown, a Swift and a PNG
    /// file. Writes files-smoke-result.json and a screenshot per preview. Only
    /// the fixture under the profile is read; no AI request is sent.
    func runFilesSmokeTest() async {
        let args = ProcessInfo.processInfo.arguments
        var report: [String: Any] = ["passed": false, "aiRequestSent": false]
        do {
            guard args.contains("--profile") else { throw MightyError("files smoke test needs a temporary --profile") }
            let directory = dataDirectory.appendingPathComponent("files-smoke-workspace", isDirectory: true)
            try? FileManager.default.removeItem(at: directory)
            try FileManager.default.createDirectory(at: directory.appendingPathComponent("Sources"), withIntermediateDirectories: true)
            try FileManager.default.createDirectory(at: directory.appendingPathComponent("node_modules/pkg"), withIntermediateDirectories: true)
            try Data("# Files pane\n\n- rendered **markdown**\n\n```swift\nlet x = 1\n```\n".utf8).write(to: directory.appendingPathComponent("README.md"))
            try Data("import Foundation\n\n// A comment\nlet answer = 42\nprint(\"hello\")\n".utf8).write(to: directory.appendingPathComponent("Sources/App.swift"))
            let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 40, pixelsHigh: 30, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
            guard let png = bitmap?.representation(using: .png, properties: [:]) else { throw MightyError("could not make the PNG fixture") }
            try png.write(to: directory.appendingPathComponent("image.png"))

            let workspace = try await repository.approveWorkspace(Workspace(name: "Files Pane", path: directory.path))
            addWorkspace(workspace)
            openFilePane(workspaceId: workspace.id)
            let paneId = FilePaneKind.paneId(workspaceId: workspace.id)
            guard snapshot.activeSessionId == paneId, let model = filePaneModel(for: workspace.id) else { throw MightyError("the files pane did not open") }
            report["paneOpened"] = true
            model.start()
            try await waitForSmoke(timeout: 10) { model.children[""] != nil }
            report["rootEntries"] = model.children[""]?.map(\.name) ?? []
            report["noiseCollapsed"] = model.children[""]?.first { $0.name == "node_modules" }?.isNoise == true && !model.expanded.contains("node_modules")
            model.toggle(WorkspaceFileEntry(name: "Sources", relativePath: "Sources", isDirectory: true))
            try await waitForSmoke(timeout: 10) { model.children["Sources"] != nil }

            let window = NSApp.windows.first { $0.isVisible && $0.contentView != nil }
            func check(_ path: String, name: String, _ matches: (FilePanePreview) -> Bool) async throws {
                model.select(WorkspaceFileEntry(name: name, relativePath: path, isDirectory: false))
                try await waitForSmoke(timeout: 10) { matches(model.preview) }
                try await Task.sleep(for: .milliseconds(300))
                if let window { report["screenshot-" + name] = try captureSmokeWindow(window, filename: "files-smoke-\(name).png").path }
            }
            try await check("README.md", name: "README.md") { if case .markdown = $0 { return true }; return false }
            report["markdown"] = true
            try await check("Sources/App.swift", name: "App.swift") { if case .source(_, let text) = $0 { return text.encoding == .utf8 && text.lineStarts.count == 6 }; return false }
            report["swift"] = true
            try await check("image.png", name: "image.png") { if case .image(_, let image) = $0 { return image.size == CGSize(width: 40, height: 30) }; return false }
            report["png"] = true
            openFilePane(workspaceId: workspace.id)
            report["reopenFocusesSamePane"] = snapshot.sessions.filter { $0.id == paneId }.count == 1 && snapshot.activeSessionId == paneId
            report["hiddenFromPhone"] = !FilePaneKind.phoneVisible(snapshot.sessions).contains { $0.id == paneId }
            report["passed"] = report["reopenFocusesSamePane"] as? Bool == true && report["hiddenFromPhone"] as? Bool == true && report["noiseCollapsed"] as? Bool == true
        } catch { report["error"] = error.localizedDescription }
        do {
            try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true)
            try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys])
                .write(to: dataDirectory.appendingPathComponent("files-smoke-result.json"), options: .atomic)
        } catch { report["passed"] = false; self.error = error.localizedDescription }
        if args.contains("--smoke-exit") {
            await shutdown()
            Darwin.exit(report["passed"] as? Bool == true ? 0 : 1)
        }
    }
}
