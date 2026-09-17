import AppKit
import MightyCore
import SwiftUI

/// Isolated smoke only. Uses the real native composer/probe and SessionPaneView;
/// never submits a prompt, changes provider settings, or reads the clipboard.
@MainActor
enum ComposerPresentationDiagnostics {
    static func run(store: AppStore) async -> [String: Any] {
        var report: [String: Any] = ["passed": false]
        let previousWindow = NSApp.keyWindow
        let window = NSWindow(contentRect: NSRect(x: 150, y: 140, width: 540, height: 510), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.title = "입력창 표시 검증"
        window.isReleasedWhenClosed = false
        defer { window.orderOut(nil); window.close(); previousWindow?.makeKeyAndOrderFront(nil) }
        do {
            let inputTransaction = ComposerInputTransactionDiagnostics.run()
            for (key, passed) in inputTransaction { report[key] = passed }
            guard inputTransaction.values.allSatisfy({ $0 }) else { throw MightyError("IME 입력 transaction 회귀 검증에 실패했습니다.") }
            let model = FixtureModel()
            let host = NSHostingView(rootView: PlaceholderFixture(model: model))
            window.contentView = host
            window.makeKeyAndOrderFront(nil)
            try await Task.sleep(for: .milliseconds(150))
            func probes(_ view: NSView) -> [TextEditorHeightReader.HeightProbe] {
                (view as? TextEditorHeightReader.HeightProbe).map { [$0] } ?? view.subviews.flatMap(probes)
            }
            guard let probe = probes(host).first, let editor = probe.editor, let label = probe.placeholderLabel,
                  window.makeFirstResponder(editor) else { throw MightyError("입력 안내 검증에 사용할 편집기를 찾지 못했습니다.") }
            let delegate = editor.delegate
            report["emptyPlaceholderVisible"] = !label.isHidden
            editor.insertText("a", replacementRange: NSRange(location: 0, length: 0))
            report["typingHidesImmediately"] = label.isHidden && editor.string == "a"
            editor.insertText("", replacementRange: NSRange(location: 0, length: (editor.string as NSString).length))
            try await Task.sleep(for: .milliseconds(30))
            report["clearRestoresPlaceholder"] = !label.isHidden && editor.string.isEmpty
            editor.setMarkedText("ㅎ", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: 0))
            report["markedTextHidesImmediately"] = editor.hasMarkedText() && label.isHidden
            editor.setMarkedText("한", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: (editor.string as NSString).length))
            report["compositionKeepsPlaceholderHidden"] = editor.hasMarkedText() && label.isHidden
            editor.unmarkText()
            report["commitKeepsPlaceholderHidden"] = !editor.hasMarkedText() && label.isHidden
            editor.insertText("", replacementRange: NSRange(location: 0, length: (editor.string as NSString).length))
            try await Task.sleep(for: .milliseconds(40))
            report["deleteAfterCompositionRestoresPlaceholder"] = editor.string.isEmpty && !label.isHidden
            report["sameNativeEditorAndDelegate"] = probe.editor === editor && editor.delegate === delegate && label.superview === editor
            report["focusPreserved"] = window.firstResponder === editor
            report["placeholderHitTestPassesThrough"] = label.hitTest(NSPoint(x: 2, y: 2)) == nil
            report["placeholderAlignedToText"] = abs(label.frame.minX - editor.textContainerOrigin.x - (editor.textContainer?.lineFragmentPadding ?? 0)) < 1

            // Exercise the actual AppStore-backed pane: a native click must
            // focus its empty editor before the first character, without a
            // leading space or a programmatic makeFirstResponder shortcut.
            guard !store.hasModal, let inputSession = store.activeSessions.first(where: { $0.kind != "shell" }) else { throw MightyError("첫 입력 검증에 사용할 모달 없는 세션이 없습니다.") }
            let previousDraft = store.drafts[inputSession.id]
            let previousActive = store.snapshot.activeSessionId
            defer {
                store.drafts[inputSession.id] = previousDraft
                if let previousActive { store.selectSession(previousActive) }
            }
            store.drafts[inputSession.id] = ""
            if let other = store.activeSessions.first(where: { $0.id != inputSession.id }) { store.selectSession(other.id) }
            let inputHost = NSHostingView(rootView: StoreComposerFixture(store: store, sessionID: inputSession.id))
            window.contentView = inputHost
            try await Task.sleep(for: .milliseconds(150))
            guard let inputProbe = probes(inputHost).first, let inputEditor = inputProbe.editor else { throw MightyError("빈 입력창의 네이티브 편집기를 찾지 못했습니다.") }
            let inputDelegate = inputEditor.delegate
            report["stableNativeComposer"] = inputEditor is ComposerTextView
            window.makeFirstResponder(nil)
            let point = inputEditor.convert(NSPoint(x: inputEditor.textContainerOrigin.x + (inputEditor.textContainer?.lineFragmentPadding ?? 0) + 3, y: inputEditor.textContainerOrigin.y + 8), to: nil)
            func mouse(_ type: NSEvent.EventType) throws -> NSEvent {
                guard let event = NSEvent.mouseEvent(with: type, location: point, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil, eventNumber: 1, clickCount: 1, pressure: type == .leftMouseDown ? 1 : 0) else { throw MightyError("입력 포커스 검증 이벤트를 만들지 못했습니다.") }
                return event
            }
            NSApp.postEvent(try mouse(.leftMouseUp), atStart: false)
            window.sendEvent(try mouse(.leftMouseDown))
            report["emptyComposerClickFocusesEditor"] = window.firstResponder === inputEditor
            guard let key = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil, characters: "a", charactersIgnoringModifiers: "a", isARepeat: false, keyCode: 0) else { throw MightyError("첫 글자 검증 이벤트를 만들지 못했습니다.") }
            report["firstCharacterPassesKeyMonitor"] = inputProbe.handleKeyEvent(key) != nil
            window.sendEvent(key)
            try await Task.sleep(for: .milliseconds(120))
            report["firstKeyNativeText"] = inputEditor.string
            report["firstKeyWithoutSpace"] = !inputEditor.string.isEmpty && !inputEditor.string.hasPrefix(" ") && window.firstResponder === inputEditor
            report["firstKeyScreenshot"] = try store.captureSmokeWindow(window, filename: "composer-first-key.png").path
            inputEditor.unmarkText()
            try await Task.sleep(for: .milliseconds(60))
            report["firstKeyBindingRetained"] = store.drafts[inputSession.id] == inputEditor.string && !inputEditor.string.isEmpty
            inputEditor.insertText("", replacementRange: NSRange(location: 0, length: (inputEditor.string as NSString).length))
            try await Task.sleep(for: .milliseconds(40))
            inputEditor.setMarkedText("ㅎ", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: 0))
            report["firstKoreanMarkedImmediately"] = inputEditor.hasMarkedText() && inputEditor.string == "ㅎ"
            let markedRange = inputEditor.markedRange()
            let markedSelection = inputEditor.selectedRange()
            store.objectWillChange.send()
            try await Task.sleep(for: .milliseconds(120))
            report["firstKoreanMarkedAfterRender"] = inputEditor.hasMarkedText() && inputEditor.string == "ㅎ"
            report["firstKoreanRangeAfterRender"] = inputEditor.markedRange() == markedRange && inputEditor.selectedRange() == markedSelection
            report["markedDraftIncludesVisibleText"] = store.drafts[inputSession.id] == "ㅎ"
            report["markedFirstSyllableEnablesSend"] = accessibilityEnabled(window, identifier: "send-\(inputSession.id)") == true
            report["firstKoreanScreenshot"] = try store.captureSmokeWindow(window, filename: "composer-first-korean.png").path
            inputEditor.setMarkedText("한", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: (inputEditor.string as NSString).length))
            store.objectWillChange.send()
            try await Task.sleep(for: .milliseconds(40))
            report["secondKoreanMarkAfterRender"] = inputEditor.hasMarkedText() && inputEditor.string == "한"
            inputEditor.insertText("한", replacementRange: inputEditor.markedRange())
            try await Task.sleep(for: .milliseconds(100))
            report["firstKoreanCommitRetained"] = inputEditor.string == "한" && store.drafts[inputSession.id] == "한" && !inputEditor.hasMarkedText()
            inputEditor.setMarkedText("글", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: (inputEditor.string as NSString).length, length: 0))
            store.objectWillChange.send()
            try await Task.sleep(for: .milliseconds(60))
            report["lastMarkedSyllableIncluded"] = inputEditor.string == "한글" && store.drafts[inputSession.id] == "한글" && inputEditor.hasMarkedText()
            (inputEditor as? ComposerTextView)?.prepareForSubmission()
            report["submissionPreparationKeepsLastSyllable"] = inputEditor.string == "한글" && store.drafts[inputSession.id] == "한글" && !inputEditor.hasMarkedText() && window.firstResponder === inputEditor
            store.drafts[inputSession.id] = ""
            try await Task.sleep(for: .milliseconds(80))
            report["externalClearAfterCommit"] = inputEditor.string.isEmpty && inputProbe.placeholderLabel?.isHidden == false
            store.drafts[inputSession.id] = "복원한 초안"
            try await Task.sleep(for: .milliseconds(80))
            report["externalDraftRestore"] = inputEditor.string == "복원한 초안" && !inputEditor.hasMarkedText()
            inputEditor.setSelectedRange(NSRange(location: 2, length: 1))
            store.objectWillChange.send()
            try await Task.sleep(for: .milliseconds(60))
            report["unrelatedUpdatePreservesSelection"] = inputEditor.selectedRange() == NSRange(location: 2, length: 1)

            let pasteboard = NSPasteboard(name: NSPasteboard.Name("dev.mightyclaude.first-input.\(UUID().uuidString)"))
            defer { pasteboard.clearContents() }
            pasteboard.setString("일반 텍스트 붙여넣기", forType: .string)
            inputEditor.setSelectedRange(NSRange(location: 0, length: (inputEditor.string as NSString).length))
            let pasted = inputEditor.readSelection(from: pasteboard)
            try await Task.sleep(for: .milliseconds(60))
            report["nativeTextPastePreserved"] = pasted && inputEditor.string == "일반 텍스트 붙여넣기" && store.drafts[inputSession.id] == inputEditor.string
            inputEditor.setMarkedText("ㅎ", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: (inputEditor.string as NSString).length))
            store.drafts[inputSession.id] = ""
            try await Task.sleep(for: .milliseconds(60))
            report["externalClearWaitsForComposition"] = inputEditor.hasMarkedText() && inputEditor.string == "ㅎ"
            inputEditor.insertText("한", replacementRange: inputEditor.markedRange())
            try await Task.sleep(for: .milliseconds(60))
            report["deferredExternalClearApplied"] = inputEditor.string.isEmpty && store.drafts[inputSession.id] == "" && !inputEditor.hasMarkedText()
            report["firstInputEditorRetained"] = inputProbe.editor === inputEditor && inputEditor.delegate === inputDelegate && inputEditor.window === window && window.firstResponder === inputEditor

            let fixtureId = "composer-geometry-fixture"
            let previousFixtureDraft = store.drafts[fixtureId]
            let previousFixtureAttachments = store.attachmentDrafts[fixtureId]
            defer {
                store.drafts[fixtureId] = previousFixtureDraft
                store.attachmentDrafts[fixtureId] = previousFixtureAttachments
            }
            let nextDraft = "다음 요청 초안"
            let nextAttachment = RunAttachment(id: "composer-next-attachment", name: "notes.txt", mediaType: "text/plain", dataBase64: Data("next request".utf8).base64EncodedString())
            store.drafts[fixtureId] = nextDraft
            store.attachmentDrafts[fixtureId] = [nextAttachment]
            let actionModel = PrimaryActionFixtureModel(RunSession(id: fixtureId, workspaceId: store.activeWorkspace?.id ?? "composer-fixture", title: "입력창 검증", provider: "codex", model: "long-model-name-for-responsive-toolbar", status: "running", resumeId: "fixture-resume"))
            let actionHost = NSHostingView(rootView: PrimaryActionFixture(model: actionModel, store: store))
            window.contentView = actionHost
            let controls = ["attach-", "composer-model-", "composer-effort-", "composer-permission-", "composer-fast-", "composer-more-", "composer-options-", "composer-stop-", "send-"].map { $0 + fixtureId }
            var rows: [[String: Any]] = []
            var allRowsPass = true
            var samePrimaryPosition = true
            for width in [CGFloat(920), 540, 315] {
                window.setContentSize(NSSize(width: width, height: 510))
                var primaryFrame: NSRect?
                for status in ["running", "completed", "running"] {
                    actionModel.session.status = status
                    try await Task.sleep(for: .milliseconds(120))
                    window.contentView?.layoutSubtreeIfNeeded()
                    let nodes = accessibilityFrames(window)
                    let frames = controls.compactMap { id -> (String, NSRect)? in nodes[id].map { (id, $0) } }
                    let centerSpread = (frames.map { $0.1.midY }.max() ?? 0) - (frames.map { $0.1.midY }.min() ?? 0)
                    let heights = frames.map { $0.1.height }
                    let content = window.convertToScreen(window.contentView?.convert(window.contentView?.bounds ?? .zero, to: nil) ?? .zero)
                    let primary = (status == "running" ? "composer-stop-" : "send-") + fixtureId
                    let absent = (status == "running" ? "send-" : "composer-stop-") + fixtureId
                    let required = ["attach-" + fixtureId, "composer-model-" + fixtureId, primary].allSatisfy { nodes[$0] != nil } && nodes[absent] == nil
                    let hasSettings = nodes["composer-more-" + fixtureId] != nil || nodes["composer-options-" + fixtureId] != nil
                    let inside = frames.allSatisfy { $0.1.minX >= content.minX - 1 && $0.1.maxX <= content.maxX + 1 }
                    let rowPass = required && hasSettings && centerSpread <= 2 && heights.allSatisfy { abs($0 - 32) <= 2 } && inside
                    if let frame = nodes[primary] {
                        if let prior = primaryFrame { samePrimaryPosition = samePrimaryPosition && abs(prior.minX - frame.minX) < 1 && abs(prior.midY - frame.midY) < 1 }
                        primaryFrame = frame
                    } else { samePrimaryPosition = false }
                    allRowsPass = allRowsPass && rowPass
                    rows.append(["width": width, "status": status, "passed": rowPass, "centerSpread": centerSpread, "heights": heights, "insideWindow": inside,
                                 "controls": frames.map { ["id": $0.0, "frame": NSStringFromRect($0.1)] }])
                }
            }
            guard let actionProbe = probes(actionHost).first, let actionEditor = actionProbe.editor,
                  window.makeFirstResponder(actionEditor),
                  let enter = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber, context: nil, characters: "\r", charactersIgnoringModifiers: "\r", isARepeat: false, keyCode: 36) else { throw MightyError("실행 중 입력창을 찾지 못했습니다.") }
            report["runningEnterConsumedWithoutStopOrSubmit"] = actionProbe.handleKeyEvent(enter) == nil && actionEditor.string == nextDraft
            report["toolbarRows"] = rows
            report["singleAlignedControlRow"] = allRowsPass
            report["onePrimaryButtonChangesInPlace"] = samePrimaryPosition && allRowsPass
            report["nextDraftAndAttachmentSurviveStateChanges"] = store.drafts[fixtureId] == nextDraft && store.attachmentDrafts[fixtureId] == [nextAttachment]
            report["primaryActionScreenshot"] = try store.captureSmokeWindow(window, filename: "composer-single-stop.png").path
            guard report.values.compactMap({ $0 as? Bool }).filter({ !$0 }).count == 1 else { throw MightyError("입력 안내 또는 하단 컨트롤 배치 검증에 실패했습니다.") }
            report["passed"] = true
        } catch { report["error"] = error.localizedDescription }
        return report
    }

    private static func accessibilityEnabled(_ element: Any, identifier: String, depth: Int = 0) -> Bool? {
        guard depth < 50 else { return nil }
        let children: [Any]
        if let node = element as? any NSAccessibilityProtocol {
            if node.accessibilityIdentifier() == identifier { return node.isAccessibilityEnabled() }
            children = node.accessibilityChildren() ?? []
        } else if let node = element as? NSObject {
            if node.responds(to: NSSelectorFromString("accessibilityIdentifier")), node.value(forKey: "accessibilityIdentifier") as? String == identifier,
               node.responds(to: NSSelectorFromString("isAccessibilityEnabled")) { return node.value(forKey: "accessibilityEnabled") as? Bool }
            children = node.responds(to: NSSelectorFromString("accessibilityChildren")) ? node.value(forKey: "accessibilityChildren") as? [Any] ?? [] : []
        } else { children = [] }
        for child in children {
            if let enabled = accessibilityEnabled(child, identifier: identifier, depth: depth + 1) { return enabled }
        }
        return nil
    }

    private static func accessibilityFrames(_ element: Any, depth: Int = 0) -> [String: NSRect] {
        guard depth < 50 else { return [:] }
        var result: [String: NSRect] = [:]
        var children: [Any] = []
        if let node = element as? any NSAccessibilityProtocol {
            if let id = node.accessibilityIdentifier(), !id.isEmpty {
                let frame = node.accessibilityFrame()
                if frame.width > 0, frame.height > 0 { result[id] = frame }
            }
            children = node.accessibilityChildren() ?? []
        } else if let node = element as? NSObject {
            if node.responds(to: NSSelectorFromString("accessibilityIdentifier")), let id = node.value(forKey: "accessibilityIdentifier") as? String,
               node.responds(to: NSSelectorFromString("accessibilityFrame")), let frame = (node.value(forKey: "accessibilityFrame") as? NSValue)?.rectValue,
               !id.isEmpty, frame.width > 0, frame.height > 0 { result[id] = frame }
            if node.responds(to: NSSelectorFromString("accessibilityChildren")) { children = node.value(forKey: "accessibilityChildren") as? [Any] ?? [] }
        }
        for child in children { result.merge(accessibilityFrames(child, depth: depth + 1)) { first, _ in first } }
        return result
    }

    private final class PrimaryActionFixtureModel: ObservableObject {
        @Published var session: RunSession
        init(_ session: RunSession) { self.session = session }
    }
    private struct PrimaryActionFixture: View {
        @ObservedObject var model: PrimaryActionFixtureModel
        @ObservedObject var store: AppStore
        var body: some View { SessionPaneView(session: model.session).environmentObject(store).id(model.session.id) }
    }
    private final class FixtureModel: ObservableObject { @Published var text = "" }
    private struct StoreComposerFixture: View {
        @ObservedObject var store: AppStore
        let sessionID: String
        var body: some View {
            if let session = store.snapshot.sessions.first(where: { $0.id == sessionID }) { SessionPaneView(session: session).environmentObject(store).id(session.id) }
        }
    }
    private struct PlaceholderFixture: View {
        @ObservedObject var model: FixtureModel
        @ViewState private var height: CGFloat = 22
        var body: some View {
            VStack {
                NativeComposerEditor(text: $model.text).frame(height: height)
                    .background(TextEditorHeightReader(text: model.text, height: $height, canSubmit: false, onSubmit: {}, placeholder: "요청할 작업을 입력하세요…"))
                Spacer()
            }.padding(20)
        }
    }
}
