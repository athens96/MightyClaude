import Foundation

// Back/forward history of one browser pane.
//
// The pane's buttons must be enabled exactly when the page history allows, so
// the rule lives in Core next to BrowserNavigationState and is exercised by
// MightyCoreTests without a CEF engine present.
public struct BrowserHistory: Sendable, Equatable {
    private var entries: [URL] = []
    private var index: Int = -1

    public init() {}

    public var current: URL? { entries.indices.contains(index) ? entries[index] : nil }
    public var canGoBack: Bool { index > 0 }
    public var canGoForward: Bool { index >= 0 && index < entries.count - 1 }

    // A fresh navigation drops whatever was ahead of the current entry, the way
    // Chromium does. Re-visiting the entry the pane already shows is a reload
    // and must not grow the history.
    public mutating func visit(_ url: URL) {
        if current == url { return }
        if index >= 0 && index < entries.count - 1 {
            entries.removeSubrange((index + 1)...)
        }
        entries.append(url)
        index = entries.count - 1
    }

    @discardableResult
    public mutating func goBack() -> URL? {
        guard canGoBack else { return nil }
        index -= 1
        return current
    }

    @discardableResult
    public mutating func goForward() -> URL? {
        guard canGoForward else { return nil }
        index += 1
        return current
    }

    public func state(isLoading: Bool = false) -> BrowserNavigationState {
        BrowserNavigationState(canGoBack: canGoBack,
                               canGoForward: canGoForward,
                               isLoading: isLoading,
                               url: current)
    }
}
