import Foundation
import Testing
@testable import MightyCore

/// The 400-entry cap, pinned numerically, and the two places that must agree
/// about it: what the repository writes back, and how far the phone can page.
struct TranscriptRetentionTests {
    private func entries(_ count: Int) -> [LogEntry] {
        (0..<count).map { LogEntry(id: "log-\($0)", kind: "assistant", text: "line \($0)") }
    }

    @Test func aPaneKeepsExactlyTheNewestFourHundredEntries() {
        #expect(TranscriptRetention.maximumEntries == 400)
        let kept = TranscriptRetention.trimmed(entries(500))
        #expect(kept.count == 400)
        #expect(kept.first?.id == "log-100" && kept.last?.id == "log-499")
        #expect(TranscriptRetention.trimmed(entries(400)).count == 400)
        #expect(TranscriptRetention.trimmed(entries(399)).count == 399)
        #expect(TranscriptRetention.trimmed(entries(7)).map(\.id) == entries(7).map(\.id))
        #expect(TranscriptRetention.trimmed([]).isEmpty)
    }

    @Test func savingAndRestoringKeepsTheSameEntriesTheRunningAppKept() {
        let workspace = Workspace(id: "ws", name: "Repo", path: "/tmp/repo")
        let session = RunSession(id: "pane", workspaceId: "ws", title: "Claude", status: "completed", logs: entries(500))
        let restored = StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [session]), restoring: true).sessions[0]
        // One rule, one result: a repository that kept a different number would
        // lengthen or shorten history across a restart.
        #expect(restored.logs.map(\.id) == TranscriptRetention.trimmed(entries(500)).map(\.id))
        #expect(restored.logs.count == TranscriptRetention.maximumEntries)
    }

    @Test func thePhonePagesBackToExactlyTheOldestRetainedEntry() {
        let kept = TranscriptRetention.trimmed(entries(500))
        #expect(MobileRemoteSupport.hasOlder(entryCount: kept.count))
        // The detail carries the newest window; everything older is paged.
        var window = Array(kept.suffix(MobileSessionDetail.maximumEntries))
        var walked = window
        var pages = 0
        while let oldest = window.first, pages < 32 {
            pages += 1
            let page = MobileRemoteSupport.page(entries: kept, before: oldest.id, limit: MobileSessionDetail.maximumEntries)
            guard !page.entries.isEmpty else { break }
            walked = page.entries + walked
            window = page.entries
            if !page.hasMore { break }
        }
        #expect(pages == 4)
        #expect(walked.map(\.id) == kept.map(\.id))
        #expect(walked.first?.id == "log-100")
        // A cursor for an entry the cap already dropped answers empty rather
        // than silently restarting from the oldest one left.
        let evicted = MobileRemoteSupport.page(entries: kept, before: "log-99", limit: MobileSessionDetail.maximumEntries)
        #expect(evicted.entries.isEmpty && !evicted.hasMore)
    }
}
