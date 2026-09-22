import Foundation
import MightyCore

struct LocalModelContext: Hashable, Sendable {
    let workspaceID: String
    let path: String
    let provider: String
}

/// Serializes each context independently. Invalidations during a probe require
/// another probe after it finishes; they cannot join the old service request.
@MainActor
final class LocalModelCatalogRefresh {
    typealias Loader = @MainActor (LocalModelContext) async -> ProviderRuntime
    private final class Entry {
        var revision = 0
        var loadedRevision: Int?
        var value: ProviderRuntime?
        var loadedAt = Date.distantPast
        var task: Task<ProviderRuntime?, Never>?
    }
    private var entries: [LocalModelContext: Entry] = [:]
    private let loader: Loader
    private let changed: @MainActor (LocalModelContext, ProviderRuntime?, ProviderRuntime) -> Void
    private var stopped = false
    private var discardedProviders = Set<String>()

    init(loader: @escaping Loader, changed: @escaping @MainActor (LocalModelContext, ProviderRuntime?, ProviderRuntime) -> Void) {
        self.loader = loader
        self.changed = changed
    }

    func value(for key: LocalModelContext) -> ProviderRuntime? { entries[key]?.value }
    func isRefreshing(_ key: LocalModelContext) -> Bool { entries[key]?.task != nil }
    func hasDiscarded(_ provider: String) -> Bool { discardedProviders.contains(provider) }

    func discard(provider: String) {
        discardedProviders.insert(provider)
        for key in entries.keys.filter({ $0.provider == provider }) {
            entries.removeValue(forKey: key)?.task?.cancel()
        }
    }

    func isCurrent(_ key: LocalModelContext) -> Bool {
        guard let entry = entries[key] else { return false }
        return entry.loadedRevision == entry.revision && entry.task == nil
    }

    func invalidate(provider: String? = nil) {
        for (key, entry) in entries where provider == nil || key.provider == provider {
            entry.revision &+= 1
            entry.loadedAt = .distantPast
        }
    }

    func request(_ key: LocalModelContext, force: Bool = false, invalidate: Bool = false) -> Task<ProviderRuntime?, Never> {
        guard !stopped else { return Task { nil } }
        let entry = entries[key] ?? Entry()
        entries[key] = entry
        if invalidate { entry.revision &+= 1; entry.loadedAt = .distantPast }
        if let task = entry.task { return task }
        let lifetime: TimeInterval = force ? 2 : 60
        if let value = entry.value, value.modelCatalog.source == "cli", Date().timeIntervalSince(entry.loadedAt) < lifetime { return Task { value } }
        let task = Task { [weak self] () -> ProviderRuntime? in
            guard let self else { return nil }
            // Combine selection and focus notifications from one UI operation.
            try? await Task.sleep(for: .milliseconds(100))
            while !Task.isCancelled, !stopped {
                let revision = entry.revision
                let value = await loader(key)
                guard !Task.isCancelled, !stopped else { break }
                guard revision == entry.revision else { continue }
                let previous = entry.value
                entry.value = value
                entry.loadedAt = Date()
                entry.loadedRevision = revision
                entry.task = nil
                changed(key, previous, value)
                return value
            }
            entry.task = nil
            return nil
        }
        entry.task = task
        return task
    }

    func shutdown() {
        stopped = true
        for entry in entries.values { entry.task?.cancel() }
        entries.removeAll()
    }
}
