import AppKit
import Combine
import MightyCore
import SwiftUI

@MainActor
final class WorkspaceGitState: ObservableObject {
    @Published private(set) var info: WorkspaceGitInfo?
    private var generation = UUID()
    private var workspaceKey: String?
    private var cache: [String: WorkspaceGitInfo] = [:]

    /// The live reading, for the observed workspace only. Another workspace's last
    /// reading may be old, so the dashboard shows no badge for it rather than stale counts.
    func info(for workspace: Workspace) -> WorkspaceGitInfo? {
        workspaceKey == workspace.id + "|" + workspace.path ? info : nil
    }

    func observe(_ workspace: Workspace?) async {
        let generation = UUID(); self.generation = generation
        guard let workspace else { workspaceKey = nil; info = nil; return }
        let key = workspace.id + "|" + workspace.path
        workspaceKey = key
        info = cache[key]
        while !Task.isCancelled, self.generation == generation {
            let value = await WorkspaceGitInfo.read(path: workspace.path)
            guard !Task.isCancelled, self.generation == generation else { return }
            info = value
            if cache.count >= 64, cache[key] == nil { cache.removeAll(keepingCapacity: true) }
            cache[key] = value
            do { try await Task.sleep(for: .seconds(10)) } catch { return }
        }
    }
}

struct WorkspaceGitBadge: View {
    let info: WorkspaceGitInfo

    var body: some View {
        HStack(spacing: DesignMetrics.Spacing.xs) {
            Image(systemName: "arrow.triangle.branch")
            Text(info.label).lineLimit(1).truncationMode(.middle)
            if info.isDirty { Circle().fill(Palette.accent).frame(width: 5, height: 5).accessibilityHidden(true) }
            if let ahead = info.ahead, ahead > 0 { Text("↑\(ahead)").monospacedDigit() }
            if let behind = info.behind, behind > 0 { Text("↓\(behind)").monospacedDigit() }
        }
        .font(.system(size: 10, weight: .medium)).foregroundStyle(Palette.ink2)
        .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.xxs)
        .background(Palette.subtle, in: Capsule())
        .frame(maxWidth: 260)
        .help(L("git.help", ["label": info.label, "state": info.isDirty ? L("git.dirty") : L("git.clean")]))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(L("git.accessibility", ["label": info.label, "state": info.isDirty ? L("git.dirty") : L("git.clean")]))
        .accessibilityIdentifier("workspace-git-info")
    }
}
