import AppKit
import MightyCore
import SwiftUI

/// The three ways to add a workspace, as buttons or menu items: open an existing folder (⌘O),
/// make a new folder, or create one from a Git address. `idPrefix` tells the sets apart where
/// two show at once (the sidebar's empty list under the welcome screen).
struct WorkspaceAddChoices: View {
    let store: AppStore
    var idPrefix = "workspace-add"

    var body: some View {
        Button { store.openWorkspace() } label: { Label(L("workspace.add.existing"), systemImage: "folder") }
            .accessibilityIdentifier("\(idPrefix)-existing")
        Button { store.showWorkspaceCreate(.newFolder) } label: { Label(L("workspace.add.newFolder"), systemImage: "plus.rectangle.on.folder") }
            .accessibilityIdentifier("\(idPrefix)-new-folder")
        Button { store.showWorkspaceCreate(.fromGit) } label: { Label(L("workspace.add.fromGit"), systemImage: "arrow.down.circle") }
            .accessibilityIdentifier("\(idPrefix)-from-git")
    }
}

/// What the create sheets share: the first reason the folder cannot be made, checked in the
/// order the design gives (name, location, an existing file or folder at the target).
private enum CreateCheck {
    static func isFolder(_ path: String) -> Bool {
        var isFolder: ObjCBool = false
        return !path.isEmpty && FileManager.default.fileExists(atPath: path, isDirectory: &isFolder) && isFolder.boolValue
    }

    static func problem(name: String, location: String) -> String? {
        if let problem = WorkspaceCreate.checkName(name) { return WorkspaceCreate.message(for: problem) }
        if !isFolder(location) { return L("workspace.location.error.missing") }
        if FileManager.default.fileExists(atPath: WorkspaceCreate.target(location: location, name: name)) { return L("workspace.name.error.exists") }
        return nil
    }

    /// An untouched empty name keeps the button off without a red line under it.
    static func shownProblem(name: String, location: String) -> String? {
        name.isEmpty ? nil : problem(name: name, location: location)
    }
}

/// The location row: the path, cut in the middle when long, and the folder picker.
private struct CreateLocationRow: View {
    let store: AppStore
    @Binding var location: String
    let disabled: Bool

    var body: some View {
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Text(location).font(.ui(size: 12)).lineLimit(1).truncationMode(.middle)
                .frame(maxWidth: .infinity, alignment: .leading)
                .help(location)
                .accessibilityIdentifier("workspace-create-location")
            Button {
                if let picked = store.chooseCreateLocation(current: location) { location = picked }
            } label: { Image(systemName: "folder") }
            .disabled(disabled)
            .help(L("workspace.create.chooseLocation"))
            .accessibilityLabel(L("workspace.create.chooseLocation"))
            .accessibilityIdentifier("workspace-create-choose-location")
        }
    }
}

/// The sheets' title and the line under it, as RenameSheet draws its title.
private struct CreateSheetHeading: View {
    let title: String
    let subtitle: String

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            Text(title).font(.ui(size: 13, weight: .bold))
            Text(subtitle).font(.ui(size: 11)).foregroundStyle(.secondary)
        }
    }
}

/// "새 폴더 만들기": a name, a location and an optional `git init`; the folder is then added
/// as ⌘O adds one.
struct NewFolderSheet: View {
    @EnvironmentObject private var store: AppStore
    @ViewState private var name = ""
    @ViewState private var location = ""
    @ViewState private var gitInit = false
    @ViewState private var busy = false
    @ViewState private var createError: String?
    @FocusState private var nameFocused: Bool

