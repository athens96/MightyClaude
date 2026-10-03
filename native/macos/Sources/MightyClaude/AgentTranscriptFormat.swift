import AppKit
import MightyCore

/// A transcript is one attributed document. Paragraphs and messages are not
/// separate selectable views, so the native text system can select across them.
@MainActor
enum AgentTranscriptFormat {
    static let accent = Palette.nsAccent
    /// The accent at 7%, resolved per appearance: the user turn's wash.
    static let accentWash = Palette.nsToken(\.accent, alpha: 0.07)
    static let codeAttribute = NSAttributedString.Key("MightyTranscriptCode")
    /// The `AgentImageKey.id` of a picture's attachment character.
    static let imageAttribute = NSAttributedString.Key("MightyTranscriptImage")

    /// `references` turns file paths and addresses into links. Only the Mighty
    /// graph opts in; the basic transcript keeps model text as plain text.
    /// `imageRoot` is the workspace Markdown pictures may be read from
    /// (`AgentImagePaths`); without it only the temporary folders qualify.
    /// `cards` draws concept D's conversation in the same single document: the
    /// user's turn as an ink bubble, a reply as a white card under its speaker,
    /// each tool call as a chip row and code on the ink code surface. Every one is
    /// a paragraph text block (`RoundedTextBlock`), never a table, so selection,
    /// copy, find and VoiceOver read straight through them.
    static func entry(_ entry: LogEntry, provider: String, running: Bool, expanded: Bool, references: Bool = false,
                      records: [GraphResponseRecord] = [], childBlocks: [String: GraphChildBlock] = [:],
                      catalog: [ModelOption] = [], imageRoot: URL? = nil, cards: Bool = false) -> NSAttributedString {
        let builder = Builder(references: references, imageRoot: imageRoot, cards: cards)
        if cards, let activity = entry.activity {
            builder.toolChip(entry, activity: activity, running: running, expanded: expanded, records: records, childBlocks: childBlocks, catalog: catalog)
        } else if cards, entry.kind == "user" {
            builder.userBubble(entry)
        } else if let activity = entry.activity {
            let live = running && ["running", "waiting"].contains(activity.state)
            let color = activity.state == "error" ? Palette.nsErrText : live ? accent : .secondaryLabelColor
            let line = NSMutableAttributedString(attributedString: symbol(AgentActivityRow.symbol(activity.kind), color: color))
            let summary = activity.summary.isEmpty ? activity.toolName ?? L("transcript.tool.fallback") : activity.summary
            let font = ["command", "read", "edit"].contains(activity.kind) ? NSFont.monospacedSystemFont(ofSize: 12, weight: .regular) : .systemFont(ofSize: 12)
            builder.appendText("  " + summary, attributes: [.font: font, .foregroundColor: NSColor.secondaryLabelColor], to: line)
            if let duration = ActivitySupport.durationLabel(activity) {
                line.append(NSAttributedString(string: "  · " + duration, attributes: [.font: NSFont.monospacedDigitSystemFont(ofSize: 10, weight: .regular), .foregroundColor: NSColor.secondaryLabelColor]))
            }
            if live { line.append(NSAttributedString(string: "  · " + toolState(activity.state), attributes: [.font: NSFont.systemFont(ofSize: 10), .foregroundColor: accent])) }
            else if activity.state == "error" { line.append(NSAttributedString(string: "  · " + toolState(activity.state), attributes: [.font: NSFont.systemFont(ofSize: 10), .foregroundColor: Palette.nsErrText])) }
            if !records.isEmpty,
               let suffix = ModelUsageFormat.activitySuffix(activityId: activity.id, records: records, childBlock: childBlocks[activity.id], catalog: catalog, versioned: true) {
                line.append(NSAttributedString(string: "  " + suffix, attributes: [.font: NSFont.monospacedDigitSystemFont(ofSize: 10, weight: .regular), .foregroundColor: NSColor.secondaryLabelColor]))
            }
            if !(activity.output ?? "").isEmpty, let link = disclosureLink(entry.id) {
                line.append(NSAttributedString(string: "  " + disclosureLabel(expanded), attributes: [.font: NSFont.systemFont(ofSize: 10), .link: link, .foregroundColor: accent]))
            }
            builder.paragraph(line, font: font, color: .secondaryLabelColor, spacing: 6)
            if expanded, let output = activity.output, !output.isEmpty { builder.code(output, language: nil) }
        } else if entry.kind == "assistant" {
            let selectedProvider = entry.provider ?? provider
            // Concept D: the speaker and the whole reply share one white card.
            if cards { builder.container = Builder.card() }
            let header = NSMutableAttributedString(attributedString: providerSymbol(selectedProvider, color: accent))
            let timestamp = time(entry.timestamp)
            header.append(NSAttributedString(string: "  \(ProviderOptions.label(selectedProvider))\(timestamp.isEmpty ? "" : "  ·  " + timestamp)", attributes: [.font: NSFont.systemFont(ofSize: cards ? 12 : 11, weight: cards ? .semibold : .medium), .foregroundColor: builder.secondary]))
            builder.paragraph(header, spacing: 10)
            if let questions = UserQuestionnaire.parse(inputJSON: entry.text) {
                builder.questionnaire(questions)
            } else {
                for block in AgentMarkdownDocument.parse(entry.text).blocks { builder.block(block) }
            }
            builder.container = nil
        } else if entry.kind == "user" {
            let line = NSMutableAttributedString(attributedString: symbol("arrow.up.right", color: accent))
            builder.appendText("  " + entry.text, attributes: [:], to: line)
            builder.paragraph(line, spacing: 12, box: builder.box(background: accentWash, border: .clear))
        } else if entry.kind == "output" {
            builder.code(entry.text, language: nil)
        } else if entry.kind == "image", let refs = entry.images, !refs.isEmpty {
            // A tool's pictures, in order, under its row; the entry text names them.
            for ref in refs { builder.paragraph(AgentImageAttachmentCell.attachment(.stored(ref), caption: ref.source), spacing: 4) }
            builder.paragraph(NSAttributedString(string: entry.text), font: .systemFont(ofSize: 10), color: builder.secondary, spacing: 7)
        } else {
            let color: NSColor = entry.kind == "error" ? Palette.nsErrText : builder.secondary
            let line = NSMutableAttributedString(attributedString: symbol(entry.kind == "error" ? "exclamationmark.circle" : "info.circle", color: color))
            builder.appendText("  " + entry.text, attributes: [:], to: line)
            builder.paragraph(line, font: .systemFont(ofSize: 12), color: color, spacing: 7)
        }
        builder.separation()
        return NSAttributedString(attributedString: builder.result)
    }

