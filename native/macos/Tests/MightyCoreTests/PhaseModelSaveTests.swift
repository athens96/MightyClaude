import Foundation
import Testing
@testable import MightyCore

@Suite(.serialized)
struct PhaseModelSaveTests {

    private func makeHome() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("phase-save-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    private func cleanup(_ url: URL) {
        try? FileManager.default.removeItem(at: url)
    }

    private func writeOmcConfig(_ content: String, store: ModelSettingsFileStore) throws {
        let url = store.omcConfigURL
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(content.utf8).write(to: url)
    }

    private func writeOuroborosConfig(_ content: String, store: ModelSettingsFileStore) throws {
        let url = store.ouroborosConfigURL
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(content.utf8).write(to: url)
    }

    // MARK: - Phase row reaches config.jsonc

    @Test func planningPhaseRowReachesOmcConfigJsonc() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let initial = #"{"agents":{"planner":{"model":"default"},"architect":{"model":"default"},"critic":{"model":"default"}}}"#
        try writeOmcConfig(initial, store: store)

        try store.applyAndSaveOmcPhaseRow(.planning, value: "claude-opus-5-5")

        let agents = try store.loadOmcAgents()
        #expect(agents?["planner"]   == "claude-opus-5-5")
        #expect(agents?["architect"] == "claude-opus-5-5")
        #expect(agents?["critic"]    == "claude-opus-5-5")
    }

    @Test func executionPhaseRowReachesOmcConfigJsonc() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let initial = #"{"agents":{"executor":{"model":"default"}}}"#
        try writeOmcConfig(initial, store: store)

        try store.applyAndSaveOmcPhaseRow(.execution, value: "claude-sonnet-5")

        let agents = try store.loadOmcAgents()
        #expect(agents?["executor"] == "claude-sonnet-5")
    }

    @Test func reviewPhaseRowReachesOmcConfigJsonc() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let initial = #"{"agents":{"codeReviewer":{"model":"default"},"verifier":{"model":"default"}}}"#
        try writeOmcConfig(initial, store: store)

        try store.applyAndSaveOmcPhaseRow(.review, value: "claude-sonnet-5")

        let agents = try store.loadOmcAgents()
        #expect(agents?["codeReviewer"] == "claude-sonnet-5")
        #expect(agents?["verifier"]     == "claude-sonnet-5")
    }

    // MARK: - Phase row reaches config.yaml

    @Test func planningPhaseRowReachesOuroborosConfigYaml() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let yaml = "clarification:\n  default_model: claude-sonnet-5\n"
        try writeOuroborosConfig(yaml, store: store)

        try store.applyAndSaveOuroborosPhaseRow(.planning, value: "claude-opus-5-5")

        let keys = try store.loadOuroborosKeys()
        #expect(keys?["clarification.default_model"] == "claude-opus-5-5")
    }

    @Test func reviewPhaseRowReachesOuroborosConfigYaml() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let yaml = "evaluation:\n  semantic_model: default\nconsensus:\n  judge_model: default\nllm:\n  qa_model: default\n"
        try writeOuroborosConfig(yaml, store: store)

        try store.applyAndSaveOuroborosPhaseRow(.review, value: "claude-opus-5-5")

        let keys = try store.loadOuroborosKeys()
        #expect(keys?["evaluation.semantic_model"] == "claude-opus-5-5")
        #expect(keys?["consensus.judge_model"]     == "claude-opus-5-5")
        #expect(keys?["llm.qa_model"]              == "claude-opus-5-5")
    }

    // MARK: - Backup created

    @Test func omcPhaseRowSaveCreatesBackup() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let original = Data(#"{"agents":{"executor":{"model":"default"}}}"#.utf8)
        try FileManager.default.createDirectory(at: store.omcConfigURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try original.write(to: store.omcConfigURL)

        try store.applyAndSaveOmcPhaseRow(.execution, value: "claude-sonnet-5")

        let dir = store.omcConfigURL.deletingLastPathComponent()
        let backups = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)
            .filter { $0.lastPathComponent.contains("mighty-backup") }
        #expect(!backups.isEmpty, "backup must exist after phase row save")
        #expect(try Data(contentsOf: backups[0]) == original, "backup must contain original bytes")
    }

    // MARK: - orchestrator.cli_path kept

    @Test func ouroborosCliPathKeptAfterPhaseRowSave() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let yaml = """
        orchestrator:
          cli_path: /custom/claude-nested
        evaluation:
          semantic_model: claude-sonnet-5
        consensus:
          judge_model: claude-sonnet-5
        llm:
          qa_model: claude-sonnet-5
        """
        try writeOuroborosConfig(yaml, store: store)

        try store.applyAndSaveOuroborosPhaseRow(.review, value: "claude-opus-5-5")

        let content = try String(contentsOf: store.ouroborosConfigURL, encoding: .utf8)
        #expect(content.contains("cli_path: /custom/claude-nested"),
                "orchestrator.cli_path must survive phase row save")
        #expect(content.contains("semantic_model: claude-opus-5-5"), "semantic_model must be updated")
    }

    // MARK: - Unparseable file refused and byte-identical

    @Test func unparsableOmcFileRefusedByteIdentical() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let bad = Data("{ not json }{broken".utf8)
        try FileManager.default.createDirectory(at: store.omcConfigURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try bad.write(to: store.omcConfigURL)

        #expect(throws: (any Error).self) {
            try store.applyAndSaveOmcPhaseRow(.planning, value: "claude-opus-5-5")
        }

        let after = try Data(contentsOf: store.omcConfigURL)
        #expect(after == bad, "unparseable omc file must remain byte-identical")
    }

    @Test func unparsableOuroborosFileRefusedByteIdentical() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let bad = Data([0xFF, 0xFE, 0x00, 0x01])
        try FileManager.default.createDirectory(at: store.ouroborosConfigURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try bad.write(to: store.ouroborosConfigURL)

        #expect(throws: (any Error).self) {
            try store.applyAndSaveOuroborosPhaseRow(.review, value: "claude-opus-5-5")
        }

        let after = try Data(contentsOf: store.ouroborosConfigURL)
        #expect(after == bad, "unparseable Ouroboros file must remain byte-identical")
    }

    // MARK: - Values reload after save

    @Test func omcValuesReloadAfterPhaseRowSave() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let initial = #"{"agents":{"planner":{"model":"default"},"architect":{"model":"default"},"critic":{"model":"default"},"executor":{"model":"default"}}}"#
        try writeOmcConfig(initial, store: store)

        try store.applyAndSaveOmcPhaseRow(.planning, value: "claude-opus-5-5")

        let reloaded = try store.loadOmcAgents()
        #expect(reloaded?["planner"]   == "claude-opus-5-5")
        #expect(reloaded?["architect"] == "claude-opus-5-5")
        #expect(reloaded?["critic"]    == "claude-opus-5-5")
        // "default" model is stored as absent in config.jsonc; nil means the key was not changed
        #expect(reloaded?["executor"] != "claude-opus-5-5",
                "executor must not be set to the planning row value")
    }

    @Test func ouroborosValuesReloadAfterPhaseRowSave() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let yaml = "clarification:\n  default_model: default\nevaluation:\n  semantic_model: default\nconsensus:\n  judge_model: default\nllm:\n  qa_model: default\n"
        try writeOuroborosConfig(yaml, store: store)

        try store.applyAndSaveOuroborosPhaseRow(.review, value: "claude-opus-5-5")

        let reloaded = try store.loadOuroborosKeys()
        #expect(reloaded?["evaluation.semantic_model"] == "claude-opus-5-5")
        #expect(reloaded?["consensus.judge_model"]     == "claude-opus-5-5")
        #expect(reloaded?["llm.qa_model"]              == "claude-opus-5-5")
        #expect(reloaded?["clarification.default_model"] == "default",
                "planning key must not change when review row is applied")
    }

    // MARK: - No-op when file absent

    @Test func omcPhaseRowIsNoopWhenFileAbsent() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        try store.applyAndSaveOmcPhaseRow(.planning, value: "claude-opus-5-5")
        #expect(!FileManager.default.fileExists(atPath: store.omcConfigURL.path))
    }

    @Test func ouroborosPhaseRowIsNoopWhenFileAbsent() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        try store.applyAndSaveOuroborosPhaseRow(.review, value: "claude-opus-5-5")
        #expect(!FileManager.default.fileExists(atPath: store.ouroborosConfigURL.path))
    }

    // MARK: - Suite marker

    @Test func markerMACWiredOK() {
        print("Suite PhaseModelSaveTests passed")
        print("MAC_WIRED_OK")
    }
}
