import Foundation

/// Which items an install runs and in what order: the picked ones plus every
/// prerequisite that is not there yet, prerequisites first, the catalog's order
/// breaking ties. Pure, so the screen and the tests share it.
public enum SetupPlan {
    public static func order(selected: Set<String>, catalog: SetupCatalog, platform: SetupPlatform, isSatisfied: (String) -> Bool) -> [String] {
        var included = Set<String>()
        func include(_ id: String) {
            guard let entry = catalog.item(id)?.platforms[platform], included.insert(id).inserted else { return }
            for prerequisite in entry.prerequisites where !isSatisfied(prerequisite) { include(prerequisite) }
        }
        for item in catalog.items where selected.contains(item.id) { include(item.id) }
        return topological(included, catalog: catalog, platform: platform) ?? catalog.items.map(\.id).filter(included.contains)
    }

    /// The prerequisites `order` adds that were not picked themselves.
    public static func addedPrerequisites(selected: Set<String>, catalog: SetupCatalog, platform: SetupPlatform, isSatisfied: (String) -> Bool) -> Set<String> {
        Set(order(selected: selected, catalog: catalog, platform: platform, isSatisfied: isSatisfied)).subtracting(selected)
    }

    static func hasNoCycle(catalog: SetupCatalog, platform: SetupPlatform) -> Bool {
        topological(Set(catalog.items.map(\.id)), catalog: catalog, platform: platform) != nil
    }

    /// Kahn's order over `ids`, earliest catalog item first among the ready ones; nil on a cycle.
    private static func topological(_ ids: Set<String>, catalog: SetupCatalog, platform: SetupPlatform) -> [String]? {
        let ordered = catalog.items.map(\.id).filter(ids.contains)
        var waiting = Dictionary(uniqueKeysWithValues: ordered.map { id in
            (id, Set((catalog.item(id)?.platforms[platform]?.prerequisites ?? []).filter(ids.contains)))
        })
        var result: [String] = []
        while !waiting.isEmpty {
            guard let next = ordered.first(where: { waiting[$0]?.isEmpty == true }) else { return nil }
            result.append(next)
            waiting.removeValue(forKey: next)
            for key in waiting.keys { waiting[key]?.remove(next) }
        }
        return result
    }
}

/// One item's part of the generated install script.
public struct SetupScriptStep: Sendable, Equatable, Identifiable {
    public var itemId: String
    public var title: String
    /// What runs, exactly as the script has it; the confirmation shows this.
    public var body: String
    public var id: String { itemId }
}

/// The words the script prints, in the app's language.
public struct SetupScriptLabels: Sendable {
    public var step: @Sendable (_ index: Int, _ count: Int, _ title: String) -> String
    public var failed: String
    public var waiting: String
    public var done: String
    public init(step: @escaping @Sendable (Int, Int, String) -> String, failed: String, waiting: String, done: String) {
        self.step = step; self.failed = failed; self.waiting = waiting; self.done = done
    }
}

/// Builds the one script the setup terminal runs, from catalog items only:
/// every value is single-quoted, and nothing the user typed or a file outside
/// the bundled catalog said reaches it. The app never adds sudo; a step that
/// needs a password asks for it in the terminal itself.
public enum SetupScript {
    /// Folders the steps need on PATH even when this shell's profile does not
    /// list them yet (an install earlier in the same run puts its tool there).
    public static let pathPrefix = "$HOME/.local/bin:$HOME/.bun/bin:/opt/homebrew/bin:/usr/local/bin"

    /// POSIX single quotes: the text is taken literally, a quote inside is closed, escaped and reopened.
    public static func quote(_ text: String) -> String {
        "'" + text.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    /// The steps for `ids` in that order. Items that only open a page, or have no
    /// entry for the platform, are left out.
    public static func steps(for ids: [String], catalog: SetupCatalog, platform: SetupPlatform, title: (SetupItem) -> String) -> [SetupScriptStep] {
        ids.compactMap { id in
            guard let item = catalog.item(id), let entry = item.platforms[platform], let body = body(entry) else { return nil }
            return SetupScriptStep(itemId: id, title: title(item), body: body)
        }
    }

    /// What one platform entry runs, or nil when nothing runs in the terminal.
    public static func body(_ entry: SetupPlatformEntry) -> String? {
        switch entry.install {
        case .argv(let argv, let finishesOutside):
            let command = argv.map(quote).joined(separator: " ")
            // The system dialog finishes the install; later steps wait for it.
            if finishesOutside, entry.probe == .commandLineTools {
                return command + "; until /usr/bin/xcode-select -p >/dev/null 2>&1; do sleep 5; done"
            }
            return command
        case .officialScript(let url, let shell):
            // Fetched first and handed to the shell as its script, so the script's
            // own questions (a password, a yes/no) still read the terminal.
            let interpreter = shell == "sh" ? "/bin/sh" : "/bin/bash"
            return "mighty_script=\"$(curl -fsSL \(quote(url.absoluteString)))\" && \(interpreter) -c \"$mighty_script\""
        case .brewFormula(let name): return "brew install " + quote(name)
        case .brewCask(let name): return "brew install --cask " + quote(name)
        case .npmGlobal(let name): return "npm install -g " + quote(name)
        case .claudePlugin(let marketplace, let plugin):
            // An already added marketplace is fine; the install decides.
            return "claude plugin marketplace add \(quote(marketplace)); claude plugin install \(quote(plugin)) --scope user"
        case .skill(let repository, let folder, let setup):
            let destination = "\"$HOME\"/" + quote(".claude/skills/" + folder)
            var body = "mkdir -p \"$HOME\"/" + quote(".claude/skills") + " && git clone --single-branch --depth 1 \(quote(repository.absoluteString)) \(destination)"
            if let setup { body += " && cd \(destination) && " + quote("./" + setup) }
            return body
        case .vendorPage: return nil
        }
    }

    /// The whole script. `markerPath` is written when the script ends for any
    /// reason (finished, a failed step, Control-C), which tells the app to look again.
    public static func render(steps: [SetupScriptStep], labels: SetupScriptLabels, markerPath: String) -> String {
        var lines = [
            "#!/bin/bash",
            "# Written by Mighty Claude from its bundled tool catalog (setup-catalog.json).",
            "export PATH=\"\(pathPrefix):$PATH\"",
            "mighty_marker=" + quote(markerPath),
            "trap ': > \"$mighty_marker\"' EXIT",
            "mighty_failed=0",
        ]
        for (index, step) in steps.enumerated() {
            lines.append("printf '\\n\\033[1m%s\\033[0m\\n' " + quote(labels.step(index + 1, steps.count, step.title)))
            if step.body.contains("xcode-select -p") { lines.append("printf '%s\\n' " + quote(labels.waiting)) }
            lines.append("if ( \(step.body) ); then :; else mighty_failed=1; printf '%s\\n' \(quote(labels.failed)); fi")
        }
        lines.append("printf '\\n%s\\n' " + quote(labels.done))
        lines.append("exit $mighty_failed")
        return lines.joined(separator: "\n") + "\n"
    }

    /// What the terminal types to run the written script.
    public static func launchCommand(scriptPath: String) -> String { "/bin/bash " + quote(scriptPath) }
}
