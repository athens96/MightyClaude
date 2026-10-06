import AppKit
import MightyCore
import SwiftUI

// Use the same NSViewRepresentable/NSScrollView boundary as a real split pane,
// with neighboring content and a composer reserving space below the document.
private struct TranscriptDiagnosticPane: View {
    let scroll: NSScrollView
    var body: some View {
        VStack(spacing: 0) {
            Text("도구 출력 펼침 · 연속 선택 검증").frame(maxWidth: .infinity).padding(8)
            Divider()
            HStack(spacing: 0) {
                TranscriptDiagnosticScroll(scroll: scroll)
                Divider()
                Text("옆 실행 창").frame(width: 100, height: 100)
            }
            Divider()
            Text("다음 요청 입력 영역").frame(maxWidth: .infinity).padding(16)
        }.frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

private struct TranscriptDiagnosticScroll: NSViewRepresentable {
    let scroll: NSScrollView
    func makeNSView(context: Context) -> NSScrollView { scroll }
    func updateNSView(_ view: NSScrollView, context: Context) {}
}

/// A graph block: a request preview above the transcript whose height can
/// change after mounting, and the corner resize handle drawn over it.
@MainActor
private final class TranscriptDiagnosticBlockModel: ObservableObject {
    @Published var previewHeight: CGFloat = 14
}

private struct TranscriptDiagnosticBlock: View {
    let scroll: NSScrollView
    @ObservedObject var model: TranscriptDiagnosticBlockModel
    var body: some View {
        VStack(spacing: 0) {
            Text("블록 높이 변화 검증").frame(maxWidth: .infinity).frame(height: 38)
            Divider()
            Color.clear.frame(height: model.previewHeight)
            Divider()
            TranscriptDiagnosticScroll(scroll: scroll)
        }
        .frame(width: 420, height: 380)
        .overlay(alignment: .bottomTrailing) {
            Image(systemName: "arrow.up.left.and.arrow.down.right").frame(width: 22, height: 22).padding(2)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// Called only by the isolated agent smoke. No CLI requests or general
/// pasteboard access; selection is exercised on the actual native renderer.
@MainActor
enum AgentTranscriptDiagnostics {
    static func run(screenshotDirectory: URL? = nil) async -> [String: Any] {
        var report: [String: Any] = ["passed": false, "verification": "SwiftUI split-pane embedded NSTextView; native selection/disclosure mouse events; glyph geometry; private pasteboard"]
        let previousWindow = NSApp.keyWindow
        defer { previousWindow?.makeKeyAndOrderFront(nil) }
        do {
            try await verifyRestoredHistory(report: &report, screenshotDirectory: screenshotDirectory)
            try await verifyShrinkingBlockKeepsLastLines(report: &report, screenshotDirectory: screenshotDirectory)
            // The pane draws concept D's cards; the graph's blocks keep the plain
            // look. Selection and streaming are held to the same checks in both.
            try await verifySelectionAndStreaming(cards: true, report: &report, screenshotDirectory: screenshotDirectory)
            try await verifySelectionAndStreaming(cards: false, report: &report, screenshotDirectory: nil)
            let checks = report.values.compactMap { $0 as? Bool }
            // `passed` starts false and is the only intentionally false flag.
            guard checks.filter({ !$0 }).count == 1 else { throw MightyError("출력 선택 또는 서식 회귀 검증에 실패했습니다.") }
            report["passed"] = true
        } catch { report["error"] = error.localizedDescription }
        return report
    }

    /// Native selection, copy, streaming, disclosure and Markdown on one transcript.
    /// The plain pass writes its results under `plain.`-prefixed keys.
    private static func verifySelectionAndStreaming(cards: Bool, report: inout [String: Any], screenshotDirectory: URL?) async throws {
        var local: [String: Any] = [:]
        defer { for (key, value) in local { report[cards ? key : "plain." + key] = value } }
        let coordinator = AgentTranscriptCoordinator()
        let scroll = coordinator.makeScrollView(sessionId: cards ? "transcript-fixture" : "transcript-fixture-plain")
        let window = NSWindow(contentRect: NSRect(x: 180, y: 150, width: 690, height: 570), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.title = "연속 선택 검증"
        window.isReleasedWhenClosed = false
        window.contentView = NSHostingView(rootView: TranscriptDiagnosticPane(scroll: scroll))
        defer { window.orderOut(nil); window.close() }
        var entries = [
            LogEntry(id: "answer-a", kind: "assistant", text: "## 선택 검증\n\n문단A: 첫 번째 문단을 연속 선택합니다.\n\n문단B: 다음 문단과 **강조 문구**도 함께 선택합니다.", provider: "claude"),
            LogEntry(id: "tool-a", kind: "system", text: "swift test", activity: AgentActivity(id: "tool-a", provider: "claude", kind: "command", state: "running", summary: "swift test")),
            LogEntry(id: "answer-b", kind: "assistant", text: "문단C: 다른 메시지도 같은 선택에 포함됩니다.", provider: "claude"),
        ]
        coordinator.update(entries: entries, provider: "claude", running: true, dark: false, cards: cards)
        window.makeKeyAndOrderFront(nil)
        try await Task.sleep(for: .milliseconds(100))
        guard let editor = coordinator.textView, window.makeFirstResponder(editor), let manager = editor.layoutManager, let container = editor.textContainer else { throw MightyError("네이티브 출력 뷰를 찾지 못했습니다.") }
        scroll.layoutSubtreeIfNeeded()
        manager.ensureLayout(for: container)
        scroll.contentView.scroll(to: .zero)
        scroll.reflectScrolledClipView(scroll.contentView)
        let source = editor.string as NSString
        let start = source.range(of: "문단A:").location
        let last = source.range(of: "다른 메시지도 같은 선택")
        guard start != NSNotFound, last.location != NSNotFound else { throw MightyError("선택 fixture가 렌더링되지 않았습니다.") }
        let target = NSRange(location: start, length: NSMaxRange(last) - start)
        editor.setSelectedRange(target)
        local["nativeRangeAcrossMessages"] = selectedText(editor).contains("문단B:") && selectedText(editor).contains("swift test") && selectedText(editor).contains("다른 메시지도")

        func point(at character: Int) -> NSPoint {
            let glyph = manager.glyphIndexForCharacter(at: character)
            let rect = manager.boundingRect(forGlyphRange: NSRange(location: glyph, length: 1), in: container)
            return editor.convert(NSPoint(x: rect.minX + editor.textContainerOrigin.x + 0.25, y: rect.midY + editor.textContainerOrigin.y), to: nil)
        }
        func mouse(_ type: NSEvent.EventType, point: NSPoint, number: Int) throws -> NSEvent {
            guard let event = NSEvent.mouseEvent(with: type, location: point, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil, eventNumber: number, clickCount: 1, pressure: type == .leftMouseUp ? 0 : 1) else { throw MightyError("선택 검증 이벤트를 만들지 못했습니다.") }
            return event
        }
        editor.setSelectedRange(NSRange(location: 0, length: 0))
        let down = try mouse(.leftMouseDown, point: point(at: start), number: 1)
        let drag = try mouse(.leftMouseDragged, point: point(at: NSMaxRange(last)), number: 2)
        let up = try mouse(.leftMouseUp, point: point(at: NSMaxRange(last)), number: 3)
        // NSTextView performs native mouse tracking synchronously. Queue
        // its drag/up first, then enter the same mouseDown path as a click.
        NSApp.postEvent(drag, atStart: false)
        NSApp.postEvent(up, atStart: false)
        editor.mouseDown(with: down)
        let dragged = selectedText(editor)
        let crossMessages = dragged.contains("첫 번째 문단") && dragged.contains("문단B:") && dragged.contains("swift test") && dragged.contains("다른 메시지도")
        local["nativeDragAcrossMessages"] = crossMessages
        local["dragSelectedRange"] = ["location": editor.selectedRange().location, "length": editor.selectedRange().length]
        local["dragSelectedText"] = dragged
        guard crossMessages else { throw MightyError("드래그 선택이 문단과 메시지 경계를 넘지 못했습니다.") }

        let pasteboard = NSPasteboard.withUniqueName()
        defer { pasteboard.releaseGlobally() }
        let copied = editor.writeSelection(to: pasteboard, types: [.string])
        let copiedText = pasteboard.string(forType: .string) ?? ""
        local["nativeCopyAcrossMessages"] = copied && copiedText.contains("문단B:") && copiedText.contains("다른 메시지도") && !copiedText.contains("\u{FFFC}")

        entries[2].text += "\n\n새 응답이 스트리밍으로 이어집니다."
        coordinator.update(entries: entries, provider: "claude", running: true, dark: false, cards: cards)
        local["selectionPreservedOnAppend"] = selectedText(editor) == dragged

        let selectedPhrase = "다른 메시지도 같은 선택"
        editor.setSelectedRange((editor.string as NSString).range(of: selectedPhrase))
        let previousLocation = editor.selectedRange().location
        entries[1].activity?.summary = "swift test --filter AgentActivityTests · 앞쪽 활동 행의 내용이 길어집니다."
        entries[1].activity?.state = "completed"
        entries[1].activity?.durationMs = 12_340
        coordinator.update(entries: entries, provider: "claude", running: true, dark: false, cards: cards)
        local["completedActivityDurationRendered"] = editor.string.contains("· " + L("run.activity.durationSeconds", ["seconds": "12.3"]))
        guard local["completedActivityDurationRendered"] as? Bool == true else { throw MightyError("완료된 도구 작업의 소요 시간이 표시되지 않았습니다.") }
        local["selectionPreservedAfterEarlierUpdate"] = selectedText(editor) == selectedPhrase && editor.selectedRange().location > previousLocation
        entries.removeFirst()
        coordinator.update(entries: entries, provider: "claude", running: true, dark: false, cards: cards)
        local["selectionPreservedAfterPruning"] = selectedText(editor) == selectedPhrase

        // Exercise real link tracking, not just a formatter with expanded=true.
        // The changed tool lies before an unchanged, cached answer segment.
        let toolOutput = (1...28).map { "도구 출력 \($0): 파일 검증과 테스트 결과를 확인합니다." }.joined(separator: "\n")
        entries[0].activity?.output = toolOutput
        coordinator.update(entries: entries, provider: "claude", running: false, dark: false, cards: cards)
        func glyphRect(_ phrase: String) throws -> NSRect {
            let range = (editor.string as NSString).range(of: phrase)
            guard range.location != NSNotFound else { throw MightyError("출력 배치 fixture 문구가 없습니다: \(phrase)") }
            let glyphs = manager.glyphRange(forCharacterRange: range, actualCharacterRange: nil)
            return manager.boundingRect(forGlyphRange: glyphs, in: container)
        }
        func clickDisclosure(_ title: String, number: Int) throws {
            let range = (editor.string as NSString).range(of: title)
            guard range.location != NSNotFound else { throw MightyError("도구 상세 링크가 없습니다.") }
            editor.scrollRangeToVisible(range)
            let location = point(at: range.location + 1)
            NSApp.postEvent(try mouse(.leftMouseUp, point: location, number: number + 1), atStart: false)
            editor.mouseDown(with: try mouse(.leftMouseDown, point: location, number: number))
        }
        let collapsedFollowing = try glyphRect("문단C:")
        let collapsedHeight = editor.frame.height
        try clickDisclosure(L("transcript.tool.showDetail"), number: 10)
        let expandedFollowing = try glyphRect("문단C:")
        let expandedOutput = try glyphRect(toolOutput)
        let expandedHeight = editor.frame.height
        local["nativeToolDisclosure"] = editor.string.contains(toolOutput) && editor.string.contains(L("transcript.tool.hideDetail"))
        local["expandedToolPushesFollowingParagraph"] = expandedFollowing.minY > collapsedFollowing.minY + 100 && expandedOutput.maxY <= expandedFollowing.minY
        local["expandedToolDocumentHeight"] = expandedHeight > collapsedHeight && expandedHeight >= manager.usedRect(for: container).maxY + editor.textContainerOrigin.y
        local["toolExpansionGeometry"] = ["collapsedFollowingY": collapsedFollowing.minY, "expandedFollowingY": expandedFollowing.minY, "outputBottomY": expandedOutput.maxY, "collapsedHeight": collapsedHeight, "expandedHeight": expandedHeight]
        if let screenshotDirectory {
            editor.scrollRangeToVisible((editor.string as NSString).range(of: "문단C:"))
            local["expandedToolScreenshot"] = try capture(window, in: screenshotDirectory, name: "transcript-tool-expanded.png")
        }

        try clickDisclosure(L("transcript.tool.hideDetail"), number: 20)
        let foldedFollowing = try glyphRect("문단C:")
        local["nativeToolCollapseRestoresLayout"] = !editor.string.contains(toolOutput) && abs(foldedFollowing.minY - collapsedFollowing.minY) < 1 && abs(editor.frame.height - collapsedHeight) < 1
        if let screenshotDirectory {
            local["collapsedToolScreenshot"] = try capture(window, in: screenshotDirectory, name: "transcript-tool-collapsed.png")
        }
        try clickDisclosure(L("transcript.tool.showDetail"), number: 30)
        editor.setSelectedRange((editor.string as NSString).range(of: selectedPhrase))
        let extraOutput = "\n추가 도구 출력: 스트리밍으로 이어지는 마지막 결과입니다."
        entries[0].activity?.output = toolOutput + extraOutput
        coordinator.update(entries: entries, provider: "claude", running: false, dark: false, cards: cards)
        let streamedFollowing = try glyphRect("문단C:")
        let streamedOutput = try glyphRect(toolOutput + extraOutput)
        local["expandedToolStreamingDoesNotOverlap"] = streamedOutput.maxY <= streamedFollowing.minY && streamedFollowing.minY > expandedFollowing.minY && selectedText(editor) == selectedPhrase

        // A split pane can become narrower while a tool remains expanded.
        window.setContentSize(NSSize(width: 390, height: 570))
        window.contentView?.layoutSubtreeIfNeeded()
        scroll.layoutSubtreeIfNeeded()
        manager.ensureLayout(for: container)
        let narrowFollowing = try glyphRect("문단C:")
        let narrowOutput = try glyphRect(toolOutput + extraOutput)
        local["expandedToolResizeDoesNotOverlap"] = narrowOutput.maxY <= narrowFollowing.minY && selectedText(editor) == selectedPhrase && editor.frame.height >= manager.usedRect(for: container).maxY + editor.textContainerOrigin.y
        window.setContentSize(NSSize(width: 690, height: 570))
        window.contentView?.layoutSubtreeIfNeeded()

        entries.append(LogEntry(id: "markdown-format", kind: "assistant", text: AgentMarkdownDiagnostics.fixture))
        coordinator.update(entries: entries, provider: "claude", running: false, dark: false, cards: cards)
        var hasTable = false, hasCode = false, hasHeading = false, safeLinks = true
        editor.textStorage?.enumerateAttributes(in: NSRange(location: 0, length: editor.textStorage?.length ?? 0)) { attributes, _, _ in
            if let paragraph = attributes[.paragraphStyle] as? NSParagraphStyle, paragraph.textBlocks.contains(where: { $0 is NSTextTableBlock }) { hasTable = true }
            if attributes[AgentTranscriptFormat.codeAttribute] != nil { hasCode = true }
            if let font = attributes[.font] as? NSFont, font.pointSize >= 18 { hasHeading = true }
            if let url = attributes[.link] as? URL, url.scheme != "mighty-transcript", !AgentMarkdownDocument.safeLink(url) { safeLinks = false }
        }
        local["nativeMarkdownTable"] = hasTable
        local["nativeMarkdownCode"] = hasCode
        local["nativeMarkdownHeading"] = hasHeading
        local["safeLinks"] = safeLinks
        local["singleNativeTextView"] = scroll.documentView === editor && !editor.isEditable && editor.isSelectable
    }

    private static func selectedText(_ editor: NSTextView) -> String {
        let range = editor.selectedRange()
        let text = editor.string as NSString
        return NSMaxRange(range) <= text.length ? text.substring(with: range) : ""
    }

    private static func verifyRestoredHistory(report: inout [String: Any], screenshotDirectory: URL?) async throws {
        let coordinator = AgentTranscriptCoordinator()
        let scroll = coordinator.makeScrollView(sessionId: "restored-history-fixture")
        let window = NSWindow(contentRect: NSRect(x: 160, y: 140, width: 610, height: 460), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.title = "복원된 대화의 첫 스크롤 검증"
        window.isReleasedWhenClosed = false
        defer { window.orderOut(nil); window.close() }
        var entries = (1...28).map { index in
            LogEntry(id: "restored-\(index)", kind: "assistant", text: "## 복원된 응답 \(index)\n\n이전 대화의 내용을 읽습니다. 창 너비에 따라 여러 줄로 바뀌어도 마지막 메시지가 처음에 보여야 합니다.\n\n응답 끝 \(index)", provider: "claude")
        }
        // Populate the original 500×300 native view before SwiftUI mounts it.
        // The first scroll must use the later, narrower real viewport instead.
        coordinator.update(entries: entries, provider: "claude", running: false, dark: false, cards: true)
        window.contentView = NSHostingView(rootView: TranscriptDiagnosticPane(scroll: scroll))
        window.makeKeyAndOrderFront(nil)
        guard let editor = coordinator.textView else { throw MightyError("복원된 출력 뷰가 없습니다.") }
        window.makeFirstResponder(editor)
        // No run-loop yield: the native layout must already point at the final
        // messages, rather than briefly painting old history before an async hop.
        window.contentView?.layoutSubtreeIfNeeded()
        scroll.needsLayout = true; scroll.layoutSubtreeIfNeeded()
        let firstBottom = max(0, editor.bounds.height - scroll.contentView.bounds.height)
        report["restoredHistoryPositionedBeforeAsyncPaint"] = firstBottom > 500 && abs(scroll.contentView.bounds.minY - firstBottom) < 2
        guard report["restoredHistoryPositionedBeforeAsyncPaint"] as? Bool == true else { throw MightyError("복원된 출력의 첫 native layout이 이전 메시지를 표시했습니다.") }
        try await Task.sleep(for: .milliseconds(120))
        window.contentView?.layoutSubtreeIfNeeded()
        let clip = scroll.contentView
        let bottom = max(0, editor.bounds.height - clip.bounds.height)
        report["restoredHistoryStartsAtBottom"] = bottom > 500 && abs(clip.bounds.minY - bottom) < 2
        report["restoredHistoryGeometry"] = ["originY": clip.bounds.minY, "bottomY": bottom, "viewportHeight": clip.bounds.height, "documentHeight": editor.bounds.height]
        guard report["restoredHistoryStartsAtBottom"] as? Bool == true else { throw MightyError("복원된 대화가 첫 표시에서 마지막 메시지로 이동하지 않았습니다.") }
        if let screenshotDirectory {
            report["restoredHistoryScreenshot"] = try capture(window, in: screenshotDirectory, name: "transcript-restored-bottom.png")
        }

        // Send a scroll-wheel event directly to this native scroll view; never
        // post it globally or change the real user's window/clipboard state.
        guard let wheel = CGEvent(scrollWheelEvent2Source: nil, units: .pixel, wheelCount: 1, wheel1: 500, wheel2: 0, wheel3: 0),
              let event = NSEvent(cgEvent: wheel) else { throw MightyError("복원 스크롤 검증 이벤트를 만들지 못했습니다.") }
        scroll.scrollWheel(with: event)
        try await Task.sleep(for: .milliseconds(100))
        let manualOrigin = clip.bounds.minY
        guard manualOrigin < bottom - 40 else { throw MightyError("복원된 출력의 수동 휠 스크롤을 재현하지 못했습니다.") }
        scroll.needsLayout = true
        window.contentView?.layoutSubtreeIfNeeded()
        coordinator.update(entries: entries, provider: "claude", running: false, dark: false, cards: true)
        entries.append(LogEntry(id: "restored-appended", kind: "assistant", text: "나중에 도착한 응답은 읽던 위치를 바꾸지 않습니다.", provider: "claude"))
        coordinator.update(entries: entries, provider: "claude", running: false, dark: false, cards: true)
        try await Task.sleep(for: .milliseconds(100))
        report["restoredHistoryManualScrollPreserved"] = abs(clip.bounds.minY - manualOrigin) < 2
        let selection = (editor.string as NSString).range(of: "응답 끝 20")
        editor.setSelectedRange(selection)
        coordinator.update(entries: entries, provider: "claude", running: true, dark: false, cards: true)
        try await Task.sleep(for: .milliseconds(50))
        report["restoredHistorySelectionPreserved"] = editor.selectedRange() == selection && abs(clip.bounds.minY - manualOrigin) < 2
        guard report["restoredHistoryManualScrollPreserved"] as? Bool == true,
              report["restoredHistorySelectionPreserved"] as? Bool == true else { throw MightyError("복원 후 수동 스크롤 또는 선택 위치가 바뀌었습니다.") }
    }

    /// A graph block that gets shorter after mounting (the request preview
    /// measuring itself, a live resize, a collapse) keeps a following reader
    /// at the true bottom with the last line clear of the corner handle, and
    /// leaves a reader who scrolled up where they were.
    private static func verifyShrinkingBlockKeepsLastLines(report: inout [String: Any], screenshotDirectory: URL?) async throws {
        let coordinator = AgentTranscriptCoordinator()
        let scroll = coordinator.makeScrollView(sessionId: "shrinking-block-fixture")
        guard let editor = coordinator.textView else { throw MightyError("블록 출력 뷰가 없습니다.") }
        editor.bottomInset = AgentTranscriptView.cornerHandleBottomInset
        let model = TranscriptDiagnosticBlockModel()
        let window = NSWindow(contentRect: NSRect(x: 170, y: 150, width: 460, height: 420), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = "블록이 줄어도 마지막 줄 검증"
        window.isReleasedWhenClosed = false
        defer { window.orderOut(nil); window.close() }
        var entries = (1...18).map { index in
            LogEntry(id: "block-\(index)", kind: "assistant", text: "블록 응답 \(index): 결과 블록 안에서 여러 줄로 감기는 응답입니다. 마지막 줄은 블록이 줄어도 보여야 합니다.", provider: "claude")
        }
        coordinator.update(entries: entries, provider: "claude", running: true, dark: false, cards: false)
        window.contentView = NSHostingView(rootView: TranscriptDiagnosticBlock(scroll: scroll, model: model))
        window.makeKeyAndOrderFront(nil)
        func settle() async throws {
            window.contentView?.layoutSubtreeIfNeeded()
            scroll.layoutSubtreeIfNeeded()
            try await Task.sleep(for: .milliseconds(80))
            window.contentView?.layoutSubtreeIfNeeded()
        }
        let handle: CGFloat = 24 // 22pt glyph + 2pt padding
        let clip = scroll.contentView
        func lastLineClear() -> Bool {
            guard let manager = editor.layoutManager, manager.numberOfGlyphs > 0 else { return false }
            var line = manager.lineFragmentUsedRect(forGlyphAt: manager.numberOfGlyphs - 1, effectiveRange: nil)
            line.origin.x += editor.textContainerOrigin.x
            line.origin.y += editor.textContainerOrigin.y
            let visible = clip.bounds
            return line.minY >= visible.minY - 0.5 && line.maxY <= visible.maxY - handle + 0.5
        }
        func atTrueBottom() -> Bool {
            abs(clip.bounds.minY - max(0, editor.bounds.height - clip.bounds.height)) < 2
        }
        try await settle()
        let initialHeight = clip.bounds.height
        report["shrinkingBlockStartsAtBottom"] = editor.bounds.height > initialHeight + 200 && atTrueBottom() && lastLineClear()
        guard report["shrinkingBlockStartsAtBottom"] as? Bool == true else { throw MightyError("블록 출력이 처음에 마지막 줄을 보여 주지 않았습니다.") }

        // The request preview reports its real height a turn later: 50pt more.
        model.previewHeight += 50
        try await settle()
        let shrunkHeight = clip.bounds.height
        report["shrinkingBlockStaysAtBottom"] = shrunkHeight < initialHeight - 45 && atTrueBottom() && lastLineClear()
        report["shrinkingBlockGeometry"] = ["viewportBefore": initialHeight, "viewportAfter": shrunkHeight, "originY": clip.bounds.minY, "documentHeight": editor.bounds.height]
        if let screenshotDirectory {
            report["shrinkingBlockScreenshot"] = try capture(window, in: screenshotDirectory, name: "transcript-block-shrunk.png")
        }
        guard report["shrinkingBlockStaysAtBottom"] as? Bool == true else { throw MightyError("블록이 줄어든 뒤 마지막 줄이 가려졌습니다.") }

        // Streaming still follows after a shrink larger than the follow slack.
        entries[entries.count - 1].text += "\n\n스트리밍으로 이어지는 마지막 문단도 손잡이에 가리지 않습니다."
        coordinator.update(entries: entries, provider: "claude", running: true, dark: false, cards: false)
        try await settle()
        report["shrinkingBlockStreamingFollowsToBottom"] = atTrueBottom() && lastLineClear()
        guard report["shrinkingBlockStreamingFollowsToBottom"] as? Bool == true else { throw MightyError("줄어든 블록에서 스트리밍이 마지막 줄을 따라가지 않았습니다.") }

        // A reader who scrolled up keeps their place when the block shrinks.
        // The wheel path itself is covered by the restored-history check.
        editor.cancelInitialScroll()
        clip.scroll(to: NSPoint(x: clip.bounds.minX, y: max(0, editor.bounds.height - clip.bounds.height - 300)))
        scroll.reflectScrolledClipView(clip)
        let readingOrigin = clip.bounds.minY
        guard readingOrigin < editor.bounds.height - clip.bounds.height - 60 else { throw MightyError("블록을 위로 스크롤하지 못했습니다.") }
        model.previewHeight += 50
        try await settle()
        report["shrinkingBlockKeepsReaderPlace"] = abs(clip.bounds.minY - readingOrigin) < 2 && clip.bounds.height < shrunkHeight - 45
        guard report["shrinkingBlockKeepsReaderPlace"] as? Bool == true else { throw MightyError("위로 스크롤한 위치가 블록이 줄 때 바뀌었습니다.") }
    }

    private static func capture(_ window: NSWindow, in directory: URL, name: String) throws -> String {
        guard let content = window.contentView else { throw MightyError("진단 창이 없습니다.") }
        content.layoutSubtreeIfNeeded()
        window.displayIfNeeded()
        guard let bitmap = content.bitmapImageRepForCachingDisplay(in: content.bounds) else { throw MightyError("출력 배치 화면을 캡처하지 못했습니다.") }
        content.cacheDisplay(in: content.bounds, to: bitmap)
        guard let png = bitmap.representation(using: .png, properties: [:]) else { throw MightyError("출력 배치 PNG를 만들지 못했습니다.") }
        let url = directory.appendingPathComponent(name)
        try png.write(to: url)
        return url.path
    }
}
