import Foundation
import Testing
@testable import MightyCore

struct ComponentCatalogTests {
    @Test func installCommandsCoverEveryAgentAndNothingElse() {
        #expect(ComponentCatalog.installCommand(provider: "claude") == "npm install -g @anthropic-ai/claude-code")
        #expect(ComponentCatalog.installCommand(provider: "codex") == "npm install -g @openai/codex")
        #expect(ComponentCatalog.installCommand(provider: "gemini") == "curl -fsSL https://antigravity.google/cli/install.sh | bash")
        #expect(ComponentCatalog.installCommand(provider: "browser") == nil)
        #expect(ComponentCatalog.requiredPlugins.allSatisfy { ProviderOptions.ids.contains($0.provider) })
    }
}
