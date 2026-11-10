import Foundation
import Testing
@testable import MightyCore

private final class PiRecorder<T: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [T] = []
    func append(_ item: T) { lock.lock(); items.append(item); lock.unlock() }
    func values() -> [T] { lock.lock(); defer { lock.unlock() }; return items }
}

/// pi (`pi --mode rpc`) as the fourth agent: the recorded RPC output in
/// native/contracts/fixtures/pi-rpc-*.jsonl (Windows reads the same files), the argv a
/// permission becomes, the model list, the sign-in reading, the setup item and the
/// session list.
struct PiTests {
    static let root: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url
    }()
    static let sessionID = "0197c1d2-7f3a-7c4e-9b1a-5d2e8f6a4b30"

    static func fixture(_ name: String) throws -> [Data] {
        let data = try Data(contentsOf: root.appendingPathComponent("native/contracts/fixtures/pi-rpc-\(name).jsonl"))
        return data.split(separator: 10).map { Data($0) }
    }

    /// What a parser did with a fixture.
    struct Replay {
        var logs: [(kind: String, text: String)] = []
        var activities: [AgentActivity] = []
        var resumes: [String] = []
        var usage: [SessionUsage] = []
        var graph: [ExecutionGraphNode] = []
        var replies: [Data] = []
        var failed = false
        var turnOpenAfterEach: [Bool] = []
    }

    static func replay(_ name: String) throws -> Replay {
        var result = Replay()
        let box = PiRecorder<Data>()
        let parser = CLIStreamParser(provider: "pi", log: { result.logs.append(($0, $1)) }, resume: { result.resumes.append($0) }, activityNamespace: "run-pi",
                                     activity: { result.activities.append($0) }, usage: { result.usage.append($0) }, graph: { result.graph.append($0) }, graphInput: "go",
                                     piReply: { box.append($0) })
        for line in try fixture(name) {
            var data = line; data.append(10)
            parser.push(data)
            result.turnOpenAfterEach.append(parser.piTurnOpen)
        }
        parser.flush(); parser.finishActivities(stopped: false); parser.finishGraph(state: "completed")
        result.failed = parser.failed; result.replies = box.values()
        return result
    }

    // MARK: Events to the log and the graph

    @Test func anAnswerIsLoggedOnceWhateverTheDeltasAndTheEndOfTheMessage() throws {
        let run = try Self.replay("answer")
        #expect(run.logs.map(\.kind) == ["assistant"])
        #expect(run.logs.first?.text == "Hello! How can I help with this project?")
        // The thinking text is not copied into the transcript.
        #expect(!run.logs.contains { $0.text.contains("greets") })
        #expect(run.resumes == [Self.sessionID])
        #expect(!run.failed)
        // Open from agent_start to agent_end.
        #expect(run.turnOpenAfterEach.contains(true) && run.turnOpenAfterEach.last == false)
        let main = try #require(run.graph.last { $0.kind == "main" })
        #expect(main.state == "completed" && main.output == "Hello! How can I help with this project?")
        #expect(main.usage == GraphTokenUsage(inputTokens: 1203, outputTokens: 18))
    }

    @Test func toolsAreRowsThatSettleByTheirCallIdAndTheSecondMessageIsLoggedToo() throws {
        let run = try Self.replay("tools")
        #expect(run.logs.filter { $0.kind == "assistant" }.map(\.text) == ["Let me look at the entry point and run the tests.",
                                                                          "The edit did not apply, but the read shows `main` returns 1 and one test fails."])
        let read = run.activities.filter { $0.toolName == "read" }
        #expect(read.map(\.state) == ["running", "completed"] && read.last?.kind == "read" && read.last?.summary == "src/main.ts")
        #expect(read.last?.output?.contains("export function main()") == true)
        let bash = run.activities.filter { $0.toolName == "bash" }
        #expect(bash.map(\.state) == ["running", "running", "completed"] && bash.first?.kind == "command" && bash.first?.summary == "npm test")
        #expect(bash.last?.output?.contains("1 failed | 3 passed") == true)
        let edit = run.activities.filter { $0.toolName == "edit" }
        #expect(edit.map(\.state) == ["running", "error"] && edit.last?.kind == "edit" && edit.last?.output == "Could not find the exact text in src/main.ts.")
        #expect(!run.failed)
    }

    @Test func usageAddsEachMessageWithCachedInputInsideInput() throws {
        let run = try Self.replay("tools")
        let usage = try #require(run.usage.last)
        #expect(usage.provider == "pi" && usage.tokenScope == "run" && usage.source == "pi.rpc")
        // input + cacheRead + cacheWrite of the three messages
        #expect(usage.inputTokens == (2410 + 1800) + (3120 + 1800) + (3380 + 1800))
        #expect(usage.outputTokens == 96 + 64 + 41)
        #expect(usage.cacheReadTokens == 3600 && usage.cacheWriteTokens == 1800)
        #expect(usage.totalTokens == (usage.inputTokens ?? 0) + (usage.outputTokens ?? 0))
        #expect(usage.providerSessionId == Self.sessionID && usage.model == "claude-sonnet-4-5")
        #expect(usage.contextWindowTokens == 200_000 && usage.contextUsedTokens == 3380 + 1800)
        #expect((usage.costUSD ?? 0) > 0 && usage.costScope == "run")
        #expect(SessionUsageSupport.normalized(usage) != nil)
        let main = try #require(run.graph.last { $0.kind == "main" })
        #expect(main.usage?.cacheReadTokens == 3600 && main.usage?.outputTokens == 201)
        #expect(main.output == "The edit did not apply, but the read shows `main` returns 1 and one test fails.")
    }

    @Test func aRetriedErrorIsNotTheRunsFailureAndKeepsTheTurnOpen() throws {
        let run = try Self.replay("retry")
        #expect(!run.failed)
        #expect(!run.logs.contains { $0.kind == "error" })
        let overloaded = "529 {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}"
        #expect(run.logs.contains { $0.kind == "system" && $0.text == L("run.notice.piRetry", ["attempt": "1", "max": "3", "error": ActivitySupport.clean(overloaded, maximumBytes: 512, singleLine: true)]) })
        #expect(run.logs.filter { $0.kind == "assistant" }.map(\.text) == ["Done: the summary is below."])
        // agent_end of the failed attempt: the retry announced right after reopens the turn.
        let lines = try Self.fixture("retry")
        let ends = lines.indices.filter { String(decoding: lines[$0], as: UTF8.self).contains("\"type\":\"agent_end\"") }
        #expect(ends.count == 2)
        #expect(run.turnOpenAfterEach[ends[0]] == false && run.turnOpenAfterEach[ends[0] + 1] == true)
        #expect(run.turnOpenAfterEach[ends[1]] == false && run.turnOpenAfterEach[ends[1] + 1] == false)
        // The compaction is a line of its own, and holds the turn while it runs.
        #expect(run.logs.contains { $0.kind == "system" && $0.text.hasPrefix(ContextCompaction.title) && $0.text.contains("88,320") })
        let start = try #require(lines.firstIndex { String(decoding: $0, as: UTF8.self).contains("compaction_start") })
        #expect(run.turnOpenAfterEach[start] == true && run.turnOpenAfterEach.last == false)
    }

    @Test func anErrorNoRetryAnsweredFailsTheRunAndADialogIsCancelled() throws {
        let run = try Self.replay("error")
        #expect(run.failed)
        let errors = run.logs.filter { $0.kind == "error" }.map(\.text)
        #expect(errors.count == 1 && errors[0].contains("authentication_error"))
        // The confirm is answered with a cancel; the notify needs no answer.
        #expect(run.replies.count == 1)
        let reply = try #require(JSONSerialization.jsonObject(with: run.replies[0]) as? [String: Any])
        #expect(reply["type"] as? String == "extension_ui_response" && reply["id"] as? String == "d7a0f1e2-0000-4000-8000-000000000001" && reply["cancelled"] as? Bool == true)
        #expect(String(decoding: run.replies[0], as: UTF8.self).hasSuffix("\n"))
        #expect(run.logs.contains { $0.kind == "system" && $0.text == L("run.notice.piDialogDismissed", ["title": "Allow dangerous command?"]) })
    }

    @Test func aRejectedCommandFailsTheRun() throws {
        let run = try Self.replay("rejected")
        #expect(run.failed && run.logs.contains { $0.kind == "error" && $0.text.contains("already processing") })
    }

    @Test func theGraphTrackerIsOnlyForTheProvidersThatProduceAGraph() {
        #expect(MightyGraphSupport.providers.contains("pi"))
        #expect(SessionHistory.providers == ["claude", "codex", "gemini"])
    }

    // MARK: Permission and settings to argv

    private func request(_ mode: String = "manual", model: String = "default", effort: String = "default", resume: String? = nil) -> StartRunRequest {
        StartRunRequest(sessionId: "pane", workspaceId: "w", input: "hi", model: model, provider: "pi",
                        settings: RunSettings(effort: effort, permissionMode: mode), resumeId: resume)
    }

    @Test func permissionsBecomeTheToolAllowlist() throws {
        let plugin = URL(fileURLWithPath: "/tmp")
        #expect(try ProviderService.arguments(request("manual"), pluginDirectory: plugin) == ["--mode", "rpc", "--tools", "read,grep,find,ls"])
        #expect(try ProviderService.arguments(request("fullAccess"), pluginDirectory: plugin) == ["--mode", "rpc"])
        #expect(try ProviderService.arguments(request("manual", model: "anthropic/claude-sonnet-4-5", effort: "high", resume: Self.sessionID), pluginDirectory: plugin)
                == ["--mode", "rpc", "--model", "anthropic/claude-sonnet-4-5", "--thinking", "high", "--tools", "read,grep,find,ls", "--session", Self.sessionID])
        #expect(try ProviderService.arguments(request("fullAccess", effort: "off"), pluginDirectory: plugin) == ["--mode", "rpc", "--thinking", "off"])
        // The two modes pi has; the others are not its.
        #expect(ProviderOptions.permissionModes(provider: "pi") == ["manual", "fullAccess"])
        #expect(ProviderOptions.normalizedSettings(provider: "pi", settings: RunSettings(effort: "minimal", permissionMode: "acceptEdits")).permissionMode == "manual")
        #expect(ProviderOptions.normalizedSettings(provider: "pi", settings: RunSettings(effort: "minimal", permissionMode: "fullAccess")).effort == "minimal")
        #expect(ProviderOptions.normalizedSettings(provider: "claude", settings: RunSettings(effort: "minimal")).effort == "default")
    }

    @Test func thinkingLevelsAreTheEffortAndAModelWithoutThinkingHasNone() throws {
        #expect(ProviderOptions.effortLevels(provider: "pi", model: "default", registeredModels: []) == ["off", "minimal", "low", "medium", "high", "xhigh"])
        let catalog = ProviderService.normalizePiCatalog(Self.modelList)
        #expect(ProviderOptions.effortLevels(provider: "pi", model: "openai/gpt-4o", catalog: catalog, registeredModels: []) == [])
        #expect(ProviderOptions.effortLevels(provider: "pi", model: "anthropic/claude-sonnet-4-5", catalog: catalog, registeredModels: []).count == 6)
        try CoreValidation.validate(request("manual", effort: "minimal"))
        #expect(throws: (any Error).self) { try CoreValidation.validate(StartRunRequest(sessionId: "p", workspaceId: "w", input: "x", provider: "pi", settings: RunSettings(effort: "max"))) }
        #expect(throws: (any Error).self) { try CoreValidation.validateSelection(request("manual", model: "openai/gpt-4o", effort: "high"), catalog: catalog, registeredModels: []) }
    }

    @Test func theRpcStdinIsGetStateThenThePromptAndASteerIsOneLine() throws {
        let lines = String(decoding: try ProviderInput.piStartup("fix \"it\" / now"), as: UTF8.self).split(separator: "\n").map(String.init)
        #expect(lines.count == 2 && lines[0] == #"{"type":"get_state"}"#)
        let prompt = try #require(JSONSerialization.jsonObject(with: Data(lines[1].utf8)) as? [String: Any])
        #expect(prompt["type"] as? String == "prompt" && prompt["message"] as? String == "fix \"it\" / now" && prompt["images"] == nil)
        let withImage = String(decoding: try ProviderInput.piStartup("look", images: [["type": "image", "data": "AAAA", "mimeType": "image/png"]]), as: UTF8.self)
        #expect(withImage.contains(#""images":[{"data":"AAAA","mimeType":"image\/png","type":"image"}]"#) || withImage.contains(#""mimeType":"image/png""#))
        #expect(String(decoding: try ProviderInput.piSteer("stop and do this"), as: UTF8.self) == #"{"message":"stop and do this","type":"steer"}"# + "\n")
    }

    // MARK: Model list

    static let modelList = """
    provider   model                      context  max-out  thinking  images
    anthropic  claude-sonnet-4-5          200K     64K      yes       yes
    anthropic  claude-opus-4-1            200K     32K      yes       yes
    openai     gpt-4o                     128K     16.4K    no        yes
    openrouter anthropic/claude-3.5-haiku 200K     8.2K     no        yes

    """

    @Test func theModelListBecomesProviderSlashModelRows() throws {
        let catalog = ProviderService.normalizePiCatalog(Self.modelList)
        #expect(catalog.source == "cli")
        #expect(catalog.models.map(\.value) == ["default", "anthropic/claude-sonnet-4-5", "anthropic/claude-opus-4-1", "openai/gpt-4o", "openrouter/anthropic/claude-3.5-haiku"])
        #expect(catalog.models[0].displayName == L("provider.fallback.defaultLabel", ["name": "pi"]))
        #expect(catalog.models[1].supportsEffort == true && catalog.models[3].supportsEffort == false)
        #expect(catalog.models[3].description == L("provider.catalog.piModelRow", ["context": "128K", "output": "16.4K"]))
        for model in catalog.models { #expect(CoreValidation.model(model.value)) }
    }

    @Test func withoutASignInTheListIsEmptyAndTheFallbackIsJustPisOwnDefault() {
        let text = "No models available. Use /login to log into a provider via OAuth or API key. See:\n  /x/docs/providers.md\n  /x/docs/models.md\n"
        #expect(ProviderService.normalizePiCatalog(text) == ProviderOptions.fallbackCatalog("pi"))
        #expect(ProviderService.normalizePiCatalog("").models.map(\.value) == ["default"])
        #expect(ProviderOptions.fallbackCatalog("pi").models.map(\.value) == ["default"])
        #expect(ProviderOptions.label("pi") == "pi" && ProviderOptions.executable("pi") == "pi" && ProviderOptions.ids.firstIndex(of: "pi") == 3)
    }

    // MARK: Sign-in

    private func temporary() throws -> URL {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-pi-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }
    private func writeAuth(_ home: URL, _ json: String) throws {
        let folder = home.appendingPathComponent(".pi/agent")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data(json.utf8).write(to: folder.appendingPathComponent("auth.json"))
    }
    private let noModels = "No models available. Use /login to log into a provider via OAuth or API key. See:\n"

    @Test func noModelsReadsSignedOutWhateverElseIsAround() throws {
        let status = CLIAccountSupport.piStatus(exitCode: 0, output: noModels, authJSON: nil, environment: [:])
        #expect(status.loggedIn == false && status.provider == "pi" && !status.canSignOut)
        #expect(CLIAccountSupport.piStatus(exitCode: 0, output: noModels, authJSON: Data(#"{"anthropic":{"type":"api_key","key":"x"}}"#.utf8), environment: ["OPENAI_API_KEY": "k"]).loggedIn == false)
    }

    @Test func credentialsInAuthJsonAndEnvironmentKeysSignPiIn() throws {
        let listed = Self.modelList
        let subscription = CLIAccountSupport.piStatus(exitCode: 0, output: listed, authJSON: Data(#"{"anthropic":{"type":"oauth","access":"a","refresh":"r","expires":1}}"#.utf8), environment: [:])
        #expect(subscription.loggedIn == true && subscription.methodId == CLIAccountMethod.piSubscription && subscription.account == "anthropic" && !subscription.canSignOut)
        let key = CLIAccountSupport.piStatus(exitCode: 0, output: listed, authJSON: Data(#"{"openai":{"type":"api_key","key":"sk"},"google":{"type":"api_key","key":"g"}}"#.utf8), environment: [:])
        #expect(key.loggedIn == true && key.methodId == CLIAccountMethod.piAPIKey && key.account == "google, openai")
        // An empty auth file has no credential; the environment's key is then the sign-in.
        let environment = CLIAccountSupport.piStatus(exitCode: 0, output: listed, authJSON: Data("{}".utf8), environment: ["GEMINI_API_KEY": "k"])
        #expect(environment.loggedIn == true && environment.methodId == CLIAccountMethod.piAPIKey && environment.account == nil)
        // Models from models.json alone still make it usable.
        #expect(CLIAccountSupport.piStatus(exitCode: 0, output: listed, authJSON: nil, environment: [:]).methodId == CLIAccountMethod.piModels)
        // A failed or timed-out call is unknown, not signed out.
        #expect(CLIAccountSupport.piStatus(exitCode: 1, output: "boom", authJSON: nil, environment: [:]).loggedIn == nil)
        #expect(CLIAccountSupport.piStatus(exitCode: -1, output: "", authJSON: nil, environment: [:]).loggedIn == nil)
    }

    @Test func authFileAndEnvironmentAreReadFromTheHomeAndNeverShown() throws {
        let home = try temporary(); defer { try? FileManager.default.removeItem(at: home) }
        #expect(PiAgent.credentials(authJSON: CLIAccountSupport.boundedData(PiAgent.authFile(home: home, environment: [:]))).providers.isEmpty)
        try writeAuth(home, #"{"anthropic":{"type":"api_key","key":"sk-ant-SECRET"},"note":"text"}"#)
        let credentials = PiAgent.credentials(authJSON: CLIAccountSupport.boundedData(PiAgent.authFile(home: home, environment: [:])))
        #expect(credentials.providers == ["anthropic"] && !credentials.subscription)
        let status = CLIAccountSupport.piStatus(exitCode: 0, output: Self.modelList, authJSON: CLIAccountSupport.boundedData(PiAgent.authFile(home: home, environment: [:])), environment: [:])
        #expect(!(status.summary + status.detail + (status.account ?? "")).contains("SECRET"))
        #expect(PiAgent.authFile(home: home, environment: ["PI_CODING_AGENT_DIR": "/elsewhere/agent"]).path == "/elsewhere/agent/auth.json")
        #expect(PiAgent.environmentKeys(["ANTHROPIC_API_KEY": "", "XAI_API_KEY": "x", "PATH": "/bin"]) == ["XAI_API_KEY"])
        #expect(CLIAccountSupport.loginCommand(provider: "pi") == "pi" && CLIAccountSupport.statusArguments(provider: "pi") == ["pi", "--list-models"] && CLIAccountSupport.logoutArguments(provider: "pi") == nil)
    }

    @Test func theAccountServiceAsksPiForItsModelsWithTheHomesAuthFile() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let bin = directory.appendingPathComponent("bin"); try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let home = directory.appendingPathComponent("home"); try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        let script = bin.appendingPathComponent("pi")
        let listing = Self.modelList.replacingOccurrences(of: "'", with: "")
        try Data("#!/bin/sh\nif [ -f \"$PI_FIXTURE_EMPTY\" ]; then echo 'No models available. Use /login'; exit 0; fi\ncat <<'EOF'\n\(listing)EOF\n".utf8).write(to: script)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: script.path)
        let empty = directory.appendingPathComponent("empty")
        let environment = ["PATH": bin.path + ":/usr/bin:/bin", "PI_FIXTURE_EMPTY": empty.path]
        let service = CLIAccountService(environment: environment, home: home)
        try writeAuth(home, #"{"github-copilot":{"type":"oauth","refresh":"r","access":"a","expires":1}}"#)
        let signedIn = await service.status(provider: "pi")
        #expect(signedIn.installed && signedIn.loggedIn == true && signedIn.methodId == CLIAccountMethod.piSubscription && signedIn.account == "github-copilot")
        try Data().write(to: empty)
        #expect(await service.status(provider: "pi").loggedIn == false)
        let missing = await CLIAccountService(environment: ["PATH": "/usr/bin:/bin"], home: home).status(provider: "pi")
        #expect(!missing.installed)
    }

    // MARK: Setup

    @Test func theSetupItemInstallsPiWithNpmAfterNodeAndThenSignsIn() throws {
        let catalog = try SetupCatalogDecoder.decode(Data(contentsOf: Self.root.appendingPathComponent("native/contracts/fixtures/setup-catalog.json")))
        let item = try #require(catalog.item("pi"))
        #expect(item.group == .agent && !item.required && item.provider == "pi")
        #expect(item.labelKey == "setup.item.pi.title" && item.descriptionKey == "setup.item.pi.detail")
        for platform in [SetupPlatform.macos, .windows] {
            let entry = try #require(item.platforms[platform])
            #expect(entry.probe == .executable(name: "pi", minimumVersion: nil))
            #expect(entry.install == .npmGlobal("@mariozechner/pi-coding-agent"))
            #expect(entry.prerequisites == ["node"])
            #expect(entry.source == URL(string: "https://github.com/badlogic/pi-mono"))
        }
        // From a Mac with nothing: Homebrew first (it needs Git), Node.js, then pi.
        let order = SetupPlan.order(selected: ["pi"], catalog: catalog, platform: .macos, isSatisfied: { $0 == "git" ? true : false })
        #expect(order == ["homebrew", "node", "pi"])
        #expect(SetupPlan.order(selected: ["pi"], catalog: catalog, platform: .macos, isSatisfied: { _ in true }) == ["pi"])
        #expect(ComponentCatalog.installCommand(provider: "pi") == "npm install -g @mariozechner/pi-coding-agent")
    }

    // MARK: Session list

    @Test func sessionsOfTheWorkspaceFolderAreListedWithTheirFirstRequest() throws {
        let home = try temporary(); defer { try? FileManager.default.removeItem(at: home) }
        let workspace = home.appendingPathComponent("project"); try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        let real = URL(fileURLWithPath: workspace.path).resolvingSymlinksInPath().path
        let folder = home.appendingPathComponent(".pi/agent/sessions/" + PiAgent.sessionFolder(real))
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let id = "0197c1d2-7f3a-7c4e-9b1a-5d2e8f6a4b30", other = "0197c1d2-aaaa-7c4e-9b1a-5d2e8f6a4b31", empty = "0197c1d2-bbbb-7c4e-9b1a-5d2e8f6a4b32"
        let lines = [
            #"{"type":"session","version":3,"id":"\#(id)","timestamp":"2026-11-10T09:30:00.000Z","cwd":"\#(real)"}"#,
            #"{"type":"model_change","id":"a1b2c3d4","parentId":null,"timestamp":"2026-11-10T09:30:00.100Z","provider":"anthropic","modelId":"claude-sonnet-4-5"}"#,
            #"{"type":"message","id":"b2c3d4e5","parentId":"a1b2c3d4","timestamp":"2026-11-10T09:30:01.000Z","message":{"role":"user","content":[{"type":"text","text":"  Fix the\nfailing test  "}],"timestamp":1762767001000}}"#,
            #"{"type":"message","id":"c3d4e5f6","parentId":"b2c3d4e5","timestamp":"2026-11-10T09:30:05.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Done."}],"provider":"anthropic","model":"claude-sonnet-4-5","stopReason":"stop","timestamp":1762767005000}}"#,
            #"{"type":"message","id":"d4e5f6a7","parentId":"c3d4e5f6","timestamp":"2026-11-10T09:31:00.000Z","message":{"role":"user","content":"And the lint?","timestamp":1762767060000}}"#,
            #"{"type":"message","id":"e5f6a7b8","parentId":"d4e5f6a7","timestamp":"2026-11-10T09:31:09.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Clean."}],"provider":"anthropic","model":"claude-opus-4-1","stopReason":"stop","timestamp":1762767069000}}"#,
        ]
        try Data((lines.joined(separator: "\n") + "\n").utf8).write(to: folder.appendingPathComponent("2026-11-10T09-30-00-000Z_\(id).jsonl"))
        // Another folder's header, and a session nobody wrote a request in, are not listed.
        try Data((lines[0].replacingOccurrences(of: id, with: other).replacingOccurrences(of: real, with: "/somewhere/else") + "\n" + lines[2] + "\n").utf8)
            .write(to: folder.appendingPathComponent("2026-11-09T09-30-00-000Z_\(other).jsonl"))
        try Data((lines[0].replacingOccurrences(of: id, with: empty) + "\n").utf8).write(to: folder.appendingPathComponent("2026-11-08T09-30-00-000Z_\(empty).jsonl"))
        try Data("not a session".utf8).write(to: folder.appendingPathComponent("notes.txt"))

        let listing = ResumableSessions.listing(ResumableSessionQuery(workspacePath: workspace.path, home: home), provider: "pi")
        let item = try #require(listing.items.first)
        #expect(listing.items.count == 1 && listing.hidden == 0)
        #expect(item.provider == "pi" && item.sessionID == id && item.title == "Fix the failing test" && item.requests == 2 && item.model == "claude-opus-4-1")
        #expect(ResumableSessions.listing(ResumableSessionQuery(workspacePath: workspace.path, home: home)).items.map(\.id) == ["pi:" + id])
        #expect(ResumableSessions.providers.contains("pi"))
        #expect(ResumableSessions.listing(ResumableSessionQuery(workspacePath: workspace.path, home: home, excluding: [id.uppercased()]), provider: "pi").items.isEmpty)
        // The session file a pane resumes is found by the id alone.
        #expect(PiAgent.sessionFile(id: id, workspacePath: workspace.path, home: home, environment: [:])?.lastPathComponent == "2026-11-10T09-30-00-000Z_\(id).jsonl")
        #expect(PiAgent.sessionFile(id: "0197c1d2-cccc-7c4e-9b1a-5d2e8f6a4b33", workspacePath: workspace.path, home: home, environment: [:]) == nil)
        #expect(PiAgent.sessionFolder("/Users/dev/my project") == "--Users-dev-my project--")
    }

    // MARK: A real run

    /// A pi that answers on stdin as `pi --mode rpc` does, from a fixture, and exits when stdin ends.
    private func fakePi(_ directory: URL, fixture: String, waitForSteer: Bool = false, hang: Bool = false) throws -> URL {
        let binary = directory.appendingPathComponent("pi")
        let fixtureFile = Self.root.appendingPathComponent("native/contracts/fixtures/pi-rpc-\(fixture).jsonl").path
        let source = """
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf '0.73.1\\n'; exit 0; fi
        printf '%s\\n' "$@" > argv.txt
        while IFS= read -r line; do
          printf '%s\\n' "$line" >> stdin.txt
          case "$line" in
            *'"get_state"'*) sed -n 1p '\(fixtureFile)' ;;
            *'"prompt"'*)
              sed -n 2,4p '\(fixtureFile)'
              \(hang ? "sleep 60" : "")
              \(waitForSteer ? "IFS= read -r steerline; printf '%s\\n' \"$steerline\" >> stdin.txt" : "")
              sed -n '5,$p' '\(fixtureFile)' ;;
          esac
        done
        """
        try Data(source.utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        return binary
    }
    private func wait(_ predicate: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(30)
        while !predicate() {
            guard Date() < deadline else { throw MightyError("Timed out waiting for the pi fixture") }
            try await Task.sleep(for: .milliseconds(20))
        }
    }

    @Test func aRunWritesTheStartupLinesKeepsStdinOpenUntilTheTurnEndsThenCompletes() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let binary = try fakePi(directory, fixture: "answer")
        let service = ProviderService(binaryOverrides: ["pi": binary])
        let events = PiRecorder<RunEvent>()
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        do {
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "hi there", provider: "pi"), workspace: workspace)
            try await wait { events.values().contains { $0.status == "completed" || $0.status == "error" } }
            let all = events.values()
            #expect(all.last { $0.type == "status" }?.status == "completed")
            #expect(all.contains { $0.type == "log" && $0.entry?.kind == "assistant" && $0.entry?.text == "Hello! How can I help with this project?" })
            #expect(all.contains { $0.type == "resume" && $0.resumeId == Self.sessionID })
            #expect(all.contains { $0.type == "usage" && $0.usage?.provider == "pi" && $0.usage?.outputTokens == 18 })
            #expect(all.contains { $0.type == "graph" && $0.graph?.kind == "main" && $0.graph?.state == "completed" })
            let argv = try String(contentsOf: directory.appendingPathComponent("argv.txt"), encoding: .utf8).split(separator: "\n").map(String.init)
            #expect(argv == ["--mode", "rpc", "--tools", "read,grep,find,ls"])
            let stdin = try String(contentsOf: directory.appendingPathComponent("stdin.txt"), encoding: .utf8).split(separator: "\n").map(String.init)
            #expect(stdin.count == 2 && stdin[0] == #"{"type":"get_state"}"# && stdin[1].contains(#""message":"hi there""#))
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }

    @Test func aSteerIsWrittenWhileTheTurnIsOpenAndRefusedBeforeAndAfter() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let binary = try fakePi(directory, fixture: "answer", waitForSteer: true)
        let service = ProviderService(binaryOverrides: ["pi": binary])
        let events = PiRecorder<RunEvent>()
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        do {
            #expect(await runner.steer(sessionId: "pane", text: "nobody home") == false)
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "hi", provider: "pi"), workspace: workspace)
            // agent_start is the first thing after the prompt's response, before the script waits.
            var steered = false
            let deadline = Date().addingTimeInterval(30)
            while !steered, Date() < deadline {
                steered = await runner.steer(sessionId: "pane", text: "also check the lint")
                if !steered { try await Task.sleep(for: .milliseconds(20)) }
            }
            #expect(steered)
            try await wait { events.values().contains { $0.status == "completed" || $0.status == "error" } }
            #expect(events.values().last { $0.type == "status" }?.status == "completed")
            let stdin = try String(contentsOf: directory.appendingPathComponent("stdin.txt"), encoding: .utf8)
            #expect(stdin.contains(#"{"message":"also check the lint","type":"steer"}"#))
            #expect(await runner.steer(sessionId: "pane", text: "too late") == false)
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }

    @Test func stoppingARunEndsItStoppedAndAResumeOfAMissingSessionStartsANewOne() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let binary = try fakePi(directory, fixture: "answer", hang: true)
        let service = ProviderService(binaryOverrides: ["pi": binary])
        let events = PiRecorder<RunEvent>()
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        do {
            // This session was never written: pi would exit on it, so the run drops --session.
            var request = StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "stop me", provider: "pi", settings: RunSettings(permissionMode: "fullAccess"))
            request.resumeId = "0197c1d2-dead-7c4e-9b1a-5d2e8f6a4b34"
            try await runner.start(request: request, workspace: workspace)
            try await wait { FileManager.default.fileExists(atPath: directory.appendingPathComponent("stdin.txt").path) && (try? String(contentsOf: directory.appendingPathComponent("stdin.txt"), encoding: .utf8))?.contains("prompt") == true }
            await runner.stop(id: "pane")
            #expect(events.values().last { $0.type == "status" }?.status == "stopped")
            let argv = try String(contentsOf: directory.appendingPathComponent("argv.txt"), encoding: .utf8).split(separator: "\n").map(String.init)
            #expect(argv == ["--mode", "rpc"])
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }

    // MARK: Not offered where it cannot run yet

    @Test func piIsNeitherADelegationChildNorAParent() {
        #expect(!DelegationProviders.isAgentPane(kind: SessionKind.claude, provider: "pi"))
        #expect(DelegationProviders.humanChildChoices(parent: "claude", parentMode: "acceptEdits", planFirst: false, openHumanChildren: 0, availability: { _ in .ready }).map(\.agent) == ["claude", "codex", "gemini"])
        #expect(!DelegationProviders.mayHaveHumanChild(kind: SessionKind.claude, provider: "pi", mode: "fullAccess"))
    }

    @Test func slashCommandsAndMarkExistForPi() {
        #expect(SlashCommandCatalog.builtins(provider: "pi").contains { $0.invocation == "model" } && SlashCommandCatalog.builtins(provider: "pi").contains { $0.invocation == "new" })
        #expect(ProviderMark.markedProvider("pi") == "pi" && ProviderMark.colors(provider: "pi").count == 1)
        #expect(!ProviderMark.commands(ProviderMark.outline(provider: "pi")).isEmpty && ProviderMark.outline(provider: "pi") != ProviderMark.outline(provider: "claude"))
        #expect(ActivitySupport.kind(tool: "find") == "search" && ActivitySupport.kind(tool: "ls") == "read")
    }
}
