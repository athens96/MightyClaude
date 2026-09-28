import Foundation
import Testing
@testable import MightyCore

@Suite struct PaneTitleTests {
    // MARK: - shortened

    @Test func shortenedReturnsNilForEmptyInput() {
        #expect(PaneTitle.shortened("") == nil)
    }

    @Test func shortenedReturnsNilForWhitespaceOnly() {
        #expect(PaneTitle.shortened("   ") == nil)
        #expect(PaneTitle.shortened("\n\n\t  \n") == nil)
    }

    @Test func shortenedCollapsesNewlinesAndWhitespaceRuns() {
        #expect(PaneTitle.shortened("hello\nworld") == "hello world")
        #expect(PaneTitle.shortened("hello  \n\n  world") == "hello world")
        #expect(PaneTitle.shortened("  leading and trailing  ") == "leading and trailing")
    }

    @Test func shortenedReturnsExactlyFortyCharactersUnchanged() {
        let exactly40 = String(repeating: "a", count: 40)
        #expect(PaneTitle.shortened(exactly40) == exactly40)
    }

    @Test func shortenedAppendsEllipsisAt41Characters() {
        let fortyOne = String(repeating: "a", count: 41)
        let result = PaneTitle.shortened(fortyOne)
        #expect(result == String(repeating: "a", count: 40) + "…")
    }

    @Test func shortenedTruncatesLongInputAt40CharacterBoundary() {
        let long = "This is a really long request that definitely exceeds forty characters"
        let result = PaneTitle.shortened(long)
        #expect(result?.hasSuffix("…") == true)
        // The text portion before "…" is exactly 40 characters
        #expect(result.map { String($0.dropLast()) }?.count == 40)
    }

    @Test func shortenedHandlesSlashCommands() {
        #expect(PaneTitle.shortened("/ouroboros:run seed") == "/ouroboros:run seed")
        let long = "/ouroboros:run seed with a very long set of additional arguments here"
        let result = PaneTitle.shortened(long)
        #expect(result?.hasPrefix("/ouroboros:run") == true)
        #expect(result?.hasSuffix("…") == true)
    }

    @Test func shortenedUsesCharacterCountNot16BitUnits() {
        // Korean characters are multi-byte but each is one Swift Character
        let korean = String(repeating: "한", count: 40)
        #expect(PaneTitle.shortened(korean) == korean)
        let longKorean = String(repeating: "한", count: 41)
        #expect(PaneTitle.shortened(longKorean) == String(repeating: "한", count: 40) + "…")
    }

    // MARK: - RunSession.titleMode default

    @Test func runSessionTitleModeDefaultsToNil() {
        let session = RunSession(workspaceId: "ws", title: "Claude", kind: "claude", provider: "claude")
        #expect(session.titleMode == nil)
    }

    @Test func runSessionTitleModeDecodesFromJSON() throws {
        let json = #"{"id":"x","workspaceId":"ws","titleMode":"fixed"}"#
        let session = try JSONDecoder().decode(RunSession.self, from: Data(json.utf8))
        #expect(session.titleMode == "fixed")
    }

    @Test func runSessionTitleModeAbsentDecodesAsNil() throws {
        let json = #"{"id":"x","workspaceId":"ws"}"#
        let session = try JSONDecoder().decode(RunSession.self, from: Data(json.utf8))
        #expect(session.titleMode == nil)
    }

    // MARK: - RunSession.titleTooltip

    @Test func titleTooltipReturnsNilWhenNoUserLogs() {
        let session = RunSession(workspaceId: "ws", title: "Claude", kind: "claude", provider: "claude")
        #expect(session.titleTooltip == nil)
    }

    @Test func titleTooltipReturnsMostRecentUserLogText() {
        var session = RunSession(workspaceId: "ws", title: "Claude", kind: "claude", provider: "claude")
        session.logs = [
            LogEntry(kind: "user", text: "first request"),
            LogEntry(kind: "assistant", text: "response"),
            LogEntry(kind: "user", text: "second request"),
        ]
        #expect(session.titleTooltip == "second request")
    }

