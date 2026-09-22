import Darwin
import Foundation

/// The uploads one submit has taken out of the store, with the claim that
/// holds them. Nothing is deleted yet: a submit the pane refuses releases the
/// claim and the phone can simply send again.
public struct MobileUploadClaim: Sendable, Equatable {
    public let id: UUID
    public let ids: [String]
}
public struct MobileClaimedAttachments: Sendable {
    public let attachments: [RunAttachment]
    public let claim: MobileUploadClaim
}

/// Chunked uploads from a phone (docs/mobile-remote.md, "m1 확장"). A phone
/// declares a file, sends it in fixed chunks strictly in order, completes it,
/// and then names the upload id in `submit`. The bytes live in an owner-only
/// folder under the data directory and never under the phone's own name: the
/// file is `<uploadId>.part`, so nothing the phone types can pick a path.
///
/// Every upload belongs to the device that opened it as well as to the pane,
/// so one phone can neither read nor cancel another's files.
public actor MobileUploadStore {
    /// The contract's chunk size (192 KiB) and the body limit of the chunk
    /// route alone, which must hold one base64-encoded chunk.
    public static let chunkSize = 196_608
    public static let chunkBodyLimit = 300 * 1024
    /// Unfinished uploads are swept after ten minutes.
    public static let expiry: TimeInterval = 600
    /// How many uploads may be open at once: per pane, and across the host.
    /// Both are "too many right now", not "too big", so they answer 429 and a
    /// phone that finishes or cancels what it holds gets its slots back.
    public static let maximumOpenPerSession = 16
    public static let maximumOpen = 64
    public static let maximumNameLength = 120

    private struct Upload {
        let id: String
        let sessionId: String
        let deviceId: String
        let name: String
        let size: Int
        var received: Int
        var nextChunk: Int
        var completed: Bool
        var updatedAt: Date
        var url: URL
        /// Held by the submit that is carrying it right now, so a second
        /// submit naming the same id finds it taken.
        var claim: UUID?
    }

    private let directory: URL
    private let now: @Sendable () -> Date
    private var uploads: [String: Upload] = [:]
    private var prepared = false

    public init(directory: URL, now: @escaping @Sendable () -> Date = { Date() }) {
        self.directory = directory; self.now = now
    }

    // MARK: Names

    /// The display name the attachment will carry: the last path component,
    /// without control or invisible characters, without a leading dot, at most
    /// 120 characters. Nil when nothing usable is left, which the route answers 400.
    public static func sanitize(_ raw: String) -> String? {
        let plain = MobileRemoteSupport.stripInvisibles(raw)
        let component = plain.replacingOccurrences(of: "\\", with: "/").split(separator: "/").last.map(String.init) ?? ""
        var clean = ActivitySupport.clean(component, maximumBytes: 4 * maximumNameLength, singleLine: true)
        while clean.hasPrefix(".") { clean.removeFirst() }
        clean = clean.trimmingCharacters(in: .whitespaces)
        guard !clean.isEmpty else { return nil }
        return String(clean.prefix(maximumNameLength))
    }

    // MARK: Lifecycle of one upload

    public func begin(sessionId: String, deviceId: String, name: String, size: Int, mimeType: String?) throws -> MobileUploadTicket {
        sweep()
        guard let clean = Self.sanitize(name) else { throw MobileHostError.badRequest("파일 이름이 올바르지 않습니다.") }
        // An empty or negative declaration is a malformed body; only a real
        // file that is simply too big is a size refusal.
        guard size > 0 else { throw MobileHostError.badRequest("size는 1바이트 이상이어야 합니다.") }
        guard size <= AttachmentSupport.maximumFileBytes else { throw MobileHostError.tooLarge("파일 하나는 최대 5 MB입니다.") }
        if let type = mimeType, type.utf8.count > 160 { throw MobileHostError.badRequest("mimeType이 올바르지 않습니다.") }
        guard open(sessionId: sessionId) < Self.maximumOpenPerSession else {
            throw MobileHostError.tooMany("이 실행 창에서 동시에 올릴 수 있는 파일은 \(Self.maximumOpenPerSession)개입니다.")
        }
        guard uploads.count < Self.maximumOpen else { throw MobileHostError.tooMany("동시에 올릴 수 있는 파일 수를 넘었습니다.") }
        try prepare()
        let id = UUID().uuidString.lowercased()
        let url = directory.appendingPathComponent(id + ".part")
        guard FileManager.default.createFile(atPath: url.path, contents: Data(), attributes: [.posixPermissions: 0o600]) else {
            throw MightyError("업로드 파일을 만들지 못했습니다.")
        }
        uploads[id] = Upload(id: id, sessionId: sessionId, deviceId: deviceId, name: clean, size: size, received: 0, nextChunk: 0,
                             completed: false, updatedAt: now(), url: url, claim: nil)
        return MobileUploadTicket(uploadId: id, chunkSize: Self.chunkSize)
    }

    /// Appends chunk `index`. Chunks arrive strictly in order: a repeat or a
    /// skip is a conflict, and a chunk of the wrong length is malformed.
    public func append(id: String, deviceId: String, index: Int, data: Data) throws -> Int {
        sweep()
        var upload = try owned(id, deviceId: deviceId)
        guard !upload.completed else { throw MobileHostError.conflict("이미 끝난 업로드입니다.") }
        let chunks = (upload.size + Self.chunkSize - 1) / Self.chunkSize
        guard index >= 0, index < chunks else { throw MobileHostError.badRequest("chunk 번호가 범위를 벗어났습니다.") }
        guard index == upload.nextChunk else { throw MobileHostError.conflict("chunk는 0부터 순서대로 보내야 합니다.") }
        let expected = min(Self.chunkSize, upload.size - upload.received)
        guard data.count == expected else { throw MobileHostError.badRequest("chunk 크기가 선언과 다릅니다.") }
        guard let handle = try? FileHandle(forWritingTo: upload.url) else { throw MightyError("업로드 파일을 열지 못했습니다.") }
        defer { try? handle.close() }
        do {
            try handle.seekToEnd()
            try handle.write(contentsOf: data)
        } catch { throw MightyError("업로드 내용을 저장하지 못했습니다.") }
        upload.received += data.count
        upload.nextChunk += 1
        upload.updatedAt = now()
        uploads[id] = upload
        return upload.received
    }

    public func complete(id: String, deviceId: String) throws -> MobileUploadAttachment {
        sweep()
        var upload = try owned(id, deviceId: deviceId)
        guard !upload.completed else { throw MobileHostError.conflict("이미 끝난 업로드입니다.") }
        guard upload.received == upload.size else { throw MobileHostError.badRequest("받은 크기가 선언한 크기와 다릅니다.") }
        upload.completed = true
        upload.updatedAt = now()
        uploads[id] = upload
        return MobileUploadAttachment(id: id, name: upload.name, size: upload.size)
    }

    public func cancel(id: String, deviceId: String) throws {
        sweep()
        _ = try owned(id, deviceId: deviceId)
        discard(id)
    }

    /// One upload, but only for the device that opened it. Another phone gets
    /// the same 404 an unknown id would get: whether the file exists is not
    /// its business either.
    private func owned(_ id: String, deviceId: String) throws -> Upload {
        guard let upload = uploads[id], upload.deviceId == deviceId else { throw MobileHostError.notFound("업로드를 찾을 수 없습니다.") }
        return upload
    }

    /// Turns finished uploads into the attachments the Mac's own composer
    /// produces and claims them for this submit. Nothing is spent yet: a
    /// submit the pane refuses releases the claim and the phone's files stay
    /// where they are so it can simply send again.
    public func attachments(ids: [String], sessionId: String, deviceId: String) throws -> MobileClaimedAttachments {
        sweep()
        guard ids.count <= AttachmentSupport.maximumCount else { throw MobileHostError.tooLarge("첨부 파일은 최대 \(AttachmentSupport.maximumCount)개입니다.") }
        guard Set(ids).count == ids.count else { throw MobileHostError.badRequest("같은 업로드를 두 번 첨부할 수 없습니다.") }
        var attachments: [RunAttachment] = []
        var total = 0
        for id in ids {
            guard let upload = uploads[id], upload.completed, upload.sessionId == sessionId, upload.deviceId == deviceId else {
                throw MobileHostError.badRequest("끝나지 않았거나 이 실행 창의 것이 아닌 업로드입니다.")
            }
            // Already travelling with another submit: two requests naming one
            // upload must not both get a copy of it.
            guard upload.claim == nil else { throw MobileHostError.badRequest("이미 전송 중인 업로드입니다.") }
            guard let data = CLIAccountSupport.boundedData(upload.url, maximumBytes: AttachmentSupport.maximumFileBytes), data.count == upload.size else {
                throw MightyError("업로드한 파일을 읽지 못했습니다.")
            }
            total += data.count
            guard total <= AttachmentSupport.maximumTotalBytes else { throw MobileHostError.tooLarge("첨부 파일의 합계는 최대 8 MB입니다.") }
            do { attachments.append(try AttachmentSupport.make(name: upload.name, data: data)) }
            catch { throw MobileHostError.badRequest("첨부할 수 없는 파일입니다.") }
        }
        let claim = MobileUploadClaim(id: UUID(), ids: ids)
        for id in ids { uploads[id]?.claim = claim.id }
        return MobileClaimedAttachments(attachments: attachments, claim: claim)
    }

    /// One use: called once the pane has actually taken the request, so the
    /// same upload id can never be attached twice.
    public func spend(_ claim: MobileUploadClaim) {
        for id in claim.ids where uploads[id]?.claim == claim.id { discard(id) }
    }

    /// The pane refused the request: the files go back to the phone's hands.
    public func release(_ claim: MobileUploadClaim) {
        for id in claim.ids where uploads[id]?.claim == claim.id { uploads[id]?.claim = nil }
    }

    /// Open uploads, for the tests and the limit checks.
    public func count() -> Int { uploads.count }
    public func open(sessionId: String) -> Int { uploads.values.filter { $0.sessionId == sessionId }.count }

    /// Everything one pane was holding, dropped with its bytes: a closed pane
    /// can never take a submit, so its uploads are already unclaimable.
    public func discard(sessionId: String) {
        for (id, upload) in uploads where upload.sessionId == sessionId { discard(id) }
    }

    /// Everything one phone was holding, dropped with its bytes: called when
    /// that phone is unpaired, so nothing it left behind survives the revoke.
    public func discard(device: String) {
        for (id, upload) in uploads where upload.deviceId == device { discard(id) }
    }

    /// Drops everything, files included: called when the host stops.
    public func shutdown() {
        for id in uploads.keys { discard(id) }
        try? FileManager.default.removeItem(at: directory)
        prepared = false
    }

    // MARK: Housekeeping

    private func discard(_ id: String) {
        guard let upload = uploads.removeValue(forKey: id) else { return }
        try? FileManager.default.removeItem(at: upload.url)
    }

    /// Removes uploads nobody finished within the expiry window, so a phone
    /// that walked out of range does not hold a slot or its bytes forever.
    private func sweep() {
        let deadline = now().addingTimeInterval(-Self.expiry)
        for (id, upload) in uploads where upload.updatedAt < deadline { discard(id) }
    }

    /// Creates the owner-only folder and, once per run, clears anything a
    /// previous crash left behind: no live upload can own those bytes.
    ///
    /// A symlink where the folder should be is refused rather than followed.
    /// Following one would let whoever placed it choose where the phone's
    /// bytes land — and, worse, where the sweep above deletes.
    private func prepare() throws {
        guard !prepared else { return }
        var info = stat()
        if lstat(directory.path, &info) != 0 {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        }
        let descriptor = Darwin.open(directory.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW)
        guard descriptor >= 0 else { throw MightyError("업로드 폴더를 열지 못했습니다.") }
        defer { close(descriptor) }
        guard fchmod(descriptor, 0o700) == 0 else { throw MightyError("업로드 폴더 권한을 설정하지 못했습니다.") }
        let leftovers = (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? []
        for url in leftovers where uploads.values.contains(where: { $0.url == url }) == false { try? FileManager.default.removeItem(at: url) }
        prepared = true
    }
}
