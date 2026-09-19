import Foundation
import Testing
@testable import MightyCore

struct StyleCapabilityTests {
    private func write(_ text: String, _ url: URL, modified: Date? = nil) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: url)
        if let modified { try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: url.path) }
    }

    @Test func theCasebookHasThreeStatesAndAFixedFileOrder() throws {
        let root = StyleFixtures.temporaryDirectory("casebook")
        defer { try? FileManager.default.removeItem(at: root) }
        let workspace = root.appendingPathComponent("repo", isDirectory: true)
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        let names = [StyleCapabilityID.casebook]
        #expect(StyleCapabilities.evaluate(names, workspacePath: workspace.path).states == [StyleCapabilityID.casebook: "absent"])
        #expect(StyleCapabilityID.states(of: StyleCapabilityID.casebook) == ["absent", "open", "complete"])
        #expect(StyleCapabilityID.emptyDetail(of: StyleCapabilityID.casebook) == "열린 사이클이 없습니다. re0-plan으로 케이스북을 여세요.")
        #expect(StyleCapabilityID.states(of: "acme.thing").isEmpty && StyleCapabilityID.emptyDetail(of: "acme.thing") == nil)

        let folder = workspace.appendingPathComponent(".re0/iteration/0.3.0-now")
        for name in ["REF-api.local.md", "EVIDENCE.local.md", "WORKFLOW.local.md"] { try write(name, folder.appendingPathComponent(name)) }
        let open = StyleCapabilities.evaluate(names, workspacePath: workspace.path)
        #expect(open.states == [StyleCapabilityID.casebook: "open"])
        #expect(open.attachments.map(\.id) == ["WORKFLOW.local.md", "EVIDENCE.local.md", "REF-api.local.md"])
        #expect(open.attachments.map(\.title) == ["WORKFLOW", "EVIDENCE", "REF-api"])
        #expect(open.attachments.allSatisfy { $0.readOnly && $0.detail == "0.3.0-now \u{00B7} lightweight" })
        #expect(open.attachments[0].openPath == folder.appendingPathComponent("WORKFLOW.local.md").path)

        try write("d", folder.appendingPathComponent("DESIGN.local.md"))
        try write("r", folder.appendingPathComponent("RETRO.local.md"))
        let complete = StyleCapabilities.evaluate(names, workspacePath: workspace.path)
        #expect(complete.states == [StyleCapabilityID.casebook: "complete"])
        #expect(complete.attachments.map(\.id).prefix(4) == ["DESIGN.local.md", "WORKFLOW.local.md", "EVIDENCE.local.md", "RETRO.local.md"])
        #expect(complete.attachments.allSatisfy { $0.detail == "0.3.0-now \u{00B7} full" })
        // The payload carries 24 and the panel draws 6 (§1.8).
        for index in 0..<40 { try write("x", folder.appendingPathComponent("REF-\(index).local.md")) }
        let many = StyleCapabilities.evaluate(names, workspacePath: workspace.path)
        #expect(many.attachments.count == StyleLimits.maximumCasebookFiles && StyleLimits.maximumCasebookChips == 6)
        // An unnamed capability reads nothing at all.
        #expect(StyleCapabilities.evaluate([], workspacePath: workspace.path).states.isEmpty)
    }

    @Test func linksAreNeverFollowedAndHardLinksAreRefused() throws {
        let root = StyleFixtures.temporaryDirectory("casebook-links")
        defer { try? FileManager.default.removeItem(at: root) }
        let workspace = root.appendingPathComponent("repo", isDirectory: true)
        let folder = workspace.appendingPathComponent(".re0/iteration/0.1.0-real")
        try write("w", folder.appendingPathComponent("WORKFLOW.local.md"))
        let outside = root.appendingPathComponent("outside")
        try write("secret", outside.appendingPathComponent("DESIGN.local.md"))
        // An item that is a link, and a hard link that looks like a plain file.
        try FileManager.default.createSymbolicLink(at: folder.appendingPathComponent("AAA.local.md"), withDestinationURL: outside.appendingPathComponent("DESIGN.local.md"))
        try FileManager.default.linkItem(at: outside.appendingPathComponent("DESIGN.local.md"), to: folder.appendingPathComponent("BBB.local.md"))
        let casebook = try #require(StyleCasebook.latest(workspacePath: workspace.path))
        #expect(casebook.files == ["WORKFLOW.local.md"])
        // A linked iteration folder is outside the workspace and is not walked.
        let linked = root.appendingPathComponent("repo2", isDirectory: true)
        try FileManager.default.createDirectory(at: linked.appendingPathComponent(".re0"), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: linked.appendingPathComponent(".re0/iteration"), withDestinationURL: outside)
        #expect(StyleCasebook.latest(workspacePath: linked.path) == nil)
    }

    @Test func capabilityOutputIsNormalised() throws {
        let root = StyleFixtures.temporaryDirectory("casebook-bidi")
        defer { try? FileManager.default.removeItem(at: root) }
        let workspace = root.appendingPathComponent("repo", isDirectory: true)
        // A repository can name a folder with a direction control; neither the
        // Mac panel nor the phone payload may carry it through (§1.8).
        let folder = workspace.appendingPathComponent(".re0/iteration/0.1.0-\u{202E}evil")
        try write("w", folder.appendingPathComponent("WORKFLOW.local.md"))
        let result = StyleCapabilities.evaluate([StyleCapabilityID.casebook], workspacePath: workspace.path)
        let detail = try #require(result.attachments.first?.detail)
        #expect(detail.contains("\u{FFFD}") && !StyleText.containsBanned(detail))
        let raw = try #require(StyleCasebook.latest(workspacePath: workspace.path))
        #expect(StyleText.containsBanned(raw.name) && !StyleText.containsBanned(StyleCapabilities.normalised(raw).name))
        // The projection and the legacy payload are both built from the clean one.
        let style = StyleFixtures.bundled("paperthin")
        let panel = StylePanelProjection.make(style: style, prompts: [], selectedGroupId: "coil", capabilityStates: result.states,
                                              attachments: result.attachments, prerequisites: StylePrerequisiteResult(ready: true))
        #expect(panel.attachments.allSatisfy { !StyleText.containsBanned($0.detail ?? "") })
        let legacy = MobileLegacyStyleAdapter.payloads(style: style, panel: panel, casebook: raw)
        #expect(!StyleText.containsBanned(legacy.paperthin?.casebook?.name ?? ""))
        #expect((legacy.paperthin?.casebook?.name ?? "").contains("\u{FFFD}"))
    }

    @Test func longNamesAreCutBeforeTheyReachAScreen() throws {
        let root = StyleFixtures.temporaryDirectory("casebook-long")
        defer { try? FileManager.default.removeItem(at: root) }
        let workspace = root.appendingPathComponent("repo", isDirectory: true)
        let folder = workspace.appendingPathComponent(".re0/iteration/" + String(repeating: "a", count: 110))
        try write("w", folder.appendingPathComponent(String(repeating: "b", count: 100) + ".local.md"))
        let result = StyleCapabilities.evaluate([StyleCapabilityID.casebook], workspacePath: workspace.path)
        let item = try #require(result.attachments.first)
        #expect(item.title.count <= StyleLimits.maximumCapabilityString + 1)
        #expect((item.detail ?? "").count <= StyleLimits.maximumCapabilityString + 1)
    }
}
