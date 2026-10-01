import Foundation
import Testing
@testable import MightyCore

/// The agent pane hero's figures: only numbers the pane holds, in a fixed order.
struct PaneHeroTests {
    static func tool(_ id: String, kind: String = "tool", state: String = "completed") -> LogEntry {
        LogEntry(id: id, kind: "system", text: id, activity: AgentActivity(id: id, provider: "claude", kind: kind, state: state, toolName: "Read", summary: id))
    }

    static func usage(provider: String = "claude", used: Int? = 82_000, window: Int? = 200_000, cost: Double? = 0.384) -> SessionUsage {
        SessionUsage(provider: provider, source: "test", contextUsedTokens: used, contextWindowTokens: window, costUSD: cost)
    }

    @Test func toolCountStartsAtTheLatestRequest() {
        let logs = [
            LogEntry(kind: "user", text: "first"),
            Self.tool("a"), Self.tool("b"),
            LogEntry(kind: "assistant", text: "done"),
            LogEntry(kind: "user", text: "second"),
            Self.tool("c", state: "running"), Self.tool("d", state: "error"),
            // The turn's own lifecycle is not a tool call.
            Self.tool("turn", kind: "turn"),
            LogEntry(kind: "assistant", text: "reply"),
        ]
        #expect(PaneHero.toolCount(logs) == 2)
    }

    @Test func toolCountIsZeroAfterARequestWithoutToolsAndAbsentBeforeAny() {
        #expect(PaneHero.toolCount([Self.tool("a"), LogEntry(kind: "user", text: "hi")]) == 0)
        #expect(PaneHero.toolCount([Self.tool("a")]) == nil)
        #expect(PaneHero.toolCount([]) == nil)
    }

    @Test func figuresKeepOrderAndRoundTheContext() {
        let timing = AgentRunTiming(startedAt: Date(timeIntervalSince1970: 1_000), finishedAt: Date(timeIntervalSince1970: 1_134))
        let session = RunSession(workspaceId: "w", title: "t", status: "completed",
                                 logs: [LogEntry(kind: "user", text: "go"), Self.tool("a")], runTiming: timing, sessionUsage: Self.usage())
        #expect(PaneHero.figures(session) == [.elapsed(timing), .context(41), .cost(0.384), .tools(1)])
    }

    @Test func missingNumbersAreLeftOutNotZeroed() {
        let bare = RunSession(workspaceId: "w", title: "t")
        #expect(PaneHero.figures(bare).isEmpty)
        // No cost and no context window reported: neither figure appears.
        let partial = RunSession(workspaceId: "w", title: "t", sessionUsage: Self.usage(window: nil, cost: nil))
        #expect(PaneHero.figures(partial).isEmpty)
        // Negative or non-finite costs are not money the session spent.
        let broken = RunSession(workspaceId: "w", title: "t", sessionUsage: Self.usage(used: nil, cost: -.infinity))
        #expect(PaneHero.figures(broken).isEmpty)
    }

    @Test func aReadingFromAnotherProviderIsIgnored() {
        let session = RunSession(workspaceId: "w", title: "t", provider: "codex", sessionUsage: Self.usage(provider: "claude"))
        #expect(PaneHero.figures(session).isEmpty)
    }

    @Test func anInvalidClockIsLeftOut() {
        let backwards = AgentRunTiming(startedAt: Date(timeIntervalSince1970: .infinity))
        let session = RunSession(workspaceId: "w", title: "t", runTiming: backwards)
        #expect(PaneHero.figures(session).isEmpty)
    }

    @Test func costReadsLikeThePhone() {
        #expect(PaneHero.cost(0.384) == "$0.38")
        #expect(PaneHero.cost(0) == "$0.00")
        #expect(PaneHero.cost(12.5) == "$12.50")
    }
}
