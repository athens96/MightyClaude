# Workspace Create (새 폴더 · Git으로 생성) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Besides opening an existing folder, the Mac and Windows apps add a workspace by making a new folder (optionally `git init`) or by creating it from a Git address that this computer's credentials can reach.

**Architecture:** A shared fixture (`native/contracts/fixtures/workspace-create.json`) fixes the pure rules: URL → folder name, folder-name checks, git stderr → error kind, `git clone --progress` lines → phase and fraction. MightyCore (Swift) and MightyClaude.Core (C#) each implement those rules plus three runners: make a folder (+ `git init`), probe a URL with `git ls-remote`, and run a cancellable clone that streams progress. The Mac app adds two SwiftUI sheets. Windows adds two web dialogs (with bridge calls and a progress push) and two WinUI fallback dialogs. Every place that offers "open folder" today offers all three.

**Tech Stack:** Swift 6 / SwiftUI, Swift Testing (`@Test`), C# .NET (Core checks in `Verification.cs`), React + TypeScript + vitest (`native/windows/web`), WinUI 3.

Spec: `docs/superpowers/specs/2026-10-26-workspace-create-design.md`.

## Global Constraints

- **Platforms:** Mac + Windows (the web page and the WinUI fallback). The phone is unchanged.
- **Names:** the three items are "기존 폴더 열기…" (⌘O / Ctrl+O, as today), "새 폴더 만들기…" and "Git으로 생성…". The word is never "복제". The new items get no shortcut.
- **New folder sheet:**
  - Fields: name, location, ☐ "Git 저장소로 시작(git init)", a path preview, Cancel / 만들기.
  - If `git init` fails, the folder stays and the workspace is still added, with a notice.
- **Git sheet:**
  - Fields: URL, 연결 확인 link, location, folder name (filled from the URL and editable; once the user edits it, a URL change no longer overwrites it), path preview, Cancel / 생성.
  - Probing is automatic 0.6 s after typing stops: `git ls-remote --heads <url>` with a 20 s limit.
  - 생성 is enabled only when the probe succeeded, the target does not exist and the name passes.
- **Cloning:** `git clone --progress <url> <target>`.
  - While it runs, a progress bar and phase text show. Cancel stops git and deletes the target.
  - On failure the target is deleted and the sheet stays open with the error.
  - On success the folder goes through the existing add flow (approve → add → select → Claude pane).
- **Credentials:** the computer's own (credential helper / keychain, ssh-agent, gh/glab helpers).
  - The app never prompts. It sets `GIT_TERMINAL_PROMPT=0` and `GCM_INTERACTIVE=never`, plus `GIT_SSH_COMMAND="ssh -o BatchMode=yes"` unless the user already has `GIT_SSH_COMMAND` in the environment or `core.sshCommand` in `git config --global`.
  - The app never stores credentials.
- **Location default:** the last location used (`lastCreateLocation` in the snapshot, optional and compatible with old files). The first time, it is the parent of the active workspace; failing that, the home folder. It is saved on a successful create.
- **Copy:** all copy goes in `locales/{ko,en,zh,ja}.json` plus the copies that `scripts/check-locales.js` keeps in sync. No hard-coded Korean.
- **Builds and tests:** none unless the user asks (rule of 2026-10-23). Write the tests; run only the quick text checks: `node scripts/check-locales.js --check`, `node scripts/check-design-tokens.js --check`, `bash scripts/check-style-freeze.sh`, `node scripts/help/build.mjs --check`.
- **Repository:**
  - Work on `main` in `~/Work/MightyClaude` and do not push. The repo is public: no personal paths, emails or IPs.
  - Never commit `.ouroboros_eval_artifact.md`.
  - Commit subjects are `(area) what the user now gets` and end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Check that `git config user.email` is `11283764+athens96@users.noreply.github.com` before committing.
- **Out of scope:** the phone, remote (Tailscale) workspaces, branch choice, submodules, shallow clones, account choice, typing credentials, auto-sharing a new workspace remotely.

---

## File Structure

| File | Responsibility |
|---|---|
| `native/contracts/fixtures/workspace-create.json` (new) | Shared cases: repo names, folder-name checks per platform, error kinds, progress lines |
| `native/macos/Sources/MightyCore/WorkspaceCreate.swift` (new) | Pure rules: `repoName`, `checkName`, `errorKind`, `progress`; `defaultLocation` |
| `native/macos/Sources/MightyCore/WorkspaceCreateGit.swift` (new) | Runners: `GitTool` (find git, environment), `makeFolder`, `probe`, `GitCloneJob` |
| `native/macos/Tests/MightyCoreTests/WorkspaceCreateTests.swift` (new) | Fixture cases and local bare-repo runner tests |
| `native/macos/Sources/MightyCore/Models.swift`, `StateRepository.swift` | `AppSnapshot.lastCreateLocation` |
| `native/macos/Sources/MightyClaude/WorkspaceCreateViews.swift` (new) | `NewFolderSheet`, `GitCreateSheet`, `WorkspaceAddChoices` (the three items) |
| `native/macos/Sources/MightyClaude/AppStore+WorkspaceCreate.swift` (new) | Sheet state and create functions on `AppStore` |
| `native/macos/Sources/MightyClaude/AppStore.swift`, `WorkspaceView.swift`, `MightyClaudeApp.swift` | `hasBlockingModal`, sheets, entry points |
| `native/windows/MightyClaude.Core/WorkspaceCreate.cs` (new) | Same pure rules and runners in C# |
| `native/windows/MightyClaude.Core/AddPaneMenu.cs`, `Models.cs`, `StateStore.cs`, `DesktopService.cs` | Menu entries `newFolder` / `fromGit`, `LastCreateLocation`, an add-path helper |
| `native/windows/MightyClaude.Core.Tests/WorkspaceCreateVerification.cs` (new), `Verification.cs`, `.csproj` | Core checks reading the embedded fixture |
| `native/windows/MightyClaude.WinUI/MainWindow.WebShell.Shell.cs`, `MainWindow.WorkspaceCreate.cs` (new) | Bridge methods, dialog flows, WinUI fallback dialogs |
| `native/windows/web/src/dialogs/WorkspaceCreate.tsx` (new), `Dialogs.tsx`, `shell/types.ts`, `Sidebar.tsx`, `Dashboard.tsx`, `addMenu.tsx` | Web dialogs and entry points |
| `native/windows/web/src/dialogs/workspaceCreate.test.ts` (new) | vitest for the button rules and the name autofill |
| `docs/help/content/{ko,en,zh,ja}/getting-started.md`, `layout.md`, `windows.md`, `docs/i18n.md` | Help and the generated i18n doc |

---

### Task 1: Shared fixture + Mac core (rules, runners, tests)

**Files:**
- Create: `native/contracts/fixtures/workspace-create.json`
- Create: `native/macos/Sources/MightyCore/WorkspaceCreate.swift`
- Create: `native/macos/Sources/MightyCore/WorkspaceCreateGit.swift`
- Create: `native/macos/Tests/MightyCoreTests/WorkspaceCreateTests.swift`
- Modify: `native/macos/Sources/MightyCore/Models.swift` (`AppSnapshot`: add `lastCreateLocation: String?` after `backgroundWorkOpen`, in the init with default `nil`)
- Modify: `native/macos/Sources/MightyCore/StateRepository.swift` (decode `object["lastCreateLocation"] as? String` where `backgroundWorkOpen` is read, about line 219; encode it where `output.backgroundWorkOpen` is set, about line 341)

**Interfaces:**
- Produces, all `public` in MightyCore:
  - Pure rules:
    - `enum WorkspaceCreate`
    - `static func repoName(_ url: String) -> String?`
    - `enum NameProblem: String { case empty, badCharacter, reserved, trailingDotOrSpace, dots, tooLong }`
    - `static func checkName(_ name: String, windows: Bool = false) -> NameProblem?`
    - `enum GitErrorKind: String { case gitMissing, invalidURL, auth, notFound, network, other }`
    - `static func errorKind(stderr: String) -> GitErrorKind`
    - `static func looksLikeGitURL(_ url: String) -> Bool`
    - `struct CloneProgress: Equatable { phase: Phase; fraction: Double; percent: Int }` with `enum Phase: String { case starting, receiving, resolving, checkout }`
    - `static func progress(line: String) -> CloneProgress?`
    - `static func defaultLocation(last: String?, activeWorkspacePath: String?, home: String) -> String`
    - `static func target(location: String, name: String) -> String` (the location with the trimmed name appended)
  - Runners:
    - `enum GitTool`
    - `static var executable: URL?`
    - `static func environment(base: [String: String]) async -> [String: String]`
    - `static func makeFolder(at path: String, gitInit: Bool) async -> MakeFolderResult`, where `enum MakeFolderResult { case made, madeButGitInitFailed(String), failed(String) }`
    - `static func probe(_ url: String) async -> ProbeResult`, where `enum ProbeResult: Equatable { case reachable, failed(GitErrorKind, String) }`
    - `final class GitCloneJob: @unchecked Sendable`
      - `init(url: String, target: String, progress: @escaping @Sendable (CloneProgress) -> Void)`
      - `func run() async -> CloneResult`, where `enum CloneResult: Equatable { case done, cancelled, failed(GitErrorKind, String) }`
      - `func cancel()`
  - `AppSnapshot.lastCreateLocation: String?`

- [ ] **Step 1: Write the shared fixture**

`native/contracts/fixtures/workspace-create.json`:

```json
{
  "about": "Workspace create rules shared by MightyCore WorkspaceCreate.swift and MightyClaude.Core WorkspaceCreate.cs (docs/superpowers/specs/2026-10-26-workspace-create-design.md).",
  "repoNames": [
    { "url": "https://github.com/athens96/MightyClaude.git", "name": "MightyClaude" },
    { "url": "https://github.com/athens96/MightyClaude", "name": "MightyClaude" },
    { "url": "https://github.com/athens96/MightyClaude/", "name": "MightyClaude" },
    { "url": "https://gitlab.com/group/sub/project.git", "name": "project" },
    { "url": "git@github.com:owner/my-repo.git", "name": "my-repo" },
    { "url": "ssh://git@example.com:2222/owner/tool.git", "name": "tool" },
    { "url": "git://example.com/proj.git", "name": "proj" },
    { "url": "file:///tmp/remote/demo.git", "name": "demo" },
    { "url": "/tmp/remote/demo.git", "name": "demo" },
    { "url": "  https://github.com/a/b.git  ", "name": "b" },
    { "url": "https://github.com/", "name": null },
    { "url": "", "name": null },
    { "url": "not a url", "name": null }
  ],
  "gitURLs": {
    "valid": ["https://github.com/a/b.git", "http://host/a/b", "ssh://git@host/a/b.git", "git@github.com:a/b.git", "git://host/a.git", "file:///tmp/a.git", "/tmp/a.git", "C:\\repos\\a.git"],
    "invalid": ["", "   ", "not a url", "github.com/a/b", "https://", "ftp://host/a.git"]
  },
  "names": [
    { "name": "my-app", "mac": null, "windows": null },
    { "name": "  my-app  ", "mac": null, "windows": null },
    { "name": "", "mac": "empty", "windows": "empty" },
    { "name": "   ", "mac": "empty", "windows": "empty" },
    { "name": "a/b", "mac": "badCharacter", "windows": "badCharacter" },
    { "name": "a\\b", "mac": "badCharacter", "windows": "badCharacter" },
    { "name": "a:b", "mac": "badCharacter", "windows": "badCharacter" },
    { "name": "a?b", "mac": null, "windows": "badCharacter" },
    { "name": "a*b", "mac": null, "windows": "badCharacter" },
    { "name": "a<b", "mac": null, "windows": "badCharacter" },
    { "name": "a|b", "mac": null, "windows": "badCharacter" },
    { "name": "a\"b", "mac": null, "windows": "badCharacter" },
    { "name": "line\nbreak", "mac": "badCharacter", "windows": "badCharacter" },
    { "name": ".", "mac": "dots", "windows": "dots" },
    { "name": "..", "mac": "dots", "windows": "dots" },
    { "name": "CON", "mac": null, "windows": "reserved" },
    { "name": "nul", "mac": null, "windows": "reserved" },
    { "name": "COM1", "mac": null, "windows": "reserved" },
    { "name": "lpt9.txt", "mac": null, "windows": "reserved" },
    { "name": "console", "mac": null, "windows": null },
    { "name": "name.", "mac": null, "windows": "trailingDotOrSpace" },
    { "name": ".hidden", "mac": null, "windows": null },
    { "name": "한글 폴더", "mac": null, "windows": null },
    { "name": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "mac": "tooLong", "windows": "tooLong" }
  ],
  "errors": [
    { "stderr": "fatal: Authentication failed for 'https://github.com/a/b.git/'", "kind": "auth" },
    { "stderr": "fatal: could not read Username for 'https://github.com': terminal prompts disabled", "kind": "auth" },
    { "stderr": "git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.", "kind": "auth" },
    { "stderr": "remote: HTTP Basic: Access denied\nfatal: Authentication failed for 'https://gitlab.com/a/b.git/'", "kind": "auth" },
    { "stderr": "fatal: unable to access 'https://host/a.git/': The requested URL returned error: 403", "kind": "auth" },
    { "stderr": "fatal: unable to access 'https://host/a.git/': The requested URL returned error: 401", "kind": "auth" },
    { "stderr": "remote: Repository not found.\nfatal: repository 'https://github.com/a/b.git/' not found", "kind": "notFound" },
    { "stderr": "fatal: unable to access 'https://host/a.git/': The requested URL returned error: 404", "kind": "notFound" },
    { "stderr": "fatal: '/tmp/none.git' does not appear to be a git repository\nfatal: Could not read from remote repository.", "kind": "notFound" },
    { "stderr": "fatal: unable to access 'https://nohost.invalid/a.git/': Could not resolve host: nohost.invalid", "kind": "network" },
    { "stderr": "ssh: Could not resolve hostname nohost.invalid: nodename nor servname provided, or not known", "kind": "network" },
    { "stderr": "fatal: unable to access 'https://host/a.git/': Failed to connect to host port 443 after 75000 ms: Connection refused", "kind": "network" },
    { "stderr": "ssh: connect to host host port 22: Operation timed out", "kind": "network" },
    { "stderr": "ssh: connect to host host port 22: Network is unreachable", "kind": "network" },
    { "stderr": "fatal: protocol 'ftp' is not supported", "kind": "invalidURL" },
    { "stderr": "fatal: destination path 'x' already exists and is not an empty directory.", "kind": "other" }
  ],
  "progress": [
    { "line": "Cloning into 'demo'...", "phase": "starting", "percent": 0, "fraction": 0 },
    { "line": "remote: Counting objects: 100% (10/10), done.", "phase": "starting", "percent": 0, "fraction": 0 },
    { "line": "Receiving objects:   0% (0/1000)", "phase": "receiving", "percent": 0, "fraction": 0 },
    { "line": "Receiving objects:  42% (420/1000), 1.20 MiB | 2.00 MiB/s", "phase": "receiving", "percent": 42, "fraction": 0.294 },
    { "line": "Receiving objects: 100% (1000/1000), 3.00 MiB | 2.00 MiB/s, done.", "phase": "receiving", "percent": 100, "fraction": 0.7 },
    { "line": "Resolving deltas:  50% (5/10)", "phase": "resolving", "percent": 50, "fraction": 0.8 },
    { "line": "Updating files:  50% (5/10)", "phase": "checkout", "percent": 50, "fraction": 0.95 },
    { "line": "Updating files: 100% (10/10), done.", "phase": "checkout", "percent": 100, "fraction": 1 },
    { "line": "warning: something", "phase": null, "percent": null, "fraction": null }
  ]
}
```

The rules this fixture fixes (both platforms):
- **`repoName`:**
  1. Trim, then drop a `?query` or `#fragment`, then trailing `/`, then a trailing `.git`.
  2. Take the text after the last `/`, or after the last `:` when there is no `/` after the scheme (scp form).
  3. The result is null when that is empty, or when `looksLikeGitURL` is false.
- **`looksLikeGitURL`:** after trimming, any one of these:
  - a scheme in `https http ssh git file` followed by `://` and a non-empty rest;
  - the scp form `user@host:path` (`^[^/\s@]+@[^/\s:]+:\S+$`);
  - an absolute local path (`/…` on the Mac; `X:\…`, `X:/…` or `\\…` on Windows; the `C:\repos\a.git` case is valid on both platforms, so the check accepts both forms on both).
- **`checkName(windows:)`:** trim surrounding whitespace first, then check in this order:
  1. empty → `empty`
  2. `.` or `..` → `dots`
  3. more than 255 UTF-8 bytes → `tooLong`
  4. contains `/`, `\`, `:` or a control character → `badCharacter`
  5. Windows only:
     - contains any of `<>"|?*` → `badCharacter`
     - the base name (the part before the first `.`, compared case-insensitively) is one of CON PRN AUX NUL COM1–COM9 LPT1–LPT9 → `reserved`
     - ends with `.` or a space → `trailingDotOrSpace`
- **`errorKind`:** match in this order, case-insensitively.

  | Kind | Substrings |
  |---|---|
  | `auth` | `authentication failed`, `could not read username`, `could not read password`, `permission denied`, `access denied`, `error: 401`, `error: 403` |
  | `notFound` | `not found`, `error: 404`, `does not appear to be a git repository` |
  | `network` | `could not resolve host`, `could not resolve hostname`, `connection refused`, `timed out`, `network is unreachable`, `failed to connect`, `connection reset` |
  | `invalidURL` | `is not supported`, `not a valid`, `invalid url` |
  | `other` | anything else |

  `gitMissing` is never produced by `errorKind`; only the runners produce it, when no git executable is found.
- **`progress`:**
  - `Receiving objects: N%` → `receiving`, fraction `0.7*N/100`.
  - `Resolving deltas: N%` → `resolving`, fraction `0.7 + 0.2*N/100`.
  - `Updating files: N%` or `Checking out files: N%` → `checkout`, fraction `0.9 + 0.1*N/100`.
  - `Cloning into`, `remote: …` (counting, compressing, enumerating) → `starting`, percent 0, fraction 0.
  - Anything else → nil.
  - Round the fraction to 3 decimals. Tests compare with a tolerance of 0.001.

- [ ] **Step 2: Write the failing Mac tests**

`native/macos/Tests/MightyCoreTests/WorkspaceCreateTests.swift`:

```swift
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
        for arguments in [["init", "-q"], ["add", "README.md"], ["-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-q", "-m", "first"]] {
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
}
```

- [ ] **Step 3: (Run only if the user asks.)** `DEVELOPER_DIR=/Library/Developer/CommandLineTools bash scripts/test-native-macos.sh --scratch-path /tmp/mc-wc --filter '\.WorkspaceCreateTests/'`. Expected now: FAIL (types missing).

- [ ] **Step 4: Write the pure rules**

`native/macos/Sources/MightyCore/WorkspaceCreate.swift`:

```swift
import Foundation

/// The rules for adding a workspace from a new folder or a Git address
/// (docs/superpowers/specs/2026-10-26-workspace-create-design.md), shared with
/// MightyClaude.Core WorkspaceCreate.cs through native/contracts/fixtures/workspace-create.json.
public enum WorkspaceCreate {
    public enum NameProblem: String, Sendable { case empty, badCharacter, reserved, trailingDotOrSpace, dots, tooLong }
    public enum GitErrorKind: String, Sendable { case gitMissing, invalidURL, auth, notFound, network, other }

    public struct CloneProgress: Equatable, Sendable {
        public enum Phase: String, Sendable { case starting, receiving, resolving, checkout }
        public let phase: Phase; public let percent: Int; public let fraction: Double
    }

    private static let schemes = ["https://", "http://", "ssh://", "git://", "file://"]

    public static func looksLikeGitURL(_ raw: String) -> Bool {
        let url = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !url.isEmpty, !url.contains(where: \.isWhitespace) else { return false }
        if let scheme = schemes.first(where: { url.lowercased().hasPrefix($0) }) { return url.count > scheme.count }
        if url.range(of: #"^[^/\s@]+@[^/\s:]+:\S+$"#, options: .regularExpression) != nil { return true }
        if url.hasPrefix("/") || url.hasPrefix("\\\\") { return true }
        return url.range(of: #"^[A-Za-z]:[\\/]"#, options: .regularExpression) != nil
    }

    public static func repoName(_ raw: String) -> String? {
        guard looksLikeGitURL(raw) else { return nil }
        var url = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if let cut = url.firstIndex(where: { $0 == "?" || $0 == "#" }) { url = String(url[..<cut]) }
        while url.hasSuffix("/") || url.hasSuffix("\\") { url.removeLast() }
        if url.lowercased().hasSuffix(".git") { url.removeLast(4) }
        let afterScheme = schemes.first(where: { url.lowercased().hasPrefix($0) }).map { String(url.dropFirst($0.count)) }
        let path = afterScheme.map { rest in rest.contains("/") ? String(rest[rest.firstIndex(of: "/")!...]) : "" } ?? url
        let name = path.split(whereSeparator: { $0 == "/" || $0 == "\\" || (afterScheme == nil && $0 == ":") }).last.map(String.init) ?? ""
        return name.isEmpty ? nil : name
    }

    private static let reservedNames: Set<String> = Set(["CON", "PRN", "AUX", "NUL"] + (1...9).map { "COM\($0)" } + (1...9).map { "LPT\($0)" })

    public static func checkName(_ raw: String, windows: Bool = false) -> NameProblem? {
        let name = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if name.isEmpty { return .empty }
        if name == "." || name == ".." { return .dots }
        if name.utf8.count > 255 { return .tooLong }
        if name.contains(where: { "/\\:".contains($0) }) || name.unicodeScalars.contains(where: { $0.properties.generalCategory == .control }) { return .badCharacter }
        guard windows else { return nil }
        if name.contains(where: { "<>\"|?*".contains($0) }) { return .badCharacter }
        let base = String(name.split(separator: ".", maxSplits: 1, omittingEmptySubsequences: false).first ?? "").uppercased()
        if reservedNames.contains(base) { return .reserved }
        if name.hasSuffix(".") || name.hasSuffix(" ") { return .trailingDotOrSpace }
        return nil
    }

    public static func errorKind(stderr: String) -> GitErrorKind {
        let text = stderr.lowercased()
        let table: [(GitErrorKind, [String])] = [
            (.auth, ["authentication failed", "could not read username", "could not read password", "permission denied", "access denied", "error: 401", "error: 403"]),
            (.notFound, ["not found", "error: 404", "does not appear to be a git repository"]),
            (.network, ["could not resolve host", "could not resolve hostname", "connection refused", "timed out", "network is unreachable", "failed to connect", "connection reset"]),
            (.invalidURL, ["is not supported", "not a valid", "invalid url"]),
        ]
        return table.first(where: { $0.1.contains(where: text.contains) })?.0 ?? .other
    }

    public static func progress(line raw: String) -> CloneProgress? {
        let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        func percent() -> Int? {
            guard let range = line.range(of: #"(\d{1,3})%"#, options: .regularExpression) else { return nil }
            return min(100, Int(line[range].dropLast()) ?? 0)
        }
        func make(_ phase: CloneProgress.Phase, _ base: Double, _ span: Double) -> CloneProgress? {
            guard let p = percent() else { return nil }
            return CloneProgress(phase: phase, percent: p, fraction: ((base + span * Double(p) / 100) * 1000).rounded() / 1000)
        }
        if line.hasPrefix("Receiving objects:") { return make(.receiving, 0, 0.7) }
        if line.hasPrefix("Resolving deltas:") { return make(.resolving, 0.7, 0.2) }
        if line.hasPrefix("Updating files:") || line.hasPrefix("Checking out files:") { return make(.checkout, 0.9, 0.1) }
        if line.hasPrefix("Cloning into") || line.hasPrefix("remote:") { return CloneProgress(phase: .starting, percent: 0, fraction: 0) }
        return nil
    }

    public static func target(location: String, name: String) -> String {
        (location as NSString).appendingPathComponent(name.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    public static func defaultLocation(last: String?, activeWorkspacePath: String?, home: String) -> String {
        var isFolder: ObjCBool = false
        if let last, FileManager.default.fileExists(atPath: last, isDirectory: &isFolder), isFolder.boolValue { return last }
        if let active = activeWorkspacePath {
            let parent = ((active as NSString).deletingLastPathComponent as NSString).standardizingPath
            if FileManager.default.fileExists(atPath: parent, isDirectory: &isFolder), isFolder.boolValue { return parent }
        }
        return home
    }
}
```

Mind the scp case. `git@github.com:owner/my-repo.git` has no scheme, so the split also breaks on `:`, and the last piece is `my-repo`. For `ssh://git@example.com:2222/owner/tool.git`, the path after the first `/` past the scheme is `/owner/tool`, so the result is `tool`. For `https://github.com/`, the path is empty, so the result is nil.

- [ ] **Step 5: Write the runners**

`native/macos/Sources/MightyCore/WorkspaceCreateGit.swift`:

```swift
import Foundation

/// Git for making workspaces: the user's own git, configuration and credentials, never a prompt.
public enum GitTool {
    public static var executable: URL? {
        DelegationGit.executable ?? ToolkitProcess.resolveExecutable("git", environment: ProcessInfo.processInfo.environment)
    }

    /// No prompt can appear: a credential helper or ssh-agent answers, or git fails at once.
    public static func environment(base: [String: String] = ProcessInfo.processInfo.environment) async -> [String: String] {
        var environment = base.filter { $0.key != "GIT_DIR" && $0.key != "GIT_WORK_TREE" && $0.key != "GIT_INDEX_FILE" }
        environment["GIT_TERMINAL_PROMPT"] = "0"; environment["GCM_INTERACTIVE"] = "never"; environment["LC_ALL"] = "C"
        environment["SSH_ASKPASS_REQUIRE"] = "never"
        if environment["GIT_SSH_COMMAND"] == nil, await globalSSHCommand() == nil { environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes" }
        return environment
    }

    private static func globalSSHCommand() async -> String? {
        guard let executable else { return nil }
        let result = try? await ProcessCapture.run(executable: executable, arguments: ["config", "--global", "--get", "core.sshCommand"], environment: nil, cwd: FileManager.default.homeDirectoryForCurrentUser, input: nil, timeout: 5, maximumBytes: 4096)
        let value = result.map { DelegationGit.line($0.stdout) } ?? ""
        return result?.exitCode == 0 && !value.isEmpty ? value : nil
    }

    public enum MakeFolderResult: Equatable, Sendable { case made, madeButGitInitFailed(String), failed(String) }

    public static func makeFolder(at path: String, gitInit: Bool) async -> MakeFolderResult {
        do { try FileManager.default.createDirectory(atPath: path, withIntermediateDirectories: false) }
        catch { return .failed(error.localizedDescription) }
        guard gitInit else { return .made }
        guard let executable else { return .madeButGitInitFailed(L("workspace.git.error.gitMissing")) }
        let result = try? await ProcessCapture.run(executable: executable, arguments: ["init", "-q"], environment: await environment(), cwd: URL(fileURLWithPath: path), input: nil, timeout: 20, maximumBytes: 65_536)
        guard let result else { return .madeButGitInitFailed(L("workspace.git.error.gitMissing")) }
        return result.exitCode == 0 ? .made : .madeButGitInitFailed(DelegationGit.message(result))
    }

    public enum ProbeResult: Equatable, Sendable { case reachable, failed(WorkspaceCreate.GitErrorKind, String) }

    public static func probe(_ raw: String) async -> ProbeResult {
        let url = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard WorkspaceCreate.looksLikeGitURL(url) else { return .failed(.invalidURL, "") }
        guard let executable else { return .failed(.gitMissing, "") }
        do {
            let result = try await ProcessCapture.run(executable: executable, arguments: ["ls-remote", "--heads", "--", url], environment: await environment(), cwd: FileManager.default.temporaryDirectory, input: nil, timeout: 20, maximumBytes: 1_048_576)
            if result.exitCode == 0 { return .reachable }
            let text = String(decoding: result.stderr, as: UTF8.self)
            return .failed(WorkspaceCreate.errorKind(stderr: text), DelegationGit.message(result))
        } catch {
            return .failed(.network, error.localizedDescription)
        }
    }
}

/// `git clone --progress` into a folder that must not exist yet; progress from its stderr, cancel stops it,
/// and a cancelled or failed clone leaves no folder behind.
public final class GitCloneJob: @unchecked Sendable {
    public enum CloneResult: Equatable, Sendable { case done, cancelled, failed(WorkspaceCreate.GitErrorKind, String) }

    private let url: String, target: String
    private let onProgress: @Sendable (WorkspaceCreate.CloneProgress) -> Void
    private let lock = NSLock()
    private var child: NativeChildProcess?
    private var cancelled = false

    public init(url: String, target: String, progress: @escaping @Sendable (WorkspaceCreate.CloneProgress) -> Void) {
        self.url = url.trimmingCharacters(in: .whitespacesAndNewlines); self.target = target; self.onProgress = progress
    }

    public func cancel() {
        lock.lock(); cancelled = true; let child = self.child; lock.unlock()
        child?.stop()
    }

    private var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return cancelled }

    public func run() async -> CloneResult {
        if isCancelled { return .cancelled }
        guard WorkspaceCreate.looksLikeGitURL(url) else { return .failed(.invalidURL, "") }
        guard let executable = GitTool.executable else { return .failed(.gitMissing, "") }
        if FileManager.default.fileExists(atPath: target) { return .failed(.other, L("workspace.name.error.exists")) }
        let environment = await GitTool.environment()
        let errors = StderrLines()
        let code: Int32 = await withCheckedContinuation { continuation in
            do {
                let child = try NativeChildProcess(
                    executable: executable, arguments: ["clone", "--progress", "--", url, target], environment: environment,
                    cwd: URL(fileURLWithPath: (target as NSString).deletingLastPathComponent),
                    stdout: { _ in },
                    stderr: { [onProgress] data in for line in errors.append(data) { if let p = WorkspaceCreate.progress(line: line) { onProgress(p) } } },
                    exited: { continuation.resume(returning: $0) })
                lock.lock(); self.child = child; let stop = cancelled; lock.unlock()
                child.write(Data(), closeAfter: true)
                if stop { child.stop() }
            } catch {
                continuation.resume(returning: -1)
            }
        }
        lock.lock(); child = nil; lock.unlock()
        if code == 0 && !isCancelled { return .done }
        try? FileManager.default.removeItem(atPath: target)
        if isCancelled { return .cancelled }
        let text = errors.text
        return .failed(code == -1 ? .gitMissing : WorkspaceCreate.errorKind(stderr: text), text.split(separator: "\n").last.map(String.init) ?? "")
    }
}

/// stderr split on `\r` and `\n` (git redraws its progress line with `\r`); keeps the last 64 KiB of text.
private final class StderrLines: @unchecked Sendable {
    private let lock = NSLock(); private var pending = ""; private var all = ""
    func append(_ data: Data) -> [String] {
        lock.lock(); defer { lock.unlock() }
        pending += String(decoding: data, as: UTF8.self)
        var lines: [String] = []
        while let cut = pending.firstIndex(where: { $0 == "\r" || $0 == "\n" }) {
            let line = String(pending[..<cut]); pending = String(pending[pending.index(after: cut)...])
            if !line.isEmpty { lines.append(line); all += line + "\n" }
        }
        if all.utf8.count > 65_536 { all = String(all.suffix(32_768)) }
        return lines
    }
    var text: String { lock.lock(); defer { lock.unlock() }; return (all + pending).trimmingCharacters(in: .whitespacesAndNewlines) }
}
```

Check `NativeChildProcess`'s actual `write(_:closeAfter:)` and `stop()` signatures in `ProcessRunner.swift` (around lines 12–260), and adapt the calls if they differ. If `exited` can fire before `init` returns, the continuation still resumes exactly once. The existing `ToolkitProcess.run` uses the same shape; follow it.

The "an existing target must fail" test expects the refused clone to keep the existing folder. That is why the `fileExists` guard returns before any removal.

- [ ] **Step 6: Add `lastCreateLocation` to the snapshot**

`Models.swift`:
- Add `public var lastCreateLocation: String?` after `backgroundWorkOpen`.
- Add an init parameter `lastCreateLocation: String? = nil` and assign it.

`StateRepository.swift`:
- In the decode call around line 219, add `lastCreateLocation: object["lastCreateLocation"] as? String`.
- Where `output.backgroundWorkOpen = value.backgroundWorkOpen` (around line 341), add `output.lastCreateLocation = value.lastCreateLocation`.

If `SnapshotPreferencesTests` has a round-trip test listing these optional fields, add one assertion that the new field round-trips and that an old file without it decodes as nil.

- [ ] **Step 7: Locale keys used by MightyCore**

`L("workspace.git.error.gitMissing")` and `L("workspace.name.error.exists")` are used above. Add every key of this feature to `locales/{ko,en,zh,ja}.json` now, so Task 2 and Task 3 share them, and run the sync described in `scripts/check-locales.js` (it copies to the Mac resources and `mobile/src/locales`).

| key | ko | en |
|---|---|---|
| `workspace.add.existing` | 기존 폴더 열기… | Open Existing Folder… |
| `workspace.add.newFolder` | 새 폴더 만들기… | New Folder… |
| `workspace.add.fromGit` | Git으로 생성… | Create from Git… |
| `workspace.add.menu` | 워크스페이스 추가 | Add Workspace |
| `workspace.create.cancel` | 취소 | Cancel |
| `workspace.create.location` | 위치 | Location |
| `workspace.create.chooseLocation` | 위치 고르기 | Choose Location |
| `workspace.create.name` | 폴더 이름 | Folder Name |
| `workspace.create.path` | 만들어질 경로: {path} | Will be created at: {path} |
| `workspace.newFolder.title` | 새 폴더 만들기 | New Folder |
| `workspace.newFolder.subtitle` | 새 폴더를 만들어 워크스페이스로 엽니다 | Make a new folder and open it as a workspace |
| `workspace.newFolder.gitInit` | Git 저장소로 시작 (git init) | Start as a Git repository (git init) |
| `workspace.newFolder.create` | 만들기 | Create |
| `workspace.newFolder.gitInitFailed` | 폴더는 만들었지만 git init에 실패했습니다: {message} | The folder was made, but git init failed: {message} |
| `workspace.git.title` | Git으로 생성 | Create from Git |
| `workspace.git.subtitle` | Git 저장소를 내려받아 새 워크스페이스를 만듭니다 | Download a Git repository into a new workspace |
| `workspace.git.url` | URL | URL |
| `workspace.git.urlPlaceholder` | Git 저장소 주소 | Git repository URL |
| `workspace.git.test` | 연결 확인 | Test Connection |
| `workspace.git.checking` | 확인 중… | Checking… |
| `workspace.git.reachable` | 접근할 수 있습니다 | Reachable |
| `workspace.git.create` | 생성 | Create |
| `workspace.git.progress.starting` | 시작하는 중… | Starting… |
| `workspace.git.progress.receiving` | 받는 중 {percent}% | Receiving {percent}% |
| `workspace.git.progress.resolving` | 정리하는 중 {percent}% | Resolving {percent}% |
| `workspace.git.progress.checkout` | 파일 쓰는 중 {percent}% | Writing files {percent}% |
| `workspace.git.openSetup` | 필요한 도구 열기 | Open Required Tools |
| `workspace.git.error.gitMissing` | Git이 설치되어 있지 않습니다 | Git is not installed |
| `workspace.git.error.invalidURL` | 올바른 Git 주소가 아닙니다 | This is not a Git address |
| `workspace.git.error.auth` | 이 컴퓨터의 자격 증명으로 접근할 수 없습니다 | This computer's credentials cannot access it |
| `workspace.git.error.notFound` | 저장소를 찾을 수 없거나 접근 권한이 없습니다 | Repository not found, or no access |
| `workspace.git.error.network` | 서버에 연결할 수 없습니다 | Cannot reach the server |
| `workspace.git.error.other` | Git 오류: {message} | Git error: {message} |
| `workspace.name.error.empty` | 폴더 이름을 입력하세요 | Enter a folder name |
| `workspace.name.error.badCharacter` | 폴더 이름에 쓸 수 없는 글자가 있습니다 | The name has a character folders cannot use |
| `workspace.name.error.reserved` | Windows에서 쓸 수 없는 이름입니다 | Windows reserves this name |
| `workspace.name.error.trailingDotOrSpace` | 이름이 점이나 공백으로 끝날 수 없습니다 | The name cannot end with a dot or a space |
| `workspace.name.error.dots` | . 이나 .. 은 쓸 수 없습니다 | . and .. cannot be used |
| `workspace.name.error.tooLong` | 이름이 너무 깁니다 | The name is too long |
| `workspace.name.error.exists` | 같은 이름의 파일이나 폴더가 이미 있습니다 | A file or folder with this name already exists |
| `workspace.location.error.missing` | 위치 폴더가 없습니다 | The location folder does not exist |

Also change the value of `menu.openProject` to "기존 폴더 열기…" / "Open Existing Folder…" (zh and ja too); its key stays, so ⌘O, the help's `{{ui:menu.openProject}}` and Windows `AddPaneMenu.OpenProjectKey` follow. Write natural zh and ja translations for every row, keeping `{path}`, `{message}` and `{percent}`.

- [ ] **Step 8: Text checks**

Run `node scripts/check-locales.js --check` and `node scripts/check-design-tokens.js --check`. Expected: both pass. If the Korean-literal budget (`scripts/korean-literal-budget.json`) complains, the new code has hard-coded Korean: move it to a key.

- [ ] **Step 9: Commit**

```bash
git add native/contracts/fixtures/workspace-create.json native/macos/Sources/MightyCore/WorkspaceCreate.swift native/macos/Sources/MightyCore/WorkspaceCreateGit.swift native/macos/Tests/MightyCoreTests/WorkspaceCreateTests.swift native/macos/Sources/MightyCore/Models.swift native/macos/Sources/MightyCore/StateRepository.swift locales mobile/src/locales native/macos/Sources/MightyCore/Resources/Locales
git commit -m "(mac) MightyCore can make a workspace folder (with git init) and create one from a Git address checked with this computer's credentials, following the shared workspace-create rules

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Mac app — the two sheets and every entry point

**Files:**
- Create: `native/macos/Sources/MightyClaude/WorkspaceCreateViews.swift`
- Create: `native/macos/Sources/MightyClaude/AppStore+WorkspaceCreate.swift`
- Modify: `native/macos/Sources/MightyClaude/AppStore.swift` (`hasBlockingModal` at about line 438; split `openWorkspace()` at about lines 600–618 so the approve-and-add part is reusable)
- Modify: `native/macos/Sources/MightyClaude/WorkspaceView.swift` (sheets near `.sheet(item: $store.renameTarget)` at about line 41; the sidebar + button at about lines 127–133; the empty state at about lines 161–165; the welcome screen at about line 583; `WorkspaceAddMenuItems` at about lines 840–870)
- Modify: `native/macos/Sources/MightyClaude/MightyClaudeApp.swift` (File menu, lines 63–66)

**Interfaces:**
- Consumes (Task 1):
  - `WorkspaceCreate.repoName / checkName / target / defaultLocation / progress`
  - `GitTool.makeFolder / probe`
  - `GitCloneJob`
  - `AppSnapshot.lastCreateLocation`
- Produces (`AppStore`):
  - `@Published var workspaceCreate: WorkspaceCreateSheet?`, where `enum WorkspaceCreateSheet: String, Identifiable { case newFolder, fromGit; var id: String { rawValue } }`
  - `func showWorkspaceCreate(_ kind: WorkspaceCreateSheet)`
  - `func addLocalWorkspace(path: String) throws`: the old body of `openWorkspace()` after the panel. It approves the path, adds, selects and opens a Claude pane. `openWorkspace()` calls it.
  - `var createLocationDefault: String`
  - `func chooseCreateLocation(current: String) -> String?`: an `NSOpenPanel` for directories, `canCreateDirectories = true`.
  - `func rememberCreateLocation(_ path: String)`

- [ ] **Step 1: Refactor `openWorkspace()`**

```swift
func openWorkspace() {
    guard !hasModal else { return }
    let panel = NSOpenPanel()
    // … keep the existing panel set-up unchanged …
    guard panel.runModal() == .OK, let url = panel.url else { return }
    do { try addLocalWorkspace(path: url.path) } catch { /* keep today's error handling exactly as it is */ }
}

func addLocalWorkspace(path: String) throws {
    let url = URL(fileURLWithPath: path)
    let workspace = try repository.approveWorkspace(Workspace(name: url.lastPathComponent, path: url.path))
    addWorkspace(workspace)
}
```

Keep whatever `openWorkspace()` does today around `approveWorkspace` (its error banner and its `addWorkspace` call). Only move it into the shared function; the behaviour of ⌘O stays identical.

- [ ] **Step 2: Store state and helpers**

`AppStore+WorkspaceCreate.swift`:

```swift
import AppKit
import MightyCore

enum WorkspaceCreateSheet: String, Identifiable { case newFolder, fromGit; var id: String { rawValue } }

extension AppStore {
    func showWorkspaceCreate(_ kind: WorkspaceCreateSheet) {
        guard isLoaded, !hasModal else { return }
        workspaceCreate = kind
    }

    var createLocationDefault: String {
        WorkspaceCreate.defaultLocation(last: snapshot.lastCreateLocation, activeWorkspacePath: activeWorkspace?.remote == nil ? activeWorkspace?.path : nil,
                                        home: FileManager.default.homeDirectoryForCurrentUser.path)
    }

    func chooseCreateLocation(current: String) -> String? {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true; panel.canChooseFiles = false; panel.allowsMultipleSelection = false; panel.canCreateDirectories = true
        panel.directoryURL = URL(fileURLWithPath: current)
        panel.prompt = L("workspace.create.chooseLocation")
        return panel.runModal() == .OK ? panel.url?.path : nil
    }

    func rememberCreateLocation(_ path: String) { snapshot.lastCreateLocation = path }
}
```

In `AppStore.swift`, add `@Published var workspaceCreate: WorkspaceCreateSheet?` next to `renameTarget`, and `|| workspaceCreate != nil` to `hasBlockingModal`.

- [ ] **Step 3: The sheets**

`WorkspaceCreateViews.swift`. It holds three views:
- `WorkspaceAddChoices`, the three buttons or menu items:

```swift
struct WorkspaceAddChoices: View {
    @EnvironmentObject private var store: AppStore
    var body: some View {
        Button(L("workspace.add.existing")) { store.openWorkspace() }.accessibilityIdentifier("workspace-add-existing")
        Button(L("workspace.add.newFolder")) { store.showWorkspaceCreate(.newFolder) }.accessibilityIdentifier("workspace-add-new-folder")
        Button(L("workspace.add.fromGit")) { store.showWorkspaceCreate(.fromGit) }.accessibilityIdentifier("workspace-add-from-git")
    }
}
```

- `NewFolderSheet`:
  - Shape: a `VStack` like `RenameSheet` (`.padding(DesignMetrics.Inset.sheet)`, width 460). Title `workspace.newFolder.title`, subtitle in `.secondary`.
  - Name row: a `TextField` with the label `workspace.create.name`, focused on appear.
  - Location row: the path text (`.lineLimit(1).truncationMode(.middle)`) plus a `folder` icon button that calls `store.chooseCreateLocation`.
  - `Toggle(L("workspace.newFolder.gitInit"))`.
  - Preview: `L("workspace.create.path", ["path": target])` in caption.
  - Under the fields, the first error that applies, in `Palette.errText`:
    - `checkName(name)` → `workspace.name.error.<raw>`
    - the location is missing or not a folder → `workspace.location.error.missing`
    - the target exists → `workspace.name.error.exists`
  - `HStack { Spacer(); Cancel (.cancelAction); 만들기 (.defaultAction, disabled unless valid) }`.
  - On 만들기:
    1. Set `busy = true`.
    2. `let result = await GitTool.makeFolder(at: target, gitInit: gitInit)`.
    3. `.failed(m)` → show `m`, stop.
    4. Otherwise `store.rememberCreateLocation(location)`, then `try store.addLocalWorkspace(path: target)`, and close the sheet (`store.workspaceCreate = nil`).
    5. For `.madeButGitInitFailed(m)`, also show the existing notice/banner with `L("workspace.newFolder.gitInitFailed", ["message": m])`. Use the same toast or banner `AppStore` uses for other non-fatal notices; search for `showNotice` or `banner`.
- `GitCreateSheet`:
  - Fields: URL (placeholder `workspace.git.urlPlaceholder`, focused), a `Button(L("workspace.git.test"))` styled as a link (`.buttonStyle(.link)`), location (as above), and a name `TextField` with `nameEdited` tracking. `onChange(of: url)` sets `name = WorkspaceCreate.repoName(url) ?? ""` unless `nameEdited`. Editing the name field sets `nameEdited = true`.
  - Probe state: `enum Probe { case idle, checking, reachable, failed(WorkspaceCreate.GitErrorKind, String) }`.
    - `.task(id: url)`: `try? await Task.sleep(for: .milliseconds(600))`, then `guard !Task.isCancelled`, then `probe = .checking`, then `let r = await GitTool.probe(url)`, then `guard !Task.isCancelled`, then set the result.
    - The test link restarts the same probe immediately by bumping a `probeToken` that is part of the task id.
  - Probe row:
    - `.checking`: `ProgressView().controlSize(.small)` + `workspace.git.checking`.
    - `.reachable`: `✓` + `workspace.git.reachable` in `Palette.doneText` (or the done tone the app uses).
    - `.failed(kind, message)`: `✕` + `workspace.git.error.<kind>` (`.other` passes `["message": message]`) in `Palette.errText`. For `.gitMissing`, a button `workspace.git.openSetup` closes the sheet and calls `store.openSetup()`.
  - Path preview and name/location errors: as in `NewFolderSheet`.
  - 생성 is enabled when `probe == .reachable`, `checkName(name) == nil`, the location exists, the target does not exist, and `job == nil`.
  - On 생성:
    1. Create a `GitCloneJob(url:target:progress:)`. Its progress closure hops to the main actor: `Task { @MainActor in self.progress = p }`.
    2. Lock the fields. Show `ProgressView(value: progress.fraction)` and the phase text `workspace.git.progress.<phase>` with `["percent": "\(progress.percent)"]`.
    3. Cancel during the run calls `job.cancel()`. Cancel when idle closes the sheet.
    4. `await job.run()`:
       - `.done` → `rememberCreateLocation`, `addLocalWorkspace(path: target)`, close.
       - `.cancelled` → unlock and stay.
       - `.failed(kind, m)` → show the error row as above, unlock.
  - On disappear, if a job runs, call `job.cancel()`.

Present both sheets in `WorkspaceView` next to the rename sheet:

```swift
.sheet(item: $store.workspaceCreate) { kind in
    switch kind {
    case .newFolder: NewFolderSheet().environmentObject(store)
    case .fromGit: GitCreateSheet().environmentObject(store)
    }
}
```

- [ ] **Step 4: Entry points**

- **`MightyClaudeApp.swift`**, File menu: keep the ⌘O item, now labelled by the changed `menu.openProject`. Add after it:

```swift
Button(L("workspace.add.newFolder")) { store.showWorkspaceCreate(.newFolder) }
    .disabled(!store.isLoaded || store.hasModal)
Button(L("workspace.add.fromGit")) { store.showWorkspaceCreate(.fromGit) }
    .disabled(!store.isLoaded || store.hasModal)
```

- **Sidebar + button** (`WorkspaceView.swift` about lines 127–133): turn the `Button` into a `Menu { WorkspaceAddChoices() } label: { Image(systemName: "folder.badge.plus") }`.
  - Use `.menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()`.
  - Keep the help text and the accessibility id `sidebar-open-folder` on the `Menu`.
- **Sidebar empty state** (about lines 161–165): replace the single button with a `VStack(spacing: DesignMetrics.Spacing.xs) { WorkspaceAddChoices() }`.
- **Welcome screen** (about line 583): keep the prominent open button first. Add two bordered buttons for new folder and from Git beside or under it, following the row the screen already uses for "다른 컴퓨터 연결…".
- **`WorkspaceAddMenuItems`** (the 창 추가 menu, about lines 866–869): after the existing `menu.openProject` item, add the two new items. Use ids `workspace-new-folder-<id>` and `workspace-from-git-<id>`, following the `workspace-open-folder-<id>` pattern.

- [ ] **Step 5: Text checks**

Run `node scripts/check-locales.js --check` and `node scripts/check-design-tokens.js --check`. A raw-spacing ratchet failure means a literal `.padding(n)` or `spacing: n`; use `DesignMetrics.Spacing` / `Inset`.

- [ ] **Step 6: Commit**

```bash
git add native/macos/Sources/MightyClaude
git commit -m "(mac) a workspace can be added three ways everywhere the open-folder choice was: open an existing folder, make a new folder (optionally a Git repository), or create it from a Git address that this computer's credentials reach, with progress and cancel

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Windows Core — rules, runners, menu, snapshot, checks

**Files:**
- Create: `native/windows/MightyClaude.Core/WorkspaceCreate.cs`
- Create: `native/windows/MightyClaude.Core.Tests/WorkspaceCreateVerification.cs`
- Modify: `native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj` (embed the fixture as `MightyClaude.Core.Tests.WorkspaceCreate.json`, like `DelegationModes.json` at line 18)
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs` (register the checks next to the other fixture checks)
- Modify: `native/windows/MightyClaude.Core/AddPaneMenu.cs`
- Modify: `native/windows/MightyClaude.Core/Models.cs` (`AppSnapshot.LastCreateLocation`, next to `BackgroundWorkOpen` at line 332)
- Modify: `native/windows/MightyClaude.Core/DesktopService.cs` (remember the location; the add path is `AddWorkspaceAsync`)

**Interfaces:**
- Produces (C#, namespace `MightyClaude.Core`):
  - `public static class WorkspaceCreate`:
    - `RepoName(string url) → string?`
    - `LooksLikeGitUrl(string url) → bool`
    - `CheckName(string name, bool windows = true) → string?`, returning the problem's camelCase name as in the fixture
    - `ErrorKind(string stderr) → string`, returning `auth`, `notFound`, `network`, `invalidURL` or `other`
    - `Progress(string line) → CloneProgress?`, where `public sealed record CloneProgress(string Phase, int Percent, double Fraction)`
    - `Target(string location, string name) → string`
    - `DefaultLocation(string? last, string? activeWorkspacePath, string home) → string`
  - `public static class GitTool`:
    - `Executable` (= `DelegationGit.Executable`)
    - `MakeFolderAsync(string path, bool gitInit) → Task<MakeFolderResult>`, where `public sealed record MakeFolderResult(string Outcome, string? Message)` and Outcome is `made`, `madeButGitInitFailed` or `failed`
    - `ProbeAsync(string url, CancellationToken token = default) → Task<ProbeResult>`, where `public sealed record ProbeResult(bool Reachable, string? Kind, string? Message)`
  - `public sealed class GitCloneJob`:
    - `GitCloneJob(string url, string target, Action<CloneProgress> progress)`
    - `RunAsync() → Task<CloneResult>`, where `public sealed record CloneResult(string Outcome, string? Kind, string? Message)` and Outcome is `done`, `cancelled` or `failed`
    - `Cancel()`
  - `AddPaneMenu.NewFolder = "newFolder"`, `AddPaneMenu.FromGit = "fromGit"`; `NewFolderKey = "workspace.add.newFolder"`, `FromGitKey = "workspace.add.fromGit"`
  - `Entries()` ends `..., Separator, OpenProject, NewFolder, FromGit`
  - `AppSnapshot.LastCreateLocation { get; init; }` (string?)
  - `DesktopService.RememberCreateLocationAsync(string path) → Task`

- [ ] **Step 1: Write the failing Core checks**

`WorkspaceCreateVerification.cs` loads the embedded fixture as `DelegationCrossAgentVerification` does (lines 14–20). It has these checks:
- `RepoNamesFollowTheFixture`: every `repoNames` item has `WorkspaceCreate.RepoName(url) == name`, with JSON null meaning null.
- `GitUrlsFollowTheFixture`: every `valid` and `invalid` URL.
- `FolderNamesFollowTheFixture`: `CheckName(name, windows: true) == windows` and `CheckName(name, windows: false) == mac`.
- `GitErrorsFollowTheFixture`: every `errors` item.
- `CloneProgressFollowsTheFixture`: phase and percent equal; fraction within 0.001; null rows give null.
- `MakeFolderAndCloneWorkOnALocalBareRepository`, when `GitTool.Executable` is not null:
  1. Make a temp root, a work repo (`git init`, a commit with `-c user.name=t -c user.email=t@example.invalid`) and `git clone --bare` into `remote.git`.
  2. `ProbeAsync(bare)` is reachable, and `ProbeAsync(root/none.git)` has Kind `notFound`.
  3. `GitCloneJob(bare, root/copy).RunAsync()` is `done` and `README.md` exists.
  4. A job cancelled before it runs gives `cancelled` and leaves no folder.
  5. A second clone into `copy` fails and leaves `README.md`.
  6. `MakeFolderAsync(root/repo, true)` makes `.git`.
  7. Delete the root at the end.

Use the same failure helper the other verifications use (for example `Verify.That(...)` or `throw new InvalidOperationException(...)`, whichever `DelegationCrossAgentVerification` uses).

Register them in `Verification.cs`:

```csharp
await Test("workspace create: repo names, folder names, git errors and clone progress follow native/contracts/fixtures/workspace-create.json", WorkspaceCreateVerification.TheSharedRulesHold);
await Test("workspace create: a new folder, a probe and a clone against a local bare repository", WorkspaceCreateVerification.MakeFolderAndCloneWorkOnALocalBareRepository);
```

`TheSharedRulesHold` calls the five rule checks.

Embed the fixture in the `.csproj`:

```xml
<EmbeddedResource Include="../../contracts/fixtures/workspace-create.json" LogicalName="MightyClaude.Core.Tests.WorkspaceCreate.json" />
```

- [ ] **Step 2: (Run only if the user asks.)** `bash ~/Work/.mc-tools/windows-core-checks/core-checks.sh`. Expected now: compile FAIL (types missing).

- [ ] **Step 3: Write `WorkspaceCreate.cs`**

Port Task 1's Swift rules line for line. The same order of checks and the same tables give the same fixture answers.
- `RepoName`: as Swift. Strip `?#`, then trailing `/` and `\`, then `.git` (ignoring case). Split the path part on `/` and `\`, plus `:` when there is no scheme, and take the last non-empty piece.
- `LooksLikeGitUrl`:
  - No whitespace inside.
  - A scheme from `https:// http:// ssh:// git:// file://` (ignoring case) with more after it.
  - Or the scp regex `^[^/\s@]+@[^/\s:]+:\S+$`.
  - Or it starts with `/` or `\\`.
  - Or it matches `^[A-Za-z]:[\\/]`.
- `CheckName`: trim first, then:
  - empty → `empty`
  - `.` or `..` → `dots`
  - more than 255 UTF-8 bytes → `tooLong`
  - any of `/\:` or a control character (`char.IsControl`) → `badCharacter`
  - then, when `windows`:
    - any of `<>"|?*` → `badCharacter`
    - the base name before the first `.`, uppercased, in CON PRN AUX NUL COM1–9 LPT1–9 → `reserved`
    - ends with `.` or a space → `trailingDotOrSpace`
- `ErrorKind`: the same four substring lists, matched in order with `OrdinalIgnoreCase`.
- `Progress`: same prefixes and fractions. Use `Math.Round(x, 3)`; the percent comes from regex `(\d{1,3})%`, capped at 100.
- `DefaultLocation`: `last` if it is an existing directory; else the parent of `activeWorkspacePath` if it is an existing directory; else `home`.
- `Target`: `Path.Combine(location, name.Trim())`.

`GitTool`:
- `Executable => DelegationGit.Executable`.
- `Environment(ProcessStartInfo info)`:
  - Remove `GIT_DIR`, `GIT_WORK_TREE` and `GIT_INDEX_FILE`.
  - Set `GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`, `LC_ALL=C`.
  - Set `GIT_SSH_COMMAND=ssh -o BatchMode=yes`, unless `GIT_SSH_COMMAND` is already set or `git config --global --get core.sshCommand` prints a value. Cache that lookup for the process lifetime.
- `MakeFolderAsync`:
  - If the path exists → `failed` (`workspace.name.error.exists`).
  - `Directory.CreateDirectory(path)`; an exception → `failed` with its message.
  - If `gitInit`: run `git init -q` with `ChildProcess.StartInfo(Executable, ["init","-q"], path)` + `Environment(info)`, waiting at most 20 s.
  - A non-zero exit → `madeButGitInitFailed` with the last stderr line.
- `ProbeAsync`:
  - `!LooksLikeGitUrl` → `(false, "invalidURL", null)`.
  - No executable → `(false, "gitMissing", null)`.
  - Run `ls-remote --heads -- <url>` in `Path.GetTempPath()` with a 20 s deadline, killing the process on timeout. A timeout gives `network`.
  - Exit 0 → reachable. Otherwise `ErrorKind(stderr)` and the last stderr line.
  - Read stdout and stderr concurrently (as `DelegationGit.RunDetailed` does), so a large ref list cannot block.

`GitCloneJob`:
- If the target exists → `failed`, `other`, `L("workspace.name.error.exists")`. Use `Locale.Get("workspace.name.error.exists")`, whatever Core uses for `L`.
- Start `git clone --progress -- <url> <target>` in the target's parent folder with `GitTool.Environment`.
- Read `Error` (a `StreamReader`) char by char into a buffer. Split lines on `\r` and `\n`, and call `progress(p)` for each line where `Progress(line)` is not null. Keep the last 64 KiB of text.
- `Cancel()`: set a flag and `Kill()` the process. `ChildProcess.Kill` ends the job object, so git's children (`git-remote-https`, `ssh`) end too.
- After the exit:
  - Exit 0 and not cancelled → `done`.
  - Otherwise delete the target with a few retries: `Directory.Delete(target, recursive: true)`, retried 5 times 200 ms apart, because Windows may still hold a handle. Clear the read-only attribute on files under `.git/objects` first (git marks pack files read-only).
  - Then return `cancelled`, or `failed` with `ErrorKind(text)` and the last line.

- [ ] **Step 4: `AddPaneMenu`, snapshot, service**

```csharp
public const string NewFolder = "newFolder";
public const string FromGit = "fromGit";
public const string NewFolderKey = "workspace.add.newFolder";
public const string FromGitKey = "workspace.add.fromGit";

public static IReadOnlyList<string> Entries() =>
    [.. Wire.Providers.Select(p => AgentPrefix + p), Separator, Shell, Separator, Browser, Files, Separator, OpenProject, NewFolder, FromGit];
```

Update the class comment: the bottom group is now open / new folder / from Git.

Search Core.Tests for checks that assert `Entries()` exactly (grep `AddPaneMenu.Entries`) and add the two entries there.

- In `Models.cs`, add `public string? LastCreateLocation { get; init; }` after `BackgroundWorkOpen`.
- In `DesktopService`, add:

```csharp
public Task RememberCreateLocationAsync(string path) => UpdateAsync(snapshot => snapshot with { LastCreateLocation = path });
```

Use the exact `UpdateAsync` signature `DesktopService` has. Check how `BackgroundWorkOpen` is saved and copy that pattern.

- [ ] **Step 5: Text checks, then commit**

`node scripts/check-locales.js --check`. Then:

```bash
git add native/windows/MightyClaude.Core native/windows/MightyClaude.Core.Tests
git commit -m "(windows) the Core can make a workspace folder (with git init), check a Git address with this computer's credentials and create a workspace from it with progress and cancel, following the shared workspace-create rules; the add menu gains 새 폴더 만들기 and Git으로 생성

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Windows UI — web dialogs, bridge and WinUI fallback

**Files:**
- Create: `native/windows/web/src/dialogs/WorkspaceCreate.tsx`
- Create: `native/windows/web/src/dialogs/workspaceCreate.ts` (pure helpers) and `workspaceCreate.test.ts`
- Modify: `native/windows/web/src/dialogs/Dialogs.tsx` (render the two new dialog types)
- Modify: `native/windows/web/src/shell/types.ts` (the `DialogRequest` union at line 181; `ShellPush` at line 211)
- Modify: `native/windows/web/src/shell/Sidebar.tsx` (line 187 icon button → a three-item menu; lines 204–207 empty state → three buttons)
- Modify: `native/windows/web/src/dashboard/Dashboard.tsx` (line 116 → three buttons)
- Modify: `native/windows/web/src/shell/addMenu.tsx` (the entries come from the host's `addMenu`, so the two new entries appear on their own once `WebAddMenu` labels them)
- Modify: `native/windows/MightyClaude.WinUI/MainWindow.WebShell.Shell.cs` (bridge methods next to `renameCheck` at line 65; `addPaneEntry` at lines 94–99; `WebAddMenu` at lines 318–334)
- Create: `native/windows/MightyClaude.WinUI/MainWindow.WorkspaceCreate.cs` (shared flows and WinUI fallback dialogs)
- Modify: `native/windows/MightyClaude.WinUI/MainWindow.Navigation.cs` (WinUI add-pane menu, lines 272–279), `MainWindow.Sidebar.cs:83`, `MainWindow.EmptyStates.cs:52`

**Interfaces:**
- Consumes (Task 3):
  - `WorkspaceCreate.*`, `GitTool.*`, `GitCloneJob`
  - `AddPaneMenu.NewFolder` / `FromGit` / `NewFolderKey` / `FromGitKey`
  - `DesktopService.RememberCreateLocationAsync`
  - `snapshot.LastCreateLocation`
  - the existing `OpenWorkspaceFolder(path)` (`MainWindow.cs:207-218`), which adds the workspace and a Claude pane
- Produces:
  - Dialog requests:

    ```ts
    | { type: 'newFolder'; title: string; subtitle: string; labels: CreateLabels; location: string }
    | { type: 'gitCreate'; title: string; subtitle: string; labels: CreateLabels; location: string }
    ```

    `CreateLabels` holds every visible string, resolved by the host from the locale keys of Task 1 (name, location, choose, path with `{path}`, gitInit, create, cancel, url, urlPlaceholder, test, checking, reachable, openSetup, the progress strings with `{percent}`, the error strings with `{message}`).
  - Bridge calls:
    - `createCheck {location, name} → { problem: string|null, target: string, exists: boolean, locationMissing: boolean }`
    - `pickLocation {current} → string|null`
    - `makeFolder {location, name, gitInit} → { outcome, message }`
    - `gitProbe {url} → { reachable: boolean, kind: string|null, message: string|null }`
    - `repoName {url} → string|null`
    - `gitCreate {dialogId, url, location, name} → { outcome, kind, message }`
    - `gitCreateCancel {dialogId}`
    - `openSetup`, if it does not exist yet
  - Push: `{ kind: 'gitProgress'; id: number; phase: string; percent: number; fraction: number }`
  - Dialog answers: `{ choice: 'cancel' }` or `{ choice: 'done', path: string, location: string, notice?: string }`

- [ ] **Step 1: Pure web helpers + vitest**

`workspaceCreate.ts`:

```ts
export interface ProbeState { state: 'idle' | 'checking' | 'reachable' | 'failed'; kind?: string; message?: string }

/** The name field follows the URL until the user types in it. */
export function nextName(current: string, edited: boolean, suggested: string | null): string {
  return edited ? current : suggested ?? '';
}

/** 만들기 (new folder) is enabled when the host's check passes and nothing runs. */
export function canMakeFolder(check: { problem: string | null; exists: boolean; locationMissing: boolean } | null, busy: boolean): boolean {
  return !!check && check.problem === null && !check.exists && !check.locationMissing && !busy;
}

/** 생성 (from Git) also needs a reachable address. */
export function canCreateFromGit(check: { problem: string | null; exists: boolean; locationMissing: boolean } | null, probe: ProbeState, busy: boolean): boolean {
  return canMakeFolder(check, busy) && probe.state === 'reachable';
}

export function fill(template: string, values: Record<string, string | number>): string {
  return template.replace(/\{(\w+)\}/g, (whole, key: string) => (key in values ? String(values[key]) : whole));
}
```

`workspaceCreate.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import { canCreateFromGit, canMakeFolder, fill, nextName } from './workspaceCreate';

const ok = { problem: null, exists: false, locationMissing: false };

describe('workspace create helpers', () => {
  it('follows the URL until the name is edited', () => {
    expect(nextName('', false, 'MightyClaude')).toBe('MightyClaude');
    expect(nextName('mine', true, 'MightyClaude')).toBe('mine');
    expect(nextName('old', false, null)).toBe('');
  });
  it('enables 만들기 only for a valid, free target while idle', () => {
    expect(canMakeFolder(ok, false)).toBe(true);
    expect(canMakeFolder({ ...ok, problem: 'empty' }, false)).toBe(false);
    expect(canMakeFolder({ ...ok, exists: true }, false)).toBe(false);
    expect(canMakeFolder({ ...ok, locationMissing: true }, false)).toBe(false);
    expect(canMakeFolder(ok, true)).toBe(false);
    expect(canMakeFolder(null, false)).toBe(false);
  });
  it('enables 생성 only after a reachable probe', () => {
    expect(canCreateFromGit(ok, { state: 'reachable' }, false)).toBe(true);
    expect(canCreateFromGit(ok, { state: 'checking' }, false)).toBe(false);
    expect(canCreateFromGit(ok, { state: 'failed', kind: 'auth' }, false)).toBe(false);
  });
  it('fills placeholders', () => {
    expect(fill('받는 중 {percent}%', { percent: 42 })).toBe('받는 중 42%');
    expect(fill('{missing}', {})).toBe('{missing}');
  });
});
```

- [ ] **Step 2: The web dialogs**

`WorkspaceCreate.tsx` exports `NewFolder` and `GitCreate`. Both use `Sheet` from `Dialogs.tsx`; export `Sheet` from there if it is not exported yet. Follow the `Rename` component's pattern (lines 41–63):
- `useEffect` on `[location, name]` calls `bridge.call('createCheck', …)` and keeps the result.
- The 📁 button calls `bridge.call<string | null>('pickLocation', { current: location })`.
- Errors show as `<div className="sheet-caption tone-text-err">`.
- The path preview uses `fill(labels.path, { path: check.target })`.
- Buttons: `plain-button` for cancel, `accent-button` for the primary action. Esc cancels, through `Sheet`.

`NewFolder`:
- A checkbox (`<label><input type="checkbox" …/> {labels.gitInit}</label>`).
- On 만들기: `const r = await bridge.call('makeFolder', …)`.
  - `failed` → show `r.message`.
  - Otherwise `answer({ choice: 'done', path: check.target, location, notice: r.outcome === 'madeButGitInitFailed' ? fill(labels.gitInitFailed, { message: r.message }) : undefined })`.

`GitCreate`:
- URL input with `data-autofocus`.
- `onChange` sets the URL. A `useEffect` on `[url]` calls `bridge.call('repoName', { url })`, then `setName((n) => nextName(n, edited, suggested))`.
- Probe: a `useEffect` on `[url, probeToken]`:
  - Set a 600 ms timer, then `setProbe({ state: 'checking' })`, then `bridge.call('gitProbe', { url })`.
  - Ignore stale answers with a `live` flag, as `Rename` does.
  - The 연결 확인 link (`<button className="link-button">`) bumps `probeToken`.
- Probe row: ✓ reachable / ✕ `labels.errors[kind]`. For `gitMissing`, add a button that calls `bridge.call('openSetup')` and then `answer({ choice: 'cancel' })`.
- On 생성:
  1. `setBusy(true)`.
  2. Subscribe to `gitProgress` pushes for this dialog `id`. Use the store's push routing: add a `gitProgress` case where `ShellPush` is handled, keeping the latest progress per dialog id in the store, read here with `useStoreState`.
  3. `const r = await bridge.call('gitCreate', { dialogId: id, url, location, name })`.
  4. `done` → `answer({ choice: 'done', path: check.target, location })`.
  5. `cancelled` → `setBusy(false)`.
  6. `failed` → show the error row, `setBusy(false)`.
- Cancel while busy calls `bridge.call('gitCreateCancel', { dialogId: id })`; when idle it calls `answer({ choice: 'cancel' })`.
- While busy: `<progress max={1} value={fraction} />` + `fill(labels.progress[phase], { percent })`, with the fields disabled.

In `Dialogs.tsx`, render `dialog.type === 'newFolder'` → `<NewFolder …/>` and `'gitCreate'` → `<GitCreate …/>`, where the other types are switched.

Add `'newFolder' | 'gitCreate'` to `DialogRequest` in `types.ts`, and `| { kind: 'gitProgress'; id: number; phase: string; percent: number; fraction: number }` to `ShellPush`.

- [ ] **Step 3: Entry points in the page**

- `Sidebar.tsx:187`: the icon button opens a small menu with the three items (use the app's existing menu component, the one `addMenu.tsx` uses). The items call `bridge.call('pickFolder')`, `bridge.call('workspaceCreate', { kind: 'newFolder' })` and `bridge.call('workspaceCreate', { kind: 'fromGit' })`.
- `Sidebar.tsx:204-207` and `Dashboard.tsx:116`: three buttons with the same calls.
- `addMenu.tsx`: nothing, if it renders whatever the host's `addMenu` lists. Check that it handles unknown entry ids by calling `addPaneEntry` with the id.

- [ ] **Step 4: Host side (C#)**

`MainWindow.WorkspaceCreate.cs`:

```csharp
// The two new ways to add a workspace (docs/superpowers/specs/2026-10-26-workspace-create-design.md): a new folder and
// 'Git으로 생성'. The web page draws the dialogs (dialogs/WorkspaceCreate.tsx); the WinUI fallback has its own.
public sealed partial class MainWindow
{
    private readonly Dictionary<int, GitCloneJob> gitJobs = [];

    private string CreateLocationDefault() =>
        WorkspaceCreate.DefaultLocation(service.Snapshot.LastCreateLocation, ActiveLocalWorkspacePath(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private async Task ShowWorkspaceCreateAsync(string kind)
    {
        if (webShellActive) { await ShowWebWorkspaceCreateAsync(kind); return; }
        if (kind == AddPaneMenu.NewFolder) await ShowNewFolderDialogAsync(); else await ShowGitCreateDialogAsync();
    }

    private async Task FinishWorkspaceCreateAsync(string location, string path, string? notice)
    {
        await service.RememberCreateLocationAsync(location);
        OpenWorkspaceFolder(path);
        if (notice is not null) ShowNotice(notice);
    }
}
```

Adapt the names to the window's real members: `service.Snapshot`, how it knows the web shell is active, its notice or toast helper, and how it finds the active local workspace path.

- Register these bridge methods in `MainWindow.WebShell.Shell.cs` next to `renameCheck`:
  - **`createCheck`:** read `location` and `name`, then return:
    - `problem = WorkspaceCreate.CheckName(name)`
    - `target = WorkspaceCreate.Target(location, name)`
    - `exists = Path.Exists(target)`
    - `locationMissing = !Directory.Exists(location)`
  - **`pickLocation`:** a WinRT `FolderPicker` (same set-up as `PickFolder()` in `MainWindow.cs:207`) whose suggested start is the current location; returns the path or null.
  - **`makeFolder`:** `GitTool.MakeFolderAsync(Target(location, name), gitInit)`.
  - **`repoName`:** `WorkspaceCreate.RepoName(url)`.
  - **`gitProbe`:** `GitTool.ProbeAsync(url)`.
  - **`gitCreate`:** run the clone and answer when it ends.
    1. Create `GitCloneJob(url, target, p => bridge.Push(new { kind = "gitProgress", id = dialogId, phase = p.Phase, percent = p.Percent, fraction = p.Fraction }))`.
    2. Store it in `gitJobs[dialogId]` and `await job.RunAsync()`.
    3. Remove it in a `finally`.
    4. Return `{ outcome, kind, message }`.
    5. The progress callback runs off the UI thread. `bridge.Push` must be called on the dispatcher queue if `WebBridge` requires it; check `WebBridge.Push` (line 61) and how other background pushes are queued.
  - **`gitCreateCancel`:** `gitJobs.TryGetValue(id, out var job) ? job.Cancel()`.
  - **`workspaceCreate`:** `ShowWorkspaceCreateAsync(kind)`.
  - **`openSetup`:** open the 필요한 도구 screen the way the window already does.
- `ShowWebWorkspaceCreateAsync(kind)`:
  1. Await `OpenWebDialog(new { type = kind == AddPaneMenu.NewFolder ? "newFolder" : "gitCreate", title, subtitle, labels, location = CreateLocationDefault() })`.
  2. Read the answer. `choice == "done"` → `FinishWorkspaceCreateAsync(location from the answer, path, notice)`.
- In `addPaneEntry` (lines 94–99), route `AddPaneMenu.NewFolder` and `AddPaneMenu.FromGit` to `ShowWorkspaceCreateAsync(entry)`.
- In `WebAddMenu` (lines 318–334), label them with `Locale.Get(AddPaneMenu.NewFolderKey)` and `FromGitKey`.
- **WinUI fallback:**
  - `ShowNewFolderDialogAsync` and `ShowGitCreateDialogAsync` are `ContentDialog`s built like `RenameDialog` (`MainWindow.Navigation.cs:389-415`).
  - The fields are `TextBox` / `CheckBox` / `ProgressBar`, a location `TextBlock` + a button calling the folder picker, and an error `TextBlock`.
  - `PrimaryButton` is 만들기/생성; `IsPrimaryButtonEnabled` follows the same rules.
  - The probe runs on `TextChanged` with a 600 ms `DispatcherQueueTimer`.
  - The primary click takes a deferral, runs the job and keeps the dialog open on failure (`args.Cancel = true`). The close button cancels a running job.
  - On success it calls `FinishWorkspaceCreateAsync`.
  - Resource keys are written once. `.Parent` is never used. Unloaded checks `IsLoaded`.
- Wire the three WinUI entry points (`MainWindow.Navigation.cs:272-279` add-pane menu, `MainWindow.Sidebar.cs:83` + button as a `MenuFlyout`, `MainWindow.EmptyStates.cs:52` welcome) to the three choices.

- [ ] **Step 5: Text checks**

`node scripts/check-locales.js --check`. A web Korean-literal ratchet failure means the copy must come from `labels`.

- [ ] **Step 6: Commit**

```bash
git add native/windows/web/src native/windows/MightyClaude.WinUI
git commit -m "(windows) a workspace can be added three ways in the web window and the WinUI fallback alike: open an existing folder, make a new folder (optionally a Git repository), or create it from a Git address checked with this computer's credentials, with progress and cancel

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Help (4 languages) and the generated i18n doc

**Files:**
- Modify: `docs/help/content/{ko,en,zh,ja}/getting-started.md` (the `## … {#open-workspace}` section, ko lines 77–84)
- Modify: `docs/help/content/{ko,en,zh,ja}/layout.md` (add-pane menu line ~47, ⌘O row ~85)
- Modify: `docs/help/content/{ko,en,zh,ja}/windows.md` (~32)
- Regenerate: `docs/i18n.md`

- [ ] **Step 1: Rewrite the open-workspace section**

The ko text. Use `{{ui:…}}` for every UI name so the help follows the locales; write en, zh and ja to match.

```markdown
## 워크스페이스 열기 {#open-workspace}
워크스페이스는 세 가지로 추가합니다. 사이드바 머리글의 폴더 버튼, 파일 메뉴, 창 추가 메뉴, 처음 화면 어디서나 고를 수 있습니다.

- **{{ui:workspace.add.existing}}** (⌘O): 이미 있는 폴더를 고릅니다.
- **{{ui:workspace.add.newFolder}}**: {{ui:workspace.create.name}}과 {{ui:workspace.create.location}}를 정해 새 폴더를 만듭니다. {{ui:workspace.newFolder.gitInit}}을 켜면 빈 Git 저장소로 시작합니다.
- **{{ui:workspace.add.fromGit}}**: Git 저장소 주소를 넣으면 이 컴퓨터의 자격 증명(키체인·credential helper·ssh-agent)으로 접근되는지 바로 확인합니다. ✓가 보이면 {{ui:workspace.git.create}}을 눌러 내려받습니다. 폴더 이름은 주소에서 채워지고 고칠 수 있습니다. 받는 동안 진행 상황이 보이고 {{ui:workspace.create.cancel}}로 멈추면 만들던 폴더는 지워집니다.

위치는 마지막에 쓴 곳을 기억합니다. 만든 폴더는 바로 워크스페이스로 추가되고 Claude 창이 열립니다.
```

Keep the existing right-click lines (rename, show in Finder, remove) after it.

- [ ] **Step 2: `layout.md` and `windows.md`**

- `layout.md`: the add-pane menu's bottom group is now the three items; the ⌘O row still names `menu.openProject`.
- `windows.md`: Ctrl+O, plus the two new items in the same places.

- [ ] **Step 3: Checks, regenerate, commit**

```bash
node scripts/help/build.mjs --check
node scripts/check-locales.js --check
```

If `check-locales.js` reports `docs/i18n.md` out of date, regenerate it the way the script says (its `--write` mode).

```bash
git add docs/help/content docs/i18n.md
git commit -m "(docs) the help (four languages) says how to add a workspace by opening a folder, making a new one or creating it from a Git address

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Verification when the user asks for builds and tests

- **Mac:** `DEVELOPER_DIR=/Library/Developer/CommandLineTools bash scripts/test-native-macos.sh --scratch-path /tmp/mc-wc --filter '\.(WorkspaceCreateTests|SnapshotPreferencesTests)/'`, and the app build.
- **Windows:** `bash ~/Work/.mc-tools/windows-core-checks/core-checks.sh` and `bash scripts/check-winui-compile.sh`.
- **Web:** `cd native/windows/web && npx vitest run src/dialogs/workspaceCreate.test.ts` and `npx tsc --noEmit`.
- **Always:** the four text checks.