    private var target: String { WorkspaceCreate.target(location: location, name: name) }
    private var canCreate: Bool { !busy && CreateCheck.problem(name: name, location: location) == nil }

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.lg) {
            CreateSheetHeading(title: L("workspace.newFolder.title"), subtitle: L("workspace.newFolder.subtitle"))
            Grid(alignment: .leading, horizontalSpacing: DesignMetrics.Spacing.md, verticalSpacing: DesignMetrics.Spacing.md) {
                GridRow {
                    Text(L("workspace.create.name")).font(.ui(size: 12)).gridColumnAlignment(.trailing)
                    TextField(L("workspace.create.name"), text: $name)
                        .textFieldStyle(.roundedBorder)
                        .labelsHidden()
                        .focused($nameFocused)
                        .onSubmit { create() }
                        .accessibilityIdentifier("workspace-new-folder-name")
                }
                GridRow {
                    Text(L("workspace.create.location")).font(.ui(size: 12))
                    CreateLocationRow(store: store, location: $location, disabled: busy)
                }
                GridRow {
                    Color.clear.frame(width: 0, height: 0)
                    Toggle(L("workspace.newFolder.gitInit"), isOn: $gitInit)
                        .accessibilityIdentifier("workspace-new-folder-git-init")
                }
            }
            .disabled(busy)
            if !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                Text(L("workspace.create.path", ["path": target])).font(.ui(size: 10)).foregroundStyle(.secondary)
                    .lineLimit(2).truncationMode(.middle).textSelection(.enabled)
                    .accessibilityIdentifier("workspace-create-path")
            }
            // While the folder is being made it exists already: no "already exists" line then.
            if let problem = createError ?? (busy ? nil : CreateCheck.shownProblem(name: name, location: location)) {
                Text(problem).font(.ui(size: 10)).foregroundStyle(Palette.errText)
                    .accessibilityIdentifier("workspace-create-problem")
            }
            HStack {
                if busy { ProgressView().controlSize(.small) }
                Spacer()
                Button(L("workspace.create.cancel")) { store.workspaceCreate = nil }
                    .keyboardShortcut(.cancelAction)
                    .disabled(busy)
                Button(L("workspace.newFolder.create")) { create() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!canCreate)
                    .accessibilityIdentifier("workspace-new-folder-create")
            }
        }
        .padding(DesignMetrics.Inset.sheet).frame(width: 460)
        .interactiveDismissDisabled(busy)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("workspace-new-folder-sheet")
        .onAppear {
            if location.isEmpty { location = store.createLocationDefault }
            nameFocused = true
        }
        .onChange(of: name) { _, _ in createError = nil }
        .onChange(of: location) { _, _ in createError = nil }
    }

    private func create() {
        guard canCreate, store.workspaceCreate == .newFolder else { return }
        let target = self.target, location = self.location, gitInit = self.gitInit
        busy = true
        createError = nil
        Task { @MainActor in
            let result = await GitTool.makeFolder(at: target, gitInit: gitInit)
            var gitInitFailure: String?
            switch result {
            case .failed(let message):
                createError = message
                busy = false
                return
            case .madeButGitInitFailed(let message): gitInitFailure = message
            case .made: break
            }
            store.rememberCreateLocation(location)
            do {
                try await store.addLocalWorkspace(path: target)
            } catch {
                // The folder is there but could not be added: say so where ⌘O's failures show.
                store.workspaceCreate = nil
                store.error = error.localizedDescription
                return
            }
            if let gitInitFailure { store.error = L("workspace.newFolder.gitInitFailed", ["message": gitInitFailure]) }
        }
    }
}

/// "Git으로 생성", after Fork's Clone window without the account field: the address is checked
/// with this Mac's own credentials while typing stops, then cloned with progress and cancel.
struct GitCreateSheet: View {
    enum Probe: Equatable { case idle, checking, reachable, failed(WorkspaceCreate.GitErrorKind, String) }

    @EnvironmentObject private var store: AppStore
    @ViewState private var url = ""
    @ViewState private var name = ""
    @ViewState private var nameEdited = false
    @ViewState private var location = ""
    @ViewState private var probe = Probe.idle
    @ViewState private var probeToken = 0
    @ViewState private var probeNow = false
    @ViewState private var job: GitCloneJob?
    @ViewState private var cloneProgress = WorkspaceCreate.CloneProgress(phase: .starting, percent: 0, fraction: 0)
    @ViewState private var failure: (kind: WorkspaceCreate.GitErrorKind, message: String)?
    @ViewState private var openSetupAfterClose = false
    @FocusState private var urlFocused: Bool

