import Combine
import Foundation

struct CompanionBubbleIdentity: Equatable {
    let sessionID: String?
    let startedAt: Date?
    let completed: Bool
    init(_ agent: AgentPresence?) {
        sessionID = agent?.id
        startedAt = agent?.timing?.startedAt
        completed = agent?.status == "completed"
    }
}

/// A new request opens the bubble. Completing a request shows its result for
/// six seconds. A deliberate click cancels that deadline until the next run.
@MainActor
final class CompanionBubbleController: ObservableObject {
    @Published private(set) var isVisible = true
    private var identity: CompanionBubbleIdentity?
    private var hideTask: Task<Void, Never>?
    private let delay: Duration

    init(delay: Duration = .seconds(6)) { self.delay = delay }
    deinit { hideTask?.cancel() }

    func synchronize(_ next: CompanionBubbleIdentity) {
        guard identity != next else { return }
        identity = next
        hideTask?.cancel()
        isVisible = next.sessionID != nil
        guard next.completed else { return }
        hideTask = Task { [weak self, delay] in
            do { try await Task.sleep(for: delay) } catch { return }
            guard let self, self.identity == next, !Task.isCancelled else { return }
            self.isVisible = false
        }
    }

    func toggle() {
        hideTask?.cancel()
        hideTask = nil
        isVisible.toggle()
    }

    func show() {
        hideTask?.cancel()
        hideTask = nil
        isVisible = true
    }
}
