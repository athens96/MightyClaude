import SwiftUI
import MightyCore

/// A plan opened like a document: the plan card's 펼치기, or an answered plan.
struct PlanDocument: Identifiable {
    let id: String
    let title: String
    let subtitle: String
    let plan: String
}

struct PlanDocumentSheet: View {
    let document: PlanDocument
    let onClose: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "list.bullet.clipboard").foregroundStyle(Palette.accent)
                Text(document.title).font(.system(size: 14, weight: .bold)).foregroundStyle(Palette.ink)
                Text(document.subtitle).font(.system(size: 11)).foregroundStyle(Palette.ink2).lineLimit(1)
                Spacer(minLength: 0)
                Button(L("plan.card.close"), action: onClose).buttonStyle(PaneCardButtonStyle())
                    .keyboardShortcut(.cancelAction)
                    .accessibilityIdentifier("plan-document-close")
            }
            .padding(.horizontal, DesignMetrics.Inset.sheet).padding(.vertical, DesignMetrics.Spacing.md)
            Divider().overlay(Palette.border)
            ScrollView {
                AgentMarkdownView(source: document.plan).textSelection(.enabled)
                    .padding(DesignMetrics.Spacing.lg).frame(maxWidth: 860, alignment: .leading)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .background(Palette.panel)
        }
        .frame(minWidth: 620, idealWidth: 860, minHeight: 460, idealHeight: 720)
        .background(Palette.panel)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("plan-document-\(document.id)")
    }
}

/// Claude's plan waiting for the user (ExitPlanMode): the Markdown plan and the
/// four answers. Docked above the composer, or the large card in the Mighty
/// diagram where the run's result will go.
struct PlanApprovalCard: View {
    @EnvironmentObject private var store: AppStore
    let sessionId: String
    let request: ToolPermissionRequest
    var count = 1
    /// Drawn in the diagram: it fills its block instead of docking.
    var inDiagram = false
    @ViewState private var revising = false
    @ViewState private var feedback = ""

