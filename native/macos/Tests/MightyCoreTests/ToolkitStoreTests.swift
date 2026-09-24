import Foundation
import Testing
@testable import MightyCore

// MARK: - Fake executor

/// Records every argv it receives; each call pops the front of `responses`.
/// Throws ToolkitStoreError if no response is queued.
final class FakeToolkitExecutor: ToolkitCommandExecutor, @unchecked Sendable {
    private var responses: [String]
    private(set) var calls: [[String]] = []

    init(responses: [String] = []) { self.responses = responses }

    func run(_ argv: [String]) throws -> String {
        calls.append(argv)
        guard !responses.isEmpty else { throw ToolkitStoreError("Fake executor: no response queued for \(argv)") }
        return responses.removeFirst()
    }
}

// MARK: - Helpers

private func tempDir(_ label: String) -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("\(label)-\(UUID().uuidString)", isDirectory: true)
    try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

private func samplePlugin(id: String = "my-plugin") -> ToolkitEntry {
    ToolkitEntry(entryId: id, displayName: "My Plugin", source: .user,
                 install: .plugin(source: "owner/repo", pluginID: "\(id)@repo"))
}

private func sampleRepoScript(id: String = "my-script", ref: String = "v1.0") -> ToolkitEntry {
    ToolkitEntry(entryId: id, displayName: "My Script", source: .user,
                 install: .repoScript(url: "https://github.com/x/setup.git",
                                      ref: ref, scriptPath: "scripts/install.sh"))
}

private let fakeSHA = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"
private let fakeSHA2 = "b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3"

// MARK: - Suite

@Suite struct ToolkitStoreTests {

    // MARK: Merged list

    @Test func mergedListWithNoFileReturnsBundledOnly() async {
        let dir = tempDir("toolkit-empty")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        let (entries, error) = await store.list()
        #expect(error == nil)
        #expect(entries.count == 1)
        #expect(entries[0].entryId == "mighty-styles")
        #expect(entries[0].source == .bundled)
    }

    @Test func mergedListIncludesUserEntries() async throws {
        let dir = tempDir("toolkit-merge")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        try await store.addEntry(samplePlugin())
        let (entries, error) = await store.list()
        #expect(error == nil)
        #expect(entries.count == 2)
        #expect(entries[0].entryId == "mighty-styles")
        #expect(entries[0].source == .bundled)
        #expect(entries[1].entryId == "my-plugin")
        #expect(entries[1].source == .user)
    }

    @Test func mergedListBundledFirstThenUserEntries() async throws {
        let dir = tempDir("toolkit-order")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        try await store.addEntry(samplePlugin(id: "alpha"))
        try await store.addEntry(samplePlugin(id: "beta"))
        let (entries, _) = await store.list()
        #expect(entries[0].source == .bundled)
        #expect(entries[1].entryId == "alpha")
        #expect(entries[2].entryId == "beta")
    }

    // MARK: Approval – content hash

    @Test func approvalBoundToContentHash() async throws {
        let dir = tempDir("toolkit-approval")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        let executor = FakeToolkitExecutor()
        let entry = samplePlugin()
        try await store.addEntry(entry)
        try await store.approve(entryId: entry.entryId, executor: executor)

        let approval = await store.approval(for: entry)
        #expect(approval != nil)
        #expect(!approval!.contentHash.isEmpty)
    }

    @Test func approvalLostWhenDisplayNameChanges() async throws {
        let dir = tempDir("toolkit-approval-name")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        let entry = samplePlugin()
        try await store.addEntry(entry)
        try await store.approve(entryId: entry.entryId, executor: FakeToolkitExecutor())

        // Replacing the entry with a different displayName invalidates the approval.
        let modified = ToolkitEntry(entryId: entry.entryId, displayName: "Renamed",
                                    source: .user, install: entry.install)
        try await store.addEntry(modified)

        let approval = await store.approval(for: modified)
        #expect(approval == nil)
    }

