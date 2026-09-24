import Foundation

public struct BrowserNavigationState: Sendable, Equatable {
    public var canGoBack: Bool
    public var canGoForward: Bool
    public var isLoading: Bool
    public var url: URL?

    public init(canGoBack: Bool = false, canGoForward: Bool = false,
                isLoading: Bool = false, url: URL? = nil) {
        self.canGoBack = canGoBack
        self.canGoForward = canGoForward
        self.isLoading = isLoading
        self.url = url
    }
}
