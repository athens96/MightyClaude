import Foundation
import MightyCore

extension AppStore {
    /// A deliberate reset covers local Claude panes only. Running requests keep
    /// their configuration, so reset waits until all affected panes are idle.
    func resetClaudeModels() {
        guard !ending, !claudeModelResetInProgress else { return }
        let sessions = snapshot.sessions.filter { $0.provider == "claude" && localModelContext(for: $0) != nil }
        guard !sessions.contains(where: { $0.status == "running" || pendingRuns.contains($0.id) }) else {
            cliAccountMessages["claude"] = "Claude 실행이 끝난 뒤 모델을 초기화하세요."
            return
        }
        claudeModelResetInProgress = true
        cliAccountMessages["claude"] = "저장된 모델 선택을 초기화하고 현재 CLI 설정에서 목록을 다시 불러오는 중입니다."
        for session in sessions {
            updateSession(session.id) { $0.model = "default"; $0.settings.effort = "default" }
            modelPriorCatalogs.removeValue(forKey: session.id)
        }
        modelRefreshSelections = modelRefreshSelections.filter { $0.key.provider != "claude" }
        localModels.discard(provider: "claude")
        modelRefreshRevision &+= 1
        let contexts = snapshot.workspaces.filter { $0.remote == nil }.map {
            LocalModelContext(workspaceID: $0.id, path: $0.path, provider: "claude")
        }
        Task { [weak self] in
            guard let self else { return }
            await providers.discardModelCatalogs(provider: "claude")
            guard !ending else { claudeModelResetInProgress = false; return }
            // Start together; each workspace can have different CLI settings.
            let tasks = contexts.map { self.localModels.request($0, force: true, invalidate: true) }
            var failed = 0
            for task in tasks {
                let result = await task.value
                if result?.modelCatalog.source != "cli" { failed += 1 }
            }
            claudeModelResetInProgress = false
            modelRefreshRevision &+= 1
            guard !ending else { return }
            if contexts.isEmpty {
                cliAccountMessages["claude"] = "모델 캐시를 초기화했습니다. 로컬 워크스페이스를 열면 목록을 새로 불러옵니다."
            } else if failed > 0 {
                cliAccountMessages["claude"] = "모델 선택과 캐시는 초기화했지만 \(failed)개 워크스페이스의 CLI 모델 목록을 확인하지 못했습니다. 로그인·Bedrock 설정을 완료한 뒤 다시 불러오세요."
            } else {
                cliAccountMessages["claude"] = "Claude 모델 선택을 초기화하고 \(contexts.count)개 워크스페이스의 CLI 모델 목록을 새로 불러왔습니다. 모델 호출 권한은 실제 실행 시 확인됩니다."
            }
        }
    }
}
