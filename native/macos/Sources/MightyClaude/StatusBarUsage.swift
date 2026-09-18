import AppKit
import Combine
import MightyCore
import SwiftUI

/// Account quotas for the providers that have local AI panes, shown in the
/// bottom status bar. Automatic reads go through AccountUsageService and never
/// prompt; the popover's refresh is the interactive read that may.
@MainActor
final class AccountUsageStatusController: ObservableObject {
    @Published private(set) var snapshots: [String: AccountUsageSnapshot] = [:]
    @Published private(set) var providers: [String] = []
    @Published private(set) var refreshing = false
    /// Claude quota comes from the CLI's own rate-limit events and Mods
    /// readings. Reading the login Keychain is an explicit opt-in because the
    /// item belongs to Claude Code and every ad-hoc build asks again.
    @Published var claudeKeychainEnabled: Bool = UserDefaults.standard.bool(forKey: AccountUsageStatusController.keychainDefaultsKey) {
        didSet {
            UserDefaults.standard.set(claudeKeychainEnabled, forKey: Self.keychainDefaultsKey)
            if claudeKeychainEnabled { refresh(force: true, interactive: true) }
            else if snapshots["claude"]?.status == "permission" || snapshots["claude"]?.windows.isEmpty == true { snapshots.removeValue(forKey: "claude") }
        }
    }
    static let keychainDefaultsKey = "usage.claudeKeychain"
    private let service = AccountUsageService()
    private weak var store: AppStore?
    private var subscriptions = Set<AnyCancellable>()
    private var pollTask: Task<Void, Never>?
    private var refreshTask: Task<Void, Never>?
    private var stopped = false
    private var testing: Bool { ProcessInfo.processInfo.arguments.contains { $0.hasPrefix("--") && $0.contains("smoke-test") } }

    func configure(store: AppStore) {
        guard self.store == nil else { return }
        self.store = store
        store.$snapshot.sink { [weak self] snapshot in self?.update(snapshot) }.store(in: &subscriptions)
        guard !testing else { return }
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                self?.refresh()
                do { try await Task.sleep(for: .seconds(60)) } catch { break }
            }
        }
    }

    private func update(_ snapshot: AppSnapshot) {
        guard !stopped else { return }
        // Local AI panes only; a remote host never borrows this Mac's account.
        let local = Set(snapshot.workspaces.filter { $0.remote == nil }.map(\.id))
        var next: [String] = []
        for session in snapshot.sessions where session.kind != "shell" && local.contains(session.workspaceId) && ProviderOptions.ids.contains(session.provider) {
            if !next.contains(session.provider) { next.append(session.provider) }
        }
        next.sort { (ProviderOptions.ids.firstIndex(of: $0) ?? 0) < (ProviderOptions.ids.firstIndex(of: $1) ?? 0) }
        let added = Set(next).subtracting(providers)
        if next != providers { providers = next }
        // Limits reported by a running session are newer than a polled fetch.
        for session in snapshot.sessions where local.contains(session.workspaceId) {
            guard let measured = session.sessionUsage, measured.provider == session.provider,
                  let clean = SessionUsageSupport.normalized(measured), let rates = clean.rateLimits else { continue }
            let windows = rates.compactMap { rate -> AccountUsageWindow? in
                guard let percent = rate.percentUsed else { return nil }
                return AccountUsageWindow(kind: rate.kind, usedPercent: percent, resetsAt: rate.resetsAt)
            }
            guard !windows.isEmpty, let date = Self.date(clean.rateLimitsUpdatedAt),
                  date > (Self.date(snapshots[session.provider]?.fetchedAt) ?? .distantPast) else { continue }
            let stale = Date().timeIntervalSince(date) > 300
            snapshots[session.provider] = AccountUsageSnapshot(provider: session.provider, windows: windows, fetchedAt: clean.rateLimitsUpdatedAt,
                status: stale ? "stale" : "available", detail: stale ? "세션에서 마지막으로 받은 계정 한도입니다." : "실행 중인 세션에서 받은 계정 한도입니다.")
        }
        if !added.isEmpty, !testing { refresh() }
    }

    /// `interactive` is the user's click; only then may the Keychain dialog appear.
    func refresh(force: Bool = false, interactive: Bool = false) {
        guard !stopped, !testing, refreshTask == nil else { return }
        let targets = providers.filter { $0 != "claude" || claudeKeychainEnabled }
        guard !targets.isEmpty else { return }
        refreshing = true
        refreshTask = Task { [weak self] in
            guard let self else { return }
            defer { self.refreshing = false; self.refreshTask = nil }
            for provider in targets {
                guard !Task.isCancelled, !self.stopped else { break }
                let value = await self.service.read(provider: provider, force: force, interactive: interactive)
                guard !self.stopped else { break }
                if let existing = self.snapshots[provider], !existing.windows.isEmpty,
                   let old = Self.date(existing.fetchedAt), let new = Self.date(value.fetchedAt), old > new { continue }
                self.snapshots[provider] = value
            }
        }
    }

    func stop() {
        stopped = true
        pollTask?.cancel(); refreshTask?.cancel(); subscriptions.removeAll()
        Task { await service.shutdown() }
    }

    static func date(_ value: String?) -> Date? {
        guard let value else { return nil }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: value) { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: value)
    }
}

