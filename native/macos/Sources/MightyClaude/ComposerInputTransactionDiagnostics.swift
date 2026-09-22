import AppKit
import SwiftUI

/// Deterministic document/command contracts using the production editor. No
/// keyboard source, clipboard, app activation or real window focus is changed.
@MainActor
enum ComposerInputTransactionDiagnostics {
    static func run() -> [String: Bool] {
        var report: [String: Bool] = [:]
        let fixture = Fixture()
        let editor = fixture.editor
        editor.performInputTransaction {
            editor.setMarkedText("ㅎ", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: 0))
            editor.setMarkedText("한", selectedRange: NSRange(location: 1, length: 0), replacementRange: editor.markedRange())
            report["preeditCallbacksNeverPublishSynchronously"] = fixture.writes.isEmpty && fixture.model.isEmpty
        }
        report["nativeEventEndsBeforeDraftPublication"] = fixture.writes.isEmpty && fixture.pending.count == 1
        fixture.drain()
        report["preeditSnapshotPublishesOnce"] = fixture.writes == ["한"] && fixture.model == "한"
        report["synchronousBindingEchoPreservesMarkedRange"] = editor.hasMarkedText() && editor.markedRange() == NSRange(location: 0, length: 1)
        fixture.writes.removeAll()
        editor.performInputTransaction {
            editor.unmarkText()
            editor.insertText("한", replacementRange: NSRange(location: 0, length: 1))
            editor.setMarkedText("글", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 1, length: 0))
            report["commitAndNextPreeditShareOnePublication"] = fixture.writes.isEmpty
        }
        fixture.drain()
        report["nextNativeSyllableAndMarkedRangePreserved"] = fixture.writes == ["한글"] && editor.string == "한글" && editor.markedRange() == NSRange(location: 1, length: 1)
        editor.prepareForSubmission()
        report["explicitSubmitFlushIncludesFinalSyllable"] = !editor.hasMarkedText() && editor.string == "한글" && fixture.model == "한글"
        fixture.external("")
        report["explicitExternalClearApplies"] = editor.string.isEmpty && fixture.model.isEmpty

        editor.setMarkedText("한", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: 0))
        fixture.drain()
        fixture.external("older")
        report["externalReplacementWaitsForMarkedText"] = editor.hasMarkedText() && editor.string == "한"
        editor.replaceDraft("newer")
        fixture.drain()
        report["newExplicitReplacementSupersedesDeferredModel"] = editor.string == "newer" && fixture.model == "newer" && !editor.hasMarkedText()
        editor.replaceDraft("")
        editor.setMarkedText("한", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: 0))
        fixture.drain(); fixture.external("")
        editor.unmarkText(); fixture.drain()
        report["deferredClearAppliesAfterNativeCommit"] = editor.string.isEmpty && fixture.model.isEmpty

        editor.insertText("a", replacementRange: NSRange(location: 0, length: 0))
        fixture.external("restored")
        fixture.drain()
        report["externalModelInvalidatesOlderQueuedPublication"] = editor.string == "restored" && fixture.model == "restored"
        editor.insertText("b", replacementRange: NSRange(location: (editor.string as NSString).length, length: 0))
        fixture.model = "store changed before SwiftUI update"
        fixture.drain()
        report["unrenderedStoreChangeWinsOverQueuedNativeSnapshot"] = editor.string == fixture.model && fixture.model == "store changed before SwiftUI update"
        fixture.coordinator.receiveModelText("restored")
        report["oldSwiftUIModelSnapshotCannotReplaceCurrentStoreValue"] = editor.string == "store changed before SwiftUI update"
        fixture.model = "another unrendered value"
        editor.replaceDraft("explicit newest command")
        fixture.drain()
        report["explicitReplacementWinsOverUnrenderedStoreChange"] = editor.string == "explicit newest command" && fixture.model == editor.string
        editor.setSelectedRange(NSRange(location: 2, length: 4))
        let selection = editor.selectedRange()
        fixture.coordinator.receiveModelText(fixture.model)
        report["unchangedModelPreservesEditorAndSelection"] = fixture.coordinator.editor === editor && editor.selectedRange() == selection
        editor.replaceDraft("")
        editor.insertText("last native edit", replacementRange: NSRange(location: 0, length: 0))
        let controller = ComposerInputController(); controller.editor = editor; fixture.coordinator.inputController = controller
        fixture.coordinator.detach(); fixture.drain()
        report["dismantleFlushesLastNativeEditAndDetachesController"] = fixture.model == "last native edit" && controller.editor == nil && editor.delegate == nil
        report.merge(commandChecks()) { _, new in new }
        report.merge(standardDocumentChecks()) { _, new in new }
        return report
    }

    private static func commandChecks() -> [String: Bool] {
        let fixture = Fixture()
        let editor = fixture.editor
        let window = FixtureWindow(contentRect: NSRect(x: 0, y: 0, width: 300, height: 100), styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false; window.contentView = editor; window.fixtureResponder = editor
        defer { fixture.coordinator.detach(); window.contentView = nil; window.close() }
        var sends: [String] = []
        var commandFlags: [Bool] = []
        editor.canSubmit = { !fixture.model.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        editor.onSubmit = { command in sends.append(fixture.model); commandFlags.append(command) }
        func event(_ modifiers: NSEvent.ModifierFlags = [], repeating: Bool = false, code: UInt16 = 36) -> NSEvent {
            FixtureEvent(window: window, modifiers: modifiers, repeating: repeating, code: code)
        }
        func interpreted(_ selector: String = "insertNewline:", modifiers: NSEvent.ModifierFlags = [], repeating: Bool = false, code: UInt16 = 36) -> Bool {
            var consumed = false
            editor.performNativeKeyEvent(event(modifiers, repeating: repeating, code: code)) {
                consumed = fixture.coordinator.textView(editor, doCommandBy: NSSelectorFromString(selector))
            }
            return consumed
        }
        var report: [String: Bool] = [:]
        editor.insertText("한", replacementRange: NSRange(location: 0, length: 0))
        report["typingDoesNotPublishBeforeNextRunLoop"] = fixture.model.isEmpty
        let consumed = interpreted()
        report["immediateReturnFlushesDraftBeforeEligibilityAndSubmit"] = consumed && sends == ["한"] && fixture.model == "한"
        fixture.drain()
        report["queuedEchoCannotSubmitAgain"] = sends == ["한"]
        sends.removeAll(); commandFlags.removeAll()
        editor.performNativeKeyEvent(event()) {
            // An IME consumed Return to confirm a candidate and delivered no
            // editing command. The application must not manufacture a send.
        }
        report["imeConsumedReturnDoesNotSubmit"] = sends.isEmpty
        _ = interpreted(modifiers: .command)
        report["commandReturnForwardsSteeringOnce"] = sends == ["한"] && commandFlags == [true]
        sends.removeAll(); commandFlags.removeAll()
        report["nativeNoopForExactCommandReturnSubmitsOnce"] = interpreted("noop:", modifiers: .command) && sends == ["한"] && commandFlags == [true]
        sends.removeAll(); commandFlags.removeAll()
        report["otherNoopCommandsAreNotSubmissions"] = !interpreted("noop:", modifiers: .command, code: 0) && !interpreted("noop:") && sends.isEmpty
        editor.performNativeKeyEvent(event(.command)) { }
        report["imeConsumedCommandReturnDoesNotSubmit"] = sends.isEmpty
        var commitNoopConsumed = true
        editor.performNativeKeyEvent(event(.command)) {
            editor.insertText("한", replacementRange: NSRange(location: 0, length: 1))
            commitNoopConsumed = fixture.coordinator.textView(editor, doCommandBy: NSSelectorFromString("noop:"))
        }
        report["unmarkedNativeCommitThenNoopDoesNotSubmit"] = !commitNoopConsumed && sends.isEmpty && editor.string == "한"
        editor.performNativeKeyEvent(event()) {
            editor.insertText("한", replacementRange: NSRange(location: 0, length: 1))
            _ = fixture.coordinator.textView(editor, doCommandBy: NSSelectorFromString("insertNewline:"))
        }
        report["unmarkedCommitThenReturnSubmitsOnFirstPress"] = sends == ["한"] && fixture.model == "한"
        sends.removeAll(); commandFlags.removeAll()
        editor.performNativeKeyEvent(event(.command)) {
            _ = fixture.coordinator.textView(editor, doCommandBy: NSSelectorFromString("noop:"))
            _ = fixture.coordinator.textView(editor, doCommandBy: NSSelectorFromString("insertNewline:"))
        }
        report["severalNativeCommandsForOneKeySubmitAtMostOnce"] = sends == ["한"]
        sends.removeAll(); commandFlags.removeAll()
        _ = interpreted(modifiers: .numericPad, code: 76)
        report["keypadReturnSubmitsOnce"] = sends == ["한"]
        sends.removeAll()
        report["repeatedReturnIsConsumedWithoutSending"] = interpreted(repeating: true) && sends.isEmpty
        report["shiftReturnRemainsNative"] = !interpreted(modifiers: .shift) && sends.isEmpty
        report["optionReturnRemainsNative"] = !interpreted(modifiers: .option) && sends.isEmpty
        editor.canSubmit = { false }
        report["disabledReturnIsConsumedWithoutSending"] = interpreted() && sends.isEmpty
        editor.canSubmit = { true }
        editor.setMarkedText("글", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 1, length: 0))
        report["markedReturnStaysWithNativeInputMethod"] = !interpreted() && sends.isEmpty && editor.hasMarkedText()
        report["markedCommandReturnNoopStaysWithNativeInputMethod"] = !interpreted("noop:", modifiers: .command) && sends.isEmpty && editor.hasMarkedText()
        var commitKeyConsumed = true
        editor.performNativeKeyEvent(event()) {
            editor.unmarkText()
            commitKeyConsumed = fixture.coordinator.textView(editor, doCommandBy: NSSelectorFromString("insertNewline:"))
        }
        report["nativeReturnCommandAfterCommitSubmitsOnFirstPress"] = commitKeyConsumed && sends == ["한글"] && !editor.hasMarkedText()
        sends.removeAll(); commandFlags.removeAll()
        editor.replaceDraft("한")
        editor.setMarkedText("글", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 1, length: 0))
        editor.performNativeKeyEvent(event()) {
            editor.insertText("글", replacementRange: editor.markedRange())
            _ = fixture.coordinator.textView(editor, doCommandBy: NSSelectorFromString("insertNewline:"))
            _ = fixture.coordinator.textView(editor, doCommandBy: NSSelectorFromString("insertNewline:"))
        }
        report["nativeCommitThenReturnIncludesFinalSyllableExactlyOnce"] = sends == ["한글"] && fixture.model == "한글"
        sends.removeAll(); commandFlags.removeAll()
        editor.replaceDraft("한")
        editor.setMarkedText("글", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 1, length: 0))
        editor.performNativeKeyEvent(event()) {
            editor.insertText("글", replacementRange: editor.markedRange())
        }
        report["commitOnlyReturnDoesNotManufactureSubmit"] = sends.isEmpty && editor.string == "한글"
        editor.replaceDraft("한")
        editor.setMarkedText("글", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 1, length: 0))
        editor.performNativeKeyEvent(event(.command)) {
            editor.unmarkText()
            _ = fixture.coordinator.textView(editor, doCommandBy: NSSelectorFromString("noop:"))
        }
        report["markedCommitThenCommandNoopDoesNotSubmit"] = sends.isEmpty
        editor.replaceDraft("한")
        editor.setMarkedText("글", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 1, length: 0))
        editor.performNativeKeyEvent(event(.command)) {
            editor.insertText("글", replacementRange: editor.markedRange())
            _ = fixture.coordinator.textView(editor, doCommandBy: NSSelectorFromString("insertNewline:"))
        }
        report["explicitCommandReturnAfterCommitSteersExactlyOnce"] = sends == ["한글"] && commandFlags == [true]
        sends.removeAll(); commandFlags.removeAll()
        editor.replaceDraft("한")
        editor.setMarkedText("글", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 1, length: 0))
        var shiftConsumed = true
        editor.performNativeKeyEvent(event(.shift)) {
            editor.insertText("글", replacementRange: editor.markedRange())
            shiftConsumed = fixture.coordinator.textView(editor, doCommandBy: NSSelectorFromString("insertNewline:"))
            if !shiftConsumed { editor.insertText("\n", replacementRange: NSRange(location: 2, length: 0)) }
        }
        report["shiftReturnAfterCommitRemainsNativeNewline"] = !shiftConsumed && sends.isEmpty && editor.string == "한글\n"
        editor.replaceDraft("한")
        editor.setMarkedText("글", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 1, length: 0))
        editor.performNativeKeyEvent(event(repeating: true)) {
            editor.insertText("글", replacementRange: editor.markedRange())
            _ = fixture.coordinator.textView(editor, doCommandBy: NSSelectorFromString("insertNewline:"))
        }
        report["repeatedCommitReturnDoesNotSend"] = sends.isEmpty
        let otherResponder = NSResponder(); window.fixtureResponder = otherResponder
        report["anotherResponderPreventsComposerCommands"] = !interpreted() && sends.isEmpty
        window.fixtureResponder = editor
        var navigated: [ComposerNavigationKey] = []
        editor.onNavigationKey = { key in navigated.append(key); return true }
        report["paletteReceivesNativeArrowCommand"] = interpreted("moveUp:", code: 126) && navigated == [.up]
        navigated.removeAll()
        report["paletteReceivesNativeTabCommand"] = interpreted("insertTab:", code: 48) && navigated == [.select]
        navigated.removeAll()
        report["paletteReturnSelectsWithoutSubmitting"] = interpreted() && navigated == [.select] && sends.isEmpty
        navigated.removeAll()
        editor.replaceDraft("한")
        editor.setMarkedText("글", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 1, length: 0))
        editor.performNativeKeyEvent(event()) {
            editor.insertText("글", replacementRange: editor.markedRange())
            _ = fixture.coordinator.textView(editor, doCommandBy: NSSelectorFromString("insertNewline:"))
            _ = fixture.coordinator.textView(editor, doCommandBy: NSSelectorFromString("insertNewline:"))
        }
        report["postCommitPaletteReturnSelectsOnceWithoutSubmitting"] = navigated == [.select] && sends.isEmpty && fixture.model == "한글"
        navigated.removeAll()
        editor.replaceDraft("한")
        editor.setMarkedText("글", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 1, length: 0))
        var nativeTabConsumed = true
        editor.performNativeKeyEvent(event(code: 48)) {
            editor.insertText("글", replacementRange: editor.markedRange())
            nativeTabConsumed = fixture.coordinator.textView(editor, doCommandBy: NSSelectorFromString("insertTab:"))
        }
        report["postCommitNativeCandidateTabDoesNotSelectPalette"] = !nativeTabConsumed && navigated.isEmpty && sends.isEmpty
        navigated.removeAll()
        report["paletteEscapeUsesNativeCancelCommand"] = interpreted("cancelOperation:", code: 53) && navigated == [.dismiss]
        report["nonKeyboardNewlineIsNotASubmit"] = !fixture.coordinator.textView(editor, doCommandBy: NSSelectorFromString("insertNewline:"))
        // Exercise NSTextInputClient -> NSTextView delegate dispatch itself,
        // rather than calling our coordinator directly.
        editor.onNavigationKey = nil
        let standardLineBreak = NSTextView(frame: editor.frame)
        standardLineBreak.string = "한글"
        standardLineBreak.setSelectedRange(NSRange(location: 2, length: 0))
        standardLineBreak.doCommand(by: NSSelectorFromString("insertLineBreak:"))
        let nativeLineBreakResult = standardLineBreak.string
        editor.replaceDraft("한글"); sends.removeAll()
        editor.performNativeKeyEvent(event()) {
            editor.doCommand(by: NSSelectorFromString("insertLineBreak:"))
        }
        report["nativePlainReturnLineBreakCommandSubmitsOnce"] = sends == ["한글"] && editor.string == "한글"
        editor.replaceDraft("한글"); sends.removeAll()
        editor.performNativeKeyEvent(event(.shift)) {
            editor.doCommand(by: NSSelectorFromString("insertLineBreak:"))
        }
        report["nativeShiftReturnLineBreakCommandKeepsNewline"] = sends.isEmpty && editor.string == nativeLineBreakResult
        editor.replaceDraft("한글"); sends.removeAll()
        editor.performNativeKeyEvent(event(.option)) {
            editor.doCommand(by: NSSelectorFromString("insertLineBreak:"))
        }
        report["nativeOptionReturnLineBreakCommandKeepsNewline"] = sends.isEmpty && editor.string == nativeLineBreakResult
        return report
    }

    private static func standardDocumentChecks() -> [String: Bool] {
        let fixture = Fixture()
        let standard = NSTextView(frame: NSRect(x: 0, y: 0, width: 300, height: 100))
        var report: [String: Bool] = [:]
        report["explicitClickIntoInactiveComposerIsAccepted"] = fixture.editor.acceptsFirstMouse(for: nil)
        fixture.editor.isEditable = false
        report["readOnlyComposerRejectsInactiveClick"] = !fixture.editor.acceptsFirstMouse(for: nil)
        fixture.editor.isEditable = true; fixture.editor.isHidden = true
        report["hiddenComposerRejectsInactiveClick"] = !fixture.editor.acceptsFirstMouse(for: nil)
        fixture.editor.isHidden = false
        for editor in [fixture.editor as NSTextView, standard] {
            editor.setMarkedText("ㅎ", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: 0))
            editor.setMarkedText("한", selectedRange: NSRange(location: 1, length: 0), replacementRange: editor.markedRange())
        }
        report["nativePreeditMatchesStandardTextView"] = fixture.editor.string == standard.string && fixture.editor.markedRange() == standard.markedRange() && fixture.editor.selectedRange() == standard.selectedRange()
        for editor in [fixture.editor as NSTextView, standard] {
            editor.insertText("한", replacementRange: editor.markedRange())
            editor.insertText("글", replacementRange: NSRange(location: 1, length: 0))
            editor.setSelectedRange(NSRange(location: 1, length: 1))
            editor.insertText("국어", replacementRange: NSRange(location: NSNotFound, length: 0))
            editor.deleteBackward(nil)
        }
        report["replacementSelectionAndDeleteMatchStandardTextView"] = fixture.editor.string == standard.string && fixture.editor.selectedRange() == standard.selectedRange() && fixture.editor.hasMarkedText() == standard.hasMarkedText()
        fixture.drain()
        report["nativeDocumentPublishesWithoutTransformingText"] = fixture.model == standard.string
        fixture.coordinator.detach()
        return report
    }

    @MainActor
    private final class Fixture {
        let editor = ComposerTextView(frame: NSRect(x: 0, y: 0, width: 500, height: 100))
        var model = ""
        var writes: [String] = []
        var pending: [() -> Void] = []
        var coordinator: NativeComposerEditor.Coordinator!
        init() {
            editor.isRichText = false
            coordinator = NativeComposerEditor.Coordinator(text: Binding(get: { [weak self] in self?.model ?? "" }, set: { [weak self] value in
                guard let self else { return }
                self.model = value; self.writes.append(value)
                self.coordinator.receiveModelText(value)
            }), enqueue: { [weak self] action in self?.pending.append(action) })
            coordinator.attach(editor)
        }
        func external(_ value: String) { model = value; coordinator.receiveModelText(value) }
        func drain() {
            var passes = 0
            while !pending.isEmpty, passes < 10 {
                let work = pending; pending.removeAll(); work.forEach { $0() }; passes += 1
            }
        }
    }
    private final class FixtureEvent: NSEvent {
        private let owner: NSWindow
        private let ownerNumber: Int
        private let flags: NSEvent.ModifierFlags
        private let repeating: Bool
        private let code: UInt16
        @MainActor init(window: NSWindow, modifiers: NSEvent.ModifierFlags, repeating: Bool, code: UInt16) {
            owner = window; ownerNumber = window.windowNumber; flags = modifiers; self.repeating = repeating; self.code = code
            super.init()
        }
        required init?(coder: NSCoder) { fatalError("unused") }
        override var window: NSWindow? { owner }
        override var windowNumber: Int { ownerNumber }
        override var type: NSEvent.EventType { .keyDown }
        override var modifierFlags: NSEvent.ModifierFlags { flags }
        override var isARepeat: Bool { repeating }
        override var keyCode: UInt16 { code }
        override var characters: String? { "\r" }
        override var charactersIgnoringModifiers: String? { "\r" }
        override var timestamp: TimeInterval { 0 }
    }
    private final class FixtureWindow: NSWindow {
        var fixtureResponder: NSResponder?
        override var firstResponder: NSResponder? { fixtureResponder }
        override var isVisible: Bool { true }
    }
}
