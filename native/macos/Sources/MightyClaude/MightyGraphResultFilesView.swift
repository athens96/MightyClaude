import Foundation
import MightyCore
import SwiftUI

/// Resolve only final result text, once per text/workspace key, away from the UI
/// actor. A cancelled or superseded request cannot publish stale file links.
@MainActor
final class MightyGraphResultFilesModel: ObservableObject {
    struct Source: Hashable, Sendable {
        let runID: String
        let texts: [String]
    }
    struct Request: Hashable, Sendable {
        let sessionID: String
        let root: URL?
        let sources: [Source]

        init(sessionID: String, runs: [MightyGraphRun], root: URL?) {
            self.sessionID = sessionID; self.root = root
            sources = runs.filter { $0.status == "completed" && $0.settled && !$0.resultEntries.isEmpty }
                .map { Source(runID: $0.id, texts: $0.resultEntries.filter { $0.kind != "user" }.map(\.text)) }
        }
    }
    private struct CacheKey: Hashable, Sendable {
        let root: URL?
        let texts: [String]
    }
    @Published private(set) var filesByRunID: [String: [ReferenceFile]] = [:]
    @Published private(set) var selectedRunID: String?
    private var cache: [CacheKey: [ReferenceFile]] = [:]
    private var activeRequest: Request?
    private var lastAutoResultID: String?

    func files(for runID: String) -> [ReferenceFile] { filesByRunID[runID] ?? [] }
    func toggle(_ runID: String) {
        guard !files(for: runID).isEmpty else { return }
        selectedRunID = selectedRunID == runID ? nil : runID
    }
    func close() { selectedRunID = nil }

    func load(_ request: Request) async {
        if activeRequest?.sessionID != request.sessionID || activeRequest?.root != request.root {
            selectedRunID = nil; lastAutoResultID = nil
        }
        activeRequest = request
        let keys = Set(request.sources.map { CacheKey(root: request.root, texts: $0.texts) })
        cache = cache.filter { keys.contains($0.key) }
        publish(request, autoOpen: false)
        let missing = keys.filter { cache[$0] == nil }
        if !missing.isEmpty {
            let resolution = Task.detached(priority: .utility) {
                var values: [CacheKey: [ReferenceFile]] = [:]
                for key in missing {
                    guard !Task.isCancelled else { return values }
                    values[key] = ReferenceLinkSupport.resultFiles(in: key.texts, root: key.root)
                }
                return values
            }
            let values = await withTaskCancellationHandler(operation: { await resolution.value }, onCancel: { resolution.cancel() })
            guard !Task.isCancelled, activeRequest == request else { return }
            cache.merge(values) { _, new in new }
        }
        guard !Task.isCancelled, activeRequest == request else { return }
        publish(request, autoOpen: true)
    }

    private func publish(_ request: Request, autoOpen: Bool) {
        filesByRunID = Dictionary(uniqueKeysWithValues: request.sources.map {
            ($0.runID, cache[CacheKey(root: request.root, texts: $0.texts)] ?? [])
        })
        if let selectedRunID, files(for: selectedRunID).isEmpty { self.selectedRunID = nil }
        if autoOpen, let newest = request.sources.last(where: { !files(for: $0.runID).isEmpty }), newest.runID != lastAutoResultID {
            lastAutoResultID = newest.runID
            selectedRunID = newest.runID
        }
    }
}

struct MightyGraphResultFilesView: View {
    let nodeID: String
    let files: [ReferenceFile]
    let onOpen: (ReferenceFile) -> Void
    let onClose: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 7) {
                Image(systemName: "doc.on.doc").foregroundStyle(Palette.accent)
                Text("결과에 나온 파일").font(.system(size: 12, weight: .semibold))
                Text("\(files.count)").font(.system(size: 10)).foregroundStyle(.secondary)
                Spacer(minLength: 4)
                Button(action: onClose) { Image(systemName: "xmark") }
                    .buttonStyle(.plain).help("파일 목록 닫기")
                    .accessibilityLabel("파일 목록 닫기")
                    .accessibilityIdentifier("mighty-result-files-close-\(nodeID)")
            }.padding(.horizontal, 12).frame(height: 38)
            Divider()
            ScrollView(.vertical) {
                LazyVStack(alignment: .leading, spacing: 4) {
                    ForEach(files) { file in
                        Button { onOpen(file) } label: {
                            HStack(alignment: .top, spacing: 8) {
                                Image(systemName: "doc.text").font(.system(size: 12)).foregroundStyle(Palette.accent)
                                    .padding(.top, 2)
                                VStack(alignment: .leading, spacing: 3) {
                                    Text(file.url.lastPathComponent).font(.system(size: 12, weight: .medium))
                                        .lineLimit(1).truncationMode(.middle)
                                    Text(file.path + (file.line.map { ":\($0)" } ?? ""))
                                        .font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary)
                                        .lineLimit(2).truncationMode(.middle)
                                }.frame(maxWidth: .infinity, alignment: .leading)
                            }
                            .padding(8).contentShape(Rectangle())
                            .background(Palette.subtle, in: RoundedRectangle(cornerRadius: 6))
                        }
                        .buttonStyle(.plain).help(file.path)
                        .accessibilityLabel("\(file.path) 열기")
                        .accessibilityIdentifier("mighty-result-file-\(nodeID)-\(file.path)")
                    }
                }.padding(8).padding(.bottom, 16)
            }
            .accessibilityIdentifier("mighty-result-files-scroll-\(nodeID)")
        }
        .background(Palette.panel, in: RoundedRectangle(cornerRadius: 12))
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .overlay { RoundedRectangle(cornerRadius: 12).stroke(Palette.accent.opacity(0.45), lineWidth: 1) }
        .accessibilityElement(children: .contain).accessibilityIdentifier("mighty-node-\(nodeID)")
    }
}
