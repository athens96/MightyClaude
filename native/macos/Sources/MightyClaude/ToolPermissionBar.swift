import SwiftUI
import MightyCore

/// A pending request is actionable only through these explicit buttons.
/// Return in the composer never grants tool access.
struct ToolPermissionBar: View {
    @EnvironmentObject private var store: AppStore
    let sessionId: String

    /// In a guided style the composer itself shows the agent's questions,
    /// and the style's own listed tools are approved without a card.
    /// A plan the Mighty diagram draws in its flow is not docked here too.
    private var visibleRequests: [ToolPermissionRequest] {
        guard let session = store.snapshot.sessions.first(where: { $0.id == sessionId }) else { return store.toolPermissions[sessionId] ?? [] }
        let requests = store.usesGuidedStyle(session) ? store.guidedVisibleRequests(sessionId) : store.toolPermissions[sessionId] ?? []
        guard let plan = PlanCardSupport.pendingPlan(requests) else { return requests }
        let showsDiagram = session.kind == "claude" && MightyGraphSupport.providers.contains(session.provider)
            && session.agentViewMode == "mighty" && session.mightyViewMode == .diagram
        guard PlanCardSupport.diagramPlanRunID(plan, showsDiagram: showsDiagram, runs: session.mightyGraphRuns) != nil else { return requests }
        return requests.filter { $0.id != plan.id || $0.runId != plan.runId }
    }

    @ViewBuilder var body: some View {
        let requests = visibleRequests
        if let request = requests.first {
            Group {
                if request.canAnswerPlan, request.plan != nil {
                    PlanApprovalCard(sessionId: sessionId, request: request, count: requests.count)
                        .layoutPriority(1)
                } else if let questionnaire = request.questionnaire {
                    UserQuestionnaireCard(sessionId: sessionId, request: request,
                                          questionnaire: questionnaire,
                                          count: requests.count)
                        .layoutPriority(1)
                } else {
                    ToolPermissionCard(sessionId: sessionId, request: request,
                                       count: requests.count)
                }
            }.id(store.permissionResponseKey(sessionId: sessionId, request: request))
        }
    }
}

private struct ToolPermissionCard: View {
    @EnvironmentObject private var store: AppStore
    let sessionId: String
    let request: ToolPermissionRequest
    let count: Int
    @ViewState private var showsJSON = false
    private var busy: Bool { store.permissionResponses.contains(store.permissionResponseKey(sessionId: sessionId, request: request)) }
    private var presentation: ToolPermissionPresentation { ToolPermissionPresentation.make(toolName: request.toolName, inputJSON: request.inputJSON) }

    var body: some View {
        let presentation = presentation
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                PaneWaitBadge(systemImage: "hand.raised.fill")
                Text(L("permission.bar.title", ["title": presentation.title])).font(.system(size: 13, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1)
                Text(request.toolName).font(.system(size: 11.5, design: .monospaced)).foregroundStyle(Palette.ink2).lineLimit(1)
                Spacer(minLength: 0)
                if count > 1 { Text(L("phone.questionnaire.waiting", ["count": "\(count)"])).foregroundStyle(Palette.ink2) }
            }.font(.system(size: 12))
            if let headline = presentation.headline {
                Text(headline).font(.system(size: 14, weight: .bold)).foregroundStyle(Palette.ink).textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true).frame(maxWidth: .infinity, alignment: .leading)
                    .accessibilityIdentifier("permission-headline")
            }
            ScrollView {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                    ForEach(Array(presentation.fields.enumerated()), id: \.offset) { _, field in
                        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                            Text(field.label).font(.system(size: 10, weight: .semibold)).foregroundStyle(Palette.ink2)
                            if field.code {
                                Text(field.value).font(.system(size: 11, design: .monospaced)).textSelection(.enabled)
                                    .fixedSize(horizontal: false, vertical: true).frame(maxWidth: .infinity, alignment: .leading)
                                    .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.sm)
                                    .background(Palette.raised, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                            } else {
                                Text(field.value).font(.system(size: 11)).textSelection(.enabled)
                                    .fixedSize(horizontal: false, vertical: true).frame(maxWidth: .infinity, alignment: .leading)
                            }
                        }
                    }
                    if presentation.fields.isEmpty, presentation.headline == nil {
                        Text(request.summary).font(.system(size: 12, design: .monospaced)).textSelection(.enabled)
                    }
                    if let path = request.blockedPath, !path.isEmpty { Text(L("permission.bar.blockedPath", ["path": path])).font(.system(size: 10)) }
                    if let reason = request.reason, !reason.isEmpty { Text(reason).font(.system(size: 10)).foregroundStyle(Palette.ink2).fixedSize(horizontal: false, vertical: true) }
                    DisclosureGroup(L("styles.approval.raw"), isExpanded: $showsJSON) {
                        Text(request.inputJSON).font(.system(size: 10, design: .monospaced)).textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading).padding(.top, DesignMetrics.Spacing.xs)
                    }.font(.system(size: 10)).accessibilityIdentifier("permission-json")
                }.frame(maxWidth: .infinity, alignment: .leading).padding(.trailing, DesignMetrics.Spacing.xs)
            }.frame(maxHeight: 180)
            if !request.canAllow {
                Text(L("permission.bar.cannotAllow"))
                    .font(.system(size: 10)).foregroundStyle(Palette.ink2).fixedSize(horizontal: false, vertical: true)
            }
            if let error = store.permissionErrors[sessionId] {
                Text(error).font(.system(size: 10)).foregroundStyle(Palette.errText).lineLimit(2)
            }
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Text(L("permission.bar.thisRequestOnly")).font(.system(size: 11)).foregroundStyle(Palette.ink2)
                Spacer(minLength: 0)
                if busy { ProgressView().controlSize(.mini) }
                Button(L("permission.deny")) { answer(false) }.buttonStyle(PaneCardButtonStyle()).accessibilityIdentifier("permission-deny")
                Button(L("permission.allowOnce")) { answer(true) }
                    .buttonStyle(PaneCardButtonStyle(prominent: true)).disabled(!request.canAllow)
                    .accessibilityIdentifier("permission-allow-once")
            }.disabled(busy)
        }
        // Concept D: the same amber-edged card as a question, above the composer.
        .paneWaitCard()
        .padding(.horizontal, DesignMetrics.Spacing.md).padding(.top, DesignMetrics.Spacing.sm)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("permission-request-\(request.id)")
    }

    private func answer(_ allow: Bool) {
        Task { await store.answerPermission(sessionId: sessionId, request: request, allow: allow) }
    }
}
