import Foundation
import Testing
@testable import MightyCore

/// A Claude pane's advisor setting (`RunSettings.advisor`) and how it reaches the CLI: nothing when the pane follows
/// Claude's settings, `--advisor <model>` for a model, `CLAUDE_CODE_DISABLE_ADVISOR_TOOL=1` for that run alone when
/// the pane turned it off. And what Claude's own settings files say about the advisor (`ClaudeAdvisorSettings`),
/// read for a pane that follows them and never written.
struct ClaudeAdvisorSettingsTests {
    private let plugin = URL(fileURLWithPath: "/tmp/plugin", isDirectory: true)
    private func request(advisor: String?, effort: String = "default", provider: String = "claude", mode: String = "plan") -> StartRunRequest {
        StartRunRequest(sessionId: "run", workspaceId: "workspace", input: "go", model: "sonnet", provider: provider,
                        settings: RunSettings(effort: effort, permissionMode: mode, advisor: advisor))
    }
    private func environment(_ arguments: [String]) throws -> [String: String]? {
        guard let index = arguments.firstIndex(of: "--settings") else { return nil }
        let object = try JSONSerialization.jsonObject(with: Data(arguments[index + 1].utf8)) as? [String: [String: String]]
        return object?["env"]
    }
    private func folder() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("advisor-settings-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
    private func write(_ text: String, to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: url)
    }

    // MARK: The pane's setting

    @Test func aPaneThatFollowsClaudesSettingsPassesNothing() throws {
        let arguments = try ProviderService.arguments(request(advisor: nil), pluginDirectory: plugin)
        #expect(!arguments.contains("--advisor"))
        #expect(try environment(arguments) == nil)
        // With an effort the settings argument is there for the effort alone.
        let effort = try ProviderService.arguments(request(advisor: nil, effort: "high"), pluginDirectory: plugin)
        #expect(try environment(effort) == ["CLAUDE_CODE_EFFORT_LEVEL": "high"])
    }

    @Test func aModelIsPassedAsTheAdvisorBesideTheMainModel() throws {
        let arguments = try ProviderService.arguments(request(advisor: "opus"), pluginDirectory: plugin)
        let index = try #require(arguments.firstIndex(of: "--advisor"))
        #expect(arguments[index + 1] == "opus")
        #expect(arguments.firstIndex(of: "--model").map { arguments[$0 + 1] } == "sonnet")
        // No switch is set: the CLI checks the model itself.
        #expect(try environment(arguments) == nil)
        let full = try ProviderService.arguments(request(advisor: "claude-opus-5-5[1m]"), pluginDirectory: plugin)
        #expect(full.firstIndex(of: "--advisor").map { full[$0 + 1] } == "claude-opus-5-5[1m]")
    }

    @Test func offIsTheDisableVariableForThatRunAlone() throws {
        let arguments = try ProviderService.arguments(request(advisor: RunSettings.advisorOff), pluginDirectory: plugin)
        #expect(!arguments.contains("--advisor"))
        #expect(try environment(arguments) == ["CLAUDE_CODE_DISABLE_ADVISOR_TOOL": "1"])
        let both = try ProviderService.arguments(request(advisor: RunSettings.advisorOff, effort: "high"), pluginDirectory: plugin)
        #expect(try environment(both) == ["CLAUDE_CODE_DISABLE_ADVISOR_TOOL": "1", "CLAUDE_CODE_EFFORT_LEVEL": "high"])
    }

    @Test func anotherAgentNeverReadsItAndAValueThatIsNoModelIsRefused() throws {
        // A pane moved to another agent keeps the choice for when it comes back; that agent's arguments have none of it.
        let codex = try ProviderService.arguments(request(advisor: "opus", provider: "codex", mode: "acceptEdits"), pluginDirectory: plugin)
        #expect(!codex.contains("--advisor") && !codex.joined(separator: " ").contains("ADVISOR"))
        #expect(throws: (any Error).self) { try ProviderService.arguments(request(advisor: "two words"), pluginDirectory: plugin) }
        #expect(throws: (any Error).self) { try CoreValidation.validate(request(advisor: "--dangerous")) }
    }

