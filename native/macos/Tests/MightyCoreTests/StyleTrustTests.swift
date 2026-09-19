import Foundation
import Testing
@testable import MightyCore

struct StyleTrustTests {
    private func store(_ root: URL) -> StyleTrustStore { StyleTrustStore(directory: root.appendingPathComponent("style-trust", isDirectory: true)) }

    private func style(_ data: Data, path: String, workspacePath: String? = nil) throws -> RegisteredStyle {
        try StyleFixtures.registered(data, source: workspacePath == nil ? .user : .workspace, path: path, workspacePath: workspacePath, approval: .pending)
    }

    @Test func approvalIsBoundToBytesAndRefusalToThePlace() async throws {
        let root = StyleFixtures.temporaryDirectory("style-trust")
        defer { try? FileManager.default.removeItem(at: root) }
        let trust = store(root)
        let path = root.appendingPathComponent("flow.json").path
        let first = try style(StyleFixtures.data(), path: path)
        try await trust.approve(first)
        let records = try await trust.load()
        #expect(records.count == 1 && records[0].state == "approved")
        let file = StyleFixtures.discovered(StyleFixtures.data(), source: .user, url: URL(fileURLWithPath: path))
        #expect(StyleTrustStore.state(for: file, in: records) == .approved)
        // One byte more and the very next scan asks again (§4.2).
        let edited = StyleFixtures.discovered(StyleFixtures.data(StyleFixtures.flat, ["summary": "\"조금 다릅니다\""]), source: .user, url: URL(fileURLWithPath: path))
        #expect(StyleTrustStore.state(for: edited, in: records) == .pending)
        // The file lives at 0600 inside a 0700 folder.
        let attributes = try FileManager.default.attributesOfItem(atPath: root.appendingPathComponent("style-trust/approvals.json").path)
        #expect((attributes[.posixPermissions] as? NSNumber)?.int32Value == 0o600)
        let folder = try FileManager.default.attributesOfItem(atPath: root.appendingPathComponent("style-trust").path)
        #expect((folder[.posixPermissions] as? NSNumber)?.int32Value == 0o700)
        // A refusal sticks to the place, whatever the bytes become.
        try await trust.revoke(try style(StyleFixtures.data(), path: path))
        let afterRevoke = try await trust.load()
        #expect(StyleTrustStore.state(for: file, in: afterRevoke) == .revoked)
        #expect(StyleTrustStore.state(for: edited, in: afterRevoke) == .revoked)
        try await trust.allowAgain(styleId: "flow", path: path, workspacePath: nil)
        #expect(StyleTrustStore.state(for: file, in: try await trust.load()) == .pending)
    }

    @Test func oneApprovalPerPlaceSoOldBytesCannotReturnSilently() async throws {
        let root = StyleFixtures.temporaryDirectory("style-trust")
        defer { try? FileManager.default.removeItem(at: root) }
        let trust = store(root)
        let path = root.appendingPathComponent("flow.json").path
        let v1 = StyleFixtures.data(StyleFixtures.flat, ["summary": "\"v1\""])
        let v2 = StyleFixtures.data(StyleFixtures.flat, ["summary": "\"v2\""])
        try await trust.approve(try style(v1, path: path))
        try await trust.approve(try style(v2, path: path))
        let records = try await trust.load()
        #expect(records.count == 1)
        #expect(StyleTrustStore.state(for: StyleFixtures.discovered(v2, source: .user, url: URL(fileURLWithPath: path)), in: records) == .approved)
        // Reverting the file to the dangerous first version asks again (§4.3).
        #expect(StyleTrustStore.state(for: StyleFixtures.discovered(v1, source: .user, url: URL(fileURLWithPath: path)), in: records) == .pending)
        // The same file in another clone is a different place.
        try await trust.approve(try style(v1, path: "/mine/.claude/mighty-styles/flow.json", workspacePath: "/mine"))
        let both = try await trust.load()
        let other = StyleFixtures.discovered(v1, source: .workspace, url: URL(fileURLWithPath: "/theirs/.claude/mighty-styles/flow.json"), workspacePath: "/theirs")
        #expect(StyleTrustStore.state(for: other, in: both) == .pending)
    }

