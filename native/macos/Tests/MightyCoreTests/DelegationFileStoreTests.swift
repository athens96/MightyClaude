import Foundation
import Testing
@testable import MightyCore

/// The delegation side file (macOS only): one file beside workspace-state.json,
/// written atomically, capped at 4 MiB, with TASK/REPORT copies cut at 64 KiB.
struct DelegationFileStoreTests {
    private func makeDirectory() throws -> URL {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("DelegationFileStoreTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    private func copy(_ text: String, child: String = "c1", kind: DelegationCopy.Kind = .report, revision: Int = 1) -> DelegationCopy {
        DelegationCopy(childId: child, kind: kind, revision: revision, contents: Data(text.utf8))
    }

    private func sample() -> DelegationFile {
        var reported = ChildRecord(id: "c1", parentSessionId: "p1", worktreePath: "/tmp/worktrees/c1", parentBranch: "main", baseCommit: "a1", startingMode: "acceptEdits", requestKey: "k1", followUpCount: 1, state: .running)
        reported.recordReport(head: "b2")
        let closed = ChildRecord(id: "c2", parentSessionId: "p1", worktreePath: "/tmp/worktrees/c2", parentBranch: "main", baseCommit: "a1", startingMode: "plan", requestKey: "k2", state: .closed)
        let receipt = DeliveryReceipt(time: "2026-10-09T01:02:03Z", route: .steer, runId: "run-7")
        var file = DelegationFile(
            children: [reported, closed],
            notices: [Notice(id: "n1", childId: "c1", reportRevision: 1, kind: .reported, lane: .delivered, receipt: receipt),
                      Notice(id: "n2", childId: "c2", reportRevision: 0, kind: .endedWithoutReport, lane: .held)],
            followUps: [FollowUp(id: "f1", childId: "c1", text: "테스트도 추가해 줘", lane: .delivered, receipt: DeliveryReceipt(time: "2026-10-09T01:05:00Z", route: .queue, runId: "run-8"))],
            merges: [MergeRecord(childId: "c1", kind: .toolFastForward, parentBranch: "main", preMergeCommit: "a1", mergedCommit: "b2", childHead: "b2")])
        file.setCopy(copy("# 할 일\n버그를 고쳐라", kind: .task, revision: 0))
        file.setCopy(copy("# 보고\n고쳤다"))
        return file
    }

    @Test func aMissingFileLoadsAsEmptyAndCreatesNothing() throws {
        let directory = try makeDirectory(); defer { try? FileManager.default.removeItem(at: directory) }
        let store = DelegationFileStore(directory: directory.appendingPathComponent("profile"))
        #expect(try store.load() == DelegationFile())
        #expect(!FileManager.default.fileExists(atPath: store.fileURL.path))
    }

    @Test func aSavedFileRestoresExactlyBesideTheWorkspaceState() async throws {
        let directory = try makeDirectory(); defer { try? FileManager.default.removeItem(at: directory) }
        try await StateRepository(directory: directory, legacyStateURL: nil).save(AppSnapshot())
        let store = DelegationFileStore(directory: directory)
        let file = sample()
        try store.save(file)
        #expect(store.fileURL == directory.appendingPathComponent("delegation-state.json"))
        #expect(try Set(FileManager.default.contentsOfDirectory(atPath: directory.path)) == ["workspace-state.json", "delegation-state.json"])
        let permissions = try FileManager.default.attributesOfItem(atPath: store.fileURL.path)[.posixPermissions] as? Int
        #expect(permissions == 0o600)

        let restored = try DelegationFileStore(directory: directory).load()
        #expect(restored == file)
        #expect(restored.children.first?.reportRevision == 1); #expect(restored.children.first?.state == .reported)
        #expect(restored.notices.first?.receipt?.runId == "run-7"); #expect(restored.followUps.first?.receipt?.route == .queue)
        #expect(restored.merges.first?.mergedCommit == "b2")
        #expect(restored.copy(childId: "c1", kind: .task)?.text == "# 할 일\n버그를 고쳐라")
        #expect(restored.copy(childId: "c1", kind: .report)?.truncated == false)
        // Saving the restored file writes the same bytes.
        let bytes = try Data(contentsOf: store.fileURL)
        try store.save(restored)
        #expect(try Data(contentsOf: store.fileURL) == bytes)
    }

    @Test func aNewCopyReplacesTheChildsEarlierCopyOfTheSameKind() {
        var file = sample()
        file.setCopy(copy("# 보고 2", revision: 2))
        #expect(file.copies.filter { $0.childId == "c1" && $0.kind == .report }.map(\.revision) == [2])
        #expect(file.copy(childId: "c1", kind: .task)?.revision == 0)
    }

    @Test func copiesAreCutAt64KiBBehindAVisibleMarker() throws {
        let cap = DelegationFileStore.maximumCopyBytes, marker = DelegationCopy.truncationMarker
        #expect(cap == 64 * 1024)
        let exact = copy(String(repeating: "a", count: cap))
        #expect(!exact.truncated); #expect(exact.text.utf8.count == cap); #expect(!exact.text.hasSuffix(marker))

        let long = String(repeating: "a", count: cap + 1)
        let cut = copy(long)
        #expect(cut.truncated); #expect(cut.originalBytes == cap + 1)
        #expect(cut.text.hasSuffix(marker)); #expect(cut.text.utf8.count == cap)
        #expect(long.hasPrefix(String(cut.text.dropLast(marker.count))))

        // Three-byte Korean characters are never split.
        let korean = String(repeating: "보고서", count: 30_000)
        let cutKorean = copy(korean)
        let kept = String(cutKorean.text.dropLast(marker.count))
        #expect(cutKorean.truncated); #expect(cutKorean.text.utf8.count <= cap)
        #expect(kept.allSatisfy { "보고서".contains($0) }); #expect(korean.hasPrefix(kept))
        #expect(cap - cutKorean.text.utf8.count < 3)
    }

    @Test func readingAHugeReportStoresOnlyTheCutCopy() throws {
        let directory = try makeDirectory(); defer { try? FileManager.default.removeItem(at: directory) }
        let report = directory.appendingPathComponent("REPORT.md")
        let body = Data(String(repeating: "line of the report\n", count: 60_000).utf8)
        try body.write(to: report)
        let read = try DelegationCopy.read(childId: "c1", kind: .report, revision: 3, from: report)
        #expect(read == DelegationCopy(childId: "c1", kind: .report, revision: 3, contents: body))
        #expect(read.truncated); #expect(read.originalBytes == body.count); #expect(read.text.utf8.count <= DelegationFileStore.maximumCopyBytes)

        try Data("# 짧은 보고".utf8).write(to: report)
        let short = try DelegationCopy.read(childId: "c1", kind: .report, revision: 4, from: report)
        #expect(!short.truncated); #expect(short.text == "# 짧은 보고")
    }

    @Test func aSaveOverFourMiBIsRefusedAndTheEarlierFileStays() throws {
        let directory = try makeDirectory(); defer { try? FileManager.default.removeItem(at: directory) }
        let store = DelegationFileStore(directory: directory)
        #expect(DelegationFileStore.maximumFileBytes == 4 * 1024 * 1024)
        try store.save(sample())
        let before = try Data(contentsOf: store.fileURL)

        let full = String(repeating: "r", count: DelegationFileStore.maximumCopyBytes * 2)
        var fits = DelegationFile()
        for index in 0 ..< 63 { fits.setCopy(copy(full, child: "c\(index)")) }
        #expect(try DelegationFileStore.encode(fits).count <= DelegationFileStore.maximumFileBytes)
        var over = fits
        over.setCopy(copy(full, child: "c63")); over.setCopy(copy(full, child: "c64"))
        #expect(try DelegationFileStore.encode(over).count > DelegationFileStore.maximumFileBytes)
        #expect(throws: DelegationFileError.full) { try store.save(over) }
        #expect(try Data(contentsOf: store.fileURL) == before)
        #expect(try store.load() == sample())
    }

    @Test func anOversizedOrDamagedFileIsRefusedOnLoadAndLeftAlone() throws {
        let directory = try makeDirectory(); defer { try? FileManager.default.removeItem(at: directory) }
        let store = DelegationFileStore(directory: directory)
        let cases = [Data(count: DelegationFileStore.maximumFileBytes + 1), Data("{".utf8),
                     Data(#"{"version":2,"children":[],"notices":[],"followUps":[],"merges":[],"copies":[]}"#.utf8)]
        for bytes in cases {
            try bytes.write(to: store.fileURL)
            #expect(throws: DelegationFileError.unreadable) { try store.load() }
            #expect(try Data(contentsOf: store.fileURL) == bytes)
        }
    }
}
