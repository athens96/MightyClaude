import Foundation
import Testing
@testable import MightyCore

struct CLIAccountTests {
    private func jwt(_ claims: [String: Any]) throws -> String {
        func encode(_ data: Data) -> String { data.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "") }
        return encode(Data(#"{"alg":"none"}"#.utf8)) + "." + encode(try JSONSerialization.data(withJSONObject: claims)) + ".sig"
    }

    @Test func claudeAndCodexStatusesExposeAccountLabelsOnly() throws {
        let claude = CLIAccountSupport.parseClaudeStatus(Data(#"{"loggedIn":true,"authMethod":"claude.ai","email":"me@example.com","orgName":"Org","subscriptionType":"max"}"#.utf8))
        #expect(claude.loggedIn == true && claude.account == "me@example.com" && claude.plan == "Max" && claude.method == "Claude 구독")
        #expect(claude.summary == "me@example.com · Max · Claude 구독")
        #expect(claude.accessVerified == nil && claude.canSignOut)
        let out = CLIAccountSupport.parseClaudeStatus(Data(#"{"loggedIn":false}"#.utf8))
        #expect(out.loggedIn == false && out.summary == "로그인되지 않음")
        let unknown = CLIAccountSupport.parseClaudeStatus(Data("nonsense".utf8))
        #expect(unknown.loggedIn == nil && unknown.summary == unknown.detail && !unknown.detail.isEmpty)
        #expect(CLIAccountStatus(provider: "x").summary == "상태를 확인하지 못했습니다.")
        let org = CLIAccountSupport.parseClaudeStatus(Data(#"{"loggedIn":true,"authMethod":"console","orgName":"Acme"}"#.utf8))
        #expect(org.account == "Acme" && org.method == "Anthropic Console" && org.canSignOut)

        let token = try jwt(["email": "dev@example.com", "https://api.openai.com/auth": ["chatgpt_plan_type": "plus"]])
        let auth = try JSONSerialization.data(withJSONObject: ["tokens": ["id_token": token, "access_token": "secret"]])
        let codex = CLIAccountSupport.parseCodexStatus(text: "Logged in using ChatGPT\n", authJSON: auth)
        #expect(codex.loggedIn == true && codex.method == "ChatGPT" && codex.account == "dev@example.com" && codex.plan == "Plus")
        #expect(!codex.summary.contains("secret"))
        #expect(CLIAccountSupport.parseCodexStatus(text: "Not logged in", authJSON: auth).loggedIn == false)
        #expect(CLIAccountSupport.parseCodexStatus(text: "Logged in using an API key - sk-***", authJSON: nil).method == "API 키")
        #expect(CLIAccountSupport.parseCodexStatus(text: "error: boom", authJSON: nil).loggedIn == nil)
        #expect(CLIAccountSupport.jwtClaims("not-a-jwt") == nil)
    }

    @Test func claudeExternalConfigurationIsNotVerifiedAccessOrAnOAuthAccount() throws {
        // `auth status` reports loggedIn=true even with a fictitious Bedrock
        // bearer token. Its JSON is configuration discovery, not an API check.
        for (provider, label) in [("bedrock", "AWS Bedrock"), ("vertex", "Google Vertex AI"), ("foundry", "Microsoft Foundry"), ("future-provider", "외부 제공자")] {
            let data = try JSONSerialization.data(withJSONObject: [
                "loggedIn": true, "authMethod": "third_party", "apiProvider": provider,
                "email": "previous-oauth@example.com", "subscriptionType": "max", "orgName": "Old Org",
                "apiKey": "fixture-secret", "accessToken": "fixture-secret",
                "AWS_BEARER_TOKEN_BEDROCK": "fixture-secret", "apiKeySource": "fixture-secret"
            ])
            let status = CLIAccountSupport.parseClaudeStatus(data)
            #expect(status.loggedIn == true && status.accessVerified == false && !status.canSignOut)
            #expect(status.method == label && status.summary == "\(label) · 설정됨 · 접근 미확인")
            #expect(status.account == nil && status.plan == nil && !status.detail.isEmpty)
            #expect(!String(describing: status).contains("fixture-secret"))
            #expect(!status.summary.contains("previous-oauth"))
        }
        // A provider field alone still identifies external auth; an explicit
        // signed-out result must never be upgraded to configured/logged in.
        let out = CLIAccountSupport.parseClaudeStatus(Data(#"{"loggedIn":false,"apiProvider":"bedrock"}"#.utf8))
        #expect(out.loggedIn == false && out.accessVerified == false && !out.canSignOut)
        #expect(out.summary == "로그인되지 않음")
        let malformed = CLIAccountSupport.parseClaudeStatus(Data(#"{"apiProvider":"bedrock","authMethod":"third_party","accessToken":"fixture-secret"}"#.utf8))
        #expect(malformed.loggedIn == nil && malformed.accessVerified == nil)
        #expect(!String(describing: malformed).contains("fixture-secret"))
    }

    @Test func claudeAPIKeyAndUnknownMethodsDoNotEchoRawStatus() {
        let keyed = CLIAccountSupport.parseClaudeStatus(Data(#"{"loggedIn":true,"authMethod":"api_key","apiProvider":"firstParty","apiKey":"fixture-secret"}"#.utf8))
        #expect(keyed.method == "Anthropic API 키" && keyed.accessVerified == false && !keyed.canSignOut)
        #expect(keyed.summary == "Anthropic API 키 · 설정됨 · 접근 미확인")
        #expect(!String(describing: keyed).contains("fixture-secret"))
        let unknown = CLIAccountSupport.parseClaudeStatus(Data(#"{"loggedIn":true,"authMethod":"fixture-secret","apiProvider":"fixture-secret"}"#.utf8))
        #expect(unknown.method == "기타 인증" && unknown.accessVerified == nil)
        #expect(!String(describing: unknown).contains("fixture-secret"))
    }

    @Test func geminiStatusAndLogoutWorkOnItsAccountFiles() throws {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("gemini-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let directory = home.appendingPathComponent(".gemini", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        #expect(CLIAccountSupport.geminiStatus(home: home, environment: [:]).loggedIn == false)
        try Data(#"{"security":{"auth":{"selectedType":"oauth-personal"}}}"#.utf8).write(to: directory.appendingPathComponent("settings.json"))
        try Data(#"{"access_token":"x"}"#.utf8).write(to: directory.appendingPathComponent("oauth_creds.json"))
        try Data(#"{"active":"g@example.com","old":["first@example.com"]}"#.utf8).write(to: directory.appendingPathComponent("google_accounts.json"))
        let status = CLIAccountSupport.geminiStatus(home: home, environment: [:])
        #expect(status.loggedIn == true && status.account == "g@example.com" && status.method == "Google 계정")
        try CLIAccountSupport.geminiLogout(home: home)
        #expect(!FileManager.default.fileExists(atPath: directory.appendingPathComponent("oauth_creds.json").path))
        let accounts = try JSONSerialization.jsonObject(with: Data(contentsOf: directory.appendingPathComponent("google_accounts.json"))) as? [String: Any]
        #expect(accounts?["active"] is NSNull && (accounts?["old"] as? [String]) == ["first@example.com", "g@example.com"])
        #expect(CLIAccountSupport.geminiStatus(home: home, environment: [:]).loggedIn == false)
        try CLIAccountSupport.geminiLogout(home: home) // idempotent
        try Data(#"{"security":{"auth":{"selectedType":"gemini-api-key"}}}"#.utf8).write(to: directory.appendingPathComponent("settings.json"))
        let keyed = CLIAccountSupport.geminiStatus(home: home, environment: ["GEMINI_API_KEY": "k"])
        #expect(keyed.method == "Gemini API 키" && keyed.loggedIn == true && !keyed.canSignOut && !keyed.detail.contains("k\""))
        #expect(CLIAccountSupport.geminiStatus(home: home, environment: [:]).loggedIn == nil)
        try Data(#"{"security":{"auth":{"selectedType":"vertex-ai"}}}"#.utf8).write(to: directory.appendingPathComponent("settings.json"))
        #expect(CLIAccountSupport.geminiStatus(home: home, environment: [:]).loggedIn == nil)
        let vertex = CLIAccountSupport.geminiStatus(home: home, environment: ["GOOGLE_CLOUD_PROJECT": "p"])
        #expect(vertex.loggedIn == true && !vertex.canSignOut)
        // Oversized or non-regular account files are ignored, and CODEX_HOME relocates Codex.
        #expect(CLIAccountSupport.boundedData(directory) == nil)
        #expect(CLIAccountSupport.codexHome(home: home, environment: ["CODEX_HOME": "/opt/codex"]).path == "/opt/codex")
        #expect(CLIAccountSupport.codexHome(home: home, environment: [:]).path == home.appendingPathComponent(".codex").path)
    }

    @Test func commandsAreTheCLIsOwn() {
        #expect(CLIAccountSupport.loginCommand(provider: "claude") == "claude auth login")
        #expect(CLIAccountSupport.loginCommand(provider: "claude", option: .console) == "claude auth login --console")
        #expect(CLIAccountSupport.loginCommand(provider: "claude", option: .bedrock) == "CLAUDE_CODE_USE_BEDROCK=1 claude /setup-bedrock")
        #expect(CLIAccountSupport.loginCommand(provider: "codex") == "codex login" && CLIAccountSupport.loginCommand(provider: "gemini") == "gemini")
        #expect(CLIAccountSupport.loginCommand(provider: "other") == nil)
        #expect(CLIAccountSupport.statusArguments(provider: "claude") == ["claude", "auth", "status", "--json"] && CLIAccountSupport.statusArguments(provider: "gemini") == nil)
        #expect(CLIAccountSupport.logoutArguments(provider: "codex") == ["codex", "logout"] && CLIAccountSupport.logoutArguments(provider: "gemini") == nil)
    }
}
