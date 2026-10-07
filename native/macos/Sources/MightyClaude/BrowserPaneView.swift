import SwiftUI
import MightyCore

struct BrowserPaneView: View {
    @EnvironmentObject private var store: AppStore
    let session: RunSession

    @ViewState private var addressText = ""
    @StateObject private var engine: CefBrowserEngine
    /// An agent's browser pane shows the address it navigated to.
    private let followsNavigation: Bool

    init(session: RunSession) {
        self.session = session
        let profileKey = session.workspaceProfileKey ?? session.workspaceId
        self._engine = StateObject(wrappedValue: CefBrowserEngine(profileKey: profileKey))
        followsNavigation = false
    }

    /// A pane drawn on an engine the store keeps, so the page survives the
    /// view going away when the pane is hidden or closed.
    init(session: RunSession, engine: CefBrowserEngine) {
        self.session = session
        self._engine = StateObject(wrappedValue: engine)
        self._addressText = ViewState(initialValue: engine.navState.url?.absoluteString ?? "")
        followsNavigation = true
    }

    var body: some View {
        VStack(spacing: 0) {
            navigationBar
            Divider()
            contentArea
        }
        .onChange(of: engine.navState.url) { _, url in
            if followsNavigation, let url { addressText = url.absoluteString }
        }
    }

    private var navigationBar: some View {
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Button {
                engine.goBack()
            } label: {
                Image(systemName: "chevron.left").font(.system(size: 12))
            }
            .buttonStyle(.plain)
            .disabled(!engine.canGoBack)
            .help(L("browser.back"))
            .accessibilityLabel(L("browser.back"))

            Button {
                engine.goForward()
            } label: {
                Image(systemName: "chevron.right").font(.system(size: 12))
            }
            .buttonStyle(.plain)
            .disabled(!engine.canGoForward)
            .help(L("browser.forward"))
            .accessibilityLabel(L("browser.forward"))

            Button {
                engine.reload()
            } label: {
                Image(systemName: "arrow.clockwise").font(.system(size: 12))
            }
            .buttonStyle(.plain)
            .disabled(!engine.isAvailable)
            .help(L("browser.reload"))
            .accessibilityLabel(L("browser.reload"))

            TextField(L("browser.address.placeholder"), text: $addressText)
                .textFieldStyle(.roundedBorder)
                .font(.system(size: 12))
                .accessibilityLabel(L("browser.address.placeholder"))
                .disabled(!engine.isAvailable)
                .onSubmit {
                    guard let url = BrowserAddress.resolve(addressText) else { return }
                    engine.loadURL(url)
                }

            Spacer(minLength: 0)
        }
        .padding(.horizontal, DesignMetrics.Spacing.md)
        .padding(.vertical, DesignMetrics.Spacing.sm)
        .background(Palette.subtle)
    }

    @ViewBuilder
    private var contentArea: some View {
        ZStack {
            BrowserContainerView(engine: engine)
                .frame(maxWidth: .infinity, maxHeight: .infinity)

            if let reason = engine.failureReason {
                VStack(spacing: DesignMetrics.Spacing.md) {
                    Image(systemName: "globe.slash").font(.system(size: 36)).foregroundStyle(.secondary)
                    Text(reason)
                        .font(.system(size: 13))
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: 380)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(Palette.canvas)
                .accessibilityElement(children: .contain)
                .accessibilityIdentifier("browser-engine-error")
            }
        }
    }
}

/// An agent pane's browser pane: the browser view on the engine the store
/// keeps for that agent pane, which the agent's opens navigate.
struct AgentBrowserPaneView: View {
    @EnvironmentObject private var store: AppStore
    let session: RunSession

    var body: some View {
        if let owner = session.ownerSessionId, let engine = store.agentBrowsers[owner] {
            BrowserPaneView(session: session, engine: engine).id(ObjectIdentifier(engine))
        } else { Color.clear }
    }
}