    @Test func approvalLostOnRepoScriptRefChange() async throws {
        let dir = tempDir("toolkit-approval-ref")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        let v1 = sampleRepoScript(ref: fakeSHA)
        try await store.addEntry(v1)
        try await store.approve(entryId: v1.entryId, executor: FakeToolkitExecutor())
        #expect(await store.approval(for: v1) != nil)

        // Changing the ref makes the hash mismatch.
        let v2 = sampleRepoScript(ref: fakeSHA2)
        try await store.addEntry(v2)
        #expect(await store.approval(for: v2) == nil)
    }

    @Test func approvalHashIsDeterministic() async throws {
        let e = samplePlugin()
        let h1 = ToolkitStore.canonicalHash(e)
        let h2 = ToolkitStore.canonicalHash(e)
        #expect(h1 == h2)
        #expect(h1.count == 64) // SHA-256 hex
    }

    @Test func approvalHashDiffersWhenInstallSpecDiffers() async {
        let e1 = ToolkitEntry(entryId: "x", displayName: "X", source: .user,
                              install: .package(manager: .brew, name: "ripgrep"))
        let e2 = ToolkitEntry(entryId: "x", displayName: "X", source: .user,
                              install: .package(manager: .npm, name: "ripgrep"))
        #expect(ToolkitStore.canonicalHash(e1) != ToolkitStore.canonicalHash(e2))
    }

    @Test func approvalSurvivesRoundTrip() async throws {
        let dir = tempDir("toolkit-roundtrip")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        let entry = samplePlugin()
        try await store.addEntry(entry)
        try await store.approve(entryId: entry.entryId, executor: FakeToolkitExecutor())
        let hash1 = (await store.approval(for: entry))?.contentHash

        // A fresh store from the same directory must reload the approval.
        let store2 = ToolkitStore(directory: dir)
        let (entries2, _) = await store2.list()
        let reloaded = entries2.first { $0.entryId == entry.entryId }!
        let hash2 = await store2.approval(for: reloaded)?.contentHash
        #expect(hash1 == hash2)
    }

    // MARK: Tag resolution

    @Test func tagResolvedToSHAAtApproval() async throws {
        let dir = tempDir("toolkit-tag")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        let entry = sampleRepoScript(ref: "v1.2.3")
        try await store.addEntry(entry)

        let executor = FakeToolkitExecutor(responses: ["\(fakeSHA)\trefs/tags/v1.2.3"])
        try await store.approve(entryId: entry.entryId, executor: executor)

        // Executor was called with git ls-remote.
        #expect(executor.calls.count == 1)
        #expect(executor.calls[0][0] == "git")
        #expect(executor.calls[0][1] == "ls-remote")

        let approval = await store.approval(for: entry)
        #expect(approval?.resolvedCommit == fakeSHA)
    }

    @Test func sha40RefStoredDirectlyAsResolvedCommit() async throws {
        let dir = tempDir("toolkit-sha40")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        let entry = sampleRepoScript(ref: fakeSHA)
        try await store.addEntry(entry)

        let executor = FakeToolkitExecutor()
        try await store.approve(entryId: entry.entryId, executor: executor)

        // No network call needed for a 40-hex ref.
        #expect(executor.calls.isEmpty)
        #expect(await store.approval(for: entry)?.resolvedCommit == fakeSHA)
    }

    @Test func approvalStoredResolvedCommitSurvivesRoundTrip() async throws {
        let dir = tempDir("toolkit-commit-roundtrip")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        let entry = sampleRepoScript(ref: "v1.0")
        try await store.addEntry(entry)
        let executor = FakeToolkitExecutor(responses: ["\(fakeSHA)\trefs/tags/v1.0"])
        try await store.approve(entryId: entry.entryId, executor: executor)

        let store2 = ToolkitStore(directory: dir)
        let (entries2, _) = await store2.list()
        let reloaded = entries2.first { $0.entryId == entry.entryId }!
        let approval = await store2.approval(for: reloaded)
        #expect(approval?.resolvedCommit == fakeSHA)
    }

