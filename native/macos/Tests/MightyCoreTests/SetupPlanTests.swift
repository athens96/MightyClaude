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
        // Items with no Windows entry (Homebrew, gstack) are never planned there; Windows has its own prerequisites.
        #expect(SetupPlan.order(selected: ["homebrew", "gstack"], catalog: catalog, platform: .windows, isSatisfied: { _ in false }).isEmpty)
        #expect(SetupPlan.order(selected: ["paperthin"], catalog: catalog, platform: .windows, isSatisfied: { _ in false }) == ["node", "paperthin"])
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
        let curl = "/usr/bin/curl --proto '=https' --proto-redir '=https' --tlsv1.2 -fsSL"
        #expect(try body("claude") == "mighty_script=\"$(\(curl) 'https://claude.ai/install.sh')\" && /bin/bash -c \"$mighty_script\"")
        #expect(try body("codex") == "mighty_script=\"$(\(curl) 'https://chatgpt.com/codex/install.sh')\" && /bin/sh -c \"$mighty_script\"")
        #expect(try body("node") == "brew install 'node'")
        // The system's own xcode-select, by its path; Control-C ends only the wait, which ends after half an hour anyway.
        #expect(try body("git") == "'/usr/bin/xcode-select' '--install'; trap 'break' INT; mighty_waited=0; until /usr/bin/xcode-select -p >/dev/null 2>&1 || [ \"$mighty_waited\" -ge 1800 ]; do sleep 5; mighty_waited=$((mighty_waited + 5)); done; trap - INT")
        #expect(try body("ouroboros") == "claude plugin marketplace add 'Q00/ouroboros'; claude plugin install 'ouroboros@ouroboros' --scope user")
        // Pinned: the skills CLI to an exact version, gstack to a commit.
        #expect(try body("paperthin") == "'npx' 'skills@1.7.2' 'add' 'LilMGenius/paperthin' '--global' '--agent' 'claude-code'")
        let gstack = try #require(try body("gstack"))
        let sha = "'d258bcffb0cb7bda5f133f3e957f932d816d282f'", destination = "\"$HOME\"/'.claude/skills/gstack'"
        #expect(gstack.hasPrefix("if [ -d \(destination)/.git ]; then git -C \(destination) fetch -q --depth 1 'https://github.com/garrytan/gstack.git' \(sha) && git -C \(destination) checkout -q --detach FETCH_HEAD; else mighty_tmp=\"$(mktemp -d)\""))
        #expect(gstack.contains("&& mv \"$mighty_tmp/repo\" \(destination); fi"))
        #expect(gstack.hasSuffix("&& : > \(destination)/'.mighty-setup' && (cd \(destination) && './setup') && : > \(destination)/'.mighty-setup-ok'"))
        #expect(!gstack.contains("git clone"))
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
        #expect(ids == ["claude", "git", "homebrew", "tailscale", "gh"])
        let steps = SetupScript.steps(for: ids + ["not-in-catalog"], catalog: catalog, platform: .macos, title: { $0.id.uppercased() })
        // The vendor page and the unknown ID are not steps.
        #expect(steps.map(\.itemId) == ["claude", "git", "homebrew", "gh"])
        let home = URL(fileURLWithPath: "/Users/fixture")
        let script = SetupScript.render(steps: steps, labels: Self.labels, home: home, startedMarker: "/tmp/it's started", doneMarker: "/tmp/it's done")
        let lines = script.split(separator: "\n").map(String.init)
        #expect(lines.first == "#!/bin/bash")
        #expect(lines[1] == "mkdir -- '/tmp/it'\\''s started' 2>/dev/null")
        #expect(lines.contains("mighty_marker='/tmp/it'\\''s done'"))
        #expect(lines.contains("trap 'mkdir -- \"$mighty_marker\" 2>/dev/null' EXIT"))
        // The working folder, the installers' folders after the shell's own PATH, and npm's registry, before any step.
        let preamble = SetupScript.preamble(home: home)
        #expect(preamble == ["cd \"$HOME\" || exit 1",
                             "export PATH=\"$PATH\":'/Users/fixture/.local/bin':'/Users/fixture/.npm-global/bin':'/Users/fixture/.bun/bin':'/opt/homebrew/bin':'/usr/local/bin'",
                             "export npm_config_registry='https://registry.npmjs.org/'"])
        let firstStep = try #require(lines.firstIndex { $0.hasPrefix("printf '\\n\\033[1m") })
        #expect(preamble.allSatisfy { line in lines.firstIndex(of: line).map { $0 < firstStep } == true })
        let headers = lines.filter { $0.hasPrefix("printf '\\n\\033[1m%s\\033[0m\\n'") }
        #expect(headers == ["[1/4] CLAUDE", "[2/4] GIT", "[3/4] HOMEBREW", "[4/4] GH"].map { "printf '\\n\\033[1m%s\\033[0m\\n' '\($0)'" })
        // Each step runs in its own subshell and a failure only marks the run failed.
        for step in steps {
            #expect(lines.contains("if ( \(step.body) ); then :; else mighty_failed=1; printf '%s\\n' 'FAILED'; fi"))
        }
        #expect(lines.contains("printf '%s\\n' 'WAITING'"))
        // Only the wait step keeps Control-C from ending the script.
        #expect(lines.filter { $0 == "trap ':' INT" }.count == 1 && lines.filter { $0 == "trap - INT" }.count == 1)
        #expect(steps.filter(\.waitsOutside).map(\.itemId) == ["git"])
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
        let started = root.appendingPathComponent("started marker"), marker = root.appendingPathComponent("done marker")
        let scriptURL = try SetupScriptFiles.writeScript(SetupScript.render(steps: steps, labels: Self.labels, home: root, startedMarker: started.path, doneMarker: marker.path), in: root)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/bash")
        process.arguments = [scriptURL.path]
        process.standardOutput = FileHandle.nullDevice
        try process.run(); process.waitUntilExit()
        #expect(process.terminationStatus == 1)
        #expect(FileManager.default.fileExists(atPath: root.appendingPathComponent("second").path))
        #expect(SetupScriptFiles.markerExists(started) && SetupScriptFiles.markerExists(marker))
    }

    /// Control-C during the wait for a system window ends that wait only: the
    /// script goes on to the next step and still leaves its end marker.
    @Test func controlCDuringTheWaitEndsOnlyTheWait() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("setup-wait-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let waiting = root.appendingPathComponent("waiting"), next = root.appendingPathComponent("next")
        let steps = [
            SetupScriptStep(itemId: "wait", title: "wait", body: "trap 'break' INT; : > \(SetupScript.quote(waiting.path)); until false; do sleep 5; done; trap - INT", waitsOutside: true),
            SetupScriptStep(itemId: "next", title: "next", body: ": > " + SetupScript.quote(next.path)),
        ]
        let started = root.appendingPathComponent("started"), done = root.appendingPathComponent("done")
        let scriptURL = try SetupScriptFiles.writeScript(SetupScript.render(steps: steps, labels: Self.labels, home: root, startedMarker: started.path, doneMarker: done.path), in: root)
        let ended = DispatchSemaphore(value: 0)
        // Its own process group, as a terminal's foreground job is, so Control-C reaches all of it.
        let child = try NativeChildProcess(executable: URL(fileURLWithPath: "/bin/bash"), arguments: [scriptURL.path], environment: ["PATH": "/usr/bin:/bin", "HOME": root.path],
                                           cwd: root, stdout: { _ in }, stderr: { _ in }, exited: { _ in ended.signal() })
        for _ in 0..<100 where !FileManager.default.fileExists(atPath: waiting.path) { Thread.sleep(forTimeInterval: 0.05) }
        #expect(FileManager.default.fileExists(atPath: waiting.path))
        #expect(Darwin.kill(-child.pid, SIGINT) == 0)
        #expect(ended.wait(timeout: .now() + 15) == .success)
        #expect(FileManager.default.fileExists(atPath: next.path))
        #expect(SetupScriptFiles.markerExists(done))
    }

    @Test func theScriptFolderAndFilesRefuseLinksAndOtherUsers() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("setup-files-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("setup")
        try SetupScriptFiles.prepareDirectory(folder)
        let mode = try #require(try FileManager.default.attributesOfItem(atPath: folder.path)[.posixPermissions] as? Int)
        #expect(mode & 0o077 == 0)
        try SetupScriptFiles.prepareDirectory(folder)
        // Open to others, or a link to a folder: refused.
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: folder.path)
        #expect(throws: (any Error).self) { try SetupScriptFiles.prepareDirectory(folder) }
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: folder.path)
        let link = root.appendingPathComponent("linked")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: folder)
        #expect(throws: (any Error).self) { try SetupScriptFiles.prepareDirectory(link) }
        // Each script is a new file with its own name, 0700.
        let first = try SetupScriptFiles.writeScript("echo one\n", in: folder), second = try SetupScriptFiles.writeScript("echo two\n", in: folder)
        let text = try String(contentsOf: first, encoding: .utf8)
        let permissions = try FileManager.default.attributesOfItem(atPath: first.path)[.posixPermissions] as? Int
        #expect(first != second && text == "echo one\n")
        #expect(permissions == 0o700)
        // A marker that is a link to a folder does not count.
        let markers = SetupScriptFiles.markers(in: folder)
        #expect(!SetupScriptFiles.markerExists(markers.done))
        try FileManager.default.createSymbolicLink(at: markers.done, withDestinationURL: root)
        #expect(!SetupScriptFiles.markerExists(markers.done))
        SetupScriptFiles.removeLeftovers(in: folder)
        #expect((try FileManager.default.contentsOfDirectory(atPath: folder.path)).isEmpty)
        #expect(FileManager.default.fileExists(atPath: root.path))
    }
}
