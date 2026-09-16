import AppKit
import Foundation
import SwiftUI

struct AgentMarkdownBlock: Identifiable {
    let id: Int
    let kind: PresentationIntent.Kind
    var text: AttributedString
    var children: [AgentMarkdownBlock]

    var plainText: String { String(text.characters) }
    var descendants: [AgentMarkdownBlock] { [self] + children.flatMap(\.descendants) }
}

/// Foundation supplies the CommonMark/GFM block hierarchy. Keeping its intent
/// identities also preserves view identity as an unfinished response grows.
final class AgentMarkdownDocument: NSObject {
    let blocks: [AgentMarkdownBlock]
    private static let cache: NSCache<NSString, AgentMarkdownDocument> = {
        let cache = NSCache<NSString, AgentMarkdownDocument>()
        cache.countLimit = 128
        cache.totalCostLimit = 8 * 1024 * 1024
        return cache
    }()

    private init(blocks: [AgentMarkdownBlock]) { self.blocks = blocks }

    static func parse(_ source: String) -> AgentMarkdownDocument {
        let key = source as NSString
        if let cached = cache.object(forKey: key) { return cached }
        let document: AgentMarkdownDocument
        if source.utf8.count <= 131_072,
           let parsed = try? AttributedString(markdown: source, options: .init(interpretedSyntax: .full, failurePolicy: .returnPartiallyParsedIfPossible)) {
            var roots: [Builder] = []
            var nodes: [Int: Builder] = [:]
            var fallbackId = -1
            for (intent, range) in parsed.runs[\.presentationIntent] {
                var parent: Builder?
                for component in (intent?.components ?? []).reversed() {
                    if let existing = nodes[component.identity] { parent = existing }
                    else {
                        let node = Builder(id: component.identity, kind: component.kind)
                        if let parent { parent.children.append(node) } else { roots.append(node) }
                        nodes[component.identity] = node
                        parent = node
                    }
                }
                if parent == nil {
                    let node = Builder(id: fallbackId, kind: .paragraph)
                    fallbackId -= 1
                    roots.append(node)
                    parent = node
                }
                parent?.text.append(safeInline(AttributedString(parsed[range])))
            }
            let blocks = roots.map { $0.freeze() }
            document = AgentMarkdownDocument(blocks: blocks.isEmpty && !source.isEmpty ? [literal(source)] : blocks)
        } else { document = AgentMarkdownDocument(blocks: [literal(source)]) }
        cache.setObject(document, forKey: key, cost: source.utf8.count * 4)
        return document
    }

    static func safeLink(_ url: URL) -> Bool {
        switch url.scheme?.lowercased() {
        case "https", "http": return !(url.host ?? "").isEmpty
        case "mailto": return !url.absoluteString.dropFirst(7).isEmpty
        default: return false
        }
    }

    private static func literal(_ source: String) -> AgentMarkdownBlock { AgentMarkdownBlock(id: -1, kind: .paragraph, text: AttributedString(source), children: []) }

    private static func safeInline(_ source: AttributedString) -> AttributedString {
        var result = source
        result.presentationIntent = nil
        for run in Array(result.runs) {
            if let link = run.link, !safeLink(link) { result[run.range].link = nil }
            // Image alt text remains readable; messages never fetch image URLs
            // or load local files merely because the model mentioned them.
            result[run.range].imageURL = nil
        }
        return result
    }

    private final class Builder {
        let id: Int
        let kind: PresentationIntent.Kind
        var text = AttributedString()
        var children: [Builder] = []
        init(id: Int, kind: PresentationIntent.Kind) { self.id = id; self.kind = kind }
        func freeze() -> AgentMarkdownBlock { AgentMarkdownBlock(id: id, kind: kind, text: text, children: children.map { $0.freeze() }) }
    }
}

struct AgentMarkdownView: View {
    let source: String

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            ForEach(AgentMarkdownDocument.parse(source).blocks) { block in AgentMarkdownBlockView(block: block) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .font(.system(size: 13)).lineSpacing(4)
        .textSelection(.enabled)
        .tint(Palette.accent)
        .environment(\.openURL, OpenURLAction { url in AgentMarkdownDocument.safeLink(url) ? .systemAction : .discarded })
    }
}

private struct AgentMarkdownBlockView: View {
    let block: AgentMarkdownBlock

