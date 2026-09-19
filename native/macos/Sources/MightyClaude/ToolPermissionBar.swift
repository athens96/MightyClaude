import SwiftUI
import MightyCore

/// A pending request is actionable only through these explicit buttons.
/// Return in the composer never grants tool access.
struct ToolPermissionBar: View {
    @EnvironmentObject private var store: AppStore
    let sessionId: String

    /// In a guided style the composer itself shows the agent's questions,
    /// and the style's own listed tools are approved without a card.
    private var visibleRequests: [ToolPermissionRequest] {
        guard let session = store.snapshot.sessions.first(where: { $0.id == sessionId }), store.usesGuidedStyle(session) else { return store.toolPermissions[sessionId] ?? [] }
        return store.guidedVisibleRequests(sessionId)
    }

    @ViewBuilder var body: some View {
        let requests = visibleRequests
        if let request = requests.first {
            Group {
                if let questionnaire = request.questionnaire {
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
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 7) {
                Image(systemName: "hand.raised.fill").foregroundStyle(.orange)
                Text("\(presentation.title) · 승인 요청").fontWeight(.semibold).lineLimit(1)
                Text(request.toolName).foregroundStyle(.secondary).lineLimit(1)
                Spacer(minLength: 0)
                if count > 1 { Text("\(count)개 대기").foregroundStyle(.secondary) }
            }.font(.system(size: 11))
            if let headline = presentation.headline {
                Text(headline).font(.system(size: 12, weight: .medium)).textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true).frame(maxWidth: .infinity, alignment: .leading)
                    .accessibilityIdentifier("permission-headline")
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(Array(presentation.fields.enumerated()), id: \.offset) { _, field in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(field.label).font(.system(size: 9, weight: .medium)).foregroundStyle(.secondary)
                            if field.code {
                                Text(field.value).font(.system(size: 11, design: .monospaced)).textSelection(.enabled)
                                    .fixedSize(horizontal: false, vertical: true).frame(maxWidth: .infinity, alignment: .leading)
                                    .padding(.horizontal, 8).padding(.vertical, 6)
                                    .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 6))
                            } else {
                                Text(field.value).font(.system(size: 11)).textSelection(.enabled)
                                    .fixedSize(horizontal: false, vertical: true).frame(maxWidth: .infinity, alignment: .leading)
                            }
                        }
                    }
                    if presentation.fields.isEmpty, presentation.headline == nil {
                        Text(request.summary).font(.system(size: 12, design: .monospaced)).textSelection(.enabled)
                    }
                    if let path = request.blockedPath, !path.isEmpty { Text("접근 경로: \(path)").font(.system(size: 10)) }
                    if let reason = request.reason, !reason.isEmpty { Text(reason).font(.system(size: 10)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
                    DisclosureGroup("원본 JSON", isExpanded: $showsJSON) {
                        Text(request.inputJSON).font(.system(size: 10, design: .monospaced)).textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading).padding(.top, 4)
                    }.font(.system(size: 10)).accessibilityIdentifier("permission-json")
                }.frame(maxWidth: .infinity, alignment: .leading).padding(.trailing, 4)
            }.frame(maxHeight: 180)
            if !request.canAllow {
                Text("이 요청은 현재 승인 화면에서 허용할 수 없습니다. 거부하거나 실행을 중지하세요.")
                    .font(.system(size: 10)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            if let error = store.permissionErrors[sessionId] {
                Text(error).font(.system(size: 10)).foregroundStyle(.red).lineLimit(2)
            }
            HStack(spacing: 8) {
                Text("이 요청에만 적용").font(.system(size: 10)).foregroundStyle(.secondary)
                Spacer(minLength: 0)
                if busy { ProgressView().controlSize(.mini) }
                Button("거부") { answer(false) }.accessibilityIdentifier("permission-deny")
                Button("이번만 허용") { answer(true) }
                    .buttonStyle(.borderedProminent).disabled(!request.canAllow)
                    .accessibilityIdentifier("permission-allow-once")
            }.controlSize(.small).disabled(busy)
        }
        .padding(12)
        .background(Color.orange.opacity(0.055))
        .overlay(alignment: .top) { Divider() }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("permission-request-\(request.id)")
    }

    private func answer(_ allow: Bool) {
        Task { await store.answerPermission(sessionId: sessionId, request: request, allow: allow) }
    }
}
