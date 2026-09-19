import Foundation

/// The byte pass of §2's step ⓪. Nesting depth, duplicate keys and the
/// position of `schema` cannot be seen after parsing — a JSON object keeps
/// neither order nor duplicates — so this is the one place they live.
enum StyleManifestPreScan {
    private struct Frame {
        var isObject: Bool
        var label: String
        var keys: Set<String> = []
        var awaitingKey: Bool
        var lastKey: String?
    }

    static func scan(_ data: Data) throws {
        var stack: [Frame] = []
        let bytes = [UInt8](data)
        var position = 0
        while position < bytes.count {
            let byte = bytes[position]
            switch byte {
            case UInt8(ascii: "{"), UInt8(ascii: "["):
                let label = stack.last?.lastKey ?? ""
                stack.append(Frame(isObject: byte == UInt8(ascii: "{"), label: label, awaitingKey: byte == UInt8(ascii: "{")))
                if stack.count > StyleLimits.maximumDepth { throw StyleErrors.tooDeep }
                position += 1
            case UInt8(ascii: "}"), UInt8(ascii: "]"):
                if !stack.isEmpty { stack.removeLast() }
                position += 1
            case UInt8(ascii: ","):
                if !stack.isEmpty, stack[stack.count - 1].isObject { stack[stack.count - 1].awaitingKey = true }
                position += 1
            case UInt8(ascii: "\""):
                let (text, next) = try string(bytes, from: position)
                position = next
                guard !stack.isEmpty, stack[stack.count - 1].isObject, stack[stack.count - 1].awaitingKey else { continue }
                let depth = stack.count - 1
                if stack[depth].keys.contains(text) { throw StyleErrors.duplicateKey(path(stack, key: text)) }
                if depth == 0, stack[depth].keys.isEmpty, text != "schema" { throw StyleErrors.schemaNotFirst }
                stack[depth].keys.insert(text)
                stack[depth].lastKey = text
                stack[depth].awaitingKey = false
            default:
                position += 1
            }
        }
    }

    /// Reads one JSON string literal, honouring `\"`, and returns its raw text
    /// (escapes other than `\"` stay as written: key names never need them).
    private static func string(_ bytes: [UInt8], from start: Int) throws -> (String, Int) {
        var position = start + 1
        var scalars: [UInt8] = []
        while position < bytes.count {
            let byte = bytes[position]
            if byte == UInt8(ascii: "\\") {
                if position + 1 < bytes.count { scalars.append(bytes[position]); scalars.append(bytes[position + 1]) }
                position += 2
                continue
            }
            if byte == UInt8(ascii: "\"") { return (String(decoding: scalars, as: UTF8.self), position + 1) }
            scalars.append(byte)
            position += 1
        }
        throw StyleErrors.notJSON
    }

    private static func path(_ stack: [Frame], key: String) -> String {
        let labels = stack.map(\.label).filter { !$0.isEmpty }
        return (labels + [key]).joined(separator: ".")
    }
}

/// A hand-written pass over the `Any` tree: a synthesised `Codable` would drop
/// unknown keys silently and could not carry the `<경로>` every refusal names.
public enum StyleManifestDecoder {
    public static func decode(_ data: Data, source: StyleSource) throws -> StyleManifest {
        guard data.count <= StyleLimits.maximumBytes else { throw StyleErrors.tooLarge }
        try StyleManifestPreScan.scan(data)
        let parsed: Any
        do { parsed = try JSONSerialization.jsonObject(with: data, options: []) } catch { throw StyleErrors.notJSON }
        guard let root = parsed as? [String: Any] else { throw StyleErrors.notJSON }
        let manifest = try structure(root)
        try StyleManifestValidator.validate(manifest, source: source, knownCapabilities: StyleCapabilityID.all)
        return manifest
    }

    // MARK: - step ③

    private struct Reader {
        let path: String
        var fields: [String: Any]
        init(_ value: Any, at path: String) throws {
            guard let dictionary = value as? [String: Any] else { throw StyleErrors.type(path) }
            self.path = path; self.fields = dictionary
        }
        mutating func take(_ key: String) -> Any? { fields.removeValue(forKey: key) }
        func child(_ key: String) -> String { path.isEmpty ? key : path + "." + key }
        /// Whatever is left was never part of schema 1 (§1.1).
        func finish() throws {
            if let leftover = fields.keys.sorted().first { throw StyleErrors.unknownField(child(leftover)) }
        }
    }

