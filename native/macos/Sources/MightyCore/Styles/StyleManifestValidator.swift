import Foundation

/// Step ④ of §2: everything that needs the whole file, and the two judgements
/// that differ for a bundled manifest (reserved ids and names, `ToolSearch`).
public enum StyleManifestValidator {
    public static func validate(_ manifest: StyleManifest, source: StyleSource, knownCapabilities: Set<String>) throws {
        try names(manifest, source: source)
        try capabilities(manifest, known: knownCapabilities)
        try references(manifest)
        try rules(manifest)
        try autoAllow(manifest, source: source)
    }

    private static func names(_ manifest: StyleManifest, source: StyleSource) throws {
        guard source != .bundled else { return }
        guard !MightyStyleIDs.reserved.contains(manifest.id) else { throw StyleErrors.reservedId(manifest.id) }
        // The user reads the name, not the id, so the name is reserved too (§1.2).
        let folded = StyleText.folded(manifest.name)
        guard !MightyStyleIDs.reservedNames.contains(where: { StyleText.folded($0) == folded }) else { throw StyleErrors.reservedName(manifest.name) }
    }

    private static func capabilities(_ manifest: StyleManifest, known: Set<String>) throws {
        for name in manifest.capabilities where !known.contains(name) { throw StyleErrors.unknownCapability(name) }
        for (name, map, path) in usedCapabilities(manifest) {
            guard manifest.capabilities.contains(name) else { throw StyleErrors.capabilityUndeclared(name) }
            for state in StyleCapabilityID.states(of: name) where map[state] == nil { throw StyleErrors.capabilityMap(state) }
            for state in map.keys.sorted() where !StyleCapabilityID.states(of: name).contains(state) {
                throw StyleErrors.unknownReference(path, state)
            }
        }
    }

    private static func usedCapabilities(_ manifest: StyleManifest) -> [(String, [String: String], String)] {
        var used: [(String, [String: String], String)] = []
        if case .capability(let name, let map, _) = manifest.rules.recommend { used.append((name, map, "rules.recommend.map")) }
        if case .capabilityState(let name, let map) = manifest.rules.initialGroup { used.append((name, map, "rules.initialGroup.map")) }
        return used
    }

    private static func references(_ manifest: StyleManifest) throws {
        let actionIds = Set(manifest.actions.map(\.id))
        let phaseIds = Set(manifest.phases.map(\.id))
        let matches = manifest.actions.compactMap(\.match)
        if let job = manifest.job {
            for id in job.whileOpen where !actionIds.contains(id) {
                throw StyleErrors.unknownReference("job.whileOpen", id)
            }
        }

        for group in manifest.groups {
            for action in group.actions where !actionIds.contains(action) {
                throw StyleErrors.unknownReference("groups." + group.id + ".actions", action)
            }
        }
        for action in manifest.actions {
            if let phase = action.phase, !phaseIds.contains(phase) { throw StyleErrors.unknownReference("actions." + action.id + ".phase", phase) }
        }
        for alias in manifest.aliases {
            guard phaseIds.contains(alias.phase) else { throw StyleErrors.unknownReference("aliases." + alias.name + ".phase", alias.phase) }
            // One recognised name may not carry two meanings (§1.4).
            guard !actionIds.contains(alias.name), !matches.contains(alias.name) else { throw StyleErrors.aliasCollision(alias.name) }
        }
        var seenMatch: Set<String> = []
        for action in manifest.actions {
            guard let match = action.match else { continue }
            // §1.3.4 forbids a clash with *another* action's id; naming itself
            // is redundant but harmless.
            guard !actionIds.subtracting([action.id]).contains(match), seenMatch.insert(match).inserted else { throw StyleErrors.aliasCollision(match) }
        }
        // Recognition lowercases the name it reads, but the lookup tables are
        // keyed by the id, the `match` and the alias name as written. An
        // uppercase id under this rule would pass every other check and then
        // never be recognised at runtime (§1.3.4, §1.4).
        if manifest.recognition.lowercase {
            for action in manifest.actions {
                guard action.id == action.id.lowercased(), action.match == action.match?.lowercased() else {
                    throw StyleErrors.promptRecognition(action.id)
                }
            }
            for alias in manifest.aliases where alias.name != alias.name.lowercased() {
                throw StyleErrors.promptRecognition(alias.name)
            }
        }
        // A prompt the recognition rule cannot read back is an action that is
        // never recognised: no title, no phase move, nothing to notice it (§1.3.4).
        for action in manifest.actions {
            let bare = action.prompt(text: "")
            let name = manifest.recognition.name(inPrompt: bare)
            let expected = manifest.recognition.lowercase ? [action.id.lowercased(), action.match?.lowercased()] : [action.id, action.match]
            guard let name, expected.contains(name) else { throw StyleErrors.promptRecognition(action.id) }
        }
    }

