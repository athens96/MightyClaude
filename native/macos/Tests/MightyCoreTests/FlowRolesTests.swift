import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// Who a flow block is (`FlowRoles`): one ink per role, a custom agent's own colour, the type chip, the marks, the
/// model before a block's own responses name it, and which of the chips a header has room for.
struct FlowRolesTests {
    private func agent(_ title: String = "Map the repo", type: String? = nil, requested: String? = nil, resolved: String? = nil,
                       isolation: String? = nil, background: Bool = false, kind: String? = nil) -> MightyGraphAgent {
        let identity = GraphAgentIdentity(type: type, requestedModel: requested, resolvedModel: resolved, isolation: isolation)
        return MightyGraphAgent(id: "a", title: title, kind: kind, background: background ? true : nil, identity: identity.isEmpty ? nil : identity)
    }

    @Test func theFourRolesTakeFourInksThatDifferInBothAppearances() {
        let inks = FlowRole.allCases.map(FlowRoles.ink)
        #expect(Set(inks).count == 4)
        #expect(FlowRoles.ink(.subagent) == .agent)
        // A background command is none of the agents: the palette's secondary ink, not the lead's teal.
        #expect(FlowRoles.ink(.task) == .neutral && FlowRoles.ink(.task) != FlowRoles.ink(.lead))
        for palette in [DesignTokens.light, DesignTokens.dark] {
            let colours = inks.map { palette[keyPath: $0.token] }
            #expect(Set(colours).count == 4)
            // Words on a card: each is one of the palette's AA inks.
            for colour in colours { #expect(colour.contrast(with: palette.card) >= 4.5) }
        }
    }

    @Test func everyClaudeColourIsAPaletteInkAndAnUnknownOneIsNone() {
        #expect(FlowRoles.colors.count == 8)
        for colour in FlowRoles.colors { #expect(FlowRoles.ink(color: colour) != nil, "\(colour)") }
        #expect(FlowRoles.ink(color: "red") == .err)
        #expect(FlowRoles.ink(color: "blue") == .accent)
        #expect(FlowRoles.ink(color: "green") == .done)
        #expect(FlowRoles.ink(color: "yellow") == .wait)
        #expect(FlowRoles.ink(color: "purple") == .agent)
        #expect(FlowRoles.ink(color: "orange") == .question)
        #expect(FlowRoles.ink(color: "pink") == .steer)
        #expect(FlowRoles.ink(color: " Cyan ") == .task)
        #expect(FlowRoles.ink(color: "teal") == nil)
        #expect(FlowRoles.ink(color: nil) == nil)
        // Six of the eight are told apart; the two ambers are one value in today's palette.
        #expect(Set(FlowRoles.colors.compactMap { FlowRoles.ink(color: $0) }.map { DesignTokens.light[keyPath: $0.token] }).count == 7)
    }

    @Test func aCustomColourReplacesOnlyTheSubagentInk() {
        #expect(FlowRoles.ink(.subagent, color: "green") == .done)
        #expect(FlowRoles.ink(.subagent, color: "mauve") == .agent)
        #expect(FlowRoles.ink(.lead, color: "green") == FlowRoles.ink(.lead))
        #expect(FlowRoles.ink(.advisor, color: "green") == FlowRoles.ink(.advisor))
    }

    @Test func theLeadShowsThePanesEffortOnlyWhenItNamedOne() {
        LocaleOverride.$language.withValue(.ko) {
            var run = MightyGraphRun(id: "r", configuredEffort: "high")
            let lead = FlowRoles.lead(run)
            #expect(lead.role == .lead)
            #expect(lead.ink == FlowRoles.ink(.lead))
            #expect(lead.effort == "High")
            #expect(lead.summary == "메인 · 사고 강도 · High")
            #expect(lead.marks.isEmpty && lead.typeChip == nil && lead.modelLabel == nil)
            run.configuredEffort = "default"
            #expect(FlowRoles.lead(run).effort == nil)
            #expect(FlowRoles.lead(run).summary == "메인")
            run.configuredEffort = nil
            #expect(FlowRoles.lead(run).effort == nil)
            // pi's levels are efforts too.
            run.configuredEffort = "minimal"
            #expect(FlowRoles.lead(run).effort == "Minimal")
        }
    }

    @Test func aSubagentSaysItsTypeItsModelAndHowItRuns() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let block = agent(type: "Explore", requested: "haiku", background: true)
            let who = try #require(FlowRoles.subagent(block))
            #expect(who.role == .subagent)
            #expect(who.ink == .agent)
            #expect(who.type == "Explore")
            #expect(who.typeChip == "Explore")
            // Asked for, not yet said to run: marked as requested.
            #expect(who.modelLabel == "haiku · 요청")
            #expect(who.marks == [.background])
            #expect(who.summary == "하위 에이전트 · Explore · 요청한 모델 Haiku · 백그라운드")

            // The model the CLI resolved outranks the alias that was asked for.
            let launched = try #require(FlowRoles.subagent(agent(type: "Explore", requested: "haiku", resolved: "claude-haiku-5-5")))
            #expect(launched.modelLabel == "claude-haiku-5-5")
            #expect(launched.summary == "하위 에이전트 · Explore · Haiku 5.5")
            #expect(launched.marks.isEmpty)
        }
    }

