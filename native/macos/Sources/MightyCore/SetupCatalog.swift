import Foundation

// The first-run setup screen's tools (필요한 도구), read from the catalog the app
// ships (native/contracts/fixtures/setup-catalog.json, bundled as
// Resources/Setup/setup-catalog.json). The decoder is closed: an unknown key,
// kind, host or executable is an error, so the catalog can only ask for what
// this file knows how to probe and install.

public enum SetupGroup: String, Sendable, CaseIterable {
    case agent, prerequisite, style, network, dev
    public var titleKey: String {
        switch self {
        case .agent: "setup.group.agent"
        case .prerequisite: "setup.group.prerequisite"
        case .style: "setup.group.style"
        case .network: "setup.group.network"
        case .dev: "setup.group.dev"
        }
    }
}

public enum SetupPlatform: String, Sendable, CaseIterable { case macos, windows }

public enum SetupAfterInstall: Sendable, Equatable {
    case none
    /// Probe again until it is there (a system dialog or a download finishes it).
    case reprobe
    /// Offer the provider's sign-in once the CLI is there.
    case signIn(provider: String)
}

public enum SetupProbe: Sendable, Equatable {
    /// On the login shell's PATH; with a minimum, `--version` must reach it.
    case executable(name: String, minimumVersion: String?)
    /// A user-scope Claude plugin whose ID starts with `prefix` (`ouroboros@`).
    case claudePlugin(prefix: String)
    /// ~/.claude/skills/<name>/SKILL.md.
    case skill(name: String)
    /// `xcode-select -p` names a folder that exists.
    case commandLineTools
    /// `name` in /Applications or ~/Applications, or `executable` on the PATH.
    case appBundle(name: String, executable: String?)
}

public enum SetupRecipe: Sendable, Equatable {
    /// One of `SetupCatalog.allowedExecutables` with fixed arguments. `finishesOutside`
    /// marks a command that only starts the install (a system dialog finishes it).
    case argv([String], finishesOutside: Bool)
    case officialScript(url: URL, shell: String)
    case brewFormula(String)
    case brewCask(String)
    case npmGlobal(String)
    case claudePlugin(marketplace: String, plugin: String)
    /// ~/.claude/skills/<folder> at the pinned `commit` (a fresh clone is made in a
    /// temporary folder and moved into place; an existing clone is moved to the
    /// commit), then `setup` (a path inside the clone) if any.
    case skill(repository: URL, folder: String, commit: String, setup: String?)
    /// Windows only: `winget install` of an exact package ID from the winget source,
    /// in `user` or `machine` scope. The Windows app writes the command; the Mac
    /// only decodes it so the shared catalog stays one closed file.
    case winget(id: String, scope: String)
    /// Nothing runs: the app opens the page.
    case vendorPage(URL)

    public var runsInTerminal: Bool { if case .vendorPage = self { return false }; return true }
}

public struct SetupPlatformEntry: Sendable, Equatable {
    public var probe: SetupProbe
    public var install: SetupRecipe
    public var prerequisites: [String]
    public var source: URL
    public init(probe: SetupProbe, install: SetupRecipe, prerequisites: [String], source: URL) {
        self.probe = probe; self.install = install; self.prerequisites = prerequisites; self.source = source
    }
}

public struct SetupItem: Sendable, Equatable, Identifiable {
    public var id: String
    public var group: SetupGroup
    public var labelKey: String
    public var descriptionKey: String
    public var required: Bool
    public var afterInstall: SetupAfterInstall
    public var platforms: [SetupPlatform: SetupPlatformEntry]
    public init(id: String, group: SetupGroup, labelKey: String, descriptionKey: String, required: Bool, afterInstall: SetupAfterInstall, platforms: [SetupPlatform: SetupPlatformEntry]) {
        self.id = id; self.group = group; self.labelKey = labelKey; self.descriptionKey = descriptionKey
        self.required = required; self.afterInstall = afterInstall; self.platforms = platforms
    }
    public var title: String { L(labelKey) }
    public var detail: String { L(descriptionKey) }
    /// The agent CLI's provider ID when this item is one.
    public var provider: String? { if case .signIn(let provider) = afterInstall { return provider }; return nil }
}

public struct SetupCatalog: Sendable, Equatable {
    public var items: [SetupItem]
    public init(items: [SetupItem]) { self.items = items }
    public func item(_ id: String) -> SetupItem? { items.first { $0.id == id } }

    /// The file's name in the MightyCore bundle's Setup/ folder.
    public static let fileName = "setup-catalog.json"

