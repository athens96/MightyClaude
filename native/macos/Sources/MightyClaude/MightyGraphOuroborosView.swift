import Foundation
import MightyCore
import SwiftUI

/// Live state of the background Ouroboros executions a pane started, keyed by
/// `OuroborosExecutionLink.key`. Owned by the graph view: polling lives in its
/// `.task`, so it stops when the graph leaves the screen or the pane closes.
/// Daemon starts are counted process-wide (`OuroborosDaemonStarts`), so a new
/// model for the same pane does not start it again sooner.
@MainActor
final class MightyGraphOuroborosModel: ObservableObject {
    struct Request: Hashable {
        let links: [OuroborosExecutionLink]
        /// Blocks whose AC list is open: only these read the board stream.
        let listKeys: Set<String>
    }
    @Published private(set) var snapshots: [String: OuroborosExecutionSnapshot] = [:]
    /// Blocks whose "open dashboard" click is still being handled.
    @Published private(set) var opening: Set<String> = []
    /// Blocks whose last "open dashboard" click showed nothing.
    @Published private(set) var openFailed: Set<String> = []
    private var polls: [String: OuroborosExecutionPoll] = [:]
    private let client: OuroborosDashboardClient
    private let starts: OuroborosDaemonStarts

    init(client: OuroborosDashboardClient = OuroborosDashboardClient(), starts: OuroborosDaemonStarts = .shared) {
        self.client = client; self.starts = starts
    }

    func run(_ request: Request) async {
        let keys = Set(request.links.map(\.key))
        // Published only when something actually goes: every assignment redraws the graph.
        if snapshots.keys.contains(where: { !keys.contains($0) }) { snapshots = snapshots.filter { keys.contains($0.key) } }
        polls = polls.filter { keys.contains($0.key) }
        let now = Date()
        for link in request.links {
            if polls[link.key] == nil {
                polls[link.key] = OuroborosExecutionPoll(now: now, stale: OuroborosExecutionPoll.isStale(timestamp: link.timestamp, now: now))
            } else if request.listKeys.contains(link.key), snapshots[link.key]?.items == nil {
                // An AC list opened with nothing read yet reads now, not at
                // the block's next (possibly slow) turn or never.
                polls[link.key]?.wake(now)
            }
        }
        while !Task.isCancelled {
            let due = request.links.filter { polls[$0.key]?.isDue(Date()) == true }
            if !due.isEmpty { await tick(due, listKeys: request.listKeys) }
            // Every block stopped (terminal, job-only or unanswered history).
            guard !Task.isCancelled, let next = request.links.compactMap({ polls[$0.key]?.nextAt }).min() else { return }
            try? await Task.sleep(nanoseconds: UInt64(max(0.2, next.timeIntervalSinceNow) * 1_000_000_000))
        }
    }

