import SwiftUI
import MightyCore

struct CLIUpdateSettingsSection: View {
    @EnvironmentObject private var store: AppStore

    var body: some View {
        Section("CLI 업데이트") {
            Toggle("앱 시작 시 CLI 자동 업데이트", isOn: Binding(
                get: { store.snapshot.autoUpdateCLIs == true },
                set: { store.snapshot.autoUpdateCLIs = $0 }
            ))
            .accessibilityIdentifier("cli-auto-update")
            Text("이 Mac에 설치된 Claude Code·Codex·Gemini CLI를 기존 설치 방식으로 업데이트합니다.")
                .font(.system(size: 11)).foregroundStyle(.secondary)
            HStack(spacing: 8) {
                if store.isUpdatingCLIs {
                    ProgressView().controlSize(.small)
                    Text(store.updatingCLI.map { "\(ProviderOptions.label($0)) 업데이트 중…" } ?? "설치 정보 확인 중…")
                        .font(.system(size: 11)).accessibilityIdentifier("cli-update-progress")
                } else if let finished = store.cliUpdateFinishedAt {
                    Text("마지막 실행 \(finished.formatted(date: .omitted, time: .shortened))")
                        .font(.system(size: 11)).foregroundStyle(.secondary)
                }
                Spacer()
                Button(store.isUpdatingCLIs ? "업데이트 중…" : "업데이트 하기") { store.startCLIUpdates() }
                    .disabled(store.isUpdatingCLIs || store.isManagingPlugins || !store.canManageCLIUpdates)
                    .accessibilityIdentifier("cli-update-now")
            }
            ForEach(ProviderOptions.ids, id: \.self) { provider in
                if let result = store.cliUpdateResults[provider] {
                    VStack(alignment: .leading, spacing: 4) {
                        HStack(spacing: 6) {
                            Image(systemName: icon(result.status))
                                .foregroundStyle(result.status == "failed" ? Color.orange : Color.secondary)
                            Text("\(ProviderOptions.label(provider)) · \(label(result.status))")
                                .font(.system(size: 11, weight: .medium))
                        }
                        if let before = result.beforeVersion, let after = result.afterVersion, before != after {
                            Text("\(before) → \(after)").font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary)
                        }
                        Text(result.detail).font(.system(size: 11)).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                    }
                    .accessibilityElement(children: .combine)
                    .accessibilityIdentifier("cli-update-result-\(provider)")
                }
            }
        }
    }

    private func label(_ status: String) -> String {
        switch status {
        case "updated": "업데이트 완료"
        case "current": "변경 없음"
        case "failed": "업데이트 실패"
        case "cancelled": "취소됨"
        case "busy": "다른 업데이트 진행 중"
        default: "건너뜀"
        }
    }
    private func icon(_ status: String) -> String {
        switch status {
        case "updated", "current": "checkmark.circle.fill"
        case "failed": "exclamationmark.circle"
        default: "info.circle"
        }
    }
}
