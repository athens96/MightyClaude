import Foundation
import MightyCore

extension AppStore {
    func localModelContext(for session: RunSession) -> LocalModelContext? {
        // A remote workspace's models are the host's (`remoteProviderRuntime`), never probed here.
        guard session.kind != "shell", !FilePaneKind.isReadOnlyPane(session.kind), let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }), workspace.remote == nil else { return nil }
        return LocalModelContext(workspaceID: workspace.id, path: workspace.path, provider: session.provider)
    }

    func refreshModels(for sessionID: String, force: Bool = true, invalidate: Bool = false) {
        // The help capture's demo CLIs have no process to ask for models.
        guard !ending, !helpCapturing, let session = snapshot.sessions.first(where: { $0.id == sessionID }), let key = localModelContext(for: session) else { return }
        let task = localModels.request(key, force: force, invalidate: invalidate)
        modelRefreshRevision &+= 1
        Task { [weak self] in
            _ = await task.value
            guard let self, !self.ending else { return }
            self.modelRefreshRevision &+= 1
        }
    }

    func isRefreshingModels(for session: RunSession) -> Bool {
        guard let key = localModelContext(for: session) else { return false }
        return localModels.isRefreshing(key)
    }

    func invalidateLocalModels(provider: String? = nil) {
        localModels.invalidate(provider: provider)
        if let session = snapshot.sessions.first(where: { $0.id == snapshot.activeSessionId }), provider == nil || session.provider == provider { refreshModels(for: session.id) }
    }

    /// A sign-in or sign-out changed what the provider's CLI lists: every list ProviderService read before it goes
    /// (each workspace's, and the one without a workspace that the runtime summary and another computer's panes
    /// get), then the local menus read again, so no read begun before the change answers for it.
    func modelsAfterAccountChange(_ provider: String) {
        Task { [weak self] in
            guard let self else { return }
            await self.providers.discardModelCatalogs(provider: provider)
            guard !self.ending else { return }
            self.invalidateLocalModels(provider: provider)
        }
    }

    /// A Hermes session keeps the picker it offered as the menu's list (`HermesAgent.saveModels`): read it again
    /// when the run ends, so the models a first request brought are in the menu without a manual refresh.
    func modelsAfterRunEnded(_ sessionID: String) {
        guard snapshot.sessions.first(where: { $0.id == sessionID })?.provider == "hermes", !isRemoteSession(sessionID) else { return }
        invalidateLocalModels(provider: "hermes")
    }

    /// The muted line under the model menu's refresh button: the built-in list after a read that failed, or the
    /// last list the CLI gave when a newer read failed (help: why). Nothing for a list the CLI just gave.
    func modelListNotice(for session: RunSession) -> ModelListNotice? {
        providerRuntime(session.provider, workspaceId: session.workspaceId).modelCatalog.notice()
    }

    func acceptLocalModels(_ key: LocalModelContext, previous: ProviderRuntime?, refreshed: ProviderRuntime) {
        guard !ending, let workspace = snapshot.workspaces.first(where: { $0.id == key.workspaceID }), workspace.remote == nil, workspace.path == key.path else { return }
        modelRefreshRevision &+= 1
        // A late response never changes a running request. Reconcile the current
        // selection, so a user choice made during the probe is not overwritten.
        for session in snapshot.sessions where session.workspaceId == key.workspaceID && session.provider == key.provider && session.kind != "shell" && !FilePaneKind.isReadOnlyPane(session.kind) {
            if session.status == "running" {
                if modelPriorCatalogs[session.id] == nil { modelPriorCatalogs[session.id] = previous?.modelCatalog }
                continue
            }
            guard modelRefreshSelections[key]?[session.id] == [session.model, session.settings.effort] else { continue }
            reconcileModel(session.id, previous: previous?.modelCatalog, refreshed: refreshed.modelCatalog)
        }
    }

    func reconcileModel(_ id: String, previous: ModelCatalog?, refreshed: ModelCatalog) {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), session.status != "running" else { return }
        let resolution = ModelSelectionSupport.reconcile(model: session.model, effort: session.settings.effort, previous: previous, refreshed: refreshed, provider: session.provider)
        var model = resolution.model
        if session.model != "default", model == "default", session.resumeId != nil,
           let resolved = refreshed.models.first(where: { $0.value == "default" })?.resolvedModel, !resolved.isEmpty, resolved != "default" {
            // Persist an explicit current model: an omitted --model would let
            // the resumed CLI conversation restore its obsolete model again.
            model = resolved
        }
        guard model != session.model || resolution.effort != session.settings.effort else { return }
        updateSession(id) {
            $0.model = model
            $0.settings.effort = resolution.effort
            $0.logs.append(LogEntry(kind: "system", text: L("composer.model.catalogAdjusted", ["fromModel": session.model, "toModel": model, "fromEffort": session.settings.effort, "toEffort": resolution.effort])))
            $0.logs = TranscriptRetention.trimmed($0.logs)
        }
    }

    func prepareLocalModels(for session: RunSession) async throws {
        guard let key = localModelContext(for: session) else { return }
        repeat {
            _ = await localModels.request(key).value
            try Task.checkCancellation()
            guard !ending else { throw CancellationError() }
        } while !localModels.isCurrent(key)
        // Running sessions skipped automatic correction. Once the next request
        // is admitted, apply the cached fresh catalog before making its request.
        if let value = localModels.value(for: key) { reconcileModel(session.id, previous: modelPriorCatalogs.removeValue(forKey: session.id), refreshed: value.modelCatalog) }
    }
}
