import AppKit
import MightyCore
import SwiftUI

/// The work map window's own sizes (docs/work-map.md): the detail column, the overview's rows, the lane bar. Insets
/// and gaps that the spacing scale has no step for are named here.
enum WorkMapMetrics {
    static let detailWidth: CGFloat = 380
    static let cardMinWidth: CGFloat = 250
    static let columnMinWidth: CGFloat = 64
    static let labelWidth: CGFloat = 96
    static let headHeight: CGFloat = 20
    static let rowHeight: CGFloat = 22
    static let footHeight: CGFloat = 18
    static let barWidth: CGFloat = 90
    static let barHeight: CGFloat = 6
    static let laneDot: CGFloat = 8
    static let columnRadius: CGFloat = 6
    static let cardRadius: CGFloat = 10
    static let topicRadius: CGFloat = 8
    static let pageInset: CGFloat = 14
    static let cardInset: CGFloat = 12
    static let topicInsetH: CGFloat = 11
    static let topicInsetV: CGFloat = 9
    static let overviewInsetH: CGFloat = 12
    static let overviewInsetV: CGFloat = 10
    static let detailInsetH: CGFloat = 16
    static let detailInsetV: CGFloat = 14
    static let sectionGap: CGFloat = 14
    static let dimmed: Double = 0.25
    static let shownFiles = 100
}

/// The lane colours: the Mac the agent fill, Windows the run blue, the phone the done green, CI and release the
/// wait amber, the rest the stop grey.
enum WorkMapLane {
    static func color(_ lane: String) -> Color {
        switch lane {
        case "mac": Palette.token(\.agent)
        case "windows": Palette.run
        case "phone": Palette.done
        case "ci": Palette.wait
        default: Palette.stop
        }
    }

    static func title(_ lane: String) -> String { L("workMap.lane." + lane) }
}

/// The work map's words.
enum WorkMapText {
    /// The version's number, or "next version" for an unreleased one without a number.
    static func name(_ version: WorkMapVersion) -> String { version.version ?? L("workMap.version.next") }

    /// "11/02" for "2026-11-02" or an ISO time on that day.
    static func shortDate(_ iso: String?) -> String {
        guard let iso, iso.count >= 10 else { return "" }
        return String(iso.prefix(10).dropFirst(5)).replacingOccurrences(of: "-", with: "/")
    }

    static func range(_ map: WorkMapModel) -> String {
        guard let newest = map.versions.first, let oldest = map.versions.last else { return "" }
        return L("workMap.range", ["from": name(oldest), "to": name(newest)])
    }

    static func status(_ version: WorkMapVersion) -> String {
        switch version.status {
        case "released": L("workMap.status.released", ["date": shortDate(version.date)])
        case "releasing": L("workMap.status.releasing", ["step": version.releaseStep ?? ""])
        default: L("workMap.status.unreleased")
        }
    }

    static func unavailable(_ reason: String?) -> String {
        switch reason {
        case "notGit": L("workMap.unavailable.notGit")
        case "remote": L("workMap.unavailable.remote")
        default: L("workMap.unavailable.gitFailed")
        }
    }

    static func counts(commits: Int, requests: Int) -> String {
        L("workMap.commits", ["count": "\(commits)"]) + " · " + L("workMap.requestsCount", ["count": "\(requests)"])
    }
}

/// The work map pane (docs/work-map.md): an overview grid of versions by platform, the versions newest first with
/// their topics, and the picked topic's requests, commits and files.
struct WorkMapView: View {
    @EnvironmentObject private var store: AppStore
    let session: RunSession

    var body: some View {
        WorkMapContent(model: store.workMapModel(for: session.workspaceId))
    }
}

private struct WorkMapContent: View {
    @EnvironmentObject private var store: AppStore
    @ObservedObject var model: WorkMapPaneModel

