import AppKit
import MightyCore
import SwiftUI

/// The "그림 미리보기" block beside a step: the pictures it produced, in order,
/// as thumbnails. Two rows folded, four unfolded; the last tile shows how
/// many more there are. A tile opens the picture larger.
struct MightyGraphImagesCard: View {
    let nodeID: String
    let items: [AgentImageItem]
    let expanded: Bool
    let onToggle: () -> Void
    let onOpen: () -> Void
    @ObservedObject private var library = AgentImageLibrary.shared
    private let tint = Color.pink

    private var visible: Int { MightyGraphLayout.visibleImages(count: items.count, expanded: expanded) }

    var body: some View {
        let shown = Array(items.prefix(visible))
        let hidden = items.count - shown.count
        let foldable = MightyGraphLayout.visibleImages(count: items.count, expanded: true) > MightyGraphLayout.visibleImages(count: items.count, expanded: false)
        VStack(spacing: 0) {
            HStack(spacing: 7) {
                Image(systemName: "photo.on.rectangle.angled").foregroundStyle(tint)
                Text(L("graph.images.title")).font(.system(size: 12, weight: .semibold)).lineLimit(1)
                Spacer(minLength: 3)
                Text(L("graph.images.count", ["count": "\(items.count)"])).font(.system(size: 10)).foregroundStyle(.secondary).monospacedDigit()
                if foldable {
                    Button(action: onToggle) { Image(systemName: expanded ? "rectangle.compress.vertical" : "rectangle.expand.vertical") }
                        .buttonStyle(.plain).foregroundStyle(expanded ? Palette.accent : Color.secondary)
                        .help(expanded ? L("graph.images.collapse") : L("graph.images.expand"))
                        .accessibilityLabel(expanded ? L("graph.images.collapse") : L("graph.images.expand"))
                        .accessibilityIdentifier("mighty-images-toggle-\(nodeID)")
                }
            }.padding(.horizontal, 12).frame(height: 38)
            Divider()
            let columns = Array(repeating: GridItem(.fixed(MightyGraphLayout.imagesTile), spacing: 8), count: MightyGraphLayout.imagesColumns)
            LazyVGrid(columns: columns, alignment: .leading, spacing: 8) {
                ForEach(Array(shown.enumerated()), id: \.element.id) { index, item in
                    tile(item, more: index == shown.count - 1 && hidden > 0 ? hidden : 0)
                }
            }
            .padding(12)
            Spacer(minLength: 0)
        }
        .background(Palette.panel, in: RoundedRectangle(cornerRadius: 12))
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .overlay { RoundedRectangle(cornerRadius: 12).stroke(tint.opacity(0.45), lineWidth: 1) }
        .accessibilityElement(children: .contain).accessibilityIdentifier("mighty-node-\(nodeID)")
    }

    @ViewBuilder private func tile(_ item: AgentImageItem, more: Int) -> some View {
        if let key = AgentImageKey(item) {
            let state = library.state(key)
            let label = Self.label(item)
            Button {
                onOpen()
                // "+k" folds the rest away; unfolding shows them in the block.
                if more > 0, !expanded { onToggle() } else { AgentImageActions.open(key) }
            } label: {
                ZStack {
                    RoundedRectangle(cornerRadius: 7).fill(Palette.subtle)
                    switch state {
                    case .ready(let thumbnail):
                        Image(nsImage: thumbnail.image).resizable().interpolation(.high).scaledToFill()
                            .frame(width: MightyGraphLayout.imagesTile, height: MightyGraphLayout.imagesTile).clipped()
                    case .loading:
                        ProgressView().controlSize(.small)
                    case .missing:
                        Image(systemName: "photo.badge.exclamationmark").foregroundStyle(.secondary).help(L("images.missing"))
                    }
                    if more > 0 {
                        Color.black.opacity(0.45)
                        Text(L("graph.images.more", ["count": "\(more)"])).font(.system(size: 15, weight: .semibold)).foregroundStyle(.white)
                    }
                }
                .frame(width: MightyGraphLayout.imagesTile, height: MightyGraphLayout.imagesTile)
                .clipShape(RoundedRectangle(cornerRadius: 7))
                .overlay { RoundedRectangle(cornerRadius: 7).stroke(Palette.border, lineWidth: 0.5) }
            }
            .buttonStyle(.plain)
            .help(label)
            .accessibilityLabel(L("images.entry.one", ["source": label]))
            .accessibilityIdentifier("mighty-image-\(nodeID)-\(item.id)")
            .contextMenu {
                Button(L("images.menu.open")) { AgentImageActions.open(key) }
                if case .ready(let thumbnail) = state {
                    Button(L("images.menu.copy")) { AgentImageActions.copy(key) }
                    if thumbnail.originalURL != nil || thumbnail.fileURL != nil { Button(L("images.menu.reveal")) { AgentImageActions.reveal(thumbnail) } }
                    if thumbnail.fileURL != nil { Button(L("images.menu.preview")) { AgentImageActions.openInPreview(thumbnail) } }
                }
            }
        }
    }

    private static func label(_ item: AgentImageItem) -> String {
        switch item.content {
        case .stored(let ref): return ref.source
        case .located(let location, let alt):
            if !alt.isEmpty { return alt }
            if case .file(let url, _) = location { return url.lastPathComponent }
            return L("images.source.agent")
        }
    }
}

extension AgentImageKey {
    init?(_ item: AgentImageItem) {
        switch item.content {
        case .stored(let ref): self = .stored(ref)
        case .located(let location, _): self.init(location)
        }
    }
}
