import SwiftUI
import MightyCore

struct BrowserPaneView: View {
    @EnvironmentObject private var store: AppStore
    let session: RunSession

    @ViewState private var addressText = ""
    @StateObject private var engine: CefBrowserEngine

    init(session: RunSession) {
        self.session = session
        let profileKey = session.workspaceProfileKey ?? session.workspaceId
        self._engine = StateObject(wrappedValue: CefBrowserEngine(profileKey: profileKey))
    }

    var body: some View {
        VStack(spacing: 0) {
            navigationBar
            Divider()
            contentArea
        }
    }

    private var navigationBar: some View {
        HStack(spacing: 6) {
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
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(Palette.subtle)
    }

    @ViewBuilder
    private var contentArea: some View {
        ZStack {
            BrowserContainerView(engine: engine)
                .frame(maxWidth: .infinity, maxHeight: .infinity)

            if let reason = engine.failureReason {
                VStack(spacing: 12) {
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
