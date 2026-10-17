import AppKit
import MightyCore
import SwiftUI

/// 필요한 도구 as a sheet: at first launch, from the welcome screen, and from a
/// pane blocked for a missing CLI (scrolled to that CLI).
struct SetupSheetView: View {
    @EnvironmentObject private var store: AppStore
    let request: SetupSheetRequest
    @ViewState private var neverShow = false

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "shippingbox").font(.ui(size: 15)).foregroundStyle(Palette.accent)
                Text(L("setup.title")).font(.ui(size: 15, weight: .semibold))
                Spacer()
            }
            Text(L("setup.subtitle")).font(.ui(size: 12)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            ScrollViewReader { proxy in
                ScrollView {
                    SetupToolsList(focus: request.focus).padding(.trailing, DesignMetrics.Spacing.md)
                }
                .frame(minHeight: 320, maxHeight: 520)
                .task {
                    guard let focus = request.focus else { return }
                    try? await Task.sleep(for: .milliseconds(150))
                    withAnimation { proxy.scrollTo(focus, anchor: .center) }
                }
            }
            SetupToolsFooter(onRecheck: { Task { await store.refreshSetup(forceEnvironment: true) } })
            Divider()
            HStack(spacing: DesignMetrics.Spacing.md) {
                Toggle(L("setup.button.neverShow"), isOn: $neverShow).toggleStyle(.checkbox).font(.ui(size: 11))
                    .accessibilityIdentifier("setup-never-show")
                Spacer()
                Button(L("setup.button.later")) { store.dismissSetup(neverShowAgain: neverShow) }
                    .keyboardShortcut(.cancelAction).accessibilityIdentifier("setup-later")
            }
        }
        .padding(DesignMetrics.Inset.sheet)
        .frame(width: 600)
        .setupPolling()
        .sheet(item: $store.setupConfirmation) { SetupConfirmationView(confirmation: $0).environmentObject(store) }
    }
}

/// Settings → 도구 → 구성 요소 hosts the same list in place of the old copy-only rows.
struct SetupToolsSettingsContent: View {
    @EnvironmentObject private var store: AppStore

    var body: some View {
        Text(L("setup.subtitle")).font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        SetupToolsList(focus: nil)
        SetupToolsFooter(onRecheck: {
            Task {
                await store.refreshSetup(forceEnvironment: true)
                await store.refreshComponents()
            }
        })
        .setupPolling()
        .sheet(item: $store.setupConfirmation) { SetupConfirmationView(confirmation: $0).environmentObject(store) }
    }
}

private extension View {
    /// Probes when the list appears, then every few seconds while something is
    /// still not installed (an install that finishes outside the app, such as a
    /// system dialog or a download, has nothing else to tell the app).
    func setupPolling() -> some View {
        modifier(SetupPolling())
    }
}

private struct SetupPolling: ViewModifier {
    @EnvironmentObject private var store: AppStore
    func body(content: Content) -> some View {
        content.task {
            await store.refreshSetup(forceEnvironment: true)
            store.preselectSetup()
            if store.setupFixedResults == nil { store.refreshCLIAccounts() }
            while !Task.isCancelled {
                try? await Task.sleep(for: AppStore.setupPollInterval)
                guard !Task.isCancelled, !store.setupProbing,
                      store.setupItems.contains(where: { store.setupResults[$0.id]?.isInstalled != true }) else { continue }
                await store.refreshSetup()
            }
        }
    }
}

/// The groups and their rows.
struct SetupToolsList: View {
    @EnvironmentObject private var store: AppStore
    let focus: String?

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.lg) {
            ForEach(SetupGroup.allCases, id: \.self) { group in
                let items = store.setupItems.filter { $0.group == group }
                if !items.isEmpty {
                    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                        Text(L(group.titleKey)).font(.ui(size: 11, weight: .semibold)).foregroundStyle(.secondary)
                        ForEach(items) { item in SetupItemRow(item: item, focused: item.id == focus).id(item.id) }
                    }
                }
            }
        }
        .accessibilityElement(children: .contain).accessibilityIdentifier("setup-list")
    }
}