    private static func isBoolean(_ value: Any) -> Bool { CFGetTypeID(value as CFTypeRef) == CFBooleanGetTypeID() }

    private static func string(_ value: Any?, at path: String, range: ClosedRange<Int>, separator: Bool = true) throws -> String {
        guard let value else { throw StyleErrors.missingField(path) }
        guard let text = value as? String, !isBoolean(value) else { throw StyleErrors.type(path) }
        if StyleText.containsBanned(text) { throw StyleErrors.controlChar(path) }
        if !separator, text.contains("\u{00B7}") { throw StyleErrors.reservedSeparator(path) }
        guard range.contains(text.count), text.count <= StyleLimits.maximumString else { throw StyleErrors.stringLength(path) }
        return text
    }

    private static func optionalString(_ value: Any?, at path: String, range: ClosedRange<Int>, separator: Bool = true) throws -> String? {
        guard value != nil else { return nil }
        return try string(value, at: path, range: range, separator: separator)
    }

    private static func bool(_ value: Any?, at path: String, default fallback: Bool? = nil) throws -> Bool {
        guard let value else {
            if let fallback { return fallback }
            throw StyleErrors.missingField(path)
        }
        guard isBoolean(value), let number = value as? NSNumber else { throw StyleErrors.type(path) }
        return number.boolValue
    }

    /// `1.0` and `1` are the same value to this parser; `1e308` is a finite
    /// Double whose `Int(_:)` traps, so the range is checked, never converted.
    private static func integer(_ value: Any?, at path: String) throws -> Int {
        guard let value else { throw StyleErrors.missingField(path) }
        guard !isBoolean(value), let number = value as? NSNumber else { throw StyleErrors.type(path) }
        let raw = number.doubleValue
        guard raw.isFinite, raw == raw.rounded(), abs(raw) <= 9_007_199_254_740_992 else { throw StyleErrors.type(path) }
        return Int(raw)
    }

    private static func array(_ value: Any?, at path: String, maximum: Int, minimum: Int = 0) throws -> [Any] {
        guard let value else { throw StyleErrors.missingField(path) }
        guard let items = value as? [Any] else { throw StyleErrors.type(path) }
        guard items.count >= minimum else { throw StyleErrors.missingField(path) }
        guard items.count <= maximum else { throw StyleErrors.limit(path, maximum) }
        return items
    }

    private static func identifier(_ value: String, pattern: String = "^[a-z0-9][a-z0-9-]{0,39}$") throws -> String {
        guard value.range(of: pattern, options: .regularExpression) != nil else { throw StyleErrors.idShape(value) }
        return value
    }

    private static func icon(_ value: Any?, at path: String) throws -> StyleIcon? {
        guard let raw = try optionalString(value, at: path, range: 1...StyleLimits.maximumString) else { return nil }
        guard let icon = StyleIcon(rawValue: raw) else { throw StyleErrors.unknownIcon(raw) }
        return icon
    }

    private static func tint(_ value: Any?, at path: String) throws -> StyleTint? {
        guard let raw = try optionalString(value, at: path, range: 1...StyleLimits.maximumString) else { return nil }
        guard let tint = StyleTint(rawValue: raw) else { throw StyleErrors.unknownTint(raw) }
        return tint
    }