    var body: some View {
        VStack(spacing: 0) {
            header
            if let map = model.map {
                if map.available {
                    WorkMapOverview(model: model, map: map).padding(.bottom, DesignMetrics.Spacing.md)
                    Rectangle().fill(Palette.border).frame(height: 1)
                    HStack(spacing: 0) {
                        WorkMapVersionList(model: model, map: map).frame(maxWidth: .infinity, maxHeight: .infinity)
                        Rectangle().fill(Palette.border).frame(width: 1)
                        WorkMapDetail(model: model).frame(width: WorkMapMetrics.detailWidth).frame(maxHeight: .infinity)
                    }
                } else {
                    message(WorkMapText.unavailable(map.reason))
                }
            } else {
                ProgressView().controlSize(.small).frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .background(Palette.panel)
        .onAppear { if model.map == nil { store.refreshWorkMap(workspaceId: model.workspaceId) } }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("workmap-pane-\(model.workspaceId)")
    }

    private var header: some View {
        HStack(spacing: DesignMetrics.Spacing.md) {
            Text(headerTitle).font(.ui(size: 15, weight: .semibold)).foregroundStyle(Palette.ink).lineLimit(1)
                .accessibilityAddTraits(.isHeader)
            Spacer(minLength: DesignMetrics.Spacing.md)
            ForEach(WorkMap.lanes, id: \.self) { lane in
                WorkMapChip(title: WorkMapLane.title(lane), on: model.enabledLanes.contains(lane), color: WorkMapLane.color(lane)) {
                    if model.enabledLanes.contains(lane) { model.enabledLanes.remove(lane) } else { model.enabledLanes.insert(lane) }
                }
                .accessibilityIdentifier("workmap-lane-\(lane)")
            }
            WorkMapChip(title: L("workMap.filter.withRequests"), on: model.onlyWithRequests, color: nil) { model.onlyWithRequests.toggle() }
                .accessibilityIdentifier("workmap-only-requests")
            Button { store.refreshWorkMap(workspaceId: model.workspaceId) } label: {
                Image(systemName: "arrow.clockwise").font(.ui(size: 11)).frame(width: 24, height: 24).contentShape(Rectangle())
            }
            .buttonStyle(.plain).foregroundStyle(Palette.ink2).disabled(model.loading)
            .help(L("files.tree.refresh")).accessibilityLabel(L("files.tree.refresh"))
            .accessibilityIdentifier("workmap-refresh")
        }
        .padding(.horizontal, WorkMapMetrics.pageInset).padding(.vertical, DesignMetrics.Spacing.lg)
    }

    private var headerTitle: String {
        guard let map = model.map, map.available, !map.versions.isEmpty else { return L("workMap.title") }
        return L("workMap.title") + " · " + WorkMapText.range(map)
    }

    private func message(_ text: String) -> some View {
        Text(text).font(.ui(size: 12)).foregroundStyle(Palette.ink2).multilineTextAlignment(.center)
            .padding(WorkMapMetrics.pageInset).frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// A filter chip: a lane (with its colour dot) or the requests filter.
private struct WorkMapChip: View {
    let title: String
    let on: Bool
    let color: Color?
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: DesignMetrics.Spacing.xs) {
                if let color { Circle().fill(color).frame(width: WorkMapMetrics.laneDot, height: WorkMapMetrics.laneDot) }
                Text(title).font(.ui(size: 11)).lineLimit(1)
            }
            .foregroundStyle(on ? Palette.accent : Palette.ink2)
            .padding(.horizontal, DesignMetrics.Spacing.md).frame(minHeight: DesignMetrics.Layout.hitTarget)
            .background(on ? Palette.accentSoft : Palette.panel, in: Capsule())
            .overlay { Capsule().strokeBorder(on ? Color.clear : Palette.border, lineWidth: 1) }
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? .isSelected : [])
    }
}

// MARK: Overview

/// Columns are versions (oldest to newest, the unreleased one last), rows are platforms, a dot's diameter the
/// platform's commits.
private struct WorkMapOverview: View {
    @ObservedObject var model: WorkMapPaneModel
    let map: WorkMapModel

    var body: some View {
        let columns = Array(map.versions.reversed())
        let largest = map.versions.flatMap { $0.counts.values }.max() ?? 0
        ScrollView(.horizontal) {
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.xxs) {
                labels
                ForEach(columns, id: \.key) { version in column(version, largest: largest) }
            }
            .padding(.horizontal, WorkMapMetrics.overviewInsetH).padding(.vertical, WorkMapMetrics.overviewInsetV)
        }
        .background(Palette.raised, in: RoundedRectangle(cornerRadius: WorkMapMetrics.cardRadius, style: .continuous))
        .padding(.horizontal, WorkMapMetrics.pageInset)
        .accessibilityIdentifier("workmap-overview")
    }

