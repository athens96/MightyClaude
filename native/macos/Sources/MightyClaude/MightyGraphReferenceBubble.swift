import AppKit
import MightyCore
import SwiftUI
import WebKit

/// A file mentioned in a Mighty block, resolved inside the workspace root.
/// `url` is nil when the path could not be resolved; the bubble then explains.
struct MightyGraphReference: Equatable {
    let path: String
    let line: Int?
    let url: URL?
    var title: String { url?.lastPathComponent ?? (path as NSString).lastPathComponent }
}

/// A speech bubble docked beside the diagram. It previews Markdown, HTML,
/// images and text without leaving the graph; other files open externally.
struct MightyGraphReferenceBubble: View {
    let sessionID: String
    let reference: MightyGraphReference
    let onLeft: Bool
    let onFlip: () -> Void
    let onClose: () -> Void
    @ViewState private var content: Content = .loading
    static let defaultWidth: Double = 420
    static let minimumWidth: CGFloat = 300
    static let minimumHeight: CGFloat = 200
    static let margin: CGFloat = 12

    /// The panel size for stored preferences inside the space next to the graph.
    static func size(available: CGSize, storedWidth: Double, storedHeight: Double) -> CGSize {
        let width = min(max(minimumWidth, CGFloat(storedWidth)), max(minimumWidth, available.width * 0.85))
        let height = storedHeight > 0 ? min(max(minimumHeight, CGFloat(storedHeight)), max(minimumHeight, available.height)) : max(minimumHeight, available.height)
        return CGSize(width: width, height: height)
    }
    /// Where the bubble, including its tail, sits inside a canvas of this size.
    /// The native overlay view is given exactly this frame.
    static func frame(onLeft: Bool, canvas: CGSize, storedWidth: Double, storedHeight: Double) -> CGRect {
        let available = CGSize(width: max(0, canvas.width - margin * 2), height: max(0, canvas.height - margin * 2))
        let panel = size(available: available, storedWidth: storedWidth, storedHeight: storedHeight)
        let width = panel.width + MightyBubbleShape.tailLength
        return CGRect(x: onLeft ? margin : max(margin, canvas.width - margin - width), y: margin, width: width, height: panel.height)
    }

    private enum Content: Equatable {
        case loading, markdown(String), text(String), image(NSImage), html(URL), tooLarge, unreadable, missing
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            preview.frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        // The native host is sized to `frame(onLeft:canvas:...)`; fill it.
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(MightyBubbleShape(tailOnLeft: !onLeft).fill(Palette.panel).shadow(color: .black.opacity(0.18), radius: 12, y: 4))
        .overlay(MightyBubbleShape(tailOnLeft: !onLeft).stroke(Palette.border, lineWidth: 1))
        .clipShape(MightyBubbleShape(tailOnLeft: !onLeft))
        // Same corner handle as graph blocks; the canvas probe performs the drag.
        .overlay(alignment: onLeft ? .bottomTrailing : .bottomLeading) { resizeGlyph }
        .padding(.leading, onLeft ? 0 : MightyBubbleShape.tailLength)
        .padding(.trailing, onLeft ? MightyBubbleShape.tailLength : 0)
        .onExitCommand(perform: onClose)
        .task(id: reference) { content = await Self.load(reference) }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("참조 말풍선 · \(reference.title)")
        .accessibilityIdentifier("mighty-reference-bubble-\(sessionID)")
    }

    private var resizeGlyph: some View {
        Image(systemName: "arrow.up.left.and.arrow.down.right")
            .font(.system(size: 10, weight: .semibold)).foregroundStyle(.secondary)
            .frame(width: 22, height: 22)
            .background(Palette.panel.opacity(0.95), in: RoundedRectangle(cornerRadius: 5))
            .padding(2)
            .help("드래그하여 말풍선 크기 조절 · 두 번 클릭해 기본 크기")
            .allowsHitTesting(false)
            .accessibilityLabel("말풍선 크기 조절")
            .accessibilityIdentifier("mighty-reference-resize-\(sessionID)")
    }

