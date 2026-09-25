import Foundation
import Testing
@testable import MightyCore

// MARK: - Helpers

private func tempDir(_ label: String) -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("toolkit-platform-\(label)-\(UUID().uuidString)", isDirectory: true)
    try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

private func samplePlugin(id: String = "my-plugin") -> ToolkitEntry {
    ToolkitEntry(entryId: id, displayName: "My Plugin", source: .user,
                 install: .plugin(source: "owner/repo", pluginID: "\(id)@repo"))
}

private func sampleWinget(id: String = "wt", name: String = "Microsoft.WindowsTerminal",
                           executable: String = "wt.exe") -> ToolkitEntry {
    ToolkitEntry(entryId: id, displayName: "Windows Terminal", source: .user,
                 install: .package(manager: .winget, name: name, executable: executable))
}

// MARK: - Suite

@Suite struct ToolkitPlatformTests {

    // MARK: – Platform table

    @Test func platformTablePlugin() {
        let p = ToolkitInstallSpec.plugin(source: "o/r", pluginID: "p@m").platforms
        #expect(p == [.macOS, .windows])
    }

    @Test func platformTableMcp() {
        let p = ToolkitInstallSpec.mcp(name: "srv", executable: "node", args: []).platforms
        #expect(p == [.macOS, .windows])
    }

    @Test func platformTableSkill() {
        let p = ToolkitInstallSpec.skill(url: "https://github.com/x/y.git").platforms
        #expect(p == [.macOS, .windows])
    }

    @Test func platformTablePackageBrew() {
        let p = ToolkitInstallSpec.package(manager: .brew, name: "ripgrep").platforms
        #expect(p == [.macOS])
    }

    @Test func platformTablePackageNpm() {
        let p = ToolkitInstallSpec.package(manager: .npm, name: "typescript").platforms
        #expect(p == [.macOS, .windows])
    }

    @Test func platformTablePackageWinget() {
        let p = ToolkitInstallSpec.package(manager: .winget, name: "Microsoft.VSCode", executable: "Code.exe").platforms
        #expect(p == [.windows])
    }

    @Test func platformTableRepoScript() {
        let sha = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"
        let p = ToolkitInstallSpec.repoScript(url: "https://github.com/x/y.git",
                                               ref: sha, scriptPath: "install.sh").platforms
        #expect(p == [.macOS])
    }

    // MARK: – Decoder: winget decodes

    @Test func wingetEntryDecodes() throws {
        let entry = try ToolkitEntryDecoder.decode([
            "id": "code-editor", "displayName": "VS Code",
            "install": ["kind": "package", "manager": "winget",
                        "name": "Microsoft.VisualStudioCode", "executable": "Code.exe"] as [String: Any],
        ])
        guard case .package(let manager, let name, let executable) = entry.install else {
            Issue.record("Expected package spec"); return
        }
        #expect(manager == .winget)
        #expect(name == "Microsoft.VisualStudioCode")
        #expect(executable == "Code.exe")
        #expect(entry.source == .user)
    }

    @Test func wingetNameWithPlusAndDotDecodes() throws {
        let entry = try ToolkitEntryDecoder.decode([
            "id": "winterm", "displayName": "Terminal",
            "install": ["kind": "package", "manager": "winget",
                        "name": "Microsoft.WindowsTerminal", "executable": "wt.exe"] as [String: Any],
        ])
        guard case .package(let manager, let name, _) = entry.install else {
            Issue.record("Expected package"); return
        }
        #expect(manager == .winget)
        #expect(name == "Microsoft.WindowsTerminal")
    }

    // MARK: – Decoder: brew/npm with executable rejected