/// Compact chips that match the status bar's 10pt secondary text; the popover
/// carries the full breakdown, refresh and the Keychain permission action.
struct StatusBarUsageView: View {
    @ObservedObject var controller: AccountUsageStatusController
    @ViewState private var showsDetails = false

    var body: some View {
        if !controller.providers.isEmpty {
            Button { showsDetails.toggle() } label: {
                HStack(spacing: 6) {
                    ForEach(controller.providers, id: \.self) { provider in chip(provider) }
                }
            }
            .buttonStyle(.plain).help("계정 사용 한도 · 클릭해 상세 보기")
            .accessibilityLabel("계정 사용 한도").accessibilityIdentifier("statusbar-usage")
            .popover(isPresented: $showsDetails, arrowEdge: .bottom) { StatusBarUsageDetails(controller: controller) }
            Divider().frame(height: 12).padding(.horizontal, 4)
        }
    }

    private func chip(_ provider: String) -> some View {
        let usage = controller.snapshots[provider]
        return HStack(spacing: 5) {
            ProviderIcon(provider: provider, size: 9)
            if let usage, !usage.windows.isEmpty {
                ForEach(Array(Self.leading(usage.windows).enumerated()), id: \.offset) { _, window in
                    Text(Self.windowLabel(window.kind) + " " + Self.percent(window.usedPercent)).monospacedDigit()
                        .foregroundStyle(window.usedPercent >= 90 ? Color.orange : Color.secondary)
                }
            } else if usage?.status == "permission" {
                Text("Keychain 허용 필요").foregroundStyle(.orange)
            } else if provider == "claude", !controller.claudeKeychainEnabled {
                Text("실행 후 표시").foregroundStyle(.secondary)
            } else {
                Text(controller.refreshing ? "확인 중" : "—").foregroundStyle(.secondary)
            }
        }
        .font(.system(size: 10)).padding(.horizontal, 7).padding(.vertical, 3)
        .background(Palette.subtle, in: Capsule())
        .accessibilityIdentifier("statusbar-usage-\(provider)")
    }

    static func windowLabel(_ kind: String) -> String { RateLimitWindowLabel.label(kind) }
    static func percent(_ value: Double) -> String { "\(Int(value.rounded()))%" }
    /// The two windows worth a chip: the session window, then the weekly one.
    static func leading(_ windows: [AccountUsageWindow]) -> [AccountUsageWindow] {
        let ranked = windows.filter { $0.kind != "spend_limit" }.sorted { rank($0.kind) < rank($1.kind) }
        return Array(ranked.prefix(2))
    }
    private static func rank(_ kind: String) -> Int {
        switch RateLimitWindowLabel.label(kind) { case "세션": return 0; case "주간": return 1; default: return 2 }
    }
}

