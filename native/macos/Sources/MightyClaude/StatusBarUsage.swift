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
    private let service: AccountUsageService
    /// The default is the app's own service. A smoke run injects one built on a
    /// fixture clock and a fake transport, so the rows render from fixture data.
    init(service: AccountUsageService = AccountUsageService()) { self.service = service }
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
        let local = Set(snapshot.workspaces.map(\.id))
        var next: [String] = []
        for session in snapshot.sessions where session.kind != "shell" && !FilePaneKind.isFilePane(session.kind) && local.contains(session.workspaceId) && ProviderOptions.ids.contains(session.provider) {
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
            // A session's rate_limit_event carries no limit-reset data, so the rows
            // the service read stay exactly as they were.
            snapshots[session.provider] = AccountUsageSnapshot(provider: session.provider, windows: windows,
                resets: snapshots[session.provider]?.resets ?? [], fetchedAt: clean.rateLimitsUpdatedAt,
                status: stale ? "stale" : "available", detail: stale ? L("windows.accountUsage.detailSessionReportedStale") : L("windows.accountUsage.detailSessionReported"))
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

    /// Reads through the injected service and publishes the result, so the
    /// popover's limit-reset rows render from exactly what that service returned.
    func loadUsageResetRowsForSmoke() async -> AccountUsageSnapshot {
        let snapshot = await service.read(provider: "claude", force: true)
        if !providers.contains("claude") { providers.append("claude") }
        snapshots["claude"] = snapshot
        return snapshot
    }

    /// Smoke: constructs a controller from an injected service built on a
    /// fixture clock and a fake transport that only ever sees GET, so
    /// fakeTransportPostCount is 0. The rows render irrespective of the
    /// direct-lookup switch, the preference is never written, and the returned
    /// dict is what the smoke runner writes to JSON.
    @MainActor
    static func runUsageResetSmoke() async -> [String: Any] {
        let savedPreference = UserDefaults.standard.bool(forKey: keychainDefaultsKey)
        let (service, transport) = AccountResetSmoke.fixture()
        let controller = AccountUsageStatusController(service: service)
        let snapshot = await controller.loadUsageResetRowsForSmoke()
        let result = AccountResetSmoke.score(snapshot, transport: transport)
        let fakeTransportPostCount = result.fakeTransportPostCount
        // Restore: stop the injected service and leave the switch exactly as found.
        controller.stop()
        let preference = UserDefaults.standard.bool(forKey: keychainDefaultsKey)
        let preferenceUnchanged = preference == savedPreference
        if preference != savedPreference { UserDefaults.standard.set(savedPreference, forKey: keychainDefaultsKey) }
        return [
            "passed": result.passed && fakeTransportPostCount == 0 && preferenceUnchanged,
            "fakeTransportPostCount": fakeTransportPostCount,
            "usageReset.cedar_ember": result.cedarEmberState,
            "usageReset.juniper_tide": result.juniperTideState,
            "usageReset.requests": result.requests,
            "usageReset.lines": result.lines,
            "preferenceUnchanged": preferenceUnchanged,
        ]
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
            .buttonStyle(.plain).help(L("windows.accountUsage.chipsTooltip"))
            .accessibilityLabel(L("windows.accountUsage.title")).accessibilityIdentifier("statusbar-usage")
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
                        .foregroundStyle(window.usedPercent >= 90 ? Palette.waitText : Color.secondary)
                }
            } else if usage?.status == "permission" {
                Text(L("usage.keychain.needed")).foregroundStyle(Palette.waitText)
            } else if provider == "claude", !controller.claudeKeychainEnabled {
                Text(L("windows.accountUsage.chipBeforeFirstRun")).foregroundStyle(.secondary)
            } else {
                Text(controller.refreshing ? L("windows.accountUsage.chipChecking") : "—").foregroundStyle(.secondary)
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
        // Ranked by what the window is, not by its label in the current language.
        switch RateLimitWindowLabel.label(kind) {
        case RateLimitWindowLabel.label("session"): return 0
        case RateLimitWindowLabel.label("weekly"): return 1
        default: return 2
        }
    }
}

