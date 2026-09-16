import AppKit
import MightyCore
import SwiftUI

@MainActor
enum AccountIslandDiagnostics {
    /// Uses isolated fixtures; no account credentials, model calls, or provider requests.
    static func run(store: AppStore) async -> [String: Any] {
        var result: [String: Any] = ["passed": false, "providerRequestSent": false]
        guard ProcessInfo.processInfo.arguments.contains("--profile"), let workspace = store.activeWorkspace else { return result }
        let previous = store.snapshot
        let preferences = store.companion.preferences
        let keyWindow = NSApp.keyWindow
        let controller = store.sessionIsland
        let window = NSWindow(contentRect: NSRect(x: 160, y: 130, width: 660, height: 120), styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.title = "계정 아일랜드 검증"
        defer {
            controller.showsBottomPopover = false
            _ = controller.showDiagnosticPanel(false)
            window.orderOut(nil); window.close()
            store.snapshot = previous
            controller.setDiagnosticUsage([:])
            store.companion.preferences = preferences
            keyWindow?.makeKeyAndOrderFront(nil)
        }
        do {
            result["compactPolicy"] = try verifyPolicy()
            let display = NSRect(x: 0, y: 0, width: 1512, height: 982)
            let left = NSRect(x: 0, y: 944, width: 656, height: 38)
            let right = NSRect(x: 856, y: 944, width: 656, height: 38)
            let notch = CameraIslandGeometry.make(screen: display, safeTop: 38, left: left, right: right)
            guard notch.frame.maxY == display.maxY, notch.frame.height == 38, notch.gap == 200,
                  notch.frame.minX + notch.leftRect.maxX == left.maxX,
                  notch.frame.minX + notch.rightRect.minX == right.minX else { throw MightyError("아일랜드가 카메라 양옆에 맞지 않습니다.") }
            let external = NSRect(x: -1920, y: 100, width: 1920, height: 1080)
            let plain = CameraIslandGeometry.make(screen: external, safeTop: 0, left: nil, right: nil)
            let fallback = CameraIslandGeometry.make(screen: display, safeTop: 38, left: nil, right: nil)
            let narrow = CameraIslandGeometry.make(screen: NSRect(x: 50, y: -100, width: 260, height: 400), safeTop: 38, left: nil, right: nil)
            guard external.contains(plain.frame), plain.frame.maxY == external.maxY, plain.gap == 24,
                  fallback.gap == 200, fallback.frame.maxY == display.maxY,
                  narrow.frame.minX >= 50, narrow.frame.maxX <= 310 else { throw MightyError("외부 화면 또는 카메라 전환 배치가 잘못됐습니다.") }
            result["cameraGeometryFixtures"] = true
            let remote = Workspace(id: "island-remote-workspace", name: "Remote fixture", path: "/fixture",
                remote: RemoteWorkspaceReference(connectionId: "island-host", workspaceId: "remote-workspace", hostName: "다른 Mac"))
            var sessions = ProviderOptions.ids.map { RunSession(id: "island-\($0)", workspaceId: workspace.id, title: $0, provider: $0) }
            guard let claude = sessions.firstIndex(where: { $0.provider == "claude" }) else { throw MightyError("Claude fixture missing") }
            if let codex = sessions.firstIndex(where: { $0.provider == "codex" }) { sessions[codex].status = "running" }
            sessions[claude].sessionUsage = SessionUsage(provider: "claude", source: "fixture", contextUsedTokens: 198_000, contextWindowTokens: 200_000)
            sessions.append(RunSession(id: "island-remote-claude", workspaceId: remote.id, title: "Remote Claude", provider: "claude"))
            store.snapshot = AppSnapshot(workspaces: [workspace, remote], sessions: sessions, activeWorkspaceId: workspace.id, activeSessionId: sessions[claude].id)
            let now = mightyTimestamp()
            controller.setDiagnosticUsage([
                "claude": AccountUsageSnapshot(provider: "claude", accountLabel: "fixture@example.invalid", plan: "Max", windows: [
                    AccountUsageWindow(kind: "five_hour", usedPercent: 24, resetsAt: now),
                    AccountUsageWindow(kind: "seven_day", usedPercent: 67, resetsAt: now)
                ], fetchedAt: now, status: "available", detail: "Fixture"),
                "codex": AccountUsageSnapshot(provider: "codex", plan: "Pro", windows: [AccountUsageWindow(kind: "primary", usedPercent: 38), AccountUsageWindow(kind: "secondary", usedPercent: 52)], fetchedAt: now, status: "stale", detail: "마지막 확인값입니다."),
                "gemini": AccountUsageSnapshot(provider: "gemini", detail: "현재 CLI는 계정 한도를 제공하지 않습니다.")
            ])
            guard controller.accounts.count == 4,
                  controller.accounts.first(where: { $0.id == "claude|local" })?.usage?.windows.first?.usedPercent == 24,
                  controller.accounts.first(where: { $0.remote })?.usage == nil,
                  controller.accounts.first(where: { $0.provider == "gemini" })?.usage?.windows.isEmpty == true else { throw MightyError("계정·컨텍스트 또는 원격 계정 경계가 섞였습니다.") }
            result["contextExcludedFromAccountQuota"] = true
            result["localQuotaNeverAssignedToRemote"] = true
            result["unsupportedQuotaNotZero"] = true
            var remoteUsage = SessionUsage(provider: "claude", source: "claude.mods", rateLimits: [SessionRateLimit(kind: "five_hour", percentUsed: 81)])
            remoteUsage.rateLimitsUpdatedAt = mightyTimestamp()
            store.snapshot.sessions[sessions.count - 1].sessionUsage = remoteUsage
            guard controller.accounts.first(where: { $0.remote })?.usage?.windows.first?.usedPercent == 81,
                  controller.accounts.first(where: { $0.id == "claude|local" })?.usage?.windows.first?.usedPercent == 24 else { throw MightyError("원격 세션의 직접 한도를 연결하지 못했습니다.") }
            result["remoteDirectQuota"] = true
            guard controller.compactAccounts.map(\.provider) == ["claude", "codex"],
                  controller.compactAccounts.first?.id == "claude|local", controller.accounts.count == 4 else {
                throw MightyError("요약 아일랜드의 프로바이더 중복 제거 또는 원본 계정 보존에 실패했습니다.")
            }
            result["compactUsesTwoDistinctProviders"] = true
            result["allHostAccountsRemainInDetails"] = true
            window.contentView = NSHostingView(rootView: HStack {
                Text("작업 중 1").font(.system(size: 11))
                Spacer()
                AccountIslandButton(controller: controller)
                Image(systemName: "pawprint")
            }.padding(16).frame(maxWidth: .infinity, maxHeight: .infinity))
            window.makeKeyAndOrderFront(nil)
            try await store.waitForSmoke(timeout: 3) { node(window, identifier: "account-island-bottom") != nil }
            guard let bottom = node(window, identifier: "account-island-bottom") else { throw MightyError("하단 아일랜드가 없습니다.") }
            press(bottom)
            try await store.waitForSmoke(timeout: 3) { controller.showsBottomPopover && popup() != nil }
            if let details = popup() {
                result["bottomDetailsScreenshot"] = try store.captureSmokeWindow(details, filename: "account-island-details.png").path
                guard let content = details.contentView, let clip = scrollClip(in: content),
                      let card = node(details, identifier: "account-quota-claude|local"),
                      card.responds(to: NSSelectorFromString("accessibilityFrame")),
                      let rect = (card.value(forKey: "accessibilityFrame") as? NSValue)?.rectValue else { throw MightyError("계정 카드의 표시 영역을 확인하지 못했습니다.") }
                let visible = details.convertToScreen(clip.convert(clip.bounds, to: nil))
                result["accountCardFrame"] = NSStringFromRect(rect)
                result["accountViewportFrame"] = NSStringFromRect(visible)
                guard rect.minX >= visible.minX - 1, rect.maxX <= visible.maxX + 1 else { throw MightyError("계정 카드가 팝업의 가로 표시 영역을 벗어났습니다.") }
                result["accountCardWithinViewport"] = true
            }
            result["bottomPopover"] = true
            controller.showsBottomPopover = false
            try await store.waitForSmoke(timeout: 3) { popup() == nil }
            result["bottomScreenshot"] = try store.captureSmokeWindow(window, filename: "account-island-bottom.png").path
            store.companion.preferences.showsSessionIsland = true
            guard let panel = controller.showDiagnosticPanel(true) else { throw MightyError("상단 아일랜드가 없습니다.") }
            try await store.waitForSmoke(timeout: 3) { panel.isVisible && node(panel, identifier: "account-island-floating") != nil }
            guard let screen = NSScreen.screens.first(where: { $0.safeAreaInsets.top > 0 }) ?? NSScreen.main,
                  abs(panel.frame.maxY - screen.frame.maxY) < 1,
                  panel.level.rawValue > NSWindow.Level.statusBar.rawValue else { throw MightyError("아일랜드가 화면 맨 위 카메라 위치에 붙지 않았습니다.") }
            result["attachedToScreenTop"] = true
            result["actualCameraGap"] = panel.islandGeometry.gap
            result["islandScreenFrame"] = NSStringFromRect(screen.frame)
            result["islandFrame"] = NSStringFromRect(panel.frame)
            result["topScreenshot"] = try store.captureSmokeWindow(panel, filename: "account-island-top.png").path
            guard let capsule = node(panel, identifier: "account-island-floating") else { throw MightyError("상단 아일랜드 버튼이 없습니다.") }
            press(capsule)
            try await store.waitForSmoke(timeout: 3) { popup() != nil }
            result["topPopover"] = true
            panel.closeDetails()
            let multiProviderSnapshot = store.snapshot
            store.snapshot.sessions = store.snapshot.sessions.filter { $0.provider == "claude" }
            try await store.waitForSmoke(timeout: 3) { controller.compactAccounts.count == 1 && controller.accounts.count == 2 }
            guard controller.compactAccounts.first?.id == "claude|local" else { throw MightyError("단일 프로바이더 대표 계정이 잘못됐습니다.") }
            result["singleProviderTopScreenshot"] = try store.captureSmokeWindow(panel, filename: "account-island-single-provider-top.png").path
            result["singleProviderBottomScreenshot"] = try store.captureSmokeWindow(window, filename: "account-island-single-provider-bottom.png").path
            result["singleProviderAppearsOnce"] = true
            store.snapshot = multiProviderSnapshot
            store.companion.preferences.showsSessionIsland = false
            try await store.waitForSmoke(timeout: 3) { !panel.isVisible }
            guard node(window, identifier: "account-island-bottom") != nil else { throw MightyError("상단 숨김이 하단까지 숨겼습니다.") }
            result["floatingPreferenceIndependentOfBottom"] = true
            result["passed"] = true
        } catch { result["error"] = error.localizedDescription }
        return result
    }

    private static func verifyPolicy() throws -> [String: Any] {
        let local = Workspace(id: "policy-local", name: "Local", path: "/fixture")
        let remote = Workspace(id: "policy-remote", name: "Remote", path: "/remote", remote: RemoteWorkspaceReference(connectionId: "remote", workspaceId: "there", hostName: "Remote host"))
        let localUsage = AccountUsageSnapshot(provider: "claude", accountLabel: "local fixture", windows: [
            AccountUsageWindow(kind: "session", usedPercent: 24), AccountUsageWindow(kind: "weekly", usedPercent: 67),
            AccountUsageWindow(kind: "Sonnet", usedPercent: 92)], status: "available")
        let remoteUsage = AccountUsageSnapshot(provider: "claude", accountLabel: "remote fixture", windows: [AccountUsageWindow(kind: "session", usedPercent: 81)], status: "available")
        let accounts = [
            IslandAccount(id: "claude|local", provider: "claude", host: "Local", remote: false, sessionCount: 1, usage: localUsage),
            IslandAccount(id: "claude|remote", provider: "claude", host: "Remote host", remote: true, sessionCount: 1, usage: remoteUsage),
            IslandAccount(id: "codex|local", provider: "codex", host: "Local", remote: false, sessionCount: 1),
            IslandAccount(id: "gemini|local", provider: "gemini", host: "Local", remote: false, sessionCount: 1)]
        var sessions = [
            RunSession(id: "local", workspaceId: local.id, title: "Local", createdAt: "2026-01-01T00:00:00Z"),
            RunSession(id: "remote", workspaceId: remote.id, title: "Remote", createdAt: "2026-01-02T00:00:00Z"),
            RunSession(id: "codex", workspaceId: local.id, title: "Codex", provider: "codex", createdAt: "2026-01-03T00:00:00Z"),
            RunSession(id: "gemini", workspaceId: local.id, title: "Gemini", provider: "gemini", createdAt: "2026-01-04T00:00:00Z")]
        var snapshot = AppSnapshot(workspaces: [local, remote], sessions: sessions, activeWorkspaceId: local.id, activeSessionId: "local")
        let recent = IslandSummaryPolicy.representatives(accounts: accounts, snapshot: snapshot)
        guard recent.map(\.id) == ["claude|local", "gemini|local"] else { throw MightyError("활성·최근 사용 프로바이더 우선순위가 잘못됐습니다.") }
        sessions[2].status = "running"; snapshot.sessions = sessions
        guard IslandSummaryPolicy.representatives(accounts: accounts, snapshot: snapshot).map(\.id) == ["claude|local", "codex|local"] else { throw MightyError("실행 중 프로바이더를 우선하지 않았습니다.") }
        snapshot.activeSessionId = "remote"
        let remoteSummary = IslandSummaryPolicy.representatives(accounts: accounts, snapshot: snapshot)
        guard remoteSummary.first?.id == "claude|remote", remoteSummary.first?.usage == remoteUsage,
              accounts[0].usage == localUsage, accounts.count == 4 else { throw MightyError("대표 계정을 고르며 원격·로컬 한도 또는 계정 신원을 합쳤습니다.") }
        snapshot.sessions = Array(sessions.prefix(2))
        guard IslandSummaryPolicy.representatives(accounts: accounts, snapshot: snapshot).map(\.id) == ["claude|remote"] else { throw MightyError("단일 프로바이더를 중복 표시했습니다.") }
        guard IslandSummaryPolicy.windows(localUsage).map(\.kind) == ["weekly", "Sonnet"] else { throw MightyError("사용률이 높은 두 한도를 선택하지 않았습니다.") }
        let reordered = AccountUsageSnapshot(provider: "claude", windows: [AccountUsageWindow(kind: "weekly", usedPercent: 90), AccountUsageWindow(kind: "session", usedPercent: 80)])
        guard IslandSummaryPolicy.windows(reordered).map(\.kind) == ["session", "weekly"] else { throw MightyError("선택된 한도가 세션·주간 순서로 표시되지 않습니다.") }
        let tied = AccountUsageSnapshot(provider: "claude", windows: [AccountUsageWindow(kind: "Sonnet", usedPercent: 80), AccountUsageWindow(kind: "weekly", usedPercent: 80), AccountUsageWindow(kind: "session", usedPercent: 80)])
        guard IslandSummaryPolicy.windows(tied).map(\.kind) == ["session", "weekly"] else { throw MightyError("한도 동률 우선순위가 잘못됐습니다.") }
        let invalid = AccountUsageSnapshot(provider: "gemini", windows: [AccountUsageWindow(kind: "session", usedPercent: .nan), AccountUsageWindow(kind: "weekly", usedPercent: -1)])
        guard IslandSummaryPolicy.windows(invalid).isEmpty, IslandSummaryPolicy.lines(accounts[3]) == ["계정 한도 —"] else { throw MightyError("미측정 한도를 0으로 표시했습니다.") }
        return ["passed": true, "singleProviderOnce": true, "maximumTwoUsedProviders": true, "activeThenRunningThenRecent": true,
                "remoteIdentityUnmerged": true, "mostConstrainedTwoWindows": true, "sessionWeeklyOtherDisplayOrder": true, "unknownNotZero": true]
    }

    private static func node(_ element: Any, identifier: String, depth: Int = 0) -> NSObject? {
        guard depth < 45, let object = element as? NSObject else { return nil }
        func value(_ key: String) -> Any? { object.responds(to: NSSelectorFromString(key)) ? object.value(forKey: key) : nil }
        if value("accessibilityIdentifier") as? String == identifier { return object }
        for child in value("accessibilityChildren") as? [Any] ?? [] {
            if let found = node(child, identifier: identifier, depth: depth + 1) { return found }
        }
        return nil
    }
    private static func popup() -> NSWindow? { NSApp.windows.first { $0.isVisible && node($0, identifier: "account-island-details") != nil } }
    private static func scrollClip(in view: NSView) -> NSClipView? {
        if let scroll = view as? NSScrollView, !scroll.isHiddenOrHasHiddenAncestor { return scroll.contentView }
        for child in view.subviews { if let clip = scrollClip(in: child) { return clip } }
        return nil
    }
    private static func press(_ object: NSObject) {
        let selector = NSSelectorFromString("accessibilityPerformPress")
        guard object.responds(to: selector), let implementation = object.method(for: selector) else { return }
        typealias Press = @convention(c) (AnyObject, Selector) -> Bool
        _ = unsafeBitCast(implementation, to: Press.self)(object, selector)
    }
}