    private static func structure(_ root: [String: Any]) throws -> StyleManifest {
        var reader = try Reader(root, at: "")
        guard let schemaValue = reader.take("schema") else { throw StyleErrors.schemaMissing }
        guard try integer(schemaValue, at: "schema") == 1 else { throw StyleErrors.schemaVersion }
        let id = try identifier(string(reader.take("id"), at: "id", range: 1...40))
        let name = try string(reader.take("name"), at: "name", range: 1...40, separator: false)
        let summary = try string(reader.take("summary"), at: "summary", range: 1...240)
        let subtitle = try string(reader.take("subtitle"), at: "subtitle", range: 1...80)
        let placeholders = try self.placeholders(reader.take("placeholders"))
        let guidance = try self.guidance(reader.take("guidance"))
        let prerequisites = try self.prerequisites(reader.take("prerequisites"))
        let install = try self.install(reader.take("install"))
        let phases = try self.phases(reader.take("phases"))
        let groups = try self.groups(reader.take("groups"))
        let actions = try self.actions(reader.take("actions"))
        let aliases = try self.aliases(reader.take("aliases"))
        let recognition = try self.recognition(reader.take("recognition"))
        let rules = try self.rules(reader.take("rules"))
        let capabilities = try self.capabilities(reader.take("capabilities"))
        let autoAllow = try self.autoAllow(reader.take("autoAllow"))
        let presentation = try self.presentation(reader.take("presentation"))
        try reader.finish()
        return StyleManifest(schema: 1, id: id, name: name, summary: summary, subtitle: subtitle, placeholders: placeholders,
                             guidance: guidance, prerequisites: prerequisites, install: install, phases: phases, groups: groups,
                             actions: actions, aliases: aliases, recognition: recognition, rules: rules,
                             capabilities: capabilities, autoAllow: autoAllow, presentation: presentation)
    }

    private static func placeholders(_ value: Any?) throws -> StylePlaceholders {
        guard let value else { throw StyleErrors.missingField("placeholders") }
        var reader = try Reader(value, at: "placeholders")
        let idle = try string(reader.take("idle"), at: "placeholders.idle", range: 0...120)
        let answering = try string(reader.take("answering"), at: "placeholders.answering", range: 0...120)
        let initial = try optionalString(reader.take("initial"), at: "placeholders.initial", range: 0...120)
        let running = try optionalString(reader.take("running"), at: "placeholders.running", range: 0...120)
        try reader.finish()
        return StylePlaceholders(idle: idle, answering: answering, initial: initial, running: running)
    }

    private static func guidance(_ value: Any?) throws -> StyleGuidance {
        guard let value else { throw StyleErrors.missingField("guidance") }
        var reader = try Reader(value, at: "guidance")
        let start = try optionalString(reader.take("start"), at: "guidance.start", range: 0...160)
        let next = try optionalString(reader.take("next"), at: "guidance.next", range: 0...160)
        let running = try optionalString(reader.take("running"), at: "guidance.running", range: 0...160)
        try reader.finish()
        for (path, text) in [("guidance.start", start), ("guidance.next", next), ("guidance.running", running)] {
            guard let text else { continue }
            guard StyleGuidanceTemplate.isValid(text) else { throw StyleErrors.promptPlaceholder(path) }
        }
        return StyleGuidance(start: start, next: next, running: running)
    }

    private static func probeName(_ value: String) throws -> String {
        guard value.range(of: "^[A-Za-z0-9_][A-Za-z0-9@._-]{0,63}$", options: .regularExpression) != nil else { throw StyleErrors.probeNameShape(value) }
        return value
    }

    private static func prerequisites(_ value: Any?) throws -> StylePrerequisites {
        guard let value else { throw StyleErrors.missingField("prerequisites") }
        var reader = try Reader(value, at: "prerequisites")
        let rawMode = try string(reader.take("mode"), at: "prerequisites.mode", range: 1...16)
        guard let mode = StylePrerequisites.Mode(rawValue: rawMode) else { throw StyleErrors.unknownRule("prerequisites.mode", rawMode) }
        var report = StylePrerequisites.Report.first
        if let rawReport = try optionalString(reader.take("report"), at: "prerequisites.report", range: 1...16) {
            guard let value = StylePrerequisites.Report(rawValue: rawReport) else { throw StyleErrors.unknownRule("prerequisites.report", rawReport) }
            report = value
        }
        var probes: [StyleProbe] = []
        for (index, item) in try array(reader.take("probes"), at: "prerequisites.probes", maximum: StyleLimits.maximumProbes).enumerated() {
            let path = "prerequisites.probes[\(index)]"
            var probe = try Reader(item, at: path)
            let kind = try string(probe.take("kind"), at: path + ".kind", range: 1...32)
            let missing = try string(probe.take("missing"), at: path + ".missing", range: 1...200)
            let hint = try optionalString(probe.take("hint"), at: path + ".hint", range: 0...200)
            let install = try bool(probe.take("install"), at: path + ".install", default: true)
            switch kind {
            case "plugin":
                let prefix = try probeName(string(probe.take("prefix"), at: path + ".prefix", range: 1...64))
                probes.append(.plugin(prefix: prefix, missing: missing, hint: hint, install: install))
            case "executable":
                let name = try probeName(string(probe.take("name"), at: path + ".name", range: 1...64))
                probes.append(.executable(name: name, missing: missing, hint: hint, install: install))
            case "skill":
                let name = try probeName(string(probe.take("name"), at: path + ".name", range: 1...64))
                let raw = try array(probe.take("scopes"), at: path + ".scopes", maximum: 2)
                var scopes: [StyleScope] = []
                for entry in raw {
                    guard let text = entry as? String, let scope = StyleScope(rawValue: text), !scopes.contains(scope) else {
                        throw StyleErrors.scopes(raw.map { String(describing: $0) }.joined(separator: ","))
                    }
                    scopes.append(scope)
                }
                guard !scopes.isEmpty else { throw StyleErrors.scopes("[]") }
                probes.append(.skill(name: name, scopes: scopes, missing: missing, hint: hint, install: install))
            default:
                throw StyleErrors.unknownProbe(kind)
            }
            try probe.finish()
        }
        try reader.finish()
        return StylePrerequisites(mode: mode, report: report, probes: probes)
    }

