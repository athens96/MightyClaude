import AppKit

/// Uses the production coordinator with a simulated app/key-window state and
/// event clock. Windows stay offscreen; no real activation, input-source change,
/// event replay, draft or clipboard is involved.
@MainActor
enum InputSessionRecoveryDiagnostics {
    static func run() -> [String: Bool] {
        var report: [String: Bool] = [:]
        func check(_ name: String, _ body: (Fixture) -> Bool) {
            let fixture = Fixture()
            defer { fixture.dispose() }
            report[name] = body(fixture)
        }
        check("missingKeyWindowResolvesLastVisibleNativeResponder") { f in
            f.keyWindow == nil && f.coordinator.resolveEditor() === f.editor
        }
        check("activationRequestDoesNotCommitPreeditOrResetResponder") { f in
            f.editor.setMarkedText("한", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: 0))
            let selection = f.editor.selectedRanges
            f.start(); f.tick(count: 4)
            return f.activations == 1 && f.commits == 0 && f.responderCalls.isEmpty && f.editor.hasMarkedText()
                && f.editor.string == "한" && f.editor.selectedRanges == selection && f.coordinator.state == .activating
        }
        check("windowKeyRequestWaitsForActualAppActivationAndRunsOnce") { f in
            f.start(); f.tick(count: 2)
            let before = f.keyRequests
            f.active = true; f.coordinator.activationStateDidChange(); f.tick(count: 4)
            return before == 0 && f.keyRequests == 1 && f.commits == 0 && f.responderCalls.isEmpty
        }
        check("actualActivationCommitsPreeditOnceAndPreservesDraftAndSelection") { f in
            f.editor.setMarkedText("한", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: 0))
            f.start(); f.activateWindow()
            let waiting = f.coordinator.state == .verifying && f.commits == 1 && !f.editor.hasMarkedText()
            let selection = f.editor.selectedRanges
            f.contextCurrent = true; f.tick()
            return waiting && f.coordinator.state == .reconnected && f.editor.string == "한"
                && f.editor.selectedRanges == selection && f.responderCalls == ["release", "focus"]
        }
        check("nonemptySelectionSurvivesExplicitReconnection") { f in
            f.editor.string = "abc한글def"; f.editor.setSelectedRange(NSRange(location: 2, length: 4))
            f.start(); f.activateWindow(); f.contextCurrent = true; f.tick()
            return f.editor.string == "abc한글def" && f.editor.selectedRange() == NSRange(location: 2, length: 4)
        }
        check("activationTimeoutReportsFailureWithoutInputMutation") { f in
            f.start(); f.tick(count: 25)
            return f.coordinator.state == .failed(.activationUnavailable) && f.activations == 1 && f.commits == 0 && f.responderCalls.isEmpty
        }
        check("keyWindowTimeoutDoesNotResetResponder") { f in
            f.active = true; f.start(); f.tick(count: 25)
            return f.coordinator.state == .failed(.windowUnavailable) && f.keyRequests == 1 && f.responderCalls.isEmpty
        }
        check("inputContextTimeoutNeverClaimsSuccessOrRepeatsRebind") { f in
            f.start(); f.activateWindow(); f.tick(count: 25)
            return f.coordinator.state == .failed(.inputContextUnavailable) && f.commits == 1 && f.responderCalls == ["release", "focus"]
        }
        check("hiddenPreferredEditorCannotRetargetAnotherEditor") { f in
            f.editor.isHidden = true; f.start(preferred: f.editor)
            return f.coordinator.state == .failed(.noEditableTarget) && f.activations == 0
        }
        check("rememberedEditorMustStillOwnFirstResponder") { f in
            f.window.responder = f.window
            return f.coordinator.resolveEditor() == nil
        }
        check("anotherKeyWindowPreventsInitialActivationRequest") { f in
            let other = Window(contentRect: .zero, styleMask: .borderless, backing: .buffered, defer: true)
            other.isReleasedWhenClosed = false
            defer { other.close() }
            f.keyWindow = other; f.start(preferred: f.editor)
            return f.coordinator.state == .cancelled && f.activations == 0 && f.responderCalls.isEmpty
        }
        check("anotherWindowKeyNotificationCancelsBeforePolling") { f in
            let other = Window(contentRect: .zero, styleMask: .borderless, backing: .buffered, defer: true)
            other.isReleasedWhenClosed = false
            defer { other.close() }
            f.start(); f.coordinator.keyWindowDidChange(to: other); f.activateWindow(); f.tick()
            return f.coordinator.state == .cancelled && f.responderCalls.isEmpty
        }
        check("retiredEditorIsNoLongerARecoveryFallback") { f in
            f.coordinator.retireEditor(f.editor)
            return f.coordinator.resolveEditor() == nil
        }
        check("hiddenPaneCancelsPendingActivation") { f in
            f.start(); f.editor.isHidden = true; f.activateWindow()
            return f.coordinator.state == .cancelled && f.commits == 0 && f.responderCalls.isEmpty
        }
        check("detachedPaneCancelsPendingActivation") { f in
            f.start(); f.editor.removeFromSuperview(); f.activateWindow()
            return f.coordinator.state == .cancelled && f.responderCalls.isEmpty
        }
        check("reparentedEditorCannotReuseOldRecoveryRequest") { f in
            f.start()
            let parent = NSView(frame: f.container.bounds); f.container.addSubview(parent); parent.addSubview(f.editor)
            f.activateWindow()
            return f.coordinator.state == .cancelled && f.responderCalls.isEmpty
        }
        check("newResponderCancelsPendingActivation") { f in
            f.start(); f.window.responder = f.window; f.activateWindow()
            return f.coordinator.state == .cancelled && f.responderCalls.isEmpty
        }
        check("retireCancelsQueuedWorkEvenBeforeViewDisappears") { f in
            f.start(); f.coordinator.retireEditor(f.editor); f.activateWindow(); f.tick(count: 3)
            return f.coordinator.state == .cancelled && f.responderCalls.isEmpty
        }
        check("activatingCallbackCancellationPreventsActivationRequest") { f in
            f.coordinator.onStateChange = { state in if state == .activating { f.coordinator.cancel() } }
            f.start()
            return f.coordinator.state == .cancelled && f.activations == 0 && f.responderCalls.isEmpty
        }
        check("dismissAndRetryPublishesSameActivationStateAgain") { f in
            var starts = 0
            f.coordinator.onStateChange = { state in if state == .activating { starts += 1 } }
            f.start(); f.coordinator.cancel(notify: false); f.start()
            return starts == 2 && f.coordinator.state == .activating && f.activations == 2
        }
        check("dismissAndRetryPublishesSameMissingTargetFailureAgain") { f in
            var failures = 0
            f.coordinator.onStateChange = { state in if state == .failed(.noEditableTarget) { failures += 1 } }
            f.editor.isHidden = true
            f.start(); f.coordinator.cancel(notify: false); f.start()
            return failures == 2 && f.coordinator.state == .failed(.noEditableTarget) && f.activations == 0
        }
        check("verifyingCallbackCancellationPreventsResponderReset") { f in
            f.coordinator.onStateChange = { state in if state == .verifying { f.coordinator.cancel() } }
            f.start(); f.activateWindow()
            return f.coordinator.state == .cancelled && f.responderCalls.isEmpty
        }
        check("commitCallbackDeactivationPreventsResponderReset") { f in
            f.onCommit = { f.active = false }
            f.start(); f.activateWindow()
            return f.coordinator.state == .cancelled && f.responderCalls.isEmpty
        }
        check("releaseCallbackCancellationCannotRefocusOldEditor") { f in
            f.onRelease = { f.coordinator.cancel() }
            f.start(); f.activateWindow()
            return f.coordinator.state == .cancelled && f.responderCalls == ["release"]
        }
        check("releaseCallbackRetirementCannotRefocusDetachedPane") { f in
            f.onRelease = { f.coordinator.retireEditor(f.editor) }
            f.start(); f.activateWindow()
            return f.coordinator.state == .cancelled && f.responderCalls == ["release"]
        }
        check("releaseCallbackDeactivationCannotRefocusOldEditor") { f in
            f.onRelease = { f.active = false }
            f.start(); f.activateWindow()
            return f.coordinator.state == .cancelled && f.responderCalls == ["release"]
        }
        check("releaseCallbackNewResponderCannotBeStolen") { f in
            let other = NSTextView(frame: f.editor.frame); f.container.addSubview(other)
            f.onRelease = { f.window.responder = other }
            f.start(); f.activateWindow()
            return f.coordinator.state == .cancelled && f.window.firstResponder === other && f.responderCalls == ["release"]
        }
        check("releaseCallbackNewRequestSurvivesOldContinuation") { f in
            let other = NSTextView(frame: f.editor.frame); f.container.addSubview(other)
            f.onRelease = {
                f.onRelease = nil; f.window.responder = other
                f.coordinator.requestRecovery(editor: other) { _ in }
            }
            f.start(); f.activateWindow()
            let oldStopped = f.responderCalls == ["release"] && f.window.firstResponder === other && f.coordinator.state == .activating
            f.contextCurrent = true; f.tick(count: 3)
            return oldStopped && f.window.firstResponder === other && f.coordinator.state == .reconnected
        }
        check("ownResponderResignationDoesNotCancelRequestedReconnection") { f in
            f.onRelease = { f.coordinator.forgetEditor(f.editor) }
            f.start(); f.activateWindow(); f.contextCurrent = true; f.tick()
            return f.coordinator.state == .reconnected && f.responderCalls == ["release", "focus"]
        }
        check("rejectedResponderReportsFailureWithoutFalseSuccess") { f in
            f.acceptResponder = false
            f.start(); f.activateWindow(); f.contextCurrent = true; f.tick()
            return f.coordinator.state == .failed(.responderRejected)
        }
        check("lifecycleHealthyReturnMakesNoActivationOrResponderChanges") { f in
            f.activateWindow(); f.contextCurrent = true
            f.coordinator.applicationReturned(); f.tick(count: 4)
            return f.activations == 0 && f.keyRequests == 0 && f.commits == 0 && f.responderCalls.isEmpty
        }
        check("lifecycleReturnWaitsForRealActivationThenRebindsOnlyOnce") { f in
            f.editor.string = "fixture한글"; f.editor.setSelectedRange(NSRange(location: 1, length: 4))
            let selection = f.editor.selectedRanges
            f.coordinator.applicationReturned(); f.tick(count: 3)
            let waiting = f.activations == 1 && f.keyRequests == 0 && f.responderCalls.isEmpty
            f.active = true; f.coordinator.activationStateDidChange(); f.tick(count: 2)
            let waitsForKey = f.keyRequests == 1 && f.responderCalls.isEmpty
            f.activateWindow(); f.contextCurrent = true; f.tick(count: 3)
            return waiting && waitsForKey && f.commits == 0 && f.responderCalls == ["release", "focus"]
                && f.coordinator.state == .reconnected && f.editor.string == "fixture한글" && f.editor.selectedRanges == selection
        }
        check("lifecycleRepeatedReturnNotificationsDoNotRepeatActivation") { f in
            for _ in 0..<5 { f.coordinator.applicationReturned() }
            f.tick()
            for _ in 0..<5 { f.coordinator.applicationReturned() }
            f.tick(count: 3)
            return f.activations == 1 && f.responderCalls.isEmpty
        }
        check("lifecycleExplicitEditorClickDefersUntilNativeDispatchReturns") { f in
            f.coordinator.editorClickIntent(view: f.editor, in: f.window)
            let deferred = f.activations == 0 && f.responderCalls.isEmpty
            f.tick(); f.activateWindow(); f.contextCurrent = true; f.tick()
            return deferred && f.activations == 1 && f.commits == 0 && f.responderCalls == ["release", "focus"]
        }
        check("lifecycleNativeHealthySettlementAvoidsUnnecessaryRebind") { f in
            f.coordinator.editorClickIntent(view: f.editor, in: f.window)
            f.activateWindow(); f.contextCurrent = true; f.tick(count: 3)
            return f.activations == 0 && f.keyRequests == 0 && f.responderCalls.isEmpty
        }
        check("lifecycleForeignFrontmostNeverActivatesApplication") { f in
            f.frontmost = false
            f.coordinator.editorClickIntent(view: f.editor, in: f.window); f.coordinator.applicationReturned(); f.tick(count: 4)
            return f.activations == 0 && f.responderCalls.isEmpty
        }
        check("lifecycleOtherApplicationCancelsPendingAndActiveRequests") { f in
            f.coordinator.applicationReturned(); f.tick()
            f.frontmost = false; f.coordinator.applicationDeparted(); f.activateWindow(); f.tick(count: 3)
            return f.activations == 1 && f.responderCalls.isEmpty && f.coordinator.state == .cancelled
        }
        check("lifecycleMarkedTextIsNeverCommittedOrResetAutomatically") { f in
            f.editor.setMarkedText("한", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: 0))
            let selection = f.editor.selectedRanges
            f.coordinator.applicationReturned(); f.tick(count: 25)
            return f.activations == 0 && f.commits == 0 && f.responderCalls.isEmpty
                && f.editor.hasMarkedText() && f.editor.string == "한" && f.editor.selectedRanges == selection
        }
        check("lifecycleMarkedTextBeginningWhileActivatingPreventsRebind") { f in
            f.coordinator.applicationReturned(); f.tick()
            f.editor.setMarkedText("한", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: 0))
            f.activateWindow(); f.tick(count: 3)
            let held = f.responderCalls.isEmpty && f.commits == 0 && f.editor.hasMarkedText()
            f.editor.unmarkText(); f.tick(); f.contextCurrent = true; f.tick()
            return held && f.responderCalls == ["release", "focus"] && f.editor.string == "한" && f.commits == 0
        }
        check("lifecycleInputTransactionDefersAllAutomaticActions") { f in
            f.processingInput = true; f.coordinator.applicationReturned(); f.tick(count: 3)
            let held = f.activations == 0 && f.responderCalls.isEmpty
            f.processingInput = false; f.tick(); f.processingInput = true; f.activateWindow(); f.tick(count: 2)
            return held && f.activations == 1 && f.responderCalls.isEmpty && f.commits == 0
        }
        check("lifecycleNewClickCancelsOlderRecoveryBeforeResponderChange") { f in
            f.coordinator.applicationReturned(); f.tick()
            f.coordinator.editorClickIntent(view: nil, in: f.window); f.activateWindow(); f.tick(count: 3)
            return f.responderCalls.isEmpty && f.coordinator.state == .cancelled
        }
        check("lifecycleCannotRefocusAfterUserChoosesAnotherEditor") { f in
            f.coordinator.editorClickIntent(view: f.editor, in: f.window)
            let other = NSTextView(frame: f.editor.frame); f.container.addSubview(other); f.window.responder = other
            f.tick(count: 3)
            return f.activations == 0 && f.responderCalls.isEmpty && f.window.firstResponder === other
        }
        check("lifecycleReparentedClickTargetCannotBeReused") { f in
            f.coordinator.editorClickIntent(view: f.editor, in: f.window)
            let parent = NSView(frame: f.container.bounds); f.container.addSubview(parent); parent.addSubview(f.editor)
            f.tick(count: 3)
            return f.activations == 0 && f.responderCalls.isEmpty
        }
        check("lifecycleForeignActivationDuringReleaseNeverRefocuses") { f in
            f.onRelease = { f.frontmost = false }
            f.coordinator.applicationReturned(); f.tick(); f.activateWindow(); f.tick()
            return f.responderCalls == ["release"] && f.coordinator.state == .cancelled
        }
        check("lifecycleHealthyContextAfterActivationSkipsResponderReset") { f in
            f.coordinator.applicationReturned(); f.tick()
            f.contextCurrent = true; f.activateWindow(); f.tick(count: 3)
            return f.activations == 1 && f.responderCalls.isEmpty && f.coordinator.state == .reconnected
        }
        check("lifecycleOtherKeyWindowPreventsActivationAndRebind") { f in
            let other = Window(contentRect: .zero, styleMask: .borderless, backing: .buffered, defer: true)
            other.isReleasedWhenClosed = false
            defer { other.close() }
            f.coordinator.editorClickIntent(view: f.editor, in: f.window); f.keyWindow = other; f.tick(count: 3)
            return f.activations == 0 && f.keyRequests == 0 && f.responderCalls.isEmpty
        }
        check("lifecycleModalOpeningDuringSettleCancelsRecovery") { f in
            f.coordinator.applicationReturned(); f.modalWindow = f.window; f.tick(count: 3)
            return f.activations == 0 && f.responderCalls.isEmpty
        }
        check("explicitMissedClickFocusesOnlyCapturedEditorAfterActualActivation") { f in
            f.window.responder = f.window
            f.coordinator.editorClickIntent(view: f.editor, in: f.window); f.tick(count: 3)
            let waitedForActivation = f.activations == 1 && f.responderCalls.isEmpty
            f.active = true; f.tick(count: 3)
            let waitedForKey = f.keyRequests == 1 && f.responderCalls.isEmpty
            f.activateWindow(); f.tick(); f.contextCurrent = true; f.tick(count: 3)
            return waitedForActivation && waitedForKey && f.responderCalls == ["focus"]
                && f.window.firstResponder === f.editor && f.commits == 0
        }
        check("explicitMissedClickCannotReplaceChangedPriorResponder") { f in
            f.window.responder = f.window
            f.coordinator.editorClickIntent(view: f.editor, in: f.window); f.tick()
            let newer = NSTextView(frame: f.editor.frame); f.container.addSubview(newer); f.window.responder = newer
            f.activateWindow(); f.tick(count: 3)
            return f.responderCalls.isEmpty && f.window.firstResponder === newer
        }
        check("applicationReturnWithoutRetainedEditorDoesNotChooseFocus") { f in
            f.window.responder = f.window; f.coordinator.applicationReturned(); f.tick(count: 3)
            return f.activations == 0 && f.responderCalls.isEmpty && f.window.firstResponder === f.window
        }
        check("missedClickCannotCommitAnotherEditorsPreedit") { f in
            let prior = NSTextView(frame: f.editor.frame); f.container.addSubview(prior)
            prior.setMarkedText("한", selectedRange: NSRange(location: 1, length: 0), replacementRange: NSRange(location: 0, length: 0))
            f.window.responder = prior
            f.coordinator.editorClickIntent(view: f.editor, in: f.window); f.tick(count: 25)
            return f.activations == 0 && f.responderCalls.isEmpty && prior.hasMarkedText() && f.window.firstResponder === prior
        }
        check("newTypingInPriorEditorCancelsMissedClick") { f in
            let prior = NSTextView(frame: f.editor.frame); f.container.addSubview(prior)
            f.window.responder = prior; f.activateWindow()
            f.coordinator.editorClickIntent(view: f.editor, in: f.window)
            prior.insertText("new user input", replacementRange: NSRange(location: 0, length: 0))
            f.tick(count: 4)
            return f.responderCalls.isEmpty && f.window.firstResponder === prior
        }
        check("priorEditorSelectionChangeCancelsMissedClick") { f in
            let prior = NSTextView(frame: f.editor.frame); f.container.addSubview(prior); prior.string = "fixture"
            prior.setSelectedRange(NSRange(location: 0, length: 0)); f.window.responder = prior; f.activateWindow()
            f.coordinator.editorClickIntent(view: f.editor, in: f.window)
            prior.setSelectedRange(NSRange(location: 3, length: 0)); f.tick(count: 4)
            return f.responderCalls.isEmpty && f.window.firstResponder === prior && prior.selectedRange().location == 3
        }
        check("priorEditorArrowOrOtherKeyCancelsMissedClick") { f in
            let prior = NSTextView(frame: f.editor.frame); f.container.addSubview(prior); f.window.responder = prior
            f.coordinator.editorClickIntent(view: f.editor, in: f.window)
            f.coordinator.keyboardInteraction(in: f.window); f.activateWindow(); f.tick(count: 4)
            return f.responderCalls.isEmpty && f.window.firstResponder === prior
        }
        check("priorNonTextResponderKeyCancelsMissedClick") { f in
            let prior = NSView(frame: f.editor.frame); f.container.addSubview(prior); f.window.responder = prior
            f.coordinator.editorClickIntent(view: f.editor, in: f.window)
            f.coordinator.keyboardInteraction(in: f.window); f.activateWindow(); f.tick(count: 4)
            return f.responderCalls.isEmpty && f.window.firstResponder === prior
        }
        check("unrelatedKeyCannotCancelClickedTargetRepair") { f in
            f.coordinator.editorClickIntent(view: f.editor, in: f.window)
            f.coordinator.keyboardInteraction(in: nil)
            f.tick(); f.activateWindow(); f.contextCurrent = true; f.tick(count: 3)
            return f.activations == 1 && f.coordinator.state == .reconnected
        }
        check("alreadyFocusedClickTargetInputDoesNotCancelActivationRepair") { f in
            f.coordinator.editorClickIntent(view: f.editor, in: f.window)
            f.coordinator.keyboardInteraction(in: f.window)
            f.editor.insertText("fixture", replacementRange: NSRange(location: 0, length: 0))
            f.tick(); f.activateWindow(); f.contextCurrent = true; f.tick(count: 3)
            return f.activations == 1 && f.coordinator.state == .reconnected && f.editor.string == "fixture"
        }
        check("sameLengthModelEditInPriorEditorCancelsMissedClick") { f in
            let prior = NSTextView(frame: f.editor.frame); f.container.addSubview(prior); prior.string = "abc"
            f.window.responder = prior; f.coordinator.editorClickIntent(view: f.editor, in: f.window)
            prior.textStorage?.replaceCharacters(in: NSRange(location: 0, length: 1), with: "z")
            f.activateWindow(); f.tick(count: 4)
            return f.responderCalls.isEmpty && f.window.firstResponder === prior
        }
        report.merge(fieldEditorChecks()) { _, new in new }
        return report
    }

    /// These use AppKit's real shared field editor and real responder changes.
    /// Only activation/key-window reporting and the input-context check are
    /// simulated; no window is ordered and no keyboard events are sent.
    private static func fieldEditorChecks() -> [String: Bool] {
        var report: [String: Bool] = [:]
        let appWasActive = NSApp.isActive
        let keyWindow = NSApp.keyWindow
        func check(_ name: String, field: NSTextField? = nil, observesNotifications: Bool = false,
                   text: String = "fixture한글", _ body: (FieldFixture) -> Bool) {
            let fixture = FieldFixture(field: field, observesNotifications: observesNotifications, text: text)
            defer { fixture.dispose() }
            report[name] = body(fixture)
        }
        check("nativeFieldEditorRebindsThroughOwningControl") { f in
            guard let editor = f.editor else { return false }
            editor.setSelectedRange(NSRange(location: 2, length: 3))
            let selection = editor.selectedRanges
            f.start(); f.tick()
            return f.coordinator.state == .reconnected && f.releasedFieldEditorDetached
                && f.focusTargetsWereOwner && f.responderCalls == ["release", "focus"]
                && f.editor?.selectedRanges == selection && f.editor?.string == "fixture한글"
                && f.window.firstResponder === f.editor
        }
        check("nativeFieldEditorCommitsMarkedSyllableWithoutLosingDraft") { f in
            guard let editor = f.editor else { return false }
            editor.setMarkedText("한", selectedRange: NSRange(location: 1, length: 0),
                                 replacementRange: NSRange(location: 0, length: editor.textStorage?.length ?? 0))
            f.start(); f.tick()
            return f.commits == 1 && f.coordinator.state == .reconnected
                && f.field.stringValue == "한" && f.editor?.string == "한" && f.editor?.hasMarkedText() == false
        }
        check("nativeSearchFieldRebindsThroughOwner", field: NSSearchField(frame: .zero)) { f in
            f.start(); f.tick()
            return f.coordinator.state == .reconnected && f.focusTargetsWereOwner && f.editor?.string == "fixture한글"
        }
        check("nativeComboBoxRebindsThroughOwner", field: NSComboBox(frame: .zero)) { f in
            f.start(); f.tick()
            return f.coordinator.state == .reconnected && f.focusTargetsWereOwner && f.editor?.string == "fixture한글"
        }
        check("nativeFieldEditorWithoutSupportedOwnerFailsBeforeRelease") { f in
            guard let editor = f.editor else { return false }
            let delegate = editor.delegate
            editor.delegate = nil
            defer { editor.delegate = delegate }
            f.start(preferred: editor); f.tick()
            return f.coordinator.state == .failed(.noEditableTarget) && f.responderCalls.isEmpty
                && f.window.firstResponder === editor
        }
        check("sharedFieldEditorCannotRetargetAnotherControlWhileActivating") { f in
            guard let editor = f.editor else { return false }
            f.active = false; f.start()
            guard f.window.makeFirstResponder(f.other), f.other.currentEditor() === editor else { return false }
            f.active = true; f.coordinator.activationStateDidChange(); f.tick()
            return f.coordinator.state == .cancelled && f.responderCalls.isEmpty
                && f.window.firstResponder === f.other.currentEditor()
        }
        check("sharedFieldEditorFallbackRemembersOwningControl") { f in
            guard let editor = f.editor else { return false }
            f.coordinator.rememberFocusedEditor(editor)
            guard f.window.makeFirstResponder(f.other), f.other.currentEditor() === editor else { return false }
            f.keyWindow = nil
            return f.coordinator.resolveEditor() == nil
        }
        check("fieldOwnerRemovalDuringReleaseCancelsRebind") { f in
            f.onRelease = { f.field.removeFromSuperview() }
            f.start(); f.tick()
            return f.coordinator.state == .cancelled && f.responderCalls == ["release"]
        }
        check("fieldOwnerReparentingDuringReleaseCancelsRebind") { f in
            f.onRelease = {
                let host = NSView(frame: f.container.bounds)
                f.container.addSubview(host); host.addSubview(f.field)
            }
            f.start(); f.tick()
            return f.coordinator.state == .cancelled && f.responderCalls == ["release"]
        }
        check("fieldOwnerDisabledDuringReleaseCancelsRebind") { f in
            f.onRelease = { f.field.isEnabled = false }
            f.start(); f.tick()
            return f.coordinator.state == .cancelled && f.responderCalls == ["release"]
        }
        check("fieldReleaseCallbackCannotStealAnotherControl") { f in
            f.onRelease = { _ = f.window.makeFirstResponder(f.other) }
            f.start(); f.tick()
            return f.coordinator.state == .cancelled && f.responderCalls == ["release"]
                && f.window.firstResponder === f.other.currentEditor()
        }
        check("fieldOwnerRejectingRebindReportsFailure") { f in
            f.acceptFocus = false; f.start(); f.tick()
            return f.coordinator.state == .failed(.responderRejected) && f.responderCalls == ["release", "focus"]
                && f.field.stringValue == "fixture한글"
        }
        check("fieldSelectionClampsAfterEndEditingUpdatesValue") { f in
            guard let editor = f.editor else { return false }
            editor.setSelectedRange(NSRange(location: 4, length: 3))
            f.onRelease = { f.field.stringValue = "새" }
            f.start(); f.tick()
            return f.coordinator.state == .reconnected && f.editor?.string == "새"
                && f.editor?.selectedRange() == NSRange(location: 1, length: 0)
        }
        check("fieldFocusCallbackNewControlCancelsVerification") { f in
            f.onFocus = { _ = f.window.makeFirstResponder(f.other) }
            f.start(); f.tick()
            return f.coordinator.state == .cancelled && f.window.firstResponder === f.other.currentEditor()
        }
        check("nativeFieldEditingIsRememberedWithoutManualRegistration", observesNotifications: true) { f in
            guard let editor = f.editor else { return false }
            editor.insertText("x", replacementRange: NSRange(location: 0, length: 0))
            let text = editor.string
            f.active = false; f.keyWindow = nil
            return f.coordinator.resolveEditor() === editor && editor.string == text
                && f.window.firstResponder === editor && f.responderCalls.isEmpty && f.commits == 0
        }
        check("disabledNotificationObservationDoesNotRegisterFields") { f in
            f.editor?.insertText("x", replacementRange: NSRange(location: 0, length: 0))
            f.active = false; f.keyWindow = nil
            return f.coordinator.resolveEditor() == nil && f.responderCalls.isEmpty
        }
        check("nativeEditingRegistrationCannotFollowReusedFieldEditor", observesNotifications: true) { f in
            guard let editor = f.editor else { return false }
            editor.insertText("x", replacementRange: NSRange(location: 0, length: 0))
            guard f.window.makeFirstResponder(f.other), f.other.currentEditor() === editor else { return false }
            f.active = false; f.keyWindow = nil
            return f.coordinator.resolveEditor() == nil && f.responderCalls.isEmpty
        }
        check("emptyFieldResigningKeyWindowCanRecoverWithoutTyping", observesNotifications: true, text: "") { f in
            guard let editor = f.editor, editor.string.isEmpty else { return false }
            f.active = false; f.keyWindow = nil
            NotificationCenter.default.post(name: NSWindow.didResignKeyNotification, object: f.window)
            let remembered = f.coordinator.resolveEditor() === editor
            f.coordinator.requestRecovery(editor: nil) { _ in }
            let waitingWithoutMutation = f.coordinator.state == .activating && f.responderCalls.isEmpty && editor.string.isEmpty
            f.active = true; f.keyWindow = f.window; f.coordinator.activationStateDidChange(); f.tick()
            return remembered && waitingWithoutMutation && f.coordinator.state == .reconnected
                && f.editor?.string.isEmpty == true && f.window.firstResponder === f.editor
        }
        check("fieldLifetimeObservationSurvivesCompletedRecovery", observesNotifications: true) { f in
            f.start(); f.tick()
            guard f.coordinator.state == .reconnected, f.window.makeFirstResponder(f.other),
                  let editor = f.other.currentEditor() as? NSTextView else { return false }
            editor.insertText("x", replacementRange: NSRange(location: 0, length: 0))
            f.active = false; f.keyWindow = nil
            return f.coordinator.resolveEditor() === editor && f.window.firstResponder === editor
        }
        check("resigningTrackedWindowCancelsRequestAndKeepsPassiveTarget", observesNotifications: true) { f in
            guard let editor = f.editor else { return false }
            editor.insertText("x", replacementRange: NSRange(location: 0, length: 0))
            f.active = false; f.keyWindow = nil
            f.coordinator.requestRecovery(editor: nil) { _ in }
            let wasWaiting = f.coordinator.state == .activating
            NotificationCenter.default.post(name: NSWindow.didResignKeyNotification, object: f.window)
            return wasWaiting && f.coordinator.state == .cancelled && f.coordinator.resolveEditor() === editor && f.responderCalls.isEmpty
        }
        check("anotherWindowResigningCannotRetargetPendingFieldRequest", observesNotifications: true) { f in
            guard let editor = f.editor else { return false }
            editor.insertText("x", replacementRange: NSRange(location: 0, length: 0))
            f.active = false; f.keyWindow = nil
            f.coordinator.requestRecovery(editor: nil) { _ in }
            let other = FieldFixture(field: nil)
            defer { other.dispose() }
            NotificationCenter.default.post(name: NSWindow.didResignKeyNotification, object: other.window)
            return f.coordinator.state == .activating && f.coordinator.resolveEditor() === editor && f.responderCalls.isEmpty
        }
        check("explicitMissedFieldClickUsesOwningControlNotSharedEditor") { f in
            f.ownFrontmost = true
            f.coordinator.editorClickIntent(view: f.other, in: f.window); f.tick(); f.tick()
            return f.other.currentEditor() != nil && f.field.currentEditor() == nil && f.responderCalls == ["focus"] && f.commits == 0
        }
        check("explicitFieldClickCannotFollowReusedPriorFieldEditor") { f in
            f.ownFrontmost = true
            let third = NSTextField(frame: NSRect(x: 20, y: 40, width: 200, height: 24)); f.container.addSubview(third)
            f.coordinator.editorClickIntent(view: f.other, in: f.window)
            guard f.window.makeFirstResponder(third) else { return false }
            f.tick(); f.tick()
            return third.currentEditor() != nil && f.other.currentEditor() == nil && f.responderCalls.isEmpty
        }
        report["fieldEditorFixturesPreserveRealApplicationActivationAndKeyWindow"] = NSApp.isActive == appWasActive && NSApp.keyWindow === keyWindow
        let returning = Fixture(observesNotifications: true)
        NotificationCenter.default.post(name: NSApplication.didBecomeActiveNotification, object: NSApp)
        returning.tick(count: 4)
        report["ownApplicationReturnRepairsInactiveRetainedEditorWithoutManualButton"] = returning.activations == 1
        returning.dispose()
        let resigning = Fixture(observesNotifications: true)
        resigning.coordinator.editorClickIntent(view: resigning.editor, in: resigning.window)
        NotificationCenter.default.post(name: NSWindow.didResignKeyNotification, object: resigning.window)
        resigning.tick(count: 4)
        report["keyWindowResignationCancelsUnprocessedClickIntent"] = resigning.activations == 0 && resigning.responderCalls.isEmpty
        resigning.dispose()
        return report
    }

    private final class FieldWindow: NSWindow {
        override var isVisible: Bool { true }
        override var isKeyWindow: Bool { true }
    }

    @MainActor
    private final class FieldFixture {
        let window = FieldWindow(contentRect: NSRect(x: -20000, y: -20000, width: 320, height: 120), styleMask: .titled, backing: .buffered, defer: true)
        let container = NSView(frame: NSRect(x: 0, y: 0, width: 320, height: 120))
        let field: NSTextField
        let other = NSTextField(frame: NSRect(x: 15, y: 20, width: 290, height: 26))
        var editor: NSTextView? { field.currentEditor() as? NSTextView }
        var active = true
        var ownFrontmost = false
        var keyWindow: NSWindow?
        var commits = 0
        var responderCalls: [String] = []
        var releasedFieldEditorDetached = false
        var focusTargetsWereOwner = true
        var acceptFocus = true
        var onRelease: (() -> Void)?
        var onFocus: (() -> Void)?
        private let observesNotifications: Bool
        private var time: TimeInterval = 0
        private var pending: [() -> Void] = []
        lazy var coordinator = InputSessionRecoveryCoordinator(environment: .init(
            appIsActive: { [weak self] in self?.active ?? false }, keyWindow: { [weak self] in self?.keyWindow }, modalWindow: { nil },
            contextIsCurrent: { _ in true }, activate: {}, makeKey: { _ in },
            makeFirstResponder: { [weak self] window, responder in
                guard let self else { return false }
                self.responderCalls.append(responder == nil ? "release" : "focus")
                if responder == nil {
                    let prior = self.editor
                    let accepted = window.makeFirstResponder(nil)
                    self.releasedFieldEditorDetached = prior != nil && prior?.window == nil && self.editor == nil
                    self.onRelease?()
                    return accepted
                }
                self.focusTargetsWereOwner = self.focusTargetsWereOwner && responder === self.field
                guard self.acceptFocus else { return false }
                let accepted = window.makeFirstResponder(responder)
                self.onFocus?()
                return accepted
            }, now: { [weak self] in self?.time ?? 0 }, schedule: { [weak self] _, action in self?.pending.append(action) },
            ownAppIsFrontmost: { [weak self] in self?.ownFrontmost ?? false }), observesNotifications: observesNotifications)

        init(field: NSTextField?, observesNotifications: Bool = false, text: String = "fixture한글") {
            self.field = field ?? NSTextField(frame: .zero)
            self.observesNotifications = observesNotifications
            self.field.frame = NSRect(x: 15, y: 70, width: 290, height: 26)
            self.field.stringValue = text; other.stringValue = "other"
            window.isReleasedWhenClosed = false; window.contentView = container
            container.addSubview(self.field); container.addSubview(other)
            keyWindow = window
            _ = coordinator
            _ = window.makeFirstResponder(self.field)
        }
        func start(preferred: NSTextView? = nil) {
            coordinator.requestRecovery(editor: preferred ?? editor) { [weak self] editor in
                self?.commits += 1
                if editor.hasMarkedText() { editor.unmarkText() }
            }
        }
        func tick() {
            time += 0.1
            let work = pending; pending.removeAll(); work.forEach { $0() }
        }
        func dispose() {
            coordinator.cancel(notify: false); pending.removeAll(); onRelease = nil; onFocus = nil
            _ = window.makeFirstResponder(nil); window.contentView = nil; window.close()
        }
    }

    private final class Window: NSWindow {
        var responder: NSResponder?
        var key = false
        override var firstResponder: NSResponder? { responder }
        override var isVisible: Bool { true }
        override var isKeyWindow: Bool { key }
    }

    @MainActor
    private final class Fixture {
        let window = Window(contentRect: NSRect(x: -20000, y: -20000, width: 320, height: 120), styleMask: .borderless, backing: .buffered, defer: true)
        let container = NSView(frame: NSRect(x: 0, y: 0, width: 320, height: 120))
        let editor = NSTextView(frame: NSRect(x: 0, y: 0, width: 320, height: 120))
        var active = false
        var frontmost = true
        var processingInput = false
        var keyWindow: NSWindow?
        var modalWindow: NSWindow?
        var contextCurrent = false
        var activations = 0
        var keyRequests = 0
        var commits = 0
        var responderCalls: [String] = []
        var acceptResponder = true
        var onCommit: (() -> Void)?
        var onRelease: (() -> Void)?
        private let observesNotifications: Bool
        private var time: TimeInterval = 0
        private var pending: [() -> Void] = []
        lazy var coordinator = InputSessionRecoveryCoordinator(environment: .init(
            appIsActive: { [weak self] in self?.active ?? false }, keyWindow: { [weak self] in self?.keyWindow }, modalWindow: { [weak self] in self?.modalWindow },
            contextIsCurrent: { [weak self] _ in self?.contextCurrent ?? false }, activate: { [weak self] in self?.activations += 1 },
            makeKey: { [weak self] _ in self?.keyRequests += 1 }, makeFirstResponder: { [weak self] window, responder in
                guard let self, self.acceptResponder else { return false }
                self.responderCalls.append(responder == nil ? "release" : "focus")
                self.window.responder = responder ?? window
                if responder == nil { self.onRelease?() }
                return true
            }, now: { [weak self] in self?.time ?? 0 }, schedule: { [weak self] _, action in self?.pending.append(action) },
            ownAppIsFrontmost: { [weak self] in self?.frontmost ?? false },
            editorIsProcessingInput: { [weak self] _ in self?.processingInput ?? false }), observesNotifications: observesNotifications)

        init(observesNotifications: Bool = false) {
            self.observesNotifications = observesNotifications
            window.isReleasedWhenClosed = false; window.contentView = container; container.addSubview(editor)
            editor.isEditable = true; window.responder = editor
            coordinator.rememberFocusedEditor(editor)
        }
        func start(preferred: NSTextView? = nil) {
            coordinator.requestRecovery(editor: preferred) { [weak self] editor in
                self?.commits += 1
                if editor.hasMarkedText() { editor.unmarkText() }
                self?.onCommit?()
            }
        }
        func activateWindow() {
            active = true; keyWindow = window; window.key = true
            coordinator.activationStateDidChange()
        }
        func tick(count: Int = 1) {
            for _ in 0..<count {
                time += 0.1
                let work = pending; pending.removeAll()
                work.forEach { $0() }
            }
        }
        func dispose() {
            coordinator.cancel(notify: false); coordinator.onStateChange = nil
            pending.removeAll(); onCommit = nil; onRelease = nil
            window.responder = nil; window.contentView = nil; window.close()
        }
    }
}