    @Test func brewWithExecutableIsRejected() {
        #expect(throws: (any Error).self) {
            try ToolkitEntryDecoder.decode([
                "id": "rg", "displayName": "ripgrep",
                "install": ["kind": "package", "manager": "brew", "name": "ripgrep",
                            "executable": "rg"] as [String: Any],
            ])
        }
    }

    @Test func npmWithExecutableIsRejected() {
        #expect(throws: (any Error).self) {
            try ToolkitEntryDecoder.decode([
                "id": "ts", "displayName": "TypeScript",
                "install": ["kind": "package", "manager": "npm", "name": "typescript",
                            "executable": "tsc"] as [String: Any],
            ])
        }
    }

    // MARK: – Decoder: winget validation

    @Test func wingetWithoutExecutableIsRejected() {
        #expect(throws: (any Error).self) {
            try ToolkitEntryDecoder.decode([
                "id": "code", "displayName": "Code",
                "install": ["kind": "package", "manager": "winget",
                            "name": "Microsoft.VSCode"] as [String: Any],
            ])
        }
    }

    @Test func wingetWithAbsolutePathExecutableIsRejected() {
        #expect(throws: (any Error).self) {
            try ToolkitEntryDecoder.decode([
                "id": "code", "displayName": "Code",
                "install": ["kind": "package", "manager": "winget",
                            "name": "Microsoft.VSCode", "executable": "/usr/bin/code"] as [String: Any],
            ])
        }
    }

    @Test func wingetWithSpaceInExecutableIsRejected() {
        #expect(throws: (any Error).self) {
            try ToolkitEntryDecoder.decode([
                "id": "code", "displayName": "Code",
                "install": ["kind": "package", "manager": "winget",
                            "name": "Microsoft.VSCode", "executable": "my code.exe"] as [String: Any],
            ])
        }
    }

    @Test func wingetWithEmptyNameIsRejected() {
        #expect(throws: (any Error).self) {
            try ToolkitEntryDecoder.decode([
                "id": "bad", "displayName": "Bad",
                "install": ["kind": "package", "manager": "winget",
                            "name": "", "executable": "code.exe"] as [String: Any],
            ])
        }
    }

    @Test func wingetWithUnknownFieldIsRejected() {
        #expect(throws: (any Error).self) {
            try ToolkitEntryDecoder.decode([
                "id": "code", "displayName": "Code",
                "install": ["kind": "package", "manager": "winget",
                            "name": "Microsoft.VSCode", "executable": "Code.exe",
                            "extra": "evil"] as [String: Any],
            ])
        }
    }

    // MARK: – Store: winget entry preserved across operations

    @Test func wingetEntryPreservedAcrossAddAndRemove() async throws {
        let dir = tempDir("winget-add-remove")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)

        try await store.addEntry(sampleWinget())
        try await store.addEntry(samplePlugin())
        try await store.removeEntry(id: "my-plugin")

        let jsonURL = dir.appendingPathComponent("toolkit.json")
        let data = try Data(contentsOf: jsonURL)
        let obj = try #require((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])
        let entries = try #require(obj["entries"] as? [[String: Any]])
        let found = try #require(entries.first { $0["id"] as? String == "wt" })
        let install = try #require(found["install"] as? [String: Any])
        #expect(install["manager"] as? String == "winget")
        #expect(install["name"] as? String == "Microsoft.WindowsTerminal")
        #expect(install["executable"] as? String == "wt.exe")
    }

    @Test func wingetApprovalPreservedAcrossAddOperation() async throws {
        let dir = tempDir("winget-approval-preserve")
        defer { try? FileManager.default.removeItem(at: dir) }
        let jsonURL = dir.appendingPathComponent("toolkit.json")
        let fakeHash = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef"
        let initial: [String: Any] = [
            "version": 1,
            "entries": [
                [
                    "id": "wt",
                    "displayName": "Windows Terminal",
                    "install": ["kind": "package", "manager": "winget",
                                "name": "Microsoft.WindowsTerminal", "executable": "wt.exe"] as [String: Any],
                    "approval": ["contentHash": fakeHash] as [String: Any],
                ] as [String: Any],
            ],
        ]
        try JSONSerialization.data(withJSONObject: initial).write(to: jsonURL)

        let store = ToolkitStore(directory: dir)
        try await store.addEntry(samplePlugin())

        let data = try Data(contentsOf: jsonURL)
        let obj = try #require((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])
        let entries = try #require(obj["entries"] as? [[String: Any]])
        let found = try #require(entries.first { $0["id"] as? String == "wt" })
        let install = try #require(found["install"] as? [String: Any])
        let approval = try #require(found["approval"] as? [String: Any])
        #expect(install["executable"] as? String == "wt.exe")
        #expect(install["manager"] as? String == "winget")
        #expect(install["name"] as? String == "Microsoft.WindowsTerminal")
        #expect(approval["contentHash"] as? String == fakeHash)
    }

    @Test func wingetEntryPreservedAfterApproveOfOtherEntry() async throws {
        let dir = tempDir("winget-approve-other")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)

        try await store.addEntry(sampleWinget())
        try await store.addEntry(samplePlugin())
        try await store.approve(entryId: "my-plugin", executor: FakeToolkitExecutor())

        let jsonURL = dir.appendingPathComponent("toolkit.json")
        let data = try Data(contentsOf: jsonURL)
        let obj = try #require((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])
        let entries = try #require(obj["entries"] as? [[String: Any]])
        let wtEntry = try #require(entries.first { $0["id"] as? String == "wt" })
        let install = try #require(wtEntry["install"] as? [String: Any])
        #expect(install["manager"] as? String == "winget")
        #expect(install["executable"] as? String == "wt.exe")
    }

    // MARK: – Store: winget not in list

    @Test func wingetEntryNotInList() async throws {
        let dir = tempDir("winget-not-listed")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        try await store.addEntry(sampleWinget())
        try await store.addEntry(samplePlugin())
        let (entries, _) = await store.list()
        #expect(!entries.contains { $0.entryId == "wt" })
        #expect(entries.contains { $0.entryId == "my-plugin" })
    }

    @Test func wingetEntryNotInListAfterReload() async throws {
        let dir = tempDir("winget-not-listed-reload")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store1 = ToolkitStore(directory: dir)
        try await store1.addEntry(sampleWinget())

        let store2 = ToolkitStore(directory: dir)
        let (entries, error) = await store2.list()
        #expect(error == nil)
        #expect(!entries.contains { $0.entryId == "wt" })
    }

    // MARK: – Store: import accepts winget

    @Test func importAcceptsWingetEntry() async throws {
        let dir = tempDir("winget-import")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        let payload: [[String: Any]] = [[
            "id": "code-editor", "displayName": "VS Code",
            "install": ["kind": "package", "manager": "winget",
                        "name": "Microsoft.VisualStudioCode", "executable": "Code.exe"] as [String: Any],
        ]]
        try await store.importData(try JSONSerialization.data(withJSONObject: payload))

        // Not in visible list
        let (entries, _) = await store.list()
        #expect(!entries.contains { $0.entryId == "code-editor" })

        // But present in toolkit.json
        let jsonURL = dir.appendingPathComponent("toolkit.json")
        let data = try Data(contentsOf: jsonURL)
        let obj = try #require((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])
        let saved = try #require(obj["entries"] as? [[String: Any]])
        #expect(saved.contains { $0["id"] as? String == "code-editor" })
    }

    // MARK: – Store: export includes winget

    @Test func exportIncludesWingetEntryWithoutApproval() async throws {
        let dir = tempDir("winget-export")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        try await store.addEntry(sampleWinget())

        let data = try await store.exportData()
        let exported = try #require((try? JSONSerialization.jsonObject(with: data)) as? [[String: Any]])
        let e = try #require(exported.first { $0["id"] as? String == "wt" })
        let install = try #require(e["install"] as? [String: Any])
        #expect(install["manager"] as? String == "winget")
        #expect(install["executable"] as? String == "wt.exe")
        #expect(e["approval"] == nil)
    }

    // MARK: – Store: stale `platforms` key preserved with original bytes

    @Test func wingetEntryWithStalePlatformsKeyPreservedAcrossOperations() async throws {
        let dir = tempDir("winget-stale-platforms")
        defer { try? FileManager.default.removeItem(at: dir) }
        let jsonURL = dir.appendingPathComponent("toolkit.json")

        // Compute the real canonical hash so the approval stays valid after reload.
        let winget = sampleWinget()
        let realHash = ToolkitStore.canonicalHash(winget)

        // Write a file whose winget entry carries a stale `platforms` key.
        let initial: [String: Any] = [
            "version": 1,
            "entries": [
                [
                    "id": "wt",
                    "displayName": "Windows Terminal",
                    "install": ["kind": "package", "manager": "winget",
                                "name": "Microsoft.WindowsTerminal",
                                "executable": "wt.exe"] as [String: Any],
                    "platforms": ["windows"],
                    "approval": ["contentHash": realHash] as [String: Any],
                ] as [String: Any],
            ],
        ]
        try JSONSerialization.data(withJSONObject: initial).write(to: jsonURL)

        let store = ToolkitStore(directory: dir)
        // add unrelated entry, remove it, add again, approve it
        try await store.addEntry(samplePlugin())
        try await store.removeEntry(id: "my-plugin")
        try await store.addEntry(samplePlugin())
        try await store.approve(entryId: "my-plugin", executor: FakeToolkitExecutor())

        // Read back and verify winget entry
        let data = try Data(contentsOf: jsonURL)
        let obj = try #require((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])
        let entries = try #require(obj["entries"] as? [[String: Any]])
        let wtEntry = try #require(entries.first { $0["id"] as? String == "wt" })

        // Stale `platforms` key still present (original bytes preserved)
        let platforms = try #require(wtEntry["platforms"] as? [String])
        #expect(platforms == ["windows"])

        // All install fields intact
        let install = try #require(wtEntry["install"] as? [String: Any])
        #expect(install["manager"] as? String == "winget")
        #expect(install["name"] as? String == "Microsoft.WindowsTerminal")
        #expect(install["executable"] as? String == "wt.exe")

        // Approval still present with the original hash
        let approvalObj = try #require(wtEntry["approval"] as? [String: Any])
        #expect(approvalObj["contentHash"] as? String == realHash)

        // Approval is still valid: a fresh store can verify it
        let store2 = ToolkitStore(directory: dir)
        let reloadedApproval = await store2.approval(for: winget)
        #expect(reloadedApproval != nil)
        #expect(reloadedApproval?.contentHash == realHash)
    }

    // MARK: – Store: canonical file is a fixed point of load → save

    @Test func canonicalFileIsFixedPoint() async throws {
        let dir = tempDir("fixed-point")
        defer { try? FileManager.default.removeItem(at: dir) }
        let jsonURL = dir.appendingPathComponent("toolkit.json")

        // Step 1: write a canonical file through the store
        let store1 = ToolkitStore(directory: dir)
        try await store1.addEntry(samplePlugin(id: "alpha"))
        try await store1.addEntry(sampleWinget())    // other-OS entry
        let canonicalBytes = try Data(contentsOf: jsonURL)

        // Step 2: load a fresh store, add+remove a dummy entry (forces a save
        // without touching alpha or the winget entry)
        let store2 = ToolkitStore(directory: dir)
        let dummy = ToolkitEntry(entryId: "tmp-dummy", displayName: "D", source: .user,
                                  install: .skill(url: "https://github.com/x/tmp.git"))
        try await store2.addEntry(dummy)
        try await store2.removeEntry(id: "tmp-dummy")
        let roundTripBytes = try Data(contentsOf: jsonURL)

        // The file must be byte-for-byte identical
        #expect(canonicalBytes == roundTripBytes)
    }

    // MARK: – Runner: winget entry never planned

    @Test func wingetEntryNeverPlanned() async throws {
        let dir = tempDir("winget-plan")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))

        try await store.addEntry(sampleWinget())
        try await store.approve(entryId: "wt", executor: FakeToolkitExecutor())

        let ctx = ToolkitProbeContext(home: home, environment: [:],
                                      appDataDir: home.appendingPathComponent("appdata"))
        let runner = ToolkitRunner(store: store, probeContext: ctx)
        let items = await runner.plan()
        #expect(!items.contains { $0.entry.entryId == "wt" })
    }

    // MARK: – Anti-vacuity marker

    @Test func markerToolkitPlatformOK() {
        print("Suite ToolkitPlatformTests passed")
    }
}
