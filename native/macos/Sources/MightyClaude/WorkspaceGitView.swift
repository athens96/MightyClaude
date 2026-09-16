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

    func info(for workspace: Workspace) -> WorkspaceGitInfo? {
        workspace.remote == nil && workspaceKey == workspace.id + "|" + workspace.path ? info : nil
    }

    func observe(_ workspace: Workspace?) async {
        let generation = UUID(); self.generation = generation
        guard let workspace, workspace.remote == nil else { workspaceKey = nil; info = nil; return }
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
        HStack(spacing: 5) {
            Image(systemName: "arrow.triangle.branch")
            Text(info.label).lineLimit(1).truncationMode(.middle)
            if info.isDirty { Circle().fill(Palette.accent).frame(width: 5, height: 5).accessibilityHidden(true) }
            if let ahead = info.ahead, ahead > 0 { Text("↑\(ahead)").monospacedDigit() }
            if let behind = info.behind, behind > 0 { Text("↓\(behind)").monospacedDigit() }
        }
        .font(.system(size: 10, weight: .medium)).foregroundStyle(.secondary)
        .padding(.horizontal, 7).padding(.vertical, 3)
        .background(Palette.subtle, in: Capsule())
        .frame(maxWidth: 260)
        .help("Git · \(info.label) · \(info.isDirty ? "커밋하지 않은 변경 있음" : "작업 트리 깨끗함")\n↑↓는 로컬에 기록된 upstream 기준입니다.")
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Git \(info.label), \(info.isDirty ? "변경 있음" : "변경 없음")")
        .accessibilityIdentifier("workspace-git-info")
    }
}
