import Foundation
import Testing
@testable import MightyCore

/// `styles/gstack.json`. The second half of completion criterion 2, and the
/// opposite shape from oh-my-claudecode on purpose: bare `/name` recognition,
/// no MCP server, no start rule, a verbatim Enter, glyphs instead of icons.
///
/// Every id is a directory that exists under `~/.claude/skills/<name>/SKILL.md`
/// on this machine, and `_gstack-command` is the marker the suite plants to say
/// "this install is gstack" — hence the first prerequisite probe.
struct StylesThirdPartyGstackTests {
    private let manifest: StyleManifest
    private let style: RegisteredStyle
    private let data: Data

    /// Decoding and validating as `.user`: no waivers, and an empty `autoAllow`
    /// means nothing here can be auto-allowed even by mistake.
    init() throws {
        data = try Data(contentsOf: Self.url)
        manifest = try StyleManifestDecoder.decode(data, source: .user)
        style = RegisteredStyle(manifest: manifest, source: .user, path: Self.url.path, workspacePath: nil,
                                hash: StyleHash.of(data), approval: .approved)
    }

    private static var url: URL { StyleGolden.stylesDirectory.appendingPathComponent("gstack.json") }
    private var evaluator: StyleEvaluator { style.evaluator }
    private func phase(_ id: String) -> StylePhase? { manifest.phase(id) }

    private static let actionIds = ["office-hours", "spec", "autoplan", "plan-ceo-review", "plan-eng-review",
                                    "plan-design-review", "plan-devex-review",
                                    "investigate", "codex", "health",
                                    "qa", "qa-only", "review", "design-review", "devex-review", "cso", "benchmark",
                                    "ship", "land-and-deploy", "canary", "document-release",
                                    "retro", "learn",
                                    "context-save", "context-restore", "freeze", "unfreeze"]

