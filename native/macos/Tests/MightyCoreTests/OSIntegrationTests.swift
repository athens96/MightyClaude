import Foundation
import Testing
@testable import MightyCore

/// OS 연결 (`OSIntegration.swift`): the link, the shell command, the webhook.
struct OSIntegrationTests {
    @Test func linksAreParsedStrictly() throws {
        let link = try #require(DeepLink.parse(URL(string: "mightyclaude://open?path=/Users/me/app/../app&prompt=fix%20the%20tests&agent=codex")!))
        #expect(link == DeepLink(path: "/Users/me/app", prompt: "fix the tests", agent: "codex"))
        #expect(DeepLink.parse(URL(string: "mightyclaude://open?path=relative")!) == nil)
        #expect(DeepLink.parse(URL(string: "mightyclaude://run?path=/x")!) == nil)
        #expect(DeepLink.parse(URL(string: "https://open?path=/x")!) == nil)
        #expect(DeepLink.parse(URL(string: "mightyclaude://open?path=/x&agent=bash")!)?.agent == nil)
        let round = try #require(DeepLink.url(path: "/a b/c", prompt: "한글 & 기호"))
        #expect(DeepLink.parse(round) == DeepLink(path: "/a b/c", prompt: "한글 & 기호", agent: nil))
    }

    @Test func theShellCommandInstallsExecutable() throws {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-home-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let url = try ShellCommand.install(home: home)
        #expect(url.path.hasSuffix(".local/bin/mighty"))
        #expect(FileManager.default.isExecutableFile(atPath: url.path))
        #expect(try String(contentsOf: url, encoding: .utf8).hasPrefix("#!/bin/sh"))
        #expect(ShellCommand.onPath("/usr/bin:\(home.path)/.local/bin/", folder: ShellCommand.folder(home: home)))
        #expect(!ShellCommand.onPath("/usr/bin", folder: ShellCommand.folder(home: home)))
    }

    @Test func webhooksTakeOnlySafeAddressesAndSendBothKeys() throws {
        #expect(CompletionWebhook.validURL("https://hooks.slack.com/services/x") != nil)
        #expect(CompletionWebhook.validURL("http://localhost:8080/hook") != nil)
        #expect(CompletionWebhook.validURL("http://example.com/hook") == nil)
        #expect(CompletionWebhook.validURL("file:///etc/passwd") == nil)
        let body = try #require(JSONSerialization.jsonObject(with: CompletionWebhook.body(message: "done")) as? [String: String])
        #expect(body == ["text": "done", "content": "done"])
        #expect(CompletionWebhook.request(url: URL(string: "https://x")!, message: "m").httpMethod == "POST")
    }
}
