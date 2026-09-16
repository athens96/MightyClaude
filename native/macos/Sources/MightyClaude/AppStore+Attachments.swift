import AppKit
import Foundation
import UniformTypeIdentifiers
import MightyCore

extension AppStore {
    func attachmentBlockedReason(_ id: String) -> String? {
        guard let session = snapshot.sessions.first(where: { $0.id == id }) else { return "실행 창을 선택하세요." }
        if session.kind == "shell" { return "명령 창에는 파일을 첨부할 수 없습니다. AI 실행 창을 사용하세요." }
        if !providerRuntime(session.provider, workspaceId: session.workspaceId).capabilities.attachments {
            return "이 실행기는 첨부 파일을 지원하지 않습니다. 원격 앱의 연결과 버전을 확인하세요."
        }
        return nil
    }

    func chooseAttachments(_ id: String) {
        guard !hasModal, canEditAttachments(id) else { return }
        if let reason = attachmentBlockedReason(id) { attachmentErrors[id] = reason; return }
        let panel = NSOpenPanel()
        panel.title = "파일 첨부"
        panel.prompt = "첨부"
        panel.message = "최대 8개 · 파일당 5 MiB · 합계 8 MiB"
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = true
        attachmentPanelSession = id
        let completion: (NSApplication.ModalResponse) -> Void = { [weak self] response in
            Task { @MainActor in
                guard let self else { return }
                self.attachmentPanelSession = nil
                if response == .OK { self.importAttachments(id, urls: panel.urls) }
            }
        }
        if let window = NSApp.keyWindow { panel.beginSheetModal(for: window, completionHandler: completion) }
        else { panel.begin(completionHandler: completion) }
    }

    func importAttachments(_ id: String, urls: [URL]) {
        importAttachmentBatch(id, count: urls.count) {
            try await Task.detached(priority: .userInitiated) {
                var files: [RunAttachment] = []
                for url in urls {
                    try Task.checkCancellation()
                    files.append(try AttachmentImport.readFile(url))
                    try AttachmentSupport.validate(files)
                }
                return files
            }.value
        }
    }

    func importAttachments(_ id: String, providers: [NSItemProvider]) {
        importAttachmentBatch(id, count: providers.count) {
            var files: [RunAttachment] = []
            for provider in providers {
                try Task.checkCancellation()
                files.append(try await AttachmentImport.load(provider))
                try AttachmentSupport.validate(files)
            }
            return files
        }
    }

    // Called only by an explicit attachment-paste menu action. Ordinary text
    // paste stays with the native TextEditor and its input method.
    func pasteAttachments(_ id: String, from pasteboard: NSPasteboard = .general) {
        guard canEditAttachments(id) else { return }
        if let reason = attachmentBlockedReason(id) { attachmentErrors[id] = reason; return }
        let items = pasteboard.pasteboardItems ?? []
        guard items.count <= AttachmentSupport.maximumCount else { attachmentErrors[id] = "파일은 최대 8개까지 첨부할 수 있습니다."; return }
        var providers: [NSItemProvider] = []
        for item in items {
            if let text = item.string(forType: .fileURL), let url = URL(string: text), url.isFileURL {
                providers.append(NSItemProvider(item: url as NSURL, typeIdentifier: UTType.fileURL.identifier))
            } else if let type = ([NSPasteboard.PasteboardType.png, .tiff] + item.types).first(where: { item.types.contains($0) && UTType($0.rawValue)?.conforms(to: .image) == true }),
                      let data = item.data(forType: type) {
                guard data.count <= AttachmentSupport.maximumFileBytes else { attachmentErrors[id] = "이미지 하나는 최대 5 MiB까지 첨부할 수 있습니다."; return }
                let provider = NSItemProvider()
                provider.registerDataRepresentation(forTypeIdentifier: type.rawValue, visibility: .ownProcess) { completion in completion(data, nil); return nil }
                providers.append(provider)
            }
        }
        guard !providers.isEmpty else { attachmentErrors[id] = "클립보드에 이미지 또는 파일이 없습니다. 텍스트는 입력창에 붙여넣으세요."; return }
        importAttachments(id, providers: providers)
    }

    func removeAttachment(_ id: String, attachmentId: String) {
        attachmentDrafts[id]?.removeAll { $0.id == attachmentId }
        attachmentErrors.removeValue(forKey: id)
    }

    func discardAttachments(_ id: String) {
        attachmentTasks.removeValue(forKey: id)?.cancel()
        importingAttachments.remove(id)
        attachmentDrafts.removeValue(forKey: id)
        attachmentErrors.removeValue(forKey: id)
    }

    private func importAttachmentBatch(_ id: String, count: Int, load: @escaping () async throws -> [RunAttachment]) {
        guard count > 0, canEditAttachments(id) else { return }
        if let reason = attachmentBlockedReason(id) { attachmentErrors[id] = reason; return }
        guard !importingAttachments.contains(id) else { attachmentErrors[id] = "현재 파일을 읽은 후 추가하세요."; return }
        guard count + (attachmentDrafts[id]?.count ?? 0) <= AttachmentSupport.maximumCount else { attachmentErrors[id] = "파일은 최대 8개까지 첨부할 수 있습니다."; return }
        selectSession(id)
        attachmentErrors.removeValue(forKey: id)
        importingAttachments.insert(id)
        attachmentTasks[id] = Task {
            do {
                let imported = try await load()
                try Task.checkCancellation()
                guard canEditAttachments(id) else { return }
                if let reason = attachmentBlockedReason(id) { throw MightyError(reason) }
                let combined = (attachmentDrafts[id] ?? []) + imported
                try AttachmentSupport.validate(combined)
                attachmentDrafts[id] = combined
            } catch {
                if !Task.isCancelled, canEditAttachments(id) { attachmentErrors[id] = error.localizedDescription }
            }
            // A cancelled import can finish after a fresh import has been started.
            // Its cleanup must not clear the newer operation's busy state.
            if !Task.isCancelled {
                importingAttachments.remove(id)
                attachmentTasks.removeValue(forKey: id)
            }
        }
    }
}
