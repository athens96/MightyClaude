import Foundation

/// One follow-up as follow_up answers it (macOS only).
public struct DelegationFollowUpInfo: Codable, Sendable, Equatable {
    public var id: String
    public var child: String
    /// Where delivery has it as the call is answered: delivered, held, or
    /// pending while the child's pane did not take it.
    public var lane: DeliveryLane
    /// How many more follow-ups the child may still get.
    public var remaining: Int

    public init(_ followUp: FollowUp, remaining: Int) {
        id = followUp.id; child = followUp.childId; lane = followUp.lane; self.remaining = remaining
    }
}

/// The action tools (macOS only): a parent merges a reported child into its
/// recorded branch, fast-forward only, and sends its children follow-ups.
extension DelegationCoordinator {
    /// The follow-ups one child may get from its parent.
    public static let maximumFollowUps = 2
    /// The most a follow-up's text may take in the delegation file, its JSON
    /// escapes included: 48 KiB, so a child's two follow-ups, held and then
    /// released together by run next, always fit one run's input of at most
    /// 100,000 characters (``CoreValidation``). A follow-up is kept whole
    /// until it is delivered, so it is never cut.
    public static let maximumFollowUpBytes = 48 * 1024

    /// merge(child, expected_head) from `caller`. A child that is not the
    /// caller's is answered with an error. Otherwise the merge waits for its
    /// turn (one tool merge at a time) and runs
    /// ``ChildMerge/toolMerge(_:expectedHead:parentCheckout:)`` in the
    /// parent's checkout recorded when the child was made. Its refusal is
    /// answered with its one reason code (`not_reported`, `worktree_missing`,
    /// `head_moved`, `tracked_changes`, `branch_not_checked_out` or
    /// `diverged`) and changes nothing. A fast-forward writes its MergeRecord
    /// and moves the child to merged; should the child have moved on during
    /// the merge (its pane closed), the record is written all the same, so
    /// the merge can be undone from its card. Whether the call needs the
    /// human's approval first follows the parent pane's permission mode, in
    /// the Claude CLI, before the call reaches the app.
    func merge(_ id: String, expectedHead: String, caller: PaneMCPBinding) async -> DelegationResponse {
        let id = id.trimmingCharacters(in: .whitespacesAndNewlines)
        guard isChild(id, of: caller) else { return .failure(Self.notYourChildMessage(id)) }
        await takeMergeTurn()
        defer { passMergeTurn() }
        // Read again after the wait: an earlier merge may have taken this child.
        guard let child = file.children.first(where: { $0.id == id }) else { return .failure(Self.notYourChildMessage(id)) }
        guard let checkout = await parentCheckout(of: child) else { return .failure("The calling pane is closed.") }
        switch await ChildMerge.toolMerge(child, expectedHead: expectedHead, parentCheckout: checkout) {
        case .refused(let reason):
            return .refusal(reason)
        case .failed(let message):
            return .failure("Git did not merge the child: \(message)")
        case .merged(let record):
            // The merge is made, so it is on record before anything else can
            // run here, and stays so even should the disk refuse it now; the
            // next save writes it.
            if !file.recordMerge(record), file.children.contains(where: { $0.id == record.childId }) { file.merges.append(record) }
            let context = await pruneContext()
            try? commit(file, context: context)
            return DelegationResponse(merged: record)
        }
    }

