import CoreGraphics
import Foundation
@testable import MightyCore

/// Shared execution-graph vectors (native/contracts/graph-vectors.json).
///
/// The committed file holds the INPUTS and the expected macOS OUTPUTS. This
/// type is the single place that turns one input case into its output, so the
/// Swift implementation is what produces every committed expectation and
/// `GraphParityVectorTests` can re-derive them from the committed inputs.
/// The Windows port reads the same file and must reproduce every expectation.
enum GraphVectors {
    static let fixtureURL: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/graph-vectors.json")
    }()

    /// Groups in the file and the minimum number of cases each must carry.
    static let minimumCounts: [(group: String, minimum: Int)] = [
        ("claudeStream", 6), ("codexStream", 4), ("mods", 3), ("bounds", 3),
        ("layout", 8), ("camera", 6), ("capsule", 8), ("resultFiles", 4),
    ]

    /// Entries fed in as input carry this fixed timestamp so nothing in an
    /// expectation depends on the clock.
    static let fixedTimestamp = "2026-01-01T00:00:00Z"

    // MARK: - JSON helpers

    static func opt(_ value: Any?) -> Any { value ?? NSNull() }
    static func str(_ value: Any?) -> String? { value as? String }
    static func num(_ value: Any?) -> Double? {
        if let v = value as? Double { return v }
        if let v = value as? Int { return Double(v) }
        if let v = value as? NSNumber { return v.doubleValue }
        return nil
    }
    static func usageJSON(_ usage: GraphTokenUsage?) -> Any {
        guard let usage else { return NSNull() }
        return ["inputTokens": usage.inputTokens, "outputTokens": usage.outputTokens,
                "cacheReadTokens": usage.cacheReadTokens, "cacheCreationTokens": usage.cacheCreationTokens]
    }
    static func usageIn(_ value: Any?) -> GraphTokenUsage? {
        guard let o = value as? [String: Any] else { return nil }
        return GraphTokenUsage(inputTokens: o["inputTokens"] as? Int ?? 0, outputTokens: o["outputTokens"] as? Int ?? 0,
                               cacheReadTokens: o["cacheReadTokens"] as? Int ?? 0, cacheCreationTokens: o["cacheCreationTokens"] as? Int ?? 0)
    }
    static func recordJSON(_ record: GraphResponseRecord) -> [String: Any] {
        ["responseId": record.responseId, "model": opt(record.model), "usage": usageJSON(record.usage),
         "activityIds": record.activityIds, "markedAsConfigured": record.markedAsConfigured]
    }
    static func recordIn(_ value: [String: Any]) -> GraphResponseRecord {
        GraphResponseRecord(responseId: value["responseId"] as? String ?? "", model: str(value["model"]),
                            usage: usageIn(value["usage"]) ?? GraphTokenUsage(),
                            activityIds: value["activityIds"] as? [String] ?? [],
                            markedAsConfigured: value["markedAsConfigured"] as? Bool ?? false)
    }
    static func entryJSON(_ entry: LogEntry) -> [String: Any] {
        // Timestamps are wall-clock and never part of an expectation.
        ["id": entry.id, "kind": entry.kind, "text": entry.text, "provider": opt(entry.provider)]
    }
    static func entryIn(_ value: [String: Any]) -> LogEntry {
        LogEntry(id: value["id"] as? String ?? "", kind: value["kind"] as? String ?? "system",
                 text: value["text"] as? String ?? "", timestamp: value["timestamp"] as? String ?? fixedTimestamp,
                 provider: str(value["provider"]))
    }
    static func nodeJSON(_ node: ExecutionGraphNode) -> [String: Any] {
        ["id": node.id, "runId": node.runId, "parentId": opt(node.parentId), "kind": node.kind,
         "state": node.state, "title": node.title, "input": opt(node.input), "output": opt(node.output),
         "usage": usageJSON(node.usage), "activityGeneration": opt(node.activityGeneration),
         "responseRecords": opt(node.responseRecords.map { $0.map(recordJSON) }),
         "entries": node.entries.map(entryJSON)]
    }
    static func agentJSON(_ agent: MightyGraphAgent) -> [String: Any] {
        ["id": agent.id, "parentID": opt(agent.parentID), "title": agent.title, "input": agent.input,
         "status": agent.status, "kind": opt(agent.kind), "usage": usageJSON(agent.usage),
         "activityGeneration": opt(agent.activityGeneration),
         "responseRecords": opt(agent.responseRecords.map { $0.map(recordJSON) }),
         "entries": agent.entries.map(entryJSON)]
    }
    static func agentIn(_ value: [String: Any]) -> MightyGraphAgent {
        MightyGraphAgent(id: value["id"] as? String ?? "", parentID: str(value["parentID"]),
                         title: value["title"] as? String ?? "하위 에이전트", input: value["input"] as? String ?? "",
                         status: value["status"] as? String ?? "running",
                         entries: (value["entries"] as? [[String: Any]] ?? []).map(entryIn),
                         kind: str(value["kind"]), usage: usageIn(value["usage"]),
                         activityGeneration: value["activityGeneration"] as? Int,
                         responseRecords: (value["responseRecords"] as? [[String: Any]]).map { $0.map(recordIn) })
    }
    static func runJSON(_ run: MightyGraphRun) -> [String: Any] {
        ["id": run.id, "input": run.input, "status": run.status, "provider": opt(run.provider),
         "finalOutput": opt(run.finalOutput), "sourceRunID": opt(run.sourceRunID),
         "usage": usageJSON(run.usage), "nodeModelLabel": opt(run.nodeModelLabel),
         "configuredModel": opt(run.configuredModel),
         "responseRecords": opt(run.responseRecords.map { $0.map(recordJSON) }),
         "rootEntries": run.rootEntries.map(entryJSON), "resultEntries": run.resultEntries.map(entryJSON),
         "agents": run.agents.map(agentJSON)]
    }
    static func runIn(_ value: [String: Any]) -> MightyGraphRun {
        MightyGraphRun(id: value["id"] as? String ?? "", input: value["input"] as? String ?? "",
                       status: value["status"] as? String ?? "running",
                       rootEntries: (value["rootEntries"] as? [[String: Any]] ?? []).map(entryIn),
                       agents: (value["agents"] as? [[String: Any]] ?? []).map(agentIn),
                       sourceRunID: str(value["sourceRunID"]), finalOutput: str(value["finalOutput"]),
                       usage: usageIn(value["usage"]),
                       responseRecords: (value["responseRecords"] as? [[String: Any]]).map { $0.map(recordIn) },
                       provider: str(value["provider"]), nodeModelLabel: str(value["nodeModelLabel"]),
                       configuredModel: str(value["configuredModel"]))
    }
    static func activityIn(_ value: [String: Any]) -> AgentActivity {
        AgentActivity(id: value["id"] as? String ?? "", provider: value["provider"] as? String ?? "claude",
                      kind: value["kind"] as? String ?? "tool", state: value["state"] as? String ?? "completed",
                      toolName: str(value["toolName"]), summary: value["summary"] as? String ?? "",
                      output: str(value["output"]), durationMs: num(value["durationMs"]))
    }
    static func modIn(_ value: [String: Any]) -> ModMetadata {
        let graph = (value["graph"] as? [String: Any]).map { g in
            ModGraphMetadata(version: g["version"] as? Int ?? 1, phase: g["phase"] as? String ?? "starting",
                             agentId: str(g["agentId"]), parentAgentId: str(g["parentAgentId"]),
                             parentToolUseId: str(g["parentToolUseId"]), name: str(g["name"]),
                             agentType: str(g["agentType"]), model: str(g["model"]),
                             input: str(g["input"]), output: str(g["output"]))
        }
        return ModMetadata(claudeSessionId: value["claudeSessionId"] as? String ?? "session",
                           event: value["event"] as? String ?? "agent.spawn", tool: str(value["tool"]),
                           toolUseId: str(value["toolUseId"]), agentId: str(value["agentId"]), graph: graph)
    }
    static func rectJSON(_ rect: CGRect) -> [String: Any] {
        ["x": Double(rect.origin.x), "y": Double(rect.origin.y), "w": Double(rect.size.width), "h": Double(rect.size.height)]
    }
    static func rectIn(_ value: [String: Any]) -> CGRect {
        CGRect(x: num(value["x"]) ?? 0, y: num(value["y"]) ?? 0, width: num(value["w"]) ?? 0, height: num(value["h"]) ?? 0)
    }
    static func pointJSON(_ point: CGPoint) -> [String: Any] { ["x": Double(point.x), "y": Double(point.y)] }
    static func pointIn(_ value: Any?) -> CGPoint? {
        guard let o = value as? [String: Any] else { return nil }
        return CGPoint(x: num(o["x"]) ?? 0, y: num(o["y"]) ?? 0)
    }
    static func sizeIn(_ value: Any?) -> CGSize? {
        guard let o = value as? [String: Any] else { return nil }
        return CGSize(width: num(o["w"]) ?? 0, height: num(o["h"]) ?? 0)
    }
    static func anchorJSON(_ anchor: MightyGraphCamera.Anchor) -> [String: Any] {
        switch anchor {
        case .hold: return ["kind": "hold"]
        case .reaim(let nodeID, let alignTop): return ["kind": "reaim", "nodeID": nodeID, "alignTop": alignTop]
        }
    }
    static func contentKind(_ content: MightyGraphLayout.Content) -> String {
        switch content {
        case .request: return "request"
        case .agent: return "agent"
        case .result: return "result"
        case .resultFiles: return "resultFiles"
        case .draft: return "draft"
        }
    }

    // MARK: - Runners (one per group)

    /// claudeStream, codexStream and mods all drive ExecutionGraphTracker.
    /// A case is a run identity plus an ordered list of steps.
    static func runTracker(_ value: [String: Any]) -> [String: Any] {
        var emitted: [ExecutionGraphNode] = []
        let tracker = ExecutionGraphTracker(runID: value["runId"] as? String ?? "run-1",
                                            input: str(value["input"]),
                                            provider: value["provider"] as? String ?? "claude",
                                            configuredModel: str(value["configuredModel"]),
                                            emit: { emitted.append($0) })
        for step in value["steps"] as? [[String: Any]] ?? [] {
            switch step["kind"] as? String ?? "" {
            case "frame": tracker.consume(step["value"] as? [String: Any] ?? [:])
            case "mod": tracker.receiveMod(modIn(step["value"] as? [String: Any] ?? [:]))
            case "steer": tracker.steer(id: step["id"] as? String ?? "", text: step["text"] as? String ?? "")
            case "activity": _ = tracker.activity(activityIn(step["value"] as? [String: Any] ?? [:]), toolID: str(step["toolId"]))
            case "finish": tracker.finish(state: step["state"] as? String ?? "completed")
            default: continue
            }
        }
        // The latest snapshot of every node, in first-emission order.
        var order: [String] = []
        var latest: [String: ExecutionGraphNode] = [:]
        for node in emitted {
            if latest[node.id] == nil { order.append(node.id) }
            latest[node.id] = node
        }
        return ["nodes": order.compactMap { latest[$0] }.map(nodeJSON)]
    }

    static func runBounds(_ value: [String: Any]) -> [String: Any] {
        let runs = (value["runs"] as? [[String: Any]] ?? []).map(runIn)
        if value["op"] as? String == "boundedLiveHistory" {
            let result = MightyGraphSupport.boundedLiveHistory(runs)
            return ["runs": result.map(runJSON), "budget": NSNull()]
        }
        var budget = value["budget"] as? Int ?? MightyGraphSupport.liveHistoryLimit
        let result = MightyGraphSupport.normalized(runs, restoring: value["restoring"] as? Bool ?? false,
                                                   budget: &budget, provider: str(value["provider"]))
        return ["runs": result.map(runJSON), "budget": budget]
    }

    static func runLayout(_ value: [String: Any]) -> [String: Any] {
        let layout = MightyGraphLayout.make(runs: (value["runs"] as? [[String: Any]] ?? []).map(runIn),
                                            draft: value["draft"] as? String ?? "",
                                            running: value["running"] as? Bool ?? false,
                                            expanded: Set(value["expanded"] as? [String] ?? []),
                                            blockSizes: [:],
                                            resultFilesRunID: str(value["resultFilesRunID"]),
                                            viewport: sizeIn(value["viewport"]),
                                            sharedResultSize: nil)
        return ["nodes": layout.nodes.map { ["id": $0.id, "kind": contentKind($0.content), "frame": rectJSON($0.frame)] },
                "edges": layout.edges.map { ["source": $0.source, "target": $0.target, "joins": $0.joins] },
                "size": ["w": Double(layout.size.width), "h": Double(layout.size.height)],
                "originX": Double(layout.originX),
                "fittedResultID": opt(layout.fittedResultID)]
    }

    static func runCamera(_ value: [String: Any]) -> Any {
        let args = value["args"] as? [String: Any] ?? [:]
        switch value["fn"] as? String ?? "" {
        case "trimAnchor":
            return anchorJSON(MightyGraphCamera.trimAnchor(previousRunIDs: args["previousRunIDs"] as? [String] ?? [],
                                                           runIDs: args["runIDs"] as? [String] ?? [],
                                                           selectedNodeID: str(args["selectedNodeID"]),
                                                           layoutNodeIDs: Set(args["layoutNodeIDs"] as? [String] ?? [])))
        case "reaimAnchor":
            return anchorJSON(MightyGraphCamera.reaimAnchor(newestRunID: str(args["newestRunID"]),
                                                            selectedNodeID: str(args["selectedNodeID"]),
                                                            layoutNodeIDs: Set(args["layoutNodeIDs"] as? [String] ?? [])))
        case "resizeAnchor":
            var frames: [String: CGRect] = [:]
            for (key, raw) in args["frames"] as? [String: Any] ?? [:] {
                if let o = raw as? [String: Any] { frames[key] = rectIn(o) }
            }
            return anchorJSON(MightyGraphCamera.resizeAnchor(fittedResultID: str(args["fittedResultID"]),
                                                             targetID: str(args["targetID"]),
                                                             targetAlignTop: args["targetAlignTop"] as? Bool ?? false,
                                                             frames: frames))
        case "cameraOffset":
            let point = MightyGraphLayout.cameraOffset(for: rectIn(args["frame"] as? [String: Any] ?? [:]),
                                                       viewport: sizeIn(args["viewport"]) ?? .zero,
                                                       zoom: num(args["zoom"]) ?? 1,
                                                       alignTop: args["alignTop"] as? Bool ?? false)
            return pointJSON(point)
        case "lostFrameIndex":
            let index = MightyGraphCamera.lostFrameIndex(previousFrames: (args["previousFrames"] as? [[String: Any]] ?? []).map(rectIn),
                                                         currentFrames: (args["currentFrames"] as? [[String: Any]] ?? []).map(rectIn),
                                                         camera: pointIn(args["camera"]) ?? .zero,
                                                         viewport: sizeIn(args["viewport"]) ?? .zero,
                                                         zoom: num(args["zoom"]) ?? 1)
            return ["index": opt(index), "stranded": index != nil]
        case "admittedCamera":
            let point = MightyGraphCamera.admittedCamera(targetToken: str(args["targetToken"]),
                                                         consumedToken: str(args["consumedToken"]),
                                                         targetCamera: pointIn(args["targetCamera"]),
                                                         current: pointIn(args["current"]) ?? .zero,
                                                         requested: pointIn(args["requested"]) ?? .zero)
            return opt(point.map(pointJSON))
        case "trimToken":
            return MightyGraphCamera.trimToken(sequence: args["sequence"] as? Int ?? 0, nodeID: args["nodeID"] as? String ?? "")
        case "originX":
            return ["originX": Double(MightyGraphCamera.originX(leadingMinX: num(args["leadingMinX"]) ?? 0)),
                    "canvasWidth": Double(MightyGraphCamera.canvasWidth(leading: num(args["leadingMinX"]) ?? 0, trailing: num(args["trailingMaxX"]) ?? 0))]
        default:
            return NSNull()
        }
    }

    static func runCapsule(_ value: [String: Any]) -> Any {
        let args = value["args"] as? [String: Any] ?? [:]
        let catalog = (args["catalog"] as? [[String: Any]] ?? []).map {
            ModelOption(value: $0["value"] as? String ?? "", displayName: $0["displayName"] as? String ?? "",
                        resolvedModel: str($0["resolvedModel"]))
        }
        let records = (args["records"] as? [[String: Any]] ?? []).map(recordIn)
        switch value["fn"] as? String ?? "" {
        case "blockCapsule":
            return opt(ModelUsageFormat.blockCapsule(usage: usageIn(args["usage"]), records: records,
                                                     nodeModelLabel: str(args["nodeModelLabel"]), catalog: catalog))
        case "blockCapsuleHelp":
            return ModelUsageFormat.blockCapsuleHelp(records: records, catalog: catalog)
        case "activitySuffix":
            let child = (args["childBlock"] as? [String: Any]).map {
                GraphChildBlock(usage: usageIn($0["usage"]), records: ($0["records"] as? [[String: Any]] ?? []).map(recordIn))
            }
            return opt(ModelUsageFormat.activitySuffix(activityId: args["activityId"] as? String ?? "",
                                                       records: records, childBlock: child, catalog: catalog))
        case "callerAttribution":
            return opt(ModelUsageFormat.callerAttribution(activityId: args["activityId"] as? String ?? "", records: records))
        case "blockModels":
            return ModelUsageFormat.blockModels(records: records).map { ["model": $0.model, "usage": usageJSON($0.usage)] }
        case "shortName":
            return ModelUsageFormat.shortName(args["modelId"] as? String ?? "", catalog: catalog)
        case "nodeModelLabel":
            return opt(GraphModelLabel.nodeModelLabel(cliReportedModel: str(args["cliReportedModel"]),
                                                      configuredModel: args["configuredModel"] as? String ?? "default"))
        case "blockTitle":
            return MightyGraphSupport.blockTitle(agentIn(args["agent"] as? [String: Any] ?? [:]))
        case "blockKind":
            return MightyGraphSupport.blockKind(agentIn(args["agent"] as? [String: Any] ?? [:]))
        case "usageSummary":
            let usage = usageIn(args["usage"]) ?? GraphTokenUsage()
            return ["summary": usage.summary, "detail": usage.detail, "compact": GraphTokenUsage.compact(usage.total)]
        case "childBlocks":
            let map = GraphChildBlocks.map(responseRecords: (args["records"] as? [[String: Any]]).map { $0.map(recordIn) },
                                           agents: (args["agents"] as? [[String: Any]] ?? []).map(agentIn),
                                           runId: args["runId"] as? String ?? "")
            return map.keys.sorted().map { key in
                ["activityId": key, "usage": usageJSON(map[key]?.usage),
                 "records": (map[key]?.records ?? []).map(recordJSON)] as [String: Any]
            }
        default:
            return NSNull()
        }
    }

    /// Materializes the listed workspace tree in a temporary root, runs the
    /// result-file rule over the case's texts, and returns the relative paths.
    static func runResultFiles(_ value: [String: Any]) throws -> [String: Any] {
        let root = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("graph-vectors-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        for relative in value["tree"] as? [String] ?? [] {
            let file = root.appendingPathComponent(relative)
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data("fixture\n".utf8).write(to: file)
        }
        let files = ReferenceLinkSupport.resultFiles(in: value["texts"] as? [String] ?? [], root: root)
        return ["paths": files.map { $0.path.replacingOccurrences(of: "\\", with: "/") },
                "lines": files.map { opt($0.line) }]
    }

    /// The output of one case, by group. The committed `expected` of every
    /// case must equal this.
    static func expected(group: String, case value: [String: Any]) throws -> Any {
        switch group {
        case "claudeStream", "codexStream", "mods": return runTracker(value)
        case "bounds": return runBounds(value)
        case "layout": return runLayout(value)
        case "camera": return runCamera(value)
        case "capsule": return runCapsule(value)
        case "resultFiles": return try runResultFiles(value)
        default: return NSNull()
        }
    }

    static func canonical(_ value: Any) throws -> String {
        let wrapped: Any = (value is [String: Any] || value is [Any]) ? value : ["value": value]
        let data = try JSONSerialization.data(withJSONObject: wrapped, options: [.sortedKeys, .withoutEscapingSlashes])
        return String(decoding: data, as: UTF8.self)
    }
}