    @Test func aForkAndAWorktreeAreMarksAndAForkHasNoTypeChip() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let fork = try #require(FlowRoles.subagent(agent("fork count a", type: "fork", resolved: "claude-haiku-5-5", background: true)))
            #expect(fork.marks == [.background, .fork])
            #expect(fork.typeChip == nil)
            #expect(fork.type == "fork")
            #expect(fork.summary == "하위 에이전트 · Haiku 5.5 · 백그라운드 · 포크")
            let isolated = try #require(FlowRoles.subagent(agent(type: "general-purpose", isolation: "worktree")))
            #expect(isolated.marks == [.worktree])
            #expect(isolated.typeChip == "general-purpose")
            // A remote isolation is no worktree.
            #expect(try #require(FlowRoles.subagent(agent(type: "general-purpose", isolation: "remote"))).marks.isEmpty)
        }
    }

    @Test func theChipDoesNotRepeatATitleThatIsTheTypeAndNamesAPluginAgentByItsOwnName() throws {
        #expect(try #require(FlowRoles.subagent(agent("Explore", type: "Explore"))).typeChip == nil)
        let plugin = try #require(FlowRoles.subagent(agent("Fix the build", type: "oh-my-claudecode:executor")))
        #expect(plugin.typeChip == "executor")
        #expect(plugin.type == "oh-my-claudecode:executor")
        #expect(try #require(FlowRoles.subagent(agent("executor", type: "oh-my-claudecode:executor"))).typeChip == nil)
        #expect(FlowRoles.typeName("a:b:c") == "c")
        #expect(FlowRoles.typeName("plain") == "plain")
        #expect(FlowRoles.typeName("trailing:") == "trailing:")
    }

    @Test func aDefinitionGivesItsColourAndStandsInForAModelTheCallDidNotName() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let definition = AgentDefinition(name: "reviewer", color: "green", model: "sonnet")
            let who = try #require(FlowRoles.subagent(agent(type: "reviewer"), definition: definition))
            #expect(who.ink == .done)
            #expect(who.modelLabel == "sonnet · 요청")
            // The call's own model wins over the definition's; "inherit" asks for none.
            #expect(try #require(FlowRoles.subagent(agent(type: "reviewer", requested: "opus"), definition: definition)).modelLabel == "opus · 요청")
            let inherit = AgentDefinition(name: "reviewer", color: "plaid", model: "inherit")
            let plain = try #require(FlowRoles.subagent(agent(type: "reviewer"), definition: inherit))
            #expect(plain.modelLabel == nil)
            #expect(plain.ink == .agent)
            #expect(plain.summary == "하위 에이전트 · reviewer")
        }
    }

    @Test func onlyASubagentBlockHasAnIdentity() {
        #expect(FlowRoles.subagent(agent()) != nil)
        // A block that reported nothing about itself is still a sub-agent: its role's ink, no chip.
        #expect(FlowRoles.subagent(agent())?.typeChip == nil)
        for kind in MightyGraphSupport.childKinds { #expect(FlowRoles.subagent(agent(kind: kind)) == nil, "\(kind)") }
    }

    @Test func theModelLabelPrefersWhatTheCLIResolved() {
        LocaleOverride.$language.withValue(.ko) {
            #expect(GraphModelLabel.agentModelLabel(resolved: "claude-opus-5-5", requested: "opus") == "claude-opus-5-5")
            #expect(GraphModelLabel.agentModelLabel(resolved: nil, requested: "opus") == "opus · 요청")
            #expect(GraphModelLabel.agentModelLabel(resolved: "", requested: "") == nil)
            #expect(GraphModelLabel.agentModelLabel(resolved: nil, requested: "inherit") == nil)
            // The capsule reads both marked labels through the model's display name.
            #expect(ModelUsageFormat.versionedNodeLabel("opus · 요청") == "Opus · 요청")
            #expect(ModelUsageFormat.versionedNodeLabel("opus · 설정") == "Opus · 설정")
            #expect(ModelUsageFormat.versionedNodeLabel("claude-haiku-5-5") == "Haiku 5.5")
            #expect(ModelUsageFormat.blockCapsule(usage: nil, records: [], nodeModelLabel: "haiku · 요청", versioned: true) == "Haiku · 요청")
        }
    }

    @Test func theTypesOfTheRunsAreTheOnesTheirBlocksName() {
        let runs = [MightyGraphRun(id: "one", agents: [agent(type: "Explore"), agent(type: "reviewer"), agent()]),
                    MightyGraphRun(id: "two", agents: [agent(type: "Explore")])]
        #expect(FlowRoles.types(runs) == ["Explore", "reviewer"])
        #expect(FlowRoles.types([]).isEmpty)
    }

    // MARK: Header room

    private let who = FlowIdentity(role: .subagent, ink: .agent, type: "Explore", typeChip: "Explore", marks: [.background, .worktree], summary: "")

    @Test func aHeaderAtTheBlocksLeastWidthKeepsItsTitleStatusAndTokensAndShowsNoChip() {
        let fit = FlowRoles.headerFit(width: CGFloat(MightyGraphBlockSize.minimumWidth), title: "Map the repository layout", status: "실행 중",
                                      capsule: "12.3K · Haiku 5.5", buttons: 1, identity: who)
        #expect(fit == .none)
    }

    @Test func aWideHeaderShowsTheMarksAndTheTypeChip() {
        let fit = FlowRoles.headerFit(width: 520, title: "Map the repository layout", status: "실행 중", capsule: "12.3K · Haiku 5.5", buttons: 1, identity: who)
        #expect(fit.showsMarks)
        #expect((fit.typeWidth ?? 0) >= FlowRoles.typeFloor)
        #expect((fit.typeWidth ?? .infinity) <= FlowRoles.typeCeiling)
    }

    @Test func theMarksComeBeforeTheTypeChipAsTheHeaderWidens() {
        var seenMarksAlone = false
        var previous = FlowRoles.HeaderFit.none
        for width in stride(from: CGFloat(300), through: 700, by: 4) {
            let fit = FlowRoles.headerFit(width: width, title: "Map the repository layout", status: "실행 중", capsule: "12.3K · Haiku 5.5", buttons: 1, identity: who)
            if fit.showsMarks, fit.typeWidth == nil { seenMarksAlone = true }
            // Nothing a narrower header showed goes away in a wider one, and the chip only grows.
            if previous.showsMarks { #expect(fit.showsMarks) }
            if let before = previous.typeWidth { #expect((fit.typeWidth ?? 0) >= before) }
            // The type never shows without the marks.
            if fit.typeWidth != nil { #expect(fit.showsMarks) }
            previous = fit
        }
        #expect(seenMarksAlone)
        #expect(previous.typeWidth != nil)
    }

    @Test func aBlockWithoutAStatusOrTokensHasMoreRoom() {
        let busy = FlowRoles.headerFit(width: 360, title: "Map", status: "실행 중", capsule: "12.3K · Haiku 5.5 +1", buttons: 1, identity: who)
        let quiet = FlowRoles.headerFit(width: 360, title: "Map", status: nil, capsule: nil, buttons: 1, identity: who)
        #expect(quiet.typeWidth != nil)
        #expect((quiet.typeWidth ?? 0) >= (busy.typeWidth ?? 0))
    }

    @Test func theLeadsEffortShowsWhenThereIsRoom() {
        let lead = FlowIdentity(role: .lead, ink: .task, effort: "High", summary: "")
        #expect(!FlowRoles.headerFit(width: 300, title: "실행 · 요청 12", status: "턴 완료 · 백그라운드 2개 실행 중", capsule: "120.4K · Opus 5.5", buttons: 1, identity: lead).showsEffort)
        #expect(FlowRoles.headerFit(width: 500, title: "실행 · 요청 12", status: "실행 중", capsule: "120.4K · Opus 5.5", buttons: 1, identity: lead).showsEffort)
        // Nothing to show is nothing shown.
        let bare = FlowIdentity(role: .lead, ink: .task, summary: "")
        #expect(FlowRoles.headerFit(width: 900, title: "실행", status: nil, capsule: nil, buttons: 1, identity: bare) == .none)
    }

    @Test func aWideCharacterCountsAFullEm() {
        #expect(FlowRoles.estimatedWidth("실행 중", size: 10) > FlowRoles.estimatedWidth("abc d", size: 10))
        #expect(FlowRoles.estimatedWidth("", size: 10) == 0)
        #expect(FlowRoles.estimatedWidth("aaaa", size: 10, monospaced: true) > FlowRoles.estimatedWidth("aaaa", size: 10))
    }
}
