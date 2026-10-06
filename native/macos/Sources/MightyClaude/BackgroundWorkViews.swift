import SwiftUI
import MightyCore

/// Background tasks, one row each: a status dot, what the task is doing, and
/// its kind · status · elapsed time. The plan style's task-list widget and
/// the pane's own background strip both draw this, so they read the same.
struct BackgroundTaskRows: View {
    let tasks: [StyleWidgetPresentation.Task]

    var body: some View {
        // The elapsed time ticks once a second while anything still runs; rows
        // that all ended show fixed times and need no clock.
        if tasks.contains(where: \.running) {
            TimelineView(.periodic(from: .now, by: 1)) { context in rows(now: context.date) }
        } else {
            rows(now: Date())
        }
    }

    private func rows(now: Date) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            ForEach(Array(tasks.enumerated()), id: \.offset) { _, task in row(task, now: now) }
        }
    }

    private func row(_ task: StyleWidgetPresentation.Task, now: Date) -> some View {
        let detail = [task.kindTitle, task.statusTitle, task.elapsed(now: now)].filter { !$0.isEmpty }.joined(separator: " · ")
        return HStack(spacing: 6) {
            Circle().fill(color(task.status)).frame(width: 6, height: 6)
            Text(verbatim: task.text).font(.system(size: 11)).foregroundStyle(Palette.ink).lineLimit(1)
            Spacer(minLength: 4)
            Text(verbatim: detail).font(.system(size: 10).monospacedDigit()).foregroundStyle(Palette.ink2).lineLimit(1)
        }
        .accessibilityElement(children: .combine)
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
/// composer that opens to the task rows.
struct BackgroundWorkStrip: View {
    let sessionId: String
    let work: BackgroundWork
    @ViewState private var open = false

    var body: some View {
        let tasks = PlanCardSupport.backgroundTasks(work)
        VStack(alignment: .leading, spacing: 6) {
            Button { open.toggle() } label: {
                HStack(spacing: 6) {
                    Image(systemName: "square.stack.3d.up").foregroundStyle(Palette.accent)
                    Text(L("plan.background.listTitle", ["count": "\(work.tasks.count)"])).font(.system(size: 11, weight: .semibold)).foregroundStyle(Palette.ink)
                    Text(verbatim: PlanCardSupport.backgroundSummary(work)).font(.system(size: 11)).foregroundStyle(Palette.ink2).lineLimit(1)
                    Spacer(minLength: 0)
                    Image(systemName: open ? "chevron.up" : "chevron.down").font(.system(size: 10, weight: .semibold)).foregroundStyle(Palette.ink2)
                }.contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(L("plan.background.listTitle", ["count": "\(work.tasks.count)"]))
            .accessibilityValue(open ? L("plan.background.hide") : L("plan.background.show"))
            .accessibilityIdentifier("background-work-\(sessionId)")
            if open { BackgroundTaskRows(tasks: tasks) }
        }
        .padding(.horizontal, 12).padding(.vertical, 6)
        .background(Palette.panel, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay { RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Palette.border, lineWidth: 1).allowsHitTesting(false) }
        .padding(.horizontal, 12).padding(.top, 6)
    }
}
