import Foundation

public enum StyleRecognisedName: Sendable, Equatable { case action(String), alias(name: String, phase: String) }
public enum StyleEnterBehaviour: Sendable, Equatable { case verbatim, rewrite(actionId: String) }

/// Every rule of §1.3–§1.7 read out of one manifest. Pure: no disk, no clock.
public struct StyleEvaluator: Sendable {
    public let manifest: StyleManifest
    private let byId: [String: StyleAction]
    private let byMatch: [String: StyleAction]
    private let aliases: [String: String]

    public init(_ manifest: StyleManifest) {
        self.manifest = manifest
        self.byId = Dictionary(manifest.actions.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        self.byMatch = Dictionary(manifest.actions.compactMap { action in action.match.map { ($0, action) } }, uniquingKeysWith: { first, _ in first })
        self.aliases = Dictionary(manifest.aliases.map { ($0.name, $0.phase) }, uniquingKeysWith: { first, _ in first })
    }

    public func action(_ id: String) -> StyleAction? { byId[id] }

    // MARK: - recognition (§1.4's two questions)

    /// Does the name resolve to an action, a `match` or an alias? Request
    /// titles and the phase walk ask this one.
    public func recognised(inPrompt prompt: String) -> StyleRecognisedName? {
        guard let name = manifest.recognition.name(inPrompt: prompt) else { return nil }
        if let action = byId[name] { return .action(action.id) }
        if let action = byMatch[name] { return .action(action.id) }
        if let phase = aliases[name] { return .alias(name: name, phase: phase) }
        return nil
    }

    /// Is there a prefix with a non-empty name after it, whatever it means?
    /// The Enter rule asks this one, so `ooo 이거 해줘` does not get rewritten.
    public func namesSomething(inPrompt prompt: String) -> Bool { manifest.recognition.name(inPrompt: prompt) != nil }

    public func prompt(actionId: String, text: String) -> String? { byId[actionId]?.prompt(text: text) }

    public func requestTitle(forInput input: String) -> String? {
        switch recognised(inPrompt: input) {
        case .action(let id):
            guard let action = byId[id] else { return nil }
            if let title = action.requestTitle { return title }
            if let glyph = action.glyph { return glyph + " " + action.title }
            if let phase = action.phase, let value = manifest.phase(phase) { return value.title }
            return action.title
        case .alias(_, let phase):
            return manifest.phase(phase)?.title
        case .none:
            return nil
        }
    }

    public func requestIcon(forInput input: String) -> StyleIcon? {
        guard case .action(let id)? = recognised(inPrompt: input) else { return manifest.presentation.icon }
        return byId[id]?.icon ?? manifest.presentation.icon
    }

    public func requestTint(forInput input: String) -> StyleTint {
        guard case .action(let id)? = recognised(inPrompt: input) else { return manifest.presentation.tint ?? .accent }
        return byId[id]?.tint ?? manifest.presentation.tint ?? .accent
    }

    // MARK: - state

    /// Newest first until something recognised carries a phase; free text and
    /// phase-less actions never move the flow (§1.6).
    public func currentPhase(prompts: [String]) -> StylePhase? {
        guard case .lastRecognisedAction(let fallback) = manifest.rules.phase else { return nil }
        for prompt in prompts.reversed() {
            switch recognised(inPrompt: prompt) {
            case .action(let id):
                if let phase = byId[id]?.phase, let value = manifest.phase(phase) { return value }
            case .alias(_, let phase):
                if let value = manifest.phase(phase) { return value }
            case .none:
                continue
            }
        }
        return manifest.phase(fallback)
    }

    /// The pane's own request history, which survives the log being trimmed.
    public func currentPhase(session: RunSession) -> StylePhase? {
        let requests = (session.graphRuns ?? MightyGraphSupport.legacyRuns(session)).map(\.input)
        return currentPhase(prompts: requests.isEmpty ? session.logs.filter { $0.kind == "user" }.map(\.text) : requests)
    }

    public func startActions(phase: StylePhase?) -> [StyleAction] {
        guard case .actions(let startPhase, let ids, _) = manifest.rules.start, phase?.id == startPhase else { return [] }
        return ids.compactMap { byId[$0] }
    }

    public var resetTitle: String? {
        guard case .actions(_, _, let title) = manifest.rules.start else { return nil }
        return title
    }

    public func nextActions(phase: StylePhase?, group: StyleGroup?) -> [StyleAction] {
        switch manifest.rules.next {
        case .byPhase(let map):
            guard let phase else { return [] }
            return (map[phase.id] ?? []).compactMap { byId[$0] }
        case .byGroup:
            guard let group else { return [] }
            return group.actions.compactMap { byId[$0] }
        }
    }

    public func recommendedAction(capabilityStates: [String: String], group: StyleGroup?) -> String? {
        guard case .capability(let name, let map, let onlyGroup) = manifest.rules.recommend else { return nil }
        if let onlyGroup, group?.id != onlyGroup { return nil }
        guard let state = capabilityStates[name] else { return nil }
        return map[state]
    }

    /// Read once, when the panel opens: a later state change does not move the
    /// group the user is looking at (§1.6).
    public func initialGroup(capabilityStates: [String: String]) -> StyleGroup? {
        switch manifest.rules.initialGroup {
        case .fixed(let group): return manifest.group(group)
        case .capabilityState(let name, let map):
            guard let state = capabilityStates[name], let group = map[state] else { return manifest.groups.first }
            return manifest.group(group)
        }
    }

    public func drawsGroupMap() -> Bool {
        guard case .byGroup = manifest.rules.next else { return false }
        return manifest.groups.count >= 2 && manifest.groups.contains { $0.axis != nil }
    }

    // MARK: - composer

    /// All six conditions of §1.6 must hold; condition 6 is the one a
    /// manifest cannot make permanently true about itself.
    public func enterBehaviour(draft: String, phase: StylePhase?, hasAttachments: Bool, running: Bool, hasRequests: Bool) -> StyleEnterBehaviour {
        guard case .rewriteBareDraftTo(let action, let rulePhase) = manifest.rules.enter else { return .verbatim }
        guard !running, !hasAttachments, !hasRequests else { return .verbatim }
        guard !draft.trimmingCharacters(in: .whitespacesAndNewlines).hasPrefix("/") else { return .verbatim }
        guard !namesSomething(inPrompt: draft) else { return .verbatim }
        guard phase?.id == rulePhase else { return .verbatim }
        return .rewrite(actionId: action)
    }

    /// The prefix the composer draws as a non-editable chip while the rule is
    /// armed: `placeholders.initial` is the author's text and cannot say this.
    public func enterArmedPrefix(phase: StylePhase?, running: Bool, hasRequests: Bool) -> String? {
        guard case .rewriteBareDraftTo(let action, let rulePhase) = manifest.rules.enter else { return nil }
        guard !running, !hasRequests, phase?.id == rulePhase else { return nil }
        return byId[action]?.prompt(text: "")
    }

    /// Answering wins, then running, then the entry phase (§1.7). An empty
    /// string means "the app's own default", which lives in the app.
    public func placeholder(phase: StylePhase?, running: Bool, answering: Bool) -> String {
        if answering { return manifest.placeholders.answering }
        if running { return manifest.placeholders.running ?? "" }
        if case .rewriteBareDraftTo(_, let rulePhase) = manifest.rules.enter, phase?.id == rulePhase, let initial = manifest.placeholders.initial { return initial }
        return manifest.placeholders.idle
    }

    public func guidanceLine(phase: StylePhase?, running: Bool) -> String? {
        let text: String?
        if running { text = manifest.guidance.running }
        else if case .actions(let startPhase, _, _) = manifest.rules.start, phase?.id == startPhase { text = manifest.guidance.start }
        else { text = manifest.guidance.next }
        guard let text, !text.isEmpty else { return nil }
        return StyleGuidanceTemplate.render(text, phaseTitle: phase?.title)
    }

    // MARK: - permission

    /// Exact wire names only: no prefix match, no glob, no regex. The schema
    /// cannot express a pattern and the runtime refuses the question tool twice.
    public func autoAllowed(toolName: String) -> Bool {
        guard toolName != "AskUserQuestion" else { return false }
        guard let entry = manifest.autoAllow.first(where: { $0.wireName == toolName }) else { return false }
        // The schema refuses the question tool and so does the runtime, on any
        // server: two layers, so a list built some other way still cannot ask.
        return entry.tool != "AskUserQuestion"
    }
}

public enum StylePrerequisiteProbe {
    public static func evaluate(_ prerequisites: StylePrerequisites,
                                install: StyleInstall?,
                                home: URL = FileManager.default.homeDirectoryForCurrentUser,
                                workspacePath: String?,
                                environment: [String: String] = ProviderService.runtimeEnvironment()) -> StylePrerequisiteResult {
        // Nothing to install is a real answer: always ready, no block drawn.
        guard !prerequisites.probes.isEmpty else { return StylePrerequisiteResult(ready: true) }
        let satisfied = prerequisites.probes.map { satisfied($0, home: home, workspacePath: workspacePath, environment: environment) }
        let ready = prerequisites.mode == .all ? !satisfied.contains(false) : satisfied.contains(true)
        let unmet = zip(prerequisites.probes, satisfied).filter { !$0.1 }.map(\.0)
        guard !ready else { return StylePrerequisiteResult(ready: true) }
        let missing = prerequisites.report == .first ? Array(unmet.prefix(1).map(\.missing)) : unmet.map(\.missing)
        return StylePrerequisiteResult(ready: false, missing: missing, hint: unmet.first?.hint,
                                       canInstall: install != nil && unmet.contains(where: \.install))
    }

