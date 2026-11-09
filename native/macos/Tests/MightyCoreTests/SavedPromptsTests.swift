import Foundation
import Testing
@testable import MightyCore

/// 저장한 요청 (`SavedPrompts.swift`): names, which prompts a workspace offers, placeholders and the file.
struct SavedPromptsTests {
    @Test func namesAreMadePaletteSafe() {
        #expect(SavedPrompts.normalizedName("  Review PR  ") == "review-pr")
        #expect(SavedPrompts.normalizedName("릴리스 노트!") == "릴리스-노트")
        #expect(SavedPrompts.normalizedName("/:?") == nil)
        #expect(SavedPrompts.normalizedName(String(repeating: "a", count: 60))?.count == 40)
    }

    @Test func aWorkspacePromptShadowsASharedOneOfTheSameName() {
        let prompts = [
            SavedPrompt(id: "1", name: "review", text: "shared review"),
            SavedPrompt(id: "2", name: "review", text: "our review", workspaceId: "w1"),
            SavedPrompt(id: "3", name: "notes", text: "release notes\nsecond line"),
            SavedPrompt(id: "4", name: "deploy", text: "other", workspaceId: "w2"),
        ]
        #expect(SavedPrompts.offered(prompts, workspaceId: "w1").map(\.id) == ["2", "3"])
        #expect(SavedPrompts.offered(prompts, workspaceId: "w2").map(\.id) == ["4", "3", "1"])
        let commands = SavedPrompts.commands(prompts, workspaceId: nil, source: "saved")
        #expect(commands.map(\.invocation) == ["prompt:notes", "prompt:review"])
        #expect(commands[0].description == "release notes" && commands[0].action == .insertPrompt("3") && commands[0].origin == .user)
    }

    @Test func knownPlaceholdersAreFilledAndOthersKept() {
        let text = "On {branch} in {workspace} ({date}): {clipboard} {unknown}"
        #expect(SavedPrompts.expand(text, values: ["branch": "main", "workspace": "app", "date": "2026-11-09"]) == "On main in app (2026-11-09):  {unknown}")
    }

    @Test func promptsRoundTripThroughTheFile() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-prompts-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        #expect(SavedPrompts.load(directory: folder).isEmpty)
        let prompts = [SavedPrompt(id: "a", name: "fix", text: "fix it", workspaceId: "w"), SavedPrompt(id: "b", name: "Bad Name", text: "x")]
        try SavedPrompts.save(prompts, directory: folder)
        #expect(SavedPrompts.load(directory: folder) == [prompts[0]])
    }
}
