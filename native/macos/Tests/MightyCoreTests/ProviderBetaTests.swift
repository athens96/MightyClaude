import Testing
@testable import MightyCore

struct ProviderBetaTests {
    @Test func codexAndGeminiAreBetaAndClaudeIsOfficial() {
        #expect(!ProviderOptions.isBeta("claude"))
        #expect(ProviderOptions.isBeta("codex"))
        #expect(ProviderOptions.isBeta("gemini"))
        #expect(!ProviderOptions.isBeta("unknown"))
        #expect(ProviderOptions.ids.filter(ProviderOptions.isBeta) == ["codex", "gemini"])
    }

    @Test func theBadgeNeverChangesTheProviderName() {
        #expect(ProviderOptions.ids.map(ProviderOptions.label) == ["Claude", "Codex", "Gemini"])
    }

    @Test func menuTitlesCarryTheBadgeOnlyForBetaProviders() {
        let badge = L("badge.beta")
        #expect(badge != "badge.beta" && !badge.isEmpty)
        #expect(L("badge.betaAccessibility") != "badge.betaAccessibility")
        #expect(ProviderOptions.betaTitle("claude", "Claude") == "Claude")
        #expect(ProviderOptions.betaTitle("codex", "Codex") == "Codex · " + badge)
        #expect(ProviderOptions.betaTitle("gemini", "새 Gemini 실행 창") == "새 Gemini 실행 창 · " + badge)
    }
}
