import Foundation
import Testing
@testable import MightyCore

@Suite(.serialized)
struct ModelSettingsFileTests {

    private func makeHome() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("model-settings-\(UUID().uuidString)")
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

    // MARK: - Backup exists before write

    @Test func omcBackupExistsBeforeWrite() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let original = Data(#"{"agents":{"planner":{"model":"claude-sonnet-5"}}}"#.utf8)
        try FileManager.default.createDirectory(at: store.omcConfigURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try original.write(to: store.omcConfigURL)

        try store.saveOmcAgents(["planner": "claude-opus-5-5"])

        let dir = store.omcConfigURL.deletingLastPathComponent()
        let backups = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)
            .filter { $0.lastPathComponent.contains("mighty-backup") }
        #expect(!backups.isEmpty, "backup file must exist after save")
        let backupData = try Data(contentsOf: backups[0])
        #expect(backupData == original, "backup must contain the original bytes")
    }

    @Test func ouroborosBackupExistsBeforeWrite() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let original = Data("llm:\n  qa_model: claude-sonnet-5\n".utf8)
        try FileManager.default.createDirectory(at: store.ouroborosConfigURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try original.write(to: store.ouroborosConfigURL)

        try store.saveOuroborosKeys(["llm.qa_model": "claude-opus-5-5"])

        let dir = store.ouroborosConfigURL.deletingLastPathComponent()
        let backups = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)
            .filter { $0.lastPathComponent.contains("mighty-backup") }
        #expect(!backups.isEmpty, "backup file must exist after save")
        let backupData = try Data(contentsOf: backups[0])
        #expect(backupData == original, "backup must contain the original bytes")
    }

    // MARK: - Non-model keys survive

    @Test func omcNonModelKeysSurviveWrite() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let json = #"{"agents":{"planner":{"model":"claude-sonnet-5","extraProp":"keep-me"}},"topLevelKey":"survive"}"#
        try writeOmcConfig(json, store: store)

        try store.saveOmcAgents(["planner": "claude-opus-5-5"])

        let raw = try Data(contentsOf: store.omcConfigURL)
        let parsed = try ModelSettingsFileStore.parseJSONC(raw)
        let agents = parsed["agents"] as? [String: Any]
        let plannerEntry = agents?["planner"] as? [String: Any]
        #expect(plannerEntry?["model"] as? String == "claude-opus-5-5", "model must be updated")
        #expect(plannerEntry?["extraProp"] as? String == "keep-me", "non-model property must survive")
        #expect(parsed["topLevelKey"] as? String == "survive", "top-level non-agents key must survive")
    }

    @Test func ouroborosCliPathSurvivesWrite() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let yaml = """
        orchestrator:
          cli_path: /custom/claude-nested
        llm:
          qa_model: claude-sonnet-5
        """
        try writeOuroborosConfig(yaml, store: store)

        try store.saveOuroborosKeys(["llm.qa_model": "claude-opus-5-5"])

        let content = try String(contentsOf: store.ouroborosConfigURL, encoding: .utf8)
        #expect(content.contains("cli_path: /custom/claude-nested"), "orchestrator.cli_path must survive")
        #expect(content.contains("qa_model: claude-opus-5-5"), "qa_model must be updated")
    }

    // MARK: - Values survive save then reopen

    @Test func omcValuesSurviveSaveReopen() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        try writeOmcConfig("{}", store: store)

        try store.saveOmcAgents(["planner": "claude-opus-5-5", "executor": "claude-sonnet-5"])
        let loaded = try store.loadOmcAgents()
        #expect(loaded?["planner"] == "claude-opus-5-5")
        #expect(loaded?["executor"] == "claude-sonnet-5")
    }

    @Test func ouroborosValuesSurviveSaveReopen() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let yaml = "llm:\n  qa_model: claude-sonnet-5\nclarification:\n  default_model: claude-sonnet-5\n"
        try writeOuroborosConfig(yaml, store: store)

        try store.saveOuroborosKeys([
            "llm.qa_model": "claude-opus-5-5",
            "clarification.default_model": "claude-opus-5-5"
        ])
        let loaded = try store.loadOuroborosKeys()
        #expect(loaded?["llm.qa_model"] == "claude-opus-5-5")
        #expect(loaded?["clarification.default_model"] == "claude-opus-5-5")
    }

    // MARK: - File changed after load is re-read, not clobbered

    @Test func omcExternalChangeIsRespectedOnSave() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let initial = #"{"agents":{"planner":{"model":"claude-sonnet-5"},"executor":{"model":"claude-haiku-4-5-20251001"}}}"#
        try writeOmcConfig(initial, store: store)

        // Simulate load by the app
        _ = try store.loadOmcAgents()

        // External write: another process adds a new agent
        let external = #"{"agents":{"planner":{"model":"claude-sonnet-5"},"executor":{"model":"claude-haiku-4-5-20251001"},"extraAgent":{"model":"extra-model"}}}"#
        try Data(external.utf8).write(to: store.omcConfigURL)

        // Save re-reads from disk, merges only the planner key
        try store.saveOmcAgents(["planner": "claude-opus-5-5"])

        let result = try store.loadOmcAgents()
        #expect(result?["planner"] == "claude-opus-5-5", "planner must be updated")
        #expect(result?["executor"] == "claude-haiku-4-5-20251001", "executor must survive")
        #expect(result?["extraAgent"] == "extra-model", "externally added agent must survive")
    }

    @Test func ouroborosExternalChangeIsRespectedOnSave() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let initial = "llm:\n  qa_model: claude-sonnet-5\n"
        try writeOuroborosConfig(initial, store: store)

        // Simulate load
        _ = try store.loadOuroborosKeys()

        // External write: adds another key under llm
        let external = "llm:\n  qa_model: claude-sonnet-5\n  dependency_analysis_model: claude-haiku-4-5-20251001\n"
        try Data(external.utf8).write(to: store.ouroborosConfigURL)

        // Save re-reads and updates only qa_model
        try store.saveOuroborosKeys(["llm.qa_model": "claude-opus-5-5"])

        let content = try String(contentsOf: store.ouroborosConfigURL, encoding: .utf8)
        #expect(content.contains("qa_model: claude-opus-5-5"), "qa_model must be updated")
        #expect(content.contains("dependency_analysis_model: claude-haiku-4-5-20251001"),
                "externally added key must survive")
    }

    // MARK: - Unparseable file refused and left byte-identical

    @Test func omcUnparsableFileRefusedByteIdentical() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        let badContent = Data("{ not valid json }{broken".utf8)
        try FileManager.default.createDirectory(at: store.omcConfigURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try badContent.write(to: store.omcConfigURL)

        #expect(throws: (any Error).self) {
            try store.saveOmcAgents(["planner": "claude-opus-5-5"])
        }

        let after = try Data(contentsOf: store.omcConfigURL)
        #expect(after == badContent, "unparseable file must remain byte-identical")
    }

    @Test func ouroborosUnparsableFileRefusedByteIdentical() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        // Non-UTF-8 binary data is unparseable
        let badContent = Data([0xFF, 0xFE, 0x00, 0x01, 0xFF, 0xD8])
        try FileManager.default.createDirectory(at: store.ouroborosConfigURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try badContent.write(to: store.ouroborosConfigURL)

        #expect(throws: (any Error).self) {
            try store.saveOuroborosKeys(["llm.qa_model": "claude-opus-5-5"])
        }

        let after = try Data(contentsOf: store.ouroborosConfigURL)
        #expect(after == badContent, "unparseable file must remain byte-identical")
    }

    // MARK: - Nothing written under ~/.claude/agents

    @Test func nothingWrittenUnderClaudeAgents() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        try writeOmcConfig("{}", store: store)

        try store.saveOmcAgents(["planner": "claude-opus-5-5", "executor": "claude-sonnet-5"])

        let claudeAgentsURL = home.appendingPathComponent(".claude/agents")
        #expect(!FileManager.default.fileExists(atPath: claudeAgentsURL.path),
                "~/.claude/agents must not be created or written")
    }

    @Test func nothingWrittenUnderClaudeAgentsDuringOuroborosSave() throws {
        let home = try makeHome(); defer { cleanup(home) }
        let store = ModelSettingsFileStore(homeDirectory: home)
        try writeOuroborosConfig("llm:\n  qa_model: claude-sonnet-5\n", store: store)

        try store.saveOuroborosKeys(["llm.qa_model": "claude-opus-5-5"])

        let claudeAgentsURL = home.appendingPathComponent(".claude/agents")
        #expect(!FileManager.default.fileExists(atPath: claudeAgentsURL.path),
                "~/.claude/agents must not be created or written")
    }

    // MARK: - JSONC comment stripping

    @Test func jsoncCommentStripping() throws {
        let jsonc = """
        {
          // top-level comment
          "agents": {
            "planner": {
              /* block comment */
              "model": "claude-sonnet-5" // inline comment
            }
          }
        }
        """
        let data = Data(jsonc.utf8)
        let parsed = try ModelSettingsFileStore.parseJSONC(data)
        let agents = parsed["agents"] as? [String: Any]
        let planner = agents?["planner"] as? [String: Any]
        #expect(planner?["model"] as? String == "claude-sonnet-5")
    }

    // MARK: - YAML scalar parsing

    @Test func yamlScalarParsingFiltersModelKeys() {
        let yaml = """
        orchestrator:
          cli_path: /some/path
        llm:
          qa_model: claude-sonnet-5
          dependency_analysis_model: claude-opus-5-5
        economics:
          tiers:
            - name: standard
              models: [claude-sonnet-5]
        """
        let result = ModelSettingsFileStore.parseYAMLScalars(yaml)
        // Model keys are present
        #expect(result["llm.qa_model"] == "claude-sonnet-5")
        #expect(result["llm.dependency_analysis_model"] == "claude-opus-5-5")
        // Non-model scalar (cli_path) IS parsed by parseYAMLScalars (caller filters)
        #expect(result["orchestrator.cli_path"] == "/some/path")
        // List items are skipped
        #expect(result["economics.models"] == nil)
    }

    @Test func yamlRewriterPreservesNonUpdatedLines() {
        let yaml = "orchestrator:\n  cli_path: /old\nllm:\n  qa_model: old-model\n"
        let result = ModelSettingsFileStore.rewriteYAMLKeys(yaml, updates: ["llm.qa_model": "new-model"])
        #expect(result.contains("cli_path: /old"), "cli_path must not change")
        #expect(result.contains("qa_model: new-model"), "qa_model must be updated")
        #expect(!result.contains("qa_model: old-model"), "old value must be gone")
    }
}