private struct SetupItemRow: View {
    @EnvironmentObject private var store: AppStore
    let item: SetupItem
    let focused: Bool

    private var state: SetupRowState { store.setupRowState(item) }
    private var recipe: SetupRecipe? { item.platforms[.macos]?.install }
    private var added: Bool { store.setupAddedIds.contains(item.id) }
    private var checked: Bool { store.setupSelection.contains(item.id) || added }
    private var selectable: Bool {
        recipe?.runsInTerminal == true && [.missing, .failed].contains(state) && !added && store.setupRun?.finished != false
    }

    var body: some View {
        HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
            Toggle("", isOn: Binding(get: { checked }, set: { _ in store.toggleSetupSelection(item.id) }))
                .toggleStyle(.checkbox).labelsHidden()
                .disabled(!selectable)
                .opacity(recipe?.runsInTerminal == true && state != .installed ? 1 : 0)
                .accessibilityLabel(item.title)
                .accessibilityIdentifier("setup-check-\(item.id)")
                .background(AccessibilityStateProbe(identifier: "setup-check-\(item.id)", enabled: checked))
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    icon
                    Text(item.title).font(.ui(size: 13, weight: .medium))
                    if item.provider.map(ProviderOptions.isBeta) == true { BetaBadge() }
                    if item.required {
                        Text(L("setup.badge.required")).font(.ui(size: 9, weight: .medium)).foregroundStyle(Palette.accent)
                            .padding(.horizontal, DesignMetrics.Spacing.xs).padding(.vertical, 1)
                            .background(Palette.accentSoft).clipShape(RoundedRectangle(cornerRadius: 3))
                    }
                    if let version = store.setupResults[item.id]?.version {
                        Text(version).font(.ui(size: 10, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer(minLength: DesignMetrics.Spacing.sm)
                    stateLabel
                }
                Text(item.detail).font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                if case .outdated(let version, let minimum)? = store.setupResults[item.id] {
                    Text(L("setup.outdatedDetail", ["version": version ?? "?", "minimum": minimum])).font(.ui(size: 11)).foregroundStyle(Palette.waitText)
                        .fixedSize(horizontal: false, vertical: true)
                }
                if added {
                    Text(L("setup.addedForSelection")).font(.ui(size: 10)).foregroundStyle(Palette.accent)
                }
                actions
            }
        }
        .padding(.vertical, DesignMetrics.Spacing.xxs)
        .padding(.horizontal, focused ? DesignMetrics.Spacing.xs : 0)
        .background(focused ? Palette.accentSoft.opacity(0.5) : Color.clear, in: RoundedRectangle(cornerRadius: 6))
        .accessibilityElement(children: .contain).accessibilityIdentifier("setup-row-\(item.id)")
        .background(AccessibilityStateProbe(identifier: "setup-row-\(item.id)-\(state.rawValue)", enabled: true))
    }

    @ViewBuilder private var icon: some View {
        if let provider = item.provider { ProviderIcon(provider: provider, size: 13) }
        else {
            let symbol = switch item.group {
            case .prerequisite: "wrench.and.screwdriver"
            case .style: "paintpalette"
            case .network: "network"
            case .dev: "chevron.left.forwardslash.chevron.right"
            case .agent: "terminal"
            }
            Image(systemName: symbol).font(.ui(size: 12)).frame(width: 13)
        }
    }

