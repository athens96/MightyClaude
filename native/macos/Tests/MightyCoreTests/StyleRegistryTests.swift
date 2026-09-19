import Foundation
import Testing
@testable import MightyCore

struct StyleRegistryTests {
    private func file(_ id: String, source: StyleSource, name: String, workspacePath: String? = nil, summary: String = "s") -> DiscoveredStyleFile {
        let data = StyleFixtures.data(StyleFixtures.flat, ["id": "\"\(id)\"", "summary": "\"\(summary)\""])
        return StyleFixtures.discovered(data, source: source, url: URL(fileURLWithPath: name), workspacePath: workspacePath)
    }

    @Test func precedenceRefusesRatherThanShadows() throws {
        let user = file("flow", source: .user, name: "/data/styles/flow.json")
        let workspace = file("flow", source: .workspace, name: "/repo/.claude/mighty-styles/flow.json", workspacePath: "/repo", summary: "저장소")
        let made = StyleRegistry.make(files: [workspace, user], approvals: [])
        #expect(made.styles.map { ($0.id, $0.source) }.count == 1 && made.styles[0].source == .user)
        #expect(made.rejections.count == 1 && made.rejections[0].error.code == "E_ID_COLLISION" && made.rejections[0].source == .workspace)
        #expect(made.rejections[0].error.message.contains("user"))
        // Bundled wins over both.
        let bundled = StyleFixtures.discovered(StyleFixtures.data(StyleFixtures.flat, ["id": "\"flow\""]), source: .bundled, url: URL(fileURLWithPath: "/app/flow.json"))
        let three = StyleRegistry.make(files: [workspace, user, bundled], approvals: [])
        #expect(three.styles.map(\.source) == [.bundled] && three.rejections.count == 2)
    }

    @Test func sameSourceCollisionIsDecidedByName() {
        let first = file("flow", source: .user, name: "/data/styles/a.json", summary: "첫째")
        let second = file("flow", source: .user, name: "/data/styles/b.json", summary: "둘째")
        let made = StyleRegistry.make(files: [second, first], approvals: [])
        #expect(made.styles.count == 1 && made.styles[0].path == "/data/styles/a.json")
        #expect(made.rejections.map(\.path) == ["/data/styles/b.json"])
    }

    @Test func applicableAndRunnableRespectWorkspaceAndHash() throws {
        let user = file("flow", source: .user, name: "/data/styles/flow.json")
        let mine = file("repo-flow", source: .workspace, name: "/mine/.claude/mighty-styles/repo-flow.json", workspacePath: "/mine")
        let theirs = file("other-flow", source: .workspace, name: "/theirs/.claude/mighty-styles/other-flow.json", workspacePath: "/theirs")
        let approvals = [StyleApprovalRecord(styleId: "flow", source: .user, path: "/data/styles/flow.json", hash: user.hash, state: "approved", decidedAt: Date()),
                         StyleApprovalRecord(styleId: "repo-flow", source: .workspace, path: mine.url.path, workspacePath: "/mine", hash: mine.hash, state: "approved", decidedAt: Date())]
        let registry = StyleRegistry(styles: StyleRegistry.make(files: [user, mine, theirs], approvals: approvals).styles)
        let here = StyleWorkspaceRef(path: "/mine", isRemote: false)
        #expect(registry.applicable(workspace: here).map(\.id).sorted() == ["flow", "repo-flow"])
        #expect(registry.applicable(workspace: nil).map(\.id) == ["flow"])
        // A remote pane can use no workspace style at all (§3.1).
        #expect(registry.applicable(workspace: StyleWorkspaceRef(path: "/mine", isRemote: true)).map(\.id) == ["flow"])
        #expect(registry.runnable("repo-flow", workspace: here, hash: mine.hash)?.id == "repo-flow")
        #expect(registry.runnable("repo-flow", workspace: here, hash: "0000") == nil)
        #expect(registry.runnable("repo-flow", workspace: here, hash: nil) == nil)
        #expect(registry.runnable("other-flow", workspace: here, hash: theirs.hash) == nil)
        // A bundled style is not hash-bound: its bytes change with every app version.
        let bundles = StyleRegistry(styles: BundledStyles.shared.styles())
        #expect(bundles.runnable("ouroboros", workspace: nil, hash: nil)?.id == "ouroboros")
    }

    @Test func requestTitlesComeFromEveryRunnableStyle() throws {
        let bundles = StyleRegistry(styles: BundledStyles.shared.styles())
        #expect(bundles.requestTitle(forInput: "/ouroboros:seed", workspace: nil) == "시드")
        #expect(bundles.requestTitle(forInput: "/nba", workspace: nil) == "🎯 nba")
        #expect(bundles.requestTitle(forInput: "/ouroboros:evaluate", workspace: nil) == "평가")
        #expect(bundles.requestTitle(forInput: "ooo 이거 해줘", workspace: nil) == nil)
        #expect(bundles.requestIcon(forInput: "/ouroboros:seed", workspace: nil)?.rawValue == "leaf")
        #expect(bundles.requestTint(forInput: "/nba", workspace: nil) == .accent)
        // Nothing recognised means no icon and no tint of its own, or every
        // plain request block would wear the style's presentation (§1.10).
        #expect(bundles.requestIcon(forInput: "/help 좀", workspace: nil) == nil)
        #expect(bundles.requestIcon(forInput: "그냥 문장", workspace: nil) == nil)
        #expect(bundles.requestTint(forInput: "/help 좀", workspace: nil) == .accent)
        #expect(bundles.requestTitle(forInput: "/help 좀", workspace: nil) == nil)
        // A pane that runs no style at all sweeps nothing: the old
        // `MightyStyles.requestTitle(forInput:style:nil)` answered nil, and an
        // empty sweep is how that pane is expressed now (§1.10).
        let plain = StyleRequestTitles()
        #expect(plain.prefix("/nba") == nil && plain.icon("/nba") == nil && plain.tint("/nba") == .accent)
        // An unapproved style names nothing, not even through a title (§4.5).
        let pending = file("flow", source: .user, name: "/data/styles/flow.json")
        let quiet = StyleRegistry(styles: StyleRegistry.make(files: [pending], approvals: []).styles)
        #expect(quiet.styles[0].approval == .pending && quiet.requestTitle(forInput: "/go", workspace: nil) == nil)
        #expect(quiet.runnableInPrecedence(workspace: nil).isEmpty)
        // Precedence is `bundled` > `user` > `workspace`, id order within one.
        #expect(bundles.runnableInPrecedence(workspace: nil).map(\.id) == ["ouroboros", "paperthin"])
    }