    private var running: Bool { job != nil }
    private var target: String { WorkspaceCreate.target(location: location, name: name) }
    private var canCreate: Bool { probe == .reachable && !running && CreateCheck.problem(name: name, location: location) == nil }
    private var nameBinding: Binding<String> {
        Binding(get: { name }, set: { value in
            guard value != name else { return }
            name = value
            nameEdited = true
        })
    }

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.lg) {
            CreateSheetHeading(title: L("workspace.git.title"), subtitle: L("workspace.git.subtitle"))
            Grid(alignment: .leading, horizontalSpacing: DesignMetrics.Spacing.md, verticalSpacing: DesignMetrics.Spacing.md) {
                GridRow {
                    Text(L("workspace.git.url")).font(.ui(size: 12)).gridColumnAlignment(.trailing)
                    TextField(L("workspace.git.urlPlaceholder"), text: $url)
                        .textFieldStyle(.roundedBorder)
                        .labelsHidden()
                        .focused($urlFocused)
                        .accessibilityIdentifier("workspace-git-url")
                }
                GridRow {
                    Color.clear.frame(width: 0, height: 0)
                    HStack(spacing: DesignMetrics.Spacing.md) {
                        probeRow
                        Spacer()
                        Button(L("workspace.git.test")) { probeNow = true; probeToken += 1 }
                            .buttonStyle(.link)
                            .disabled(url.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                            .accessibilityIdentifier("workspace-git-test")
                    }
                }
                GridRow {
                    Text(L("workspace.create.location")).font(.ui(size: 12))
                    CreateLocationRow(store: store, location: $location, disabled: running)
                }
                GridRow {
                    Text(L("workspace.create.name")).font(.ui(size: 12))
                    TextField(L("workspace.create.name"), text: nameBinding)
                        .textFieldStyle(.roundedBorder)
                        .labelsHidden()
                        .accessibilityIdentifier("workspace-git-name")
                }
            }
            .disabled(running)
            if !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                Text(L("workspace.create.path", ["path": target])).font(.ui(size: 10)).foregroundStyle(.secondary)
                    .lineLimit(2).truncationMode(.middle).textSelection(.enabled)
                    .accessibilityIdentifier("workspace-create-path")
            }
            // git makes the target at once: no "already exists" line while the clone runs.
            if !running, let problem = CreateCheck.shownProblem(name: name, location: location) {
                Text(problem).font(.ui(size: 10)).foregroundStyle(Palette.errText)
                    .accessibilityIdentifier("workspace-create-problem")
            }
            if let failure { errorRow(kind: failure.kind, message: failure.message, identifier: "workspace-git-clone-error") }
            if running {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                    ProgressView(value: cloneProgress.fraction)
                    Text(WorkspaceCreate.message(for: cloneProgress)).font(.ui(size: 11)).foregroundStyle(.secondary)
                }
                .accessibilityElement(children: .combine)
                .accessibilityIdentifier("workspace-git-progress")
            }
            HStack {
                Spacer()
                Button(L("workspace.create.cancel")) { cancel() }
                    .keyboardShortcut(.cancelAction)
                    .accessibilityIdentifier("workspace-git-cancel")
                Button(L("workspace.git.create")) { create() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!canCreate)
                    .accessibilityIdentifier("workspace-git-create")
            }
        }
        .padding(DesignMetrics.Inset.sheet).frame(width: 460)
        .interactiveDismissDisabled(running)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("workspace-git-sheet")
        .onAppear {
            if location.isEmpty { location = store.createLocationDefault }
            urlFocused = true
        }
        .onChange(of: url) { _, value in
            // Off at once: Enter must not clone an address the restarted check has not seen.
            probe = .idle
            failure = nil
            if !nameEdited { name = WorkspaceCreate.repoName(value) ?? "" }
        }
        .onChange(of: name) { _, _ in failure = nil }
        .onChange(of: location) { _, _ in failure = nil }
        // A new address drops the check on the old one; it runs once typing stops for 0.6 s,
        // or at once from "연결 확인".
        .task(id: "\(probeToken)|\(url)") { await runProbe() }
        .onDisappear {
            job?.cancel()
            // The setup screen is a sheet too: it opens once this one is gone.
            if openSetupAfterClose { Task { @MainActor in store.openSetup(focus: "git") } }
        }
    }

    @ViewBuilder private var probeRow: some View {
        switch probe {
        case .idle: EmptyView()
        case .checking:
            HStack(spacing: DesignMetrics.Spacing.sm) {
                ProgressView().controlSize(.small)
                Text(L("workspace.git.checking")).font(.ui(size: 11)).foregroundStyle(.secondary)
            }
            .accessibilityElement(children: .combine)
            .accessibilityIdentifier("workspace-git-probe")
        case .reachable:
            Text("✓ " + L("workspace.git.reachable")).font(.ui(size: 11)).foregroundStyle(Palette.doneText)
                .accessibilityIdentifier("workspace-git-probe")
        case .failed(let kind, let message):
            errorRow(kind: kind, message: message, identifier: "workspace-git-probe")
        }
    }

    private func errorRow(kind: WorkspaceCreate.GitErrorKind, message: String, identifier: String) -> some View {
        HStack(spacing: DesignMetrics.Spacing.md) {
            Text("✕ " + WorkspaceCreate.message(for: kind, detail: message)).font(.ui(size: 11)).foregroundStyle(Palette.errText)
                .lineLimit(3).textSelection(.enabled)
                .accessibilityIdentifier(identifier)
            if kind == .gitMissing {
                Button(L("workspace.git.openSetup")) {
                    openSetupAfterClose = true
                    store.workspaceCreate = nil
                }
                .controlSize(.small)
                .accessibilityIdentifier("workspace-git-open-setup")
            }
        }
    }

    private func runProbe() async {
        let address = url.trimmingCharacters(in: .whitespacesAndNewlines)
        let immediate = probeNow
        probeNow = false
        probe = .idle
        guard !address.isEmpty, !running else { return }
        if !immediate {
            try? await Task.sleep(for: .milliseconds(600))
            guard !Task.isCancelled else { return }
        }
        probe = .checking
        let result = await GitTool.probe(address)
        guard !Task.isCancelled else { return }
        switch result {
        case .reachable: probe = .reachable
        case .failed(let kind, let message): probe = .failed(kind, message)
        }
    }

    private func cancel() {
        if let job { job.cancel() } else { store.workspaceCreate = nil }
    }

    private func create() {
        guard canCreate, store.workspaceCreate == .fromGit else { return }
        let target = self.target, location = self.location
        failure = nil
        cloneProgress = WorkspaceCreate.CloneProgress(phase: .starting, percent: 0, fraction: 0)
        let started = GitCloneJob(url: url, target: target) { progress in
            Task { @MainActor in cloneProgress = progress }
        }
        job = started
        Task { @MainActor in
            let result = await started.run()
            job = nil
            switch result {
            case .cancelled: return
            case .failed(let kind, let message): failure = (kind, message)
            case .done:
                store.rememberCreateLocation(location)
                do { try await store.addLocalWorkspace(path: target) } catch {
                    store.workspaceCreate = nil
                    store.error = error.localizedDescription
                }
            }
        }
    }
}