struct StatusBarUsageDetails: View {
    @ObservedObject var controller: AccountUsageStatusController

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Label(L("windows.accountUsage.title"), systemImage: "chart.pie").font(.system(size: 13, weight: .semibold))
                Spacer()
                Button { controller.refresh(force: true, interactive: true) } label: {
                    if controller.refreshing { ProgressView().controlSize(.mini) } else { Image(systemName: "arrow.clockwise") }
                }
                .buttonStyle(.plain).disabled(controller.refreshing)
                .help(L("windows.accountUsage.refreshTooltip")).accessibilityLabel(L("windows.accountUsage.refreshAccessibilityLabel"))
            }
            ForEach(controller.providers, id: \.self) { provider in card(provider) }
            if controller.providers.contains("claude") {
                Toggle(isOn: $controller.claudeKeychainEnabled) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(L("usage.keychain.toggle")).font(.system(size: 11))
                        Text(L("usage.keychain.toggleDescription")).font(.system(size: 10)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                }
                .toggleStyle(.switch).controlSize(.mini)
                .accessibilityIdentifier("statusbar-usage-keychain-toggle")
            }
            Text(L("usage.sharedNoteMac"))
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
                if ProviderOptions.isBeta(provider) { BetaBadge() }
                Spacer()
                if let usage, usage.accountLabel != nil || usage.plan != nil {
                    Text([usage.accountLabel, usage.plan].compactMap { $0 }.joined(separator: " · ")).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            if let usage {
                ForEach(Array(usage.windows.enumerated()), id: \.offset) { _, window in
                    VStack(alignment: .leading, spacing: 3) {
                        HStack {
                            Text(StatusBarUsageView.windowLabel(window.kind) + (window.windowMinutes.map { $0 == 300 ? L("windows.accountUsage.windowFiveHourSuffix") : $0 == 10080 ? L("windows.accountUsage.windowSevenDaySuffix") : "" } ?? ""))
                            Spacer()
                            Text(L("usage.usedSuffix", ["percent": StatusBarUsageView.percent(window.usedPercent)])).monospacedDigit()
                        }.font(.system(size: 11))
                        ProgressView(value: min(1, max(0, window.usedPercent / 100))).tint(window.usedPercent >= 90 ? Palette.waitText : Palette.accent)
                        if let reset = window.resetsAt, let date = AccountUsageStatusController.date(reset) {
                            Text(L("windows.accountUsage.resetTemplate", ["date": date.formatted(date: .abbreviated, time: .shortened)])).font(.system(size: 10)).foregroundStyle(.secondary)
                        }
                    }
                }
                if usage.status != "available" || usage.windows.isEmpty {
                    Text(usage.detail).font(.system(size: 10)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                if usage.status == "permission" {
                    Button { controller.refresh(force: true, interactive: true) } label: { Label(L("usage.keychain.allowAndCheck"), systemImage: "key") }
                        .controlSize(.small).disabled(controller.refreshing)
                        .accessibilityIdentifier("statusbar-usage-keychain-\(provider)")
                }
                resetSection(provider: provider, usage: usage)
                if let fetched = usage.fetchedAt, let date = AccountUsageStatusController.date(fetched) {
                    Text((["error", "stale"].contains(usage.status) ? L("windows.accountUsage.lastKnownPrefix") : "") + L("windows.accountUsage.checkedAtTemplate", ["time": date.formatted(date: .omitted, time: .shortened)]))
                        .font(.system(size: 9)).foregroundStyle(.tertiary)
                }
            } else if provider == "claude", !controller.claudeKeychainEnabled {
                Text(L("windows.accountUsage.claudeBeforeFirstRunNote")).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            } else {
                Text(controller.refreshing ? L("windows.accountUsage.cardChecking") : L("windows.accountUsage.cardNotCheckedYet")).font(.system(size: 11)).foregroundStyle(.secondary)
            }
        }
        .padding(10).frame(maxWidth: .infinity, alignment: .leading)
        .background(Palette.subtle, in: RoundedRectangle(cornerRadius: 9))
    }

    /// The read-only limit-reset rows. There is no reset button and no claim: the
    /// only action is the link, and it is live in every one of the seven states.
    @ViewBuilder
    private func resetSection(provider: String, usage: AccountUsageSnapshot?) -> some View {
        // Core decides whether there is anything to draw; an empty list means
        // the direct lookup is off and the section does not exist at all.
        let rows = provider == "claude"
            ? AccountResetPresentation.rows(usage, directLookupEnabled: controller.claudeKeychainEnabled)
            : []
        if !rows.isEmpty {
            Divider()
            VStack(alignment: .leading, spacing: 4) {
                Text(L("usage.reset.title")).font(.system(size: 11, weight: .medium))
                ForEach(rows) { row in
                    VStack(alignment: .leading, spacing: 1) {
                        Text(row.label).font(.system(size: 10)).foregroundStyle(.secondary)
                        Text(row.line).font(.system(size: 11)).fixedSize(horizontal: false, vertical: true)
                    }
                    .accessibilityIdentifier("statusbar-usage-reset-\(row.program)")
                }
                // There is no reset button: the only action is this link, and
                // it is live in every one of the seven states.
                Link(AccountResetEntitlement.linkLabel, destination: URL(string: AccountResetEntitlement.linkTarget)!)
                    .font(.system(size: 11))
                    .accessibilityIdentifier("statusbar-usage-reset-link")
            }
        }
    }
}