    /// Hosts an official script, a vendor page, a marketplace URL or a skill's
    /// repository may live on. The catalog cannot add one.
    public static let allowedHosts: Set<String> = [
        "claude.ai", "chatgpt.com", "antigravity.google", "raw.githubusercontent.com",
        "astral.sh", "bun.com", "github.com", "tailscale.com",
    ]
    /// The only programs an `argv` install may start, each with the only options
    /// (arguments starting with `-`) it may be given.
    public static let allowedFlags: [String: Set<String>] = [
        "xcode-select": ["--install"],
        "npx": ["--global", "--agent"],
    ]
    public static var allowedExecutables: Set<String> { Set(allowedFlags.keys) }
    /// The GitHub repositories a URL on github.com or raw.githubusercontent.com, or a
    /// marketplace's owner/repo short form, may name: only those the catalog uses.
    public static let allowedRepositories: Set<String> = [
        "Homebrew/install", "Q00/ouroboros", "anthropics/claude-plugins-community",
        "Yeachan-Heo/oh-my-claudecode", "garrytan/gstack",
    ]

    /// Every label and description key a catalog item may name. Kept here so an
    /// item cannot point at an arbitrary string, and so the locale check sees each
    /// key used.
    public static let knownKeys: Set<String> = [
        "setup.item.claude.title", "setup.item.claude.detail",
        "setup.item.codex.title", "setup.item.codex.detail",
        "setup.item.agy.title", "setup.item.agy.detail",
        "setup.item.git.title", "setup.item.git.detail",
        "setup.item.homebrew.title", "setup.item.homebrew.detail",
        "setup.item.node.title", "setup.item.node.detail",
        "setup.item.bun.title", "setup.item.bun.detail",
        "setup.item.uv.title", "setup.item.uv.detail",
        "setup.item.ouroboros.title", "setup.item.ouroboros.detail",
        "setup.item.superpowers.title", "setup.item.superpowers.detail",
        "setup.item.paperthin.title", "setup.item.paperthin.detail",
        "setup.item.omc.title", "setup.item.omc.detail",
        "setup.item.gstack.title", "setup.item.gstack.detail",
        "setup.item.tailscale.title", "setup.item.tailscale.detail",
        "setup.item.gh.title", "setup.item.gh.detail",
        "setup.item.glab.title", "setup.item.glab.detail",
    ]

    /// The catalog bundled with the app.
    public static func bundled() throws -> SetupCatalog {
        let result = ResourceHealthChecker.checkSetupCatalog()
        guard let path = result.resolvedPath else { throw MightyError(result.warning ?? "missing Setup/\(fileName)") }
        return try SetupCatalogDecoder.decode(Data(contentsOf: URL(fileURLWithPath: path)))
    }
}

