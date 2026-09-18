import AppKit
import MightyCore

/// A transcript is one attributed document. Paragraphs and messages are not
/// separate selectable views, so the native text system can select across them.
@MainActor
enum AgentTranscriptFormat {
    static let accent = NSColor(calibratedRed: 0.86, green: 0.65, blue: 0.55, alpha: 1)
    static let codeAttribute = NSAttributedString.Key("MightyTranscriptCode")

    /// `references` turns file paths and addresses into links. Only the Mighty
    /// graph opts in; the basic transcript keeps model text as plain text.
    static func entry(_ entry: LogEntry, provider: String, running: Bool, expanded: Bool, references: Bool = false) -> NSAttributedString {
        let builder = Builder(references: references)
        if let activity = entry.activity {
            let live = running && ["running", "waiting"].contains(activity.state)
            let color = activity.state == "error" ? NSColor.systemRed : live ? accent : .secondaryLabelColor
            let line = NSMutableAttributedString(attributedString: symbol(AgentActivityRow.symbol(activity.kind), color: color))
            let summary = activity.summary.isEmpty ? activity.toolName ?? "작업" : activity.summary
            let font = ["command", "read", "edit"].contains(activity.kind) ? NSFont.monospacedSystemFont(ofSize: 12, weight: .regular) : .systemFont(ofSize: 12)
            builder.appendText("  " + summary, attributes: [.font: font, .foregroundColor: NSColor.secondaryLabelColor], to: line)
            if let duration = ActivitySupport.durationLabel(activity) {
                line.append(NSAttributedString(string: "  · " + duration, attributes: [.font: NSFont.monospacedDigitSystemFont(ofSize: 10, weight: .regular), .foregroundColor: NSColor.secondaryLabelColor]))
            }
            if live { line.append(NSAttributedString(string: activity.state == "waiting" ? "  · 대기 중" : "  · 진행 중", attributes: [.font: NSFont.systemFont(ofSize: 10), .foregroundColor: accent])) }
            else if activity.state == "error" { line.append(NSAttributedString(string: "  · 실패", attributes: [.font: NSFont.systemFont(ofSize: 10), .foregroundColor: NSColor.systemRed])) }
            if !(activity.output ?? "").isEmpty {
                var url = URLComponents()
                url.scheme = "mighty-transcript"; url.host = "activity"
                url.queryItems = [URLQueryItem(name: "id", value: entry.id)]
                if let link = url.url {
                    line.append(NSAttributedString(string: expanded ? "  상세 접기" : "  상세 보기", attributes: [.font: NSFont.systemFont(ofSize: 10), .link: link, .foregroundColor: accent]))
                }
            }
            builder.paragraph(line, font: font, color: .secondaryLabelColor, spacing: 6)
            if expanded, let output = activity.output, !output.isEmpty { builder.code(output, language: nil) }
        } else if entry.kind == "assistant" {
            let selectedProvider = entry.provider ?? provider
            let header = NSMutableAttributedString(attributedString: providerSymbol(selectedProvider, color: accent))
            let timestamp = ISO8601DateFormatter().date(from: entry.timestamp)?.formatted(date: .omitted, time: .shortened) ?? ""
            header.append(NSAttributedString(string: "  \(ProviderOptions.label(selectedProvider))\(timestamp.isEmpty ? "" : "  ·  " + timestamp)", attributes: [.font: NSFont.systemFont(ofSize: 11, weight: .medium), .foregroundColor: NSColor.secondaryLabelColor]))
            builder.paragraph(header, spacing: 10)
            if let questions = UserQuestionnaire.parse(inputJSON: entry.text) {
                builder.questionnaire(questions)
            } else {
                for block in AgentMarkdownDocument.parse(entry.text).blocks { builder.block(block) }
            }
        } else if entry.kind == "user" {
            let line = NSMutableAttributedString(attributedString: symbol("arrow.up.right", color: accent))
            builder.appendText("  " + entry.text, attributes: [:], to: line)
            builder.paragraph(line, spacing: 12, box: builder.box(background: accent.withAlphaComponent(0.07), border: .clear))
        } else if entry.kind == "output" {
            builder.code(entry.text, language: nil)
        } else {
            let color: NSColor = entry.kind == "error" ? .systemRed : .secondaryLabelColor
            let line = NSMutableAttributedString(attributedString: symbol(entry.kind == "error" ? "exclamationmark.circle" : "info.circle", color: color))
            builder.appendText("  " + entry.text, attributes: [:], to: line)
            builder.paragraph(line, font: .systemFont(ofSize: 12), color: color, spacing: 7)
        }
        builder.separation()
        return NSAttributedString(attributedString: builder.result)
    }

