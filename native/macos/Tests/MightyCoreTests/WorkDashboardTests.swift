import Foundation
import Testing
@testable import MightyCore

/// The "작업 현황" numbers: what the tiles, the sidebar badges and the cards count,
/// read only from what the app holds (mirrors the phone's `dashboard.test.ts`).
struct WorkDashboardTests {
    static func pane(_ id: String, workspace: String = "w1", kind: String = "claude", provider: String = "claude", model: String = "default",
                     status: String = "idle", logs: [LogEntry] = [], createdAt: String = "2026-10-01T09:00:00.000Z",
                     timing: AgentRunTiming? = nil, usage: SessionUsage? = nil) -> RunSession {
        RunSession(id: id, workspaceId: workspace, title: "pane " + id, kind: kind, provider: provider, model: model,
                   status: status, logs: logs, createdAt: createdAt, runTiming: timing, sessionUsage: usage)
    }

    static func request(_ id: String, question: Bool = false, state: String = "pending") -> ToolPermissionRequest {
        ToolPermissionRequest(id: id, runId: "r", toolUseId: id, toolName: question ? "AskUserQuestion" : "Bash", inputJSON: "{}",
                              summary: "s", state: state, canAnswerQuestions: question)
    }

    static func date(_ text: String) -> Date { AgentRunTiming.parseTimestamp(text)! }

    @Test func statsCountRunningPanesWaitingRequestsAndFinishedPanes() {
        let sessions = [
            Self.pane("run", status: "running"),
            Self.pane("asking", status: "running"),
            Self.pane("done", status: "completed"),
            Self.pane("err", status: "error"),
            Self.pane("shell", kind: "shell", status: "running"),
            // An agent's own terminal and the files pane are not counted, as on the phone.
            Self.pane("io", kind: AgentIOPaneKind.terminal, status: "running"),
            Self.pane("files", kind: FilePaneKind.kind, status: "completed"),
        ]
        let permissions = [
            "asking": [Self.request("q1", question: true), Self.request("q2", question: true), Self.request("p1")],
            "done": [Self.request("old", state: "allowed")],
            "io": [Self.request("p2")],
        ]
        let stats = WorkDashboard.stats(sessions: sessions, permissions: permissions)
        // A pane waiting on a question still counts as running; requests, not panes, wait.
        #expect(stats == WorkDashboard.Stats(running: 3, waiting: 3, done: 1))
    }

    @Test func statsAreZeroWithoutPanes() {
        #expect(WorkDashboard.stats(sessions: [], permissions: [:]) == WorkDashboard.Stats())
    }

    @Test func attentionCountsOnlyPendingRequestsAndSplitsQuestions() {
        let attention = WorkDashboard.attention([Self.request("a", question: true), Self.request("b"), Self.request("c", state: "denied")])
        #expect(attention == WorkDashboard.Attention(questions: 1, permissions: 1))
        #expect(attention.total == 2)
        #expect(WorkDashboard.attention(nil).total == 0)
    }

    @Test func aPaneHoldingARequestShowsAsWaiting() {
        #expect(WorkDashboard.displayStatus("running", attention: .init(questions: 1)) == "waiting")
        #expect(WorkDashboard.displayStatus("error", attention: .init(permissions: 2)) == "waiting")
        #expect(WorkDashboard.displayStatus("completed", attention: .init()) == "completed")
    }

    @Test func workspaceBadgesSeparateWaitingFromRunning() {
        let sessions = [
            Self.pane("a", status: "running"),
            Self.pane("b", status: "running"),
            Self.pane("c", status: "error"),
            Self.pane("d", status: "failed"),
            Self.pane("e", status: "completed"),
        ]
        let badges = WorkDashboard.badges(sessions: sessions, permissions: ["b": [Self.request("q", question: true), Self.request("p")]])
        #expect(badges == WorkDashboard.Badges(questions: 1, permissions: 1, errors: 2, running: 1, done: 1))
    }

