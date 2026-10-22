import SwiftUI
import MightyCore

/// A pending request is actionable only through these explicit buttons.
/// Return in the composer never grants tool access.
struct ToolPermissionBar: View {
    @EnvironmentObject private var store: AppStore
    let sessionId: String

    /// In a guided style the composer itself shows the agent's questions,
    /// and the style's own listed tools are approved without a card. A plan
    /// the Mighty diagram draws leaves only its answers here — or, in a guided
    /// style, in the panel instead (`PlanCardSupport.composerPlace`).
    private var visibleRequests: (requests: [ToolPermissionRequest], planActions: Bool) {
        guard let session = store.snapshot.sessions.first(where: { $0.id == sessionId }) else { return (store.toolPermissions[sessionId] ?? [], false) }
        let guided = store.usesGuidedStyle(session)
        let requests = guided ? store.guidedVisibleRequests(sessionId) : store.toolPermissions[sessionId] ?? []
        guard let plan = PlanCardSupport.pendingPlan(requests) else { return (requests, false) }
        switch PlanCardSupport.composerPlace(plan, showsDiagram: PlanCardSupport.showsDiagram(session), runs: session.mightyGraphRuns, guided: guided) {
        case .panelActions?: return (requests.filter { $0.id != plan.id || $0.runId != plan.runId }, false)
        case .barActions?: return (requests, true)
        case .card?, nil: return (requests, false)
        }
    }

