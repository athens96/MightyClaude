import Foundation
import Testing
@testable import MightyCore

/// native/contracts/fixtures/setup-catalog.json, the setup screen's closed list,
/// and the decoder that refuses anything outside it.
struct SetupCatalogTests {
    static let root: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url
    }()
    static let fixtureURL = root.appendingPathComponent("native/contracts/fixtures/setup-catalog.json")
    static let bundledURL = root.appendingPathComponent("native/macos/Sources/MightyCore/Resources/Setup/setup-catalog.json")

    static func fixture() throws -> [String: Any] {
        try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: fixtureURL)) as? [String: Any])
    }

    /// The fixture with `edit` applied to its first item's macOS entry.
    static func decodeEditing(_ edit: (inout [String: Any]) -> Void) throws -> SetupCatalog {
        var root = try fixture()
        var items = try #require(root["items"] as? [[String: Any]])
        var item = items[0]
        edit(&item)
        items[0] = item
        root["items"] = items
        return try SetupCatalogDecoder.decode(JSONSerialization.data(withJSONObject: root))
    }

    static func setMac(_ item: inout [String: Any], _ key: String, _ value: Any) {
        var platforms = item["platforms"] as! [String: Any]
        var mac = platforms["macos"] as! [String: Any]
        mac[key] = value
        platforms["macos"] = mac
        item["platforms"] = platforms
    }

    @Test func theBundledCopyIsByteIdenticalToTheContract() throws {
        #expect(try Data(contentsOf: Self.fixtureURL) == Data(contentsOf: Self.bundledURL))
    }

    @Test func theAppFindsTheCatalogInItsResourceBundle() throws {
        #expect(ResourceHealthChecker.checkSetupCatalog().found)
        #expect(try SetupCatalog.bundled() == SetupCatalogDecoder.decode(Data(contentsOf: Self.fixtureURL)))
    }

    @Test func theCatalogParsesWithEveryGroupTheUserAskedFor() throws {
        let catalog = try SetupCatalogDecoder.decode(Data(contentsOf: Self.fixtureURL))
        let ids = catalog.items.map(\.id)
        for id in ["claude", "codex", "agy", "node", "git", "homebrew", "uv", "ouroboros", "superpowers", "paperthin", "omc", "gstack", "tailscale", "gh", "glab"] {
            #expect(ids.contains(id), "missing \(id)")
        }
        #expect(Set(catalog.items.map(\.group)) == Set(SetupGroup.allCases))
        // Every item has a Mac entry with an official source page.
        for item in catalog.items {
            let mac = try #require(item.platforms[.macos], "\(item.id) has no Mac entry")
            #expect(mac.source.scheme == "https")
            #expect(SetupCatalog.knownKeys.contains(item.labelKey) && SetupCatalog.knownKeys.contains(item.descriptionKey))
        }
        // Required is the command line tools; an agent CLI is needed too, but any one of them (SetupFirstRun).
        #expect(catalog.items.filter(\.required).map(\.id) == ["git"])
        #expect(catalog.item("claude")?.provider == "claude" && catalog.item("agy")?.provider == "gemini" && catalog.item("codex")?.provider == "codex")
        // Claude uses the native installer, which needs no Node.
        #expect(catalog.item("claude")?.platforms[.macos]?.prerequisites == [])
        #expect(catalog.item("claude")?.platforms[.macos]?.install == .officialScript(url: URL(string: "https://claude.ai/install.sh")!, shell: "bash"))
    }

    @Test func everyKnownKeyIsUsedByTheCatalogAndExistsInEveryLanguage() throws {
        let catalog = try SetupCatalogDecoder.decode(Data(contentsOf: Self.fixtureURL))
        let used = Set(catalog.items.flatMap { [$0.labelKey, $0.descriptionKey] })
        #expect(used == SetupCatalog.knownKeys)
        for language in ["ko", "en", "zh", "ja"] {
            let url = Self.root.appendingPathComponent("locales/\(language).json")
            let strings = try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: String])
            for key in used.union(SetupGroup.allCases.map(\.titleKey)) { #expect(strings[key]?.isEmpty == false, "\(language) misses \(key)") }
        }
    }

    @Test func scriptsAndPagesStayOnAllowListedHosts() throws {
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "officialScript", "url": "https://example.com/install.sh", "shell": "bash"]) }
        }
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "officialScript", "url": "http://claude.ai/install.sh", "shell": "bash"]) }
        }
        // A look-alike host or a query string is not the allowed host.
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "officialScript", "url": "https://claude.ai.example.com/install.sh", "shell": "bash"]) }
        }
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "officialScript", "url": "https://claude.ai/install.sh?x=$(id)", "shell": "bash"]) }
        }
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "vendorPage", "url": "https://evil.example/download"]) }
        }
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "skill", "repository": "https://gitlab.example/x.git", "folder": "x"]) }
        }
        // Windows runs PowerShell, the Mac bash or sh.
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "officialScript", "url": "https://claude.ai/install.sh", "shell": "zsh"]) }
        }
    }

    @Test func kindsAreClosed() throws {
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "shell", "command": "curl x | bash"]) }
        }
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "probe", ["kind": "command", "command": "which claude"]) }
        }
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { $0["afterInstall"] = ["kind": "runScript"] }
        }
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { $0["group"] = "misc" }
        }
        // An unknown key anywhere is refused, so nothing rides along unchecked.
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { $0["extra"] = true }
        }
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "brewFormula", "name": "node", "args": ["--HEAD"]]) }
        }
        // argv starts only the allowed programs, with plain arguments.
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "argv", "argv": ["bash", "-c", "id"]]) }
        }
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "argv", "argv": ["npx", "x;id"]]) }
        }
        // A skill's setup script stays inside its clone.
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "skill", "repository": "https://github.com/a/b.git", "folder": "b", "setup": "../evil"]) }
        }
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "skill", "repository": "https://github.com/a/b.git", "folder": "b", "setup": "/bin/sh"]) }
        }
        // Labels are keys the app knows, not free text.
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { $0["label"] = "Install everything" }
        }
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { $0["afterInstall"] = ["kind": "signIn", "provider": "someone"] }
        }
    }

    @Test func programsTakeOnlyTheirOwnOptionsAndGitHubOnlyTheCatalogsRepositories() throws {
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "argv", "argv": ["npx", "--yes", "skills@1.7.2"]]) }
        }
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "argv", "argv": ["xcode-select", "--switch", "/tmp"]]) }
        }
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "argv", "argv": ["npx", "--install"]]) }
        }
        // The owner/repo short form: no dot segments, and only a repository the catalog uses.
        for marketplace in ["Q00/..", "./ouroboros", "someone/ouroboros"] {
            #expect(throws: (any Error).self) {
                try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "claudePlugin", "marketplace": marketplace, "plugin": "ouroboros@ouroboros"]) }
            }
        }
        for url in ["https://github.com/someone/gstack.git", "https://raw.githubusercontent.com/someone/install/HEAD/install.sh", "https://github.com/garrytan", "https://github.com/../garrytan/gstack"] {
            #expect(throws: (any Error).self) {
                try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "officialScript", "url": url, "shell": "bash"]) }
            }
        }
        // A skill is pinned to a full commit.
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "skill", "repository": "https://github.com/garrytan/gstack.git", "folder": "gstack"]) }
        }
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "skill", "repository": "https://github.com/garrytan/gstack.git", "folder": "gstack", "commit": "d258bcf"]) }
        }
        // What the catalog does use still decodes.
        let ok = try Self.decodeEditing { Self.setMac(&$0, "install", ["kind": "claudePlugin", "marketplace": "Q00/ouroboros", "plugin": "ouroboros@ouroboros"]) }
        #expect(ok.items.first?.platforms[.macos]?.install == .claudePlugin(marketplace: "Q00/ouroboros", plugin: "ouroboros@ouroboros"))
    }

    @Test func prerequisitesMustExistAndNotFormACycle() throws {
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "prerequisites", ["nothing-like-this"]) }
        }
        #expect(throws: (any Error).self) {
            try Self.decodeEditing { Self.setMac(&$0, "prerequisites", ["claude"]) }
        }
        // claude → node → homebrew → git, while node already needs homebrew: homebrew → claude closes a loop.
        var root = try Self.fixture()
        var items = try #require(root["items"] as? [[String: Any]])
        for index in items.indices {
            if items[index]["id"] as? String == "claude" { Self.setMac(&items[index], "prerequisites", ["node"]) }
            if items[index]["id"] as? String == "homebrew" { Self.setMac(&items[index], "prerequisites", ["claude"]) }
        }
        root["items"] = items
        #expect(throws: (any Error).self) { try SetupCatalogDecoder.decode(JSONSerialization.data(withJSONObject: root)) }
    }

    @Test func repeatedIDsAndOtherSchemasAreRefused() throws {
        var root = try Self.fixture()
        var items = try #require(root["items"] as? [[String: Any]])
        items.append(items[0])
        root["items"] = items
        #expect(throws: (any Error).self) { try SetupCatalogDecoder.decode(JSONSerialization.data(withJSONObject: root)) }
        var other = try Self.fixture()
        other["schema"] = 2
        #expect(throws: (any Error).self) { try SetupCatalogDecoder.decode(JSONSerialization.data(withJSONObject: other)) }
    }
}
