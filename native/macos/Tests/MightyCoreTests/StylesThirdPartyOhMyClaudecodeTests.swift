import Foundation
import Testing
@testable import MightyCore

/// `styles/oh-my-claudecode.json`, read from the repository the same way the
/// frozen `StyleGoldenContractTests` reads it. Completion criterion 2: a third
/// party style is a manifest plus tests, and not one line of engine or phone
/// code moves. Everything asserted here is a literal, so a later edit to the
/// manifest has to say so out loud.
///
/// The catalogue is grounded in what is actually installed on this machine:
/// the plugin key `oh-my-claudecode@omc` in `~/.claude/plugins/installed_plugins.json`,
/// its `skills/<name>/SKILL.md` directory, and the one MCP server its `.mcp.json`
/// declares (`t`), whose tools therefore travel as `mcp__plugin_oh-my-claudecode_t__<tool>`.
struct StylesThirdPartyOhMyClaudecodeTests {
    private let manifest: StyleManifest
    private let style: RegisteredStyle
    private let data: Data

    /// Decoding **and** validating as `.user` is the first assertion: a bundled
    /// source would waive the reserved id, reserved name and `ToolSearch`
    /// judgements (§2), and this manifest is never bundled.
    init() throws {
        data = try Data(contentsOf: Self.url)
        manifest = try StyleManifestDecoder.decode(data, source: .user)
        style = RegisteredStyle(manifest: manifest, source: .user, path: Self.url.path, workspacePath: nil,
                                hash: StyleHash.of(data), approval: .approved)
    }

    private static var url: URL { StyleGolden.stylesDirectory.appendingPathComponent("oh-my-claudecode.json") }
    private var evaluator: StyleEvaluator { style.evaluator }
    private func phase(_ id: String) -> StylePhase? { manifest.phase(id) }

    /// The 16 skills this style offers, in manifest order. Every one of them is
    /// a directory under the installed plugin's `skills/`.
    private static let actionIds = ["plan", "ralplan", "deep-interview", "execute", "autopilot", "ralph", "team",
                                    "review", "verify", "research", "external-context", "trace", "debug",
                                    "wiki", "remember", "cancel"]

