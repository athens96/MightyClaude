import Foundation

/// One picture a graph step shows in its preview block.
public struct AgentImageItem: Equatable, Sendable, Identifiable {
    public enum Content: Equatable, Sendable {
        case stored(AgentImageRef)
        case located(AgentImageLocation, alt: String)
    }
    public var id: String
    public var content: Content
}

public enum MightyGraphImages {
    /// The step names a preview block attaches to.
    public static let requestStep = "request"
    public static func agentStep(_ agentID: String) -> String { "agent:" + agentID }

    /// Pictures a step produced, in order, once each: stored tool pictures,
    /// then readable Markdown pictures (local files under the path rule and
    /// data URIs). Remote and refused Markdown targets are not previewed.
    public static func items(_ entries: [LogEntry], root: URL?) -> [AgentImageItem] {
        var seen = Set<String>()
        var result: [AgentImageItem] = []
        for entry in entries {
            for ref in entry.images ?? [] where seen.insert("hash:" + ref.hash).inserted {
                result.append(AgentImageItem(id: "hash:" + ref.hash, content: .stored(ref)))
            }
            guard entry.kind == "assistant" else { continue }
            for image in AgentMarkdownImages.extract(entry.text) {
                let location = AgentImagePaths.locate(image.source, workspaceRoot: root)
                let id: String
                switch location {
                case .file(let url, _): id = "file:" + url.path
                case .inline(_, let base64): id = "data:" + AgentImageSupport.sha256(Data(base64.utf8))
                case .remote, .refused: continue
                }
                if seen.insert(id).inserted { result.append(AgentImageItem(id: id, content: .located(location, alt: image.alt))) }
            }
        }
        return result
    }

    /// A step's pictures, worked out once per step and remembered until its
    /// entries or status change: the graph asks on every redraw, and finding
    /// Markdown pictures touches the file system. A file that appears later
    /// is picked up when the step settles.
    public static func items(_ run: MightyGraphRun, step: String, root: URL?) -> [AgentImageItem] {
        if step == requestStep { return remembered(run.id, step, run.rootEntries, status: run.status, root: root) }
        guard step.hasPrefix("agent:"), let agent = run.agents.first(where: { agentStep($0.id) == step }) else { return [] }
        return remembered(run.id, step, agent.entries, status: agent.status, root: root)
    }

    /// Every step with at least one picture, for the layout.
    public static func galleries(runs: [MightyGraphRun], root: URL?) -> [MightyGraphLayout.ImageGallery] {
        var result: [MightyGraphLayout.ImageGallery] = []
        for run in runs {
            let main = remembered(run.id, requestStep, run.rootEntries, status: run.status, root: root).count
            if main > 0 { result.append(.init(runID: run.id, step: requestStep, count: main)) }
            for agent in run.agents {
                let count = remembered(run.id, agentStep(agent.id), agent.entries, status: agent.status, root: root).count
                if count > 0 { result.append(.init(runID: run.id, step: agentStep(agent.id), count: count)) }
            }
        }
        return result
    }

    private static let memo = Memo()
    private static func remembered(_ runID: String, _ step: String, _ entries: [LogEntry], status: String, root: URL?) -> [AgentImageItem] {
        var hasher = Hasher()
        hasher.combine(status); hasher.combine(entries.count)
        // Only entries that can carry pictures; their text by length, not content.
        for entry in entries where entry.kind == "assistant" || entry.images != nil {
            hasher.combine(entry.id); hasher.combine(entry.kind); hasher.combine(entry.text.utf8.count); hasher.combine(entry.images)
        }
        return memo.value(key: runID + "\u{1F}" + step + "\u{1F}" + (root?.path ?? ""), signature: hasher.finalize()) { items(entries, root: root) }
    }

    private final class Memo: @unchecked Sendable {
        private let lock = NSLock()
        private var values: [String: (signature: Int, items: [AgentImageItem])] = [:]
        func value(key: String, signature: Int, make: () -> [AgentImageItem]) -> [AgentImageItem] {
            lock.lock()
            if let hit = values[key], hit.signature == signature { lock.unlock(); return hit.items }
            lock.unlock()
            let items = make()
            lock.lock()
            if values.count >= 1_024 { values.removeAll() }
            values[key] = (signature, items)
            lock.unlock()
            return items
        }
    }
}
