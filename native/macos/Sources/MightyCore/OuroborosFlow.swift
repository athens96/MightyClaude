import Foundation

/// The Ouroboros style of Mighty mode. Ouroboros (github.com/Q00/ouroboros)
/// runs inside the agent session as skills plus `ouroboros_*` MCP tools and
/// asks the human through the runtime's question tool, so the app drives it
/// by sending skill prompts and watching the stream: no second engine.
public enum OuroborosPhase: String, CaseIterable, Sendable, Equatable {
    case goal, interview, seed, run, evaluate, evolve
    public var title: String {
        switch self { case .goal: "목표"; case .interview: "인터뷰"; case .seed: "시드"; case .run: "실행"; case .evaluate: "평가"; case .evolve: "진화" }
    }
}

public struct OuroborosAction: Sendable, Equatable, Identifiable {
    public var skill: String
    public var title: String
    public var systemImage: String
    public var help: String
    public var id: String { skill }
}

/// The guided styles a Mighty pane can use besides the plain CLI style (nil).
public enum MightyStyles {
    public static let all = [OuroborosFlow.style, PaperthinCatalog.style]
    public static func normalized(_ value: String?) -> String? { value.flatMap { all.contains($0) ? $0 : nil } }
    /// Request block title for a guided prompt: the Ouroboros phase or the
    /// Paperthin skill. Only panes on a guided style get one, so `/nba` typed
    /// into a plain CLI pane stays an ordinary request. Either style's titles
    /// show, because a pane keeps its earlier blocks when the style changes.
    public static func requestTitle(forInput input: String, style: String?) -> String? {
        guard normalized(style) != nil else { return nil }
        return OuroborosFlow.requestTitle(forInput: input) ?? PaperthinCatalog.requestTitle(forInput: input)
    }
}

public enum OuroborosFlow {
    public static let style = "ouroboros"
    public static let toolPrefix = "mcp__plugin_ouroboros_ouroboros__"
    public static let installCommand = "claude plugin marketplace add Q00/ouroboros && claude plugin install ouroboros@ouroboros"

    static let actions: [String: OuroborosAction] = Dictionary(uniqueKeysWithValues: [
        OuroborosAction(skill: "interview", title: "인터뷰 시작", systemImage: "questionmark.bubble", help: "소크라테스식 질문으로 요구를 또렷하게 만듭니다 (모호도 0.2 이하까지)"),
        OuroborosAction(skill: "auto", title: "자동 진행", systemImage: "wand.and.stars", help: "목표에서 시드 생성과 실행까지 한 번에 진행합니다"),
        OuroborosAction(skill: "seed", title: "시드 생성", systemImage: "leaf", help: "인터뷰 결과를 불변 명세(시드)로 굳힙니다"),
        OuroborosAction(skill: "run", title: "실행", systemImage: "play.fill", help: "시드를 Double Diamond 흐름으로 실행합니다"),
        OuroborosAction(skill: "evaluate", title: "평가", systemImage: "checkmark.seal", help: "기계 · 의미 · 합의 3단계로 결과를 검증합니다"),
        OuroborosAction(skill: "evolve", title: "진화", systemImage: "arrow.triangle.2.circlepath", help: "평가를 반영해 다음 세대 시드로 수렴할 때까지 반복합니다"),
        OuroborosAction(skill: "ralph", title: "랄프 루프", systemImage: "infinity", help: "수렴할 때까지 진화 단계를 계속 돌립니다"),
        OuroborosAction(skill: "status", title: "상태", systemImage: "gauge.with.dots.needle.33percent", help: "세션 상태와 목표 이탈(drift)을 확인합니다"),
        OuroborosAction(skill: "unstuck", title: "막힘 풀기", systemImage: "lightbulb", help: "다섯 가지 관점으로 막힌 지점을 다시 봅니다"),
    ].map { ($0.skill, $0) })

    public static func action(_ skill: String) -> OuroborosAction? { actions[skill] }

