import Foundation
import Testing
@testable import MightyCore

struct WorkspaceFilesTests {
    private func makeRoot() throws -> (root: URL, outside: URL) {
        let temp = FileManager.default.temporaryDirectory
        let root = temp.appendingPathComponent("files-root-" + UUID().uuidString, isDirectory: true)
        let outside = temp.appendingPathComponent("files-outside-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
        return (root, outside)
    }

    private func write(_ text: String, _ path: String, in root: URL) throws {
        let url = root.appendingPathComponent(path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: url)
    }

    @Test func listingPutsFoldersFirstInNaturalCaseInsensitiveOrderAndShowsHiddenFiles() throws {
        let (root, outside) = try makeRoot()
        defer { try? FileManager.default.removeItem(at: root); try? FileManager.default.removeItem(at: outside) }
        for name in ["file10.txt", "file2.txt", "b.swift", "A.md", ".env", ".gitignore"] { try write("x", name, in: root) }
        for folder in ["src", "Docs", "node_modules", ".git"] { try FileManager.default.createDirectory(at: root.appendingPathComponent(folder), withIntermediateDirectories: true) }

        let listing = try WorkspaceFiles.list("", root: root)
        #expect(listing.entries.map(\.name) == [".git", "Docs", "node_modules", "src", ".env", ".gitignore", "A.md", "b.swift", "file2.txt", "file10.txt"])
        #expect(!listing.truncated)
        #expect(listing.entries.filter(\.isNoise).map(\.name) == [".git", "node_modules"])
        #expect(listing.entries.first { $0.name == "src" }?.isNoise == false)
        #expect(WorkspaceFileEntry(name: "build", relativePath: "build", isDirectory: false).isNoise == false)
        for name in [".git", "node_modules", ".build", "build", "dist", "DerivedData", ".next", "Pods", ".venv", "__pycache__"] {
            #expect(WorkspaceFiles.isNoiseFolder(name))
        }

        try write("x", "src/inner/deep.swift", in: root)
        let nested = try WorkspaceFiles.list("src", root: root)
        #expect(nested.entries.map(\.relativePath) == ["src/inner"])
        #expect(try WorkspaceFiles.list("src/inner", root: root).entries.map(\.relativePath) == ["src/inner/deep.swift"])
    }

    @Test func pureSortIsStable() {
        let entries = [
            WorkspaceFileEntry(name: "z10", relativePath: "z10", isDirectory: false),
            WorkspaceFileEntry(name: "Z2", relativePath: "Z2", isDirectory: false),
            WorkspaceFileEntry(name: "lib", relativePath: "lib", isDirectory: true),
            WorkspaceFileEntry(name: "App", relativePath: "App", isDirectory: true),
        ]
        #expect(WorkspaceFiles.sorted(entries).map(\.name) == ["App", "lib", "Z2", "z10"])
    }

    @Test func symlinksThatLeaveTheRootAreRefused() throws {
        let (root, outside) = try makeRoot()
        defer { try? FileManager.default.removeItem(at: root); try? FileManager.default.removeItem(at: outside) }
        try write("secret", "secret.txt", in: outside)
        try write("inside", "docs/readme.md", in: root)
        let fm = FileManager.default
        try fm.createSymbolicLink(at: root.appendingPathComponent("escape.txt"), withDestinationURL: outside.appendingPathComponent("secret.txt"))
        try fm.createSymbolicLink(at: root.appendingPathComponent("outdir"), withDestinationURL: outside)
        try fm.createSymbolicLink(at: root.appendingPathComponent("alias"), withDestinationURL: root.appendingPathComponent("docs"))
        try fm.createSymbolicLink(at: root.appendingPathComponent("dangling"), withDestinationURL: root.appendingPathComponent("missing"))

        let names = try WorkspaceFiles.list("", root: root).entries.map(\.name)
        #expect(names == ["alias", "docs"])
        let alias = try WorkspaceFiles.list("", root: root).entries.first { $0.name == "alias" }
        #expect(alias?.isDirectory == true && alias?.isSymlink == true)
        #expect(try WorkspaceFiles.list("alias", root: root).entries.map(\.relativePath) == ["alias/readme.md"])

        #expect(WorkspaceFiles.resolve("escape.txt", root: root) == nil)
        #expect(WorkspaceFiles.resolve("outdir", root: root) == nil)
        #expect(WorkspaceFiles.resolve("outdir/secret.txt", root: root) == nil)
        #expect(WorkspaceFiles.resolve("../" + outside.lastPathComponent + "/secret.txt", root: root) == nil)
        #expect(WorkspaceFiles.resolve("docs/../../" + outside.lastPathComponent, root: root) == nil)
        #expect(WorkspaceFiles.resolve(outside.path, root: root) == nil)
        #expect(WorkspaceFiles.resolve("dangling", root: root) == nil)
        #expect(WorkspaceFiles.resolve("docs/readme.md", root: root) != nil)
        #expect(WorkspaceFiles.resolve("", root: root) == WorkspaceFiles.realRoot(root))
        #expect(throws: WorkspaceFileError.outsideRoot) { try WorkspaceFiles.list("outdir", root: root) }
        #expect(throws: WorkspaceFileError.notDirectory) { try WorkspaceFiles.list("docs/readme.md", root: root) }
    }

    @Test func unreadableFoldersReportAnErrorInsteadOfCrashing() throws {
        let (root, outside) = try makeRoot()
        let locked = root.appendingPathComponent("locked")
        try FileManager.default.createDirectory(at: locked, withIntermediateDirectories: true)
        try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: locked.path)
        defer {
            try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: locked.path)
            try? FileManager.default.removeItem(at: root); try? FileManager.default.removeItem(at: outside)
        }
        guard !FileManager.default.isReadableFile(atPath: locked.path) else { return } // running as root
        #expect(throws: WorkspaceFileError.self) { try WorkspaceFiles.list("locked", root: root) }
    }

    @Test func classificationUsesNamesThenSniffing() {
        let text = Data("hello\n".utf8), binary = Data([0x89, 0x50, 0x4E, 0x47, 0x00, 0x01])
        #expect(FilePreviewClassifier.classify(name: "README.md", head: text) == .markdown)
        #expect(FilePreviewClassifier.classify(name: "page.MDX", head: text) == .markdown)
        #expect(FilePreviewClassifier.classify(name: "App.swift", head: text) == .source(.swift))
        #expect(FilePreviewClassifier.classify(name: "index.tsx", head: text) == .source(.javascript))
        #expect(FilePreviewClassifier.classify(name: "main.rs", head: text) == .source(.rust))
        #expect(FilePreviewClassifier.classify(name: "build.gradle", head: text) == .source(.gradle))
        #expect(FilePreviewClassifier.classify(name: "Dockerfile", head: text) == .source(.dockerfile))
        #expect(FilePreviewClassifier.classify(name: "Dockerfile.dev", head: text) == .source(.dockerfile))
        #expect(FilePreviewClassifier.classify(name: "Makefile", head: text) == .source(.makefile))
        #expect(FilePreviewClassifier.classify(name: ".env", head: text) == .source(.shell))
        #expect(FilePreviewClassifier.classify(name: ".env.local", head: text) == .source(.shell))
        #expect(FilePreviewClassifier.classify(name: "data.csv", head: text) == .source(.plain))
        #expect(FilePreviewClassifier.classify(name: "LICENSE", head: text) == .source(.plain))
        #expect(FilePreviewClassifier.classify(name: "photo.JPG", head: binary) == .image)
        #expect(FilePreviewClassifier.classify(name: "logo.svg", head: text) == .image)
        #expect(FilePreviewClassifier.classify(name: "spec.pdf", head: binary) == .image)
        #expect(FilePreviewClassifier.classify(name: "program", head: binary) == .unsupported)
        #expect(FilePreviewClassifier.classify(name: "Info.plist", head: Data("bplist00\0".utf8)) == .unsupported)
        #expect(FilePreviewClassifier.classify(name: "archive.zip", head: binary) == .unsupported)
        #expect(FilePreviewClassifier.classify(name: "empty", head: Data()) == .source(.plain))
        #expect(FilePreviewClassifier.fileExtension(".gitignore") == "")
        #expect(FilePreviewClassifier.fileExtension("a.b.TS") == "ts")
    }

    @Test func sniffingAcceptsUTF8CutMidCharacterButNotOtherBytes() {
        let korean = Data("안녕".utf8)
        #expect(FilePreviewClassifier.looksLikeText(korean.dropLast(1)))
        #expect(FilePreviewClassifier.decodedPrefix(korean.dropLast(2)) == "안")
        #expect(!FilePreviewClassifier.looksLikeText(Data([0x41, 0xC3, 0x28, 0xFF, 0x41])))
    }

    @Test func encodingsFollowTheByteOrderMarkThenUTF8ThenCP949() throws {
        func decoded(_ bytes: [UInt8]) -> (String, TextEncoding)? {
            FilePreviewClassifier.decodeText(Data(bytes), sample: false).map { ($0.text, $0.encoding) }
        }
        #expect(decoded([0xEF, 0xBB, 0xBF] + Array("가a".utf8))! == ("가a", .utf8BOM))
        // UTF-16/32 text is full of NUL bytes; the mark says it is still text.
        #expect(decoded([0xFF, 0xFE, 0x41, 0x00, 0x00, 0xAC])! == ("A가", .utf16LE))
        #expect(decoded([0xFE, 0xFF, 0x00, 0x41, 0xAC, 0x00])! == ("A가", .utf16BE))
        #expect(decoded([0xFF, 0xFE, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00])! == ("A", .utf32LE))
        #expect(decoded([0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x41])! == ("A", .utf32BE))
        #expect(FilePreviewClassifier.looksLikeText(Data([0xFF, 0xFE, 0x41, 0x00, 0x42])), "a UTF-16 sample cut mid-unit")
        #expect(decoded(Array("plain 안녕".utf8))! == ("plain 안녕", .utf8))

        let korean = try #require("안녕하세요 똠방각하 abc".data(using: TextEncoding.cp949Encoding))
        #expect(String(data: korean, encoding: .utf8) == nil)
        #expect(decoded([UInt8](korean))! == ("안녕하세요 똠방각하 abc", .cp949))
        // A sample may end on the first byte of a two-byte character.
        #expect(FilePreviewClassifier.decodeText(korean.prefix(5), sample: true)! == ("안녕", .cp949))
        #expect(FilePreviewClassifier.classify(name: "notes.txt", head: korean) == .source(.plain))
        #expect(FilePreviewClassifier.classify(name: "notes.txt", head: Data("a\0b".utf8)) == .unsupported)
        #expect(TextEncoding.cp949.displayName == "CP949 (EUC-KR)")

        let (root, outside) = try makeRoot()
        defer { try? FileManager.default.removeItem(at: root); try? FileManager.default.removeItem(at: outside) }
        try Data([0xFF, 0xFE] + Array("line\n".utf16).flatMap { [UInt8($0 & 0xFF), UInt8($0 >> 8)] }).write(to: root.appendingPathComponent("wide.txt"))
        try korean.write(to: root.appendingPathComponent("euckr.txt"))
        let wide = try FilePreviewClassifier.readText(WorkspaceFiles.openFile("wide.txt", root: root).handle)
        #expect(wide.text == "line\n" && wide.encoding == .utf16LE)
        let legacy = try FilePreviewClassifier.readText(WorkspaceFiles.openFile("euckr.txt", root: root).handle)
        #expect(legacy.text == "안녕하세요 똠방각하 abc" && legacy.encoding == .cp949)
        let cut = try FilePreviewClassifier.readText(WorkspaceFiles.openFile("euckr.txt", root: root).handle, maximumBytes: 5)
        #expect(cut.truncated && cut.text == "안녕" && cut.encoding == .cp949)
    }

    @Test func textReadingStopsAtTheCap() throws {
        let (root, outside) = try makeRoot()
        defer { try? FileManager.default.removeItem(at: root); try? FileManager.default.removeItem(at: outside) }
        try write(String(repeating: "가", count: 10), "big.txt", in: root)
        let file = try WorkspaceFiles.openFile("big.txt", root: root)
        let capped = try FilePreviewClassifier.readText(file.handle, maximumBytes: 7)
        #expect(capped.truncated)
        #expect(capped.text == "가가" && capped.encoding == .utf8)
        let whole = try FilePreviewClassifier.readText(file.handle)
        #expect(!whole.truncated && whole.text.count == 10)
        #expect(FilePreviewClassifier.maximumTextBytes == 1_048_576)
        #expect(try FilePreviewClassifier.readHead(file.handle).count == 30)
        #expect(file.size == 30 && file.modified != nil)
    }

    @Test func filesOpenOnlyAsRegularFilesInsideTheRoot() throws {
        let (root, outside) = try makeRoot()
        defer { try? FileManager.default.removeItem(at: root); try? FileManager.default.removeItem(at: outside) }
        try write("inside", "docs/readme.md", in: root)
        try write("secret", "secret.txt", in: outside)
        let fm = FileManager.default
        try fm.createSymbolicLink(at: root.appendingPathComponent("alias.md"), withDestinationURL: root.appendingPathComponent("docs/readme.md"))
        #expect(mkfifo(root.appendingPathComponent("pipe").path, 0o644) == 0)

        let opened = try WorkspaceFiles.openFile("docs/readme.md", root: root)
        #expect(try FilePreviewClassifier.readHead(opened.handle) == Data("inside".utf8))
        // An in-root link resolves to its target before the open.
        #expect(try WorkspaceFiles.openFile("alias.md", root: root).size == 6)
        // A FIFO opens without blocking and is refused as not a regular file; so is a folder.
        #expect(throws: WorkspaceFileOpenError.notRegularFile) { try WorkspaceFiles.openFile("pipe", root: root) }
        #expect(throws: WorkspaceFileOpenError.notRegularFile) { try WorkspaceFiles.openFile("docs", root: root) }
        #expect(throws: WorkspaceFileOpenError.missing) { try WorkspaceFiles.openFile("gone.txt", root: root) }
        // After resolution: a final symlink is never followed (a swap between
        // resolve and open), and the descriptor's real path must be in the root.
        #expect(throws: WorkspaceFileOpenError.missing) { try WorkspaceFiles.openResolved(root.appendingPathComponent("alias.md"), root: root) }
        #expect(throws: WorkspaceFileOpenError.missing) { try WorkspaceFiles.openResolved(outside.appendingPathComponent("secret.txt"), root: root) }
        #expect(try WorkspaceFiles.openResolved(root.appendingPathComponent("docs/readme.md"), root: root).size == 6)
    }

    @Test func filePanesAreTransientAndHiddenFromThePhone() {
        #expect(FilePaneKind.paneId(workspaceId: "ws1") == "files:ws1")
        #expect(CoreValidation.identifier(FilePaneKind.paneId(workspaceId: UUID().uuidString)))
        #expect(!SessionKind.isStored(FilePaneKind.kind))
        let sessions = [RunSession(id: "agent1", workspaceId: "ws1", title: "a"), RunSession(id: "files:ws1", workspaceId: "ws1", title: "f", kind: FilePaneKind.kind)]
        #expect(FilePaneKind.phoneVisible(sessions).map(\.title) == ["a"])
        var snapshot = AppSnapshot()
        snapshot.workspaces = [Workspace(id: "ws1", name: "w", path: "/tmp")]
        snapshot.sessions = sessions
        #expect(StateRepository.normalize(snapshot, restoring: true).sessions.map(\.id) == ["agent1"])
    }
}

