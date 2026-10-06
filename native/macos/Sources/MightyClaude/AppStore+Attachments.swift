import AppKit
import Foundation
import UniformTypeIdentifiers
import MightyCore

extension AppStore {
    func attachmentBlockedReason(_ id: String) -> String? {
        guard let session = snapshot.sessions.first(where: { $0.id == id }) else { return L("composer.attachment.blockedNoPane") }
        if session.kind == "shell" { return L("composer.attachment.blockedShell") }
        if !providerRuntime(session.provider, workspaceId: session.workspaceId).capabilities.attachments {
            return L("composer.attachment.unsupported")
        }
        return nil
    }

    func chooseAttachments(_ id: String) {
        guard !hasModal, canEditAttachments(id) else { return }
        if let reason = attachmentBlockedReason(id) { attachmentErrors[id] = reason; return }
        let panel = NSOpenPanel()
        panel.title = L("phone.composer.attach")
        panel.prompt = L("composer.attachment.panelPrompt")
        panel.message = L("composer.attachment.panelMessage")
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
        guard items.count <= AttachmentSupport.maximumCount else { attachmentErrors[id] = L("composer.attachment.tooMany"); return }
        var providers: [NSItemProvider] = []
        for item in items {
            if let text = item.string(forType: .fileURL), let url = URL(string: text), url.isFileURL {
                providers.append(NSItemProvider(item: url as NSURL, typeIdentifier: UTType.fileURL.identifier))
            } else if let type = ([NSPasteboard.PasteboardType.png, .tiff] + item.types).first(where: { item.types.contains($0) && UTType($0.rawValue)?.conforms(to: .image) == true }),
                      let data = item.data(forType: type) {
                guard data.count <= AttachmentSupport.maximumFileBytes else { attachmentErrors[id] = L("composer.attachment.imageTooLarge"); return }
                let provider = NSItemProvider()
                provider.registerDataRepresentation(forTypeIdentifier: type.rawValue, visibility: .ownProcess) { completion in completion(data, nil); return nil }
                providers.append(provider)
            }
        }
        guard !providers.isEmpty else { attachmentErrors[id] = L("composer.attachment.clipboardEmpty"); return }
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
        guard !importingAttachments.contains(id) else { attachmentErrors[id] = L("composer.attachment.stillReading"); return }
        guard count + (attachmentDrafts[id]?.count ?? 0) <= AttachmentSupport.maximumCount else { attachmentErrors[id] = L("composer.attachment.tooMany"); return }
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