    /// "오후 3:12" for an entry's ISO timestamp, empty when it does not parse.
    private static func time(_ timestamp: String) -> String {
        AgentRunTiming.parseTimestamp(timestamp)?.formatted(date: .omitted, time: .shortened) ?? ""
    }

    private static func providerSymbol(_ provider: String, color: NSColor) -> NSAttributedString {
        // The mark keeps its brand colour; `color` only styles the fallback bullet.
        guard let image = ProviderIconImage.image(provider: provider, pointSize: 11) else { return NSAttributedString(string: "•", attributes: [.foregroundColor: color]) }
        let attachment = NSTextAttachment()
        attachment.image = image
        attachment.bounds = NSRect(x: 0, y: -2, width: 13, height: 13)
        return NSAttributedString(attachment: attachment)
    }

    /// A live or failed tool call's state word, shared by the chip row and the plain row.
    private static func toolState(_ state: String) -> String {
        switch state {
        case "waiting": L("transcript.tool.waiting")
        case "error": L("transcript.tool.failed")
        default: L("transcript.tool.running")
        }
    }

    /// The words of a tool row's disclosure link.
    private static func disclosureLabel(_ expanded: Bool) -> String {
        expanded ? L("transcript.tool.hideDetail") : L("transcript.tool.showDetail")
    }

