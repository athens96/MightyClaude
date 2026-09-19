import Foundation

// Everything in this file is a thin reading of the two bundled manifests
// through the engine. It exists only so the app module keeps building while
// Lane B still calls the old names.

/// Removed with its last caller in the app module (docs/mighty-styles.md §6.3).
public enum OuroborosPhase: String, CaseIterable, Sendable, Equatable {
    case goal, interview, seed, run, evaluate, evolve
    public var title: String { BundledStyles.shared.manifest(MightyStyleIDs.ouroboros)?.phase(rawValue)?.title ?? rawValue }
}

/// Removed with its last caller in the app module (docs/mighty-styles.md §6.3).
public struct OuroborosAction: Sendable, Equatable, Identifiable {
    public var skill: String
    public var title: String
    public var systemImage: String
    public var help: String
    public var id: String { skill }
    public init(skill: String, title: String, systemImage: String, help: String) {
        self.skill = skill; self.title = title; self.systemImage = systemImage; self.help = help
    }
    init(_ action: StyleAction) {
        self.init(skill: action.id, title: action.title, systemImage: action.icon?.rawValue ?? StyleIcon.requestDefault.rawValue, help: action.help)
    }
}

/// Removed with its last caller in the app module (docs/mighty-styles.md §6.3).
public enum MightyStyles {
    public static let all = [MightyStyleIDs.ouroboros, MightyStyleIDs.paperthin]
    public static func normalized(_ value: String?) -> String? { value.flatMap { all.contains($0) ? $0 : nil } }
    public static func requestTitle(forInput input: String, style: String?) -> String? {
        guard normalized(style) != nil else { return nil }
        for id in all {
            if let title = BundledStyles.shared.evaluator(id)?.requestTitle(forInput: input) { return title }
        }
        return nil
    }
}

/// Removed with its last caller in the app module (docs/mighty-styles.md §6.3).
public enum OuroborosFlow {
    public static let style = MightyStyleIDs.ouroboros
    public static let toolPrefix = "mcp__plugin_ouroboros_ouroboros__"
    public static var installCommand: String { manifest?.install?.command ?? "" }

    static var manifest: StyleManifest? { BundledStyles.shared.manifest(style) }
    static var evaluator: StyleEvaluator? { BundledStyles.shared.evaluator(style) }

    public static var allActions: [OuroborosAction] { (manifest?.actions ?? []).map(OuroborosAction.init) }
    public static func action(_ skill: String) -> OuroborosAction? { manifest?.action(skill).map(OuroborosAction.init) }
    public static func prompt(skill: String, text: String = "") -> String? { evaluator?.prompt(actionId: skill, text: text) }
    public static func skill(inPrompt prompt: String) -> String? { manifest?.recognition.name(inPrompt: prompt) }

    public static func phase(forSkill skill: String) -> OuroborosPhase? {
        guard let manifest else { return nil }
        if let action = manifest.action(skill), let phase = action.phase { return OuroborosPhase(rawValue: phase) }
        if let alias = manifest.aliases.first(where: { $0.name == skill }) { return OuroborosPhase(rawValue: alias.phase) }
        return nil
    }

    public static func currentPhase(prompts: [String]) -> OuroborosPhase {
        evaluator?.currentPhase(prompts: prompts).flatMap { OuroborosPhase(rawValue: $0.id) } ?? .goal
    }
    public static func currentPhase(session: RunSession) -> OuroborosPhase {
        evaluator?.currentPhase(session: session).flatMap { OuroborosPhase(rawValue: $0.id) } ?? .goal
    }

    public static var textSkills: [String] { (manifest?.actions ?? []).filter(\.takesText).map(\.id) }
    public static func takesText(_ skill: String) -> Bool { manifest?.action(skill)?.takesText ?? false }

    public static func nextActions(after phase: OuroborosPhase) -> [OuroborosAction] {
        guard let manifest, let evaluator else { return [] }
        return evaluator.nextActions(phase: manifest.phase(phase.rawValue), group: nil).map(OuroborosAction.init)
    }

    public static func requestTitle(forInput input: String) -> String? { evaluator?.requestTitle(forInput: input) }
    public static func autoAllowed(toolName: String) -> Bool { evaluator?.autoAllowed(toolName: toolName) ?? false }