    private var labels: some View {
        VStack(alignment: .leading, spacing: 0) {
            Color.clear.frame(height: WorkMapMetrics.headHeight)
            ForEach(WorkMap.lanes, id: \.self) { lane in
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    Circle().fill(WorkMapLane.color(lane)).frame(width: WorkMapMetrics.laneDot, height: WorkMapMetrics.laneDot)
                    Text(WorkMapLane.title(lane)).font(.ui(size: 11)).foregroundStyle(Palette.ink2).lineLimit(1)
                }
                .opacity(model.enabledLanes.contains(lane) ? 1 : WorkMapMetrics.dimmed)
                .frame(height: WorkMapMetrics.rowHeight, alignment: .leading)
            }
            Color.clear.frame(height: WorkMapMetrics.footHeight)
        }
        .frame(width: WorkMapMetrics.labelWidth, alignment: .leading)
        .accessibilityHidden(true)
    }

    private func column(_ version: WorkMapVersion, largest: Int) -> some View {
        let selected = version.key == model.selectedKey
        return Button {
            model.select(version: version.key)
            model.scrollTarget = version.key
        } label: {
            VStack(spacing: 0) {
                Text(WorkMapText.name(version)).font(.ui(size: 11, weight: .semibold)).lineLimit(1)
                    .foregroundStyle(selected ? Palette.accent : Palette.ink2).frame(height: WorkMapMetrics.headHeight)
                ForEach(WorkMap.lanes, id: \.self) { lane in
                    dot(version, lane: lane, largest: largest).frame(height: WorkMapMetrics.rowHeight)
                }
                Text("\(version.total)").font(.ui(size: 10)).monospacedDigit().foregroundStyle(Palette.ink2).frame(height: WorkMapMetrics.footHeight)
            }
            .frame(minWidth: WorkMapMetrics.columnMinWidth)
            .background(selected ? Palette.accentSoft : Color.clear, in: RoundedRectangle(cornerRadius: WorkMapMetrics.columnRadius, style: .continuous))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(WorkMapText.name(version) + ", " + WorkMapText.status(version))
        .accessibilityAddTraits(selected ? .isSelected : [])
        .accessibilityIdentifier("workmap-column-\(version.key)")
    }

    @ViewBuilder private func dot(_ version: WorkMapVersion, lane: String, largest: Int) -> some View {
        let count = version.counts[lane] ?? 0
        if count > 0 {
            let size = CGFloat(WorkMap.dotSize(count: count, largest: largest))
            Circle().fill(WorkMapLane.color(lane)).frame(width: size, height: size)
                .overlay {
                    // An unreleased column's dots carry a dashed rim.
                    if version.status != "released" { Circle().strokeBorder(Palette.waitText, style: StrokeStyle(lineWidth: 1.5, dash: [3, 2])) }
                }
                .opacity(model.enabledLanes.contains(lane) ? 1 : WorkMapMetrics.dimmed)
                .help(WorkMapLane.title(lane) + " · " + L("workMap.commits", ["count": "\(count)"]))
        } else {
            Color.clear
        }
    }
}

// MARK: Versions

private struct WorkMapVersionList: View {
    @ObservedObject var model: WorkMapPaneModel
    let map: WorkMapModel

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.lg) {
                    ForEach(map.versions, id: \.key) { version in
                        WorkMapVersionCard(model: model, map: map, version: version).id(version.key)
                    }
                }
                .padding(WorkMapMetrics.pageInset)
            }
            .onChange(of: model.scrollTarget) { _, target in
                guard let target else { return }
                withAnimation(.easeInOut(duration: DesignMetrics.Motion.seconds(DesignMetrics.Motion.scroll))) { proxy.scrollTo(target, anchor: .top) }
                model.scrollTarget = nil
            }
        }
        .accessibilityIdentifier("workmap-versions")
    }
}

private struct WorkMapVersionCard: View {
    @EnvironmentObject private var store: AppStore
    @ObservedObject var model: WorkMapPaneModel
    let map: WorkMapModel
    let version: WorkMapVersion