    @Test func badgesAlsoCountWhatHasSettledForTheWorkspaceHeader() {
        let sessions = [
            Self.pane("done", status: "completed"),
            Self.pane("stopped", status: "stopped"),
            Self.pane("shell", kind: "shell"),
            Self.pane("asking", status: "completed"),
            // An agent's own terminal is not counted, as everywhere else.
            Self.pane("io", kind: AgentIOPaneKind.terminal),
        ]
        let badges = WorkDashboard.badges(sessions: sessions, permissions: ["asking": [Self.request("q", question: true)]])
        // A pane holding a request is counted once, as waiting, not also as done.
        #expect(badges == WorkDashboard.Badges(questions: 1, done: 1, stopped: 1, idle: 1))
    }

    @Test func cardReadsContextOnlyFromThePanesOwnProvider() {
        let own = SessionUsage(provider: "codex", source: "test", contextUsedTokens: 41, contextWindowTokens: 100)
        let card = WorkDashboard.card(Self.pane("a", provider: "codex", model: "gpt-5-codex", usage: own), permissions: nil)
        #expect(card.contextPercent == 41)
        #expect(card.model == "gpt-5-codex")
        let other = SessionUsage(provider: "claude", source: "test", contextUsedTokens: 41, contextWindowTokens: 100)
        #expect(WorkDashboard.card(Self.pane("b", provider: "codex", usage: other), permissions: nil).contextPercent == nil)
        let windowless = SessionUsage(provider: "claude", source: "test", contextUsedTokens: 41)
        #expect(WorkDashboard.card(Self.pane("c", usage: windowless), permissions: nil).contextPercent == nil)
    }

    @Test func cardHidesTheDefaultModelAndAnInvalidClock() {
        let start = Self.date("2026-10-01T10:00:00Z")
        let valid = AgentRunTiming(startedAt: start, finishedAt: start.addingTimeInterval(134))
        let card = WorkDashboard.card(Self.pane("a", timing: valid), permissions: nil)
        #expect(card.model == nil)
        #expect(card.timing == valid)
        #expect(card.contextPercent == nil)
    }

    @Test func cardTakesItsStatusToneAndAgeFromThePane() {
        let logs = [LogEntry(kind: "user", text: "go", timestamp: "2026-10-01T10:00:00.000Z"),
                    LogEntry(kind: "assistant", text: "done", timestamp: "2026-10-01T10:05:00.000Z")]
        let card = WorkDashboard.card(Self.pane("a", status: "running", logs: logs), permissions: [Self.request("q", question: true)])
        #expect(card.displayStatus == "waiting")
        #expect(card.tone == .wait)
        #expect(card.updatedAt == Self.date("2026-10-01T10:05:00Z"))
        let empty = WorkDashboard.card(Self.pane("b"), permissions: nil)
        #expect(empty.updatedAt == Self.date("2026-10-01T09:00:00Z"))
        #expect(empty.lastActivity == nil)
    }