    @Test func titleTooltipSkipsAttachmentOnlyUserLogs() {
        var session = RunSession(workspaceId: "ws", title: "Claude", kind: "claude", provider: "claude")
        session.logs = [
            LogEntry(kind: "user", text: "real message"),
            LogEntry(kind: "user", text: "첨부: file.txt (1KB)"),
        ]
        #expect(session.titleTooltip == "real message")
    }

    @Test func titleTooltipReturnsNilWhenOnlyAttachmentOnlyLogs() {
        var session = RunSession(workspaceId: "ws", title: "Claude", kind: "claude", provider: "claude")
        session.logs = [LogEntry(kind: "user", text: "첨부: file.txt (1KB)")]
        #expect(session.titleTooltip == nil)
    }

    @Test func titleTooltipDropsAttachmentLinesAfterTypedText() {
        var session = RunSession(workspaceId: "ws", title: "Claude", kind: "claude", provider: "claude")
        session.logs = [LogEntry(kind: "user", text: "fix the build\n\nfirst line kept\n\n첨부: a.png (1KB)\n첨부: b.txt (2KB)")]
        #expect(session.titleTooltip == "fix the build\n\nfirst line kept")
        #expect(PaneTitle.autoTitle(for: session, defaultTitle: "Claude") == "fix the build first line kept")
    }

    @Test func titleHelpShowsTheRequestOnlyForAutomaticAgentPanes() {
        var session = RunSession(workspaceId: "ws", title: "fix the build", kind: "claude", provider: "claude")
        session.logs = [LogEntry(kind: "user", text: "fix the build")]
        #expect(session.titleHelp == "fix the build")
        session.titleMode = "fixed"; session.title = "My pane"
        #expect(session.titleHelp == "My pane")
        var shell = RunSession(workspaceId: "ws", title: "터미널", kind: "shell", provider: "claude")
        shell.logs = [LogEntry(kind: "user", text: "ls")]
        #expect(shell.titleHelp == "터미널")
    }

    // MARK: - PaneTitle.autoTitle

    @Test func autoTitleReturnsDefaultWhenNoUserLogs() {
        let session = RunSession(workspaceId: "ws", title: "Claude", kind: "claude", provider: "claude")
        #expect(PaneTitle.autoTitle(for: session, defaultTitle: "Claude") == "Claude")
    }

    @Test func autoTitleReturnsShortenedRequestText() {
        var session = RunSession(workspaceId: "ws", title: "Claude", kind: "claude", provider: "claude")
        session.logs = [LogEntry(kind: "user", text: "fix the bug")]
        #expect(PaneTitle.autoTitle(for: session, defaultTitle: "Claude") == "fix the bug")
    }

    @Test func autoTitleTruncatesLongRequestAt40Chars() {
        var session = RunSession(workspaceId: "ws", title: "Claude", kind: "claude", provider: "claude")
        let long = "This is a really long request that definitely exceeds forty characters"
        session.logs = [LogEntry(kind: "user", text: long)]
        let result = PaneTitle.autoTitle(for: session, defaultTitle: "Claude")
        #expect(result.hasSuffix("…"))
        #expect(result != "Claude")
    }

    @Test func autoTitleUsesLatestUserLogNotFirst() {
        var session = RunSession(workspaceId: "ws", title: "Claude", kind: "claude", provider: "claude")
        session.logs = [
            LogEntry(kind: "user", text: "first request"),
            LogEntry(kind: "user", text: "second request"),
        ]
        #expect(PaneTitle.autoTitle(for: session, defaultTitle: "Claude") == "second request")
    }

    @Test func autoTitleSkipsAttachmentOnlyLogsAndFallsBackToDefault() {
        var session = RunSession(workspaceId: "ws", title: "Claude", kind: "claude", provider: "claude")
        session.logs = [LogEntry(kind: "user", text: "첨부: file.txt (1KB)")]
        #expect(PaneTitle.autoTitle(for: session, defaultTitle: "Claude") == "Claude")
    }

