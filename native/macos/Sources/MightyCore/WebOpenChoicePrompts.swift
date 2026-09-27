import Foundation

/// The view model behind the URL choice dialog.
///
/// ``WebOpenService`` presents each open_url call that has no remembered
/// choice here and polls for the answer. The app shows every waiting request
/// as a small card in the agent pane that asked, so only that request waits on
/// the user and the rest of the app stays usable. A button click answers the
/// request and takes its card down at once; when the service's 30-second
/// fallback fires first, it dismisses the request and the card goes away too.
///
/// Thread-safe via NSLock. `onChange` runs after every change, on whatever
/// thread made it; the app hops to the main actor there.
public final class WebOpenChoicePrompts: WebOpenPromptPresenter, @unchecked Sendable {
    private let lock = NSLock()
    private var waiting: [WebOpenPromptRequest] = []
    private var answers: [String: WebOpenPromptAnswer] = [:]
    private let onChange: @Sendable () -> Void

    public init(onChange: @escaping @Sendable () -> Void = {}) {
        self.onChange = onChange
    }

    /// Requests still waiting for the user, oldest first.
    public var pending: [WebOpenPromptRequest] {
        lock.lock()
        defer { lock.unlock() }
        return waiting
    }

    /// A button click: answer request `id`. Ignored once the request was
    /// answered or dismissed, so a late click after the fallback does nothing.
    public func choose(_ id: String, destination: WebOpenDestination, remember: Bool) {
        lock.lock()
        guard let index = waiting.firstIndex(where: { $0.id == id }) else { lock.unlock(); return }
        waiting.remove(at: index)
        answers[id] = WebOpenPromptAnswer(destination: destination, remember: remember)
        lock.unlock()
        onChange()
    }

    // MARK: - WebOpenPromptPresenter

    public func present(_ request: WebOpenPromptRequest) {
        lock.lock()
        waiting.append(request)
        lock.unlock()
        onChange()
    }

    public func answer(for id: String) -> WebOpenPromptAnswer? {
        lock.lock()
        defer { lock.unlock() }
        return answers[id]
    }

    public func dismiss(_ id: String) {
        lock.lock()
        let removed = waiting.contains { $0.id == id } || answers[id] != nil
        waiting.removeAll { $0.id == id }
        answers.removeValue(forKey: id)
        lock.unlock()
        if removed { onChange() }
    }
}