    private var selected: Bool { model.selectedKey == version.key }
    /// While any version is being summarized the other versions' buttons are off.
    private var busy: Bool { map.summarizing != nil }
    private var summarizingThis: Bool { map.summarizing == version.key }

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
            head
            if let topics = version.topics, !topics.isEmpty {
                topicGrid(topics)
                HStack(spacing: DesignMetrics.Spacing.md) {
                    if version.topicsStale { summarizeButton(L("workMap.summarizeAgain")) }
                    commitsToggle
                }
            } else { empty }
            if summarizingThis {
                HStack(spacing: DesignMetrics.Spacing.md) {
                    ProgressView().controlSize(.small)
                    Text(L("workMap.summarizing")).font(.ui(size: 11.5)).foregroundStyle(Palette.ink2)
                }
                .accessibilityElement(children: .combine)
            }
            if let failure = version.summaryError, !summarizingThis {
                Text(failure).font(.ui(size: 11.5)).foregroundStyle(Palette.errText).fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("workmap-error-\(version.key)")
            }
            if model.commitsShown.contains(version.key) { commits }
            if version.truncated {
                Text(L("workMap.truncated", ["count": "\(version.commits.count)"])).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
            }
        }
        .padding(WorkMapMetrics.cardInset)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Palette.panel, in: RoundedRectangle(cornerRadius: WorkMapMetrics.cardRadius, style: .continuous))
        .overlay { RoundedRectangle(cornerRadius: WorkMapMetrics.cardRadius, style: .continuous).strokeBorder(selected ? Palette.accent : Palette.border, lineWidth: 1) }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("workmap-version-\(version.key)")
    }

    private var head: some View {
        HStack(spacing: DesignMetrics.Spacing.md) {
            Text(WorkMapText.name(version)).font(.ui(size: 14, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1)
            badge(WorkMapText.status(version), fill: statusFill, ink: statusInk)
            if version.topicsStale { badge(L("workMap.stale"), fill: Palette.waitSoft, ink: Palette.waitText) }
            Text(L("workMap.commits", ["count": "\(version.total)"])).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
            if let topics = version.topics { Text(L("workMap.topicsCount", ["count": "\(topics.count)"])).font(.ui(size: 11)).foregroundStyle(Palette.ink2) }
            Spacer(minLength: DesignMetrics.Spacing.sm)
            laneBar
        }
        .contentShape(Rectangle())
        .onTapGesture { model.select(version: version.key) }
    }

    private var statusFill: Color {
        switch version.status {
        case "released": Palette.doneSoft
        case "releasing": Palette.waitSoft
        default: Palette.stopSoft
        }
    }

    private var statusInk: Color {
        switch version.status {
        case "released": Palette.doneText
        case "releasing": Palette.waitText
        default: Palette.stopText
        }
    }

    private func badge(_ text: String, fill: Color, ink: Color) -> some View {
        Text(text).font(.ui(size: 11, weight: .semibold)).foregroundStyle(ink).lineLimit(1)
            .padding(.horizontal, DesignMetrics.Spacing.md).padding(.vertical, DesignMetrics.Spacing.xxs)
            .background(fill, in: Capsule())
    }

    /// The platforms' shares of the version's commits.
    private var laneBar: some View {
        let total = max(1, version.counts.values.reduce(0, +))
        return GeometryReader { geometry in
            HStack(spacing: 0) {
                ForEach(WorkMap.lanes.filter { (version.counts[$0] ?? 0) > 0 }, id: \.self) { lane in
                    Rectangle().fill(WorkMapLane.color(lane))
                        .frame(width: geometry.size.width * CGFloat(version.counts[lane] ?? 0) / CGFloat(total))
                        .opacity(model.enabledLanes.contains(lane) ? 1 : WorkMapMetrics.dimmed)
                }
            }
        }
        .frame(width: WorkMapMetrics.barWidth, height: WorkMapMetrics.barHeight)
        .background(Palette.subtle)
        .clipShape(Capsule())
        .accessibilityHidden(true)
    }

    private func topicGrid(_ topics: [WorkMapTopic]) -> some View {
        let shown = Array(topics.enumerated()).filter { !model.onlyWithRequests || !$0.element.requests.isEmpty }
        return LazyVGrid(columns: [GridItem(.adaptive(minimum: WorkMapMetrics.cardMinWidth), spacing: DesignMetrics.Spacing.md, alignment: .top)], alignment: .leading, spacing: DesignMetrics.Spacing.md) {
            ForEach(shown, id: \.offset) { item in
                WorkMapTopicCard(model: model, version: version, index: item.offset, topic: item.element)
            }
        }
    }

    private var empty: some View {
        HStack(spacing: DesignMetrics.Spacing.md) {
            if version.topics == nil {
                Text(L("workMap.noTopics")).font(.ui(size: 12)).foregroundStyle(Palette.ink2)
                summarizeButton(L("workMap.summarize"))
            }
            commitsToggle
        }
    }

    /// The dashed button that asks for the version's topics (again, once they are stale).
    private func summarizeButton(_ title: String) -> some View {
        Button { store.summarizeWorkMap(workspaceId: model.workspaceId, key: version.key) } label: {
            Text(title).font(.ui(size: 11)).foregroundStyle(Palette.accent)
                .padding(.horizontal, DesignMetrics.Spacing.lg).frame(minHeight: DesignMetrics.Layout.hitTarget)
                .overlay { Capsule().strokeBorder(Palette.accent, style: StrokeStyle(lineWidth: 1, dash: [3, 2])) }
                .contentShape(Capsule())
        }
        .buttonStyle(.plain).disabled(busy)
        .accessibilityIdentifier("workmap-summarize-\(version.key)")
    }

    private var commitsToggle: some View {
        Button { toggleCommits() } label: {
            Text(model.commitsShown.contains(version.key) ? L("workMap.hideCommits") : L("workMap.showCommits")).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
                .padding(.horizontal, DesignMetrics.Spacing.lg).frame(minHeight: DesignMetrics.Layout.hitTarget)
                .overlay { Capsule().strokeBorder(Palette.border, lineWidth: 1) }
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier("workmap-commits-toggle-\(version.key)")
    }

    private func toggleCommits() {
        if model.commitsShown.contains(version.key) { model.commitsShown.remove(version.key) } else { model.commitsShown.insert(version.key) }
    }

    private var commits: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            ForEach(version.commits, id: \.sha) { commit in WorkMapCommitRow(model: model, commit: commit) }
        }
    }
}

