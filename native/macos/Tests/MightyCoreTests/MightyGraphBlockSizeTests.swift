import Foundation
import Testing
@testable import MightyCore

struct MightyGraphBlockSizeTests {
    @Test func dimensionsClampAndRejectNonfiniteValues() {
        #expect(MightyGraphBlockSize(width: 299, height: -100).normalized == .init(width: 300, height: 140))
        #expect(MightyGraphBlockSize(width: 20_000, height: 8_000).normalized == .init(width: 1_400, height: 1_200))
        #expect(MightyGraphBlockSize(width: 612.5, height: 342.25).normalized == .init(width: 612.5, height: 342.25))
        for invalid in [Double.nan, Double.infinity, -Double.infinity] {
            #expect(MightyGraphBlockSize(width: invalid, height: 200).normalized == nil)
            #expect(MightyGraphBlockSize(width: 500, height: invalid).normalized == nil)
        }
    }

    @Test func oldAndDamagedOptionalMetadataKeepTheSession() throws {
        let session = RunSession(workspaceId: "workspace", title: "Saved", logs: [LogEntry(kind: "user", text: "Keep this")])
        let original = try JSONEncoder().encode(session)
        let old = try JSONDecoder().decode(RunSession.self, from: original)
        #expect(old.graphBlockSizes == nil); #expect(old.logs == session.logs)
        var object = try #require(JSONSerialization.jsonObject(with: original) as? [String: Any])
        object["graphBlockSizes"] = ["pending-input": ["width": "broken", "height": 200]]
        let damaged = try JSONDecoder().decode(RunSession.self, from: JSONSerialization.data(withJSONObject: object))
        #expect(damaged.graphBlockSizes == nil); #expect(damaged.logs == session.logs)
    }

    @Test func normalizerDropsForeignAndExpiredNodesAndBoundsStorage() throws {
        let size = MightyGraphBlockSize(width: 800, height: 400)
        let runs = (0..<10).map { run in
            MightyGraphRun(id: "run-\(run)", status: "completed", agents: (0..<128).map { MightyGraphAgent(id: "agent-\($0)", status: "completed") })
        }
        var values: [String: MightyGraphBlockSize] = ["pending-input": size, "orphan": size, "3:old:request": size]
        for run in runs {
            values[MightyGraphBlockSize.nodeID(runID: run.id, suffix: "request")] = size
            values[MightyGraphBlockSize.nodeID(runID: run.id, suffix: "result")] = size
            for agent in run.agents { values[MightyGraphBlockSize.nodeID(runID: run.id, suffix: "agent:" + agent.id)] = size }
        }
        let normalized = try #require(MightyGraphBlockSize.normalized(values, runs: runs))
        #expect(normalized.count == MightyGraphBlockSize.maximumSavedSizes)
        #expect(normalized["pending-input"] == size)
        #expect(normalized["orphan"] == nil); #expect(normalized["3:old:request"] == nil)
        #expect(normalized[MightyGraphBlockSize.nodeID(runID: "run-9", suffix: "request")] == size)
        #expect(normalized[MightyGraphBlockSize.nodeID(runID: "run-0", suffix: "request")] == nil)
        #expect(MightyGraphBlockSize.nodeID(runID: "한글", suffix: "request") == "6:한글:request")
    }

    @Test func activeResultsAndInvalidDimensionsAreRemovedBeforeEncoding() throws {
        let workspace = Workspace(id: "workspace", name: "Graph", path: "/tmp")
        var session = RunSession(workspaceId: workspace.id, title: "Running")
        session.graphRuns = [MightyGraphRun(id: "running", agents: [MightyGraphAgent(id: "child")])]
        let main = MightyGraphBlockSize.nodeID(runID: "running", suffix: "request")
        let result = MightyGraphBlockSize.nodeID(runID: "running", suffix: "result")
        let child = MightyGraphBlockSize.nodeID(runID: "running", suffix: "agent:child")
        session.graphBlockSizes = [main: .init(width: .nan, height: 300), result: .init(width: 600, height: 400), child: .init(width: 650, height: 350)]
        let normalized = StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [session]), restoring: false)
        let sizes = try #require(normalized.sessions.first?.graphBlockSizes)
        #expect(sizes == [child: .init(width: 650, height: 350)])
        _ = try JSONEncoder().encode(normalized)
        var shell = session; shell.kind = "shell"
        #expect(StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [shell]), restoring: false).sessions.first?.graphBlockSizes == nil)
    }

    @Test func sizesRoundTripPerSessionAndLegacyNodesStayEligible() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-graph-sizes-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let repository = StateRepository(directory: directory, legacyStateURL: nil)
        let workspace = try await repository.approveWorkspace(Workspace(name: "Sizes", path: directory.path))
        var first = RunSession(id: "first", workspaceId: workspace.id, title: "First", status: "completed")
        first.graphRuns = [MightyGraphRun(id: "shared-run", status: "completed", agents: [MightyGraphAgent(id: "child", status: "completed")])]
        let main = MightyGraphBlockSize.nodeID(runID: "shared-run", suffix: "request")
        let agent = MightyGraphBlockSize.nodeID(runID: "shared-run", suffix: "agent:child")
        let result = MightyGraphBlockSize.nodeID(runID: "shared-run", suffix: "result")
        first.graphBlockSizes = [main: .init(width: 701, height: 501), agent: .init(width: 601, height: 401), result: .init(width: 901, height: 301), "pending-input": .init(width: 1, height: 8_000), "foreign": .init(width: 800, height: 400)]
        var second = first; second.id = "second"; second.graphBlockSizes = [main: .init(width: 999, height: 333)]
        let legacyMain = MightyGraphBlockSize.nodeID(runID: "legacy-input", suffix: "request")
        var legacy = RunSession(id: "legacy", workspaceId: workspace.id, title: "Legacy", logs: [LogEntry(id: "legacy-input", kind: "user", text: "Saved request")])
        legacy.graphBlockSizes = [legacyMain: .init(width: 750, height: 450)]
        try await repository.save(AppSnapshot(workspaces: [workspace], sessions: [first, second, legacy]))
        let restored = try await StateRepository(directory: directory, legacyStateURL: nil).load()
        let saved = try #require(restored.sessions.first { $0.id == "first" })
        #expect(saved.graphBlockSizes?[main] == .init(width: 701, height: 501))
        #expect(saved.graphBlockSizes?[agent] == .init(width: 601, height: 401))
        #expect(saved.graphBlockSizes?[result] == .init(width: 901, height: 301))
        #expect(saved.graphBlockSizes?["pending-input"] == .init(width: 300, height: 1_200))
        #expect(saved.graphBlockSizes?["foreign"] == nil)
        #expect(restored.sessions.first { $0.id == "second" }?.graphBlockSizes?[main] == .init(width: 999, height: 333))
        #expect(restored.sessions.first { $0.id == "legacy" }?.graphBlockSizes?[legacyMain] == .init(width: 750, height: 450))
    }
}
