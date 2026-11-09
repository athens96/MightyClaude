import MightyCore
import SwiftUI

/// 에이전트 맥락: the workspace's instruction file per agent (CLAUDE.md,
/// AGENTS.md, GEMINI.md) to edit and make the same, and the MCP servers and
/// Claude hooks the CLIs will use here (`AgentContext`). A panel (kind C).
struct AgentContextSheet: View {
    let workspace: Workspace
    let onClose: () -> Void

    @ViewState private var files: [AgentContext.InstructionFile] = []
    @ViewState private var selected = "CLAUDE.md"
    @ViewState private var text = ""
    @ViewState private var servers: [AgentContext.MCPServer] = []
    @ViewState private var hooks: [AgentContext.Hook] = []
    @ViewState private var failure: String?
    @ViewState private var copyTarget: String?

    private var root: URL { URL(fileURLWithPath: workspace.path, isDirectory: true) }
    private var current: AgentContext.InstructionFile? { files.first { $0.name == selected } }
    private var edited: Bool { text != (current?.text ?? "") }

    var body: some View {
        DialogScaffold(L("agentContext.title"), subtitle: workspace.path, systemImage: "brain",
                       close: DialogClose(identifier: "agent-context-close", action: onClose), tools: { EmptyView() }) {
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.lg) {
                instructions.frame(maxWidth: .infinity)
                Divider()
                ScrollView(.vertical) {
                    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.lg) { serverList; hookList }
                }
                .frame(width: 300)
            }
            .frame(minHeight: 440)
        }
        .dialogSheetWidth(1_000)
        .task { reload() }
        .confirmationDialog(L("agentContext.copy.confirm", ["target": copyTarget ?? "", "source": selected]), isPresented: Binding(get: { copyTarget != nil }, set: { if !$0 { copyTarget = nil } })) {
            Button(L("agentContext.copy.overwrite"), role: .destructive) { if let copyTarget { copy(to: copyTarget) } }
            Button(L("runChanges.confirm.cancel"), role: .cancel) {}
        }
    }

    private var instructions: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            Picker("", selection: $selected) {
                ForEach(files) { file in
                    Text(file.name + (file.text == nil ? " · " + L("agentContext.missing") : "")).tag(file.name)
                }
            }
            .pickerStyle(.segmented).labelsHidden()
            .onChange(of: selected) { _, _ in text = current?.text ?? ""; failure = nil }
            if AgentContext.diverged(files) {
                Label(L("agentContext.diverged"), systemImage: "exclamationmark.triangle").font(.ui(size: 11)).foregroundStyle(Palette.waitText)
            }
            TextEditor(text: $text).font(.code(size: 12)).scrollContentBackground(.hidden)
                .padding(DesignMetrics.Spacing.xs).background(Palette.raised, in: RoundedRectangle(cornerRadius: 6))
                .accessibilityIdentifier("agent-context-editor")
            if let failure { Text(failure).font(.ui(size: 11)).foregroundStyle(Palette.errText) }
            HStack {
                Menu(L("agentContext.copy.menu")) {
                    ForEach(files.filter { $0.name != selected }) { file in
                        Button(L("agentContext.copy.to", ["target": file.name])) { copyTarget = file.name }
                    }
                }
                .fixedSize().disabled(text.isEmpty || edited)
                .help(edited ? L("agentContext.copy.saveFirst") : L("agentContext.copy.help"))
                Spacer()
                Button(L("agentContext.revert")) { text = current?.text ?? "" }.disabled(!edited)
                Button(L("savedPrompts.save")) { save() }.buttonStyle(.borderedProminent).disabled(!edited)
                    .accessibilityIdentifier("agent-context-save")
            }
        }
    }

    private var serverList: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            Text(L("agentContext.servers", ["count": "\(servers.count)"])).font(.ui(size: 12, weight: .semibold))
            if servers.isEmpty { Text(L("agentContext.none")).font(.ui(size: 11)).foregroundStyle(Palette.ink2) }
            ForEach(servers) { server in
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    HStack(spacing: DesignMetrics.Spacing.xs) {
                        if let image = ProviderIconImage.image(provider: server.provider, pointSize: 11) { Image(nsImage: image) }
                        Text(server.name).font(.ui(size: 12, weight: .medium))
                        Text(server.scope == "project" ? L("agentContext.scope.project") : L("agentContext.scope.user"))
                            .font(.ui(size: 10)).foregroundStyle(Palette.ink2)
                    }
                    Text(server.target).font(.code(size: 10)).foregroundStyle(Palette.ink2).lineLimit(1).truncationMode(.middle).help(server.target)
                    Text(server.source).font(.ui(size: 9)).foregroundStyle(Palette.ink2)
                }
                .padding(DesignMetrics.Spacing.xs)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(Palette.raised, in: RoundedRectangle(cornerRadius: 5))
            }
        }
    }

    private var hookList: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            Text(L("agentContext.hooks", ["count": "\(hooks.count)"])).font(.ui(size: 12, weight: .semibold))
            if hooks.isEmpty { Text(L("agentContext.none")).font(.ui(size: 11)).foregroundStyle(Palette.ink2) }
            ForEach(hooks) { hook in
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    HStack {
                        Text(hook.event).font(.ui(size: 12, weight: .medium))
                        Text(hook.source).font(.ui(size: 9)).foregroundStyle(Palette.ink2)
                    }
                    ForEach(Array(hook.commands.enumerated()), id: \.offset) { _, command in
                        Text(command).font(.code(size: 10)).foregroundStyle(Palette.ink2).lineLimit(1).truncationMode(.middle).help(command)
                    }
                }
                .padding(DesignMetrics.Spacing.xs)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(Palette.raised, in: RoundedRectangle(cornerRadius: 5))
            }
            Text(L("agentContext.readOnly")).font(.ui(size: 10)).foregroundStyle(Palette.ink2)
        }
    }

    private func reload() {
        files = AgentContext.instructionFiles(root: root)
        if current?.text == nil, let existing = files.first(where: { $0.text != nil }) { selected = existing.name }
        text = current?.text ?? ""
        servers = AgentContext.mcpServers(root: root)
        hooks = AgentContext.claudeHooks(root: root)
    }

    private func save() {
        do { try AgentContext.write(text, name: selected, root: root); failure = nil; let keep = selected; reload(); selected = keep; text = current?.text ?? "" }
        catch { failure = error.localizedDescription }
    }

    private func copy(to target: String) {
        do { try AgentContext.write(text, name: target, root: root); failure = nil; let keep = selected; reload(); selected = keep; text = current?.text ?? "" }
        catch { failure = error.localizedDescription }
        copyTarget = nil
    }
}