struct SourceHighlighterTests {
    private func spans(_ text: String, _ language: SourceLanguage) -> [(SourceToken.Kind, String)] {
        let units = Array(text.utf16)
        return SourceHighlighter.tokens(text, language: language).map { ($0.kind, String(decoding: units[$0.location..<($0.location + $0.length)], as: UTF16.self)) }
    }

    @Test func swiftKeywordsStringsCommentsAndNumbers() {
        let result = spans("let x = \"hi \\\"there\\\"\" // note\nreturn 42 /* a\nb */ letter", .swift)
        #expect(result.map(\.0) == [.keyword, .string, .comment, .keyword, .number, .comment])
        #expect(result.map(\.1) == ["let", "\"hi \\\"there\\\"\"", "// note", "return", "42", "/* a\nb */"])
    }

    @Test func pythonHashCommentsAndTripleQuotes() {
        let result = spans("def f():\n    \"\"\"doc\nmore\"\"\"\n    return x # done", .python)
        #expect(result.map(\.1) == ["def", "\"\"\"doc\nmore\"\"\"", "return", "# done"])
    }

    @Test func shellHashNeedsAWordBoundary() {
        #expect(spans("echo $# # count", .shell).map(\.1) == ["echo", "# count"])
    }

    @Test func sqlIsCaseInsensitiveAndJsonHasLiterals() {
        #expect(spans("SELECT id FROM t -- all", .sql).map(\.1) == ["SELECT", "FROM", "-- all"])
        #expect(spans("{\"a\": true, \"b\": 1.5}", .json).map(\.0) == [.string, .keyword, .string, .number])
    }