    @Test func aDamagedOrOpenRecordFileClosesTheStore() async throws {
        let root = StyleFixtures.temporaryDirectory("style-trust")
        defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("style-trust", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let file = directory.appendingPathComponent("approvals.json")
        let broken = Data("{\"version\":1,\"records\":[".utf8)
        try broken.write(to: file)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
        let trust = store(root)
        await #expect(throws: StyleTrustFailure.self) { try await trust.load() }
        #expect(await trust.isLocked)
        await #expect(throws: StyleTrustFailure.self) { try await trust.approve(try StyleFixtures.registered(StyleFixtures.data())) }
        // Nothing was rewritten: a silent repair would erase every refusal.
        #expect(try Data(contentsOf: file) == broken)

        // A record file anyone else may write is the same kind of closed. The
        // rule is the write bit, so 0644 is readable but still trusted
        // (docs/mighty-styles-deviations.md, §4.3 against §9.3).
        let open = StyleFixtures.temporaryDirectory("style-trust-open")
        defer { try? FileManager.default.removeItem(at: open) }
        let openDirectory = open.appendingPathComponent("style-trust", isDirectory: true)
        try FileManager.default.createDirectory(at: openDirectory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let openFile = openDirectory.appendingPathComponent("approvals.json")
        try Data("{\"version\":1,\"records\":[]}".utf8).write(to: openFile)
        try FileManager.default.setAttributes([.posixPermissions: 0o666], ofItemAtPath: openFile.path)
        #expect(await store(open).isLocked)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: openFile.path)
        try FileManager.default.setAttributes([.posixPermissions: 0o777], ofItemAtPath: openDirectory.path)
        #expect(await store(open).isLocked)
        // A version this app does not read is closed too, never overwritten.
        let future = StyleFixtures.temporaryDirectory("style-trust-v2")
        defer { try? FileManager.default.removeItem(at: future) }
        let futureDirectory = future.appendingPathComponent("style-trust", isDirectory: true)
        try FileManager.default.createDirectory(at: futureDirectory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let futureFile = futureDirectory.appendingPathComponent("approvals.json")
        try Data("{\"version\":2,\"records\":[]}".utf8).write(to: futureFile)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: futureFile.path)
        #expect(await store(future).isLocked)
    }

    @Test func evictionKeepsRefusals() async throws {
        let root = StyleFixtures.temporaryDirectory("style-trust")
        defer { try? FileManager.default.removeItem(at: root) }
        let trust = store(root)
        for index in 0..<StyleLimits.maximumApprovalRecords {
            try await trust.approve(try style(StyleFixtures.data(), path: "/data/styles/\(index).json"))
        }
        var records = try await trust.load()
        #expect(records.count == StyleLimits.maximumApprovalRecords)
        try await trust.approve(try style(StyleFixtures.data(), path: "/data/styles/extra.json"))
        records = try await trust.load()
        #expect(records.count == StyleLimits.maximumApprovalRecords)
        #expect(records.contains { $0.path == "/data/styles/extra.json" } && !records.contains { $0.path == "/data/styles/0.json" })

        // A store that is all refusals has nothing it may drop.
        let full = StyleFixtures.temporaryDirectory("style-trust-full")
        defer { try? FileManager.default.removeItem(at: full) }
        let second = store(full)
        for index in 0..<StyleLimits.maximumApprovalRecords {
            try await second.revoke(try style(StyleFixtures.data(), path: "/data/styles/\(index).json"))
        }
        await #expect(throws: StyleTrustFailure.full) { try await second.approve(try style(StyleFixtures.data(), path: "/data/styles/extra.json")) }
        #expect(try await second.load().count == StyleLimits.maximumApprovalRecords)
    }

    @Test func aSecondProcessesWritesAreMergedNotErased() async throws {
        let root = StyleFixtures.temporaryDirectory("style-trust")
        defer { try? FileManager.default.removeItem(at: root) }
        let mine = store(root), theirs = store(root)
        try await mine.approve(try style(StyleFixtures.data(), path: "/data/styles/a.json"))
        try await theirs.approve(try style(StyleFixtures.data(), path: "/data/styles/b.json"))
        // `mine` still holds the older snapshot; its next write must not drop b.
        try await mine.approve(try style(StyleFixtures.data(), path: "/data/styles/c.json"))
        let paths = Set(try await mine.load().map(\.path))
        #expect(paths == ["/data/styles/a.json", "/data/styles/b.json", "/data/styles/c.json"])
    }

    @Test func theTrustStoreIsOutsideTheScannedFolder() async throws {
        let root = StyleFixtures.temporaryDirectory("style-data")
        defer { try? FileManager.default.removeItem(at: root) }
        let styles = root.appendingPathComponent("styles", isDirectory: true)
        try FileManager.default.createDirectory(at: styles, withIntermediateDirectories: true)
        let trust = store(root)
        try await trust.approve(try style(StyleFixtures.data(), path: styles.appendingPathComponent("flow.json").path))
        // A manifest that calls itself `approvals` copies to `approvals.json`
        // inside `styles/`, which is not where the records live (§4.3).
        try StyleFixtures.write(StyleFixtures.data(StyleFixtures.flat, ["id": "\"approvals\""]), to: styles.appendingPathComponent("approvals.json"))
        let found = StyleSourceScanner.user(directory: styles)
        #expect(found.map(\.url.lastPathComponent) == ["approvals.json"])
        let made = StyleRegistry.make(files: found, approvals: try await trust.load())
        #expect(made.styles.map(\.id) == ["approvals"] && made.rejections.isEmpty)
        #expect(try await trust.load().count == 1)
        #expect(!FileManager.default.fileExists(atPath: styles.appendingPathComponent("style-trust").path))
    }
}
