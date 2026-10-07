import AppKit
import SwiftUI
import MightyCore

/// What a pane is looking at inside its guided style: the group it chose, and
/// whether the reset chip put it back on the start rule's row.
struct GuidedSelection: Equatable {
    var startingNew = false
    var groupId: String?
}

/// The style menu: CLI plus every style this pane may see. A menu rather than
/// a segmented picker because an unapproved style has to be visible, disabled
/// and still tappable — a segmented tag can be none of the three (§6.1).
struct MightyStylePicker: View {
    @EnvironmentObject private var store: AppStore
    let session: RunSession
    /// A pending row opens the approval sheet instead of switching the pane.
    var onApprove: (RegisteredStyle) -> Void = { _ in }

    private var styles: [RegisteredStyle] { store.applicableStyles(session) }
    private var current: RegisteredStyle? { store.guidedStyle(session) }
    private var label: String { current?.manifest.name ?? StyleMenu.cliLabel }

    var body: some View {
        let rows = StyleMenu.rows(styles)
        let byId = Dictionary(styles.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        return Menu {
            ForEach(rows) { row in
                Button { choose(row, style: byId[row.id]) } label: { rowLabel(row) }
                    .disabled(!row.selectable && !row.opensApproval)
                    .help(row.summary)
            }
        } label: {
            HStack(spacing: DesignMetrics.Spacing.xs) {
                Text(verbatim: label).font(.system(size: 10, weight: .medium)).lineLimit(1)
                if let source = current?.source, let badge = StyleChrome.sourceBadge(source) { SourceBadge(text: badge) }
                Image(systemName: "chevron.down").font(.system(size: 7)).foregroundStyle(.secondary)
            }
            .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.xxs)
            .background(Palette.subtle, in: RoundedRectangle(cornerRadius: 6))
            // The pill stays slim; the target around it does not.
            .frame(minHeight: DesignMetrics.Layout.hitTarget).contentShape(Rectangle())
        }
        .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden).fixedSize()
        .disabled(session.status == "running")
        .help(L("guidedPanel.stylesMenuHelp"))
        .accessibilityLabel(L("guidedPanel.stylesMenuAccessibility")).accessibilityValue(label)
        .accessibilityIdentifier("mighty-style-\(session.id)")
    }

    /// Kept out of the menu's builder: a chained conditional inside one is
    /// slow for the older type checker.
    @ViewBuilder private func rowLabel(_ row: StyleMenuRow) -> some View {
        let chosen = row.id == (current?.id ?? MightyStyleIDs.cli)
        let head = [row.label, row.badge].compactMap { $0 }.joined(separator: " " + StyleChrome.separator + " ")
        let text = row.detail.map { head + " " + StyleChrome.separator + " " + $0 } ?? head
        if chosen { Label(text, systemImage: "checkmark") } else { Text(verbatim: text) }
    }

    private func choose(_ row: StyleMenuRow, style: RegisteredStyle?) {
        store.selectSession(session.id)
        if row.opensApproval, let style { onApprove(style); return }
        guard row.selectable else { return }
        store.setMightyStyle(session.id, style: row.id == MightyStyleIDs.cli ? nil : row.id)
    }
}

/// The composer's guided chrome, drawn from one manifest: where the flow
/// stands, the agent's current question, what is missing, and what may be
/// sent next. One panel for every style (§6.1).
struct GuidedPanel: View {
    @EnvironmentObject private var store: AppStore
    let session: RunSession
    let style: RegisteredStyle
    let running: Bool
    /// The pane works its phase out once and hands it down: reading it here
    /// too would walk the whole request history again on every keystroke.
    let phase: StylePhase?
    @Binding var selection: GuidedSelection
    /// Commits a syllable the input method is still composing before the
    /// panel reads or replaces the draft.
    var onPrepare: () -> Void = {}

    private var manifest: StyleManifest { style.manifest }
    private var evaluator: StyleEvaluator { style.evaluator }
    private var states: [String: String] { store.styleStates(style, for: session) }
    private var group: StyleGroup? {
        selection.groupId.flatMap { manifest.group($0) } ?? evaluator.initialGroup(capabilityStates: states)
    }
    private var draft: String { (store.drafts[session.id] ?? "").trimmingCharacters(in: .whitespacesAndNewlines) }
    private var capabilitiesLoaded: Bool { store.styleCapabilitiesAreLoaded(style, for: session) }
    private let columns = [GridItem(.adaptive(minimum: 104, maximum: 170), spacing: DesignMetrics.Spacing.xs, alignment: .leading)]