struct StatusBarUsageDetails: View {
    @ObservedObject var controller: AccountUsageStatusController

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Label("계정 사용 한도", systemImage: "chart.pie").font(.system(size: 13, weight: .semibold))
                Spacer()
                Button { controller.refresh(force: true, interactive: true) } label: {
                    if controller.refreshing { ProgressView().controlSize(.mini) } else { Image(systemName: "arrow.clockwise") }
                }
                .buttonStyle(.plain).disabled(controller.refreshing)
                .help("계정 한도 다시 확인").accessibilityLabel("계정 한도 새로고침")
            }
            ForEach(controller.providers, id: \.self) { provider in card(provider) }
            if controller.providers.contains("claude") {
                Toggle(isOn: $controller.claudeKeychainEnabled) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Claude 한도를 Keychain으로 직접 조회").font(.system(size: 11))
                        Text("끄면 Keychain 승인창이 열리지 않습니다. Claude 실행 때 CLI가 보고하는 한도만 표시합니다.").font(.system(size: 10)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                }
                .toggleStyle(.switch).controlSize(.mini)
                .accessibilityIdentifier("statusbar-usage-keychain-toggle")
            }
            Text("계정 한도는 같은 계정을 사용하는 앱·세션에서 공유됩니다. 자동 조회는 Keychain 승인창을 띄우지 않습니다.")
                .font(.system(size: 10)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
        .padding(16).frame(width: 320)
        .accessibilityIdentifier("statusbar-usage-details")
    }

    private func card(_ provider: String) -> some View {
        let usage = controller.snapshots[provider]
        return VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                ProviderIcon(provider: provider, size: 12)
                Text(ProviderOptions.label(provider)).font(.system(size: 12, weight: .semibold))
                Spacer()
                if let usage, usage.accountLabel != nil || usage.plan != nil {
                    Text([usage.accountLabel, usage.plan].compactMap { $0 }.joined(separator: " · ")).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            if let usage {
                ForEach(Array(usage.windows.enumerated()), id: \.offset) { _, window in
                    VStack(alignment: .leading, spacing: 3) {
                        HStack {
                            Text(StatusBarUsageView.windowLabel(window.kind) + (window.windowMinutes.map { $0 == 300 ? " (5시간)" : $0 == 10080 ? " (7일)" : "" } ?? ""))
                            Spacer()
                            Text(StatusBarUsageView.percent(window.usedPercent) + " 사용").monospacedDigit()
                        }.font(.system(size: 11))
                        ProgressView(value: min(1, max(0, window.usedPercent / 100))).tint(window.usedPercent >= 90 ? .orange : Palette.accent)
                        if let reset = window.resetsAt, let date = AccountUsageStatusController.date(reset) {
                            Text("초기화 \(date.formatted(date: .abbreviated, time: .shortened))").font(.system(size: 10)).foregroundStyle(.secondary)
                        }
                    }
                }
                if usage.status != "available" || usage.windows.isEmpty {
                    Text(usage.detail).font(.system(size: 10)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                if usage.status == "permission" {
                    Button { controller.refresh(force: true, interactive: true) } label: { Label("Keychain 접근 허용하고 조회", systemImage: "key") }
                        .controlSize(.small).disabled(controller.refreshing)
                        .accessibilityIdentifier("statusbar-usage-keychain-\(provider)")
                }
                if let fetched = usage.fetchedAt, let date = AccountUsageStatusController.date(fetched) {
                    Text("\(["error", "stale"].contains(usage.status) ? "마지막 확인값 · " : "")\(date.formatted(date: .omitted, time: .shortened)) 확인")
                        .font(.system(size: 9)).foregroundStyle(.tertiary)
                }
            } else if provider == "claude", !controller.claudeKeychainEnabled {
                Text("Claude를 한 번 실행하면 CLI가 보고한 세션·주간 한도가 여기에 표시됩니다.").font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            } else {
                Text(controller.refreshing ? "계정 사용 한도를 확인하고 있습니다…" : "아직 확인하지 않았습니다.").font(.system(size: 11)).foregroundStyle(.secondary)
            }
        }
        .padding(10).frame(maxWidth: .infinity, alignment: .leading)
        .background(Palette.subtle, in: RoundedRectangle(cornerRadius: 9))
    }
}