    public struct Prerequisites: Sendable, Equatable {
        public var pluginInstalled: Bool
        public var uvxAvailable: Bool
        public init(pluginInstalled: Bool, uvxAvailable: Bool) { self.pluginInstalled = pluginInstalled; self.uvxAvailable = uvxAvailable }
        public var ready: Bool { pluginInstalled && uvxAvailable }
    }
    public static func prerequisites(home: URL = FileManager.default.homeDirectoryForCurrentUser,
                                     environment: [String: String] = ProviderService.runtimeEnvironment()) -> Prerequisites {
        let probes = manifest?.prerequisites.probes ?? []
        func met(_ probe: StyleProbe?) -> Bool {
            guard let probe else { return false }
            return StylePrerequisiteProbe.satisfied(probe, home: home, workspacePath: nil, environment: environment)
        }
        return Prerequisites(pluginInstalled: met(probes.first { $0.pluginName != nil }),
                             uvxAvailable: met(probes.first { if case .executable = $0 { return true } else { return false } }))
    }
}

/// Removed with its last caller in the app module (docs/mighty-styles.md §6.3).
public enum PaperthinDomain: String, CaseIterable, Sendable, Equatable {
    case depth, breadth, coil, mesh
    public var title: String { PaperthinCatalog.group(rawValue)?.title ?? rawValue }
    public var axis: String { PaperthinCatalog.group(rawValue)?.axis ?? "" }
    public var question: String { PaperthinCatalog.group(rawValue)?.question ?? "" }
}

/// Removed with its last caller in the app module (docs/mighty-styles.md §6.3).
public struct PaperthinSkill: Sendable, Equatable, Identifiable {
    public var name: String
    public var emoji: String
    public var domain: PaperthinDomain
    public var summary: String
    public var scope: String
    public var userInvoked: Bool
    public var readOnly: Bool
    public var id: String { name }
    init(_ action: StyleAction, domain: PaperthinDomain) {
        self.name = action.id; self.emoji = action.glyph ?? ""; self.domain = domain
        self.summary = action.help; self.scope = action.scope ?? ""
        self.userInvoked = action.flags.contains(.userInvoked); self.readOnly = action.flags.contains(.readOnly)
    }
}

/// Removed with its last caller in the app module (docs/mighty-styles.md §6.3).
public enum PaperthinCatalog {
    public static let style = MightyStyleIDs.paperthin
    public static var installCommand: String { manifest?.install?.command ?? "" }

    static var manifest: StyleManifest? { BundledStyles.shared.manifest(style) }
    static var evaluator: StyleEvaluator? { BundledStyles.shared.evaluator(style) }
    static func group(_ id: String) -> StyleGroup? { manifest?.group(id) }

    public static var skills: [PaperthinSkill] {
        guard let manifest else { return [] }
        return manifest.groups.flatMap { group in
            group.actions.compactMap { id in
                guard let action = manifest.action(id), let domain = PaperthinDomain(rawValue: group.id) else { return nil }
                return PaperthinSkill(action, domain: domain)
            }
        }
    }
    public static func skills(in domain: PaperthinDomain) -> [PaperthinSkill] { skills.filter { $0.domain == domain } }
    public static func skill(_ name: String) -> PaperthinSkill? { skills.first { $0.name == name } }
    public static func prompt(skill name: String, text: String = "") -> String? { evaluator?.prompt(actionId: name, text: text) }
    public static func skill(inPrompt prompt: String) -> PaperthinSkill? {
        guard case .action(let id)? = evaluator?.recognised(inPrompt: prompt) else { return nil }
        return skill(id)
    }
    public static func requestTitle(forInput input: String) -> String? { evaluator?.requestTitle(forInput: input) }

    public static func installed(home: URL = FileManager.default.homeDirectoryForCurrentUser, workspacePath: String? = nil) -> Bool {
        guard let manifest else { return false }
        return StylePrerequisiteProbe.evaluate(manifest.prerequisites, install: manifest.install, home: home,
                                               workspacePath: workspacePath, environment: [:]).ready
    }

    public static func recommendedCoilSkill(casebook: StyleCasebook?) -> String {
        let state: String
        if let casebook { state = casebook.files.contains("DESIGN.local.md") && casebook.files.contains("RETRO.local.md") ? "complete" : "open" } else { state = "absent" }
        return evaluator?.recommendedAction(capabilityStates: [StyleCapabilityID.casebook: state], group: group("coil")) ?? "re0-plan"
    }
}

/// Removed with its last caller in the app module (docs/mighty-styles.md §6.3).
public typealias PaperthinCasebook = StyleCasebook