    private static func providerSymbol(_ provider: String, color: NSColor) -> NSAttributedString {
        // The mark keeps its brand colour; `color` only styles the fallback bullet.
        guard let image = ProviderIconImage.image(provider: provider, pointSize: 11) else { return NSAttributedString(string: "•", attributes: [.foregroundColor: color]) }
        let attachment = NSTextAttachment()
        attachment.image = image
        attachment.bounds = NSRect(x: 0, y: -2, width: 13, height: 13)
        return NSAttributedString(attachment: attachment)
    }

    private static func symbol(_ name: String, color: NSColor) -> NSAttributedString {
        guard let image = NSImage(systemSymbolName: name, accessibilityDescription: nil)?.withSymbolConfiguration(.init(pointSize: 11, weight: .medium).applying(.init(paletteColors: [color]))) else { return NSAttributedString(string: "•") }
        let attachment = NSTextAttachment()
        attachment.image = image
        attachment.bounds = NSRect(x: 0, y: -2, width: 13, height: 13)
        return NSAttributedString(attachment: attachment)
    }

    @MainActor private final class Builder {
        let result = NSMutableAttributedString(string: "")
        let references: Bool
        init(references: Bool) { self.references = references }

        /// Plain text, with file paths and addresses linked when enabled.
        func appendText(_ string: String, attributes: [NSAttributedString.Key: Any], to target: NSMutableAttributedString) {
            guard references else { target.append(NSAttributedString(string: string, attributes: attributes)); return }
            var cursor = string.startIndex
            for match in ReferenceLinkSupport.matches(in: string) where match.range.lowerBound >= cursor {
                if match.range.lowerBound > cursor { target.append(NSAttributedString(string: String(string[cursor..<match.range.lowerBound]), attributes: attributes)) }
                var linked = attributes
                if let url = match.url {
                    if AgentMarkdownDocument.safeLink(url) { linked[.link] = url; linked[.foregroundColor] = AgentTranscriptFormat.accent }
                } else if let url = ReferenceLinkSupport.referenceURL(path: match.path, line: match.line) {
                    linked[.link] = url; linked[.foregroundColor] = AgentTranscriptFormat.accent; linked[.underlineStyle] = NSUnderlineStyle.single.rawValue
                }
                target.append(NSAttributedString(string: String(string[match.range]), attributes: linked))
                cursor = match.range.upperBound
            }
            if cursor < string.endIndex { target.append(NSAttributedString(string: String(string[cursor...]), attributes: attributes)) }
        }

        func separation() {
            result.append(NSAttributedString(string: "\n", attributes: [.font: NSFont.systemFont(ofSize: 4), .paragraphStyle: style(spacing: 2)]))
        }

        func style(indent: CGFloat = 0, spacing: CGFloat = 8) -> NSMutableParagraphStyle {
            let style = NSMutableParagraphStyle()
            style.lineSpacing = 3; style.paragraphSpacing = spacing
            style.firstLineHeadIndent = indent; style.headIndent = indent
            style.lineBreakMode = .byWordWrapping
            style.defaultTabInterval = 28
            return style
        }

        func paragraph(_ value: NSAttributedString, font: NSFont = .systemFont(ofSize: 13), color: NSColor = .labelColor, indent: CGFloat = 0, spacing: CGFloat = 8, box: NSTextBlock? = nil, paragraphStyle: NSMutableParagraphStyle? = nil) {
            let paragraph = paragraphStyle ?? style(indent: indent, spacing: spacing)
            if let box { paragraph.textBlocks = [box] }
            let text = NSMutableAttributedString(attributedString: value)
            let range = NSRange(location: 0, length: text.length)
            text.enumerateAttributes(in: range) { attributes, range, _ in
                if attributes[.font] == nil { text.addAttribute(.font, value: font, range: range) }
                if attributes[.foregroundColor] == nil { text.addAttribute(.foregroundColor, value: color, range: range) }
            }
            text.addAttribute(.paragraphStyle, value: paragraph, range: range)
            if !text.string.hasSuffix("\n") { text.append(NSAttributedString(string: "\n", attributes: [.font: font, .foregroundColor: color, .paragraphStyle: paragraph])) }
            result.append(text)
        }