    // MARK: - titleMode: rename sets fixed, autoTitle restores auto

    @Test func runSessionTitleModeCanBeSetToFixed() {
        var session = RunSession(workspaceId: "ws", title: "Claude", kind: "claude", provider: "claude")
        session.titleMode = "fixed"
        #expect(session.titleMode == "fixed")
    }

    @Test func autoTitleForCodexPaneUsesCodexDefault() {
        var session = RunSession(workspaceId: "ws", title: "Codex", kind: "claude", provider: "codex")
        session.logs = [LogEntry(kind: "user", text: "review my PR")]
        #expect(PaneTitle.autoTitle(for: session, defaultTitle: "Codex") == "review my PR")
    }

    @Test func autoTitleResetsToDefaultAfterAutoModeWithNoLogs() {
        var session = RunSession(workspaceId: "ws", title: "renamed title", kind: "claude", provider: "claude")
        session.titleMode = "fixed"
        // Simulate switching back to auto with no user logs
        session.titleMode = "auto"
        let newTitle = PaneTitle.autoTitle(for: session, defaultTitle: "Claude")
        #expect(newTitle == "Claude")
    }

    // MARK: - Migration: normalize retitles automatic panes at load

    private func makeSnapshot(titleMode: String?, logText: String?, provider: String = "claude") -> AppSnapshot {
        let ws = Workspace(id: "ws1", name: "Test", path: "/tmp")
        var session = RunSession(workspaceId: "ws1", title: "Claude", kind: "claude", provider: provider)
        session.titleMode = titleMode
        if let text = logText {
            session.logs = [LogEntry(kind: "user", text: text)]
        }
        return AppSnapshot(workspaces: [ws], sessions: [session])
    }

    @Test func migrationRetitlesNilTitleModeFromUserLog() {
        let snapshot = makeSnapshot(titleMode: nil, logText: "fix the authentication bug")
        let result = StateRepository.normalize(snapshot, restoring: true)
        #expect(result.sessions.first?.title == "fix the authentication bug")
    }

    @Test func migrationRetitlesAutoTitleModeFromUserLog() {
        let snapshot = makeSnapshot(titleMode: "auto", logText: "implement dark mode")
        let result = StateRepository.normalize(snapshot, restoring: true)
        #expect(result.sessions.first?.title == "implement dark mode")
    }

    @Test func migrationKeepsProviderDefaultWhenNoUserLogs() {
        let snapshot = makeSnapshot(titleMode: nil, logText: nil)
        let result = StateRepository.normalize(snapshot, restoring: true)
        #expect(result.sessions.first?.title == "Claude")
    }

    @Test func migrationKeepsProviderDefaultCodexWhenNoUserLogs() {
        let snapshot = makeSnapshot(titleMode: nil, logText: nil, provider: "codex")
        let result = StateRepository.normalize(snapshot, restoring: true)
        #expect(result.sessions.first?.title == "Codex")
    }

    @Test func migrationDoesNotRetitleFixedSession() {
        let ws = Workspace(id: "ws1", name: "Test", path: "/tmp")
        var session = RunSession(workspaceId: "ws1", title: "My Custom Name", kind: "claude", provider: "claude")
        session.titleMode = "fixed"
        session.logs = [LogEntry(kind: "user", text: "some request")]
        let snapshot = AppSnapshot(workspaces: [ws], sessions: [session])
        let result = StateRepository.normalize(snapshot, restoring: true)
        #expect(result.sessions.first?.title == "My Custom Name")
    }

    @Test func migrationTruncatesLongRequestTo40Chars() {
        let long = "This is a really long request that definitely exceeds forty characters"
        let snapshot = makeSnapshot(titleMode: nil, logText: long)
        let result = StateRepository.normalize(snapshot, restoring: true)
        let title = result.sessions.first?.title ?? ""
        #expect(title.hasSuffix("…"))
        #expect(title != "Claude")
        #expect(title.count <= 41) // 40 chars + "…"
    }