    private var plan: String { request.plan ?? "" }
    private var busy: Bool { store.permissionResponses.contains(store.permissionResponseKey(sessionId: sessionId, request: request)) }
    private var canAnswer: Bool { request.canAnswerPlan && request.state == "pending" }
    private var received: String { PlanCardSupport.receivedText(request.receivedAt ?? "") }

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                PaneWaitBadge(systemImage: "list.bullet.clipboard")
                Text(L("plan.card.title")).font(.system(size: 13, weight: .bold)).foregroundStyle(Palette.ink)
                if request.receivedAt != nil {
                    Text(received).font(.system(size: 11)).foregroundStyle(Palette.ink2).monospacedDigit()
                        .accessibilityIdentifier("plan-received")
                }
                Spacer(minLength: 0)
                if count > 1 { Text(L("phone.questionnaire.waiting", ["count": "\(count)"])).font(.system(size: 11)).foregroundStyle(Palette.ink2) }
                Button {
                    store.planDocument = PlanDocument(id: request.id, title: L("plan.card.title"), subtitle: received, plan: plan)
                } label: { Label(L("plan.card.expand"), systemImage: "arrow.up.left.and.arrow.down.right") }
                    .buttonStyle(PaneCardButtonStyle())
                    .disabled(store.hasModal)
                    .accessibilityIdentifier("plan-expand")
            }

            ScrollView {
                AgentMarkdownView(source: plan).textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, DesignMetrics.Spacing.md).padding(.vertical, DesignMetrics.Spacing.md)
            }
            .background(Palette.raised, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            .frame(minHeight: 80, maxHeight: inDiagram ? .infinity : 260)
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier("plan-text")

            if revising {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                    TextField(L("plan.card.revisePlaceholder"), text: $feedback, axis: .vertical)
                        .textFieldStyle(.plain).font(.system(size: 12)).lineLimit(2...6)
                        .padding(.horizontal, DesignMetrics.Spacing.md).padding(.vertical, DesignMetrics.Spacing.sm)
                        .background(Palette.panel, in: RoundedRectangle(cornerRadius: 9, style: .continuous))
                        .overlay { RoundedRectangle(cornerRadius: 9, style: .continuous).strokeBorder(Palette.border, lineWidth: 1).allowsHitTesting(false) }
                        .accessibilityIdentifier("plan-revise-text")
                    if PlanCardSupport.feedbackTooLong(feedback) {
                        Text(L("plan.error.feedbackTooLong")).font(.system(size: 11)).foregroundStyle(Palette.errText)
                            .fixedSize(horizontal: false, vertical: true).accessibilityIdentifier("plan-revise-too-long")
                    }
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        Spacer(minLength: 0)
                        Button(L("plan.card.reviseClose")) { revising = false }
                            .buttonStyle(PaneCardButtonStyle()).accessibilityIdentifier("plan-revise-close")
                        Button(L("plan.card.reviseSend")) { answer(.revise(feedback: feedback)) }
                            .buttonStyle(PaneCardButtonStyle(prominent: true))
                            .disabled(!canAnswer || !PlanCardSupport.canSendRevise(feedback))
                            .accessibilityIdentifier("plan-revise-send")
                    }
                }
            }

            if let error = store.permissionErrors[sessionId] {
                Text(error).font(.system(size: 11)).foregroundStyle(Palette.errText)
                    .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                    .accessibilityIdentifier("plan-error")
            }

            ViewThatFits(in: .horizontal) {
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    Text(L("plan.card.hint")).font(.system(size: 11)).foregroundStyle(Palette.ink2).lineLimit(1)
                    Spacer(minLength: 0)
                    buttons
                }
                VStack(alignment: .trailing, spacing: DesignMetrics.Spacing.sm) {
                    HStack(spacing: DesignMetrics.Spacing.sm) { Spacer(minLength: 0); secondaryButtons }
                    HStack(spacing: DesignMetrics.Spacing.sm) { Spacer(minLength: 0); primaryButtons }
                }
            }
            .disabled(busy || !canAnswer)
        }
        // Clear of the diagram's resize handle in the block's corner.
        .padding(.bottom, inDiagram ? 12 : 0)
        .paneWaitCard()
        .frame(maxHeight: inDiagram ? .infinity : nil)
        .padding(.horizontal, inDiagram ? 0 : 12).padding(.top, inDiagram ? 0 : 8)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("plan-card-\(request.id)")
    }

    @ViewBuilder private var buttons: some View {
        secondaryButtons
        primaryButtons
    }
    @ViewBuilder private var secondaryButtons: some View {
        if busy { ProgressView().controlSize(.mini).accessibilityIdentifier("plan-sending") }
        Button(L("plan.card.cancel")) { answer(.cancel) }
            .buttonStyle(PaneCardButtonStyle()).accessibilityIdentifier("plan-cancel")
        Button(L("plan.card.revise")) { revising = true }
            .buttonStyle(PaneCardButtonStyle()).disabled(revising).accessibilityIdentifier("plan-revise")
    }
    @ViewBuilder private var primaryButtons: some View {
        Button(L("plan.card.approveConfirm")) { answer(.approveConfirmEach) }
            .buttonStyle(PaneCardButtonStyle()).accessibilityIdentifier("plan-approve-confirm")
        Button(L("plan.card.approveAuto")) { answer(.approveAutoEdit) }
            .buttonStyle(PaneCardButtonStyle(accent: true)).accessibilityIdentifier("plan-approve-auto")
    }

    private func answer(_ decision: PlanDecision) {
        Task { await store.answerPlan(sessionId: sessionId, request: request, decision: decision) }
    }
}

/// An answered plan in the pane's history: its outcome, folded to the plan's
/// first line, opening to the whole plan.
struct PlanRecordView: View {
    @EnvironmentObject private var store: AppStore
    let record: PlanRecord
    let expanded: Bool
    let onToggle: () -> Void
    /// The diagram's block fills its frame; the transcript's entry is a row.
    var inDiagram = false

