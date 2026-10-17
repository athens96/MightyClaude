import Foundation
import Testing
@testable import MightyCore

/// Which items an install runs, in what order, and the script that runs them.
struct SetupPlanTests {
    static func catalog() throws -> SetupCatalog {
        try SetupCatalogDecoder.decode(Data(contentsOf: SetupCatalogTests.fixtureURL))
    }

    static let labels = SetupScriptLabels(step: { "[\($0)/\($1)] \($2)" }, failed: "FAILED", waiting: "WAITING", done: "DONE")

    @Test func prerequisitesComeFirstAndOnlyWhenMissing() throws {
        let catalog = try Self.catalog()
        // Nothing is installed: gh needs Homebrew, which needs the command line tools.
        let nothing = SetupPlan.order(selected: ["gh"], catalog: catalog, platform: .macos, isSatisfied: { _ in false })
        #expect(nothing == ["git", "homebrew", "gh"])
        // Homebrew is there already: only gh runs.
        #expect(SetupPlan.order(selected: ["gh"], catalog: catalog, platform: .macos, isSatisfied: { $0 == "homebrew" }) == ["gh"])
        // The native Claude installer needs nothing else.
        #expect(SetupPlan.order(selected: ["claude"], catalog: catalog, platform: .macos, isSatisfied: { _ in false }) == ["claude"])
        // Paperthin needs node → Homebrew → git; Ouroboros needs Claude and uv.
        let styles = SetupPlan.order(selected: ["ouroboros", "paperthin"], catalog: catalog, platform: .macos, isSatisfied: { _ in false })
        #expect(styles.firstIndex(of: "git")! < styles.firstIndex(of: "homebrew")!)
        #expect(styles.firstIndex(of: "homebrew")! < styles.firstIndex(of: "node")!)
        #expect(styles.firstIndex(of: "node")! < styles.firstIndex(of: "paperthin")!)
        #expect(styles.firstIndex(of: "claude")! < styles.firstIndex(of: "ouroboros")!)
        #expect(styles.firstIndex(of: "uv")! < styles.firstIndex(of: "ouroboros")!)
        #expect(Set(styles) == ["git", "homebrew", "node", "paperthin", "claude", "uv", "ouroboros"])
        #expect(SetupPlan.addedPrerequisites(selected: ["ouroboros", "paperthin"], catalog: catalog, platform: .macos, isSatisfied: { _ in false })
                == ["git", "homebrew", "node", "claude", "uv"])
    }

    @Test func theOrderFollowsTheCatalogWhenNothingDependsOnAnything() throws {
        let catalog = try Self.catalog()
        let picked: Set<String> = ["glab", "codex", "tailscale", "agy"]
        #expect(SetupPlan.order(selected: picked, catalog: catalog, platform: .macos, isSatisfied: { _ in true }) == ["codex", "agy", "tailscale", "glab"])
        // An unknown ID is never planned.
        #expect(SetupPlan.order(selected: ["rm-rf"], catalog: catalog, platform: .macos, isSatisfied: { _ in false }).isEmpty)
        // No item has a Windows entry yet, so nothing is planned there.
        #expect(SetupPlan.order(selected: ["claude"], catalog: catalog, platform: .windows, isSatisfied: { _ in false }).isEmpty)
    }

    @Test func quotingKeepsEveryValueLiteral() {
        #expect(SetupScript.quote("plain") == "'plain'")
        #expect(SetupScript.quote("it's $(id) `x` \"y\"") == "'it'\\''s $(id) `x` \"y\"'")
        #expect(SetupScript.launchCommand(scriptPath: "/Users/a b/Application Support/x's.sh") == "/bin/bash '/Users/a b/Application Support/x'\\''s.sh'")
        // A quoted value read back by a real shell is the same text.
        let tricky = "a'b $(touch /tmp/never) ; `c` \\ \"d\""
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = ["-c", "printf '%s' " + SetupScript.quote(tricky)]
        let pipe = Pipe(); process.standardOutput = pipe
        try? process.run(); process.waitUntilExit()
        #expect(String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self) == tricky)
    }

    @Test func eachRecipeRunsExactlyItsCatalogCommand() throws {
        let catalog = try Self.catalog()
        func body(_ id: String) throws -> String? { SetupScript.body(try #require(catalog.item(id)?.platforms[.macos])) }
        #expect(try body("claude") == "mighty_script=\"$(curl -fsSL 'https://claude.ai/install.sh')\" && /bin/bash -c \"$mighty_script\"")
        #expect(try body("codex") == "mighty_script=\"$(curl -fsSL 'https://chatgpt.com/codex/install.sh')\" && /bin/sh -c \"$mighty_script\"")
        #expect(try body("node") == "brew install 'node'")
        #expect(try body("git") == "'xcode-select' '--install'; until /usr/bin/xcode-select -p >/dev/null 2>&1; do sleep 5; done")
        #expect(try body("ouroboros") == "claude plugin marketplace add 'Q00/ouroboros'; claude plugin install 'ouroboros@ouroboros' --scope user")
        #expect(try body("paperthin") == "'npx' 'skills@latest' 'add' 'LilMGenius/paperthin' '--global' '--agent' 'claude-code'")
        #expect(try body("gstack") == "mkdir -p \"$HOME\"/'.claude/skills' && git clone --single-branch --depth 1 'https://github.com/garrytan/gstack.git' \"$HOME\"/'.claude/skills/gstack' && cd \"$HOME\"/'.claude/skills/gstack' && './setup'")
        // A vendor page opens in the browser; nothing runs in the terminal.
        #expect(try body("tailscale") == nil)
        let entry = SetupPlatformEntry(probe: .executable(name: "x", minimumVersion: nil), install: .brewCask("x"), prerequisites: [], source: URL(string: "https://brew.sh")!)
        #expect(SetupScript.body(entry) == "brew install --cask 'x'")
        let npm = SetupPlatformEntry(probe: .executable(name: "x", minimumVersion: nil), install: .npmGlobal("@scope/x"), prerequisites: [], source: URL(string: "https://brew.sh")!)
        #expect(SetupScript.body(npm) == "npm install -g '@scope/x'")
    }

    @Test func theScriptHasAHeaderPerStepInOrderAndKeepsGoingAfterAFailure() throws {
        let catalog = try Self.catalog()
        let ids = SetupPlan.order(selected: ["gh", "tailscale", "claude"], catalog: catalog, platform: .macos, isSatisfied: { _ in false })
        let steps = SetupScript.steps(for: ids + ["not-in-catalog"], catalog: catalog, platform: .macos, title: { $0.id.uppercased() })
        // The vendor page and the unknown ID are not steps.
        #expect(steps.map(\.itemId) == ["claude", "git", "homebrew", "gh"])
        let script = SetupScript.render(steps: steps, labels: Self.labels, markerPath: "/tmp/it's done")
        let lines = script.split(separator: "\n").map(String.init)
        #expect(lines.first == "#!/bin/bash")
        #expect(lines.contains("export PATH=\"\(SetupScript.pathPrefix):$PATH\""))
        #expect(lines.contains("mighty_marker='/tmp/it'\\''s done'"))
        #expect(lines.contains("trap ': > \"$mighty_marker\"' EXIT"))
        let headers = lines.filter { $0.hasPrefix("printf '\\n\\033[1m%s\\033[0m\\n'") }
        #expect(headers == ["[1/4] CLAUDE", "[2/4] GIT", "[3/4] HOMEBREW", "[4/4] GH"].map { "printf '\\n\\033[1m%s\\033[0m\\n' '\($0)'" })
        // Each step runs in its own subshell and a failure only marks the run failed.
        for step in steps {
            #expect(lines.contains("if ( \(step.body) ); then :; else mighty_failed=1; printf '%s\\n' 'FAILED'; fi"))
        }
        #expect(lines.contains("printf '%s\\n' 'WAITING'"))
        #expect(lines.suffix(2) == ["printf '\\n%s\\n' 'DONE'", "exit $mighty_failed"])
        // Never sudo.
        #expect(!script.contains("sudo"))
    }

    @Test func theScriptRunsEveryStepEvenWhenOneFailsAndAlwaysLeavesTheMarker() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("setup-script-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let steps = [
            SetupScriptStep(itemId: "a", title: "first", body: "exit 7"),
            SetupScriptStep(itemId: "b", title: "second", body: "printf ran > " + SetupScript.quote(root.appendingPathComponent("second").path)),
        ]
        let marker = root.appendingPathComponent("done marker").path
        let scriptURL = root.appendingPathComponent("setup.sh")
        try Data(SetupScript.render(steps: steps, labels: Self.labels, markerPath: marker).utf8).write(to: scriptURL)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/bash")
        process.arguments = [scriptURL.path]
        process.standardOutput = FileHandle.nullDevice
        try process.run(); process.waitUntilExit()
        #expect(process.terminationStatus == 1)
        #expect(FileManager.default.fileExists(atPath: root.appendingPathComponent("second").path))
        #expect(FileManager.default.fileExists(atPath: marker))
    }
}