    @ViewBuilder private var actions: some View {
        let provider = item.provider
        let signIn = state == .installed && provider.map { store.cliAccounts[$0]?.loggedIn == false } == true
        let update = state == .outdated && provider != nil
        let page = state != .installed && recipe.map { !$0.runsInTerminal } == true
        if signIn || update || page {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                if signIn, let provider {
                    Button(L("setup.button.signIn")) { store.signInFromSetup(provider) }
                        .controlSize(.small).buttonStyle(.borderedProminent)
                        .accessibilityIdentifier("setup-signin-\(item.id)")
                }
                if update, let provider {
                    Button(store.isUpdatingCLIs ? L("settings.components.inProgressLabel") : L("setup.button.update")) { store.updateFromSetup(provider) }
                        .controlSize(.small).buttonStyle(.borderedProminent).disabled(store.isUpdatingCLIs)
                        .accessibilityIdentifier("setup-update-\(item.id)")
                        .background(AccessibilityStateProbe(identifier: "setup-update-\(item.id)", enabled: !store.isUpdatingCLIs))
                }
                if page {
                    Button(L("setup.button.openPage")) { store.openSetupVendorPage(item) }
                        .controlSize(.small).buttonStyle(.bordered)
                        .accessibilityIdentifier("setup-page-\(item.id)")
                        .background(AccessibilityStateProbe(identifier: "setup-page-\(item.id)", enabled: true))
                }
            }
            .padding(.top, DesignMetrics.Spacing.xxs)
        }
    }

    private var stateLabel: some View {
        let (key, color): (String, Color) = switch state {
        case .checking: ("setup.status.checking", .secondary)
        case .installed: ("setup.status.installed", Palette.doneText)
        case .missing: ("setup.status.missing", Palette.waitText)
        case .outdated: ("setup.status.outdated", Palette.waitText)
        case .installing: ("setup.status.installing", Palette.accent)
        case .failed: ("setup.status.failed", Palette.errText)
        }
        return HStack(spacing: DesignMetrics.Spacing.xs) {
            if state == .installing { ProgressView().controlSize(.mini) }
            Text(L(key)).font(.ui(size: 10, weight: .medium)).foregroundStyle(color)
        }
    }
}

/// Re-check, install the picked items, and what is going on.
private struct SetupToolsFooter: View {
    @EnvironmentObject private var store: AppStore
    let onRecheck: () -> Void

    private var running: Bool { store.setupRun?.finished == false }

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            if let message = store.setupMessage {
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Palette.waitText).padding(.top, 1)
                    Text(message).font(.ui(size: 11)).fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                }
                .accessibilityIdentifier("setup-message")
            }
            HStack(spacing: DesignMetrics.Spacing.sm) {
                if running {
                    ProgressView().controlSize(.mini)
                    Text(L("setup.running")).font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                Spacer()
                Button(store.setupProbing ? L("setup.button.checking") : L("setup.button.recheck"), action: onRecheck)
                    .disabled(store.setupProbing)
                    .accessibilityIdentifier("setup-recheck")
                Button(L("setup.button.install")) { store.requestSetupInstall() }
                    .buttonStyle(.borderedProminent)
                    .disabled(store.setupPlanIds.isEmpty || running)
                    .accessibilityIdentifier("setup-install")
                    .background(AccessibilityStateProbe(identifier: "setup-install", enabled: !store.setupPlanIds.isEmpty && !running))
            }
        }
    }
}

/// The one confirmation before anything runs: every step's command exactly as
/// the script has it, in the order it runs.
struct SetupConfirmationView: View {
    @EnvironmentObject private var store: AppStore
    let confirmation: SetupConfirmation

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
            Text(L("setup.confirm.title")).font(.ui(size: 13, weight: .bold))
            Text(L("setup.confirm.body")).font(.ui(size: 12)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            ScrollView {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
                    ForEach(Array(confirmation.steps.enumerated()), id: \.element.id) { index, step in
                        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                            Text("\(index + 1). \(step.title)").font(.ui(size: 12, weight: .medium))
                            Text(step.body).font(.ui(size: 11, design: .monospaced)).foregroundStyle(.primary)
                                .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                        }
                        .accessibilityIdentifier("setup-confirm-step-\(step.itemId)")
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxHeight: 340)
            HStack {
                Spacer()
                Button(L("setup.confirm.cancel")) { store.setupConfirmation = nil }
                    .keyboardShortcut(.cancelAction).accessibilityIdentifier("setup-confirm-cancel")
                Button(L("setup.confirm.run")) { Task { await store.startSetupInstall() } }
                    .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                    .accessibilityIdentifier("setup-confirm-run")
            }
        }
        .padding(DesignMetrics.Inset.sheet)
        .frame(minWidth: 520, maxWidth: 680)
    }
}