    private func tick(_ due: [OuroborosExecutionLink], listKeys: Set<String>) async {
        let client = client
        let hint = due.lazy.compactMap(\.dashboardURL).first
        var endpoint = await client.endpoint(hint: hint)
        var answer = await summaries(endpoint)
        // Nothing answers: bring the daemon up for a live execution whose
        // start announced a dashboard (so Ouroboros' dashboard is not turned
        // off), a few times per execution at most and never for history. A
        // dashboard that answered and cannot read its runs is up already.
        if answer == .noAnswer, !Task.isCancelled {
            let now = Date()
            var candidate: String?
            for link in due {
                guard let executionID = link.executionID, link.dashboardURL != nil, polls[link.key]?.mayEnsureDaemon == true,
                      await starts.claim(executionID, now: now) else { continue }
                candidate = candidate ?? executionID
            }
            if let candidate, !Task.isCancelled, await client.ensureDaemon(executionID: candidate) {
                endpoint = await client.endpoint(hint: hint)
                answer = await summaries(endpoint)
            }
        }
        // Boards for open AC lists, and for runs `/api/runs` no longer lists
        // (it holds only the newest): their board is all that is left.
        var boards: [String: OuroborosBoard] = [:]
        if let endpoint, let runs = answer.summaries {
            let ids = due.compactMap { link in link.executionID.flatMap { listKeys.contains(link.key) || runs[$0] == nil ? $0 : nil } }
            if !ids.isEmpty, !Task.isCancelled { boards = await client.boards(endpoint, executionIDs: ids) }
        }
        guard !Task.isCancelled else { return }
        for link in due {
            let page = endpoint.flatMap { OuroborosDashboard.pageURL($0, executionID: link.executionID) } ?? link.dashboardURL
            var snapshot: OuroborosExecutionSnapshot
            if let executionID = link.executionID, let runs = answer.summaries {
                snapshot = .merge(summary: runs[executionID], board: boards[executionID], previousItems: snapshots[link.key]?.items, dashboardURL: page)
            } else if link.executionID == nil {
                snapshot = OuroborosExecutionSnapshot(source: .noExecution, dashboardURL: page)
            } else {
                // Keep what was last seen, but say the status is not known now.
                snapshot = snapshots[link.key] ?? OuroborosExecutionSnapshot(source: .unreachable)
                snapshot.source = answer == .noAnswer ? .unreachable : .unreadable
                snapshot.status = .unknown; snapshot.dashboardURL = page
            }
            if snapshots[link.key] != snapshot { snapshots[link.key] = snapshot }
            polls[link.key]?.record(snapshot, now: Date())
        }
    }

    private func summaries(_ endpoint: OuroborosDashboardEndpoint?) async -> OuroborosRunsAnswer {
        guard let endpoint else { return .noAnswer }
        return await client.summaries(endpoint)
    }

    /// A click may start the daemon even for history or with the dashboard
    /// turned off (the user asked for it), but never for one that answered.
    /// One click at a time per block; `open` says whether anything showed.
    func openDashboard(_ link: OuroborosExecutionLink, open: @escaping @MainActor (URL) async -> Bool) {
        guard opening.insert(link.key).inserted else { return }
        openFailed.remove(link.key)
        let client = client
        Task {
            var endpoint = await client.endpoint(hint: link.dashboardURL)
            if await summaries(endpoint) == .noAnswer, await client.ensureDaemon(executionID: link.executionID) {
                endpoint = await client.endpoint(hint: link.dashboardURL)
            }
            var opened = false
            if let url = endpoint.flatMap({ OuroborosDashboard.pageURL($0, executionID: link.executionID) }) ?? link.dashboardURL {
                opened = await open(url)
            }
            opening.remove(link.key)
            if !opened { openFailed.insert(link.key) }
        }
    }
}

/// The graph block for one background execution: status, AC progress, counts,
/// phase and activity, the goal, and the AC list when opened.
struct MightyGraphOuroborosCard: View {
    let nodeID: String
    let link: OuroborosExecutionLink
    let snapshot: OuroborosExecutionSnapshot?
    let expanded: Bool
    /// The "open dashboard" click is still being handled.
    var opening = false
    /// The last "open dashboard" click showed nothing.
    var openFailed = false
    let onToggle: () -> Void
    let onOpenDashboard: () -> Void
    private let tint = Palette.compactText

    private var status: OuroborosExecutionStatus { snapshot?.status ?? .unknown }

    /// The activity indicator's own vocabulary.
    private var indicatorStatus: String {
        switch status {
        case .running: return "running"
        case .paused: return "waiting"
        case .completed: return "completed"
        case .failed: return "error"
        case .cancelled: return "stopped"
        case .unknown: return snapshot == nil ? "starting" : "unknown"
        }
    }

