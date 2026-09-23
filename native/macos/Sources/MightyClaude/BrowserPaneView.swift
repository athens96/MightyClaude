import SwiftUI
import MightyCore

struct BrowserPaneView: View {
    @EnvironmentObject private var store: AppStore
    let session: RunSession

    @ViewState private var addressText = ""
    @ViewState private var engineStatus: BrowserEngineStatus = BrowserEngineLocator.locate()

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
            } label: {
                Image(systemName: "chevron.left").font(.system(size: 12))
            }
            .buttonStyle(.plain)
            .disabled(true)
            .help(L("browser.back"))
            .accessibilityLabel(L("browser.back"))

            Button {
            } label: {
                Image(systemName: "chevron.right").font(.system(size: 12))
            }
            .buttonStyle(.plain)
            .disabled(true)
            .help(L("browser.forward"))
            .accessibilityLabel(L("browser.forward"))

            Button {
            } label: {
                Image(systemName: "arrow.clockwise").font(.system(size: 12))
            }
            .buttonStyle(.plain)
            .help(L("browser.reload"))
            .accessibilityLabel(L("browser.reload"))

            TextField(L("browser.address.placeholder"), text: $addressText)
                .textFieldStyle(.roundedBorder)
                .font(.system(size: 12))
                .accessibilityLabel(L("browser.address.placeholder"))

            Spacer(minLength: 0)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(Palette.subtle)
    }

    @ViewBuilder
    private var contentArea: some View {
        switch engineStatus {
        case .available:
            Color.clear
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        case .missing(let reason):
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
        }
    }
}
