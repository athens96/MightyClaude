import SwiftUI
import MightyCore

struct AccountIslandButton: View {
    @ObservedObject var controller: SessionIslandController
    var body: some View {
        if !controller.compactAccounts.isEmpty {
            AccountIslandCapsule(controller: controller, floating: false) { controller.showsBottomPopover.toggle() }
                .popover(isPresented: $controller.showsBottomPopover, arrowEdge: .top) {
                    AccountIslandOverview(controller: controller)
                }
        }
    }
}

struct AccountIslandCapsule: View {
    @ObservedObject var controller: SessionIslandController
    let floating: Bool
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            HStack(spacing: floating ? 12 : 9) {
                ForEach(controller.compactAccounts) { account in
                    AccountIslandSummaryRow(account: account, floating: floating)
                }
            }
            .foregroundStyle(.white.opacity(0.92))
            .padding(.horizontal, floating ? 13 : 10).padding(.vertical, floating ? 5 : 4)
            .frame(maxWidth: floating ? .infinity : nil)
            .background(Color.black.opacity(0.91), in: Capsule())
            .overlay { Capsule().stroke(.white.opacity(0.12), lineWidth: 0.5) }
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .help(summaryHelp)
        .accessibilityLabel(summaryAccessibilityLabel)
        .accessibilityIdentifier(floating ? "account-island-floating" : "account-island-bottom")
    }
    private var descriptions: [String] {
        controller.compactAccounts.map { account in
            let provider = ProviderOptions.label(account.provider)
            let quotas = IslandSummaryPolicy.lines(account).joined(separator: ", ")
            return [provider, account.host, quotas].joined(separator: " · ")
        }
    }
    private var summaryHelp: String {
        descriptions.joined(separator: "\n") + "\n활성·실행 중·최근 세션의 프로바이더 최대 2개와 사용률이 높은 한도 2개를 표시합니다. 클릭하여 모든 계정 보기"
    }
    private var summaryAccessibilityLabel: String {
        "계정 사용 한도 아일랜드 · " + descriptions.joined(separator: " / ")
    }

}

private struct AccountIslandSummaryRow: View {
    let account: IslandAccount
    let floating: Bool
    var body: some View {
        let quotaText = IslandSummaryPolicy.lines(account).joined(separator: " · ")
        let opacity = ["error", "stale"].contains(account.usage?.status ?? "") ? 0.65 : 1.0
        HStack(spacing: 5) {
            Image(systemName: Palette.symbol(account.provider))
                .font(.system(size: floating ? 13 : 10))
                .foregroundStyle(accountColor(account.provider))
            if floating {
                VStack(alignment: .leading, spacing: 1) {
                    Text(ProviderOptions.label(account.provider)).font(.system(size: 10, weight: .semibold))
                    Text(quotaText).font(.system(size: 9)).monospacedDigit().lineLimit(1)
                }
            } else {
                Text(quotaText).font(.system(size: 10, weight: .medium)).monospacedDigit().lineLimit(1)
            }
        }.opacity(opacity)
    }
}