    @ViewBuilder var body: some View {
        switch block.kind {
        case .paragraph:
            inline(block.text).fixedSize(horizontal: false, vertical: true)
        case .header(let level):
            inline(block.text)
                .font(.system(size: level == 1 ? 22 : level == 2 ? 18 : level == 3 ? 15 : 13, weight: .semibold))
                .padding(.top, level <= 2 ? 6 : 2)
                .fixedSize(horizontal: false, vertical: true)
                .accessibilityAddTraits(.isHeader)
        case .orderedList:
            list(ordered: true)
        case .unorderedList:
            list(ordered: false)
        case .listItem:
            children(block.children, spacing: 6)
        case .codeBlock(let language):
            AgentCodeBlock(code: block.plainText, language: language)
        case .blockQuote:
            HStack(alignment: .top, spacing: 11) {
                RoundedRectangle(cornerRadius: 2).fill(Palette.accent.opacity(0.6)).frame(width: 3)
                children(block.children, spacing: 8).foregroundStyle(.secondary)
            }
            .fixedSize(horizontal: false, vertical: true)
            .padding(.vertical, 3)
        case .thematicBreak:
            Divider().padding(.vertical, 4)
        case .table(let columns):
            AgentMarkdownTable(block: block, columns: columns)
        case .tableCell:
            inline(block.text)
        case .tableHeaderRow, .tableRow:
            children(block.children, spacing: 5)
        @unknown default:
            inline(block.text)
            children(block.children, spacing: 6)
        }
    }

    private func inline(_ text: AttributedString) -> Text {
        var styled = text
        for run in Array(styled.runs) where run.inlinePresentationIntent?.contains(.code) == true {
            styled[run.range].font = .system(size: 12, design: .monospaced)
            styled[run.range].backgroundColor = Palette.subtle
        }
        return Text(styled)
    }

    private func children(_ children: [AgentMarkdownBlock], spacing: CGFloat) -> AnyView {
        AnyView(VStack(alignment: .leading, spacing: spacing) {
            ForEach(children) { child in AgentMarkdownBlockView(block: child) }
        }.frame(maxWidth: .infinity, alignment: .leading))
    }

    private func list(ordered: Bool) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            ForEach(block.children) { item in
                let task = taskItem(item)
                HStack(alignment: .top, spacing: 9) {
                    Group {
                        if let checked = task.checked {
                            Image(systemName: checked ? "checkmark.square.fill" : "square").foregroundStyle(checked ? Palette.accent : Color.secondary).font(.system(size: 12))
                        } else if ordered, case .listItem(let ordinal) = item.kind {
                            Text("\(ordinal).").monospacedDigit().foregroundStyle(.secondary)
                        } else { Text("•").foregroundStyle(Palette.accent) }
                    }.frame(minWidth: 17, alignment: .trailing).padding(.top, 1)
                    children(task.children, spacing: 7)
                }
            }
        }
    }

    private func taskItem(_ item: AgentMarkdownBlock) -> (checked: Bool?, children: [AgentMarkdownBlock]) {
        guard var first = item.children.first, first.kind == .paragraph else { return (nil, item.children) }
        let prefix = String(first.text.characters.prefix(4)).lowercased()
        guard prefix == "[x] " || prefix == "[ ] " else { return (nil, item.children) }
        first.text = AttributedString(first.text[first.text.characters.index(first.text.startIndex, offsetBy: 4)...])
        return (prefix == "[x] ", [first] + item.children.dropFirst())
    }
}

private struct AgentCodeBlock: View {
    let code: String
    let language: String?
    @ViewState private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text(language?.split(separator: " ").first.map(String.init) ?? "코드").font(.system(size: 10, weight: .medium, design: .monospaced)).foregroundStyle(.secondary)
                Spacer(minLength: 10)
                Button {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(code, forType: .string)
                    copied = true
                    Task { try? await Task.sleep(for: .seconds(1.5)); copied = false }
                } label: { Label(copied ? "복사됨" : "복사", systemImage: copied ? "checkmark" : "doc.on.doc").font(.system(size: 10)) }
                .buttonStyle(.plain).foregroundStyle(.secondary).help("코드 복사")
            }.padding(.horizontal, 12).padding(.vertical, 8).background(Palette.subtle)
            Divider()
            ScrollView(.horizontal) {
                Text(code.hasSuffix("\n") ? String(code.dropLast()) : code)
                    .font(.system(size: 12, design: .monospaced)).lineSpacing(3)
                    .fixedSize(horizontal: true, vertical: false)
                    .frame(maxWidth: .infinity, alignment: .leading).padding(12)
            }
        }
        .background(Palette.canvas.opacity(0.65), in: RoundedRectangle(cornerRadius: 9))
        .overlay { RoundedRectangle(cornerRadius: 9).stroke(Palette.border) }
        .clipShape(RoundedRectangle(cornerRadius: 9))
        .accessibilityLabel(language.map { "\($0) 코드" } ?? "코드 블록")
    }
}