    @Test func unterminatedStringsStopAtTheLineEndAndPlainTextHasNoTokens() {
        #expect(spans("\"open\nlet", .swift).map(\.1) == ["\"open", "let"])
        #expect(spans("`multi\nline`", .javascript).map(\.1) == ["`multi\nline`"])
        #expect(SourceHighlighter.tokens("let x = 1", language: .plain).isEmpty)
        #expect(spans("<a href=\"x\"><!-- c --></a>", .html).map(\.0) == [.string, .comment])
    }

    @Test func lineStartsBreakLikeNSTextView() {
        let scan = SourceLines.scan("a\nbb\r\nccc\rd\u{2028}e\u{2029}f")
        #expect(scan.starts == [0, 2, 6, 10, 12, 14])
        #expect(scan.longest == 3)
        #expect(SourceLines.scan("").starts == [0])
        #expect(SourceLines.scan("x\n").starts == [0, 2])
        #expect(SourceLines.scan("\r\n\r\n").starts == [0, 2, 4])
        #expect(SourceLines.scan(String(repeating: "x", count: 6_000) + "\nshort").longest == 6_000)
        #expect(SourceLines.wrapThreshold == 5_000)
    }

    @Test func scanningIsCapped() {
        let text = String(repeating: "let ", count: 10)
        #expect(SourceHighlighter.tokens(text, language: .swift, maximumUnits: 8).count == 2)
        #expect(!spans("x1 = 2", .python).contains { $0.0 == .number && $0.1 == "1" })
    }
}
