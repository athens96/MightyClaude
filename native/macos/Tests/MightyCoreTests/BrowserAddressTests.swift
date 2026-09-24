import Foundation
import Testing
@testable import MightyCore

struct BrowserAddressTests {
    @Test func browserAddressAddsHttpsScheme() {
        let url = BrowserAddress.resolve("example.com")
        #expect(url?.absoluteString == "https://example.com")
    }

    @Test func browserAddressKeepsExplicitScheme() {
        #expect(BrowserAddress.resolve("http://example.com/path")?.absoluteString == "http://example.com/path")
        #expect(BrowserAddress.resolve("https://secure.example.com")?.absoluteString == "https://secure.example.com")
    }

    @Test func browserAddressIgnoresEmptyInput() {
        #expect(BrowserAddress.resolve("") == nil)
        #expect(BrowserAddress.resolve("   ") == nil)
    }

    @Test func browserNavigationStateFollowsHistory() {
        let a = URL(string: "https://example.com")!
        let b = URL(string: "https://example.com/second")!
        let c = URL(string: "https://example.com/third")!

        var history = BrowserHistory()
        #expect(history.state() == BrowserNavigationState())

        history.visit(a)
        var state = history.state(isLoading: true)
        #expect(state.url == a)
        #expect(state.isLoading)
        #expect(!state.canGoBack)
        #expect(!state.canGoForward)

        // Re-visiting the page on screen is a reload, not a new history entry.
        history.visit(a)
        #expect(!history.canGoBack)

        history.visit(b)
        state = history.state()
        #expect(state.url == b)
        #expect(state.canGoBack)
        #expect(!state.canGoForward)
        #expect(!state.isLoading)

        #expect(history.goBack() == a)
        state = history.state()
        #expect(state.url == a)
        #expect(!state.canGoBack)
        #expect(state.canGoForward)

        #expect(history.goForward() == b)
        #expect(history.state().url == b)
        #expect(history.goForward() == nil)

        // Navigating away from a back entry drops the forward tail.
        #expect(history.goBack() == a)
        history.visit(c)
        state = history.state()
        #expect(state.url == c)
        #expect(state.canGoBack)
        #expect(!state.canGoForward)
        #expect(history.goBack() == a)
        #expect(history.goBack() == nil)
    }
}