    /// The prompt the pane sends: `/ouroboros:interview <goal>`.
    public static func prompt(skill: String, text: String = "") -> String? {
        guard actions[skill] != nil else { return nil }
        let argument = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return "/ouroboros:" + skill + (argument.isEmpty ? "" : " " + argument)
    }

    /// The skill named by a prompt (`/ouroboros:seed`, `ooo run …`), if any.
    public static func skill(inPrompt prompt: String) -> String? {
        let trimmed = prompt.trimmingCharacters(in: .whitespacesAndNewlines)
        let rest: Substring
        if trimmed.hasPrefix("/ouroboros:") { rest = trimmed.dropFirst("/ouroboros:".count) }
        else if trimmed.hasPrefix("ooo ") { rest = trimmed.dropFirst(4) }
        else { return nil }
        let name = String(rest.prefix { !$0.isWhitespace }).lowercased()
        return name.isEmpty ? nil : name
    }

    public static func phase(forSkill skill: String) -> OuroborosPhase? {
        switch skill {
        case "interview", "auto", "pm", "socratic": return .interview
        case "seed", "crystallize": return .seed
        case "run", "execute": return .run
        case "evaluate", "eval", "qa": return .evaluate
        case "evolve", "ralph": return .evolve
        default: return nil   // status, unstuck, help… do not move the flow
        }
    }

    /// Where the flow stands: the phase of the last Ouroboros prompt among the
    /// pane's requests, oldest first. Callers pass the graph's request inputs
    /// (kept per request) rather than the log, which tool activity trims fast.
    public static func currentPhase(prompts: [String]) -> OuroborosPhase {
        for prompt in prompts.reversed() {
            if let skill = skill(inPrompt: prompt), let phase = phase(forSkill: skill) { return phase }
        }
        return .goal
    }
    public static func currentPhase(session: RunSession) -> OuroborosPhase {
        let requests = session.mightyGraphRuns.map(\.input)
        return currentPhase(prompts: requests.isEmpty ? session.logs.filter { $0.kind == "user" }.map(\.text) : requests)
    }
    /// Skills whose prompt carries what the user typed; the others go out bare.
    public static func takesText(_ skill: String) -> Bool { ["interview", "auto", "unstuck", "pm"].contains(skill) }

    /// Buttons offered once a phase's turn has ended, the natural next step first.
    public static func nextActions(after phase: OuroborosPhase) -> [OuroborosAction] {
        let skills: [String]
        switch phase {
        case .goal: skills = []
        case .interview: skills = ["seed", "status", "unstuck"]
        case .seed: skills = ["run", "evaluate", "status"]
        case .run: skills = ["evaluate", "evolve", "status", "unstuck"]
        case .evaluate: skills = ["evolve", "run", "status", "unstuck"]
        case .evolve: skills = ["ralph", "evaluate", "status", "unstuck"]
        }
        return skills.compactMap { actions[$0] }
    }

    /// "인터뷰" for a request block whose input is an Ouroboros prompt.
    public static func requestTitle(forInput input: String) -> String? {
        guard let skill = skill(inPrompt: input) else { return nil }
        if let phase = phase(forSkill: skill) { return phase.title }
        return actions[skill]?.title   // an unknown word after "ooo" is just a request
    }

    /// Ouroboros tools that only ask, record or read interview/session state.
    /// Tools that start work (execute, auto, ralph, evolve steps, evaluate,
    /// cancel, rewind) are not here and keep the pane's permission prompt.
    static let stateTools: Set<String> = [
        "ouroboros_interview", "ouroboros_pm_interview", "ouroboros_lateral_think", "ouroboros_generate_seed", "ouroboros_brownfield",
        "ouroboros_session_status", "ouroboros_job_status", "ouroboros_job_wait", "ouroboros_job_result",
        "ouroboros_query_events", "ouroboros_query_projection", "ouroboros_lineage_status", "ouroboros_measure_drift",
        "ouroboros_ac_dashboard", "ouroboros_ac_tree_hud", "ouroboros_session_signal_targets",
    ]
    /// Tool calls the style approves on its own: the exact state tools above on
    /// the Ouroboros plugin's server, and the runtime's tool discovery.
    public static func autoAllowed(toolName: String) -> Bool {
        if toolName == "ToolSearch" { return true }
        guard toolName.hasPrefix(toolPrefix) else { return false }
        return stateTools.contains(String(toolName.dropFirst(toolPrefix.count)))
    }

