import Foundation
import Testing
@testable import MightyCore

/// 요청 되돌리기 (`RunRevert.swift`): what a request changed goes back to the
/// originals the store kept, and nothing a later run or the folder rules protect is written.
struct RunRevertTests {
    private func folders() throws -> (root: URL, store: FileOriginalStore) {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-revert-" + UUID().uuidString, isDirectory: true)
        let root = base.appendingPathComponent("work", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return (root, FileOriginalStore(directory: base.appendingPathComponent("originals", isDirectory: true)))
    }

    private func write(_ text: String, _ name: String, in root: URL) throws {
        let url = root.appendingPathComponent(name)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: url)
    }

    private func read(_ name: String, in root: URL) -> String? {
        (try? Data(contentsOf: root.appendingPathComponent(name))).map { String(decoding: $0, as: UTF8.self) }
    }

    @Test func aRunsEditsGoBackAndItsNewFilesGoToTheTrash() throws {
        let (root, store) = try folders()
        try write("one\ntwo\n", "a.txt", in: root)
        #expect(store.capture(sessionId: "pane", runId: "run1", workspaceRoot: root, filePath: "a.txt", original: "one\ntwo\n"))
        try write("one\nTWO\nthree\n", "a.txt", in: root)
        try write("new\n", "src/b.txt", in: root)
        #expect(store.capture(sessionId: "pane", runId: "run1", workspaceRoot: root, filePath: "src/b.txt", original: nil))

        let plan = RunRevert.plan(store: store, sessionId: "pane", runId: "run1", root: root)
        #expect(plan.map(\.path) == ["a.txt", "src/b.txt"])
        #expect(plan[0].action == .restore && plan[0].restored == 1 && plan[0].removed == 2 && plan[0].revertible)
        #expect(plan[1].action == .trash && plan[1].revertible)

        var trashed: [String] = []
        let outcomes = RunRevert.apply(store: store, sessionId: "pane", runId: "run1", root: root,
                                       trash: { trashed.append($0.lastPathComponent); try FileManager.default.removeItem(at: $0) })
        #expect(outcomes == ["a.txt": .reverted, "src/b.txt": .reverted])
        #expect(read("a.txt", in: root) == "one\ntwo\n")
        #expect(trashed == ["b.txt"] && read("src/b.txt", in: root) == nil)
        // Done once, the same revert finds nothing left to undo.
        #expect(RunRevert.plan(store: store, sessionId: "pane", runId: "run1", root: root).first?.refusal == .unchanged)
    }

    @Test func aPathALaterRunChangedIsRefusedUntilThatRunGoesFirst() throws {
        let (root, store) = try folders()
        try write("v1\n", "a.txt", in: root)
        store.capture(sessionId: "pane", runId: "run1", workspaceRoot: root, filePath: "a.txt", original: "v0\n")
        store.capture(sessionId: "pane", runId: "run2", workspaceRoot: root, filePath: "a.txt", original: "v1\n")
        try write("v2\n", "a.txt", in: root)

        #expect(RunRevert.plan(store: store, sessionId: "pane", runId: "run1", root: root).first?.refusal == .laterRun)
        #expect(RunRevert.apply(store: store, sessionId: "pane", runId: "run1", root: root) == ["a.txt": .refused(.laterRun)])
        #expect(read("a.txt", in: root) == "v2\n")

        #expect(RunRevert.apply(store: store, sessionId: "pane", runId: "run2", root: root) == ["a.txt": .reverted])
        #expect(read("a.txt", in: root) == "v1\n")
    }

    @Test func onlyTheChosenPathsAreWrittenAndAMissingFileIsRefused() throws {
        let (root, store) = try folders()
        try write("x\n", "a.txt", in: root)
        try write("y\n", "b.txt", in: root)
        store.capture(sessionId: "pane", runId: "run1", workspaceRoot: root, filePath: "a.txt", original: "a0\n")
        store.capture(sessionId: "pane", runId: "run1", workspaceRoot: root, filePath: "b.txt", original: "b0\n")
        #expect(RunRevert.apply(store: store, sessionId: "pane", runId: "run1", root: root, paths: ["b.txt"]) == ["b.txt": .reverted])
        #expect(read("a.txt", in: root) == "x\n" && read("b.txt", in: root) == "b0\n")

        try FileManager.default.removeItem(at: root.appendingPathComponent("a.txt"))
        #expect(RunRevert.plan(store: store, sessionId: "pane", runId: "run1", root: root).first?.refusal == .missing)
        #expect(read("a.txt", in: root) == nil)
    }

    @Test func theFileKeepsItsEncodingMarkAndPermissions() throws {
        let (root, store) = try folders()
        let url = root.appendingPathComponent("tool.sh")
        try (Data([0xEF, 0xBB, 0xBF]) + Data("echo new\n".utf8)).write(to: url)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
        store.capture(sessionId: "pane", runId: "run1", workspaceRoot: root, filePath: "tool.sh", original: "\u{FEFF}echo old\n")
        #expect(RunRevert.apply(store: store, sessionId: "pane", runId: "run1", root: root) == ["tool.sh": .reverted])
        #expect(try Data(contentsOf: url) == Data([0xEF, 0xBB, 0xBF]) + Data("echo old\n".utf8))
        #expect((try FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? Int) == 0o755)
    }
}