        func inline(_ source: AttributedString, font: NSFont = .systemFont(ofSize: 13), color: NSColor = .labelColor) -> NSAttributedString {
            let result = NSMutableAttributedString(string: "")
            for run in source.runs {
                let intent = run.inlinePresentationIntent
                var selectedFont = intent?.contains(.code) == true ? NSFont.monospacedSystemFont(ofSize: min(font.pointSize, 12), weight: .regular) : font
                if intent?.contains(.stronglyEmphasized) == true { selectedFont = NSFontManager.shared.convert(selectedFont, toHaveTrait: .boldFontMask) }
                if intent?.contains(.emphasized) == true { selectedFont = NSFontManager.shared.convert(selectedFont, toHaveTrait: .italicFontMask) }
                var attributes: [NSAttributedString.Key: Any] = [.font: selectedFont, .foregroundColor: color]
                if intent?.contains(.code) == true { attributes[.backgroundColor] = NSColor.labelColor.withAlphaComponent(0.055) }
                if intent?.contains(.strikethrough) == true { attributes[.strikethroughStyle] = NSUnderlineStyle.single.rawValue }
                if let link = run.link, AgentMarkdownDocument.safeLink(link) { attributes[.link] = link; attributes[.foregroundColor] = AgentTranscriptFormat.accent }
                let text = String(source[run.range].characters)
                if attributes[.link] == nil, references, let path = run.referencePath, let url = ReferenceLinkSupport.referenceURL(path: path, line: nil) {
                    attributes[.link] = url; attributes[.foregroundColor] = AgentTranscriptFormat.accent; attributes[.underlineStyle] = NSUnderlineStyle.single.rawValue
                    result.append(NSAttributedString(string: text, attributes: attributes))
                } else if attributes[.link] == nil { appendText(text, attributes: attributes, to: result) }
                else { result.append(NSAttributedString(string: text, attributes: attributes)) }
            }
            return result
        }

        func block(_ block: AgentMarkdownBlock, indent: CGFloat = 0, quote: NSTextBlock? = nil) {
            switch block.kind {
            case .paragraph:
                paragraph(inline(block.text, color: quote == nil ? .labelColor : .secondaryLabelColor), indent: indent, box: quote)
            case .header(let level):
                let font = NSFont.systemFont(ofSize: level == 1 ? 22 : level == 2 ? 18 : level == 3 ? 15 : 13, weight: .semibold)
                let paragraphStyle = style(indent: indent, spacing: 10)
                paragraphStyle.paragraphSpacingBefore = level <= 2 ? 5 : 2
                paragraph(inline(block.text, font: font), font: font, indent: indent, box: quote, paragraphStyle: paragraphStyle)
            case .orderedList, .unorderedList:
                for item in block.children { listItem(item, ordered: block.kind == .orderedList, indent: indent, quote: quote) }
            case .listItem:
                for child in block.children { self.block(child, indent: indent, quote: quote) }
            case .codeBlock(let language):
                if let questions = UserQuestionnaire.parse(inputJSON: block.plainText) { questionnaire(questions) }
                else { code(block.plainText, language: language, indent: indent) }
            case .blockQuote:
                let border = NSTextBlock()
                border.setWidth(10, type: .absoluteValueType, for: .padding)
                border.setWidth(3, type: .absoluteValueType, for: .border, edge: .minX)
                border.setBorderColor(AgentTranscriptFormat.accent.withAlphaComponent(0.55), for: .minX)
                for child in block.children { self.block(child, indent: indent + 3, quote: border) }
            case .thematicBreak:
                paragraph(NSAttributedString(string: "────────────────────", attributes: [.foregroundColor: NSColor.separatorColor]), indent: indent, spacing: 10)
            case .table(let columns): table(block, columns: columns)
            case .tableCell: paragraph(inline(block.text), indent: indent, box: quote)
            case .tableHeaderRow, .tableRow:
                for child in block.children { self.block(child, indent: indent, quote: quote) }
            @unknown default:
                if !block.plainText.isEmpty { paragraph(inline(block.text), indent: indent, box: quote) }
                for child in block.children { self.block(child, indent: indent, quote: quote) }
            }
        }

        func questionnaire(_ form: UserQuestionnaire) {
            for (index, question) in form.questions.enumerated() {
                let title = "\(index + 1). \(question.header)  ·  \(question.multiSelect ? "복수 선택" : "하나 선택")"
                paragraph(NSAttributedString(string: title), font: .systemFont(ofSize: 11, weight: .semibold), color: AgentTranscriptFormat.accent, spacing: 6,
                          box: box(background: AgentTranscriptFormat.accent.withAlphaComponent(0.07), border: .clear))
                paragraph(NSAttributedString(string: question.question), font: .systemFont(ofSize: 14, weight: .semibold), spacing: 10)
                for option in question.options {
                    paragraph(NSAttributedString(string: "\(question.multiSelect ? "☐" : "○")  \(option.label)"), font: .systemFont(ofSize: 13, weight: .medium), spacing: 3)
                    if !option.description.isEmpty {
                        paragraph(NSAttributedString(string: option.description), font: .systemFont(ofSize: 12), color: .secondaryLabelColor, indent: 20, spacing: 10)
                    }
                }
                separation()
            }
        }