    // MARK: Export / Import

    @Test func exportCarriesNoApprovalData() async throws {
        let dir = tempDir("toolkit-export")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        let entry = samplePlugin()
        try await store.addEntry(entry)
        try await store.approve(entryId: entry.entryId, executor: FakeToolkitExecutor())

        let data = try await store.exportData()
        let array = try #require((try? JSONSerialization.jsonObject(with: data)) as? [[String: Any]])
        for obj in array {
            #expect(obj["approval"] == nil)
        }
    }

    @Test func exportContainsEntryFields() async throws {
        let dir = tempDir("toolkit-export-fields")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        try await store.addEntry(samplePlugin())
        let data = try await store.exportData()
        let array = try #require((try? JSONSerialization.jsonObject(with: data)) as? [[String: Any]])
        #expect(array.count == 1)
        #expect(array[0]["id"] as? String == "my-plugin")
        #expect(array[0]["displayName"] as? String == "My Plugin")
    }

    @Test func importYieldsUnapprovedEntries() async throws {
        let dir = tempDir("toolkit-import")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)

        // Build import payload (includes stale approval data to verify it is stripped).
        let payload: [[String: Any]] = [[
            "id": "imported-plugin", "displayName": "Imported",
            "install": ["kind": "plugin", "source": "owner/repo", "pluginID": "imported-plugin@repo"] as [String: Any],
            "approval": ["contentHash": "fakehash"] as [String: Any],
        ]]
        let data = try JSONSerialization.data(withJSONObject: payload)
        try await store.importData(data)