    @Test func catalogueAndFlow() {
        #expect(manifest.id == "gstack" && manifest.name == "gstack")
        #expect(manifest.actions.map(\.id) == Self.actionIds && Self.actionIds.count == 27)
        #expect(manifest.groups.map(\.id) == ["flow"] && manifest.group("flow")?.actions == Self.actionIds)
        #expect(!evaluator.drawsGroupMap() && evaluator.drawsPhaseProgress)
        #expect(manifest.phases.map(\.id) == ["plan", "build", "qa", "ship", "retro"])
        #expect(manifest.orderedPhases.map(\.title) == ["계획", "구현", "검증", "출시", "회고"])
        // `qa`, `ship` and `retro` are a phase id and an action id at once. The
        // four id spaces are separate and only aliases may not clash (§1.4).
        for name in ["qa", "ship", "retro"] { #expect(manifest.phase(name) != nil && manifest.action(name) != nil) }
        // No start rule, so the entry phase's `next` row is the entry menu and
        // nothing in `next.map` is dead. A reset chip would have nothing to
        // re-arm either: Enter is verbatim.
        #expect(manifest.rules.start == .none && evaluator.resetTitle == nil)
        #expect(evaluator.startActions(phase: phase("plan")).isEmpty)
        #expect(evaluator.nextActions(phase: phase("plan"), group: nil).map(\.id)
                == ["office-hours", "spec", "autoplan", "plan-eng-review", "investigate"])
        #expect(evaluator.nextActions(phase: phase("build"), group: nil).map(\.id) == ["qa", "review", "health", "investigate"])
        #expect(evaluator.nextActions(phase: phase("qa"), group: nil).map(\.id) == ["review", "ship", "cso", "investigate"])
        #expect(evaluator.nextActions(phase: phase("ship"), group: nil).map(\.id) == ["canary", "document-release", "retro"])
        #expect(evaluator.nextActions(phase: phase("retro"), group: nil).map(\.id) == ["learn", "office-hours", "spec"])
        // Session switches must not rewind the stage, so they carry no phase.
        #expect(manifest.actions.filter { $0.phase == nil }.map(\.id) == ["context-save", "context-restore", "freeze", "unfreeze"])
        // `interactive: true` in each skill's own frontmatter is what userInvoked means here.
        #expect(Set(manifest.actions.filter { $0.flags.contains(.userInvoked) }.map(\.id))
                == ["plan-ceo-review", "plan-eng-review", "plan-design-review", "plan-devex-review"])
        #expect(Set(manifest.actions.filter { $0.flags.contains(.readOnly) }.map(\.id)) == ["qa-only", "context-restore"])
        // Glyphs, not icons: the phone has no SF Symbol renderer, and a glyph is
        // what keeps `/qa` and `/qa-only` apart in a request block (§1.10).
        #expect(manifest.actions.allSatisfy { $0.glyph != nil && $0.icon == nil && $0.scope != nil && $0.foldText == .oneLine })
        #expect(Set(manifest.actions.compactMap(\.glyph)).count == 27)
        #expect(manifest.aliases.map(\.name) == ["plan-tune", "design-consultation", "design-shotgun", "design-html",
                                                 "browse", "ios-qa", "landing-report", "document-generate"])
    }

    @Test func promptsAndRecognition() {
        // `oneLine` is the phone's own folding too, and gstack arguments are
        // one-liners: a URL, a path, a branch.
        #expect(evaluator.prompt(actionId: "qa", text: " https://staging.example.dev \n\n  로그인 흐름 \n")
                == "/qa https://staging.example.dev 로그인 흐름")
        #expect(evaluator.prompt(actionId: "review", text: "  feature/pay  ") == "/review feature/pay")
        // Empty text takes the space before `{text}` with it (§1.3.1).
        #expect(evaluator.prompt(actionId: "retro", text: "\n \n") == "/retro")
        #expect(evaluator.prompt(actionId: "unfreeze", text: "") == "/unfreeze")
        #expect(evaluator.prompt(actionId: "no-such-skill", text: "") == nil)
        #expect(evaluator.recognised(inPrompt: "/qa https://staging.example.dev") == .action("qa"))
        #expect(evaluator.recognised(inPrompt: "/qa-only") == .action("qa-only"))
        #expect(evaluator.recognised(inPrompt: "/landing-report") == .alias(name: "landing-report", phase: "ship"))
        // Recognition is per style: Paperthin's skills share the bare `/` rule
        // but are not in this catalogue, so they name nothing here and move no
        // phase. The request still goes out; it just gets no chip (A.4).
        #expect(evaluator.recognised(inPrompt: "/re0 docs/spec.md") == nil)
        #expect(evaluator.recognised(inPrompt: "/oh-my-claudecode:plan 목표") == nil)
        #expect(evaluator.recognised(inPrompt: "/") == nil && evaluator.recognised(inPrompt: "그냥 물어볼게요") == nil)
        #expect(evaluator.currentPhase(prompts: [])?.id == "plan")
        #expect(evaluator.currentPhase(prompts: ["/spec 결제", "/investigate 500 에러"])?.id == "build")
        // A session switch and an unknown name both leave the stage where it was.
        #expect(evaluator.currentPhase(prompts: ["/ship", "/context-save", "/re0 docs"])?.id == "ship")
    }

    /// gstack's normal path is a free request, so Enter sends what was typed,
    /// on a fresh pane as much as a busy one. `placeholders.initial` may not
    /// even exist under this rule (`E_PLACEHOLDER_INITIAL`).
    @Test func enterIsAlwaysVerbatim() {
        #expect(manifest.rules.enter == .verbatim && manifest.placeholders.initial == nil)
        #expect(evaluator.enterBehaviour(draft: "결제 화면 버그 좀 봐줘", phase: phase("plan"), hasAttachments: false,
                                         running: false, hasRequests: false) == .verbatim)
        #expect(evaluator.enterArmedPrefix(draft: "결제 화면 버그 좀 봐줘", phase: phase("plan"), running: false, hasRequests: false) == nil)
        #expect(evaluator.enterArmedPrefix(draft: "", phase: phase("plan"), running: false, hasRequests: false, startingNew: true) == nil)
        #expect(evaluator.placeholder(phase: phase("plan"), running: false, answering: false)
                == "대상(파일 경로 \u{00B7} URL \u{00B7} 지시)을 적고 위에서 스킬을 고르세요 \u{00B7} Enter는 그대로 요청합니다…")
        #expect(evaluator.placeholder(phase: phase("plan"), running: false, answering: true) == "직접 답하려면 여기에 적고 Enter…")
        #expect(evaluator.guidanceLine(phase: phase("qa"), running: false)
                == "검증 단계입니다. 대상을 아래에 적고 스킬을 누르세요. 비워 두면 스킬만 보냅니다.")
        #expect(evaluator.guidanceLine(phase: phase("qa"), running: true) == "검증 진행 중 \u{00B7} 고른 스킬은 다음 요청으로 대기합니다")
    }

    /// Every action carries a glyph, so rule 2 of §1.10 wins and each request
    /// block is titled by the action rather than by its phase — which is the
    /// whole reason gstack writes glyphs: `/qa` and `/qa-only` share a phase.
    @Test func requestTitlesAreGlyphedAndSweptByPrecedence() throws {
        #expect(evaluator.requestTitle(forInput: "/qa https://staging.example.dev") == "🧪 QA 및 수정")
        #expect(evaluator.requestTitle(forInput: "/qa-only https://staging.example.dev") == "📋 QA 리포트")
        #expect(evaluator.requestTitle(forInput: "/ship") == "🚢 PR 출시")
        #expect(evaluator.requestTitle(forInput: "/retro") == "🔁 회고")
        // An alias has no glyph of its own, so it is titled by the phase it moves to.
        #expect(evaluator.requestTitle(forInput: "/landing-report") == "출시")
        #expect(evaluator.requestTitle(forInput: "무슨 일이 일어난 거죠") == nil)

        let registry = StyleRegistry(styles: BundledStyles.shared.styles() + [style, try Self.ohMyClaudecode()])
        // A pane keeps its earlier blocks when the style changes, so the sweep
        // runs over every runnable style in precedence order — bundled first.
        // Paperthin shares the bare `/` rule and owns these two names, so a
        // gstack pane still sees Paperthin's titles on them. That is the same
        // thing the two bundled styles do for each other today (§1.10, A.4).
        #expect(registry.requestTitle(forInput: "/re0 docs/spec.md", workspace: nil) == "♻️ re0")
        #expect(registry.requestTitle(forInput: "/nba", workspace: nil) == "🎯 nba")
        #expect(registry.requestTitle(forInput: "/ouroboros:seed", workspace: nil) == "시드")
        // `/review` is no bundled style's name — Ouroboros needs its own prefix
        // and Paperthin has no `review` skill — so the first user style by id
        // answers, and `gstack` sorts before `oh-my-claudecode`. Claude Code's
        // built-in `/review` is unaffected: the engine titles the block, it does
        // not rewrite the request.
        #expect(registry.requestTitle(forInput: "/review feature/pay", workspace: nil) == "🔎 PR 리뷰")
        #expect(registry.requestIcon(forInput: "/review", workspace: nil)?.rawValue == "cube")
        #expect(registry.requestTint(forInput: "/review", workspace: nil) == .orange)
        #expect(registry.requestTitle(forInput: "안녕하세요", workspace: nil) == nil)
        #expect(registry.requestIcon(forInput: "안녕하세요", workspace: nil) == nil)
        #expect(registry.requestTint(forInput: "안녕하세요", workspace: nil) == .accent)
    }

    /// `mode: "any"` over three skill probes, read from an injected home and an
    /// injected workspace in temporary directories — never the real `~/.claude`.
    @Test func prerequisitesFindTheSkillMarker() throws {
        let root = StyleFixtures.temporaryDirectory("gstack-home")
        defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home"), workspace = root.appendingPathComponent("repo")
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        func result(_ home: URL, _ workspacePath: String? = nil) -> StylePrerequisiteResult {
            StylePrerequisiteProbe.evaluate(manifest.prerequisites, install: manifest.install,
                                            home: home, workspacePath: workspacePath, environment: [:])
        }
        let empty = result(home)
        #expect(!empty.ready && empty.missing == ["gstack 스킬이 설치되어 있지 않습니다"] && empty.canInstall)
        #expect(empty.hint?.hasSuffix("Enter는 직접 누르세요.") == true)
        #expect(manifest.prerequisites.mode == .any && manifest.prerequisites.report == .first)
        // The leading underscore is real and §1.5's probe-name rule allows it.
        #expect(manifest.prerequisites.probes.count == 3)
        try StyleFixtures.write(Data("---\nname: gstack\n---\n".utf8),
                                to: home.appendingPathComponent(".claude/skills/_gstack-command/SKILL.md"))
        #expect(result(home).ready)
        // A project-scope install counts for that workspace and nowhere else.
        let bare = root.appendingPathComponent("home2")
        try FileManager.default.createDirectory(at: bare, withIntermediateDirectories: true)
        try StyleFixtures.write(Data("---\nname: office-hours\n---\n".utf8),
                                to: workspace.appendingPathComponent(".claude/skills/office-hours/SKILL.md"))
        #expect(result(bare, workspace.path).ready && !result(bare).ready)
        // Verified from gstack's own README: clone into the skills folder, run ./setup.
        #expect(manifest.install?.command.hasPrefix("git clone --single-branch --depth 1 https://github.com/garrytan/gstack.git") == true)
        #expect(manifest.install?.command.hasSuffix("./setup") == true && manifest.install?.paneTitle == "gstack 설치")
    }

    /// gstack ships no MCP server, so nothing is auto-allowed: every tool call
    /// follows the pane's own permission mode, exactly as Paperthin does.
    @Test func nothingIsAutoAllowed() {
        #expect(manifest.autoAllow.isEmpty && manifest.capabilities.isEmpty)
        for name in ["Bash", "Write", "Edit", "AskUserQuestion", "ToolSearch",
                     "mcp__plugin_oh-my-claudecode_t__state_read",
                     "mcp__plugin_ouroboros_ouroboros__ouroboros_session_status",
                     "mcp__mcp-atlassian__jira_get_issue", ""] {
            #expect(!evaluator.autoAllowed(toolName: name))
        }
    }

    /// Pending until approved, and invisible to a pane and a phone until then
    /// (§4.2, §7.2). Temporary directories only.
    @Test func registrationIsPendingUntilApproved() async throws {
        let root = StyleFixtures.temporaryDirectory("gstack-trust")
        defer { try? FileManager.default.removeItem(at: root) }
        let path = root.appendingPathComponent("styles/gstack.json")
        try StyleFixtures.write(data, to: path)
        let file = StyleFixtures.discovered(data, source: .user, url: path)
        let store = StyleTrustStore(directory: root.appendingPathComponent("style-trust", isDirectory: true))

        let before = StyleRegistry.make(files: [file], approvals: try await store.load())
        #expect(before.rejections.isEmpty)
        let pending = try #require(before.styles.first)
        #expect(pending.id == "gstack" && pending.approval == .pending && !pending.isRunnable)
        let unapproved = StyleRegistry(styles: before.styles)
        #expect(unapproved.runnable("gstack", workspace: nil, hash: pending.hash) == nil)
        #expect(unapproved.runnableInPrecedence(workspace: nil).isEmpty)
        #expect(unapproved.requestTitle(forInput: "/ship", workspace: nil) == nil)

        try await store.approve(pending)
        let after = StyleRegistry.make(files: [file], approvals: try await store.load())
        let approved = try #require(after.styles.first)
        #expect(approved.approval == .approved && approved.isRunnable)
        let registry = StyleRegistry(styles: after.styles)
        #expect(registry.runnable("gstack", workspace: nil, hash: approved.hash)?.id == "gstack")
        #expect(registry.runnable("gstack", workspace: nil, hash: String(repeating: "0", count: 64)) == nil)
        #expect(registry.requestTitle(forInput: "/ship", workspace: nil) == "🚢 PR 출시")
    }

    private static func ohMyClaudecode() throws -> RegisteredStyle {
        let url = StyleGolden.stylesDirectory.appendingPathComponent("oh-my-claudecode.json")
        let data = try Data(contentsOf: url)
        return RegisteredStyle(manifest: try StyleManifestDecoder.decode(data, source: .user), source: .user,
                               path: url.path, workspacePath: nil, hash: StyleHash.of(data), approval: .approved)
    }
}