    @Test func migrationSkipsAttachmentOnlyLogsAndUsesDefault() {
        let snapshot = makeSnapshot(titleMode: nil, logText: "첨부: file.txt (1KB)")
        let result = StateRepository.normalize(snapshot, restoring: true)
        #expect(result.sessions.first?.title == "Claude")
    }

    @Test func migrationDoesNotRetitleWhenNotRestoring() {
        // When restoring: false (save path), auto panes are NOT retitled automatically
        let snapshot = makeSnapshot(titleMode: nil, logText: "some request")
        let result = StateRepository.normalize(snapshot, restoring: false)
        // Title should remain as-is (whatever was set), not retitled on save
        #expect(result.sessions.first?.title != nil)
    }

    @Test func migrationLegacyRenamedPaneBecomesAutomatic() {
        // A pane that was previously "renamed" but has no titleMode saved
        // (old snapshot) should become automatic and get retitled from logs
        let ws = Workspace(id: "ws1", name: "Test", path: "/tmp")
        var session = RunSession(workspaceId: "ws1", title: "Old Custom Name", kind: "claude", provider: "claude")
        session.titleMode = nil  // no titleMode saved — old snapshot
        session.logs = [LogEntry(kind: "user", text: "refactor the auth module")]
        let snapshot = AppSnapshot(workspaces: [ws], sessions: [session])
        let result = StateRepository.normalize(snapshot, restoring: true)
        // Should be retitled from the log, not keep the old custom name
        #expect(result.sessions.first?.title == "refactor the auth module")
    }

    // MARK: - Out-of-scope panes untouched

    @Test func migrationDoesNotRetitleShellPane() {
        // Shell panes must never enter automatic titling, even with a titled user log.
        let ws = Workspace(id: "ws1", name: "Test", path: "/tmp")
        var session = RunSession(workspaceId: "ws1", title: "Terminal", kind: "shell", provider: "claude")
        session.titleMode = nil
        session.logs = [LogEntry(kind: "user", text: "ls -la")]
        let snapshot = AppSnapshot(workspaces: [ws], sessions: [session])
        let result = StateRepository.normalize(snapshot, restoring: true)
        // Title must remain exactly as saved — no auto-retitle for shell panes.
        #expect(result.sessions.first?.title == "Terminal")
    }

    @Test func migrationDoesNotRetitleBrowserPane() {
        // Browser panes must never enter automatic titling.
        let ws = Workspace(id: "ws1", name: "Test", path: "/tmp")
        var session = RunSession(workspaceId: "ws1", title: "Browser", kind: "browser", provider: "claude")
        session.titleMode = nil
        session.logs = [LogEntry(kind: "user", text: "navigate to example.com")]
        let snapshot = AppSnapshot(workspaces: [ws], sessions: [session])
        let result = StateRepository.normalize(snapshot, restoring: true)
        // Title must remain exactly as saved — no auto-retitle for browser panes.
        #expect(result.sessions.first?.title == "Browser")
    }

    @Test func migrationRetitlesAgentButNotShellInSameSnapshot() {
        // In a snapshot with both an agent pane and a shell pane, only the agent pane
        // gets retitled; the shell pane title is preserved unchanged.
        let ws = Workspace(id: "ws1", name: "Test", path: "/tmp")
        var agentSession = RunSession(workspaceId: "ws1", title: "Claude", kind: "claude", provider: "claude")
        agentSession.titleMode = nil
        agentSession.logs = [LogEntry(kind: "user", text: "explain closures")]
        var shellSession = RunSession(workspaceId: "ws1", title: "My Shell", kind: "shell", provider: "claude")
        shellSession.titleMode = nil
        shellSession.logs = [LogEntry(kind: "user", text: "git status")]
        let snapshot = AppSnapshot(workspaces: [ws], sessions: [agentSession, shellSession])
        let result = StateRepository.normalize(snapshot, restoring: true)
        let agent = result.sessions.first { $0.kind == "claude" }
        let shell = result.sessions.first { $0.kind == "shell" }
        #expect(agent?.title == "explain closures")
        #expect(shell?.title == "My Shell")
    }
}