    private static func install(_ value: Any?) throws -> StyleInstall? {
        guard let value else { return nil }
        var reader = try Reader(value, at: "install")
        let command = try string(reader.take("command"), at: "install.command", range: 1...400)
        let paneTitle = try string(reader.take("paneTitle"), at: "install.paneTitle", range: 1...40)
        try reader.finish()
        return StyleInstall(command: command, paneTitle: paneTitle)
    }

    private static func phases(_ value: Any?) throws -> [StylePhase] {
        var phases: [StylePhase] = []
        var orders: Set<Int> = []
        for (index, item) in try array(value, at: "phases", maximum: StyleLimits.maximumPhases).enumerated() {
            let path = "phases[\(index)]"
            var reader = try Reader(item, at: path)
            let id = try identifier(string(reader.take("id"), at: path + ".id", range: 1...40))
            let title = try string(reader.take("title"), at: path + ".title", range: 1...24, separator: false)
            let order = try integer(reader.take("order"), at: path + ".order")
            try reader.finish()
            guard (0...99).contains(order) else { throw StyleErrors.type(path + ".order") }
            guard !phases.contains(where: { $0.id == id }) else { throw StyleErrors.duplicateId("phases", id) }
            guard orders.insert(order).inserted else { throw StyleErrors.duplicateId(path + ".order", String(order)) }
            phases.append(StylePhase(id: id, title: title, order: order))
        }
        return phases
    }

    private static func groups(_ value: Any?) throws -> [StyleGroup] {
        var groups: [StyleGroup] = []
        for (index, item) in try array(value, at: "groups", maximum: StyleLimits.maximumGroups, minimum: 1).enumerated() {
            let path = "groups[\(index)]"
            var reader = try Reader(item, at: path)
            let id = try identifier(string(reader.take("id"), at: path + ".id", range: 1...40))
            let title = try string(reader.take("title"), at: path + ".title", range: 1...24)
            let axis = try optionalString(reader.take("axis"), at: path + ".axis", range: 0...60)
            let question = try optionalString(reader.take("question"), at: path + ".question", range: 0...200)
            let raw = try array(reader.take("actions"), at: path + ".actions", maximum: StyleLimits.maximumActions, minimum: 1)
            try reader.finish()
            let actions = try raw.map { try string($0, at: path + ".actions", range: 1...64) }
            guard !groups.contains(where: { $0.id == id }) else { throw StyleErrors.duplicateId("groups", id) }
            groups.append(StyleGroup(id: id, title: title, axis: axis, question: question, actions: actions))
        }
        return groups
    }

