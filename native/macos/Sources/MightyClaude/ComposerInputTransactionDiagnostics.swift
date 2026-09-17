import AppKit
import SwiftUI

/// Exercises the production NSTextView/coordinator without selecting an input
/// source, synthesizing real keyboard input, submitting a prompt, or opening a
/// window. The scripted callbacks represent one IME commit/preedit transition.
@MainActor
enum ComposerInputTransactionDiagnostics {
    private final class CoordinatorReference {
        weak var value: NativeComposerEditor.Coordinator?
    }

    static func run() -> [String: Bool] {
        let storage = NSTextStorage()
        let layout = NSLayoutManager()
        let container = NSTextContainer(size: NSSize(width: 500, height: 200))
        storage.addLayoutManager(layout); layout.addTextContainer(container)
        let editor = ComposerTextView(frame: NSRect(x: 0, y: 0, width: 500, height: 200), textContainer: container)
        editor.isRichText = false
        var model = ""
        var writes: [String] = []
        let coordinatorReference = CoordinatorReference()
        let binding = Binding<String>(get: { model }, set: { value in
            model = value; writes.append(value)
            // A synchronous binding echo must never replace native marked text.
            coordinatorReference.value?.receiveModelText(value)
        })
        let coordinator = NativeComposerEditor.Coordinator(text: binding)
        coordinatorReference.value = coordinator
        coordinator.editor = editor; editor.delegate = coordinator
        var finished = 0
        editor.onInputFinished = {
            finished += 1
            coordinator.publishNativeText()
        }
        defer { editor.delegate = nil; editor.onInputFinished = nil }
        var report: [String: Bool] = [:]
        editor.performInputTransaction {
            editor.setMarkedText("ㅎ", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: 0))
            editor.setMarkedText("하", selectedRange: NSRange(location: 1, length: 0), replacementRange: editor.markedRange())
            editor.setMarkedText("한", selectedRange: NSRange(location: 1, length: 0), replacementRange: editor.markedRange())
            report["intermediatePreeditNeverPublishes"] = writes.isEmpty && finished == 0 && model.isEmpty
        }
        report["outerEventPublishesOnce"] = finished == 1 && writes == ["한"] && model == "한"
        report["synchronousEchoKeepsMarkedRange"] = editor.hasMarkedText() && editor.markedRange() == NSRange(location: 0, length: 1)
        finished = 0; writes.removeAll()
        editor.performInputTransaction {
            editor.unmarkText()
            editor.insertText("한", replacementRange: NSRange(location: 0, length: 1))
            report["commitBeforeNextPreeditNeverPublishes"] = finished == 0 && writes.isEmpty
            editor.setMarkedText("글", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 1, length: 0))
        }
        report["nextSyllableStaysComposed"] = finished == 1 && writes == ["한글"] && editor.string == "한글" && editor.hasMarkedText()
        finished = 0; writes.removeAll()
        editor.prepareForSubmission()
        report["submissionCommitsOnceAndKeepsFinalSyllable"] = finished == 1 && editor.string == "한글" && model == "한글" && !editor.hasMarkedText()
        model = ""
        coordinator.receiveModelText("")
        report["externalClearAfterInputStillApplies"] = editor.string.isEmpty && !editor.hasMarkedText()
        return report
    }
}