    public struct Prerequisites: Sendable, Equatable {
        public var pluginInstalled: Bool
        public var uvxAvailable: Bool
        public var ready: Bool { pluginInstalled && uvxAvailable }
    }
    public static func prerequisites(home: URL = FileManager.default.homeDirectoryForCurrentUser, environment: [String: String] = ProviderService.runtimeEnvironment()) -> Prerequisites {
        var installed = false
        let registry = home.appendingPathComponent(".claude/plugins/installed_plugins.json")
        if let data = CLIAccountSupport.boundedData(registry, maximumBytes: 4 * 1024 * 1024), let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let plugins = object["plugins"] as? [String: Any] { installed = plugins.keys.contains { $0.hasPrefix("ouroboros@") } }
        let uvx = (environment["PATH"] ?? "").split(separator: ":").contains { FileManager.default.isExecutableFile(atPath: String($0) + "/uvx") }
        return Prerequisites(pluginInstalled: installed, uvxAvailable: uvx)
    }
}

/// Answering an AskUserQuestion one question at a time from the composer:
/// option chips and free text fill `answers` until every question has one.
public struct QuestionnaireProgress: Sendable, Equatable {
    public enum Step: Sendable, Equatable { case next, complete([String: UserQuestionAnswer]) }
    public let requestKey: String
    public private(set) var index = 0
    public private(set) var answers: [String: UserQuestionAnswer] = [:]
    /// Picks made so far for the current multi-select question.
    public private(set) var selected: [String] = []
    public init(requestKey: String) { self.requestKey = requestKey }

    public func current(in questionnaire: UserQuestionnaire) -> UserQuestionnaire.Question? {
        questionnaire.questions.indices.contains(index) ? questionnaire.questions[index] : nil
    }

    /// A single-select chip answers the question; a multi-select chip toggles.
    public mutating func choose(_ label: String, in questionnaire: UserQuestionnaire) -> Step? {
        guard let question = current(in: questionnaire), question.options.contains(where: { $0.label == label }) else { return nil }
        if question.multiSelect {
            if let position = selected.firstIndex(of: label) { selected.remove(at: position) } else { selected.append(label) }
            return nil
        }
        return record(UserQuestionAnswer(selectedOptions: [label]), for: question, in: questionnaire)
    }

    /// Enter in the composer: the typed text (and any toggled chips) answer the question.
    public mutating func commit(customText: String, in questionnaire: UserQuestionnaire) -> Step? {
        guard let question = current(in: questionnaire) else { return nil }
        let text = customText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty || !selected.isEmpty else { return nil }
        let picks = question.multiSelect ? question.options.map(\.label).filter(selected.contains) : []
        return record(UserQuestionAnswer(selectedOptions: picks, customText: text.isEmpty ? nil : text), for: question, in: questionnaire)
    }

    /// Returns to the previous question with its earlier picks shown again.
    public mutating func back(in questionnaire: UserQuestionnaire) {
        guard index > 0 else { return }
        index -= 1
        let previous = questionnaire.questions[index]
        selected = previous.multiSelect ? (answers[previous.question]?.selectedOptions ?? []) : []
    }

    private mutating func record(_ answer: UserQuestionAnswer, for question: UserQuestionnaire.Question, in questionnaire: UserQuestionnaire) -> Step {
        answers[question.question] = answer
        // The last question stays on screen until the send succeeds, so its picks stay too.
        guard index + 1 < questionnaire.questions.count else { return .complete(answers) }
        selected = []
        index += 1
        return .next
    }
}