private struct WorkMapTopicCard: View {
    @ObservedObject var model: WorkMapPaneModel
    let version: WorkMapVersion
    let index: Int
    let topic: WorkMapTopic

    var body: some View {
        let selected = model.selectedKey == version.key && model.selectedTopic == index
        let active = topic.lanes.contains { model.enabledLanes.contains($0) }
        Button {
            model.select(version: version.key)
            model.selectedTopic = index
        } label: {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                Text(topic.title).font(.ui(size: 13, weight: .semibold)).foregroundStyle(Palette.ink).lineLimit(2).multilineTextAlignment(.leading)
                if !topic.summary.isEmpty {
                    Text(topic.summary).font(.ui(size: 12)).foregroundStyle(Palette.ink2).lineLimit(2).multilineTextAlignment(.leading)
                }
                HStack(spacing: DesignMetrics.Spacing.md) {
                    WorkMapLaneDots(lanes: topic.lanes, enabled: model.enabledLanes)
                    Text(WorkMapText.counts(commits: topic.commits.count, requests: topic.requests.count)).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
                }
            }
            .padding(.horizontal, WorkMapMetrics.topicInsetH).padding(.vertical, WorkMapMetrics.topicInsetV)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(selected ? Palette.accentSoft : Palette.raised, in: RoundedRectangle(cornerRadius: WorkMapMetrics.topicRadius, style: .continuous))
            .overlay { RoundedRectangle(cornerRadius: WorkMapMetrics.topicRadius, style: .continuous).strokeBorder(selected ? Palette.accent : Color.clear, lineWidth: 1) }
            .contentShape(Rectangle())
            .opacity(active ? 1 : WorkMapMetrics.dimmed)
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
        .accessibilityIdentifier("workmap-topic-\(version.key)-\(index)")
    }
}

/// A row of lane dots; a platform whose chip is off is dimmed.
private struct WorkMapLaneDots: View {
    let lanes: [String]
    let enabled: Set<String>

    var body: some View {
        HStack(spacing: DesignMetrics.Spacing.xs) {
            ForEach(lanes, id: \.self) { lane in
                Circle().fill(WorkMapLane.color(lane)).frame(width: WorkMapMetrics.laneDot, height: WorkMapMetrics.laneDot)
                    .opacity(enabled.contains(lane) ? 1 : WorkMapMetrics.dimmed)
            }
        }
        .accessibilityLabel(lanes.map(WorkMapLane.title).joined(separator: ", "))
    }
}