    var body: some View {
        let jobOpen = evaluator.isJobOpen(session: session)
        let chips = StyleChips.make(evaluator, phase: phase, group: group, startingNew: selection.startingNew,
                                    capabilityStates: states, running: running, jobOpen: jobOpen)
        let setup = store.stylePrerequisite(style, for: session)
        // The cached reading only: file sources were read off the main actor.
        let widgets = StyleWidgetPresentation.make(store.styleState(style, for: session).widgets).filter { !$0.isEmpty }
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            if !manifest.phases.isEmpty { stepper(chips.phaseId) }
            // A waiting question always wins: nothing else may hide it.
            if let (request, questionnaire) = store.guidedQuestion(for: session.id) {
                AgentQuestionPanel(sessionId: session.id, request: request, questionnaire: questionnaire, onPrepare: onPrepare)
            } else {
                if let setup, !setup.ready { setupBlock(setup) }
                if !widgets.isEmpty { stateWidgets(widgets) }
                if evaluator.drawsGroupMap() {
                    groupMap
                    // The chosen group's question belongs to the map (§6.1).
                    if let question = group?.question, !question.isEmpty {
                        Text(verbatim: question).font(.system(size: 11)).foregroundStyle(.secondary)
                    }
                }
                if showsAttachments { attachmentRow }
                if chips.progress { progressRow } else { actionRow(chips) }
                if let guidance = evaluator.guidanceLine(phase: chips.phaseId.flatMap { manifest.phase($0) }, running: running, jobOpen: jobOpen) {
                    Text(verbatim: guidance).font(.system(size: 10)).foregroundStyle(.tertiary).fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .padding(.horizontal, DesignMetrics.Spacing.md).padding(.top, DesignMetrics.Spacing.sm)
        .accessibilityElement(children: .contain).accessibilityIdentifier("mighty-panel-\(session.id)")
        .onAppear { seedGroup() }
        .onChange(of: capabilitiesLoaded) { _, _ in seedGroup() }
    }

    /// Chosen once, when the built-in features are first known. A state that
    /// changes later does not move the group the user is working in (§1.6).
    private func seedGroup() {
        guard selection.groupId == nil else { return }
        if case .capabilityState = manifest.rules.initialGroup, !capabilitiesLoaded { return }
        selection.groupId = evaluator.initialGroup(capabilityStates: states)?.id
    }

    // MARK: 1 · phase bar

    private func stepper(_ currentId: String?) -> some View {
        let ordered = manifest.orderedPhases
        let reachedIndex = ordered.firstIndex { $0.id == currentId }
        return HStack(spacing: DesignMetrics.Spacing.xs) {
            ForEach(Array(ordered.enumerated()), id: \.element.id) { index, item in
                let isCurrent = item.id == currentId
                let reached = reachedIndex.map { index <= $0 } ?? false
                HStack(spacing: DesignMetrics.Spacing.xs) {
                    Text(verbatim: item.title).font(.system(size: 10, weight: isCurrent ? .semibold : .regular))
                        .foregroundStyle(isCurrent ? Palette.accent : reached ? Color.primary.opacity(0.75) : Color.secondary.opacity(0.6))
                    if index < ordered.count - 1 { Image(systemName: "chevron.right").font(.system(size: 7)).foregroundStyle(.tertiary) }
                }
            }
            Spacer(minLength: 0)
        }
        .accessibilityElement(children: .ignore).accessibilityLabel(L("guidedPanel.phasesAccessibility"))
        .accessibilityValue(ordered.first { $0.id == currentId }?.title ?? "")
        .accessibilityIdentifier("mighty-phases-\(session.id)")
    }

    // MARK: 3 · prerequisites

    private func setupBlock(_ result: StylePrerequisiteResult) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            ForEach(Array(result.missing.enumerated()), id: \.offset) { index, line in
                if index == 0 { Label(line, systemImage: "shippingbox").font(.system(size: 11, weight: .medium)) }
                else { Text(verbatim: line).font(.system(size: 11, weight: .medium)) }
            }
            if let hint = result.hint, !hint.isEmpty {
                Text(verbatim: hint).font(.system(size: 11)).foregroundStyle(.secondary)
                    .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
            }
            HStack(spacing: DesignMetrics.Spacing.sm) {
                // Only when the command actually fixes one of the unmet checks.
                if result.canInstall { Button(L("guidedPanel.installButton")) { store.startStyleInstall(style, from: session) }.controlSize(.small) }
                Button(L("guidedPanel.recheckButton")) { store.refreshStylePrerequisites(style, for: session) }.controlSize(.small)
            }
        }
        .accessibilityElement(children: .contain).accessibilityIdentifier("mighty-setup-\(session.id)")
    }

