import Foundation
import Testing
@testable import MightyCore

struct CLIAutoUpdateSettingsTests {
    @Test func missingSettingKeepsOldProfilesOff() throws {
        let legacy = Data(#"{"version":1,"workspaces":[],"sessions":[],"layout":"grid","theme":"dark","sidebarWidth":252}"#.utf8)
        let decoded = try JSONDecoder().decode(AppSnapshot.self, from: legacy)
        #expect(AppSnapshot().autoUpdateCLIs == nil)
        #expect(decoded.autoUpdateCLIs == nil)
        #expect(decoded.autoUpdateCLIs != true)
        #expect(StateRepository.decodeSnapshot(legacy).autoUpdateCLIs == nil)
        let data = try JSONEncoder().encode(decoded)
        let object = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
        #expect(object["autoUpdateCLIs"] == nil)
    }

    @Test func explicitOnAndOffPersistAcrossRepositoryReloads() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-cli-update-setting-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let repository = StateRepository(directory: directory, legacyStateURL: nil)
        for enabled in [true, false] {
            try await repository.save(AppSnapshot(autoUpdateCLIs: enabled))
            let saved = try Data(contentsOf: directory.appendingPathComponent("workspace-state.json"))
            #expect(try JSONDecoder().decode(AppSnapshot.self, from: saved).autoUpdateCLIs == enabled)
            let restored = try await StateRepository(directory: directory, legacyStateURL: nil).load()
            #expect(restored.autoUpdateCLIs == enabled)
        }
    }

    @Test func onlyJSONBooleansEnableTheSettingAndMalformedValuesKeepSessions() throws {
        let workspace = Workspace(id: "setting-workspace", name: "Settings", path: "/tmp")
        let session = RunSession(id: "setting-session", workspaceId: workspace.id, title: "Existing conversation", logs: [LogEntry(id: "setting-log", kind: "assistant", text: "Preserved")])
        let data = try JSONEncoder().encode(AppSnapshot(workspaces: [workspace], sessions: [session]))
        let original = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let invalidValues: [Any] = [0, 1, "true", "false", [Any](), ["enabled": true], NSNull()]
        for invalid in invalidValues {
            var object = original; object["autoUpdateCLIs"] = invalid
            let restored = StateRepository.decodeSnapshot(try JSONSerialization.data(withJSONObject: object))
            #expect(restored.autoUpdateCLIs == nil)
            #expect(restored.autoUpdateCLIs != true)
            #expect(restored.workspaces == [workspace])
            #expect(restored.sessions.first?.logs.first?.text == "Preserved")
        }
        for enabled in [true, false] {
            var object = original; object["autoUpdateCLIs"] = enabled
            let restored = StateRepository.decodeSnapshot(try JSONSerialization.data(withJSONObject: object))
            #expect(restored.autoUpdateCLIs == enabled)
        }
    }
}