private struct AgentMarkdownTable: View {
    let block: AgentMarkdownBlock
    let columns: [PresentationIntent.TableColumn]

    var body: some View {
        let widths = columnWidths()
        ScrollView(.horizontal) {
            VStack(spacing: 0) {
                ForEach(block.children) { row in
                    HStack(alignment: .top, spacing: 0) {
                        ForEach(columns.indices, id: \.self) { column in
                            let cell = row.children.first { if case .tableCell(let index) = $0.kind { return index == column }; return false }
                            Text(cell?.text ?? AttributedString(""))
                                .font(.system(size: 12, weight: row.kind == .tableHeaderRow ? .semibold : .regular))
                                .fixedSize(horizontal: false, vertical: true)
                                .frame(width: widths[column] - 24, alignment: alignment(columns[column].alignment))
                                .padding(.horizontal, 12).padding(.vertical, 9)
                        }
                    }
                    .background(row.kind == .tableHeaderRow ? Palette.subtle : Color.clear)
                    if row.id != block.children.last?.id { Divider() }
                }
            }
            .overlay { RoundedRectangle(cornerRadius: 7).stroke(Palette.border) }
            .clipShape(RoundedRectangle(cornerRadius: 7))
        }
        .accessibilityLabel("표")
    }

    private func alignment(_ value: PresentationIntent.TableColumn.Alignment) -> Alignment {
        switch value { case .left: return .leading; case .center: return .center; case .right: return .trailing; @unknown default: return .leading }
    }

    private func columnWidths() -> [CGFloat] {
        columns.indices.map { column in
            let widest = block.children.flatMap(\.children).filter { if case .tableCell(let index) = $0.kind { return index == column }; return false }
                .map { ($0.plainText as NSString).size(withAttributes: [.font: NSFont.systemFont(ofSize: 12, weight: .medium)]).width }.max() ?? 0
            return min(260, max(85, ceil(widest) + 24))
        }
    }
}

enum AgentMarkdownDiagnostics {
    static let fixture = """
    # 작업을 정리했어요

    **네이티브 화면**에서 읽기 편하게 표시합니다. `SessionPaneView.swift`와 [Swift 문서](https://www.swift.org/documentation/)를 확인하세요.

    ## 변경 사항
    - 읽기 편한 제목과 목록
      - 중첩 항목도 유지
    - [x] 입력과 터미널 유지

    > 작성 중인 초안과 실행 중인 터미널은 그대로 이어집니다.

    ```swift
    let message = "안녕하세요"
    print(message)
    ```

    | 항목 | 상태 |
    |:---|---:|
    | Markdown | 완료 |
    | 진행 상태 | 확인 중 |
    """

    static func checks() -> [String: Bool] {
        let blocks = AgentMarkdownDocument.parse(fixture).blocks.flatMap(\.descendants)
        let links = AgentMarkdownDocument.parse("[web](https://example.com) [file](file:///tmp/example) [script](javascript:alert(1))").blocks.flatMap(\.descendants).flatMap { Array($0.text.runs).compactMap(\.link) }
        let streaming = AgentMarkdownDocument.parse("```swift\nlet value = 1\n").blocks
        return [
            "headings": blocks.contains { if case .header = $0.kind { return true }; return false },
            "nestedLists": blocks.filter { $0.kind == .unorderedList }.count == 2,
            "quotes": blocks.contains { $0.kind == .blockQuote },
            "codeNewlines": blocks.contains { if case .codeBlock = $0.kind { return $0.plainText.contains("\nprint(message)") }; return false },
            "tables": blocks.contains { if case .table = $0.kind { return true }; return false },
            "safeLinks": links.count == 1 && links.first?.scheme == "https",
            "unfinishedCodeFence": streaming.contains { if case .codeBlock = $0.kind { return $0.plainText.contains("let value = 1") }; return false },
        ]
    }
}
