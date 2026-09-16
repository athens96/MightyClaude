import AppKit
import Darwin
import MightyCore
import SwiftUI

extension AppStore {
    /// Only the isolated profile flag reaches this diagnostic. Every batch
    /// supplies a fake operation; the real update button is never pressed.
    func runCLIUpdateSmokeTest() async {
        let args = ProcessInfo.processInfo.arguments
        guard args.contains("--profile") else { error = "CLI 업데이트 검증은 임시 --profile이 필요합니다."; return }
        var result: [String: Any] = ["passed": false, "realCLIUpdateInvoked": false, "aiRequestSent": false]
        var stage = "default-off"
        let fake = CLIUpdateSmokeOperation()
        let previousWindow = NSApp.keyWindow
        let window = NSWindow(contentRect: NSRect(x: 180, y: 160, width: 620, height: 510), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = "CLI 업데이트 검증"
        window.isReleasedWhenClosed = false
        defer { window.orderOut(nil); window.close(); previousWindow?.makeKeyAndOrderFront(nil) }
        do {
            try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true)
            func require(_ condition: Bool, _ message: String) throws {
                guard condition else { throw MightyError(message) }
            }
            let update: (String) async -> CLIUpdateResult = { provider in await fake.run(provider) }
            try require(snapshot.autoUpdateCLIs != true && !automaticCLIUpdateAttempted, "새 프로필의 자동 업데이트가 꺼져 있지 않습니다.")
            beginAutomaticCLIUpdatesIfNeeded(update: update)
            try require(!isUpdatingCLIs && fake.calls.isEmpty && !automaticCLIUpdateAttempted, "자동 업데이트가 꺼진 상태에서 실행되었습니다.")
            result["defaultOff"] = true

            stage = "read-only-installation-detection"
            let inspector = CLIUpdateService()
            var installations: [[String: Any]] = []
            for provider in ProviderOptions.ids {
                let installation = await inspector.inspect(provider: provider)
                installations.append(["provider": provider, "method": installation.method,
                                      "canUpdate": installation.canUpdate, "version": installation.version.map { $0 as Any } ?? NSNull()])
            }
            result["installations"] = installations

            window.contentView = NSHostingView(rootView: Form { CLIUpdateSettingsSection() }
                .formStyle(.grouped).environmentObject(self).preferredColorScheme(.dark))
            window.makeKeyAndOrderFront(nil)
            try await waitForSmoke(timeout: 3) {
                self.cliUpdateSmokeElement(window, identifier: "cli-auto-update") != nil && self.cliUpdateSmokeElement(window, identifier: "cli-update-now") != nil
            }
            try require(cliUpdateSmokeEnabled(window, identifier: "cli-update-now") == true, "수동 업데이트 버튼이 활성화되지 않았습니다.")
            result["settingsControlsVisible"] = true
            stage = "automatic-once"
            guard let toggle = cliUpdateSmokeElement(window, identifier: "cli-auto-update"), cliUpdateSmokePressToggle(toggle) else { throw MightyError("자동 업데이트 토글을 누르지 못했습니다.") }
            try await waitForSmoke(timeout: 3) { self.snapshot.autoUpdateCLIs == true }
            result["nativeToggleOn"] = true
            beginAutomaticCLIUpdatesIfNeeded(update: update)
            try await waitForSmoke(timeout: 30) { !self.isUpdatingCLIs }
            try require(automaticCLIUpdateAttempted && fake.calls == ProviderOptions.ids, "시작 시 자동 업데이트가 공급자별 한 번 실행되지 않았습니다.")
            beginAutomaticCLIUpdatesIfNeeded(update: update)
            try require(!isUpdatingCLIs && fake.calls == ProviderOptions.ids, "한 앱 실행에서 자동 업데이트가 반복되었습니다.")
            try require(cliUpdateResults["claude"]?.status == "failed" && cliUpdateResults["codex"]?.status == "updated" && cliUpdateResults["gemini"]?.status == "current", "한 CLI의 실패로 다른 CLI 업데이트가 중단되었습니다.")
            result["automaticOncePerStartup"] = true
            result["failureContinuesOtherProviders"] = true

            stage = "manual-off-and-dedup"
            guard let toggle = cliUpdateSmokeElement(window, identifier: "cli-auto-update"), cliUpdateSmokePressToggle(toggle) else { throw MightyError("자동 업데이트 토글을 끄지 못했습니다.") }
            try await waitForSmoke(timeout: 3) { self.snapshot.autoUpdateCLIs == false }
            result["nativeToggleOff"] = true
            fake.calls.removeAll(); fake.pauseFirst = true
            startCLIUpdates(update: update)
            try await waitForSmoke(timeout: 3) { fake.isWaiting && self.updatingCLI == "claude" }
            startCLIUpdates(update: update)
            try require(fake.calls == ["claude"] && isUpdatingCLIs, "수동 업데이트가 중복 시작되었습니다.")
            try await waitForSmoke(timeout: 3) { self.cliUpdateSmokeEnabled(window, identifier: "cli-update-now") == false }
            result["updateButtonDisabledDuringBatch"] = true
            result["manualWorksWhenAutomaticOff"] = snapshot.autoUpdateCLIs == false
            result["progressScreenshot"] = try captureSmokeWindow(window, filename: "cli-update-progress.png").path

            // The deliberately invalid model is a second safety boundary: even
            // if the update guard regresses, validation must reject this fixture
            // before any provider process or paid model request can start.
            let directory = dataDirectory.appendingPathComponent("CLI Update Fixture", isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let workspace = try await repository.approveWorkspace(Workspace(name: "CLI Update Fixture", path: directory.path))
            let session = RunSession(id: "cli-update-blocked-run", workspaceId: workspace.id, title: "CLI update guard", model: "invalid\0model")
            snapshot.workspaces.append(workspace); snapshot.sessions.append(session)
            drafts[session.id] = "격리 검증: 실행하지 않는 요청"
            let originalDraft = drafts[session.id]
            let blocked = runBlockedReason(session)
            try require(blocked?.contains("업데이트") == true, "업데이트 중 새 작업 차단 사유가 없습니다.")
            error = nil
            submit(session.id)
            try require(error == blocked && drafts[session.id] == originalDraft && snapshot.sessions.first(where: { $0.id == session.id })?.status == "idle" && snapshot.sessions.first(where: { $0.id == session.id })?.logs.isEmpty == true, "업데이트 중 요청이 실행되거나 초안이 소실되었습니다.")
            result["newRunBlockedWhileUpdating"] = true
            error = nil
            fake.release()
            try await waitForSmoke(timeout: 30) { !self.isUpdatingCLIs }
            try require(fake.calls == ProviderOptions.ids, "중복 누름으로 추가 업데이트가 실행되었습니다.")
            result["repeatedPressDeduplicated"] = true
            try await waitForSmoke(timeout: 3) {
                self.cliUpdateSmokeEnabled(window, identifier: "cli-update-now") == true && ProviderOptions.ids.allSatisfy { self.cliUpdateSmokeElement(window, identifier: "cli-update-result-\($0)") != nil }
            }
            result["resultsVisible"] = true
            result["screenshot"] = try captureSmokeWindow(window, filename: "cli-update-settings.png").path

            stage = "running-provider-skip"
            guard let index = snapshot.sessions.firstIndex(where: { $0.id == session.id }) else { throw MightyError("실행 중 제외 검증 세션이 없습니다.") }
            snapshot.sessions[index].status = "running"
            fake.calls.removeAll()
            startCLIUpdates(update: update)
            try await waitForSmoke(timeout: 30) { !self.isUpdatingCLIs }
            try require(fake.calls == ProviderOptions.ids.filter { $0 != "claude" } && cliUpdateResults["claude"]?.status == "skipped", "실행 중인 로컬 CLI를 업데이트했습니다.")
            result["runningProviderSkipped"] = true
            snapshot.sessions[index].status = "idle"

            stage = "shared-host-skip"
            fake.calls.removeAll()
            remoteState.host.enabled = true // State-only fixture; no listener is opened.
            startCLIUpdates(update: update)
            try await waitForSmoke(timeout: 30) { !self.isUpdatingCLIs }
            try require(fake.calls.isEmpty && ProviderOptions.ids.allSatisfy { cliUpdateResults[$0]?.status == "skipped" }, "원격 공유 중인 CLI를 업데이트했습니다.")
            result["remoteHostSharingSkipped"] = true
            remoteState.host.enabled = false
            result["passed"] = true
        } catch {
            result["failedStage"] = stage
            result["error"] = error.localizedDescription
            if let screenshot = try? captureSmokeWindow(window, filename: "cli-update-failure.png") { result["failureScreenshot"] = screenshot.path }
        }
        fake.release()
        remoteState.host.enabled = false
        cliUpdateTask?.cancel()
        await cliUpdateTask?.value
        do {
            try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true)
            try JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys])
                .write(to: dataDirectory.appendingPathComponent("cli-update-smoke-result.json"), options: .atomic)
        } catch { result["passed"] = false; NSLog("CLI update smoke report: %@", error.localizedDescription) }
        if args.contains("--smoke-exit") { await shutdown(); Darwin.exit(result["passed"] as? Bool == true ? 0 : 1) }
    }

    private func cliUpdateSmokeElement(_ element: Any, identifier: String, depth: Int = 0) -> NSObject? {
        guard depth < 40, let object = element as? NSObject else { return nil }
        if object.responds(to: NSSelectorFromString("accessibilityIdentifier")), object.value(forKey: "accessibilityIdentifier") as? String == identifier,
           object.responds(to: NSSelectorFromString("accessibilityFrame")), let frame = (object.value(forKey: "accessibilityFrame") as? NSValue)?.rectValue, frame.width > 0, frame.height > 0 { return object }
        let children = object.responds(to: NSSelectorFromString("accessibilityChildren")) ? object.value(forKey: "accessibilityChildren") as? [Any] ?? [] : []
        for child in children { if let found = cliUpdateSmokeElement(child, identifier: identifier, depth: depth + 1) { return found } }
        return nil
    }

    private func cliUpdateSmokeEnabled(_ element: Any, identifier: String) -> Bool? {
        guard let object = cliUpdateSmokeElement(element, identifier: identifier), object.responds(to: NSSelectorFromString("isAccessibilityEnabled")) else { return nil }
        return object.value(forKey: "accessibilityEnabled") as? Bool
    }

    private func cliUpdateSmokePressToggle(_ object: NSObject) -> Bool {
        // Only the preference toggle is allowed through this native action.
        guard object.responds(to: NSSelectorFromString("accessibilityIdentifier")), object.value(forKey: "accessibilityIdentifier") as? String == "cli-auto-update" else { return false }
        // SwiftUI can report false even after its action changed the binding.
        // Return whether dispatch was possible, then let each caller assert the
        // actual on/off transition. Never retry with a second click here.
        if let accessible = object as? any NSAccessibilityProtocol {
            _ = accessible.accessibilityPerformPress()
            return true
        }
        let selector = NSSelectorFromString("accessibilityPerformPress")
        guard object.responds(to: selector), let implementation = object.method(for: selector) else { return false }
        typealias Press = @convention(c) (AnyObject, Selector) -> Bool
        _ = unsafeBitCast(implementation, to: Press.self)(object, selector)
        return true
    }
}

@MainActor
private final class CLIUpdateSmokeOperation {
    var calls: [String] = []
    var pauseFirst = false
    var isWaiting: Bool { continuation != nil }
    private var continuation: CheckedContinuation<Void, Never>?

    func run(_ provider: String) async -> CLIUpdateResult {
        calls.append(provider)
        if pauseFirst {
            pauseFirst = false
            await withCheckedContinuation { continuation = $0 }
        }
        let status = provider == "claude" ? "failed" : provider == "codex" ? "updated" : "current"
        return CLIUpdateResult(provider: provider, status: status, beforeVersion: "fixture-1", afterVersion: status == "updated" ? "fixture-2" : "fixture-1", method: "fixture", detail: "격리 진단 결과입니다. 실제 CLI 설치는 변경하지 않았습니다.", output: "")
    }

    func release() { let pending = continuation; continuation = nil; pending?.resume() }
}