    // MARK: 4 · state widgets

    /// The style's declared state sources, in payload order and in the shape
    /// the phone draws them (§1.16.4). An empty list or label was already
    /// left out; an empty bar still draws its track and `0/0`.
    private func stateWidgets(_ widgets: [StyleWidgetPresentation]) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            ForEach(Array(widgets.enumerated()), id: \.offset) { _, widget in stateWidget(widget) }
        }
        .accessibilityElement(children: .contain).accessibilityLabel(L("guidedPanel.stateAccessibility"))
        .accessibilityIdentifier("mighty-state-\(session.id)")
    }

    @ViewBuilder private func stateWidget(_ widget: StyleWidgetPresentation) -> some View {
        switch widget {
        case .progressBar(let fraction, let text):
            // Both numbers are counts, so the bar says 3/7 rather than 43%.
            HStack(spacing: DesignMetrics.Spacing.sm) {
                GeometryReader { proxy in
                    ZStack(alignment: .leading) {
                        Capsule().fill(Palette.border)
                        Capsule().fill(Palette.accent).frame(width: proxy.size.width * fraction)
                    }
                }
                .frame(height: 6)
                Text(verbatim: text).font(.system(size: 10, weight: .semibold).monospacedDigit()).foregroundStyle(Palette.accent)
            }
            .accessibilityElement(children: .ignore).accessibilityLabel(L("guidedPanel.stateProgressAccessibility"))
            .accessibilityValue(text)
        case .list(let items):
            VStack(alignment: .leading, spacing: 1) {
                ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                    Text(verbatim: item).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
                }
            }
        case .label(let text):
            Text(verbatim: text).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
        case .taskList(let items):
            // §1.17: the pane's background agents and shells, with their elapsed time.
            BackgroundTaskRows(tasks: items)
        }
    }

    // MARK: 5 · group map

    private var groupMap: some View {
        HStack(spacing: DesignMetrics.Spacing.xs) {
            ForEach(manifest.groups) { item in
                let selected = item.id == group?.id
                Button { selection.groupId = item.id } label: {
                    VStack(alignment: .leading, spacing: 1) {
                        Text(verbatim: item.title).font(.system(size: 11, weight: .semibold, design: .monospaced))
                        Text(verbatim: item.axis ?? "").font(.system(size: 9)).foregroundStyle(.secondary)
                    }
                    .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.xs).frame(maxWidth: .infinity, alignment: .leading)
                    .background(selected ? Palette.accent.opacity(0.16) : Palette.subtle, in: RoundedRectangle(cornerRadius: 7))
                    .overlay { RoundedRectangle(cornerRadius: 7).stroke(selected ? Palette.accent.opacity(0.7) : Color.clear) }
                    .contentShape(RoundedRectangle(cornerRadius: 7))
                }
                .buttonStyle(.plain).help(item.question ?? item.title)
                .accessibilityLabel(item.title).accessibilityAddTraits(selected ? .isSelected : [])
                .accessibilityIdentifier("mighty-group-\(item.id)-\(session.id)")
            }
        }
    }

    // MARK: 6 · attachments

    /// The group whose recommendation the built-in feature feeds, when there
    /// is one; otherwise the row belongs to the whole style (§6.1).
    private var showsAttachments: Bool {
        guard !manifest.capabilities.isEmpty else { return false }
        guard let named = evaluator.recommendGroupId else { return true }
        return group?.id == named
    }
    /// The empty line the feature itself supplies (§1.8).
    private var attachmentEmptyDetail: String {
        manifest.capabilities.compactMap(StyleCapabilityID.emptyDetail).first ?? ""
    }

    private var attachmentRow: some View {
        let items = store.styleChips(style, for: session)
        return HStack(spacing: DesignMetrics.Spacing.sm) {
            Image(systemName: "folder").font(.system(size: 10)).foregroundStyle(.secondary)
            if items.isEmpty {
                Text(verbatim: attachmentEmptyDetail).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                Spacer(minLength: 0)
            } else {
                attachmentChips(items)
            }
            Button { store.refreshStyleCapabilities(style, for: session) } label: {
                Image(systemName: "arrow.clockwise").font(.system(size: 9))
            }
            .buttonStyle(.plain).help(L("guidedPanel.reloadHelp"))
        }
        .accessibilityElement(children: .contain).accessibilityIdentifier("mighty-attachments-\(session.id)")
    }

    private func attachmentChips(_ items: [StyleAttachmentItem]) -> some View {
        let detail = items.first?.detail ?? ""
        return ScrollView(.horizontal) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                if !detail.isEmpty {
                    Text(verbatim: detail).font(.system(size: 10)).foregroundStyle(.secondary)
                        .padding(.horizontal, DesignMetrics.Spacing.xs).padding(.vertical, 1).background(Palette.subtle, in: Capsule()).lineLimit(1)
                }
                // Six chips on screen however many the payload carries (§1.8).
                ForEach(items.prefix(StyleLimits.maximumCasebookChips)) { item in
                    Button(action: { open(item) }) { Text(verbatim: item.title) }
                        .buttonStyle(.plain).font(.system(size: 10, design: .monospaced))
                        .foregroundStyle(item.openPath == nil ? Color.secondary : Palette.accent)
                        .disabled(item.openPath == nil)
                        .help(item.openPath.map { L("guidedPanel.openPathPrefix") + $0 } ?? item.title)
                }
            }
        }
        .scrollIndicators(.hidden)
    }

    private func open(_ item: StyleAttachmentItem) {
        guard let path = item.openPath else { return }
        NSWorkspace.shared.open(URL(fileURLWithPath: path))
    }

    // MARK: 7 · actions

    /// A sequence in flight offers nothing to press, so the row is a spinner
    /// and the guidance line below it says what is running (§6.1).
    private var progressRow: some View {
        HStack(spacing: DesignMetrics.Spacing.sm) {
            ProgressView().controlSize(.small)
            Spacer(minLength: 0)
        }
        .accessibilityElement(children: .ignore).accessibilityLabel(L("guidedPanel.progressAccessibility"))
        .accessibilityIdentifier("mighty-progress-\(session.id)")
    }

    private func actionRow(_ chips: StyleChipList) -> some View {
        let recommended = chips.recommendedId
        return Group {
            if chips.grid {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                    ScrollView {
                        LazyVGrid(columns: columns, alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                            ForEach(chips.actions) { action in chip(action, chips: chips, recommended: recommended) }
                        }
                    }
                    .frame(maxHeight: Self.gridHeight(count: chips.actions.count)).scrollIndicators(.hidden)
                    resetChip(chips.reset)
                }
            } else {
                ScrollView(.horizontal) {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        ForEach(chips.actions) { action in chip(action, chips: chips, recommended: recommended) }
                        resetChip(chips.reset)
                    }
                }
                .scrollIndicators(.hidden)
            }
        }
    }

    private func chip(_ action: StyleAction, chips: StyleChipList, recommended: String?) -> some View {
        GuidedActionChip(action: action, prominent: action.id == chips.prominentId,
                         recommended: action.id == recommended,
                         disabled: action.requiresText && draft.isEmpty, sessionID: session.id) {
            onPrepare()
            selection.startingNew = false
            store.sendStyleAction(session.id, actionId: action.id, text: store.drafts[session.id] ?? "")
        }
    }

    @ViewBuilder private func resetChip(_ reset: StyleResetChip) -> some View {
        switch reset {
        case .none:
            EmptyView()
        case .reset(let title):
            Button { selection.startingNew = true } label: { Label(title, systemImage: "plus") }
                .controlSize(.small).help(L("guidedPanel.resetHelp"))
                .accessibilityIdentifier("mighty-reset-\(session.id)")
        case .cancel:
            Button(L("guidedPanel.cancelButton")) { selection.startingNew = false }
                .controlSize(.small)
                .accessibilityIdentifier("mighty-reset-cancel-\(session.id)")
        }
    }

    /// Tall enough for the rows at the narrowest pane (three chips a row),
    /// capped so a long catalogue scrolls instead of pushing the editor down.
    static func gridHeight(count: Int) -> CGFloat {
        let rows = max(1, (count + 2) / 3)
        return min(92, CGFloat(rows) * 27 + CGFloat(rows - 1) * 5)
    }
}