public enum SetupCatalogDecoder {
    public static func decode(_ data: Data) throws -> SetupCatalog {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw fail("the catalog is not a JSON object") }
        try keys(root, allowed: ["schema", "note", "items"], at: "catalog")
        guard root["schema"] as? Int == 1 else { throw fail("schema must be 1") }
        guard let rawItems = root["items"] as? [[String: Any]], !rawItems.isEmpty, rawItems.count <= 64 else { throw fail("items must be 1 to 64 objects") }
        var items: [SetupItem] = []
        for (index, raw) in rawItems.enumerated() { items.append(try item(raw, at: "items[\(index)]")) }
        let ids = items.map(\.id)
        guard Set(ids).count == ids.count else { throw fail("item IDs repeat") }
        for item in items {
            for (platform, entry) in item.platforms {
                for prerequisite in entry.prerequisites {
                    guard prerequisite != item.id, ids.contains(prerequisite) else { throw fail("\(item.id): unknown prerequisite \(prerequisite) on \(platform.rawValue)") }
                }
            }
        }
        let catalog = SetupCatalog(items: items)
        for platform in SetupPlatform.allCases {
            guard SetupPlan.hasNoCycle(catalog: catalog, platform: platform) else { throw fail("prerequisites form a cycle on \(platform.rawValue)") }
        }
        return catalog
    }

    private static func item(_ raw: [String: Any], at path: String) throws -> SetupItem {
        try keys(raw, allowed: ["id", "group", "label", "description", "need", "afterInstall", "platforms"], at: path)
        let id = try string(raw["id"], at: path + ".id")
        guard id.range(of: "^[a-z][a-z0-9-]{0,31}$", options: .regularExpression) != nil else { throw fail("\(path).id is not a plain ID") }
        guard let group = SetupGroup(rawValue: try string(raw["group"], at: path + ".group")) else { throw fail("\(path).group is unknown") }
        let label = try string(raw["label"], at: path + ".label"), description = try string(raw["description"], at: path + ".description")
        guard SetupCatalog.knownKeys.contains(label), SetupCatalog.knownKeys.contains(description) else { throw fail("\(path) names a label key the app does not know") }
        let need = try string(raw["need"], at: path + ".need")
        guard ["required", "optional"].contains(need) else { throw fail("\(path).need must be required or optional") }
        let after = try afterInstall(raw["afterInstall"], at: path + ".afterInstall")
        guard let rawPlatforms = raw["platforms"] as? [String: Any], !rawPlatforms.isEmpty else { throw fail("\(path).platforms is missing") }
        var platforms: [SetupPlatform: SetupPlatformEntry] = [:]
        for (key, value) in rawPlatforms {
            guard let platform = SetupPlatform(rawValue: key) else { throw fail("\(path).platforms.\(key) is not a platform") }
            platforms[platform] = try entry(value, platform: platform, at: "\(path).platforms.\(key)")
        }
        return SetupItem(id: id, group: group, labelKey: label, descriptionKey: description, required: need == "required", afterInstall: after, platforms: platforms)
    }

    private static func afterInstall(_ value: Any?, at path: String) throws -> SetupAfterInstall {
        guard let raw = value as? [String: Any] else { throw fail("\(path) is missing") }
        switch try string(raw["kind"], at: path + ".kind") {
        case "none": try keys(raw, allowed: ["kind"], at: path); return .none
        case "reprobe": try keys(raw, allowed: ["kind"], at: path); return .reprobe
        case "signIn":
            try keys(raw, allowed: ["kind", "provider"], at: path)
            let provider = try string(raw["provider"], at: path + ".provider")
            guard ProviderOptions.ids.contains(provider) else { throw fail("\(path).provider is not an agent") }
            return .signIn(provider: provider)
        case let kind: throw fail("\(path).kind \(kind) is unknown")
        }
    }

    private static func entry(_ value: Any, platform: SetupPlatform, at path: String) throws -> SetupPlatformEntry {
        guard let raw = value as? [String: Any] else { throw fail("\(path) is not an object") }
        try keys(raw, allowed: ["probe", "install", "prerequisites", "source"], at: path)
        guard let prerequisites = raw["prerequisites"] as? [String], Set(prerequisites).count == prerequisites.count else { throw fail("\(path).prerequisites must be distinct IDs") }
        guard let source = URL(string: try string(raw["source"], at: path + ".source")), source.scheme == "https", source.host != nil else { throw fail("\(path).source must be an https URL") }
        return SetupPlatformEntry(probe: try probe(raw["probe"], platform: platform, at: path + ".probe"),
                                  install: try recipe(raw["install"], platform: platform, at: path + ".install"),
                                  prerequisites: prerequisites, source: source)
    }

    private static func probe(_ value: Any?, platform: SetupPlatform, at path: String) throws -> SetupProbe {
        guard let raw = value as? [String: Any] else { throw fail("\(path) is missing") }
        switch try string(raw["kind"], at: path + ".kind") {
        case "executable":
            try keys(raw, allowed: ["kind", "name", "minimumVersion"], at: path)
            let name = try plainName(raw["name"], at: path + ".name")
            var minimum: String?
            if raw["minimumVersion"] != nil {
                minimum = try string(raw["minimumVersion"], at: path + ".minimumVersion")
                guard minimum?.range(of: "^[0-9]+(\\.[0-9]+){0,2}$", options: .regularExpression) != nil else { throw fail("\(path).minimumVersion is not a version") }
            }
            return .executable(name: name, minimumVersion: minimum)
        case "claudePlugin":
            try keys(raw, allowed: ["kind", "prefix"], at: path)
            let prefix = try string(raw["prefix"], at: path + ".prefix")
            guard prefix.range(of: "^[a-z0-9][a-z0-9._-]{0,63}@$", options: .regularExpression) != nil else { throw fail("\(path).prefix must name a whole plugin (name@)") }
            return .claudePlugin(prefix: prefix)
        case "skill":
            try keys(raw, allowed: ["kind", "name"], at: path)
            return .skill(name: try folderName(raw["name"], at: path + ".name"))
        case "commandLineTools":
            try keys(raw, allowed: ["kind"], at: path)
            guard platform == .macos else { throw fail("\(path): commandLineTools is a Mac probe") }
            return .commandLineTools
        case "appBundle":
            try keys(raw, allowed: ["kind", "name", "executable"], at: path)
            let bundle = try string(raw["name"], at: path + ".name")
            guard platform == .macos, bundle.range(of: "^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}\\.app$", options: .regularExpression) != nil else { throw fail("\(path).name must be an app bundle's name") }
            let executable = raw["executable"] == nil ? nil : try plainName(raw["executable"], at: path + ".executable")
            return .appBundle(name: bundle, executable: executable)
        case let kind: throw fail("\(path).kind \(kind) is unknown")
        }
    }

    private static func recipe(_ value: Any?, platform: SetupPlatform, at path: String) throws -> SetupRecipe {
        guard let raw = value as? [String: Any] else { throw fail("\(path) is missing") }
        switch try string(raw["kind"], at: path + ".kind") {
        case "argv":
            try keys(raw, allowed: ["kind", "argv", "finishesOutside"], at: path)
            guard let argv = raw["argv"] as? [String], let first = argv.first, argv.count <= 16,
                  SetupCatalog.allowedExecutables.contains(first) else { throw fail("\(path).argv must start with an allowed program") }
            for argument in argv.dropFirst() {
                guard argument.range(of: "^[A-Za-z0-9@._/:=+-]{1,128}$", options: .regularExpression) != nil else {
                    throw fail("\(path).argv has an argument with characters the catalog does not allow")
                }
                if argument.hasPrefix("-"), SetupCatalog.allowedFlags[first]?.contains(argument) != true {
                    throw fail("\(path).argv gives \(first) an option it may not take: \(argument)")
                }
            }
            let outside = raw["finishesOutside"] == nil ? false : try bool(raw["finishesOutside"], at: path + ".finishesOutside")
            return .argv(argv, finishesOutside: outside)
        case "officialScript":
            try keys(raw, allowed: ["kind", "url", "shell"], at: path)
            let url = try allowedURL(raw["url"], at: path + ".url")
            let shell = try string(raw["shell"], at: path + ".shell")
            let shells: Set<String> = platform == .macos ? ["bash", "sh"] : ["powershell"]
            guard shells.contains(shell) else { throw fail("\(path).shell \(shell) is not one this platform runs") }
            return .officialScript(url: url, shell: shell)
        case "brewFormula":
            try keys(raw, allowed: ["kind", "name"], at: path)
            guard platform == .macos else { throw fail("\(path): Homebrew is a Mac install") }
            return .brewFormula(try packageName(raw["name"], at: path + ".name"))
        case "brewCask":
            try keys(raw, allowed: ["kind", "name"], at: path)
            guard platform == .macos else { throw fail("\(path): Homebrew is a Mac install") }
            return .brewCask(try packageName(raw["name"], at: path + ".name"))
        case "npmGlobal":
            try keys(raw, allowed: ["kind", "package"], at: path)
            return .npmGlobal(try packageName(raw["package"], at: path + ".package"))
        case "claudePlugin":
            try keys(raw, allowed: ["kind", "marketplace", "plugin"], at: path)
            let marketplace = try string(raw["marketplace"], at: path + ".marketplace")
            let isRepository = marketplace.range(of: "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}/[A-Za-z0-9._-]{1,100}$", options: .regularExpression) != nil
            if isRepository {
                let parts = marketplace.split(separator: "/").map(String.init)
                guard !parts.contains(where: { $0 == "." || $0 == ".." }), SetupCatalog.allowedRepositories.contains(marketplace) else {
                    throw fail("\(path).marketplace is not a repository the catalog may use")
                }
            } else { _ = try allowedURL(marketplace, at: path + ".marketplace") }
            let plugin = try string(raw["plugin"], at: path + ".plugin")
            guard plugin.range(of: "^[a-z0-9][a-z0-9._-]{0,63}@[a-z0-9][a-z0-9._-]{0,63}$", options: .regularExpression) != nil else { throw fail("\(path).plugin must be name@marketplace") }
            return .claudePlugin(marketplace: marketplace, plugin: plugin)
        case "skill":
            try keys(raw, allowed: ["kind", "repository", "folder", "commit", "setup"], at: path)
            let repository = try allowedURL(raw["repository"], at: path + ".repository")
            let folder = try folderName(raw["folder"], at: path + ".folder")
            let commit = try string(raw["commit"], at: path + ".commit")
            guard commit.range(of: "^[0-9a-f]{40}$", options: .regularExpression) != nil else { throw fail("\(path).commit must be a full commit SHA") }
            var setup: String?
            if raw["setup"] != nil {
                let value = try string(raw["setup"], at: path + ".setup")
                let parts = value.split(separator: "/", omittingEmptySubsequences: false)
                guard !value.hasPrefix("/"), !parts.contains(where: { $0.isEmpty || $0 == "." || $0 == ".." }),
                      value.range(of: "^[A-Za-z0-9._/-]{1,128}$", options: .regularExpression) != nil else { throw fail("\(path).setup must be a path inside the clone") }
                setup = value
            }
            return .skill(repository: repository, folder: folder, commit: commit, setup: setup)
        case "winget":
            try keys(raw, allowed: ["kind", "id", "scope"], at: path)
            guard platform == .windows else { throw fail("\(path): winget is a Windows install") }
            let id = try string(raw["id"], at: path + ".id")
            guard id.range(of: "^[A-Za-z0-9][A-Za-z0-9-]{0,63}(\\.[A-Za-z0-9][A-Za-z0-9-]{0,63}){1,3}$", options: .regularExpression) != nil else { throw fail("\(path).id is not a winget package ID") }
            let scope = try string(raw["scope"], at: path + ".scope")
            guard ["user", "machine"].contains(scope) else { throw fail("\(path).scope must be user or machine") }
            return .winget(id: id, scope: scope)
        case "vendorPage":
            try keys(raw, allowed: ["kind", "url"], at: path)
            return .vendorPage(try allowedURL(raw["url"], at: path + ".url"))
        case let kind: throw fail("\(path).kind \(kind) is unknown")
        }
    }

    // MARK: Fields

    private static func fail(_ message: String) -> MightyError { MightyError("setup-catalog.json: " + message) }

    private static func keys(_ raw: [String: Any], allowed: Set<String>, at path: String) throws {
        if let extra = raw.keys.sorted().first(where: { !allowed.contains($0) }) { throw fail("\(path) has an unknown key \(extra)") }
    }

    private static func string(_ value: Any?, at path: String) throws -> String {
        guard let text = value as? String, !text.isEmpty, text.count <= 512 else { throw fail("\(path) must be a string") }
        guard !text.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else { throw fail("\(path) has a control character") }
        return text
    }

    private static func bool(_ value: Any?, at path: String) throws -> Bool {
        guard let number = value as? NSNumber, CFGetTypeID(number) == CFBooleanGetTypeID() else { throw fail("\(path) must be true or false") }
        return number.boolValue
    }

    private static func plainName(_ value: Any?, at path: String) throws -> String {
        let name = try string(value, at: path)
        guard name.range(of: "^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$", options: .regularExpression) != nil else { throw fail("\(path) is not a program name") }
        return name
    }

    private static func folderName(_ value: Any?, at path: String) throws -> String {
        let name = try string(value, at: path)
        guard name.range(of: "^[a-z0-9][a-z0-9._-]{0,63}$", options: .regularExpression) != nil, name != ".." else { throw fail("\(path) is not a folder name") }
        return name
    }

    private static func packageName(_ value: Any?, at path: String) throws -> String {
        let name = try string(value, at: path)
        guard name.range(of: "^@?[a-z0-9][a-z0-9._-]{0,99}(/[a-z0-9][a-z0-9._-]{0,99})?$", options: .regularExpression) != nil else { throw fail("\(path) is not a package name") }
        return name
    }

    private static func allowedURL(_ value: Any?, at path: String) throws -> URL {
        let text = try string(value, at: path)
        guard let url = URL(string: text), url.scheme == "https", let host = url.host?.lowercased(),
              SetupCatalog.allowedHosts.contains(host), url.user == nil, url.password == nil, url.port == nil,
              text.range(of: "^https://[A-Za-z0-9./_-]+$", options: .regularExpression) != nil else {
            throw fail("\(path) must be an https URL on an allowed host")
        }
        // GitHub hosts many owners: only the repositories the catalog uses are let in.
        if ["github.com", "raw.githubusercontent.com"].contains(host) {
            let parts = url.path.split(separator: "/").map(String.init)
            guard parts.count >= 2, !parts.contains(where: { $0 == "." || $0 == ".." }) else { throw fail("\(path) does not name a repository") }
            let repository = parts[0] + "/" + (parts[1].hasSuffix(".git") ? String(parts[1].dropLast(4)) : parts[1])
            guard SetupCatalog.allowedRepositories.contains(repository) else { throw fail("\(path) names a repository the catalog may not use") }
        }
        return url
    }
}