    @ViewBuilder var body: some View {
        let (requests, planActions) = visibleRequests
        if let request = requests.first {
            Group {
                if request.canAnswerPlan, request.plan != nil {
                    if planActions {
                        PlanActionsCard(sessionId: sessionId, request: request, count: requests.count, docked: true)
                    } else {
                        PlanApprovalCard(sessionId: sessionId, request: request, count: requests.count)
                            .layoutPriority(1)
                    }
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
        } else if let card = store.antigravityBoard[sessionId] {
            // Antigravity asks once its turn is over (`AntigravityPermissions.swift`).
            AntigravityPermissionCard(sessionId: sessionId, card: card).id(store.antigravityResponseKey(sessionId: sessionId, card: card))
        }
    }
}

/// The Claude card's shape for what an Antigravity turn was refused: every
/// action, then 거절 / 항상 허용 / 이번만 허용. An allow continues the same
/// conversation with the rules in agy's settings.
private struct AntigravityPermissionCard: View {
    @EnvironmentObject private var store: AppStore
    let sessionId: String
    let card: AntigravityPermissionRequest
    private var busy: Bool { store.antigravityResponses.contains(store.antigravityResponseKey(sessionId: sessionId, card: card)) }

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                PaneWaitBadge(systemImage: "hand.raised.fill")
                Text(L("antigravity.permission.title")).font(.ui(size: 13, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(2)
                Spacer(minLength: 0)
            }
            Text(L("antigravity.permission.subtitle")).font(.ui(size: 11)).foregroundStyle(Palette.ink2).fixedSize(horizontal: false, vertical: true)
            ScrollView {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                    ForEach(Array(card.actions.enumerated()), id: \.offset) { _, action in
                        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                            Text(AntigravityPermissionFlow.kindLabel(action.kind) + " · " + action.displayName)
                                .font(.ui(size: 10, weight: .semibold)).foregroundStyle(Palette.ink2)
                            if let target = action.target {
                                if action.kind == .command || action.kind == .file {
                                    Text(target).font(.code(size: 11)).textSelection(.enabled)
                                        .fixedSize(horizontal: false, vertical: true).frame(maxWidth: .infinity, alignment: .leading)
                                        .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.sm)
                                        .background(Palette.raised, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                                } else {
                                    Text(target).font(.ui(size: 12, weight: .semibold)).foregroundStyle(Palette.ink).textSelection(.enabled)
                                        .fixedSize(horizontal: false, vertical: true)
                                }
                            }
                            if let line = AntigravityPermissionFlow.ruleLine(action) {
                                Text(line).font(.code(size: 10)).foregroundStyle(Palette.ink2).lineLimit(3).textSelection(.enabled)
                                if let reason = AntigravityPermissionFlow.onceOnlyText(action) {
                                    Text(reason).font(.ui(size: 10)).foregroundStyle(Palette.waitText).fixedSize(horizontal: false, vertical: true)
                                }
                            } else {
                                Text(L("antigravity.permission.noRule")).font(.ui(size: 10)).foregroundStyle(Palette.waitText).fixedSize(horizontal: false, vertical: true)
                            }
                        }
                    }
                }.frame(maxWidth: .infinity, alignment: .leading).padding(.trailing, DesignMetrics.Spacing.xs)
            }.frame(maxHeight: 180)
            if let error = store.antigravityPermissionErrors[sessionId] {
                Text(error).font(.ui(size: 10)).foregroundStyle(Palette.errText).fixedSize(horizontal: false, vertical: true).lineLimit(3)
            }
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Text(L("antigravity.permission.scopeNote")).font(.ui(size: 10)).foregroundStyle(Palette.ink2).fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
                if busy { ProgressView().controlSize(.mini) }
                Button(L("antigravity.permission.deny")) { answer(.deny) }.buttonStyle(PaneCardButtonStyle()).accessibilityIdentifier("antigravity-permission-deny")
                // A rule that may be allowed only once has no "항상 허용" (the reason is under it).
                Button(L("antigravity.permission.allowAlways")) { answer(.always) }.buttonStyle(PaneCardButtonStyle())
                    .disabled(!card.canAllowAlways).help(card.canAllow && !card.canAllowAlways ? (card.actions.lazy.compactMap { AntigravityPermissionFlow.onceOnlyText($0) }.first ?? L("antigravity.permission.onceOnly")) : "")
                    .accessibilityIdentifier("antigravity-permission-allow-always")
                Button(L("permission.allowOnce")) { answer(.once) }.buttonStyle(PaneCardButtonStyle(prominent: true))
                    .disabled(!card.canAllow).accessibilityIdentifier("antigravity-permission-allow-once")
            }.disabled(busy)
        }
        .paneWaitCard()
        .padding(.horizontal, DesignMetrics.Spacing.md).padding(.top, DesignMetrics.Spacing.sm)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("antigravity-permission-\(card.id)")
    }

    private func answer(_ decision: AntigravityPermissionDecision) {
        Task { await store.answerAntigravityPermission(sessionId: sessionId, request: card, decision: decision) }
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
                Text(L("permission.bar.title", ["title": presentation.title])).font(.ui(size: 13, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1)
                Text(request.toolName).font(.ui(size: 11.5, design: .monospaced)).foregroundStyle(Palette.ink2).lineLimit(1)
                Spacer(minLength: 0)
                if count > 1 { Text(L("phone.questionnaire.waiting", ["count": "\(count)"])).foregroundStyle(Palette.ink2) }
            }.font(.ui(size: 12))
            if let headline = presentation.headline {
                Text(headline).font(.ui(size: 14, weight: .bold)).foregroundStyle(Palette.ink).textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true).frame(maxWidth: .infinity, alignment: .leading)
                    .accessibilityIdentifier("permission-headline")
            }
            ScrollView {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                    ForEach(Array(presentation.fields.enumerated()), id: \.offset) { _, field in
                        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                            Text(field.label).font(.ui(size: 10, weight: .semibold)).foregroundStyle(Palette.ink2)
                            if field.code {
                                Text(field.value).font(.code(size: 11)).textSelection(.enabled)
                                    .fixedSize(horizontal: false, vertical: true).frame(maxWidth: .infinity, alignment: .leading)
                                    .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.sm)
                                    .background(Palette.raised, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                            } else {
                                Text(field.value).font(.ui(size: 11)).textSelection(.enabled)
                                    .fixedSize(horizontal: false, vertical: true).frame(maxWidth: .infinity, alignment: .leading)
                            }
                        }
                    }
                    if presentation.fields.isEmpty, presentation.headline == nil {
                        Text(request.summary).font(.code(size: 12)).textSelection(.enabled)
                    }
                    if let path = request.blockedPath, !path.isEmpty { Text(L("permission.bar.blockedPath", ["path": path])).font(.ui(size: 10)) }
                    if let reason = request.reason, !reason.isEmpty { Text(reason).font(.ui(size: 10)).foregroundStyle(Palette.ink2).fixedSize(horizontal: false, vertical: true) }
                    DisclosureGroup(L("styles.approval.raw"), isExpanded: $showsJSON) {
                        Text(request.inputJSON).font(.code(size: 10)).textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading).padding(.top, DesignMetrics.Spacing.xs)
                    }.font(.ui(size: 10)).accessibilityIdentifier("permission-json")
                }.frame(maxWidth: .infinity, alignment: .leading).padding(.trailing, DesignMetrics.Spacing.xs)
            }.frame(maxHeight: 180)
            if !request.canAllow {
                Text(L("permission.bar.cannotAllow"))
                    .font(.ui(size: 10)).foregroundStyle(Palette.ink2).fixedSize(horizontal: false, vertical: true)
            }
            if let error = store.permissionErrors[sessionId] {
                Text(error).font(.ui(size: 10)).foregroundStyle(Palette.errText).lineLimit(2)
            }
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Text(L("permission.bar.thisRequestOnly")).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
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
