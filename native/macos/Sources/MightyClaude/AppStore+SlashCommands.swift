import Foundation
import MightyCore

/// Slash-command completion catalogue per provider and workspace. Scanning
/// touches the filesystem, so it runs off the main actor and is cached for
/// a short while; the composer asks again whenever the palette opens.
extension AppStore {
    struct SlashCatalogEntry { var commands: [SlashCommand]; var scannedAt: Date }

    static func slashCatalogKey(provider: String, workspacePath: String?) -> String { provider + "|" + (workspacePath ?? "") }

    func slashCommands(for session: RunSession) -> [SlashCommand] {
        guard session.kind != "shell" else { return [] }
        let workspace = snapshot.workspaces.first { $0.id == session.workspaceId }
        let path = workspace?.remote == nil ? workspace?.path : nil
        return slashCatalogs[Self.slashCatalogKey(provider: session.provider, workspacePath: path)]?.commands ?? []
    }

    /// Rescans when the cache is missing or older than 30 s.
    func refreshSlashCommands(for session: RunSession) {
        guard session.kind != "shell", !ending else { return }
        let workspace = snapshot.workspaces.first { $0.id == session.workspaceId }
        let path = workspace?.remote == nil ? workspace?.path : nil
        let key = Self.slashCatalogKey(provider: session.provider, workspacePath: path)
        if let entry = slashCatalogs[key], Date().timeIntervalSince(entry.scannedAt) < 30 { return }
        guard !slashScansInFlight.contains(key) else { return }
        slashScansInFlight.insert(key)
        let provider = session.provider
        Task.detached(priority: .userInitiated) { [weak self] in
            let commands = SlashCommandCatalog.commands(provider: provider, workspacePath: path)
            await MainActor.run {
                guard let self else { return }
                self.slashCatalogs[key] = SlashCatalogEntry(commands: commands, scannedAt: Date())
                self.slashScansInFlight.remove(key)
            }
        }
    }
}
