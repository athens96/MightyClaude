import SwiftUI
import MightyCore

/// A pending request is actionable only through these explicit buttons.
/// Return in the composer never grants tool access.
struct ToolPermissionBar: View {
    @EnvironmentObject private var store: AppStore
    let sessionId: String

    @ViewBuilder var body: some View {
        if let request = store.toolPermissions[sessionId]?.first {
            ToolPermissionCard(sessionId: sessionId, request: request,
                               count: store.toolPermissions[sessionId]?.count ?? 1)
                .id(store.permissionResponseKey(sessionId: sessionId, request: request))
        }
    }
}

private struct ToolPermissionCard: View {
    @EnvironmentObject private var store: AppStore
    let sessionId: String
    let request: ToolPermissionRequest
    let count: Int
    @ViewState private var expanded = true
    private var busy: Bool { store.permissionResponses.contains(store.permissionResponseKey(sessionId: sessionId, request: request)) }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 7) {
                Image(systemName: "hand.raised.fill").foregroundStyle(.orange)
                Text("\(request.toolName) · 승인 요청").fontWeight(.semibold).lineLimit(1)
                Spacer(minLength: 0)
                if count > 1 { Text("\(count)개 대기").foregroundStyle(.secondary) }
            }.font(.system(size: 11))
            Text(request.summary).font(.system(size: 12, design: .monospaced))
                .lineLimit(2).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
            DisclosureGroup("요청 내용 보기", isExpanded: $expanded) {
                ScrollView {
                    VStack(alignment: .leading, spacing: 6) {
                        if let path = request.blockedPath, !path.isEmpty { Text("접근 경로: \(path)") }
                        if let reason = request.reason, !reason.isEmpty { Text(reason).foregroundStyle(.secondary) }
                        Text(request.inputJSON).font(.system(size: 10, design: .monospaced))
                    }.textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(.vertical, 5)
                }.frame(maxHeight: 100)
            }.font(.system(size: 10))
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
        .accessibilityIdentifier("permission-request-\(request.id)")
    }

    private func answer(_ allow: Bool) {
        Task { await store.answerPermission(sessionId: sessionId, request: request, allow: allow) }
    }
}