struct AccountIslandOverview: View {
    @ObservedObject var controller: SessionIslandController
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Label("계정 사용 한도", systemImage: "chart.pie").font(.system(size: 14, weight: .semibold))
                Spacer()
                Button { controller.refresh(force: true) } label: {
                    if controller.refreshing { ProgressView().controlSize(.mini) }
                    else { Image(systemName: "arrow.clockwise") }
                }
                .buttonStyle(.plain).disabled(controller.refreshing)
                .help("계정 한도 다시 확인").accessibilityLabel("계정 한도 새로고침")
            }
            GeometryReader { viewport in
                ScrollView {
                    VStack(alignment: .leading, spacing: 12) {
                        ForEach(controller.accounts) { account in
                            accountCard(account)
                        }
                        if controller.accounts.isEmpty {
                            Text("에이전트 세션을 열면 해당 계정의 사용 한도를 표시합니다.")
                                .font(.system(size: 12)).foregroundStyle(.secondary)
                        }
                    }
                    .frame(width: max(0, viewport.size.width - 14), alignment: .leading)
                    .padding(.trailing, 14)
                    .padding(.vertical, 1)
                }
            }.frame(height: 470)
            Text("계정 한도는 같은 계정을 사용하는 앱·세션에서 공유됩니다.")
                .font(.system(size: 10)).foregroundStyle(.secondary)
        }
        .padding(18).frame(width: 360)
        .accessibilityIdentifier("account-island-details")
    }

    private func accountCard(_ account: IslandAccount) -> some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack {
                Label(ProviderOptions.label(account.provider), systemImage: Palette.symbol(account.provider))
                    .font(.system(size: 12, weight: .semibold)).foregroundStyle(accountColor(account.provider))
                Spacer()
                Text("\(account.host) · \(account.sessionCount)개 세션").font(.system(size: 10)).foregroundStyle(.secondary)
            }
            if let usage = account.usage {
                if usage.accountLabel != nil || usage.plan != nil {
                    Text([usage.accountLabel, usage.plan].compactMap { $0 }.joined(separator: " · "))
                        .font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(2).textSelection(.enabled)
                }
                ForEach(Array(usage.windows.enumerated()), id: \.offset) { _, window in
                    VStack(alignment: .leading, spacing: 4) {
                        HStack {
                            Text(quotaWindowLabel(window.kind)).lineLimit(1)
                            Spacer()
                            Text("\(quotaPercent(window.usedPercent)) 사용").monospacedDigit()
                        }.font(.system(size: 11))
                        ProgressView(value: min(1, max(0, window.usedPercent / 100)))
                            .tint(window.usedPercent >= 90 ? .orange : accountColor(account.provider))
                        if let value = window.resetsAt, let date = quotaDate(value) {
                            Text("초기화 \(date.formatted(date: .abbreviated, time: .shortened))")
                                .font(.system(size: 10)).foregroundStyle(.secondary)
                        }
                    }
                }
                if usage.status != "available" || usage.windows.isEmpty {
                    Text(usage.detail).font(.system(size: 11)).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                if let fetched = usage.fetchedAt, let date = quotaDate(fetched) {
                    Text("\(["error", "stale"].contains(usage.status) ? "마지막 확인값 · " : "")\(date.formatted(date: .omitted, time: .shortened)) 확인")
                        .font(.system(size: 9)).foregroundStyle(.tertiary)
                }
            } else {
                Text(account.remote ? "이 원격 세션에서 계정 한도를 아직 받지 못했습니다." : "계정 사용 한도를 확인하고 있습니다…")
                    .font(.system(size: 11)).foregroundStyle(.secondary)
            }
        }
        .padding(12).frame(maxWidth: .infinity, alignment: .leading)
        .background(Palette.subtle, in: RoundedRectangle(cornerRadius: 10))
        .accessibilityElement(children: .contain).accessibilityIdentifier("account-quota-\(account.id)")
    }
}

private func accountColor(_ provider: String) -> Color {
    switch provider { case "claude": Palette.accent; case "codex": .green; default: .blue }
}
private func quotaPercent(_ value: Double) -> String {
    guard value.isFinite, value >= 0, value < 1_000_000 else { return "—" }
    return "\(Int(value.rounded()))%"
}
func quotaWindowLabel(_ kind: String) -> String {
    switch kind {
    case "session", "five_hour", "primary": "세션"
    case "weekly", "seven_day", "secondary": "주간"
    case "daily", "daily_quota": "일일"
    case "spend_limit": "지출 한도"
    case "Sonnet": "Sonnet"
    default: String(kind.prefix(64))
    }
}
private func quotaDate(_ string: String) -> Date? {
    if let date = ISO8601DateFormatter().date(from: string) { return date }
    let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
    return formatter.date(from: string)
}