    @Test func savedStyleIsNormalisedByShapeAndOptionallyByKnownIds() throws {
        // The style survives normalization for local Claude panes only.
        var session = RunSession(workspaceId: "ws", title: "Claude"); session.mightyStyle = "ouroboros"; session.agentViewMode = "mighty"
        var codex = RunSession(workspaceId: "ws", title: "Codex", provider: "codex"); codex.mightyStyle = "ouroboros"
        var odd = RunSession(workspaceId: "ws", title: "Claude"); odd.mightyStyle = "something"
        let snapshot = AppSnapshot(workspaces: [Workspace(id: "ws", name: "R", path: "/tmp/r")], sessions: [session, codex, odd])
        let known = StateRepository.normalize(snapshot, restoring: true, knownStyleIds: ["ouroboros", "paperthin"])
        #expect(known.sessions.map(\.mightyStyle) == ["ouroboros", nil, nil])
        // The app passes no list, so a workspace style survives a restart that
        // happens before its repository is scanned (§3.4).
        let shapeOnly = StateRepository.normalize(snapshot, restoring: true)
        #expect(shapeOnly.sessions.map(\.mightyStyle) == ["ouroboros", nil, "something"])
        var bad = RunSession(workspaceId: "ws", title: "Claude"); bad.mightyStyle = "Bad_Shape"; bad.mightyStyleHash = "abc"
        let refused = StateRepository.normalize(AppSnapshot(workspaces: [Workspace(id: "ws", name: "R", path: "/tmp/r")], sessions: [bad]), restoring: true)
        #expect(refused.sessions[0].mightyStyle == nil && refused.sessions[0].mightyStyleHash == nil)
        var kept = RunSession(workspaceId: "ws", title: "Claude"); kept.mightyStyle = "ouroboros"; kept.mightyStyleHash = "abc"
        let round = try JSONDecoder().decode(RunSession.self, from: try JSONEncoder().encode(kept))
        #expect(round.mightyStyle == "ouroboros" && round.mightyStyleHash == "abc")
    }

    @Test func scanningFollowsNoLinksAndStopsAtTheCaps() throws {
        let root = StyleFixtures.temporaryDirectory("style-scan")
        defer { try? FileManager.default.removeItem(at: root) }
        let workspace = root.appendingPathComponent("repo", isDirectory: true)
        let directory = workspace.appendingPathComponent(".claude/mighty-styles", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        for index in 0..<40 { try StyleFixtures.write(StyleFixtures.data(StyleFixtures.flat, ["id": "\"s\(index)\""]), to: directory.appendingPathComponent(String(format: "%02d.json", index))) }
        try StyleFixtures.write(Data("not json".utf8), to: directory.appendingPathComponent("zz.txt"))
        let found = StyleSourceScanner.workspace(path: workspace.path)
        #expect(found.count == StyleLimits.maximumFilesPerSource && found.allSatisfy { $0.workspacePath == workspace.path.resolvedStylePath })
        // A hard link is indistinguishable from a plain file except by count.
        // The names sort *before* the 32 plain files, or the read would stop
        // at the cap and never look at them — and the guard would go untested.
        let linked = root.appendingPathComponent("elsewhere.json")
        try StyleFixtures.write(StyleFixtures.data(), to: linked)
        try FileManager.default.linkItem(at: linked, to: directory.appendingPathComponent("00a-hard.json"))
        try FileManager.default.createSymbolicLink(at: directory.appendingPathComponent("00b-soft.json"), withDestinationURL: linked)
        let found2 = StyleSourceScanner.workspace(path: workspace.path)
        let names = Set(found2.map(\.url.lastPathComponent))
        #expect(!names.contains("00a-hard.json") && !names.contains("00b-soft.json"))
        // Both are inside the window the read actually examines.
        #expect(names.contains("00.json") && names.contains("01.json") && found2.count == StyleLimits.maximumFilesPerSource)
        // A linked styles folder puts the source outside the repository.
        let outside = root.appendingPathComponent("outside", isDirectory: true)
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
        try StyleFixtures.write(StyleFixtures.data(), to: outside.appendingPathComponent("flow.json"))
        let second = root.appendingPathComponent("repo2", isDirectory: true)
        try FileManager.default.createDirectory(at: second.appendingPathComponent(".claude"), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: second.appendingPathComponent(".claude/mighty-styles"), withDestinationURL: outside)
        #expect(StyleSourceScanner.workspace(path: second.path).isEmpty)
    }
}

extension String {
    /// The scanner reports the workspace with its links resolved (§4.3).
    var resolvedStylePath: String { URL(fileURLWithPath: self, isDirectory: true).resolvingSymlinksInPath().path }
}