    /// The link a tool row's disclosure carries; `AgentTranscriptCoordinator` toggles on it.
    private static func disclosureLink(_ id: String) -> URL? {
        var url = URLComponents()
        url.scheme = "mighty-transcript"; url.host = "activity"
        url.queryItems = [URLQueryItem(name: "id", value: id)]
        return url.url
    }

    /// A tool call's state as a small filled square: ✓ done, ✕ failed, ● still going,
    /// – stopped. The fills carry only their own glyph ink (white; the amber its ink).
    private static func statusSquare(_ state: String, live: Bool) -> NSAttributedString {
        let fill: NSColor, glyph: NSColor, name: String
        switch state {
        case "completed": (fill, glyph, name) = (Palette.nsToken(\.done), Palette.nsToken(\.onStatus), "checkmark")
        case "error": (fill, glyph, name) = (Palette.nsToken(\.err), Palette.nsToken(\.onStatus), "xmark")
        case "waiting" where live: (fill, glyph, name) = (Palette.nsToken(\.wait), Palette.nsToken(\.onWait), "circle.fill")
        case "running" where live: (fill, glyph, name) = (Palette.nsToken(\.run), Palette.nsToken(\.onStatus), "circle.fill")
        default: (fill, glyph, name) = (Palette.nsToken(\.stop), Palette.nsToken(\.onStatus), "minus")
        }
        let size = NSSize(width: 14, height: 14)
        let image = NSImage(size: size, flipped: false) { rect in
            fill.setFill()
            NSBezierPath(roundedRect: rect, xRadius: 4, yRadius: 4).fill()
            let point: CGFloat = name == "circle.fill" ? 5 : 8
            if let symbol = NSImage(systemSymbolName: name, accessibilityDescription: nil)?
                .withSymbolConfiguration(.init(pointSize: point, weight: .heavy).applying(.init(paletteColors: [glyph]))) {
                let mark = symbol.size
                symbol.draw(in: NSRect(x: rect.midX - mark.width / 2, y: rect.midY - mark.height / 2, width: mark.width, height: mark.height))
            }
            return true
        }
        let attachment = NSTextAttachment()
        attachment.image = image
        attachment.bounds = NSRect(x: 0, y: -2.5, width: size.width, height: size.height)
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
        let imageRoot: URL?
        /// Concept D's conversation (see `entry(cards:)`).
        let cards: Bool
        /// The block every paragraph sits in while one is set: a reply's card.
        var container: NSTextBlock?
        init(references: Bool, imageRoot: URL?, cards: Bool) { self.references = references; self.imageRoot = imageRoot; self.cards = cards }

        /// Secondary words: the D ink on D surfaces, the system colour in the graph's blocks.
        var secondary: NSColor { cards ? Palette.nsToken(\.ink2) : .secondaryLabelColor }

        /// A reply's card: white, with the D border, padded like the phone's.
        static func card() -> NSTextBlock {
            let block = RoundedTextBlock()
            block.backgroundColor = Palette.nsToken(\.card)
            block.setBorderColor(Palette.nsToken(\.line))
            block.setWidth(1, type: .absoluteValueType, for: .border)
            block.setWidth(14, type: .absoluteValueType, for: .padding, edge: .minX)
            block.setWidth(14, type: .absoluteValueType, for: .padding, edge: .maxX)
            block.setWidth(11, type: .absoluteValueType, for: .padding, edge: .minY)
            block.setWidth(5, type: .absoluteValueType, for: .padding, edge: .maxY)
            return block
        }

        /// The user's turn: an ink bubble on the right, light words on it, its time under them.
        /// A text block cannot shrink to its words, so the bubble keeps the right 85% of the
        /// width and the words stay left-aligned in it.
        func userBubble(_ entry: LogEntry) {
            let bubble = RoundedTextBlock()
            bubble.backgroundColor = Palette.nsToken(\.ink)
            bubble.setWidth(15, type: .percentageValueType, for: .margin, edge: .minX)
            bubble.setWidth(14, type: .absoluteValueType, for: .padding, edge: .minX)
            bubble.setWidth(14, type: .absoluteValueType, for: .padding, edge: .maxX)
            bubble.setWidth(10, type: .absoluteValueType, for: .padding, edge: .minY)
            bubble.setWidth(8, type: .absoluteValueType, for: .padding, edge: .maxY)
            let ink = Palette.nsToken(\.card)
            let text = NSMutableAttributedString(string: "")
            // The accent fails contrast on the ink bubble; links there keep the bubble's
            // own ink and are told apart by their underline.
            appendText(entry.text, attributes: [:], to: text, linkColor: ink)
            paragraph(text, color: ink, spacing: 3, box: bubble)
            let timestamp = AgentTranscriptFormat.time(entry.timestamp)
            if !timestamp.isEmpty {
                let style = style(spacing: 0)
                style.alignment = .right
                paragraph(NSAttributedString(string: timestamp), font: .systemFont(ofSize: 10.5), color: ink, box: bubble, paragraphStyle: style)
            }
        }

        /// One tool call as a chip row: its state as a small filled square, the tool's
        /// name in bold, the detail in mono ink2, then the timing, state and disclosure.
        func toolChip(_ entry: LogEntry, activity: AgentActivity, running: Bool, expanded: Bool,
                      records: [GraphResponseRecord], childBlocks: [String: GraphChildBlock], catalog: [ModelOption]) {
            let live = running && ["running", "waiting"].contains(activity.state)
            let chip = RoundedTextBlock()
            chip.backgroundColor = Palette.nsToken(\.card)
            chip.setBorderColor(activity.state == "error" ? Palette.nsToken(\.errText) : Palette.nsToken(\.line))
            chip.setWidth(1, type: .absoluteValueType, for: .border)
            chip.setWidth(10, type: .absoluteValueType, for: .padding, edge: .minX)
            chip.setWidth(10, type: .absoluteValueType, for: .padding, edge: .maxX)
            chip.setWidth(7, type: .absoluteValueType, for: .padding, edge: .minY)
            chip.setWidth(7, type: .absoluteValueType, for: .padding, edge: .maxY)
            let mono = NSFont.monospacedSystemFont(ofSize: 11.5, weight: .regular)
            let line = NSMutableAttributedString(attributedString: AgentTranscriptFormat.statusSquare(activity.state, live: live))
            let name = activity.toolName?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            if !name.isEmpty {
                line.append(NSAttributedString(string: "  " + name, attributes: [.font: NSFont.systemFont(ofSize: 12.5, weight: .bold), .foregroundColor: NSColor.labelColor]))
            }
            let detail = activity.summary.isEmpty ? (name.isEmpty ? L("transcript.tool.fallback") : "") : activity.summary
            if !detail.isEmpty { appendText("  " + detail, attributes: [.font: mono, .foregroundColor: secondary], to: line) }
            let small = NSFont.systemFont(ofSize: 10.5)
            if let duration = ActivitySupport.durationLabel(activity) {
                line.append(NSAttributedString(string: "  · " + duration, attributes: [.font: NSFont.monospacedDigitSystemFont(ofSize: 10.5, weight: .regular), .foregroundColor: secondary]))
            }
            if live { line.append(NSAttributedString(string: "  · " + AgentTranscriptFormat.toolState(activity.state), attributes: [.font: small, .foregroundColor: activity.state == "waiting" ? Palette.nsToken(\.waitText) : AgentTranscriptFormat.accent])) }
            else if activity.state == "error" { line.append(NSAttributedString(string: "  · " + AgentTranscriptFormat.toolState(activity.state), attributes: [.font: small, .foregroundColor: Palette.nsErrText])) }
            if !records.isEmpty,
               let suffix = ModelUsageFormat.activitySuffix(activityId: activity.id, records: records, childBlock: childBlocks[activity.id], catalog: catalog, versioned: true) {
                line.append(NSAttributedString(string: "  " + suffix, attributes: [.font: NSFont.monospacedDigitSystemFont(ofSize: 10.5, weight: .regular), .foregroundColor: secondary]))
            }
            if !(activity.output ?? "").isEmpty, let link = AgentTranscriptFormat.disclosureLink(entry.id) {
                line.append(NSAttributedString(string: "  " + AgentTranscriptFormat.disclosureLabel(expanded), attributes: [.font: small, .link: link, .foregroundColor: AgentTranscriptFormat.accent]))
            }
            let style = style(spacing: 0)
            paragraph(line, font: mono, color: secondary, box: chip, paragraphStyle: style)
            if expanded, let output = activity.output, !output.isEmpty { separation(); code(output, language: nil) }
        }

        /// A Markdown picture: drawn when its target passes the path rule or is
        /// a data URI; an http(s) target stays a link and is never fetched.
        func markdownImage(_ target: String, alt: String, font: NSFont) -> NSAttributedString {
            let location = AgentImagePaths.locate(target, workspaceRoot: imageRoot)
            if let key = AgentImageKey(location) { return AgentImageAttachmentCell.attachment(key, caption: alt) }
            let label = alt.isEmpty ? String(target.prefix(120)) : alt
            let result = NSMutableAttributedString(string: label, attributes: [.font: font, .foregroundColor: NSColor.labelColor])
            let note: String
            if case .remote(let url) = location {
                if AgentMarkdownDocument.safeLink(url) {
                    result.addAttributes([.link: url, .foregroundColor: AgentTranscriptFormat.accent], range: NSRange(location: 0, length: result.length))
                }
                note = L("images.remote")
            } else { note = L("images.refused") }
            result.append(NSAttributedString(string: " (" + note + ")", attributes: [.font: NSFont.systemFont(ofSize: max(10, font.pointSize - 2)), .foregroundColor: secondary]))
            return result
        }

        /// Plain text, with file paths and addresses linked when enabled. Links take the
        /// accent unless `linkColor` is given; then they take it with an underline.
        func appendText(_ string: String, attributes: [NSAttributedString.Key: Any], to target: NSMutableAttributedString, linkColor: NSColor? = nil) {
            guard references else { target.append(NSAttributedString(string: string, attributes: attributes)); return }
            var cursor = string.startIndex
            for match in ReferenceLinkSupport.matches(in: string) where match.range.lowerBound >= cursor {
                if match.range.lowerBound > cursor { target.append(NSAttributedString(string: String(string[cursor..<match.range.lowerBound]), attributes: attributes)) }
                var linked = attributes
                if let url = match.url {
                    if AgentMarkdownDocument.safeLink(url) {
                        linked[.link] = url; linked[.foregroundColor] = linkColor ?? AgentTranscriptFormat.accent
                        if linkColor != nil { linked[.underlineStyle] = NSUnderlineStyle.single.rawValue }
                    }
                } else if let url = ReferenceLinkSupport.referenceURL(path: match.path, line: match.line) {
                    linked[.link] = url; linked[.foregroundColor] = linkColor ?? AgentTranscriptFormat.accent; linked[.underlineStyle] = NSUnderlineStyle.single.rawValue
                }
                target.append(NSAttributedString(string: String(string[match.range]), attributes: linked))
                cursor = match.range.upperBound
            }
            if cursor < string.endIndex { target.append(NSAttributedString(string: String(string[cursor...]), attributes: attributes)) }
        }

        /// A thin gap paragraph. Inside a reply's card it stays in the card, so a table
        /// or questionnaire does not split the reply into several cards.
        func separation() {
            let paragraph = style(spacing: 2)
            if let container { paragraph.textBlocks = [container] }
            result.append(NSAttributedString(string: "\n", attributes: [.font: NSFont.systemFont(ofSize: 4), .paragraphStyle: paragraph]))
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
            let blocks = [container, box].compactMap { $0 }
            if !blocks.isEmpty { paragraph.textBlocks = blocks }
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
                if intent?.contains(.code) == true {
                    // Concept D: inline code is the accent on its soft tint, as in the mockup.
                    attributes[.backgroundColor] = cards ? Palette.nsToken(\.accentSoft) : NSColor.labelColor.withAlphaComponent(0.055)
                    if cards { attributes[.foregroundColor] = AgentTranscriptFormat.accent }
                }
                if intent?.contains(.strikethrough) == true { attributes[.strikethroughStyle] = NSUnderlineStyle.single.rawValue }
                if let link = run.link, AgentMarkdownDocument.safeLink(link) { attributes[.link] = link; attributes[.foregroundColor] = AgentTranscriptFormat.accent }
                let text = String(source[run.range].characters)
                if let target = run.imageSource {
                    result.append(markdownImage(target, alt: text, font: font))
                    continue
                }
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
                paragraph(inline(block.text, color: quote == nil ? .labelColor : secondary), indent: indent, box: quote)
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
                border.setBorderColor(Palette.nsToken(\.accent, alpha: 0.55), for: .minX)
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
                          box: box(background: AgentTranscriptFormat.accentWash, border: .clear))
                paragraph(NSAttributedString(string: question.question), font: .systemFont(ofSize: 14, weight: .semibold), spacing: 10)
                for option in question.options {
                    paragraph(NSAttributedString(string: "\(question.multiSelect ? "☐" : "○")  \(option.label)"), font: .systemFont(ofSize: 13, weight: .medium), spacing: 3)
                    if !option.description.isEmpty {
                        paragraph(NSAttributedString(string: option.description), font: .systemFont(ofSize: 12), color: secondary, indent: 20, spacing: 10)
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
            // Concept D: code sits on the ink code surface in both modes.
            let background: NSTextBlock
            if cards {
                background = RoundedTextBlock()
                background.backgroundColor = Palette.nsToken(\.codeSurface)
                background.setWidth(12, type: .absoluteValueType, for: .padding)
            } else {
                background = box(background: NSColor.labelColor.withAlphaComponent(0.035))
            }
            let ink: NSColor = cards ? Palette.nsToken(\.codeText) : .labelColor
            let start = result.length
            if let language, !language.isEmpty {
                paragraph(NSAttributedString(string: language.split(separator: " ").first.map(String.init) ?? language), font: .monospacedSystemFont(ofSize: 10, weight: .medium), color: cards ? ink : secondary, indent: indent, spacing: 5, box: background)
            }
            let paragraphStyle = style(indent: indent, spacing: cards ? 2 : 8)
            paragraphStyle.lineBreakMode = .byCharWrapping
            paragraph(NSAttributedString(string: code), font: .monospacedSystemFont(ofSize: 12, weight: .regular), color: ink, indent: indent, box: background, paragraphStyle: paragraphStyle)
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

/// A paragraph text block that paints its background and border as one rounded
/// rectangle inside its margins: the bubbles, cards, chips and code of concept D.
/// It lays out exactly like `NSTextBlock` (padding, border width and margins all
/// count); only the painting differs, so selection and copying are unchanged.
final class RoundedTextBlock: NSTextBlock {
    static let radius: CGFloat = 11

    override func drawBackground(withFrame frameRect: NSRect, in controlView: NSView?, characterRange charRange: NSRange, layoutManager: NSLayoutManager) {
        // A margin may be a percentage (the user bubble keeps the right 85%); the text
        // system resolves it against the block's frame width, so the paint does too.
        func margin(_ edge: NSRectEdge) -> CGFloat {
            let value = width(for: .margin, edge: edge)
            return widthValueType(for: .margin, edge: edge) == .percentageValueType ? frameRect.width * value / 100 : value
        }
        let left = margin(.minX), right = margin(.maxX)
        let top = margin(.minY), bottom = margin(.maxY)
        let rect = NSRect(x: frameRect.minX + left, y: frameRect.minY + top,
                          width: frameRect.width - left - right, height: frameRect.height - top - bottom)
        guard rect.width > 1, rect.height > 1 else { return }
        let line = width(for: .border, edge: .minX)
        let path = NSBezierPath(roundedRect: rect.insetBy(dx: line / 2, dy: line / 2), xRadius: Self.radius, yRadius: Self.radius)
        if let fill = backgroundColor { fill.setFill(); path.fill() }
        if line > 0, let stroke = borderColor(for: .minX) {
            stroke.setStroke()
            path.lineWidth = line
            path.stroke()
        }
    }
}