    @Test func theSettingIsSavedOnlyWhenItIsSetAndOlderStateDecodes() throws {
        let plain = String(decoding: try JSONEncoder().encode(RunSettings()), as: UTF8.self)
        #expect(!plain.contains("advisor"))
        let old = try JSONDecoder().decode(RunSettings.self, from: Data(#"{"effort":"high","permissionMode":"plan","maxTurns":null,"maxBudgetUsd":null}"#.utf8))
        #expect(old.advisor == nil && old.effort == "high")
        for value in ["opus", RunSettings.advisorOff] {
            let saved = try JSONEncoder().encode(RunSettings(advisor: value))
            #expect(try JSONDecoder().decode(RunSettings.self, from: saved).advisor == value)
        }
        // An empty or malformed value is no choice.
        #expect(try JSONDecoder().decode(RunSettings.self, from: Data(#"{"advisor":""}"#.utf8)).advisor == nil)
        #expect(try JSONDecoder().decode(RunSettings.self, from: Data(#"{"advisor":7}"#.utf8)).advisor == nil)
        // The pane keeps it with its other settings.
        var session = RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude")
        session.settings.advisor = "opus"
        #expect(try JSONDecoder().decode(RunSession.self, from: JSONEncoder().encode(session)).settings.advisor == "opus")
        // Saving a pane's settings and restoring a saved pane keep a Claude pane's choice, and no other agent's.
        for value in ["opus", RunSettings.advisorOff, "claude-opus-5-5[1m]"] {
            #expect(ProviderOptions.normalizedSettings(provider: "claude", settings: RunSettings(advisor: value)).advisor == value)
        }
        #expect(ProviderOptions.normalizedSettings(provider: "claude", settings: RunSettings()).advisor == nil)
        #expect(ProviderOptions.normalizedSettings(provider: "claude", settings: RunSettings(advisor: "two words")).advisor == nil)
        #expect(ProviderOptions.normalizedSettings(provider: "codex", settings: RunSettings(advisor: "opus")).advisor == nil)
    }

    // MARK: Claude's settings files

    @Test func aFileSaysAModelOffOrNothing() {
        func entry(_ text: String) -> ClaudeAdvisorSettings.Entry { ClaudeAdvisorSettings.entry(Data(text.utf8)) }
        #expect(entry(#"{"advisorModel":"opus"}"#) == .init(model: .model("opus")))
        #expect(entry(#"{"model":"sonnet","advisorModel":"claude-opus-5-5[1m]","permissions":{"allow":[]}}"#).model == .model("claude-opus-5-5[1m]"))
        // Off in the words Claude's own command uses, and anything that names no model.
        #expect(entry(#"{"advisorModel":"off"}"#).model == .off)
        #expect(entry(#"{"advisorModel":"OFF"}"#).model == .off)
        #expect(entry(#"{"advisorModel":""}"#).model == .off)
        #expect(entry(#"{"advisorModel":null}"#).model == .off)
        #expect(entry(#"{"advisorModel":false}"#).model == .off)
        #expect(entry(#"{"advisorModel":"two words"}"#).model == .off)
        // No key, no say; and a file that is no JSON object says nothing.
        #expect(entry(#"{"model":"sonnet"}"#) == .init())
        #expect(entry("[1,2]") == .init())
        #expect(entry("{ not json") == .init())
        #expect(entry("") == .init())
        // The disable variable in the file's env.
        #expect(entry(#"{"advisorModel":"opus","env":{"CLAUDE_CODE_DISABLE_ADVISOR_TOOL":"1"}}"#) == .init(model: .model("opus"), disabled: true))
        #expect(entry(#"{"env":{"CLAUDE_CODE_DISABLE_ADVISOR_TOOL":"0"}}"#) == .init(disabled: false))
        #expect(entry(#"{"env":{"OTHER":"1"}}"#) == .init())
        for value in ["1", "true", "TRUE", " yes ", "on"] { #expect(ClaudeAdvisorSettings.truthy(value)) }
        for value in ["0", "false", "", "no", "off"] { #expect(!ClaudeAdvisorSettings.truthy(value)) }
        #expect(ClaudeAdvisorSettings.truthy(true) && ClaudeAdvisorSettings.truthy(1) && !ClaudeAdvisorSettings.truthy(nil) && !ClaudeAdvisorSettings.truthy(0))
    }

    @Test func aLaterFileWinsAsClaudeReadsThem() {
        typealias Entry = ClaudeAdvisorSettings.Entry
        #expect(ClaudeAdvisorSettings.resolve([]) == nil)
        #expect(ClaudeAdvisorSettings.resolve([Entry(model: .model("opus"))]) == "opus")
        // The project's file over the user's, the local file over the project's; a file without the key changes nothing.
        #expect(ClaudeAdvisorSettings.resolve([Entry(model: .model("opus")), Entry(model: .model("sonnet")), Entry()]) == "sonnet")
        #expect(ClaudeAdvisorSettings.resolve([Entry(model: .model("opus")), Entry(), Entry(model: .off)]) == nil)
        #expect(ClaudeAdvisorSettings.resolve([Entry(model: .off), Entry(model: .model("opus"))]) == "opus")
        // The disable variable turns it off whatever names a model, until a later file sets it back.
        #expect(ClaudeAdvisorSettings.resolve([Entry(model: .model("opus"), disabled: true)]) == nil)
        #expect(ClaudeAdvisorSettings.resolve([Entry(disabled: true), Entry(model: .model("opus"))]) == nil)
        #expect(ClaudeAdvisorSettings.resolve([Entry(model: .model("opus"), disabled: true), Entry(disabled: false)]) == "opus")
    }

    @Test func theFilesAreTheUsersThenTheFoldersTwoAndAreReadOnly() throws {
        let home = try folder(), workspace = try folder()
        defer { try? FileManager.default.removeItem(at: home); try? FileManager.default.removeItem(at: workspace) }
        let files = ClaudeAdvisorSettings.files(workspace: workspace, configDirectory: home)
        #expect(files.map(\.path) == [home.appendingPathComponent("settings.json").path, workspace.appendingPathComponent(".claude/settings.json").path,
                                     workspace.appendingPathComponent(".claude/settings.local.json").path])
        #expect(ClaudeAdvisorSettings.files(workspace: nil, configDirectory: home).count == 1)
        let cache = ClaudeAdvisorSettings.Cache()
        func model() -> String? { ClaudeAdvisorSettings.model(workspace: workspace, configDirectory: home, cache: cache) }
        // No file at all.
        #expect(model() == nil)
        try write(#"{"advisorModel":"opus"}"#, to: files[0])
        #expect(model() == "opus")
        try write(#"{"advisorModel":"sonnet"}"#, to: files[1])
        #expect(model() == "sonnet")
        try write(#"{"advisorModel":"off"}"#, to: files[2])
        #expect(model() == nil)
        // Without a folder only the user's file is read.
        #expect(ClaudeAdvisorSettings.model(workspace: nil, configDirectory: home, cache: cache) == "opus")
        // A file that changed is read again; one that went away says nothing any more.
        try write(#"{"advisorModel":"claude-opus-5-5","note":"changed"}"#, to: files[2])
        #expect(model() == "claude-opus-5-5")
        try FileManager.default.removeItem(at: files[2])
        #expect(model() == "sonnet")
        // A file too large to be a settings file, and a folder in a file's place, say nothing.
        try write("{\"advisorModel\":\"opus\",\"pad\":\"" + String(repeating: "x", count: ClaudeAdvisorSettings.maximumBytes) + "\"}", to: files[1])
        #expect(model() == "opus")
        try FileManager.default.removeItem(at: files[1])
        try FileManager.default.createDirectory(at: files[1], withIntermediateDirectories: true)
        #expect(model() == "opus")
        // Nothing was written: the user's file is as it was.
        #expect(try String(contentsOf: files[0], encoding: .utf8) == #"{"advisorModel":"opus"}"#)
    }

    // MARK: What a request starts with

    @Test func theAdvisorARequestStartsWithIsThePanesChoiceElseClaudesSettings() {
        var read = 0
        func settings(_ value: String?) -> () -> String? { { read += 1; return value } }
        // The pane's own model: Claude's settings are not even read.
        #expect(ClaudeAdvisorSettings.configured(pane: "opus", settings: settings("sonnet")) == "opus")
        #expect(ClaudeAdvisorSettings.configured(pane: RunSettings.advisorOff, settings: settings("sonnet")) == nil)
        #expect(read == 0)
        // A pane that follows them.
        #expect(ClaudeAdvisorSettings.configured(pane: nil, settings: settings("sonnet")) == "sonnet")
        #expect(ClaudeAdvisorSettings.configured(pane: nil, settings: settings(nil)) == nil)
        #expect(read == 2)
        // The run's own environment turns it off whatever is named.
        #expect(ClaudeAdvisorSettings.configured(pane: "opus", environmentDisables: true, settings: settings("sonnet")) == nil)
        #expect(ClaudeAdvisorSettings.configured(pane: nil, environmentDisables: true, settings: settings("sonnet")) == nil)
        #expect(ClaudeAdvisorSettings.configured(pane: "two words", settings: settings("sonnet")) == nil)
    }

    @Test func aPanesRequestCarriesItsAdvisorAndALaterReadOfClaudesSettingsCorrectsIt() {
        var session = RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude")
        session.settings.advisor = "opus"
        session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "r1", kind: "user", text: "go", provider: "claude")))
        #expect(session.graphRuns?.last?.configuredAdvisor == "opus")
        session.settings.advisor = RunSettings.advisorOff
        session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "r2", kind: "user", text: "again", provider: "claude")))
        #expect(session.graphRuns?.last?.configuredAdvisor == nil)
        // A pane that follows Claude's settings learns what they say once they are read.
        session.settings.advisor = nil
        session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "r3", kind: "user", text: "third", provider: "claude")))
        #expect(session.graphRuns?.last?.configuredAdvisor == nil)
        session.noteGraphRunAdvisor("sonnet", runID: "r3")
        #expect(session.graphRuns?.map(\.configuredAdvisor) == ["opus", nil, "sonnet"])
        session.noteGraphRunAdvisor(nil, runID: "r1")
        session.noteGraphRunAdvisor("not a model", runID: "r3")
        session.noteGraphRunAdvisor("opus", runID: "unknown")
        #expect(session.graphRuns?.map(\.configuredAdvisor) == [nil, nil, nil])
    }
}
