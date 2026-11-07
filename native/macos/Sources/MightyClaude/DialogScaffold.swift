import AppKit
import MightyCore
import SwiftUI

/// The popup standard on the Mac (.omc/plans/popup-standard.md, sizes from `DesignMetrics.Dialog`).
/// Kind A (a confirmation) stays the system's `.confirmationDialog` or NSAlert; kind D is a
/// `.popover`. Kinds B (a form sheet: buttons only, no ✕) and C (a panel: one ✕ in the header, no
/// 닫기 at the bottom, resizable, its size remembered) are a `.sheet` drawn by this scaffold:
/// a 48pt header (icon, 15pt title, 12pt subtitle, tools, the panel's ✕), the body inset 16pt, and
/// an optional `DialogFooter`. The sheet keeps the system's corners and shadow.
struct DialogScaffold<Tools: View, Content: View, Footer: View>: View {
    let title: String
    var subtitle: String?
    /// An SF Symbol before the title, in the accent.
    var systemImage: String?
    /// The panel's ✕ (kind C); nil for a form sheet, which closes from its buttons.
    var close: DialogClose?
    /// The body scrolls under a fixed header and footer; off when the body lays out its own scroll view.
    var scrolls: Bool
    private let tools: Tools
    private let content: Content
    private let footer: Footer

    init(_ title: String, subtitle: String? = nil, systemImage: String? = nil, close: DialogClose? = nil, scrolls: Bool = false,
         @ViewBuilder tools: () -> Tools, @ViewBuilder content: () -> Content, @ViewBuilder footer: () -> Footer) {
        self.title = title
        self.subtitle = subtitle
        self.systemImage = systemImage
        self.close = close
        self.scrolls = scrolls
        self.tools = tools()
        self.content = content()
        self.footer = footer()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider()
            if scrolls {
                ScrollView {
                    content
                        .padding(DesignMetrics.Dialog.padding)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: .infinity)
            } else {
                content
                    .padding(DesignMetrics.Dialog.padding)
                    .frame(maxWidth: .infinity, alignment: .topLeading)
            }
            footer
        }
        // The app's card surface in both themes, not the sheet's stock grey.
        .background(Palette.panel)
        // A panel at work cannot be closed: its ✕ is off, and so are Esc and the sheet's own dismissal.
        .interactiveDismissDisabled(close?.busy == true)
    }

    private var header: some View {
        HStack(spacing: DesignMetrics.Spacing.md) {
            if let systemImage {
                Image(systemName: systemImage)
                    .font(.ui(size: DesignMetrics.Dialog.iconSize * 0.85))
                    .foregroundStyle(Palette.accent)
                    .frame(width: DesignMetrics.Dialog.iconSize, height: DesignMetrics.Dialog.iconSize)
                    .accessibilityHidden(true)
            }
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                Text(title)
                    .font(.ui(size: DesignMetrics.Dialog.titleSize, weight: .semibold)).foregroundStyle(Palette.ink)
                    .lineLimit(1)
                    .accessibilityAddTraits(.isHeader)
                if let subtitle, !subtitle.isEmpty {
                    Text(subtitle)
                        .font(.ui(size: DesignMetrics.Dialog.subtitleSize)).foregroundStyle(Palette.ink2)
                        .lineLimit(1).truncationMode(.middle)
                        .help(subtitle)
                }
            }
            Spacer(minLength: DesignMetrics.Spacing.md)
            tools
            if let close { DialogCloseButton(close: close) }
        }
        .padding(.horizontal, DesignMetrics.Dialog.padding)
        .padding(.vertical, DesignMetrics.Spacing.sm)
        .frame(minHeight: DesignMetrics.Dialog.headerHeight)
    }
}

extension DialogScaffold where Tools == EmptyView {
    /// A form sheet, or a panel with nothing in its header but the ✕.
    init(_ title: String, subtitle: String? = nil, systemImage: String? = nil, close: DialogClose? = nil, scrolls: Bool = false,
         @ViewBuilder content: () -> Content, @ViewBuilder footer: () -> Footer) {
        self.init(title, subtitle: subtitle, systemImage: systemImage, close: close, scrolls: scrolls,
                  tools: { EmptyView() }, content: content, footer: footer)
    }
}

extension DialogScaffold where Footer == EmptyView {
    /// A panel with neither status nor buttons: its tools sit in the header (decision 10).
    init(_ title: String, subtitle: String? = nil, systemImage: String? = nil, close: DialogClose? = nil, scrolls: Bool = false,
         @ViewBuilder tools: () -> Tools, @ViewBuilder content: () -> Content) {
        self.init(title, subtitle: subtitle, systemImage: systemImage, close: close, scrolls: scrolls,
                  tools: tools, content: content, footer: { EmptyView() })
    }
}

/// A panel's ✕: what it does, whether the panel is at work (✕ dimmed, `dialog.closeBusy` as its
/// tip, Esc ignored), and the identifier the smoke steps press it by.
struct DialogClose {
    var busy = false
    var identifier = "dialog-close"
    let action: () -> Void
}

/// The panel's ✕ (kind C), also the settings window's: a 28pt square, a 14pt glyph, a wash on hover. Esc presses it.
struct DialogCloseButton: View {
    let close: DialogClose
    @ViewState private var hovering = false

