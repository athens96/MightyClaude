import MightyCore
import SwiftUI

/// 작업: the commands the workspace's files define, each typed into a new
/// terminal pane to start with Enter, and the dev servers listening on this
/// Mac, each to open (`ProjectTasks`). A panel (kind C).
struct ProjectTasksSheet: View {
    @EnvironmentObject private var store: AppStore
    let workspace: Workspace
    let onClose: () -> Void

    @ViewState private var tasks: [ProjectTask]?
    @ViewState private var ports: [ListeningPort]?

    var body: some View {
        DialogScaffold(L("projectTasks.title"), subtitle: workspace.path, systemImage: "play.rectangle",
                       close: DialogClose(identifier: "project-tasks-close", action: onClose)) {
            Button { Task { await reload() } } label: { Image(systemName: "arrow.clockwise") }
                .buttonStyle(.plain).foregroundStyle(Palette.ink2)
                .help(L("git.workbench.refresh")).accessibilityLabel(L("git.workbench.refresh"))
        } content: {
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.lg) {
                section(L("projectTasks.tasks"), note: L("projectTasks.tasksNote")) {
                    if let tasks {
                        if tasks.isEmpty { empty(L("projectTasks.noTasks")) }
                        ForEach(tasks) { task in
                            Button { store.typeProjectTask(task, workspaceId: workspace.id); onClose() } label: {
                                row(icon: "terminal", title: task.name, detail: task.command, trailing: task.source)
                            }
                            .buttonStyle(.plain)
                            .accessibilityIdentifier("project-task-\(task.id)")
                        }
                    } else { DialogLoading(skeletonRows: 3) }
                }
                section(L("projectTasks.ports"), note: L("projectTasks.portsNote")) {
                    if let ports {
                        if ports.isEmpty { empty(L("projectTasks.noPorts")) }
                        ForEach(ports) { port in
                            Button { store.openListeningPort(port, workspaceId: workspace.id); onClose() } label: {
                                row(icon: "network", title: "localhost:\(port.port)", detail: port.process, trailing: "pid \(port.pid)")
                            }
                            .buttonStyle(.plain)
                        }
                    } else { DialogLoading(skeletonRows: 2) }
                }
            }
            .frame(minHeight: 360)
        }
        .dialogSheetWidth(DesignMetrics.Dialog.sheetWidthL)
        .task { await reload() }
    }

    private func section<Content: View>(_ title: String, note: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            Text(title).font(.ui(size: 12, weight: .semibold))
            Text(note).font(.ui(size: 10)).foregroundStyle(Palette.ink2)
            ScrollView(.vertical) { LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs, content: content) }
        }
        .frame(maxWidth: .infinity, alignment: .topLeading)
    }

    private func empty(_ text: String) -> some View {
        Text(text).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
    }

    private func row(icon: String, title: String, detail: String, trailing: String) -> some View {
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Image(systemName: icon).foregroundStyle(Palette.accent).frame(width: 16)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.ui(size: 12, weight: .medium)).lineLimit(1)
                Text(detail).font(.code(size: 10)).foregroundStyle(Palette.ink2).lineLimit(1).truncationMode(.middle)
            }
            Spacer(minLength: DesignMetrics.Spacing.sm)
            Text(trailing).font(.ui(size: 10)).foregroundStyle(Palette.ink2)
        }
        .padding(DesignMetrics.Spacing.sm)
        .background(Palette.raised, in: RoundedRectangle(cornerRadius: 6))
        .contentShape(Rectangle())
    }

    private func reload() async {
        let root = URL(fileURLWithPath: workspace.path, isDirectory: true)
        tasks = await Task.detached(priority: .userInitiated) { ProjectTasks.detect(root: root) }.value
        ports = await ProjectTasks.listeningPorts()
    }
}
