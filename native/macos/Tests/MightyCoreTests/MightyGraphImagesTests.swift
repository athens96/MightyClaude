import CoreGraphics
import Foundation
import ImageIO
import Testing
@testable import MightyCore

/// The Mighty graph's picture preview block: which steps get one, and where
/// the layout attaches it.
@Suite(.serialized)
final class MightyGraphImagesTests {
    private var directories: [URL] = []
    deinit { for directory in directories { try? FileManager.default.removeItem(at: directory) } }
    private func temporary(_ name: String = "dir") throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-graph-images-\(name)-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true); directories.append(url)
        return WorkspaceFiles.realRoot(url)
    }
    private func png() -> Data {
        let context = CGContext(data: nil, width: 4, height: 4, bitsPerComponent: 8, bytesPerRow: 0,
                                space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.setFillColor(red: 1, green: 0, blue: 0, alpha: 1)
        context.fill(CGRect(x: 0, y: 0, width: 4, height: 4))
        let data = NSMutableData()
        let destination = CGImageDestinationCreateWithData(data, "public.png" as CFString, 1, nil)!
        CGImageDestinationAddImage(destination, context.makeImage()!, nil)
        CGImageDestinationFinalize(destination)
        return data as Data
    }

    @Test func theGraphPreviewAttachesToTheStepThatProducedThePictures() throws {
        let workspace = try temporary("workspace")
        try png().write(to: workspace.appendingPathComponent("plot.png"))
        let ref = AgentImageRef(hash: String(repeating: "c", count: 64), mediaType: "image/png", width: 4, height: 4, bytes: 10, source: "Read")
        let picture = LogEntry(id: "pic", kind: "image", text: "그림", provider: "claude", images: [ref, ref])
        let agent = MightyGraphAgent(id: "graph-child", title: "Look", input: "look", status: "completed", entries: [picture])
        let quiet = MightyGraphAgent(id: "graph-quiet", title: "Quiet", input: "x", status: "completed")
        let run = MightyGraphRun(id: "run-1", input: "show", status: "completed",
                                 rootEntries: [LogEntry(id: "md", kind: "assistant", text: "![plot](plot.png) ![web](https://example.com/x.png)", provider: "claude")],
                                 agents: [agent, quiet], finalOutput: "done")
        let galleries = MightyGraphImages.galleries(runs: [run], root: workspace)
        #expect(galleries == [.init(runID: "run-1", step: "request", count: 1), .init(runID: "run-1", step: "agent:graph-child", count: 1)])
        #expect(MightyGraphImages.items(run, step: "agent:graph-child", root: workspace).map(\.id) == ["hash:" + ref.hash])

        let layout = MightyGraphLayout.make(runs: [run], draft: "", running: false, expanded: [], galleries: galleries)
        let stepID = MightyGraphLayout.nodeID(run, suffix: "agent:graph-child")
        let galleryID = MightyGraphLayout.nodeID(run, suffix: MightyGraphLayout.imagesSuffix + "agent:graph-child")
        let step = try #require(layout.nodes.first { $0.id == stepID })
        let gallery = try #require(layout.nodes.first { $0.id == galleryID })
        #expect(gallery.content == .images(0, "agent:graph-child"))
        #expect(gallery.isAuxiliary); #expect(MightyGraphCamera.isAuxiliary(nodeID: galleryID))
        #expect(gallery.frame.minY == step.frame.minY); #expect(gallery.frame.minX > step.frame.maxX)
        #expect(gallery.frame.width == MightyGraphLayout.imagesWidth)
        #expect(!layout.edges.contains { $0.source == galleryID || $0.target == galleryID })
        #expect(layout.size.width >= gallery.frame.maxX - layout.originX)
        // No other card moves for it.
        let plain = MightyGraphLayout.make(runs: [run], draft: "", running: false, expanded: [])
        for node in plain.nodes { #expect(layout.nodes.first { $0.id == node.id }?.frame == node.frame) }
        // Unfolding grows only the preview, and only when there is more to show.
        #expect(MightyGraphLayout.imagesHeight(count: 1, expanded: true) == MightyGraphLayout.imagesHeight(count: 1, expanded: false))
        #expect(MightyGraphLayout.imagesHeight(count: 9, expanded: true) > MightyGraphLayout.imagesHeight(count: 9, expanded: false))
        #expect(MightyGraphLayout.visibleImages(count: 20, expanded: false) == 6)
        // A step's preview never overlaps a request preview on the same rows.
        let request = try #require(layout.nodes.first { $0.id == MightyGraphLayout.nodeID(run, suffix: MightyGraphLayout.imagesSuffix + "request") })
        #expect(!request.frame.intersects(gallery.frame))
    }

    @Test func aStepsPicturesAreWorkedOutOnceUntilItChanges() throws {
        let workspace = try temporary("workspace")
        let plot = workspace.appendingPathComponent("plot.png"); try png().write(to: plot)
        var run = MightyGraphRun(id: "run-memo-" + UUID().uuidString, input: "show", status: "running",
                                 rootEntries: [LogEntry(id: "md", kind: "assistant", text: "![plot](plot.png) ![later](later.png)", provider: "claude")])
        #expect(MightyGraphImages.items(run, step: "request", root: workspace).count == 1)
        // Nothing about the step changed: the remembered answer, no file look-up.
        try FileManager.default.removeItem(at: plot)
        try png().write(to: workspace.appendingPathComponent("later.png"))
        #expect(MightyGraphImages.items(run, step: "request", root: workspace).count == 1)
        #expect(MightyGraphImages.galleries(runs: [run], root: workspace).first?.count == 1)
        // The step settles: worked out again from the files as they are now.
        run.status = "completed"
        #expect(MightyGraphImages.items(run, step: "request", root: workspace).map(\.id).count == 1)
        #expect(MightyGraphImages.items(run, step: "request", root: workspace).first?.id.hasSuffix("later.png") == true)
        // A new entry also counts as a change.
        let ref = AgentImageRef(hash: String(repeating: "a", count: 64), mediaType: "image/png", width: 4, height: 4, bytes: 10, source: "Read")
        run.rootEntries.append(LogEntry(id: "pic", kind: "image", text: "그림", provider: "claude", images: [ref]))
        #expect(MightyGraphImages.galleries(runs: [run], root: workspace).first?.count == 2)
    }
}