    private static func actions(_ value: Any?) throws -> [StyleAction] {
        var actions: [StyleAction] = []
        for (index, item) in try array(value, at: "actions", maximum: StyleLimits.maximumActions, minimum: 1).enumerated() {
            let path = "actions[\(index)]"
            var reader = try Reader(item, at: path)
            let id = try identifier(string(reader.take("id"), at: path + ".id", range: 1...64), pattern: "^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$")
            let title = try string(reader.take("title"), at: path + ".title", range: 1...40)
            let help = try string(reader.take("help"), at: path + ".help", range: 0...400)
            let scope = try optionalString(reader.take("scope"), at: path + ".scope", range: 0...120)
            let prompt = try string(reader.take("prompt"), at: path + ".prompt", range: 1...400)
            let takesText = try bool(reader.take("takesText"), at: path + ".takesText")
            let requiresText = try bool(reader.take("requiresText"), at: path + ".requiresText", default: false)
            let rawFold = try optionalString(reader.take("foldText"), at: path + ".foldText", range: 1...16)
            let match = try optionalString(reader.take("match"), at: path + ".match", range: 1...64)
            let phase = try optionalString(reader.take("phase"), at: path + ".phase", range: 1...40)
            var flags: Set<StyleActionFlag> = []
            for entry in try array(reader.take("flags") ?? [Any](), at: path + ".flags", maximum: 2) {
                guard let text = entry as? String, let flag = StyleActionFlag(rawValue: text), flags.insert(flag).inserted else {
                    throw StyleErrors.unknownFlag((entry as? String) ?? String(describing: entry))
                }
            }
            let icon = try self.icon(reader.take("icon"), at: path + ".icon")
            let glyph = try optionalString(reader.take("glyph"), at: path + ".glyph", range: 1...8)
            let tint = try self.tint(reader.take("tint"), at: path + ".tint")
            let requestTitle = try optionalString(reader.take("requestTitle"), at: path + ".requestTitle", range: 1...40, separator: false)
            try reader.finish()
            if let glyph, !StyleText.isEmojiGlyph(glyph) { throw StyleErrors.type(path + ".glyph") }
            // The two folds treat a phone's line and a Mac's paragraph
            // differently, so neither may win by default (§1.3).
            var fold: StyleFold?
            if takesText {
                guard let rawFold else { throw StyleErrors.missingField(path + ".foldText") }
                guard let value = StyleFold(rawValue: rawFold) else { throw StyleErrors.unknownRule(path + ".foldText", rawFold) }
                fold = value
            } else if rawFold != nil {
                throw StyleErrors.foldText(id)
            }
            try promptShape(prompt, takesText: takesText, id: id)
            guard !actions.contains(where: { $0.id == id }) else { throw StyleErrors.duplicateId("actions", id) }
            actions.append(StyleAction(id: id, title: title, help: help, scope: scope, prompt: prompt, takesText: takesText,
                                       requiresText: requiresText, foldText: fold, match: match, phase: phase, flags: flags,
                                       icon: icon, glyph: glyph, tint: tint, requestTitle: requestTitle))
        }
        return actions
    }

    /// `{text}` at most once and no other brace, so a future placeholder
    /// cannot be smuggled in (§1.3.1).
    private static func promptShape(_ prompt: String, takesText: Bool, id: String) throws {
        var remainder = prompt
        var count = 0
        while let range = remainder.range(of: "{text}") {
            count += 1
            remainder.replaceSubrange(range, with: "")
        }
        guard count <= 1, !remainder.contains("{"), !remainder.contains("}") else { throw StyleErrors.promptPlaceholder(id) }
        guard takesText == (count == 1) else { throw StyleErrors.takesTextMismatch(id) }
    }

    private static func aliases(_ value: Any?) throws -> [StyleAlias] {
        var aliases: [StyleAlias] = []
        for (index, item) in try array(value, at: "aliases", maximum: StyleLimits.maximumAliases).enumerated() {
            let path = "aliases[\(index)]"
            var reader = try Reader(item, at: path)
            let name = try string(reader.take("name"), at: path + ".name", range: 1...64)
            let phase = try string(reader.take("phase"), at: path + ".phase", range: 1...40)
            try reader.finish()
            guard !aliases.contains(where: { $0.name == name }) else { throw StyleErrors.duplicateId("aliases", name) }
            aliases.append(StyleAlias(name: name, phase: phase))
        }
        return aliases
    }

