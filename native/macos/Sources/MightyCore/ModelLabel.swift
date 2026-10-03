import Foundation

/// The one display label for a model: family plus version (`Opus 5.5`,
/// `GPT-6.1 Sol`, `Gemini 3 Pro`). Only the label changes; the value stored and
/// sent to the CLI is never rewritten. The phone mirrors these rules in
/// `mobile/src/lib/model-label.ts`; both are held to
/// `native/contracts/fixtures/model-labels.json`.
public enum ModelLabel {
    static let oneMillionSuffix = " (1M)"

    /// A full model id read into its label, or nil when the id is not one of the
    /// shapes below (aliases, `default`, unknown names). Results are cached by id.
    /// - `claude-opus-5-5` → `Opus 5.5`, `claude-haiku-4-5-20251001` → `Haiku 4.5`,
    ///   `claude-3-5-sonnet-20241022` → `Sonnet 3.5`; Bedrock/Vertex wrappers
    ///   (`us.anthropic.…`, a Bedrock `…-v1:0`, `…@20250805`, `provider/…`) are
    ///   unwrapped. Any other trailing `v<n>` stays as a suffix (`Opus 4 v2`).
    /// - `gpt-5.2-codex` → `GPT-5.2 Codex` (a trailing `yyyy-mm-dd` is dropped).
    /// - `gemini-3-pro-preview` → `Gemini 3 Pro` (`preview`/`exp`/`latest` and what
    ///   follows, and number-only tags, are dropped).
    /// - A trailing `[1m]` adds ` (1M)`.
    public static func format(_ id: String) -> String? { cache.value(id, read) }

    /// The label for a model value. A full id is read with `format`. A Claude
    /// family alias (`opus`, `sonnet[1m]`, …) takes its version from `resolved`
    /// when that id is the same family, else it stays the bare family name — a
    /// version is never invented. Any other value (`default`, `opusplan`, `best`,
    /// custom names) keeps its own name (`fallback`, else the value) and adds the
    /// model it resolves to after ` · ` when `resolved` reads.
    public static func text(_ model: String, resolved: String? = nil, fallback: String? = nil) -> String {
        let value = model.trimmingCharacters(in: .whitespacesAndNewlines)
        if let label = format(value) { return label }
        let resolvedLabel = resolved.flatMap(format)
        var alias = value.lowercased()
        let oneMillion = alias.hasSuffix("[1m]")
        if oneMillion { alias = String(alias.dropLast(4)) }
        if families.contains(alias) {
            let family = capitalized(alias)
            let suffix = oneMillion ? oneMillionSuffix : ""
            guard let resolvedLabel, resolvedLabel.hasPrefix(family + " ") else { return family + suffix }
            return resolvedLabel.hasSuffix(oneMillionSuffix) ? resolvedLabel : resolvedLabel + suffix
        }
        let name = fallback.flatMap { $0.isEmpty ? nil : $0 } ?? value
        guard let resolvedLabel else { return name }
        return name + " · " + resolvedLabel
    }

    /// The label a picker row and a display of that row both use. `hint`
    /// (a model the CLI reported for this very selection) only reaches a bare
    /// family alias the catalogue does not resolve.
    public static func option(_ option: ModelOption, hint: String? = nil) -> String {
        text(option.value, resolved: option.resolvedModel ?? familyHint(option.value, hint), fallback: option.displayName)
    }

    /// The label for a model against its catalogue (see `option`).
    public static func text(_ model: String, catalog: ModelCatalog?, hint: String? = nil) -> String {
        if let option = catalog?.models.first(where: { $0.value == model }) { return self.option(option, hint: hint) }
        return text(model, resolved: familyHint(model, hint))
    }

    /// The id `text(_:catalog:hint:)` reads a model through, or nil — what the
    /// host sends the phone so it can label the model with the same rules.
    public static func resolution(_ model: String, catalog: ModelCatalog?, hint: String? = nil) -> String? {
        catalog?.models.first(where: { $0.value == model })?.resolvedModel ?? familyHint(model, hint)
    }

    // MARK: A pane's model

    /// The model the CLI reported for the pane, only while the model it was
    /// reported for is still the pane's selection. Usage saved before the
    /// selection was recorded gives none.
    public static func reportedModel(_ session: RunSession) -> String? {
        guard let usage = session.sessionUsage, usage.provider == session.provider, usage.selectedModel == session.model else { return nil }
        return usage.model
    }

    /// The pane's selection as displays show it (chip, dashboard, settings,
    /// status line): the picker row's label without the picker-only marks.
    public static func selection(_ session: RunSession, catalog: ModelCatalog) -> String {
        if session.model == "default", !catalog.models.contains(where: { $0.value == "default" }) { return cliDefaultName }
        return text(session.model, catalog: catalog, hint: reportedModel(session))
    }

    /// Picker rows (composer, `/model`, phone) with their label in
    /// `displayName`. Values are untouched; the reported model labels only the
    /// selected row; a saved model the catalogue lacks is marked as such.
    public static func pickerOptions(_ session: RunSession, catalog: ModelCatalog) -> [ModelOption] {
        let hint = reportedModel(session)
        var options = catalog.models.map { option in
            var labelled = option
            labelled.displayName = self.option(option, hint: option.value == session.model ? hint : nil)
            return labelled
        }
        if !options.contains(where: { $0.value == "default" }) { options.insert(ModelOption(value: "default", displayName: cliDefaultName), at: 0) }
        if !options.contains(where: { $0.value == session.model }) { options.append(ModelOption(value: session.model, displayName: "\(selection(session, catalog: catalog)) · 저장된 모델")) }
        return options
    }