    private var header: some View {
        HStack(spacing: 8) {
            Image(systemName: icon).foregroundStyle(Palette.accent)
            VStack(alignment: .leading, spacing: 1) {
                Text(reference.title + (reference.line.map { " · 줄 \($0)" } ?? "")).font(.system(size: 12, weight: .semibold)).lineLimit(1)
                Text(reference.path).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle).help(reference.path)
            }
            Spacer(minLength: 4)
            Button(action: onFlip) { Image(systemName: onLeft ? "rectangle.righthalf.inset.filled.arrow.right" : "rectangle.lefthalf.inset.filled.arrow.left") }
                .help(onLeft ? "오른쪽에 표시" : "왼쪽에 표시").accessibilityLabel(onLeft ? "말풍선을 오른쪽으로" : "말풍선을 왼쪽으로")
                .accessibilityIdentifier("mighty-reference-flip-\(sessionID)")
            if let url = reference.url {
                Button { NSWorkspace.shared.activateFileViewerSelecting([url]) } label: { Image(systemName: "folder") }
                    .help("Finder에서 보기").accessibilityLabel("Finder에서 보기")
                Button { NSWorkspace.shared.open(url) } label: { Image(systemName: "arrow.up.forward.app") }
                    .help("기본 앱으로 열기").accessibilityLabel("기본 앱으로 열기")
            }
            Button(action: onClose) { Image(systemName: "xmark") }
                .help("닫기 (Esc)").accessibilityLabel("말풍선 닫기")
                .accessibilityIdentifier("mighty-reference-close-\(sessionID)")
        }
        .buttonStyle(.plain).font(.system(size: 12))
        .padding(.horizontal, 12).frame(height: 40)
    }

    private var icon: String {
        guard let url = reference.url else { return "questionmark.folder" }
        switch ReferenceLinkSupport.kind(of: url) {
        case "markdown": return "doc.richtext"
        case "html": return "globe"
        case "image": return "photo"
        default: return "doc.text"
        }
    }

    @ViewBuilder private var preview: some View {
        switch content {
        case .loading:
            ProgressView().controlSize(.small).frame(maxWidth: .infinity, maxHeight: .infinity)
        case .markdown(let source):
            ScrollView { AgentMarkdownView(source: source).padding(14) }
        case .text(let text):
            ScrollView([.vertical, .horizontal]) {
                Text(text).font(.system(size: 11, design: .monospaced)).textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .topLeading).padding(14)
            }
        case .image(let image):
            ScrollView([.vertical, .horizontal]) { Image(nsImage: image).resizable().scaledToFit().padding(14) }
        case .html(let url):
            MightyLocalWebPreview(url: url)
        case .tooLarge:
            notice("2 MiB보다 큰 파일은 미리 보지 않습니다.", detail: "기본 앱으로 열어 확인하세요.")
        case .unreadable:
            notice("텍스트로 표시할 수 없는 파일입니다.", detail: "기본 앱으로 열어 확인하세요.")
        case .missing:
            notice("워크스페이스 안에서 찾을 수 없는 파일입니다.", detail: "경로가 프로젝트 폴더 밖이거나 파일이 없습니다.")
        }
    }

    private func notice(_ title: String, detail: String) -> some View {
        VStack(spacing: 6) {
            Image(systemName: "doc.questionmark").font(.system(size: 24, weight: .light)).foregroundStyle(.secondary)
            Text(title).font(.system(size: 12, weight: .medium))
            Text(detail).font(.system(size: 11)).foregroundStyle(.secondary).multilineTextAlignment(.center)
        }
        .padding(20).frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private static func load(_ reference: MightyGraphReference) async -> Content {
        guard let url = reference.url else { return .missing }
        let kind = ReferenceLinkSupport.kind(of: url)
        if kind == "html" { return .html(url) }
        let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        guard size <= ReferenceLinkSupport.maximumFileBytes else { return .tooLarge }
        guard let data = try? Data(contentsOf: url) else { return .unreadable }
        if kind == "image" { return NSImage(data: data).map(Content.image) ?? .unreadable }
        guard let text = String(data: data, encoding: .utf8) else { return .unreadable }
        return kind == "markdown" ? .markdown(text) : .text(text)
    }
}

/// Rounded panel with a short tail on the side facing the diagram.
struct MightyBubbleShape: Shape {
    let tailOnLeft: Bool
    static let tailLength: CGFloat = 12
    private let radius: CGFloat = 12

    func path(in rect: CGRect) -> Path {
        let body = CGRect(x: rect.minX + (tailOnLeft ? Self.tailLength : 0), y: rect.minY, width: max(0, rect.width - Self.tailLength), height: rect.height)
        var path = Path(roundedRect: body, cornerRadius: radius)
        let y = min(body.minY + 40, body.midY)
        var tail = Path()
        if tailOnLeft {
            tail.move(to: CGPoint(x: body.minX, y: y - 9)); tail.addLine(to: CGPoint(x: rect.minX, y: y)); tail.addLine(to: CGPoint(x: body.minX, y: y + 9))
        } else {
            tail.move(to: CGPoint(x: body.maxX, y: y - 9)); tail.addLine(to: CGPoint(x: rect.maxX, y: y)); tail.addLine(to: CGPoint(x: body.maxX, y: y + 9))
        }
        tail.closeSubpath()
        path.addPath(tail)
        return path
    }
}

/// Local HTML such as generated diagrams. Only file: navigation inside the
/// document's own directory is allowed; everything else is refused.
private struct MightyLocalWebPreview: NSViewRepresentable {
    let url: URL

    func makeCoordinator() -> Coordinator { Coordinator(root: url.deletingLastPathComponent()) }
    func makeNSView(context: Context) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        let view = WKWebView(frame: .zero, configuration: configuration)
        view.navigationDelegate = context.coordinator
        view.setValue(false, forKey: "drawsBackground")
        view.loadFileURL(url, allowingReadAccessTo: url.deletingLastPathComponent())
        return view
    }
    func updateNSView(_ view: WKWebView, context: Context) {
        if view.url?.standardizedFileURL != url.standardizedFileURL {
            context.coordinator.root = url.deletingLastPathComponent()
            view.loadFileURL(url, allowingReadAccessTo: url.deletingLastPathComponent())
        }
    }

    final class Coordinator: NSObject, WKNavigationDelegate {
        var root: URL
        init(root: URL) { self.root = root }
        func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
            let target = action.request.url?.standardizedFileURL
            let allowed = target?.isFileURL == true && target!.path.hasPrefix(root.standardizedFileURL.path + "/")
            decisionHandler(allowed ? .allow : .cancel)
        }
    }
}