    private static func recognition(_ value: Any?) throws -> StyleRecognition {
        guard let value else { throw StyleErrors.missingField("recognition") }
        var reader = try Reader(value, at: "recognition")
        let raw = try array(reader.take("prefixes"), at: "recognition.prefixes", maximum: StyleLimits.maximumPrefixes, minimum: 1)
        let prefixes = try raw.map { try string($0, at: "recognition.prefixes", range: 1...32) }
        let lowercase = try bool(reader.take("lowercase"), at: "recognition.lowercase")
        try reader.finish()
        return StyleRecognition(prefixes: prefixes, lowercase: lowercase)
    }

    private static func capabilities(_ value: Any?) throws -> [String] {
        let raw = try array(value, at: "capabilities", maximum: StyleLimits.maximumCapabilities)
        return try raw.map { try string($0, at: "capabilities", range: 1...64) }
    }

    private static func autoAllow(_ value: Any?) throws -> [StyleAutoAllowEntry] {
        var entries: [StyleAutoAllowEntry] = []
        for (index, item) in try array(value, at: "autoAllow", maximum: StyleLimits.maximumAutoAllow).enumerated() {
            let path = "autoAllow[\(index)]"
            var reader = try Reader(item, at: path)
            let server = try optionalString(reader.take("server"), at: path + ".server", range: 1...64)
            let tool = try string(reader.take("tool"), at: path + ".tool", range: 1...64)
            try reader.finish()
            entries.append(StyleAutoAllowEntry(server: server, tool: tool))
        }
        return entries
    }

    private static func presentation(_ value: Any?) throws -> StylePresentation {
        guard let value else { throw StyleErrors.missingField("presentation") }
        var reader = try Reader(value, at: "presentation")
        let icon = try self.icon(reader.take("icon"), at: "presentation.icon")
        let tint = try self.tint(reader.take("tint"), at: "presentation.tint")
        try reader.finish()
        return StylePresentation(icon: icon, tint: tint)
    }

    private static func rules(_ value: Any?) throws -> StyleRules {
        guard let value else { throw StyleErrors.missingField("rules") }
        var reader = try Reader(value, at: "rules")
        let start = try startRule(reader.take("start"))
        let phase = try phaseRule(reader.take("phase"))
        let next = try nextRule(reader.take("next"))
        let enter = try enterRule(reader.take("enter"))
        let recommend = try recommendRule(reader.take("recommend"))
        let initialGroup = try initialGroupRule(reader.take("initialGroup"))
        try reader.finish()
        return StyleRules(start: start, phase: phase, next: next, enter: enter, recommend: recommend, initialGroup: initialGroup)
    }

    private static func kind(_ reader: inout Reader) throws -> String {
        try string(reader.take("kind"), at: reader.child("kind"), range: 1...32)
    }

    private static func startRule(_ value: Any?) throws -> StyleStartRule {
        guard let value else { throw StyleErrors.missingField("rules.start") }
        var reader = try Reader(value, at: "rules.start")
        let kind = try kind(&reader)
        switch kind {
        case "none": try reader.finish(); return .none
        case "actions":
            let phase = try string(reader.take("phase"), at: "rules.start.phase", range: 1...40)
            let raw = try array(reader.take("actions"), at: "rules.start.actions", maximum: StyleLimits.maximumActions, minimum: 1)
            let actions = try raw.map { try string($0, at: "rules.start.actions", range: 1...64) }
            let resetTitle = try optionalString(reader.take("resetTitle"), at: "rules.start.resetTitle", range: 1...24)
            try reader.finish()
            return .actions(phase: phase, actions: actions, resetTitle: resetTitle)
        default: throw StyleErrors.unknownRule("rules.start", kind)
        }
    }

    private static func phaseRule(_ value: Any?) throws -> StylePhaseRule {
        guard let value else { throw StyleErrors.missingField("rules.phase") }
        var reader = try Reader(value, at: "rules.phase")
        let kind = try kind(&reader)
        switch kind {
        case "none": try reader.finish(); return .none
        case "lastRecognisedAction":
            let fallback = try string(reader.take("default"), at: "rules.phase.default", range: 1...40)
            try reader.finish()
            return .lastRecognisedAction(default: fallback)
        default: throw StyleErrors.unknownRule("rules.phase", kind)
        }
    }