    var body: some View {
        Button(action: close.action) {
            Image(systemName: "xmark")
                .font(.ui(size: DesignMetrics.Dialog.closeGlyph, weight: .medium))
                .foregroundStyle(Palette.ink2)
                .frame(width: DesignMetrics.Dialog.closeHit, height: DesignMetrics.Dialog.closeHit)
                .background(RoundedRectangle(cornerRadius: DesignMetrics.Dialog.closeRadius, style: .continuous)
                    .fill(hovering && !close.busy ? Palette.track : Color.clear))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .keyboardShortcut(.cancelAction)
        .disabled(close.busy)
        .opacity(close.busy ? 0.4 : 1)
        .onHover { hovering = $0 }
        .help(close.busy ? L("dialog.closeBusy") : L("dialog.close"))
        .accessibilityLabel(L("dialog.close"))
        .accessibilityHint(close.busy ? L("dialog.closeBusy") : "")
        .accessibilityIdentifier(close.identifier)
    }
}

/// The 52pt bar under a dialog's body: the status on the leading side (progress, a note, a third
/// action), the buttons on the trailing side in [secondary] [cancel] [primary] order. Buttons are
/// the regular size; the caller marks the primary `.borderedProminent` with `.defaultAction`, and
/// the cancel `.cancelAction`.
struct DialogFooter<Status: View, Buttons: View>: View {
    private let status: Status
    private let buttons: Buttons

    init(@ViewBuilder status: () -> Status, @ViewBuilder buttons: () -> Buttons) {
        self.status = status()
        self.buttons = buttons()
    }

    var body: some View {
        VStack(spacing: 0) {
            Divider()
            HStack(spacing: DesignMetrics.Dialog.buttonGap) {
                status
                    .font(.ui(size: 11))
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: DesignMetrics.Spacing.md)
                buttons
            }
            .controlSize(.regular)
            .padding(.horizontal, DesignMetrics.Dialog.padding)
            .padding(.vertical, DesignMetrics.Spacing.md)
            .frame(minHeight: DesignMetrics.Dialog.footerHeight)
        }
    }
}

extension DialogFooter where Status == EmptyView {
    init(@ViewBuilder buttons: () -> Buttons) {
        self.init(status: { EmptyView() }, buttons: buttons)
    }
}

/// A dialog body that is still loading: a small spinner and `dialog.loading` in the middle and, for
/// a list, rows shaped like the ones to come.
struct DialogLoading: View {
    var skeletonRows = 0

    var body: some View {
        VStack(spacing: DesignMetrics.Spacing.lg) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                ProgressView().controlSize(.small)
                Text(L("dialog.loading")).font(.ui(size: 12)).foregroundStyle(Palette.ink2)
            }
            .accessibilityElement(children: .combine)
            if skeletonRows > 0 {
                VStack(spacing: DesignMetrics.Spacing.md) {
                    ForEach(0..<skeletonRows, id: \.self) { _ in
                        RoundedRectangle(cornerRadius: DesignMetrics.Dialog.buttonRadius, style: .continuous)
                            .fill(Palette.raised)
                            .frame(maxWidth: .infinity, minHeight: DesignMetrics.Dialog.headerHeight, maxHeight: DesignMetrics.Dialog.headerHeight)
                    }
                }
                .accessibilityHidden(true)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, DesignMetrics.Spacing.xl)
        .accessibilityIdentifier("dialog-loading")
    }
}

extension View {
    /// Kind B: one of the three fixed form-sheet widths (`DesignMetrics.Dialog.sheetWidthS/M/L`); the height follows the content.
    func dialogSheetWidth(_ width: CGFloat) -> some View {
        frame(width: width)
    }

    /// Kind C: a sheet the user can resize from its corner, opening at the size it last had
    /// (`id` names the panel: plugins, settings, plan document…), else at `ideal`, never under
    /// `minimum` and never larger than the screen.
    func dialogPanelFrame(id: String, ideal: CGSize, minimum: CGSize) -> some View {
        modifier(DialogPanelFrame(id: id, ideal: ideal, minimum: minimum))
    }
}

/// The last size of each panel, in UserDefaults under `dialog.size.<id>.width/height`.
private struct DialogPanelFrame: ViewModifier {
    let ideal: CGSize
    let minimum: CGSize
    @AppStorage private var savedWidth: Double
    @AppStorage private var savedHeight: Double

    init(id: String, ideal: CGSize, minimum: CGSize) {
        self.ideal = ideal
        self.minimum = minimum
        _savedWidth = AppStorage(wrappedValue: 0, "dialog.size.\(id).width")
        _savedHeight = AppStorage(wrappedValue: 0, "dialog.size.\(id).height")
    }

    /// The saved (or default) size, kept inside the screen and over the minimum.
    private var opening: CGSize {
        let screen = NSScreen.main?.visibleFrame.size ?? CGSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        let width = savedWidth > 0 ? CGFloat(savedWidth) : ideal.width
        let height = savedHeight > 0 ? CGFloat(savedHeight) : ideal.height
        return CGSize(width: max(minimum.width, min(width, screen.width)), height: max(minimum.height, min(height, screen.height)))
    }

    func body(content: Content) -> some View {
        let size = opening
        content
            .frame(minWidth: minimum.width, idealWidth: size.width, minHeight: minimum.height, idealHeight: size.height)
            .background(GeometryReader { proxy in
                Color.clear.onChange(of: proxy.size) { _, next in
                    guard next.width >= minimum.width, next.height >= minimum.height else { return }
                    savedWidth = Double(next.width)
                    savedHeight = Double(next.height)
                }
            })
    }
}