    /// `model.id` and `model.display_name` for a status line. A model reported
    /// for an earlier selection gives way to the current one.
    public static func statusLine(_ session: RunSession, catalog: ModelCatalog) -> (id: String, name: String) {
        let usage = session.sessionUsage?.provider == session.provider ? session.sessionUsage : nil
        let current = usage.flatMap { $0.selectedModel == nil || $0.selectedModel == session.model ? $0.model : nil }
        let id = current ?? catalog.models.first(where: { $0.value == session.model })?.resolvedModel ?? session.model
        return (id, format(id) ?? selection(session, catalog: catalog))
    }

    static let cliDefaultName = "CLI 기본값"

    // MARK: Rules

    static let families: Set<String> = ["opus", "sonnet", "haiku", "fable"]
    private static let cache = FormatCache()

    /// A reported id may stand in for a resolution only behind a family alias,
    /// where `text` also checks the family; `default`, `best` and the like may
    /// have meant another model on that run.
    private static func familyHint(_ model: String, _ hint: String?) -> String? {
        var alias = model.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if alias.hasSuffix("[1m]") { alias = String(alias.dropLast(4)) }
        return families.contains(alias) ? hint : nil
    }

    private static func read(_ id: String) -> String? {
        var core = id.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let oneMillion = core.hasSuffix("[1m]")
        if oneMillion { core = String(core.dropLast(4)) }
        if let slash = core.lastIndex(of: "/") { core = String(core[core.index(after: slash)...]) }
        if let wrapper = core.range(of: "anthropic.", options: .backwards) { core = String(core[wrapper.upperBound...]) }
        if let at = core.firstIndex(of: "@") { core = String(core[..<at]) }
        // Bedrock's revision suffix: `-v<n>:<n>` only.
        if let revision = core.range(of: "-v", options: .backwards) {
            let parts = core[revision.upperBound...].split(separator: ":", omittingEmptySubsequences: false)
            if parts.count == 2, parts.allSatisfy({ digits(String($0)) }) { core = String(core[..<revision.lowerBound]) }
        }
        let tokens = core.split(separator: "-", omittingEmptySubsequences: false).map(String.init)
        guard tokens.count >= 2, !tokens.contains(where: \.isEmpty) else { return nil }
        let label: String?
        switch tokens[0] {
        case "claude": label = claude(Array(tokens.dropFirst()))
        case "gpt": label = gpt(Array(tokens.dropFirst()))
        case "gemini": label = gemini(Array(tokens.dropFirst()))
        default: label = nil
        }
        return label.map { oneMillion ? $0 + oneMillionSuffix : $0 }
    }

    private static func capitalized(_ word: String) -> String { word.prefix(1).uppercased() + word.dropFirst() }
    private static func digits(_ token: String) -> Bool { !token.isEmpty && token.allSatisfy { $0.isASCII && $0.isNumber } }
    private static func startsWithDigit(_ token: String) -> Bool { token.first.map { $0.isASCII && $0.isNumber } == true }
    /// `v2` stays as written; other words get a capital.
    private static func revision(_ token: String) -> Bool { token.count >= 2 && token.hasPrefix("v") && digits(String(token.dropFirst())) }
    private static func word(_ token: String) -> String { revision(token) ? token : capitalized(token) }

    /// One word (the family) and numbers: up to two digits are version parts,
    /// eight digits are a release date and dropped; a last `v<n>` is a suffix.
    /// Anything else is unknown.
    private static func claude(_ tokens: [String]) -> String? {
        var family: String?
        var version: [String] = []
        var suffix = ""
        for (index, token) in tokens.enumerated() {
            if digits(token) {
                if token.count <= 2 { version.append(token) }
                else if token.count != 8 { return nil }
            } else if index == tokens.count - 1, family != nil, revision(token) {
                suffix = " " + token
            } else if family == nil, token.allSatisfy({ $0.isASCII && $0.isLetter }) {
                family = token
            } else { return nil }
        }
        guard let family, !version.isEmpty else { return nil }
        return capitalized(family) + " " + version.joined(separator: ".") + suffix
    }

    private static func gpt(_ tokens: [String]) -> String? {
        guard let version = tokens.first, startsWithDigit(version) else { return nil }
        var rest = Array(tokens.dropFirst())
        if rest.count >= 3, digits(rest[rest.count - 3]), rest[rest.count - 3].count == 4,
           digits(rest[rest.count - 2]), rest[rest.count - 2].count == 2, digits(rest[rest.count - 1]), rest[rest.count - 1].count == 2 {
            rest.removeLast(3)
        }
        return (["GPT-" + version] + rest.map(word)).joined(separator: " ")
    }

    private static func gemini(_ tokens: [String]) -> String? {
        guard let version = tokens.first, startsWithDigit(version) else { return nil }
        var words: [String] = []
        for token in tokens.dropFirst() {
            if ["preview", "exp", "latest"].contains(token) { break }
            if !digits(token) { words.append(word(token)) }
        }
        return (["Gemini", version] + words).joined(separator: " ")
    }
}

/// `format` runs on every redraw of every label; ids repeat.
private final class FormatCache: @unchecked Sendable {
    private let lock = NSLock()
    private var values: [String: String?] = [:]
    func value(_ id: String, _ make: (String) -> String?) -> String? {
        lock.lock()
        if let hit = values[id] { lock.unlock(); return hit }
        lock.unlock()
        let made = make(id)
        lock.lock()
        if values.count >= 512 { values.removeAll() }
        values[id] = made
        lock.unlock()
        return made
    }
}
