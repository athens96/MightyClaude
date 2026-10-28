import SwiftUI
import MightyCore

/// Background tasks, one row each: a status dot, what the task is doing, and
/// its kind · status · elapsed time · whether the agent hears of it when it
/// ends or has heard (`PlanCardSupport.backgroundNotice`). The plan style's task-list widget and
/// the pane's own background strip both draw this, so they read the same.
struct BackgroundTaskRows: View {
    /// The background strip's rows carry the diagram's kind words and a run's last
    /// output line (`PlanCardSupport.backgroundStripRows`); the style's task list has neither.
    let items: [PlanCardSupport.BackgroundStripRow]
    /// Rows opened to their command and output lines, by task id. The strip's own state: opening a row
    /// redraws these rows alone.
    @ViewState private var opened = Set<String>()

    init(tasks: [StyleWidgetPresentation.Task]) { items = tasks.map { PlanCardSupport.BackgroundStripRow(task: $0) } }
    init(strip: [PlanCardSupport.BackgroundStripRow]) { items = strip }

    var body: some View {
        // The elapsed time ticks once a second while anything still runs; rows
        // that all ended show fixed times and need no clock.
        if items.contains(where: \.task.running) {
            TimelineView(.periodic(from: .now, by: 1)) { context in rows(now: context.date) }
        } else {
            rows(now: Date())
        }
    }

    private func rows(now: Date) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            ForEach(Array(items.enumerated()), id: \.offset) { _, item in row(item, now: now) }
        }
    }

    private func row(_ item: PlanCardSupport.BackgroundStripRow, now: Date) -> some View {
        let task = item.task
        // The kind words already say waiting or how it ended: only a delivered notice is added.
        let parts = item.kindText.map { [$0, task.elapsed(now: now), PlanCardSupport.noticeDelivered(task.status) ? L("plan.background.noticeDelivered") : ""] }
            ?? [task.kindTitle, task.statusTitle, task.elapsed(now: now), PlanCardSupport.backgroundNotice(task.status) ?? ""]
        let detail = parts.filter { !$0.isEmpty }.joined(separator: " · ")
        let id = item.opens ? item.id : nil
        let open = id.map(opened.contains) ?? false
        let head = HStack(spacing: DesignMetrics.Spacing.sm) {
            Circle().fill(color(task.status)).frame(width: 6, height: 6)
            Text(verbatim: task.text).font(.ui(size: 11)).foregroundStyle(Palette.ink).lineLimit(1)
            Spacer(minLength: 4)
            Text(verbatim: detail).font(.ui(size: 10).monospacedDigit()).foregroundStyle(Palette.ink2).lineLimit(1)
            if id != nil {
                Image(systemName: open ? "chevron.up" : "chevron.down").font(.ui(size: 9, weight: .semibold)).foregroundStyle(Palette.ink2)
            }
        }
        return VStack(alignment: .leading, spacing: 0) {
            if let id {
                Button { if !opened.insert(id).inserted { opened.remove(id) } } label: { head.contentShape(Rectangle()) }
                    .buttonStyle(.plain)
                    .accessibilityHint(open ? L("graph.block.collapse") : L("graph.block.expand"))
                    .accessibilityIdentifier("background-work-row-\(id)")
            } else {
                head
            }
            if open {
                // The command (a sub-agent's first prompt line) and the output's last lines, under the text.
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    if let command = item.command {
                        Text(verbatim: command).font(.code(size: 10)).foregroundStyle(Palette.ink)
                            .lineLimit(4).truncationMode(.tail).textSelection(.enabled)
                    }
                    ForEach(Array(item.tailLines.enumerated()), id: \.offset) { _, line in
                        Text(verbatim: line).font(.code(size: 10)).foregroundStyle(Palette.ink2.opacity(0.8))
                            .lineLimit(1).truncationMode(.middle)
                    }
                }
                .padding(.leading, DesignMetrics.Spacing.lg).padding(.top, DesignMetrics.Spacing.xxs)
            } else if let tail = item.tail {
                // Under the text, past the dot (6) and its gap.
                Text(verbatim: tail).font(.code(size: 10)).foregroundStyle(Palette.ink2.opacity(0.8))
                    .lineLimit(1).truncationMode(.middle)
                    .padding(.leading, DesignMetrics.Spacing.lg)
            }
        }
        .accessibilityElement(children: id == nil ? .combine : .contain)
    }

    private func color(_ status: String) -> Color {
        switch status {
        case "running": return Palette.accent
        case "completed": return Palette.done
        case "failed": return Palette.err
        default: return Palette.ink2
        }
    }
}

/// The pane's background work outside a style: one folded line above the
/// composer that opens to the task rows. Whether it is open and whether it is
/// drawn at all are app-wide and saved (`AppStore.backgroundWorkOpen`,
/// `AppStore.showsBackgroundWork`); the eye button hides it.
struct BackgroundWorkStrip: View {
    @EnvironmentObject private var store: AppStore
    let sessionId: String
    let work: BackgroundWork
    /// The pane's latest request, whose blocks the rows meet by `toolUseId`: their command and, for a run, the
    /// output lines of `output`. Its background blocks are data the diagram no longer draws.
    var run: MightyGraphRun? = nil
    /// The pane's background output lines; only this pane redraws when they change.
    @ObservedObject var output: BackgroundPaneOutput

    var body: some View {
        let rows = PlanCardSupport.backgroundStripRows(work, run: run, output: output.lines)
        let open = store.backgroundWorkOpen
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Button { store.setBackgroundWorkOpen(!open) } label: {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        Image(systemName: "square.stack.3d.up").foregroundStyle(Palette.accent)
                        Text(L("plan.background.listTitle", ["count": "\(work.tasks.count)"])).font(.ui(size: 11, weight: .semibold)).foregroundStyle(Palette.ink)
                        Text(verbatim: PlanCardSupport.backgroundStripSummary(work)).font(.ui(size: 11)).foregroundStyle(Palette.ink2).lineLimit(1)
                        Spacer(minLength: 0)
                        Image(systemName: open ? "chevron.up" : "chevron.down").font(.ui(size: 10, weight: .semibold)).foregroundStyle(Palette.ink2)
                    }.contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(L("plan.background.listTitle", ["count": "\(work.tasks.count)"]))
                .accessibilityValue(open ? L("plan.background.hide") : L("plan.background.show"))
                .accessibilityIdentifier("background-work-\(sessionId)")
                Button { store.setShowsBackgroundWork(false) } label: {
                    Image(systemName: "eye.slash").font(.ui(size: 10, weight: .semibold)).foregroundStyle(Palette.ink2)
                        .frame(width: 18, height: 16).contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(L("plan.background.hideStripHelp"))
                .accessibilityLabel(L("plan.background.hideStrip"))
                .accessibilityIdentifier("background-work-hide-\(sessionId)")
            }
            if open { BackgroundTaskRows(strip: rows) }
        }
        .padding(.horizontal, DesignMetrics.Spacing.md).padding(.vertical, DesignMetrics.Spacing.sm)
        .background(Palette.panel, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay { RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Palette.border, lineWidth: 1).allowsHitTesting(false) }
        .padding(.horizontal, DesignMetrics.Spacing.md).padding(.top, DesignMetrics.Spacing.sm)
    }
}