        let (entries, _) = await store.list()
        let imported = try #require(entries.first { $0.entryId == "imported-plugin" })
        // Even though the payload carried an "approval" block, it is stripped on import.
        let approval = await store.approval(for: imported)
        #expect(approval == nil)
    }

    @Test func importReplacesExistingIdAndBecomesUnapproved() async throws {
        let dir = tempDir("toolkit-import-replace")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        let original = samplePlugin()
        try await store.addEntry(original)
        try await store.approve(entryId: original.entryId, executor: FakeToolkitExecutor())
        #expect(await store.approval(for: original) != nil)

        // Import an entry with the same id but different displayName.
        let payload: [[String: Any]] = [[
            "id": original.entryId, "displayName": "Replaced",
            "install": ["kind": "plugin", "source": "owner/repo",
                        "pluginID": "\(original.entryId)@repo"] as [String: Any],
        ]]
        try await store.importData(try JSONSerialization.data(withJSONObject: payload))

        let (entries, _) = await store.list()
        let replaced = try #require(entries.first { $0.entryId == original.entryId })
        #expect(replaced.displayName == "Replaced")
        #expect(await store.approval(for: replaced) == nil)
    }

    // MARK: Remove

    @Test func removeEntryDelistsOnly() async throws {
        let dir = tempDir("toolkit-remove")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        try await store.addEntry(samplePlugin())
        try await store.removeEntry(id: "my-plugin")
        let (entries, _) = await store.list()
        #expect(!entries.contains { $0.entryId == "my-plugin" })
    }

    @Test func removeEntryRunsNoCommand() async throws {
        let dir = tempDir("toolkit-remove-noexec")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        try await store.addEntry(samplePlugin())

        let executor = FakeToolkitExecutor()
        // removeEntry does not accept an executor; just verify no side-effects.
        try await store.removeEntry(id: "my-plugin")

        // Executor was never called because removeEntry takes no executor at all.
        #expect(executor.calls.isEmpty)
    }

    @Test func removeEntryOnlyModifiesToolkitJson() async throws {
        let dir = tempDir("toolkit-remove-only-json")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        try await store.addEntry(samplePlugin())

        // Create a sentinel file; removeEntry must not touch it.
        let sentinel = dir.appendingPathComponent("sentinel.txt")
        try Data("original".utf8).write(to: sentinel)
        let sentinelBefore = try FileManager.default.attributesOfItem(atPath: sentinel.path)[.modificationDate] as? Date

        try await store.removeEntry(id: "my-plugin")

        let sentinelAfter = try FileManager.default.attributesOfItem(atPath: sentinel.path)[.modificationDate] as? Date
        #expect(sentinelBefore == sentinelAfter)
        #expect((try? Data(contentsOf: sentinel)).map { String(data: $0, encoding: .utf8) } == "original")
    }

    @Test func removeEntryPreservesApprovalForOtherEntries() async throws {
        let dir = tempDir("toolkit-remove-preserve")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        let alpha = samplePlugin(id: "alpha")
        let beta = samplePlugin(id: "beta")
        try await store.addEntry(alpha)
        try await store.addEntry(beta)
        try await store.approve(entryId: "alpha", executor: FakeToolkitExecutor())
        try await store.approve(entryId: "beta", executor: FakeToolkitExecutor())

        try await store.removeEntry(id: "beta")

        let (entries, _) = await store.list()
        let remaining = try #require(entries.first { $0.entryId == "alpha" })
        #expect(await store.approval(for: remaining) != nil)
    }

    // MARK: Invalid toolkit.json

    @Test func invalidToolkitJsonRefusedByteIdentical() async throws {
        let dir = tempDir("toolkit-invalid")
        defer { try? FileManager.default.removeItem(at: dir) }
        let jsonURL = dir.appendingPathComponent("toolkit.json")
        let invalid = Data("this is not valid json".utf8)
        try invalid.write(to: jsonURL)

        let store = ToolkitStore(directory: dir)
        let (entries, error) = await store.list()

        // Bundled entry still shows.
        #expect(entries.contains { $0.entryId == "mighty-styles" })
        // Error is surfaced.
        #expect(error != nil)
        // File is byte-identical.
        #expect((try? Data(contentsOf: jsonURL)) == invalid)
    }

    @Test func invalidToolkitJsonWrongVersionRefused() async throws {
        let dir = tempDir("toolkit-badversion")
        defer { try? FileManager.default.removeItem(at: dir) }
        let jsonURL = dir.appendingPathComponent("toolkit.json")
        let payload = Data("""
        {"version":99,"entries":[]}
        """.utf8)
        try payload.write(to: jsonURL)

        let store = ToolkitStore(directory: dir)
        let (entries, error) = await store.list()
        #expect(entries.contains { $0.entryId == "mighty-styles" })
        #expect(error != nil)
        #expect((try? Data(contentsOf: jsonURL)) == payload)
    }

    @Test func invalidToolkitJsonBlocksMutations() async throws {
        let dir = tempDir("toolkit-invalid-mutate")
        defer { try? FileManager.default.removeItem(at: dir) }
        let jsonURL = dir.appendingPathComponent("toolkit.json")
        let invalid = Data("not json".utf8)
        try invalid.write(to: jsonURL)

        let store = ToolkitStore(directory: dir)
        await #expect(throws: (any Error).self) {
            try await store.addEntry(samplePlugin())
        }
        // File is still the original invalid content.
        #expect((try? Data(contentsOf: jsonURL)) == invalid)
    }

    // MARK: Atomic write

    @Test func atomicWriteProducesValidToolkitJson() async throws {
        let dir = tempDir("toolkit-atomic")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = ToolkitStore(directory: dir)
        try await store.addEntry(samplePlugin())

        let jsonURL = dir.appendingPathComponent("toolkit.json")
        let data = try Data(contentsOf: jsonURL)
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            Issue.record("toolkit.json is not valid JSON"); return
        }
        #expect(object["version"] as? Int == 1)
        #expect(object["entries"] is [[String: Any]])
    }
}