    private var tone: DesignTone {
        switch record.outcome {
        case .approvedAuto, .approvedConfirm: return .done
        case .revised: return .wait
        case .cancelled: return .stop
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "list.bullet.clipboard").foregroundStyle(Palette.accent)
                Text(L("plan.card.title")).font(.system(size: 12, weight: .bold)).foregroundStyle(Palette.ink)
                MightyStatusPill(text: PlanCardSupport.outcomeTitle(record.outcome), tone: tone)
                Text(PlanCardSupport.timeText(record.decidedAt)).font(.system(size: 10)).foregroundStyle(Palette.ink2).monospacedDigit()
                Spacer(minLength: 0)
                Button {
                    store.planDocument = PlanDocument(id: record.id, title: L("plan.card.title") + " · " + PlanCardSupport.outcomeTitle(record.outcome),
                                                      subtitle: PlanCardSupport.receivedText(record.receivedAt), plan: record.plan)
                } label: { Image(systemName: "arrow.up.left.and.arrow.down.right") }
                    .buttonStyle(.plain).foregroundStyle(Palette.ink2).disabled(store.hasModal)
                    .help(L("plan.card.expand")).accessibilityLabel(L("plan.card.expand"))
                    .accessibilityIdentifier("plan-record-expand-\(record.id)")
                Button(action: onToggle) { Image(systemName: expanded ? "chevron.up" : "chevron.down") }
                    .buttonStyle(.plain).foregroundStyle(Palette.ink2)
                    .help(expanded ? L("plan.history.hide") : L("plan.history.show"))
                    .accessibilityLabel(expanded ? L("plan.history.hide") : L("plan.history.show"))
                    .accessibilityIdentifier("plan-record-toggle-\(record.id)")
            }
            if expanded {
                ScrollView {
                    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                        AgentMarkdownView(source: record.plan).textSelection(.enabled)
                        if record.planTruncated == true {
                            Text(L("plan.history.truncated")).font(.system(size: 10)).foregroundStyle(Palette.ink2)
                        }
                        feedbackView
                    }.frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: inDiagram ? .infinity : 320)
            } else {
                Text(PlanCardSupport.headline(record.plan)).font(.system(size: 11)).foregroundStyle(Palette.ink2)
                    .lineLimit(inDiagram ? 2 : 1).frame(maxWidth: .infinity, alignment: .leading)
                feedbackView
            }
            if inDiagram { Spacer(minLength: 0) }
        }
        .padding(.horizontal, DesignMetrics.Spacing.md).padding(.vertical, DesignMetrics.Spacing.sm)
        .frame(maxWidth: .infinity, maxHeight: inDiagram ? .infinity : nil, alignment: .topLeading)
        .mightyBlockCard()
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("plan-record-\(record.id)")
    }

    @ViewBuilder private var feedbackView: some View {
        if let feedback = record.feedback, !feedback.isEmpty {
            (Text(L("plan.history.feedback") + " · ").bold() + Text(feedback))
                .font(.system(size: 10.5)).foregroundStyle(Palette.waitText)
                .lineLimit(expanded ? nil : 1).textSelection(.enabled)
        }
    }
}

/// The default view's plan history: one folded line above the composer that
/// opens to the pane's answered plans, newest first.
struct PlanHistoryStrip: View {
    let sessionId: String
    let records: [PlanRecord]
    @ViewState private var open = false
    @ViewState private var expanded = Set<String>()

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            Button { open.toggle() } label: {
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    Image(systemName: "list.bullet.clipboard").foregroundStyle(Palette.accent)
                    Text(L("plan.history.title", ["count": "\(records.count)"])).font(.system(size: 11, weight: .semibold)).foregroundStyle(Palette.ink)
                    if let last = records.last {
                        Text(PlanCardSupport.outcomeTitle(last.outcome) + " · " + PlanCardSupport.headline(last.plan))
                            .font(.system(size: 11)).foregroundStyle(Palette.ink2).lineLimit(1)
                    }
                    Spacer(minLength: 0)
                    Image(systemName: open ? "chevron.up" : "chevron.down").font(.system(size: 10, weight: .semibold)).foregroundStyle(Palette.ink2)
                }.contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(L("plan.history.title", ["count": "\(records.count)"]))
            .accessibilityValue(open ? L("plan.history.hide") : L("plan.history.show"))
            .accessibilityIdentifier("plan-history-\(sessionId)")
            if open {
                ScrollView {
                    VStack(spacing: DesignMetrics.Spacing.sm) {
                        ForEach(records.reversed()) { record in
                            PlanRecordView(record: record, expanded: expanded.contains(record.id)) {
                                if !expanded.insert(record.id).inserted { expanded.remove(record.id) }
                            }
                        }
                    }
                }.frame(maxHeight: 280)
            }
        }
        .padding(.horizontal, DesignMetrics.Spacing.md).padding(.vertical, DesignMetrics.Spacing.sm)
        .background(Palette.panel, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay { RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Palette.border, lineWidth: 1).allowsHitTesting(false) }
        .padding(.horizontal, DesignMetrics.Spacing.md).padding(.top, DesignMetrics.Spacing.sm)
    }
}