    private static func rules(_ manifest: StyleManifest) throws {
        let actionIds = Set(manifest.actions.map(\.id))
        let phaseIds = Set(manifest.phases.map(\.id))
        let groupIds = Set(manifest.groups.map(\.id))

        switch manifest.rules.start {
        case .none: break
        case .actions(let phase, let actions, _):
            guard phaseIds.contains(phase) else { throw StyleErrors.startPhase(phase) }
            guard !actions.isEmpty else { throw StyleErrors.missingField("rules.start.actions") }
            for action in actions where !actionIds.contains(action) { throw StyleErrors.unknownReference("rules.start.actions", action) }
        }

        switch manifest.rules.phase {
        case .none:
            guard manifest.phases.isEmpty else { throw StyleErrors.phaseRuleNone }
        case .lastRecognisedAction(let fallback):
            guard !manifest.phases.isEmpty else { throw StyleErrors.unknownReference("rules.phase.default", fallback) }
            guard phaseIds.contains(fallback) else { throw StyleErrors.unknownReference("rules.phase.default", fallback) }
        }

        switch manifest.rules.next {
        case .byGroup: break
        case .byPhase(let map):
            for phase in manifest.phases where map[phase.id] == nil { throw StyleErrors.ruleIncomplete(phase.id) }
            for key in map.keys.sorted() {
                guard phaseIds.contains(key) else { throw StyleErrors.unknownReference("rules.next.map", key) }
                for action in map[key] ?? [] where !actionIds.contains(action) { throw StyleErrors.unknownReference("rules.next.map." + key, action) }
            }
        }

        switch manifest.rules.enter {
        case .verbatim:
            guard manifest.placeholders.initial == nil else { throw StyleErrors.placeholderInitial }
        case .rewriteBareDraftTo(let action, let phase):
            guard let target = manifest.action(action) else { throw StyleErrors.unknownReference("rules.enter.action", action) }
            guard target.takesText else { throw StyleErrors.enterActionText(action) }
            guard phaseIds.contains(phase) else { throw StyleErrors.unknownReference("rules.enter.phase", phase) }
        }

        if case .capability(_, let map, let group) = manifest.rules.recommend {
            for action in map.values.sorted() where !actionIds.contains(action) { throw StyleErrors.unknownReference("rules.recommend.map", action) }
            if let group, !groupIds.contains(group) { throw StyleErrors.unknownReference("rules.recommend.group", group) }
        }
        switch manifest.rules.initialGroup {
        case .fixed(let group):
            guard groupIds.contains(group) else { throw StyleErrors.unknownReference("rules.initialGroup.group", group) }
        case .capabilityState(_, let map):
            for group in map.values.sorted() where !groupIds.contains(group) { throw StyleErrors.unknownReference("rules.initialGroup.map", group) }
        }
    }

    private static func autoAllow(_ manifest: StyleManifest, source: StyleSource) throws {
        // Only a probe whose prefix ends with `@` establishes ownership: a bare
        // prefix matches installed plugin keys by `hasPrefix`, so `a` would
        // claim `a_b` and every `plugin_a_b_*` server with it (§1.9).
        let plugins = manifest.prerequisites.probes.compactMap(\.pluginName)
        var seen: Set<String> = []
        for entry in manifest.autoAllow {
            guard entry.tool != "AskUserQuestion" else { throw StyleErrors.autoAllowQuestion }
            guard entry.tool.range(of: "^[A-Za-z0-9_-]{1,64}$", options: .regularExpression) != nil,
                  !entry.tool.contains("__"), !entry.tool.hasPrefix("_") else { throw StyleErrors.autoAllowShape(entry.tool) }
            guard let server = entry.server else {
                // Tool discovery is not MCP; it is the one key that pulls in new
                // tool schemas without a prompt, so only a bundle may have it.
                guard entry.tool == "ToolSearch" else { throw StyleErrors.autoAllowServer(entry.tool) }
                guard source == .bundled else { throw StyleErrors.autoAllowToolSearchBundled }
                guard seen.insert(entry.wireName).inserted else { throw StyleErrors.autoAllowDuplicate(entry.wireName) }
                continue
            }
            guard server.range(of: "^[A-Za-z0-9_-]{1,64}$", options: .regularExpression) != nil,
                  !server.contains("__"), !server.hasSuffix("_") else { throw StyleErrors.autoAllowShape(server) }
            guard plugins.contains(where: { server.hasPrefix("plugin_" + $0 + "_") }) else { throw StyleErrors.autoAllowForeignServer(entry.wireName) }
            guard seen.insert(entry.wireName).inserted else { throw StyleErrors.autoAllowDuplicate(entry.wireName) }
        }
    }
}

/// What used to be `MightyStyles`: the reserved ids and the shape check, all
/// that is left once the registry owns resolution (§5.8).
public enum MightyStyleIDs {
    public static let ouroboros = "ouroboros"
    public static let paperthin = "paperthin"
    public static let cli = "cli"
    /// `cli` is the wire word for "no style", so no manifest may claim it.
    public static let reserved: Set<String> = [cli, ouroboros, paperthin]
    public static let reservedNames = ["Ouroboros", "Paperthin"]
    public static func isValidShape(_ value: String) -> Bool {
        value.range(of: "^[a-z0-9][a-z0-9-]{0,39}$", options: .regularExpression) != nil
    }
}
