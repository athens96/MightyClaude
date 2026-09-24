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
        var state = BrowserNavigationState()
        #expect(!state.canGoBack)
        #expect(!state.canGoForward)
        #expect(!state.isLoading)
        #expect(state.url == nil)

        // Page starts loading
        state.isLoading = true
        state.url = URL(string: "https://example.com")
        #expect(state.isLoading)
        #expect(state.url != nil)

        // Page finishes loading; no back/forward history yet
        state.isLoading = false
        #expect(!state.isLoading)
        #expect(!state.canGoBack)
        #expect(!state.canGoForward)

        // Navigate to a second page: back becomes available
        state.canGoBack = true
        #expect(state.canGoBack)
        #expect(!state.canGoForward)

        // Go back: back gone, forward available
        state.canGoBack = false
        state.canGoForward = true
        #expect(!state.canGoBack)
        #expect(state.canGoForward)
    }
}
