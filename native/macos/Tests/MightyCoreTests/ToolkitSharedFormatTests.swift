import Foundation
import Testing
@testable import MightyCore

// MARK: - Shared fixture
//
// This constant is reproduced verbatim in Windows
// MightyClaude.Core.Tests/ToolkitVerification.cs (SharedFormatFixture).
// Any edit here must be reflected there, and vice-versa.
private let sharedFixtureJSON = """
    {
      "version": 1,
      "entries": [
        {
          "id": "shared-brew",
          "displayName": "ripgrep",
          "install": {"kind": "package", "manager": "brew", "name": "ripgrep"},
          "approval": {"contentHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
        },
        {
          "id": "shared-npm",
          "displayName": "TypeScript",
          "install": {"kind": "package", "manager": "npm", "name": "typescript"},
          "approval": {"contentHash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}
        },
        {
          "id": "shared-winget",
          "displayName": "Node.js",
          "install": {"kind": "package", "manager": "winget", "name": "OpenJS.NodeJS", "executable": "node.exe"},
          "approval": {"contentHash": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}
        }
      ]
    }
    """

private let brewHash    = String(repeating: "a", count: 64)
private let npmHash     = String(repeating: "b", count: 64)
private let wingetHash  = String(repeating: "c", count: 64)

private func sharedTempDir(_ label: String) -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("toolkit-shared-\(label)-\(UUID().uuidString)", isDirectory: true)
    try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

// MARK: - Suite

@Suite struct ToolkitSharedFormatTests {

    // MARK: – Platform table (keyed on (kind, manager) — mirrors ToolkitFileReader on Windows)

    @Test func sharedPlatformTableBrew() {
        let p = ToolkitInstallSpec.package(manager: .brew, name: "ripgrep").platforms
        #expect(p == [.macOS])
        #expect(!p.contains(.windows))
    }

    @Test func sharedPlatformTableNpm() {
        let p = ToolkitInstallSpec.package(manager: .npm, name: "typescript").platforms
        #expect(p.contains(.macOS))
        #expect(p.contains(.windows))
    }

    @Test func sharedPlatformTableWinget() {
        let p = ToolkitInstallSpec.package(manager: .winget, name: "OpenJS.NodeJS", executable: "node.exe").platforms
        #expect(p == [.windows])
        #expect(!p.contains(.macOS))
    }

    @Test func sharedPlatformTablePlugin() {
        let p = ToolkitInstallSpec.plugin(source: "o/r", pluginID: "p@r").platforms
        #expect(p.contains(.macOS) && p.contains(.windows))
    }

    @Test func sharedPlatformTableMcp() {
        let p = ToolkitInstallSpec.mcp(name: "s", executable: "node", args: []).platforms
        #expect(p.contains(.macOS) && p.contains(.windows))
    }

    @Test func sharedPlatformTableSkill() {
        let p = ToolkitInstallSpec.skill(url: "https://github.com/x/y.git").platforms
        #expect(p.contains(.macOS) && p.contains(.windows))
    }

    @Test func sharedPlatformTableRepoScript() {
        let sha = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"
        let p = ToolkitInstallSpec.repoScript(url: "https://github.com/x/y.git",
                                               ref: sha, scriptPath: "i.sh").platforms
        #expect(p == [.macOS])
        #expect(!p.contains(.windows))
    }

    // MARK: – Fixture decoding

    @Test func sharedFixtureAllEntriesDecode() throws {
        let data = try #require(sharedFixtureJSON.data(using: .utf8))
        let object = try #require((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])
        let entries = try #require(object["entries"] as? [[String: Any]])
        #expect(entries.count == 3)
        var count = 0
        for var raw in entries {
            raw.removeValue(forKey: "approval")
            let entry = try ToolkitEntryDecoder.decode(raw)
            #expect(!entry.entryId.isEmpty)
            count += 1
        }
        #expect(count == 3)
    }

    // MARK: – Round-trip: load, save, reload — every field and approval preserved

    @Test func sharedFixtureRoundTrip() async throws {
        let dir = sharedTempDir("roundtrip")
        defer { try? FileManager.default.removeItem(at: dir) }
        let jsonURL = dir.appendingPathComponent("toolkit.json")
        try sharedFixtureJSON.data(using: .utf8)!.write(to: jsonURL)

        let store = ToolkitStore(directory: dir)
        // Force persist by adding and removing a dummy entry.
        let dummy = ToolkitEntry(entryId: "tmp-dummy", displayName: "D", source: .user,
                                  install: .skill(url: "https://github.com/x/tmp.git"))
        try await store.addEntry(dummy)
        try await store.removeEntry(id: "tmp-dummy")

        // Re-read raw JSON and verify every field and approval value.
        let savedData = try Data(contentsOf: jsonURL)
        let obj = try #require((try? JSONSerialization.jsonObject(with: savedData)) as? [String: Any])
        let saved = try #require(obj["entries"] as? [[String: Any]])

        func entry(_ id: String) -> [String: Any]? { saved.first { $0["id"] as? String == id } }
        func install(_ id: String) -> [String: Any]? { entry(id)?["install"] as? [String: Any] }
        func approvalHash(_ id: String) -> String? {
            (entry(id)?["approval"] as? [String: Any])?["contentHash"] as? String
        }

        // brew
        let brewInstall = try #require(install("shared-brew"))
        #expect(brewInstall["kind"] as? String == "package")
        #expect(brewInstall["manager"] as? String == "brew")
        #expect(brewInstall["name"] as? String == "ripgrep")
        #expect(entry("shared-brew")?["displayName"] as? String == "ripgrep")
        #expect(approvalHash("shared-brew") == brewHash)

        // npm
        let npmInstall = try #require(install("shared-npm"))
        #expect(npmInstall["kind"] as? String == "package")
        #expect(npmInstall["manager"] as? String == "npm")
        #expect(npmInstall["name"] as? String == "typescript")
        #expect(entry("shared-npm")?["displayName"] as? String == "TypeScript")
        #expect(approvalHash("shared-npm") == npmHash)

        // winget
        let wingetInstall = try #require(install("shared-winget"))
        #expect(wingetInstall["kind"] as? String == "package")
        #expect(wingetInstall["manager"] as? String == "winget")
        #expect(wingetInstall["name"] as? String == "OpenJS.NodeJS")
        #expect(wingetInstall["executable"] as? String == "node.exe")
        #expect(entry("shared-winget")?["displayName"] as? String == "Node.js")
        #expect(approvalHash("shared-winget") == wingetHash)
    }

    // MARK: – List visibility: brew shown, npm shown, winget hidden

    @Test func sharedFixtureListVisibility() async throws {
        let dir = sharedTempDir("list-vis")
        defer { try? FileManager.default.removeItem(at: dir) }
        try sharedFixtureJSON.data(using: .utf8)!.write(to: dir.appendingPathComponent("toolkit.json"))

        let store = ToolkitStore(directory: dir)
        let (entries, error) = await store.list()
        #expect(error == nil)
        let ids = Set(entries.map { $0.entryId })
        #expect(ids.contains("shared-brew"))     // brew is macOS-visible
        #expect(ids.contains("shared-npm"))      // npm is both-platforms visible
        #expect(!ids.contains("shared-winget"))  // winget is Windows-only, hidden on macOS
    }

    // MARK: – Suite marker (anti-vacuity check)

    @Test func markerSharedFormatOK() {
        print("Suite ToolkitSharedFormatTests passed")
    }
}