    @Test func catalogueAndFlow() {
        #expect(manifest.id == "oh-my-claudecode" && manifest.name == "oh-my-claudecode")
        #expect(manifest.actions.map(\.id) == Self.actionIds)
        #expect(manifest.groups.map(\.id) == ["flow"])
        // One group, flat: `next` is `byPhase`, so a group map is never drawn and
        // extra groups would be dead JSON that only the approval card shows (§1.4).
        #expect(manifest.group("flow")?.actions == Self.actionIds && !evaluator.drawsGroupMap() && evaluator.drawsPhaseProgress)
        #expect(manifest.phases.map(\.id) == ["goal", "plan", "execute", "review", "verify"])
        #expect(manifest.orderedPhases.map(\.title) == ["목표", "계획", "실행", "리뷰", "검증"])
        // An entry phase of its own, so `next.map` has no dead row: a phase that
        // is also `rules.start.phase` never draws its `next` list, because
        // `visibleActions` prefers the start actions (§1.6).
        #expect(evaluator.startActions(phase: phase("goal")).map(\.id) == ["plan", "ralplan", "deep-interview", "autopilot", "research"])
        #expect(evaluator.startActions(phase: phase("plan")).isEmpty && evaluator.resetTitle == "새 목표")
        #expect(evaluator.nextActions(phase: phase("goal"), group: nil).isEmpty)
        #expect(evaluator.nextActions(phase: phase("plan"), group: nil).map(\.id) == ["execute", "ralplan", "review", "research"])
        #expect(evaluator.nextActions(phase: phase("execute"), group: nil).map(\.id) == ["review", "verify", "trace", "debug"])
        #expect(evaluator.nextActions(phase: phase("review"), group: nil).map(\.id) == ["verify", "execute", "trace"])
        #expect(evaluator.nextActions(phase: phase("verify"), group: nil).map(\.id) == ["execute", "review", "remember"])
        // Cross-cutting lanes carry no phase, so calling one never rewinds the flow.
        let phaseless = manifest.actions.filter { $0.phase == nil }.map(\.id)
        #expect(phaseless == ["research", "external-context", "trace", "debug", "wiki", "remember", "cancel"])
        // The loops and the interview are the human's to start; review and verify
        // never author the change they judge (skills/review/SKILL.md).
        #expect(Set(manifest.actions.filter { $0.flags.contains(.userInvoked) }.map(\.id))
                == ["deep-interview", "autopilot", "ralph", "team", "cancel"])
        #expect(Set(manifest.actions.filter { $0.flags.contains(.readOnly) }.map(\.id))
                == ["review", "verify", "external-context"])
        #expect(manifest.actions.allSatisfy { $0.takesText && $0.foldText == .trimOnly && $0.icon != nil && $0.glyph == nil })
        #expect(manifest.aliases.map(\.name) == ["drydock", "deepinit", "launch", "ultragoal", "autoresearch", "ai-slop-cleaner", "visual-verdict"])
    }

    @Test func promptsAndRecognition() {
        #expect(evaluator.prompt(actionId: "plan", text: "  결제 모듈 리팩터링 ") == "/oh-my-claudecode:plan 결제 모듈 리팩터링")
        // `trimOnly` keeps the paragraph a Mac composer can hold; only the ends go.
        #expect(evaluator.prompt(actionId: "execute", text: " 첫 줄\n\n 둘째 줄 ") == "/oh-my-claudecode:execute 첫 줄\n\n 둘째 줄")
        // An empty text takes the space before `{text}` with it (§1.3.1), and
        // `requiresText` is only a UI hint, so the bare prompt is still legal.
        #expect(evaluator.prompt(actionId: "review", text: "   ") == "/oh-my-claudecode:review")
        #expect(evaluator.prompt(actionId: "plan", text: "\n \n") == "/oh-my-claudecode:plan")
        #expect(evaluator.prompt(actionId: "no-such-skill", text: "") == nil)
        #expect(evaluator.recognised(inPrompt: "/oh-my-claudecode:execute 결제") == .action("execute"))
        #expect(evaluator.recognised(inPrompt: "/oh-my-claudecode:launch 미션") == .alias(name: "launch", phase: "execute"))
        // Not this style's prefix, no name after the prefix, and plain prose.
        #expect(evaluator.recognised(inPrompt: "/review") == nil)
        #expect(evaluator.recognised(inPrompt: "/oh-my-claudecode:") == nil)
        #expect(evaluator.recognised(inPrompt: "계획 좀 세워줘") == nil)
        // A bare keyword is not recognised even though the action exists: the
        // rule needs a prefix and reads one word (followup 15).
        #expect(evaluator.recognised(inPrompt: "autopilot 결제 모듈") == nil)
        #expect(evaluator.currentPhase(prompts: [])?.id == "goal")
        #expect(evaluator.currentPhase(prompts: ["/oh-my-claudecode:plan 목표", "/oh-my-claudecode:wiki 결제"])?.id == "plan")
        #expect(evaluator.currentPhase(prompts: ["/oh-my-claudecode:execute", "그건 좀 다르게 해줘"])?.id == "execute")
        #expect(evaluator.currentPhase(prompts: ["/oh-my-claudecode:review", "/oh-my-claudecode:ultragoal 미션"])?.id == "execute")
    }

    /// A bare first draft becomes `/oh-my-claudecode:plan <draft>` rather than
    /// going out verbatim: the plugin's own routing sends a broad request to
    /// planning first, and `plan` takes the text, so the user's words are never
    /// discarded (`E_ENTER_ACTION_TEXT`). It is armed only in the entry phase,
    /// and only for a pane's first request or right after the reset chip — the
    /// two facts a manifest cannot make permanently true about itself (§1.6).
    @Test func enterRewritesOnlyTheFirstBareDraft() {
        #expect(evaluator.enterBehaviour(draft: "결제 모듈 리팩터링", phase: phase("goal"), hasAttachments: false,
                                         running: false, hasRequests: false) == .rewrite(actionId: "plan"))
        #expect(evaluator.enterArmedPrefix(draft: "결제 모듈 리팩터링", phase: phase("goal"), running: false, hasRequests: false)
                == "/oh-my-claudecode:plan")
        #expect(evaluator.enterArmedPrefix(draft: "결제 모듈 리팩터링", phase: phase("goal"), running: false, hasRequests: true) == nil)
        #expect(evaluator.enterArmedPrefix(draft: "결제 모듈 리팩터링", phase: phase("goal"), running: false,
                                           hasRequests: true, startingNew: true) == "/oh-my-claudecode:plan")
        // The chip never promises a rewrite this very Enter would not perform.
        #expect(evaluator.enterArmedPrefix(draft: "/help", phase: phase("goal"), running: false, hasRequests: false) == nil)
        #expect(evaluator.enterArmedPrefix(draft: "결제 모듈", phase: phase("plan"), running: false, hasRequests: false) == nil)
        #expect(evaluator.enterArmedPrefix(draft: "결제 모듈", phase: phase("goal"), hasAttachments: true, running: false, hasRequests: false) == nil)
        #expect(evaluator.enterArmedPrefix(draft: "결제 모듈", phase: phase("goal"), running: true, hasRequests: false) == nil)
        #expect(evaluator.placeholder(phase: phase("goal"), running: false, answering: false) == "무엇을 할까요? 목표를 적고 Enter로 계획을 시작하세요…")
        #expect(evaluator.placeholder(phase: phase("plan"), running: false, answering: false) == "이어서 요청하거나 위에서 다음 단계를 고르세요…")
        #expect(evaluator.guidanceLine(phase: phase("plan"), running: false)
                == "계획 단계입니다. 다음 단계를 고르거나, 아래에 적어 같은 대화를 이어가세요.")
        #expect(evaluator.guidanceLine(phase: phase("plan"), running: true) == "계획 진행 중 \u{00B7} 질문이 오면 여기에 표시됩니다")
        #expect(evaluator.guidanceLine(phase: phase("goal"), running: false)?.hasPrefix("무엇을 할까요?") == true)
    }

    /// Titles come from the registry, not from one pane's manifest, because a
    /// pane keeps its earlier request blocks when its style changes (§1.10).
    /// This style writes no `glyph`, so rule 2 never fires: an action with a
    /// phase is titled by the phase, and a phase-less one by its own title.
    @Test func requestTitlesIconsAndTints() throws {
        let registry = StyleRegistry(styles: BundledStyles.shared.styles() + [style, try Self.gstack()])
        #expect(evaluator.requestTitle(forInput: "/oh-my-claudecode:plan 목표") == "계획")
        #expect(evaluator.requestTitle(forInput: "/oh-my-claudecode:autopilot 결제") == "실행")
        #expect(evaluator.requestTitle(forInput: "/oh-my-claudecode:trace 로그인 실패") == "추적")
        #expect(evaluator.requestTitle(forInput: "/oh-my-claudecode:drydock") == "계획")
        #expect(evaluator.requestTitle(forInput: "무슨 일이 일어난 거죠") == nil)
        #expect(registry.requestTitle(forInput: "/oh-my-claudecode:verify", workspace: nil) == "검증")
        // `/review` belongs to gstack, whose bare `/name` rule reads it: the sweep
        // runs bundled first (Ouroboros needs `/ouroboros:`, Paperthin has no
        // `review` skill), then user styles by id, and `gstack` sorts before
        // `oh-my-claudecode`. This style's prefix could never match `/review`
        // anyway. Claude Code's own `/review` command is untouched — the engine
        // only titles the request block; the request itself still goes out as typed.
        #expect(registry.requestTitle(forInput: "/review", workspace: nil) == "🔎 PR 리뷰")
        #expect(registry.requestIcon(forInput: "/oh-my-claudecode:plan", workspace: nil)?.rawValue == "map")
        #expect(registry.requestTint(forInput: "/oh-my-claudecode:verify", workspace: nil) == .green)
        #expect(registry.requestTint(forInput: "/oh-my-claudecode:cancel", workspace: nil) == .red)
        // No action tint falls through to the style's own.
        #expect(registry.requestTint(forInput: "/oh-my-claudecode:plan", workspace: nil) == .indigo)
        // Unrecognised input gets no prefix, no icon and the app's own colour.
        #expect(registry.requestIcon(forInput: "안녕하세요", workspace: nil) == nil)
        #expect(registry.requestTint(forInput: "안녕하세요", workspace: nil) == .accent)
    }

    /// The plugin registry decides readiness, read from an injected home in a
    /// temporary directory — never the real `~/.claude`.
    @Test func prerequisitesReadTheInstalledPluginRegistry() throws {
        let home = StyleFixtures.temporaryDirectory("omc-home")
        defer { try? FileManager.default.removeItem(at: home) }
        func result() -> StylePrerequisiteResult {
            StylePrerequisiteProbe.evaluate(manifest.prerequisites, install: manifest.install,
                                            home: home, workspacePath: nil, environment: [:])
        }
        let empty = result()
        #expect(!empty.ready && empty.missing == ["oh-my-claudecode 플러그인이 설치되어 있지 않습니다"] && empty.canInstall)
        #expect(empty.hint?.hasSuffix("Enter는 직접 누르세요.") == true)
        let registry = home.appendingPathComponent(".claude/plugins/installed_plugins.json")
        try StyleFixtures.write(Data(#"{"version":1,"plugins":{"other@somewhere":[{"installPath":"/x"}]}}"#.utf8), to: registry)
        #expect(!result().ready)
        // The key this machine actually holds: `<plugin>@<marketplace>`, and the
        // probe prefix `oh-my-claudecode@` is what makes `plugin_oh-my-claudecode_t`
        // this style's own server under the ownership rule (§1.9).
        try Data(#"{"version":1,"plugins":{"oh-my-claudecode@omc":[{"installPath":"/x","scope":"user"}]}}"#.utf8).write(to: registry)
        #expect(result().ready)
        #expect(manifest.prerequisites.probes.compactMap(\.pluginName) == ["oh-my-claudecode"])
        #expect(manifest.install?.command.hasPrefix("claude plugin marketplace add ") == true)
        #expect(manifest.install?.command.hasSuffix("claude plugin install oh-my-claudecode@omc") == true)
    }

    /// Read-only state and lookup tools only. Each name was checked twice: it is
    /// defined as `name: "<tool>"` in the plugin's own `bridge/mcp-server.cjs`,
    /// and it appears in this session's deferred tool list as
    /// `mcp__plugin_oh-my-claudecode_t__<tool>`. Nothing that writes, renames,
    /// deletes or executes is here — the same line Ouroboros draws at the tools
    /// that start work.
    @Test func autoAllowIsReadOnlyExactNames() {
        let prefix = "mcp__plugin_oh-my-claudecode_t__"
        let readOnly = ["state_read", "state_get_status", "state_list_active",
                        "notepad_read", "notepad_stats", "project_memory_read",
                        "shared_memory_read", "shared_memory_list",
                        "trace_summary", "trace_timeline",
                        "wiki_query", "wiki_read", "wiki_list",
                        "session_search", "list_omc_skills", "ast_grep_search",
                        "lsp_servers", "lsp_diagnostics",
                        "lsp_document_symbols", "lsp_workspace_symbols",
                        "lsp_hover", "lsp_goto_definition", "lsp_find_references"]
        #expect(readOnly.count == 23 && manifest.autoAllow.count == 23)
        #expect(manifest.autoAllow.map(\.wireName) == readOnly.map { prefix + $0 })
        for name in readOnly { #expect(evaluator.autoAllowed(toolName: prefix + name)) }
        // Writers, executors and renamers on the very same server keep their card.
        // deepinit_manifest has a `save` action and lsp_diagnostics_directory runs the
        // project's tsc: neither is a lookup, so both keep their card.
        for name in ["deepinit_manifest", "lsp_diagnostics_directory", "state_write", "state_clear", "state_migrate_non_git",
                     "notepad_write_manual", "notepad_write_priority", "notepad_write_working", "notepad_prune",
                     "project_memory_write", "project_memory_add_note", "shared_memory_write", "shared_memory_delete",
                     "wiki_add", "wiki_delete", "wiki_ingest", "python_repl", "ast_grep_replace", "lsp_rename",
                     "load_omc_skills_global", "load_omc_skills_local"] {
            #expect(!evaluator.autoAllowed(toolName: prefix + name))
        }
        // The question tool is refused by the schema and again at runtime, and
        // `ToolSearch` is bundled-only, so a non-bundled list cannot hold either.
        #expect(!evaluator.autoAllowed(toolName: "AskUserQuestion"))
        #expect(!evaluator.autoAllowed(toolName: "ToolSearch"))
        #expect(!manifest.autoAllow.contains { $0.tool == "ToolSearch" || $0.tool == "AskUserQuestion" })
        // Another plugin's server, a user-configured server, and forged boundaries.
        for name in ["mcp__plugin_ouroboros_ouroboros__ouroboros_interview", "mcp__mcp-atlassian__jira_get_issue",
                     "mcp__other__state_read", "mcp__plugin_oh-my-claudecode_evil__state_read",
                     prefix + "evil__state_read", prefix, "state_read", "Bash", "Write", ""] {
            #expect(!evaluator.autoAllowed(toolName: name))
        }
    }

    /// A `user` manifest is `pending` until the person says yes, and a pending
    /// style is not runnable, not swept for request titles, and so never
    /// projected to a pane or a phone (§4.2, §7.2). Temporary directories only.
    @Test func registrationIsPendingUntilApproved() async throws {
        let root = StyleFixtures.temporaryDirectory("omc-trust")
        defer { try? FileManager.default.removeItem(at: root) }
        let path = root.appendingPathComponent("styles/oh-my-claudecode.json")
        try StyleFixtures.write(data, to: path)
        let file = StyleFixtures.discovered(data, source: .user, url: path)
        let store = StyleTrustStore(directory: root.appendingPathComponent("style-trust", isDirectory: true))

        let before = StyleRegistry.make(files: [file], approvals: try await store.load())
        #expect(before.rejections.isEmpty)
        let pending = try #require(before.styles.first)
        #expect(pending.id == "oh-my-claudecode" && pending.approval == .pending && !pending.isRunnable)
        let unapproved = StyleRegistry(styles: before.styles)
        #expect(unapproved.runnable("oh-my-claudecode", workspace: nil, hash: pending.hash) == nil)
        #expect(unapproved.runnableInPrecedence(workspace: nil).isEmpty)
        #expect(unapproved.requestTitle(forInput: "/oh-my-claudecode:plan 목표", workspace: nil) == nil)

        try await store.approve(pending)
        let after = StyleRegistry.make(files: [file], approvals: try await store.load())
        let approved = try #require(after.styles.first)
        #expect(approved.approval == .approved && approved.isRunnable)
        let registry = StyleRegistry(styles: after.styles)
        #expect(registry.runnable("oh-my-claudecode", workspace: nil, hash: approved.hash)?.id == "oh-my-claudecode")
        // Approval is bound to the bytes: a pane holding another hash gets no style.
        #expect(registry.runnable("oh-my-claudecode", workspace: nil, hash: String(repeating: "0", count: 64)) == nil)
        #expect(registry.requestTitle(forInput: "/oh-my-claudecode:plan 목표", workspace: nil) == "계획")
    }

    /// The other third-party manifest, needed only so the registry sweep in
    /// `requestTitlesIconsAndTints` has something to sweep past.
    private static func gstack() throws -> RegisteredStyle {
        let url = StyleGolden.stylesDirectory.appendingPathComponent("gstack.json")
        let data = try Data(contentsOf: url)
        return RegisteredStyle(manifest: try StyleManifestDecoder.decode(data, source: .user), source: .user,
                               path: url.path, workspacePath: nil, hash: StyleHash.of(data), approval: .approved)
    }
}