    private var statusLabel: String {
        switch status {
        case .running: return L("graph.ouroboros.status.running")
        case .paused: return L("graph.ouroboros.status.paused")
        case .completed: return L("graph.ouroboros.status.completed")
        case .failed: return L("graph.ouroboros.status.failed")
        case .cancelled: return L("graph.ouroboros.status.cancelled")
        case .unknown: return L("graph.ouroboros.status.unknown")
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "infinity").foregroundStyle(tint)
                Text(L("graph.ouroboros.title")).font(.system(size: 12, weight: .semibold)).lineLimit(1)
                Spacer(minLength: 3)
                MightyGraphActivityIndicator(status: indicatorStatus, tint: Palette.run)
                MightyStatusPill(text: statusLabel, tone: DesignTone(status: indicatorStatus))
                    .accessibilityIdentifier("mighty-ouroboros-status-\(nodeID)")
                Button(action: onToggle) { Image(systemName: expanded ? "list.bullet.indent" : "list.bullet") }
                    .buttonStyle(.plain).foregroundStyle(expanded ? Palette.accent : Palette.ink2)
                    .help(expanded ? L("graph.ouroboros.hideACs") : L("graph.ouroboros.showACs"))
                    .accessibilityLabel(expanded ? L("graph.ouroboros.hideACs") : L("graph.ouroboros.showACs"))
                    .accessibilityIdentifier("mighty-ouroboros-list-toggle-\(nodeID)")
            }.padding(.horizontal, DesignMetrics.Spacing.md).frame(height: DesignMetrics.Layout.blockHead)
            Divider()
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                if let goal = snapshot?.goal {
                    Text(goal).font(.system(size: 12)).lineLimit(expanded ? 2 : 1).help(goal)
                        .accessibilityIdentifier("mighty-ouroboros-goal-\(nodeID)")
                }
                progress
                counts
                if let line = [snapshot?.phase, snapshot?.activity].compactMap({ $0 }).joined(separator: " · ").nilIfEmpty {
                    Text(line).font(.system(size: 11)).foregroundStyle(Palette.ink2).lineLimit(expanded ? 2 : 1).help(line)
                }
                if let note { Text(note).font(.system(size: 11)).foregroundStyle(Palette.ink2).lineLimit(3).fixedSize(horizontal: false, vertical: true) }
                if expanded { acList } else { Spacer(minLength: 0) }
                footer
            }
            .padding(DesignMetrics.Spacing.md)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
        .mightyBlockCard()
        .overlay { MightyGraphActivityOutline(tone: DesignTone(status: indicatorStatus)) }
        .accessibilityElement(children: .contain).accessibilityIdentifier("mighty-node-\(nodeID)")
    }

    private var note: String? {
        switch snapshot?.source {
        case nil: return L("graph.ouroboros.waiting")
        case .unreachable: return L("graph.ouroboros.unreachable")
        case .unreadable: return L("graph.ouroboros.unreadable")
        case .noExecution: return L("graph.ouroboros.noExecution")
        case .live: return nil
        }
    }

    @ViewBuilder private var progress: some View {
        if let total = snapshot?.total, total > 0 {
            let completed = min(total, snapshot?.completed ?? 0)
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                ProgressView(value: Double(completed), total: Double(total)).tint(status == .failed ? Palette.err : tint)
                Text(L("graph.ouroboros.progress", ["completed": "\(completed)", "total": "\(total)"]))
                    .font(.system(size: 11, weight: .medium)).monospacedDigit()
                    .accessibilityIdentifier("mighty-ouroboros-progress-\(nodeID)")
            }
        }
    }

    @ViewBuilder private var counts: some View {
        let values: [(String, Int?, Color)] = [
            (L("graph.ouroboros.count.pending", ["n": "\(snapshot?.pending ?? 0)"]), snapshot?.pending, Palette.ink2),
            (L("graph.ouroboros.count.executing", ["n": "\(snapshot?.executing ?? 0)"]), snapshot?.executing, tint),
            (L("graph.ouroboros.count.completed", ["n": "\(snapshot?.completed ?? 0)"]), snapshot?.completed, Palette.doneText),
            (L("graph.ouroboros.count.failed", ["n": "\(snapshot?.failed ?? 0)"]), snapshot?.failed, Palette.errText),
        ]
        if values.contains(where: { $0.1 != nil }) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                ForEach(values.indices, id: \.self) { index in
                    Text(values[index].0).font(.system(size: 10)).monospacedDigit().lineLimit(1)
                        .foregroundStyle(values[index].1 ?? 0 > 0 ? values[index].2 : Palette.ink2)
                        .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.xxs).background(Palette.raised, in: Capsule())
                }
            }
            .accessibilityElement(children: .combine).accessibilityIdentifier("mighty-ouroboros-counts-\(nodeID)")
        }
    }

    @ViewBuilder private var acList: some View {
        if let items = snapshot?.items {
            if items.isEmpty {
                Text(L("graph.ouroboros.noACs")).font(.system(size: 11)).foregroundStyle(Palette.ink2)
                Spacer(minLength: 0)
            } else {
                ScrollView(.vertical) {
                    LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                        ForEach(items.prefix(300)) { item in
                            HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                                Image(systemName: Self.icon(item.status)).font(.system(size: 11)).foregroundStyle(Self.color(item.status, tint: tint))
                                    .padding(.top, 1)
                                Text(item.title).font(.system(size: 11)).lineLimit(2).help(item.title)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                            }
                            .padding(.leading, CGFloat(item.depth) * 12).padding(.vertical, DesignMetrics.Spacing.xxs).padding(.horizontal, DesignMetrics.Spacing.sm)
                            .background(Palette.raised, in: RoundedRectangle(cornerRadius: 5))
                            .accessibilityElement(children: .ignore)
                            .accessibilityLabel(item.title + ", " + Self.statusWord(item.status))
                        }
                    }.padding(.bottom, DesignMetrics.Spacing.sm)
                }
                .accessibilityIdentifier("mighty-ouroboros-acs-\(nodeID)")
            }
        } else {
            Text(L("graph.ouroboros.loadingACs")).font(.system(size: 11)).foregroundStyle(Palette.ink2)
            Spacer(minLength: 0)
        }
    }

    private var footer: some View {
        HStack(spacing: DesignMetrics.Spacing.sm) {
            if openFailed {
                Text(L("graph.ouroboros.openFailed")).font(.system(size: 10)).foregroundStyle(Palette.errText).lineLimit(1)
                    .accessibilityIdentifier("mighty-ouroboros-open-failed-\(nodeID)")
            } else {
                Text(link.executionID.map { L("graph.ouroboros.executionID", ["id": $0]) } ?? L("graph.ouroboros.jobID", ["id": link.jobID ?? ""]))
                    .font(.system(size: 9, design: .monospaced)).foregroundStyle(Palette.ink2).lineLimit(1).truncationMode(.middle)
                    .textSelection(.enabled)
            }
            Spacer(minLength: 4)
            Button(action: onOpenDashboard) {
                Label(L("graph.ouroboros.openDashboard"), systemImage: "safari").font(.system(size: 11))
            }
            .buttonStyle(.plain).foregroundStyle(Palette.accent).disabled(opening)
            .accessibilityIdentifier("mighty-ouroboros-dashboard-\(nodeID)")
        }
    }

    static func icon(_ status: OuroborosACItem.Status) -> String {
        switch status {
        case .pending: return "circle"
        case .executing: return "circle.dotted.circle"
        case .completed: return "checkmark.circle.fill"
        case .failed: return "xmark.circle.fill"
        }
    }
    static func color(_ status: OuroborosACItem.Status, tint: Color) -> Color {
        switch status {
        case .pending: return Palette.ink2
        case .executing: return tint
        case .completed: return Palette.doneText
        case .failed: return Palette.errText
        }
    }
    static func statusWord(_ status: OuroborosACItem.Status) -> String {
        switch status {
        case .pending: return L("graph.ouroboros.ac.pending")
        case .executing: return L("graph.ouroboros.ac.executing")
        case .completed: return L("graph.ouroboros.ac.completed")
        case .failed: return L("graph.ouroboros.ac.failed")
        }
    }
}

private extension String {
    var nilIfEmpty: String? { isEmpty ? nil : self }
}
