import Foundation
import Testing
@testable import MightyCore

struct ComponentCatalogTests {
    @Test func installCommandsCoverEveryAgentAndNothingElse() {
        #expect(ComponentCatalog.installCommand(provider: "claude") == "npm install -g @anthropic-ai/claude-code")
        #expect(ComponentCatalog.installCommand(provider: "codex") == "npm install -g @openai/codex")
        #expect(ComponentCatalog.installCommand(provider: "gemini") == "npm install -g @google/gemini-cli")
        #expect(ComponentCatalog.installCommand(provider: "browser") == nil)
        #expect(ComponentCatalog.requiredPlugins.allSatisfy { ProviderOptions.ids.contains($0.provider) })
    }
}