    private static func nextRule(_ value: Any?) throws -> StyleNextRule {
        guard let value else { throw StyleErrors.missingField("rules.next") }
        var reader = try Reader(value, at: "rules.next")
        let kind = try kind(&reader)
        switch kind {
        case "byGroup": try reader.finish(); return .byGroup
        case "byPhase":
            guard let raw = reader.take("map") as? [String: Any] else { throw StyleErrors.type("rules.next.map") }
            try reader.finish()
            var map: [String: [String]] = [:]
            for (phase, value) in raw {
                let items = try array(value, at: "rules.next.map." + phase, maximum: StyleLimits.maximumActions)
                map[phase] = try items.map { try string($0, at: "rules.next.map." + phase, range: 1...64) }
            }
            return .byPhase(map)
        default: throw StyleErrors.unknownRule("rules.next", kind)
        }
    }

    private static func enterRule(_ value: Any?) throws -> StyleEnterRule {
        guard let value else { throw StyleErrors.missingField("rules.enter") }
        var reader = try Reader(value, at: "rules.enter")
        let kind = try kind(&reader)
        switch kind {
        case "verbatim": try reader.finish(); return .verbatim
        case "rewriteBareDraftTo":
            let action = try string(reader.take("action"), at: "rules.enter.action", range: 1...64)
            let phase = try string(reader.take("phase"), at: "rules.enter.phase", range: 1...40)
            try reader.finish()
            return .rewriteBareDraftTo(action: action, phase: phase)
        default: throw StyleErrors.unknownRule("rules.enter", kind)
        }
    }

    private static func stateMap(_ value: Any?, at path: String) throws -> [String: String] {
        guard let raw = value as? [String: Any] else { throw StyleErrors.type(path) }
        var map: [String: String] = [:]
        for (state, target) in raw { map[state] = try string(target, at: path + "." + state, range: 1...64) }
        return map
    }

    private static func recommendRule(_ value: Any?) throws -> StyleRecommendRule {
        guard let value else { throw StyleErrors.missingField("rules.recommend") }
        var reader = try Reader(value, at: "rules.recommend")
        let kind = try kind(&reader)
        switch kind {
        case "none": try reader.finish(); return .none
        case "capability":
            let name = try string(reader.take("capability"), at: "rules.recommend.capability", range: 1...64)
            let map = try stateMap(reader.take("map"), at: "rules.recommend.map")
            let group = try optionalString(reader.take("group"), at: "rules.recommend.group", range: 1...40)
            try reader.finish()
            return .capability(name: name, map: map, group: group)
        default: throw StyleErrors.unknownRule("rules.recommend", kind)
        }
    }

    private static func initialGroupRule(_ value: Any?) throws -> StyleInitialGroupRule {
        guard let value else { throw StyleErrors.missingField("rules.initialGroup") }
        var reader = try Reader(value, at: "rules.initialGroup")
        let kind = try kind(&reader)
        switch kind {
        case "fixed":
            let group = try string(reader.take("group"), at: "rules.initialGroup.group", range: 1...40)
            try reader.finish()
            return .fixed(group: group)
        case "capabilityState":
            let name = try string(reader.take("capability"), at: "rules.initialGroup.capability", range: 1...64)
            let map = try stateMap(reader.take("map"), at: "rules.initialGroup.map")
            try reader.finish()
            return .capabilityState(name: name, map: map)
        default: throw StyleErrors.unknownRule("rules.initialGroup", kind)
        }
    }
}

/// `{phase}` is the only substitution a guidance line may carry (§1.7).
public enum StyleGuidanceTemplate {
    static func isValid(_ text: String) -> Bool {
        var remainder = text
        while let range = remainder.range(of: "{phase}") { remainder.replaceSubrange(range, with: "") }
        return !remainder.contains("{") && !remainder.contains("}")
    }

    /// With no phase, `{phase}` and the single U+0020 that follows it go too.
    public static func render(_ text: String, phaseTitle: String?) -> String {
        let parts = text.components(separatedBy: "{phase}")
        guard parts.count > 1 else { return text }
        if let phaseTitle { return parts.joined(separator: phaseTitle) }
        return ([parts[0]] + parts.dropFirst().map { $0.hasPrefix(" ") ? String($0.dropFirst()) : $0 }).joined()
    }
}
