import Foundation
import Testing
@testable import MightyCore

/// native/contracts/fixtures/workspace-create.json: the rules for making a workspace from a new folder or a Git address,
/// shared with MightyClaude.Core WorkspaceCreate.cs (WorkspaceCreateVerification).
struct WorkspaceCreateTests {
    struct Fixture: Decodable {
        struct RepoName: Decodable { let url: String; let name: String? }
        struct URLs: Decodable { let valid: [String]; let invalid: [String] }
        struct Name: Decodable { let name: String; let mac: String?; let windows: String? }
        struct ErrorCase: Decodable { let stderr: String; let kind: String }
        struct Progress: Decodable { let line: String; let phase: String?; let percent: Int?; let fraction: Double? }
        let repoNames: [RepoName]; let gitURLs: URLs; let names: [Name]; let errors: [ErrorCase]; let progress: [Progress]
    }

    static let fixture: Fixture = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return try! JSONDecoder().decode(Fixture.self, from: Data(contentsOf: url.appendingPathComponent("native/contracts/fixtures/workspace-create.json")))
    }()

    @Test func repoNamesFollowTheFixture() {
        for item in Self.fixture.repoNames { #expect(WorkspaceCreate.repoName(item.url) == item.name, "\(item.url)") }
    }

    @Test func gitURLsFollowTheFixture() {
        for url in Self.fixture.gitURLs.valid { #expect(WorkspaceCreate.looksLikeGitURL(url), "\(url)") }
        for url in Self.fixture.gitURLs.invalid { #expect(!WorkspaceCreate.looksLikeGitURL(url), "\(url)") }
    }

    @Test func folderNamesFollowTheFixtureOnBothPlatforms() {
        for item in Self.fixture.names {
            #expect(WorkspaceCreate.checkName(item.name)?.rawValue == item.mac, "mac \(item.name)")
            #expect(WorkspaceCreate.checkName(item.name, windows: true)?.rawValue == item.windows, "windows \(item.name)")
        }
    }

    @Test func gitErrorsFollowTheFixture() {
        for item in Self.fixture.errors { #expect(WorkspaceCreate.errorKind(stderr: item.stderr).rawValue == item.kind, "\(item.stderr)") }
    }

    @Test func cloneProgressFollowsTheFixture() {
        for item in Self.fixture.progress {
            let progress = WorkspaceCreate.progress(line: item.line)
            #expect(progress?.phase.rawValue == item.phase, "\(item.line)")
            #expect(progress?.percent == item.percent, "\(item.line)")
            if let fraction = item.fraction { #expect(abs((progress?.fraction ?? -1) - fraction) < 0.001, "\(item.line)") }
        }
    }

    @Test func defaultLocationPrefersTheLastThenTheActiveParentThenHome() {
        let home = NSTemporaryDirectory()
        let last = FileManager.default.temporaryDirectory.appendingPathComponent("wc-last-\(UUID().uuidString)").path
        try! FileManager.default.createDirectory(atPath: last, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(atPath: last) }
        #expect(WorkspaceCreate.defaultLocation(last: last, activeWorkspacePath: "/Users/x/Work/App", home: home) == last)
        #expect(WorkspaceCreate.defaultLocation(last: "/no/such/folder", activeWorkspacePath: home + "/App", home: home) == (home as NSString).standardizingPath)
        #expect(WorkspaceCreate.defaultLocation(last: nil, activeWorkspacePath: nil, home: home) == home)
    }

    // MARK: runners, against a local bare repository

    private func scratch() -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("wc-\(UUID().uuidString)")
        try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    private func bareRepository(in root: URL) async throws -> URL {
        let work = root.appendingPathComponent("work"), bare = root.appendingPathComponent("remote.git")
        try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
        try "hello\n".write(to: work.appendingPathComponent("README.md"), atomically: true, encoding: .utf8)
        let git = try #require(GitTool.executable)
        for arguments in [["init", "-q"], ["add", "README.md"], ["-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "first"]] {
            let result = try await ProcessCapture.run(executable: git, arguments: arguments, environment: nil, cwd: work, input: nil, timeout: 20, maximumBytes: 65_536)
            #expect(result.exitCode == 0)
        }
        let result = try await ProcessCapture.run(executable: git, arguments: ["clone", "-q", "--bare", work.path, bare.path], environment: nil, cwd: root, input: nil, timeout: 20, maximumBytes: 65_536)
        #expect(result.exitCode == 0)
        return bare
    }

    @Test func makeFolderMakesItAndInitsGitWhenAsked() async {
        let root = scratch(); defer { try? FileManager.default.removeItem(at: root) }
        let plain = root.appendingPathComponent("plain").path, repo = root.appendingPathComponent("repo").path
        #expect(await GitTool.makeFolder(at: plain, gitInit: false) == .made)
        #expect(!FileManager.default.fileExists(atPath: plain + "/.git"))
        #expect(await GitTool.makeFolder(at: repo, gitInit: true) == .made)
        #expect(FileManager.default.fileExists(atPath: repo + "/.git"))
        if case .failed = await GitTool.makeFolder(at: plain, gitInit: false) {} else { Issue.record("an existing folder must fail") }
    }

    @Test func probeReachesALocalRepositoryAndNamesAMissingOne() async throws {
        let root = scratch(); defer { try? FileManager.default.removeItem(at: root) }
        let bare = try await bareRepository(in: root)
        #expect(await GitTool.probe(bare.path) == .reachable)
        if case .failed(let kind, _) = await GitTool.probe(root.appendingPathComponent("none.git").path) { #expect(kind == .notFound) } else { Issue.record("a missing repository must fail") }
        if case .failed(let kind, _) = await GitTool.probe("not a url") { #expect(kind == .invalidURL) } else { Issue.record("a bad address must fail") }
    }

    @Test func cloneMakesTheFolderAndCancelLeavesNothing() async throws {
        let root = scratch(); defer { try? FileManager.default.removeItem(at: root) }
        let bare = try await bareRepository(in: root)
        let target = root.appendingPathComponent("copy").path
        let job = GitCloneJob(url: bare.path, target: target) { _ in }
        #expect(await job.run() == .done)
        #expect(FileManager.default.fileExists(atPath: target + "/README.md"))

        let cancelled = root.appendingPathComponent("cancelled").path
        let second = GitCloneJob(url: bare.path, target: cancelled) { _ in }
        second.cancel()
        #expect(await second.run() == .cancelled)
        #expect(!FileManager.default.fileExists(atPath: cancelled))

        let again = GitCloneJob(url: bare.path, target: target) { _ in }
        if case .failed = await again.run() {} else { Issue.record("an existing target must fail") }
        #expect(FileManager.default.fileExists(atPath: target + "/README.md"), "a refused clone must not delete what was there")
    }

    @Test func cloneOfAMissingRepositoryFailsAndLeavesNoFolder() async {
        let root = scratch(); defer { try? FileManager.default.removeItem(at: root) }
        // As a file:// address, so git answers through its transport (and makes the folder first) as for a server; a plain
        // missing path is refused before any folder ("does not exist"), and the probe already names that one notFound.
        let missing = "file://" + root.appendingPathComponent("none.git").path
        let target = root.appendingPathComponent("failed").path
        let job = GitCloneJob(url: missing, target: target) { _ in }
        let result = await job.run()
        if case .failed(let kind, _) = result { #expect(kind == .notFound, "\(result)") } else { Issue.record("a clone of a missing repository must fail: \(result)") }
        #expect(!FileManager.default.fileExists(atPath: target), "a failed clone leaves no folder")
    }
}