        private func listItem(_ item: AgentMarkdownBlock, ordered: Bool, indent: CGFloat, quote: NSTextBlock?) {
            var children = item.children
            var marker = "•"
            if ordered, case .listItem(let ordinal) = item.kind { marker = "\(ordinal)." }
            if var first = children.first, first.kind == .paragraph {
                let task = String(first.text.characters.prefix(4)).lowercased()
                if task == "[x] " || task == "[ ] " {
                    marker = task == "[x] " ? "☑" : "☐"
                    first.text = AttributedString(first.text[first.text.characters.index(first.text.startIndex, offsetBy: 4)...])
                    children[0] = first
                }
            }
            if let first = children.first, first.kind == .paragraph {
                let line = NSMutableAttributedString(string: marker + "\t", attributes: [.foregroundColor: AgentTranscriptFormat.accent])
                line.append(inline(first.text))
                let paragraphStyle = style(indent: indent, spacing: 6)
                paragraphStyle.headIndent = indent + 22
                paragraphStyle.tabStops = [NSTextTab(textAlignment: .left, location: indent + 22)]
                paragraph(line, indent: indent, spacing: 6, box: quote, paragraphStyle: paragraphStyle)
                children.removeFirst()
            } else { paragraph(NSAttributedString(string: marker), indent: indent, spacing: 3, box: quote) }
            for child in children { block(child, indent: indent + 22, quote: quote) }
        }

        func box(background: NSColor, border: NSColor = .separatorColor) -> NSTextBlock {
            let block = NSTextBlock()
            block.backgroundColor = background
            block.setWidth(10, type: .absoluteValueType, for: .padding)
            block.setWidth(0.5, type: .absoluteValueType, for: .border)
            block.setBorderColor(border)
            return block
        }

        func code(_ code: String, language: String?, indent: CGFloat = 0) {
            let background = box(background: NSColor.labelColor.withAlphaComponent(0.035))
            let start = result.length
            if let language, !language.isEmpty {
                paragraph(NSAttributedString(string: language.split(separator: " ").first.map(String.init) ?? language), font: .monospacedSystemFont(ofSize: 10, weight: .medium), color: .secondaryLabelColor, indent: indent, spacing: 5, box: background)
            }
            let paragraphStyle = style(indent: indent, spacing: 8)
            paragraphStyle.lineBreakMode = .byCharWrapping
            paragraph(NSAttributedString(string: code), font: .monospacedSystemFont(ofSize: 12, weight: .regular), indent: indent, box: background, paragraphStyle: paragraphStyle)
            result.addAttribute(AgentTranscriptFormat.codeAttribute, value: code, range: NSRange(location: start, length: result.length - start))
        }

        private func table(_ block: AgentMarkdownBlock, columns: [PresentationIntent.TableColumn]) {
            guard !columns.isEmpty else { return }
            let table = NSTextTable()
            table.numberOfColumns = columns.count
            table.layoutAlgorithm = .fixedLayoutAlgorithm
            table.collapsesBorders = true; table.hidesEmptyCells = false
            table.setContentWidth(100, type: .percentageValueType)
            for (rowIndex, row) in block.children.enumerated() {
                for column in columns.indices {
                    let cell = row.children.first { if case .tableCell(let index) = $0.kind { return index == column }; return false }
                    let box = NSTextTableBlock(table: table, startingRow: rowIndex, rowSpan: 1, startingColumn: column, columnSpan: 1)
                    box.setContentWidth(100 / CGFloat(columns.count), type: .percentageValueType)
                    box.setWidth(8, type: .absoluteValueType, for: .padding)
                    box.setWidth(0.5, type: .absoluteValueType, for: .border)
                    box.setBorderColor(.separatorColor)
                    if row.kind == .tableHeaderRow { box.backgroundColor = NSColor.labelColor.withAlphaComponent(0.04) }
                    let paragraphStyle = style(spacing: 0)
                    switch columns[column].alignment { case .left: paragraphStyle.alignment = .left; case .center: paragraphStyle.alignment = .center; case .right: paragraphStyle.alignment = .right; @unknown default: break }
                    let font = NSFont.systemFont(ofSize: 12, weight: row.kind == .tableHeaderRow ? .semibold : .regular)
                    paragraph(inline(cell?.text ?? AttributedString(""), font: font), font: font, box: box, paragraphStyle: paragraphStyle)
                }
            }
            separation()
        }
    }
}