private struct WorkMapCommitRow: View {
    @EnvironmentObject private var store: AppStore
    @ObservedObject var model: WorkMapPaneModel
    let commit: WorkMapCommit

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: DesignMetrics.Spacing.md) {
            Button { store.copyWorkMapSha(commit.sha, workspaceId: model.workspaceId) } label: {
                Text(model.copiedSha == commit.sha ? L("workMap.copied") : commit.sha)
                    .font(.ui(size: 11, design: .monospaced)).foregroundStyle(Palette.accent).lineLimit(1)
            }
            .buttonStyle(.plain)
            .accessibilityIdentifier("workmap-sha-\(commit.sha)")
            WorkMapLaneDots(lanes: commit.lanes, enabled: model.enabledLanes)
            VStack(alignment: .leading, spacing: 0) {
                if let scope = commit.scope, !scope.isEmpty {
                    Text(scope).font(.ui(size: 10.5, design: .monospaced)).foregroundStyle(Palette.ink2).lineLimit(1)
                }
                Text(commit.subject).font(.ui(size: 12)).foregroundStyle(Palette.ink2).fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
        .padding(.vertical, DesignMetrics.Spacing.xxs)
    }
}

// MARK: Detail

private struct WorkMapDetail: View {
    @EnvironmentObject private var store: AppStore
    @ObservedObject var model: WorkMapPaneModel

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: WorkMapMetrics.sectionGap) {
                if let version = model.selectedVersion, let index = model.selectedTopic, let topics = version.topics, topics.indices.contains(index) {
                    content(version: version, topic: topics[index])
                } else {
                    Text(L("workMap.pickTopic")).font(.ui(size: 12)).foregroundStyle(Palette.ink2)
                }
            }
            .padding(.horizontal, WorkMapMetrics.detailInsetH).padding(.vertical, WorkMapMetrics.detailInsetV)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(Palette.panel)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("workmap-detail")
    }

    @ViewBuilder private func content(version: WorkMapVersion, topic: WorkMapTopic) -> some View {
        let commits = topic.commits.compactMap { sha in version.commits.first { $0.sha == sha } }
        let requests = topic.requests.compactMap { id in version.requests.first { $0.id == id } }
        let files = uniqueFiles(commits)
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
            Text(topic.title).font(.ui(size: 16, weight: .semibold)).foregroundStyle(Palette.ink).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: DesignMetrics.Spacing.md) {
                WorkMapLaneDots(lanes: topic.lanes, enabled: model.enabledLanes)
                Text(WorkMapText.name(version)).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
            }
            if !topic.summary.isEmpty {
                Text(topic.summary).font(.ui(size: 12.5)).foregroundStyle(Palette.ink2).fixedSize(horizontal: false, vertical: true)
            }
        }
        section(L("workMap.detail.requests")) {
            if requests.isEmpty {
                Text(L("workMap.detail.noRequests")).font(.ui(size: 12)).foregroundStyle(Palette.ink2)
            } else {
                ForEach(requests, id: \.id) { request in
                    HStack(alignment: .firstTextBaseline, spacing: DesignMetrics.Spacing.md) {
                        Text(request.text).font(.ui(size: 12)).foregroundStyle(Palette.ink).fixedSize(horizontal: false, vertical: true)
                        Spacer(minLength: 0)
                        Text(WorkMapText.shortDate(request.date)).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
                    }
                    .padding(.horizontal, DesignMetrics.Spacing.lg).padding(.vertical, DesignMetrics.Spacing.md)
                    .background(Palette.raised, in: RoundedRectangle(cornerRadius: WorkMapMetrics.topicRadius, style: .continuous))
                }
            }
        }
        section(L("workMap.detail.commits")) {
            ForEach(commits, id: \.sha) { commit in WorkMapCommitRow(model: model, commit: commit) }
        }
        section(L("workMap.detail.files") + " · " + L("workMap.detail.filesCount", ["count": "\(files.count)"])) {
            ForEach(Array(files.prefix(WorkMapMetrics.shownFiles)), id: \.self) { path in
                Button { store.openWorkMapFile(workspaceId: model.workspaceId, path: path) } label: {
                    Text(path).font(.ui(size: 11, design: .monospaced)).foregroundStyle(Palette.ink2).lineLimit(1).truncationMode(.middle)
                        .frame(maxWidth: .infinity, alignment: .leading).contentShape(Rectangle())
                }
                .buttonStyle(.plain).help(path)
            }
        }
    }

    private func section<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            Text(title).font(.ui(size: 11, weight: .semibold)).foregroundStyle(Palette.ink2)
            content()
        }
    }

    /// The files of the commits, each once, in the order they come.
    private func uniqueFiles(_ commits: [WorkMapCommit]) -> [String] {
        var seen = Set<String>()
        return commits.flatMap(\.files).filter { seen.insert($0).inserted }
    }
}