    @Test func lastActivityPrefersTheNewestToolCallOrMessageLine() {
        let tool = AgentActivity(provider: "claude", kind: "tool", state: "running", toolName: "Bash", summary: "npx playwright screenshot")
        #expect(WorkDashboard.lastActivity([LogEntry(kind: "assistant", text: "earlier"), LogEntry(kind: "output", text: "", activity: tool)])
                == .init(text: "Bash · npx playwright screenshot", isError: false, tool: "Bash"))
        let failed = AgentActivity(provider: "claude", kind: "tool", state: "error", toolName: "Bash", summary: "npm run build")
        #expect(WorkDashboard.lastActivity([LogEntry(kind: "output", text: "", activity: failed)])?.isError == true)
        #expect(WorkDashboard.lastActivity([LogEntry(kind: "assistant", text: "\n  first line  \nsecond"), LogEntry(kind: "assistant", text: "   ")])
                == .init(text: "first line", isError: false))
        #expect(WorkDashboard.lastActivity([LogEntry(kind: "error", text: "boom")]) == .init(text: "boom", isError: true))
        #expect(WorkDashboard.lastActivity([]) == nil)
    }

    @Test func orderPutsWaitingFirstThenRunningErrorsFinishedStoppedIdle() {
        func card(_ id: String, _ status: String, _ updated: String, questions: Int = 0) -> WorkDashboard.Card {
            var value = WorkDashboard.card(Self.pane(id, status: status, createdAt: updated), permissions: nil)
            value.attention = .init(questions: questions)
            value.displayStatus = WorkDashboard.displayStatus(status, attention: value.attention)
            return value
        }
        let cards = [
            card("idle", "idle", "2026-10-01T12:00:00.000Z"),
            card("stop", "stopped", "2026-10-01T11:00:00.000Z"),
            card("doneOld", "completed", "2026-10-01T08:00:00.000Z"),
            card("doneNew", "completed", "2026-10-01T09:00:00.000Z"),
            card("err", "error", "2026-10-01T07:00:00.000Z"),
            card("run", "running", "2026-10-01T06:00:00.000Z"),
            card("wait", "running", "2026-10-01T05:00:00.000Z", questions: 1),
        ]
        #expect(WorkDashboard.ordered(cards).map(\.id) == ["wait", "run", "err", "doneNew", "doneOld", "stop", "idle"])
    }

    @Test func orderKeepsTheGivenOrderForEqualRankAndTime() {
        let cards = ["a", "b", "c"].map { WorkDashboard.card(Self.pane($0), permissions: nil) }
        #expect(WorkDashboard.ordered(cards).map(\.id) == ["a", "b", "c"])
    }

    @Test func clockReadsLikeTheRunTimingLabel() {
        #expect(WorkDashboard.clock(0) == "00:00")
        #expect(WorkDashboard.clock(134.9) == "02:14")
        #expect(WorkDashboard.clock(3_729) == "1:02:09")
        #expect(WorkDashboard.clock(-5) == "00:00")
        #expect(WorkDashboard.clock(.nan) == "00:00")
        #expect(WorkDashboard.clock(.infinity) == "00:00")
        let start = Self.date("2026-10-01T10:00:00Z")
        let timing = AgentRunTiming(startedAt: start, finishedAt: start.addingTimeInterval(408))
        #expect(WorkDashboard.clock(timing.elapsed()) == timing.label())
    }

    @Test func ageUsesTheLargestWholeUnit() {
        let now = Self.date("2026-10-02T12:00:00Z")
        #expect(WorkDashboard.age(of: now.addingTimeInterval(-30), now: now) == .now)
        #expect(WorkDashboard.age(of: now.addingTimeInterval(-11 * 60), now: now) == .minutes(11))
        #expect(WorkDashboard.age(of: now.addingTimeInterval(-2 * 3_600 - 59), now: now) == .hours(2))
        #expect(WorkDashboard.age(of: now.addingTimeInterval(-26 * 3_600), now: now) == .days(1))
        // A date in the future reads as now, never a negative age.
        #expect(WorkDashboard.age(of: now.addingTimeInterval(600), now: now) == .now)
    }

    @Test func sidebarMetaNamesTheProviderThenOnlyNumbersTheAppHas() {
        let now = Self.date("2026-10-01T10:20:00Z")
        let start = Self.date("2026-10-01T10:00:00Z")
        let usage = SessionUsage(provider: "claude", source: "test", contextUsedTokens: 406, contextWindowTokens: 1_000)
        let running = WorkDashboard.card(Self.pane("a", status: "running", timing: AgentRunTiming(startedAt: start), usage: usage), permissions: nil)
        #expect(WorkDashboard.sidebarMeta(running, now: now) == [.provider, .elapsed, .context(41)])

        // A pane waiting on a question is still running: its clock and context stay.
        let asking = WorkDashboard.card(Self.pane("q", status: "running", timing: AgentRunTiming(startedAt: start), usage: usage),
                                        permissions: [Self.request("q1", question: true)])
        #expect(WorkDashboard.sidebarMeta(asking, now: now) == [.provider, .elapsed, .context(41)])

        let logs = [LogEntry(kind: "assistant", text: "ok", timestamp: "2026-10-01T10:09:00.000Z")]
        let finished = WorkDashboard.card(Self.pane("b", status: "completed", logs: logs,
                                                    timing: AgentRunTiming(startedAt: start, finishedAt: start.addingTimeInterval(408)), usage: usage), permissions: nil)
        // At rest only how long ago it was active, as on the status-v2 row ("Codex · 11분 전").
        #expect(WorkDashboard.sidebarMeta(finished, now: now) == [.provider, .age(.minutes(11))])

        let bare = WorkDashboard.card(Self.pane("c", status: "running"), permissions: nil)
        #expect(WorkDashboard.sidebarMeta(bare, now: now) == [.provider])

        let shell = WorkDashboard.card(Self.pane("d", kind: "shell", status: "running", timing: AgentRunTiming(startedAt: start)), permissions: nil)
        #expect(WorkDashboard.sidebarMeta(shell, now: now) == [])
    }

    @Test func onlyAnAgentRowNamesItsProviderSoOnlyItCarriesTheMark() {
        let now = Self.date("2026-10-01T10:20:00Z")
        // The sidebar draws the provider's mark at `.provider`: Claude and Codex rows get theirs.
        for provider in ["claude", "codex", "gemini"] {
            let agent = WorkDashboard.card(Self.pane("a", provider: provider, status: "running"), permissions: nil)
            #expect(WorkDashboard.sidebarMeta(agent, now: now).first == .provider)
            #expect(ProviderMark.markedProvider(agent.provider) == provider)
        }
        // A shell, a browser, an agent's own terminal or browser and the files pane name their
        // kind instead, with no provider part and so no mark, whatever provider they carry.
        for kind in [SessionKind.shell, SessionKind.browser, AgentIOPaneKind.terminal, AgentIOPaneKind.browser, FilePaneKind.kind] {
            let pane = WorkDashboard.card(Self.pane("b", kind: kind, provider: "codex", status: "running"), permissions: nil)
            #expect(WorkDashboard.sidebarMeta(pane, now: now) == [])
        }
    }

    @Test func anErrorRowSaysWhyBeforeHowLongAgo() {
        let now = Self.date("2026-10-01T10:20:00Z")
        // A failed tool call: "Claude · Bash 실패 · 3분 전".
        let failed = AgentActivity(provider: "claude", kind: "tool", state: "error", toolName: "Bash", summary: "npm run build")
        let tool = WorkDashboard.card(Self.pane("a", status: "error", logs: [LogEntry(kind: "output", text: "", timestamp: "2026-10-01T10:17:00.000Z", activity: failed)]),
                                      permissions: nil)
        #expect(WorkDashboard.sidebarMeta(tool, now: now) == [.provider, .reason("Bash", isTool: true), .age(.minutes(3))])

        // An error line: its own words, cut short so the age still fits.
        let long = String(repeating: "x", count: 60)
        let line = WorkDashboard.card(Self.pane("b", status: "error", logs: [LogEntry(kind: "error", text: long, timestamp: "2026-10-01T10:17:00.000Z")]),
                                      permissions: nil)
        let reason = String(repeating: "x", count: WorkDashboard.reasonLimit - 1) + "…"
        #expect(WorkDashboard.sidebarMeta(line, now: now) == [.provider, .reason(reason, isTool: false), .age(.minutes(3))])
        let short = WorkDashboard.card(Self.pane("c", status: "failed", logs: [LogEntry(kind: "error", text: "boom", timestamp: "2026-10-01T10:17:00.000Z")]),
                                       permissions: nil)
        #expect(WorkDashboard.sidebarMeta(short, now: now) == [.provider, .reason("boom", isTool: false), .age(.minutes(3))])

        // No reason without an error to point at, nor for a pane that only finished.
        let quiet = WorkDashboard.card(Self.pane("d", status: "error", logs: [LogEntry(kind: "assistant", text: "ok", timestamp: "2026-10-01T10:17:00.000Z")]),
                                       permissions: nil)
        #expect(WorkDashboard.sidebarMeta(quiet, now: now) == [.provider, .age(.minutes(3))])
        let done = WorkDashboard.card(Self.pane("e", status: "completed", logs: [LogEntry(kind: "error", text: "old", timestamp: "2026-10-01T10:17:00.000Z")]),
                                      permissions: nil)
        #expect(WorkDashboard.sidebarMeta(done, now: now) == [.provider, .age(.minutes(3))])
    }
}
