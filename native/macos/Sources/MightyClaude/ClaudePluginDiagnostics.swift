import AppKit
import MightyCore
import SwiftUI

@MainActor
enum ClaudePluginDiagnostics {
    /// Every list, install and marketplace operation below is an in-memory
    /// fixture. No default CLI service or real mutation button is invoked.
    static func run(store: AppStore) async -> [String: Any] {
        var result: [String: Any] = ["passed": false, "realPluginInstallInvoked": false, "realMarketplaceRefreshInvoked": false, "aiRequestSent": false, "fixtureUsesTwoWindows": true]
        guard ProcessInfo.processInfo.arguments.contains("--profile"), !store.hasModal,
              let workspace = store.activeWorkspace, workspace.remote == nil else {
            result["error"] = "플러그인 검증에는 격리된 로컬 워크스페이스가 필요합니다."; return result
        }
        let originalSnapshot = store.snapshot, originalDrafts = store.drafts, originalAttachments = store.attachmentDrafts
        let originalBrowser = store.pluginBrowser, originalError = store.error
        let originalRemote = store.remoteState, originalRemoteBusy = store.remoteBusy
        let originalUpdating = store.isUpdatingCLIs
        let previousWindow = NSApp.keyWindow
        let fixture = PluginFixtureService()
        let model = ClaudePluginBrowserModel(workspace: workspace,
            load: { await fixture.snapshot() },
            install: { id, scope in await store.performPluginMutation(workspace: workspace) { await fixture.install(id: id, scope: scope, workspace: workspace) } },
            refresh: { name in await store.performPluginMutation(workspace: workspace) { await fixture.refresh(name) } },
            mutationBlockedReason: { store.pluginMutationBlockedReason(workspace: workspace) })
        let composerWindow = NSWindow(contentRect: NSRect(x: 100, y: 140, width: 540, height: 550), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        let browserWindow = NSWindow(contentRect: NSRect(x: 150, y: 170, width: 760, height: 620), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        composerWindow.title = "플러그인 초안 보존 검증"; browserWindow.title = "Claude 플러그인 검증"
        composerWindow.isReleasedWhenClosed = false; browserWindow.isReleasedWhenClosed = false
        defer {
            fixture.release()
            browserWindow.contentView = nil; composerWindow.contentView = nil
            browserWindow.orderOut(nil); browserWindow.close(); composerWindow.orderOut(nil); composerWindow.close()
            store.pluginBrowser = originalBrowser; store.snapshot = originalSnapshot
            store.drafts = originalDrafts; store.attachmentDrafts = originalAttachments
            store.remoteState = originalRemote; store.remoteBusy = originalRemoteBusy
            store.isUpdatingCLIs = originalUpdating; store.error = originalError
            previousWindow?.makeKeyAndOrderFront(nil)
        }
        var stage = "fixture-composer"
        do {
            try FileManager.default.createDirectory(at: store.dataDirectory, withIntermediateDirectories: true)
            func require(_ condition: Bool, _ message: String) throws { guard condition else { throw MightyError(message) } }
            let session = RunSession(id: "plugin-composer-fixture", workspaceId: workspace.id, title: "플러그인 검증")
            store.snapshot.sessions.append(session)
            store.snapshot.activeSessionId = session.id
            store.drafts[session.id] = "플러그인 창을 열기 전 작성한 초안"
            let attachment = try AttachmentSupport.make(name: "plugin-fixture.txt", data: Data("isolated attachment".utf8))
            store.attachmentDrafts[session.id] = [attachment]
            composerWindow.contentView = NSHostingView(rootView: PluginComposerFixture(store: store, sessionID: session.id))
            composerWindow.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            try await store.waitForSmoke(timeout: 3) { editor(in: composerWindow.contentView, id: session.id) != nil }
            guard let input = editor(in: composerWindow.contentView, id: session.id) else { throw MightyError("기존 입력창을 찾지 못했습니다.") }
            try require(input.string == store.drafts[session.id], "초안이 실제 입력창에 표시되지 않았습니다.")
            let inputIdentity = ObjectIdentifier(input)
            browserWindow.contentView = NSHostingView(rootView: ClaudePluginView(model: model, onClose: { [weak browserWindow] in browserWindow?.orderOut(nil) }).environmentObject(store).preferredColorScheme(.dark))
            browserWindow.makeKeyAndOrderFront(nil)
            model.loadIfNeeded()
            try await store.waitForSmoke(timeout: 3) { !model.isBusy && model.snapshot != nil && node(browserWindow, id: "claude-plugin-row-" + fixture.installed[0].id) != nil }
            try require(fixture.loads == 1 && fixture.installs.isEmpty && fixture.refreshes.isEmpty, "목록을 여는 것만으로 변경 작업이 실행되었습니다.")
            try require(node(browserWindow, id: "claude-plugin-browser") != nil && node(browserWindow, id: "claude-plugin-search") != nil, "플러그인 목록·검색 컨트롤이 표시되지 않았습니다.")
            let installedID = fixture.installed.first!.id
            try require(node(browserWindow, id: "claude-plugin-row-" + installedID) != nil, "설치된 플러그인이 표시되지 않았습니다.")
            result["installedListVisible"] = true
            result["openingIsReadOnly"] = true
            result["installedScreenshot"] = try store.captureSmokeWindow(browserWindow, filename: "claude-plugins-installed.png").path

            stage = "catalog-search-scope"
            try press(browserWindow, id: "claude-plugin-tab-marketplace")
            try await store.waitForSmoke(timeout: 3) { node(browserWindow, id: "claude-plugin-install-" + PluginFixtureService.successID) != nil }
            model.search = "Fixture Success"
            try await store.waitForSmoke(timeout: 3) {
                node(browserWindow, id: "claude-plugin-row-" + PluginFixtureService.successID) != nil && node(browserWindow, id: "claude-plugin-row-" + PluginFixtureService.failureID) == nil
            }
            result["catalogSearchFiltersVisibleRows"] = true
            model.search = ""; model.marketplaceFilter = "other-market"
            try await store.waitForSmoke(timeout: 3) { node(browserWindow, id: "claude-plugin-row-" + PluginFixtureService.otherID) != nil && node(browserWindow, id: "claude-plugin-row-" + PluginFixtureService.successID) == nil }
            result["marketplaceFilter"] = true
            model.marketplaceFilter = ""
            for scope in ["local", "project", "user"] {
                model.scope = scope
                try await store.waitForSmoke(timeout: 3) { node(browserWindow, id: "claude-plugin-scope") != nil }
                try require(model.scope == scope, "설치 범위를 선택할 수 없습니다.")
            }
            model.scope = "project"
            try await store.waitForSmoke(timeout: 3) { enabled(browserWindow, id: "claude-plugin-install-" + PluginFixtureService.successID) == true }
            result["catalogScreenshot"] = try store.captureSmokeWindow(browserWindow, filename: "claude-plugins-marketplace.png").path

            stage = "install-dedup-and-draft"
            fixture.pauseNext = true
            try press(browserWindow, id: "claude-plugin-install-" + PluginFixtureService.successID)
            try await store.waitForSmoke(timeout: 3) { fixture.waiting && store.isManagingPlugins && model.isMutating }
            model.install(pluginID: PluginFixtureService.successID)
            try require(fixture.installs.count == 1, "동일한 설치가 중복 실행되었습니다.")
            let directDuplicate = await store.performPluginMutation(workspace: workspace) { await fixture.install(id: "must-not-run", scope: "user", workspace: workspace) }
            try require(directDuplicate.status == "skipped" && fixture.installs.count == 1, "AppStore가 다른 경로의 중복 변경을 허용했습니다.")
            try await store.waitForSmoke(timeout: 3) { enabled(browserWindow, id: "claude-plugin-install-" + PluginFixtureService.successID) == false && enabled(browserWindow, id: "claude-plugin-close") == false }
            try require(fixture.installs.first?.scope == "project" && fixture.installs.first?.workspaceID == workspace.id && fixture.installs.first?.path == workspace.path, "설치 범위 또는 워크스페이스가 변경되었습니다.")
            result["selectedScopeAndWorkspacePassed"] = true
            result["duplicateInstallReservedBeforeAwait"] = true
            result["installControlDisabledWhileBusy"] = true
            result["progressScreenshot"] = try store.captureSmokeWindow(browserWindow, filename: "claude-plugins-installing.png").path

            // A deliberately invalid model adds a second, local safety boundary
            // if the plugin guard ever regresses. This must never start a CLI.
            guard let index = store.snapshot.sessions.firstIndex(where: { $0.id == session.id }) else { throw MightyError("초안 검증 세션이 없습니다.") }
            store.snapshot.sessions[index].model = "invalid\0model"
            let draft = store.drafts[session.id]
            let blocked = store.runBlockedReason(store.snapshot.sessions[index])
            store.error = nil; store.submit(session.id)
            try require(blocked?.contains("플러그인") == true && store.error == blocked && store.drafts[session.id] == draft && store.attachmentDrafts[session.id] == [attachment] && store.snapshot.sessions[index].logs.isEmpty, "설치 중 요청이 실행되거나 초안·첨부가 소실되었습니다.")
            store.snapshot.sessions[index].model = "default"; store.error = nil
            result["newRunBlockedAndDraftPreserved"] = true
            browserWindow.orderOut(nil); composerWindow.makeKeyAndOrderFront(nil)
            try require(composerWindow.makeFirstResponder(input), "기존 입력창에 다시 포커스할 수 없습니다.")
            input.insertText(" · 추가 작성", replacementRange: NSRange(location: (input.string as NSString).length, length: 0))
            try await store.waitForSmoke(timeout: 3) { store.drafts[session.id]?.hasSuffix(" · 추가 작성") == true }
            try require(ObjectIdentifier(try requireEditor(composerWindow, sessionID: session.id)) == inputIdentity, "플러그인 작업 중 입력창이 교체되었습니다.")
            result["underlyingEditorBindingPreservedWhileBusy"] = true
            browserWindow.makeKeyAndOrderFront(nil)
            fixture.release()
            try await store.waitForSmoke(timeout: 3) { !model.isBusy && !store.isManagingPlugins && model.lastResult?.status == "succeeded" }
            try require(fixture.installs.count == 1 && model.snapshot?.installed.contains(where: { $0.pluginID == PluginFixtureService.successID && $0.scope == "project" }) == true, "설치 성공 후 실제 범위 목록이 갱신되지 않았습니다.")
            result["installSuccessReloadsInstalledScope"] = true

            stage = "install-failure"
            model.tab = "marketplace"; model.search = ""; model.marketplaceFilter = ""
            try await store.waitForSmoke(timeout: 3) { enabled(browserWindow, id: "claude-plugin-install-" + PluginFixtureService.failureID) == true }
            try press(browserWindow, id: "claude-plugin-install-" + PluginFixtureService.failureID)
            try await store.waitForSmoke(timeout: 3) { !model.isBusy && model.lastResult?.status == "failed" }
            try require(fixture.installs.count == 2 && !store.isManagingPlugins && node(browserWindow, id: "claude-plugin-status") != nil, "설치 실패가 표시되지 않았거나 실행 잠금이 남았습니다.")
            result["failureVisibleAndAdmissionReleased"] = true
            result["failureScreenshot"] = try store.captureSmokeWindow(browserWindow, filename: "claude-plugins-failure.png").path

            stage = "native-cancel"
            fixture.pauseNext = true
            try await store.waitForSmoke(timeout: 3) { enabled(browserWindow, id: "claude-plugin-install-" + PluginFixtureService.failureID) == true }
            try press(browserWindow, id: "claude-plugin-install-" + PluginFixtureService.failureID)
            try await store.waitForSmoke(timeout: 3) { fixture.waiting && model.isMutating && enabled(browserWindow, id: "claude-plugin-cancel") == true }
            try press(browserWindow, id: "claude-plugin-cancel")
            try await store.waitForSmoke(timeout: 3) { !model.isBusy && !store.isManagingPlugins && !fixture.waiting && model.lastResult?.status == "cancelled" }
            try require(fixture.installs.count == 3 && fixture.installed.count == 2 && store.drafts[session.id]?.hasSuffix(" · 추가 작성") == true, "취소한 작업이 설치되거나 잠금·초안이 잘못 변경되었습니다.")
            result["nativeCancelStopsFixtureAndReleasesAdmission"] = true
            result["cancelScreenshot"] = try store.captureSmokeWindow(browserWindow, filename: "claude-plugins-cancelled.png").path

            stage = "marketplace-refresh"
            model.marketplaceFilter = "fixture-market"
            try await store.waitForSmoke(timeout: 3) { enabled(browserWindow, id: "claude-plugin-refresh-marketplaces") == true }
            try press(browserWindow, id: "claude-plugin-refresh-marketplaces")
            try await store.waitForSmoke(timeout: 3) { !model.isBusy && !fixture.refreshes.isEmpty }
            try require(fixture.refreshes == ["fixture-market"], "선택한 마켓 이외의 갱신 작업이 실행되었습니다.")
            result["explicitMarketplaceRefreshUsesFixtureOnly"] = true

            stage = "mutation-guards"
            let beforeCalls = fixture.installs.count
            func rejected(_ target: Workspace) async throws {
                let value = await store.performPluginMutation(workspace: target) { await fixture.install(id: "must-not-run", scope: "local", workspace: target) }
                try require(["skipped", "remote"].contains(value.status) && fixture.installs.count == beforeCalls && !store.isManagingPlugins, "차단된 환경에서 플러그인을 변경했습니다.")
            }
            store.snapshot.sessions[index].status = "running"; try await rejected(workspace); store.snapshot.sessions[index].status = "idle"
            result["runningClaudeBlocked"] = true
            store.isUpdatingCLIs = true; try await rejected(workspace); store.isUpdatingCLIs = false
            result["CLIUpdateBlocked"] = true
            store.remoteState.host.enabled = true; try await rejected(workspace); store.remoteState.host.enabled = false
            store.remoteBusy = true; try await rejected(workspace); store.remoteBusy = false
            result["remoteSharingAndConnectionBusyBlocked"] = true
            let remote = Workspace(id: "plugin-remote-fixture", name: "Remote fixture", path: "/remote/fixture", remote: RemoteWorkspaceReference(connectionId: "fixture-connection", workspaceId: "host-workspace", hostName: "Fixture host"))
            store.snapshot.workspaces.append(remote); try await rejected(remote)
            var stale = workspace; stale.path += "/moved"; try await rejected(stale)
            result["remoteAndChangedWorkspaceBlocked"] = true
            let beforeLoads = fixture.loads
            let remoteModel = ClaudePluginBrowserModel(workspace: remote, load: { await fixture.snapshot() },
                install: { id, scope in await fixture.install(id: id, scope: scope, workspace: remote) },
                refresh: { await fixture.refresh($0) })
            browserWindow.contentView = NSHostingView(rootView: ClaudePluginView(model: remoteModel, onClose: { [weak browserWindow] in browserWindow?.orderOut(nil) }).preferredColorScheme(.dark))
            remoteModel.loadIfNeeded(); remoteModel.install(pluginID: PluginFixtureService.successID); remoteModel.refreshMarketplaces()
            try await store.waitForSmoke(timeout: 3) { node(browserWindow, id: "claude-plugin-remote-unavailable") != nil }
            try require(fixture.loads == beforeLoads && fixture.installs.count == beforeCalls && remoteModel.snapshot == nil, "원격 플러그인 화면에서 로컬 서비스가 호출되었습니다.")
            result["remoteUIExplainsUnavailableWithoutLocalCalls"] = true
            result["remoteScreenshot"] = try store.captureSmokeWindow(browserWindow, filename: "claude-plugins-remote.png").path
            try press(browserWindow, id: "claude-plugin-close")
            try await store.waitForSmoke(timeout: 3) { !browserWindow.isVisible }
            await remoteModel.shutdown()
            composerWindow.makeKeyAndOrderFront(nil)
            try require(ObjectIdentifier(try requireEditor(composerWindow, sessionID: session.id)) == inputIdentity && input.string == store.drafts[session.id] && input.string.hasSuffix(" · 추가 작성") && store.attachmentDrafts[session.id] == [attachment], "플러그인 창을 닫은 뒤 입력 또는 첨부가 변경되었습니다.")
            result["draftAndAttachmentSurviveBrowserClose"] = true
            result["composerScreenshot"] = try store.captureSmokeWindow(composerWindow, filename: "claude-plugins-composer.png").path
            result["fixtureInstallCalls"] = fixture.installs.count
            result["fixtureRefreshCalls"] = fixture.refreshes.count
            result["passed"] = true
        } catch {
            result["failedStage"] = stage; result["error"] = error.localizedDescription
            if let capture = try? store.captureSmokeWindow(browserWindow.isVisible ? browserWindow : composerWindow, filename: "claude-plugins-diagnostic-failure.png") { result["diagnosticFailureScreenshot"] = capture.path }
        }
        fixture.release(); await model.shutdown()
        return result
    }

    private static func requireEditor(_ window: NSWindow, sessionID: String) throws -> NSTextView {
        guard let value = editor(in: window.contentView, id: sessionID) else { throw MightyError("실제 초안 입력창을 찾지 못했습니다.") }; return value
    }
    private static func editor(in view: NSView?, id: String) -> NSTextView? {
        guard let view else { return nil }
        if let editor = view as? NSTextView, editor.isEditable, editor.accessibilityIdentifier() == "composer-" + id { return editor }
        for child in view.subviews { if let value = editor(in: child, id: id) { return value } }
        return nil
    }
    private static func node(_ element: Any, id: String, depth: Int = 0) -> NSObject? {
        guard depth < 40, let value = element as? NSObject else { return nil }
        if value.responds(to: NSSelectorFromString("accessibilityIdentifier")), value.value(forKey: "accessibilityIdentifier") as? String == id,
           value.responds(to: NSSelectorFromString("accessibilityFrame")), let frame = (value.value(forKey: "accessibilityFrame") as? NSValue)?.rectValue, frame.width > 0, frame.height > 0 { return value }
        let children = value.responds(to: NSSelectorFromString("accessibilityChildren")) ? value.value(forKey: "accessibilityChildren") as? [Any] ?? [] : []
        for child in children { if let found = node(child, id: id, depth: depth + 1) { return found } }
        return nil
    }
    private static func enabled(_ element: Any, id: String) -> Bool? {
        guard let value = node(element, id: id), value.responds(to: NSSelectorFromString("isAccessibilityEnabled")) else { return nil }
        return value.value(forKey: "accessibilityEnabled") as? Bool
    }
    private static func press(_ window: NSWindow, id: String) throws {
        guard let value = node(window, id: id) else { throw MightyError("플러그인 \(id) 컨트롤이 없습니다.") }
        if let accessible = value as? any NSAccessibilityProtocol { _ = accessible.accessibilityPerformPress(); return }
        let selector = NSSelectorFromString("accessibilityPerformPress")
        guard value.responds(to: selector), let implementation = value.method(for: selector) else { throw MightyError("플러그인 버튼을 누를 수 없습니다.") }
        typealias Press = @convention(c) (AnyObject, Selector) -> Bool
        _ = unsafeBitCast(implementation, to: Press.self)(value, selector)
    }
}

private struct PluginComposerFixture: View {
    @ObservedObject var store: AppStore
    let sessionID: String
    var body: some View {
        if let session = store.snapshot.sessions.first(where: { $0.id == sessionID }) {
            SessionPaneView(session: session).environmentObject(store).preferredColorScheme(.dark)
        }
    }
}

@MainActor
private final class PluginFixtureService {
    static let successID = "fixture-success@fixture-market"
    static let failureID = "fixture-failure@fixture-market"
    static let otherID = "fixture-other@other-market"
    struct Install { let id: String; let scope: String; let workspaceID: String; let path: String }
    var installed = [ClaudeInstalledPlugin(pluginID: "fixture-installed@fixture-market", name: "Fixture Installed", marketplace: "fixture-market", version: "1.0.0", scope: "user", enabled: true, description: "격리된 설치 목록 예시입니다.")]
    var loads = 0
    var installs: [Install] = []
    var refreshes: [String] = []
    var pauseNext = false
    var waiting: Bool { continuation != nil }
    private var continuation: (id: UUID, value: CheckedContinuation<Void, Never>)?
    func snapshot() async -> ClaudePluginSnapshot {
        loads += 1
        return ClaudePluginSnapshot(status: "ready", detail: "진단용 카탈로그입니다. 실제 설치는 변경하지 않습니다.", cliVersion: "fixture", installed: installed,
            available: [ClaudeCatalogPlugin(id: Self.successID, name: "Fixture Success", description: "프로젝트 범위 설치 성공을 검증합니다.", marketplace: "fixture-market", version: "2.0.0", sourceKind: "github"),
                        ClaudeCatalogPlugin(id: Self.failureID, name: "Fixture Failure", description: "실패 메시지와 재시도 가능 상태를 검증합니다.", marketplace: "fixture-market", sourceKind: "github"),
                        ClaudeCatalogPlugin(id: Self.otherID, name: "Fixture Other", description: "마켓별 필터를 검증합니다.", marketplace: "other-market", sourceKind: "git")],
            marketplaces: [ClaudePluginMarketplace(name: "fixture-market", sourceKind: "github"), ClaudePluginMarketplace(name: "other-market", sourceKind: "git")], updatedAt: mightyTimestamp())
    }
    func install(id: String, scope: String, workspace: Workspace) async -> ClaudePluginOperationResult {
        installs.append(Install(id: id, scope: scope, workspaceID: workspace.id, path: workspace.path))
        if pauseNext {
            pauseNext = false
            let reservation = UUID()
            await withTaskCancellationHandler(operation: {
                await withCheckedContinuation { value in
                    if Task.isCancelled { value.resume() }
                    else { continuation = (reservation, value) }
                }
            }, onCancel: {
                Task { @MainActor [weak self] in self?.release(reservation: reservation) }
            })
        }
        if Task.isCancelled { return ClaudePluginOperationResult(status: "cancelled", detail: "검증 작업을 취소했습니다.") }
        if id == Self.failureID { return ClaudePluginOperationResult(status: "failed", detail: "Fixture 설치 실패: 실제 파일은 변경하지 않았습니다.", output: "fixture error") }
        installed.append(ClaudeInstalledPlugin(pluginID: id, name: "Fixture Success", marketplace: "fixture-market", version: "2.0.0", scope: scope, enabled: true, projectPath: scope == "user" ? nil : workspace.path))
        return ClaudePluginOperationResult(status: "succeeded", detail: "Fixture 설치 완료: 선택 범위의 목록을 갱신했습니다.")
    }
    func refresh(_ name: String) async -> ClaudePluginOperationResult {
        refreshes.append(name)
        return ClaudePluginOperationResult(status: "succeeded", detail: "Fixture 마켓을 갱신했습니다.")
    }
    func release(reservation: UUID? = nil) {
        guard reservation == nil || continuation?.id == reservation else { return }
        let pending = continuation; continuation = nil; pending?.value.resume()
    }
}