    /// The judgement itself, always made on a worker (§1.5).
    public static func satisfied(_ probe: StyleProbe,
                                 home: URL = FileManager.default.homeDirectoryForCurrentUser,
                                 workspacePath: String?,
                                 environment: [String: String] = ProviderService.runtimeEnvironment()) -> Bool {
        switch probe {
        case .plugin(let prefix, _, _, _):
            let registry = home.appendingPathComponent(".claude/plugins/installed_plugins.json")
            guard let data = CLIAccountSupport.boundedData(registry, maximumBytes: 4 * 1024 * 1024),
                  let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let entries = object["plugins"] as? [String: Any] else { return false }
            return entries.keys.contains { $0.hasPrefix(prefix) }
        case .executable(let name, _, _, _):
            return (environment["PATH"] ?? "").split(separator: ":").contains { FileManager.default.isExecutableFile(atPath: String($0) + "/" + name) }
        case .skill(let name, let scopes, _, _, _):
            return scopes.contains { scope in
                let root: URL? = scope == .user ? home : workspacePath.map { URL(fileURLWithPath: $0, isDirectory: true) }
                guard let root else { return false }
                return FileManager.default.fileExists(atPath: root.appendingPathComponent(".claude/skills/\(name)/SKILL.md").path)
            }
        }
    }
}
