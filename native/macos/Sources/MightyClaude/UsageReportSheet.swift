import Charts
import MightyCore
import SwiftUI

/// 사용량: cost and tokens of finished runs over the last days, by day, by
/// workspace, by model and by agent, with the daily budget (`UsageLedger`).
/// Only what the CLIs reported counts. A panel (kind C).
struct UsageReportSheet: View {
    @EnvironmentObject private var store: AppStore
    let onClose: () -> Void

    @ViewState private var days = 7
    @ViewState private var budgetText = ""
    @ViewState private var hovered: String?

    private var records: [UsageRecord] { store.usageLedger.recent(days: days) }

    var body: some View {
        DialogScaffold(L("usage.title"), subtitle: L("usage.subtitle"), systemImage: "chart.bar",
                       close: DialogClose(identifier: "usage-close", action: onClose)) {
            Picker("", selection: $days) {
                Text(L("usage.range.days", ["count": "7"])).tag(7)
                Text(L("usage.range.days", ["count": "30"])).tag(30)
            }
            .pickerStyle(.segmented).labelsHidden().frame(width: 160)
        } content: {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.lg) {
                headline
                chart
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.lg) {
                    table(L("usage.byWorkspace"), UsageLedger.totals(records, by: \.workspaceId), name: workspaceName)
                    table(L("usage.byModel"), UsageLedger.totals(records, by: \.model), name: { $0 })
                    table(L("usage.byAgent"), UsageLedger.totals(records, by: \.provider), name: ProviderOptions.label)
                }
                budget
            }
        }
        .dialogSheetWidth(DesignMetrics.Dialog.sheetWidthL)
        .onAppear { budgetText = store.dailyBudgetUSD > 0 ? String(format: "%.2f", store.dailyBudgetUSD) : "" }
    }

    private var headline: some View {
        let cost = records.reduce(0) { $0 + $1.costUSD }, tokens = records.reduce(0) { $0 + $1.tokens }
        return HStack(alignment: .firstTextBaseline, spacing: DesignMetrics.Spacing.lg) {
            Text(String(format: "$%.2f", cost)).font(.ui(size: 26, weight: .semibold)).monospacedDigit()
            Text(L("usage.headline", ["tokens": tokens.formatted(), "runs": "\(records.count)"])).font(.ui(size: 12)).foregroundStyle(Palette.ink2)
            Spacer()
            Text(L("usage.today", ["cost": String(format: "$%.2f", store.usageLedger.cost())])).font(.ui(size: 12)).foregroundStyle(Palette.ink2)
        }
    }

    private var chart: some View {
        let daily = store.usageLedger.daily(days: days)
        return Chart(daily) { day in
            BarMark(x: .value(L("usage.day"), String(day.key.suffix(5))), y: .value(L("usage.cost"), day.costUSD), width: .ratio(0.6))
                .foregroundStyle(Palette.accent.opacity(hovered == nil || hovered == String(day.key.suffix(5)) ? 1 : 0.45))
                .clipShape(UnevenRoundedRectangle(topLeadingRadius: 4, topTrailingRadius: 4))
        }
        .chartXSelection(value: $hovered)
        .chartYAxis { AxisMarks(position: .leading) { value in
            AxisGridLine().foregroundStyle(Palette.border)
            AxisValueLabel { if let cost = value.as(Double.self) { Text(String(format: "$%.2f", cost)).font(.ui(size: 10)).foregroundStyle(Palette.ink2) } }
        } }
        .chartXAxis { AxisMarks { _ in AxisValueLabel().font(.ui(size: 10)).foregroundStyle(Palette.ink2) } }
        .overlay(alignment: .topTrailing) {
            if let hovered, let day = daily.first(where: { String($0.key.suffix(5)) == hovered }) {
                Text(L("usage.tooltip", ["day": day.key, "cost": String(format: "$%.2f", day.costUSD), "tokens": day.tokens.formatted(), "runs": "\(day.runs)"]))
                    .font(.ui(size: 11)).foregroundStyle(Palette.ink).padding(DesignMetrics.Spacing.sm)
                    .background(Palette.raised, in: RoundedRectangle(cornerRadius: 6))
            }
        }
        .frame(height: 160)
        .accessibilityLabel(L("usage.chartLabel"))
    }

    private func table(_ title: String, _ totals: [UsageLedger.Total], name: @escaping (String) -> String) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            Text(title).font(.ui(size: 11, weight: .semibold)).foregroundStyle(Palette.ink2)
            if totals.isEmpty { Text("–").font(.ui(size: 11)).foregroundStyle(Palette.ink2) }
            ForEach(totals.prefix(8)) { total in
                HStack {
                    Text(name(total.key)).font(.ui(size: 11)).foregroundStyle(Palette.ink).lineLimit(1).truncationMode(.middle)
                    Spacer(minLength: DesignMetrics.Spacing.sm)
                    Text(String(format: "$%.2f", total.costUSD)).font(.ui(size: 11, design: .monospaced)).monospacedDigit().foregroundStyle(Palette.ink)
                }
                .help(L("usage.rowHelp", ["tokens": total.tokens.formatted(), "runs": "\(total.runs)"]))
            }
        }
        .frame(maxWidth: .infinity, alignment: .topLeading)
    }

    private func workspaceName(_ id: String) -> String {
        store.snapshot.workspaces.first { $0.id == id }?.name ?? L("usage.removedWorkspace")
    }

    private var budget: some View {
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Text(L("usage.budget.label")).font(.ui(size: 12))
            TextField("0.00", text: $budgetText).textFieldStyle(.roundedBorder).frame(width: 90).font(.ui(size: 12, design: .monospaced))
                .onSubmit(applyBudget)
            Button(L("savedPrompts.save"), action: applyBudget)
            Text(L("usage.budget.note")).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
        }
    }

    private func applyBudget() {
        store.dailyBudgetUSD = Double(budgetText.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "$", with: "")) ?? 0
        budgetText = store.dailyBudgetUSD > 0 ? String(format: "%.2f", store.dailyBudgetUSD) : ""
    }
}
