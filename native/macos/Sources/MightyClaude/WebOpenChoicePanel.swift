import SwiftUI
import MightyCore

/// The URL choice dialog for one open_url call, shown in the agent pane that
/// asked, above its composer. Only that request waits on it: the rest of the
/// app, and the pane's own transcript, stay usable. A click answers the
/// request; with no answer the service's 30-second fallback opens the page in
/// the app and takes this card down.
struct WebOpenChoicePanel: View {
    @EnvironmentObject private var store: AppStore
    let request: WebOpenPromptRequest
    @ViewState private var remember = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 7) {
                Image(systemName: "safari").foregroundStyle(Palette.accent)
                Text(WebOpenChoiceCopy.dialogTitle).fontWeight(.semibold).lineLimit(1)
                Spacer(minLength: 0)
            }.font(.system(size: 11))
            Text(WebOpenChoiceCopy.dialogMessage).font(.system(size: 12))
                .fixedSize(horizontal: false, vertical: true).frame(maxWidth: .infinity, alignment: .leading)
            Text(request.url.absoluteString).font(.system(size: 11, design: .monospaced)).textSelection(.enabled)
                .lineLimit(3).truncationMode(.middle).frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 8).padding(.vertical, 6)
                .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 6))
                .accessibilityIdentifier("web-open-url")
            Toggle(WebOpenChoiceCopy.rememberToggle, isOn: $remember)
                .toggleStyle(.checkbox).font(.system(size: 11))
                .accessibilityIdentifier("web-open-remember")
            HStack(spacing: 8) {
                Text(WebOpenChoiceCopy.fallbackHint(seconds: request.timeoutSeconds)).font(.system(size: 10)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
                Button(WebOpenChoiceCopy.externalButton) { choose(.external) }
                    .accessibilityIdentifier("web-open-external")
                Button(WebOpenChoiceCopy.inAppButton) { choose(.inApp) }
                    .buttonStyle(.borderedProminent)
                    .accessibilityIdentifier("web-open-in-app")
            }.controlSize(.small)
        }
        .padding(12)
        .background(Palette.accent.opacity(0.055))
        .overlay(alignment: .top) { Divider() }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("web-open-request-\(request.id)")
    }

    private func choose(_ destination: WebOpenDestination) {
        store.webOpenPrompts.choose(request.id, destination: destination, remember: remember)
    }
}
