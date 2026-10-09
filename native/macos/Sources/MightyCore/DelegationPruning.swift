import Foundation

/// What pruning the delegation file needs to know from outside it (macOS only).
public struct DelegationPruneContext: Sendable {
    /// Session ids of every pane open now. A parent missing here is closed.
    public var openPaneIds: Set<String>
    /// Whether a merge can still be undone: its recorded parent branch is
    /// checked out with its head at the merged commit. Such a merge record,
    /// and its child's record (the card undo starts from), are never pruned.
    public var canUndo: @Sendable (MergeRecord) -> Bool

    public init(openPaneIds: Set<String>, canUndo: @escaping @Sendable (MergeRecord) -> Bool) {
        self.openPaneIds = openPaneIds; self.canUndo = canUndo
    }
}

/// The answer to delegate's request for room for one more child.
public enum DelegationAdmission: Sendable, Equatable {
    /// The file with the child's record (and its TASK copy) added, pruned so
    /// that the child's full report copy still fits. Saving it is the caller's step.
    case admitted(DelegationFile)
    /// No room for the child's record and a full report copy even after
    /// pruning: always store_full. Nothing changed.
    case refused(DelegationReasonCode)
}

extension DelegationFile {
    /// The room kept for one more child's record, beside its report copy.
    public static let recordAllowance = 4 * 1024

    /// The room a full 64 KiB report copy of `childId` takes in the file.
    public static func reportCopyRoom(childId: String) throws -> Int {
        try elementBytes(DelegationCopy(childId: childId, kind: .report, revision: 1, contents: Data(repeating: UInt8(ascii: "x"), count: DelegationFileStore.maximumCopyBytes)))
    }

    /// The room one more child takes: a record allowance and a full report copy.
    public static func newChildRoom() throws -> Int { try recordAllowance + reportCopyRoom(childId: String(repeating: "x", count: 36)) }

    /// This file pruned until it, plus `headroom` more bytes, fits under the
    /// 4 MiB cap, or nil when it does not fit even after everything allowed is
    /// pruned. A file that already fits comes back unchanged. Pruning goes
    /// oldest first (records keep the order they were made in) and stops as
    /// soon as the file fits:
    ///
    /// 1. the report copies of closed records (closed or discarded), then
    /// 2. closed records that are fully delivered, each with its notices,
    ///    follow-ups, merge records and copies. A notice held for a child
    ///    whose parent pane is closed counts as delivered here.
    ///
    /// Never pruned: open children, failed children (a human discards what a
    /// failed start left from its card), held items of open panes, and merge
    /// records that can still be undone, with their child's record.
    public func pruned(leaving headroom: Int = 0, context: DelegationPruneContext) throws -> DelegationFile? {
        let cap = DelegationFileStore.maximumFileBytes
        var file = self
        var size = try DelegationFileStore.encode(file).count
        if size + headroom <= cap { return file }
        // After a removal `size` may undercount by a separator per emptied
        // array, so a fit is confirmed with the exact size before stopping.
        func fits() throws -> Bool {
            guard size + headroom <= cap else { return false }
            size = try DelegationFileStore.encode(file).count
            return size + headroom <= cap
        }
        let closed = children.filter { $0.state == .closed || $0.state == .discarded }
        for child in closed {
            guard let index = file.copies.firstIndex(where: { $0.childId == child.id && $0.kind == .report }) else { continue }
            size -= try Self.elementBytes(file.copies.remove(at: index))
            if try fits() { return file }
        }
        for child in closed where isFullyDelivered(child, context: context) && !merges.contains(where: { $0.childId == child.id && context.canUndo($0) }) {
            size -= try file.removeEverything(of: child.id)
            if try fits() { return file }
        }
        return nil
    }

    /// Room for one more child: the file with `child`'s record (and its TASK
    /// copy) added and pruned until the child's full report copy still fits,
    /// or store_full when it does not fit even after pruning.
    public func admitting(_ child: ChildRecord, task: DelegationCopy? = nil, context: DelegationPruneContext) throws -> DelegationAdmission {
        var file = self
        file.children.append(child)
        if let task { file.setCopy(task) }
        guard let pruned = try file.pruned(leaving: Self.reportCopyRoom(childId: child.id), context: context) else { return .refused(.storeFull) }
        return .admitted(pruned)
    }

    /// Every notice from the child and follow-up to it is delivered; a notice
    /// held for a child whose parent pane is closed counts as delivered.
    private func isFullyDelivered(_ child: ChildRecord, context: DelegationPruneContext) -> Bool {
        let parentClosed = !context.openPaneIds.contains(child.parentSessionId)
        return notices.allSatisfy { $0.childId != child.id || $0.lane == .delivered || ($0.lane == .held && parentClosed) }
            && followUps.allSatisfy { $0.childId != child.id || $0.lane == .delivered }
    }

    /// Removes the child's record and everything kept for it; returns the bytes freed.
    private mutating func removeEverything(of childId: String) throws -> Int {
        var freed = 0
        func take<Item: Encodable>(_ items: inout [Item], where belongs: (Item) -> Bool) throws {
            for item in items where belongs(item) { freed += try Self.elementBytes(item) }
            items.removeAll(where: belongs)
        }
        try take(&children) { $0.id == childId }
        try take(&notices) { $0.childId == childId }
        try take(&followUps) { $0.childId == childId }
        try take(&merges) { $0.childId == childId }
        try take(&copies) { $0.childId == childId }
        return freed
    }

    /// The bytes one array element adds to the encoded file, its separator included.
    static func elementBytes<Item: Encodable>(_ item: Item) throws -> Int { try DelegationFileStore.encode(item).count + 1 }
}

extension DelegationFileStore {
    /// Saves `file`, pruning first when it nears the cap: when it leaves less
    /// than one more child's room, pruning frees that room if it can, and
    /// otherwise as much as the file itself needs. Throws
    /// ``DelegationFileError/full``, writing nothing, when even that is not
    /// enough. Returns what was written.
    @discardableResult public func save(_ file: DelegationFile, pruning context: DelegationPruneContext) throws -> DelegationFile {
        var kept = try file.pruned(leaving: DelegationFile.newChildRoom(), context: context)
        if kept == nil { kept = try file.pruned(context: context) }
        guard let kept else { throw DelegationFileError.full }
        try save(kept)
        return kept
    }
}