    /// follow_up(child, text) from `caller`. The first rule that applies answers:
    ///
    /// 1. An empty text, a text over 48 KiB or with a NUL character (no run
    ///    input may hold one, so a held one would block the child's pane), or
    ///    a child that is not the caller's is answered with an error.
    /// 2. `parent_closed`: the caller's pane is closed. A closed parent's
    ///    children keep their cards, and nothing more is sent to them.
    /// 3. `child_closed`: the child is not open (its pane closed, its start
    ///    failed or a human discarded it).
    /// 4. `follow_up_limit`: the child already has its two follow-ups.
    /// 5. `store_full`: even after pruning, the delegation file has no room for it.
    ///
    /// Every refusal changes nothing. Otherwise the follow-up is saved in the
    /// delegation file, pending, with its own id, and the child's count goes
    /// up by one; delivery then offers it to the child's pane and hands it
    /// over exactly once.
    func followUp(_ id: String, text: String, caller: PaneMCPBinding) async -> DelegationResponse {
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return .failure("follow_up needs a text: the instruction for the child.") }
        // Escapes only add bytes, so the UTF-8 size rules out a huge text before it is encoded.
        guard text.utf8.count <= Self.maximumFollowUpBytes, DelegationCopy.storedBytes(text) <= Self.maximumFollowUpBytes else {
            return .failure("A follow-up's text may be at most 48 KiB.")
        }
        guard !text.contains("\0") else { return .failure("A follow-up's text may not contain a NUL character.") }
        let id = id.trimmingCharacters(in: .whitespacesAndNewlines)
        guard isChild(id, of: caller) else { return .failure(Self.notYourChildMessage(id)) }
        // Asks the host about every parent with children, the caller among them.
        let context = await pruneContext()
        // Nothing below suspends until the follow-up is saved, so two calls
        // at once cannot both pass the limit, and the caller's pane is as
        // last seen.
        guard context.openPaneIds.contains(caller.agentPaneId) else { return .refusal(.parentClosed) }
        guard let index = file.children.firstIndex(where: { $0.id == id }) else { return .failure(Self.notYourChildMessage(id)) }
        guard file.children[index].state.isOpen else { return .refusal(.childClosed) }
        let count = file.children[index].followUpCount + 1
        guard count <= Self.maximumFollowUps else { return .refusal(.followUpLimit) }
        let followUp = FollowUp(id: UUID().uuidString.lowercased(), childId: id, text: text)
        var next = file
        next.children[index].followUpCount = count
        next.followUps.append(followUp)
        do {
            try commit(next, context: context)
        } catch DelegationFileError.full {
            return .refusal(.storeFull)
        } catch {
            return .failure("The delegation file could not be saved, so no follow-up was recorded.")
        }
        // Offered to the child's pane before the answer, which says where it went.
        await deliverPending()
        return DelegationResponse(followUp: DelegationFollowUpInfo(file.followUps.first { $0.id == followUp.id } ?? followUp, remaining: Self.maximumFollowUps - count))
    }

    /// Whether `id` names one of `caller`'s children.
    private func isChild(_ id: String, of caller: PaneMCPBinding) -> Bool {
        file.children.contains { $0.id == id && $0.parentSessionId == caller.agentPaneId }
    }

    /// Waits until no other merge or undo is going, then holds the turn.
    func takeMergeTurn() async {
        guard isMerging else { isMerging = true; return }
        // The turn is handed over with isMerging still set.
        await withCheckedContinuation { mergeTurns.append($0) }
    }

    /// Hands the turn to the merge waiting longest, if any.
    func passMergeTurn() {
        if mergeTurns.isEmpty { isMerging = false } else { mergeTurns.removeFirst().resume() }
    }
}

extension DelegationMCPServer {
    /// merge's answer as text and as structured content.
    static func result(_ merged: MergeRecord) -> [String: Any] {
        let text = "Merged child \(merged.childId): \(merged.parentBranch) fast-forwarded from \(merged.preMergeCommit) to \(merged.mergedCommit). The merge is recorded; only a human can undo it, from the child's card."
        let data: [String: Any] = ["child": merged.childId, "kind": merged.kind.rawValue, "parentBranch": merged.parentBranch,
                                   "preMergeCommit": merged.preMergeCommit, "mergedCommit": merged.mergedCommit, "childHead": merged.childHead]
        return ["content": [["type": "text", "text": text]], "structuredContent": ["merged": data]]
    }

    /// follow_up's answer as text and as structured content.
    static func result(_ followUp: DelegationFollowUpInfo) -> [String: Any] {
        let left = followUp.remaining == 0 ? "It was this child's last follow-up." : "This child may get \(followUp.remaining) more."
        let text = "Follow-up \(followUp.id) for child \(followUp.child) is recorded; Mighty Claude hands it to the child exactly once. \(left)"
        let data: [String: Any] = ["id": followUp.id, "child": followUp.child, "lane": followUp.lane.rawValue, "remaining": followUp.remaining]
        return ["content": [["type": "text", "text": text]], "structuredContent": ["followUp": data]]
    }
}
