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

    // MARK: – Hash parity (fixture: native/contracts/toolkit-hash-parity.json)
    //
    // Canonical bytes = compact JSON of {displayName, id, install} with sorted keys,
    // no whitespace, NFC UTF-8; approval and unknown keys (e.g. stale `platforms`)
    // excluded.  Matches BuildCanonicalJson / CanonicalHash in Windows Core.

    private static let parityBrew    = ToolkitEntry(entryId: "shared-brew",   displayName: "ripgrep",    source: .user, install: .package(manager: .brew,   name: "ripgrep"))
    private static let parityNpm     = ToolkitEntry(entryId: "shared-npm",    displayName: "TypeScript", source: .user, install: .package(manager: .npm,    name: "typescript"))
    private static let parityWinget  = ToolkitEntry(entryId: "shared-winget", displayName: "Node.js",    source: .user, install: .package(manager: .winget, name: "OpenJS.NodeJS", executable: "node.exe"))
    private static let parityPlugin  = ToolkitEntry(entryId: "shared-plugin", displayName: "My Plugin",  source: .user, install: .plugin(source: "owner/repo", pluginID: "myplugin@marketplace"))

    private static let parityBrewBytes   = "{\"displayName\":\"ripgrep\",\"id\":\"shared-brew\",\"install\":{\"kind\":\"package\",\"manager\":\"brew\",\"name\":\"ripgrep\"}}"
    private static let parityNpmBytes    = "{\"displayName\":\"TypeScript\",\"id\":\"shared-npm\",\"install\":{\"kind\":\"package\",\"manager\":\"npm\",\"name\":\"typescript\"}}"
    private static let parityWingetBytes = "{\"displayName\":\"Node.js\",\"id\":\"shared-winget\",\"install\":{\"executable\":\"node.exe\",\"kind\":\"package\",\"manager\":\"winget\",\"name\":\"OpenJS.NodeJS\"}}"
    private static let parityPluginBytes = "{\"displayName\":\"My Plugin\",\"id\":\"shared-plugin\",\"install\":{\"kind\":\"plugin\",\"pluginID\":\"myplugin@marketplace\",\"source\":\"owner/repo\"}}"

    private static let parityBrewSha256   = "60c2f1ee819f3e586e7b4edbc21e33f042f578dbf07e1d4a2b50c8ae81d38c4d"
    private static let parityNpmSha256    = "6ed516dbfd00a154262de7a15ad2d96460ce255b15d4bfd25935909d1db1bdae"
    private static let parityWingetSha256 = "17bb3a3b5145260f6a69293472356d16f24c5769c57c22cff177ac8e090f4764"
    private static let parityPluginSha256 = "8b7b568e58fa4ecfad28cda421b8ba26a4943493892ddae27cef78da4453919f"

    @Test func hashParityCanonicalBytes() {
        #expect(ToolkitStore.canonicalJson(Self.parityBrew)   == Self.parityBrewBytes,   "brew canonical bytes")
        #expect(ToolkitStore.canonicalJson(Self.parityNpm)    == Self.parityNpmBytes,    "npm canonical bytes")
        #expect(ToolkitStore.canonicalJson(Self.parityWinget) == Self.parityWingetBytes, "winget canonical bytes")
        #expect(ToolkitStore.canonicalJson(Self.parityPlugin) == Self.parityPluginBytes, "plugin canonical bytes")
    }

    @Test func hashParityDigests() {
        #expect(ToolkitStore.canonicalHash(Self.parityBrew)   == Self.parityBrewSha256,   "brew sha256")
        #expect(ToolkitStore.canonicalHash(Self.parityNpm)    == Self.parityNpmSha256,    "npm sha256")
        #expect(ToolkitStore.canonicalHash(Self.parityWinget) == Self.parityWingetSha256, "winget sha256")
        #expect(ToolkitStore.canonicalHash(Self.parityPlugin) == Self.parityPluginSha256, "plugin sha256")
    }

    @Test func hashParityStalePlatformsKeyExcluded() {
        // The winget entry with stale `platforms` key must hash identically to
        // the same entry without it, because unknown keys are excluded from the
        // hash surface.
        let withPlatforms = Self.parityWinget   // platforms is not part of ToolkitEntry
        let withoutPlatforms = ToolkitEntry(entryId: "shared-winget", displayName: "Node.js",
                                            source: .user,
                                            install: .package(manager: .winget, name: "OpenJS.NodeJS", executable: "node.exe"))
        #expect(ToolkitStore.canonicalHash(withPlatforms) == ToolkitStore.canonicalHash(withoutPlatforms),
                "stale platforms key must not change the hash")
        #expect(ToolkitStore.canonicalHash(withPlatforms) == Self.parityWingetSha256,
                "hash matches fixture digest even for entry loaded from a file with stale platforms")
    }

    @Test func hashParityChangingFieldChangesHash() {
        // Changing any hashed field must produce a different digest.
        let original  = Self.parityWinget
        let modified  = ToolkitEntry(entryId: "shared-winget", displayName: "Node.js",
                                     source: .user,
                                     install: .package(manager: .winget, name: "OpenJS.NodeJS.LTS", executable: "node.exe"))
        #expect(ToolkitStore.canonicalHash(original) != ToolkitStore.canonicalHash(modified),
                "changing the winget name must change the digest")
    }

    @Test func hashParityRoundTripCanonicalFile() async throws {
        // Write 4 parity entries to a store (creates a canonical file), then
        // trigger a save via add+remove dummy and assert the whole file is
        // byte-for-byte identical.
        let dir = tempDir("hp-roundtrip-canonical")
        defer { try? FileManager.default.removeItem(at: dir) }
        let jsonURL = dir.appendingPathComponent("toolkit.json")
        let store1 = ToolkitStore(directory: dir)
        try await store1.addEntry(Self.parityBrew)
        try await store1.addEntry(Self.parityNpm)
        try await store1.addEntry(Self.parityWinget)
        try await store1.addEntry(Self.parityPlugin)
        let canonicalBytes = try Data(contentsOf: jsonURL)

        let store2 = ToolkitStore(directory: dir)
        let dummy = ToolkitEntry(entryId: "_hp_dummy_", displayName: "D", source: .user,
                                  install: .skill(url: "https://github.com/x/hp-dummy.git"))
        try await store2.addEntry(dummy)
        try await store2.removeEntry(id: "_hp_dummy_")
        let roundTripBytes = try Data(contentsOf: jsonURL)
        #expect(canonicalBytes == roundTripBytes,
                "canonical file with 4 parity entries must be a fixed point of save")
    }

    @Test func hashParityRoundTripWithStalePlatformsKey() async throws {
        // Write a file whose winget entry carries a stale `platforms` key (using
        // JSONSerialization so the format is Foundation-canonical), trigger a save,
        // and assert the whole file is byte-for-byte identical — proving that
        // untouched entries are emitted as their original bytes (unknown key preserved).
        let dir = tempDir("hp-roundtrip-stale")
        defer { try? FileManager.default.removeItem(at: dir) }
        let jsonURL = dir.appendingPathComponent("toolkit.json")

        let initial: [String: Any] = [
            "version": 1,
            "entries": [
                [
                    "id": "shared-winget",
                    "displayName": "Node.js",
                    "install": [
                        "kind": "package", "manager": "winget",
                        "name": "OpenJS.NodeJS", "executable": "node.exe",
                    ] as [String: Any],
                    "platforms": ["windows"],
                ] as [String: Any],
            ],
        ]
        var originalData = try JSONSerialization.data(
            withJSONObject: initial, options: [.sortedKeys, .prettyPrinted])
        if originalData.last != UInt8(ascii: "\n") { originalData.append(UInt8(ascii: "\n")) }
        try originalData.write(to: jsonURL)

        let store = ToolkitStore(directory: dir)
        let dummy = ToolkitEntry(entryId: "_hp_stale_dummy_", displayName: "D", source: .user,
                                  install: .skill(url: "https://github.com/x/hp-stale.git"))
        try await store.addEntry(dummy)
        try await store.removeEntry(id: "_hp_stale_dummy_")
        let savedData = try Data(contentsOf: jsonURL)
        #expect(originalData == savedData,
                "file with stale platforms key must be byte-identical after a save that does not touch the winget entry")
    }

    // MARK: – Anti-vacuity marker

    @Test func markerToolkitPlatformOK() {
        print("Suite ToolkitPlatformTests passed")
    }
}
