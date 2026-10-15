import Testing
import Foundation
@testable import MightyCore

/// The Mac's `gemini` provider runs Google's Antigravity CLI (`agy`).
struct AntigravityTests {
    /// A real `agy -p … --output-format stream-json --dangerously-skip-permissions`
    /// run (agy 1.3.5), trimmed: the conversation id, paths and tool list are
    /// placeholders, every field the app reads is kept as agy wrote it.
    static let recorded = """
    {"event":"init","conversation_id":"11111111-2222-4333-8444-555555555555","init":{"model":"gemini-3.8-flash-low","cwd":"/workspace/demo","tools":["view_file","run_command","write_to_file"],"permission_mode":"always-proceed"}}
    {"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":0,"state":"DONE","step_type":"user_input"}}
    {"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":1,"state":"DONE","step_type":"agent_response","duration_seconds":2.916978,"usage":{"input_tokens":11835,"output_tokens":45,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":11880}}}
    {"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"view_file","tool_info":{"name":"view_file","parameters":{"AbsolutePath":"/workspace/demo/note.txt"}}}}
    {"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":2,"state":"DONE","step_type":"tool","tool_name":"view_file","duration_seconds":0.04126,"tool_info":{"name":"view_file","parameters":{"AbsolutePath":"/workspace/demo/note.txt"},"output":"2 lines, 6 bytes"}}}
    {"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":3,"state":"DONE","step_type":"agent_response","duration_seconds":2.9536,"usage":{"input_tokens":12065,"output_tokens":62,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":12127}}}
    {"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":4,"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"echo hi"}}}}
    {"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":4,"state":"DONE","step_type":"tool","tool_name":"run_command","duration_seconds":0.078959,"tool_info":{"name":"run_command","parameters":{"CommandLine":"echo hi"},"output":"hi\\r\\n"}}}
    {"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":5,"state":"ACTIVE","step_type":"agent_response","text_delta":"DONE"}}
    {"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":5,"state":"DONE","step_type":"agent_response","text_delta":"\\n","duration_seconds":2.01985,"usage":{"input_tokens":4094,"output_tokens":1,"thinking_tokens":0,"cache_read_tokens":8120,"total_tokens":4095}}}
    {"event":"result","result":{"conversation_id":"11111111-2222-4333-8444-555555555555","status":"SUCCESS","response":"DONE\\n","duration_seconds":8.079104,"num_turns":1,"usage":{"input_tokens":27994,"output_tokens":108,"thinking_tokens":0,"cache_read_tokens":8120,"total_tokens":28102}}}

    """

    /// What agy 1.3.5 wrote for a shell command headless mode could not ask about (manual mode).
    static let softDenied = """
    {"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"echo hi"}}}}
    {"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":2,"state":"ERROR","step_type":"tool","tool_name":"run_command","duration_seconds":0.066979,"tool_info":{"name":"run_command","parameters":{"CommandLine":"echo hi"},"error":{"type":"TOOL_ERROR","message":"permission check failed for unsandboxed \\"echo hi\\": user denied permission to run command"}}}}
    {"event":"result","result":{"conversation_id":"11111111-2222-4333-8444-555555555555","status":"SUCCESS","response":"","num_turns":1,"usage":{"input_tokens":11834,"output_tokens":57,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":11891},"denied_actions":[{"action":"command","display_name":"RunCommand"}]}}

    """

    private final class Recorder {
        var logs: [(String, String)] = []
        var resumes: [String] = []
        var activities: [AgentActivity] = []
        var usage: [SessionUsage] = []
    }

    private func makeParser(_ recorder: Recorder, activity: Bool = true) -> CLIStreamParser {
        var clock = 0.0
        return CLIStreamParser(provider: "gemini", log: { recorder.logs.append(($0, $1)) }, resume: { recorder.resumes.append($0) }, activityNamespace: "agy-run",
                               activity: activity ? { recorder.activities.append($0) } : nil, activityClock: { clock += 0.01; return clock }, usage: { recorder.usage.append($0) })
    }

    @Test func aRecordedRunStreamsTextToolsResumeAndUsage() throws {
        try LocaleOverride.$language.withValue(.en) { () throws in
            let recorder = Recorder(); let parser = makeParser(recorder)
            parser.push(Self.recorded); parser.flush()
            // The deltas make one answer; the result's full response is not logged again.
            #expect(recorder.logs.map { $0.0 } == ["assistant"])
            #expect(recorder.logs.first?.1 == "DONE\n")
            #expect(recorder.resumes == ["11111111-2222-4333-8444-555555555555"])
            #expect(!parser.failed && !parser.authFailure)
            let tools = recorder.activities.filter { $0.kind != "turn" }
            let read = try #require(tools.last { $0.toolName == "view_file" })
            #expect(read.kind == "read" && read.state == "completed" && read.summary == "/workspace/demo/note.txt" && read.output == "2 lines, 6 bytes")
            let command = try #require(tools.last { $0.toolName == "run_command" })
            #expect(command.kind == "command" && command.state == "completed" && command.summary == "echo hi" && command.output == "hi")
            #expect(tools.contains { $0.toolName == "run_command" && $0.state == "running" })
            #expect(recorder.activities.filter { $0.kind == "turn" }.map(\.summary) == ["Antigravity is writing a response", "Antigravity is finishing the response"])
            let usage = try #require(recorder.usage.last)
            #expect(usage.provider == "gemini" && usage.source == "antigravity.stream-json" && usage.tokenScope == "session")
            #expect(usage.model == "gemini-3.8-flash-low" && usage.providerSessionId == "11111111-2222-4333-8444-555555555555")
            // agy counts cached input apart (input 27994 + cache 8120); input here includes it.
            #expect(usage.inputTokens == 36114 && usage.outputTokens == 108 && usage.cacheReadTokens == 8120 && usage.reasoningTokens == 0 && usage.totalTokens == 36222)
        }
    }

    @Test func withoutActivityRowsToolsGoToTheLog() {
        LocaleOverride.$language.withValue(.en) {
            let recorder = Recorder(); let parser = makeParser(recorder, activity: false)
            parser.push(Self.recorded); parser.flush()
            #expect(recorder.logs.map { $0.0 } == ["system", "output", "system", "output", "assistant"])
            #expect(recorder.logs[3].1 == "hi\r\n")
        }
    }

    @Test func aResultWithoutDeltasIsTheAnswerAndDeniedActionsAreNamed() throws {
        try LocaleOverride.$language.withValue(.en) { () throws in
            let recorder = Recorder(); let parser = makeParser(recorder)
            parser.push(#"{"event":"result","result":{"status":"SUCCESS","response":"Only in the result"}}"# + "\n"); parser.flush()
            #expect(recorder.logs.map { $0.1 } == ["Only in the result"])

            let denied = Recorder(); let declined = makeParser(denied)
            declined.push(Self.softDenied); declined.flush()
            // The turn went on and succeeded; the declined command shows as a failed tool and a notice.
            #expect(!declined.failed)
            let command = try #require(denied.activities.last { $0.toolName == "run_command" })
            #expect(command.state == "error" && command.output?.contains("user denied permission") == true)
            #expect(denied.logs.count == 1 && denied.logs[0].0 == "system" && denied.logs[0].1.contains("RunCommand") && denied.logs[0].1.contains("permissions.allow"))
        }
    }

    @Test func failedCancelledAndSignedOutTurnsEndTheirOwnWay() {
        LocaleOverride.$language.withValue(.en) {
            let failed = Recorder(); let error = makeParser(failed)
            error.push(#"{"event":"result","result":{"status":"ERROR","error":{"message":"Quota exceeded"}}}"# + "\n"); error.flush()
            #expect(error.failed && !error.authFailure && failed.logs.last?.0 == "error" && failed.logs.last?.1 == "Quota exceeded")

            let invalid = makeParser(Recorder())
            invalid.push(#"{"event":"result","result":{"status":"INVALID"}}"# + "\n")
            #expect(invalid.failed)

            // agy ending the turn by itself is a failure; a stop the user asked for is the runner's.
            let ended = Recorder(); let cancelled = makeParser(ended)
            cancelled.push(#"{"event":"result","result":{"status":"INTERRUPTED"}}"# + "\n")
            #expect(cancelled.failed && ended.logs.last?.0 == "error" && ended.logs.last?.1.contains("INTERRUPTED") == true)
            let waiting = Recorder(); let unknown = makeParser(waiting)
            unknown.push(#"{"event":"result","result":{"status":"WAITING"}}"# + "\n")
            #expect(!unknown.failed && waiting.logs.last?.0 == "system" && waiting.logs.last?.1.contains("WAITING") == true)

            let signedOut = makeParser(Recorder())
            signedOut.receiveStderr("Error: authentication required. Run 'agy' to log in, then retry.\n")
            #expect(signedOut.authFailure)
            let tail = makeParser(Recorder())
            tail.receiveStderr("Print mode: auth error: token revoked"); #expect(!tail.authFailure)
            tail.finishStderr(); #expect(tail.authFailure)
            let plain = makeParser(Recorder())
            plain.push("Error: authentication required. Run 'agy' to log in, then retry.\n"); #expect(plain.authFailure)
            // The same words as the model's answer, or a soft-deny notice, never count.
            let answer = makeParser(Recorder())
            answer.push(#"{"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"agent_response","text_delta":"authentication required"}}"# + "\n")
            answer.receiveStderr("jetski: no output produced — a tool required the \"command\" permission that headless mode cannot prompt for, so it was auto-denied.\n")
            answer.finishStderr()
            #expect(!answer.authFailure)
            #expect(CLIAuthFailure.antigravity(text: "Print mode: auth error: token expired"))
            // Only agy's own wording: not a timed-out sign-in, a proxy's 407, or the bare phrase.
            for text in ["warning: conversation \"x\" not found", "Print mode: auth timed out", "HTTP 407 Proxy Authentication Required", "authentication required"] {
                #expect(!CLIAuthFailure.antigravity(text: text), "\(text)")
            }
        }
    }

    @Test func argumentsStdinAndAttachments() throws {
        let plugin = URL(fileURLWithPath: "/tmp/plugin")
        var request = StartRunRequest(sessionId: "run", workspaceId: "workspace", input: "--dangerously-skip-permissions /clear", model: "gemini-4-argon-medium", provider: "gemini", settings: RunSettings(permissionMode: "manual"), resumeId: "11111111-2222-4333-8444-555555555555")
        let empty = try AttachmentPreparation([])
        let plain = try ProviderInput.prepare(request, pluginDirectory: plugin, attachments: empty)
        #expect(plain.arguments == ["--print=", "--input-format", "stream-json", "--output-format", "stream-json", "--print-timeout", "0", "--model", "gemini-4-argon-medium", "--conversation", "11111111-2222-4333-8444-555555555555"])
        let line = try #require(String(data: plain.standardInput, encoding: .utf8))
        #expect(line.hasSuffix("\n") && line.filter { $0 == "\n" }.count == 1)
        let object = try #require(try JSONSerialization.jsonObject(with: plain.standardInput) as? [String: Any])
        #expect(object["event"] as? String == "user" && (object["message"] as? [String: Any])?["content"] as? String == request.input)

        let file = try AttachmentSupport.make(name: "notes.txt", data: Data("PURPLE-42".utf8))
        request.attachments = [file]; request.input = "What does it say?"
        let prepared = try AttachmentPreparation(request.attachments); defer { prepared.cleanup() }
        let input = try ProviderInput.prepare(request, pluginDirectory: plugin, attachments: prepared)
        let directory = try #require(prepared.directory)
        #expect(Array(input.arguments.suffix(2)) == ["--add-dir", directory.path])
        let content = try #require((try JSONSerialization.jsonObject(with: input.standardInput) as? [String: Any]).flatMap { ($0["message"] as? [String: Any])?["content"] as? String })
        #expect(content.hasPrefix("What does it say?\n\n") && content.contains(prepared.files[0].url.path))
    }

    @Test func theModelListIsReadFromAgyModels() {
        let catalog = ProviderService.normalizeAntigravityCatalog("Fetching available models...\ngemini-4-argon-medium\tGemini 4 Argon (Medium)\nclaude-opus-5-5-low\tClaude Opus 5.5 (Low)\nnot a row\n--bad\tBad\ngemini-4-argon-medium\tDuplicate\n")
        #expect(catalog.source == "cli")
        #expect(catalog.models.map(\.value) == ["default", "gemini-4-argon-medium", "claude-opus-5-5-low"])
        #expect(catalog.models.map(\.displayName).dropFirst() == ["Gemini 4 Argon (Medium)", "Claude Opus 5.5 (Low)"])
        #expect(ProviderService.normalizeAntigravityCatalog("Fetching available models...\n").source == "fallback")
        let fallback = ProviderOptions.fallbackCatalog("gemini")
        #expect(fallback.models.first?.value == "default" && fallback.models.contains { $0.value == "gemini-3.8-flash-low" && $0.displayName == "Gemini 3.8 Flash (Low)" })
        #expect(ProviderOptions.effortLevels(provider: "gemini", model: "gemini-4-argon-high", registeredModels: []).isEmpty)
        #expect(!ProviderOptions.fallbackRuntime("gemini").capabilities.effort)
        #expect(ProviderOptions.fallbackRuntime("gemini").name == "Antigravity CLI")
    }

    @Test func labelsExecutableAndInstallHint() {
        #expect(ProviderOptions.label("gemini") == "Antigravity" && ProviderOptions.isBeta("gemini"))
        #expect(ProviderOptions.executable("gemini") == "agy" && ProviderOptions.executable("codex") == "codex" && ProviderOptions.executable("claude") == "claude")
        #expect(ComponentCatalog.installCommand(provider: "gemini") == "curl -fsSL https://antigravity.google/cli/install.sh | bash")
        // Mac only: agy slugs carry their effort; the shared fixture ids are unchanged.
        #expect(ModelLabel.text("gemini-4-argon-medium") == "Gemini 4 Argon (Medium)")
        #expect(ModelLabel.text("gemini-3.8-flash-low") == "Gemini 3.8 Flash (Low)")
        #expect(ModelLabel.text("gemini-2.5-flash-lite") == "Gemini 2.5 Flash Lite")
        #expect(ModelLabel.text("claude-opus-5-5-low", catalog: ProviderOptions.fallbackCatalog("gemini")) == "Claude Opus 5.5 (Low)")
        #expect(ModelLabel.text("gpt-oss-120b-medium", catalog: ProviderOptions.fallbackCatalog("gemini")) == "GPT-OSS 120B (Medium)")
        #expect(ActivitySupport.kind(tool: "grep_search") == "search" && ActivitySupport.kind(tool: "write_to_file") == "edit" && ActivitySupport.kind(tool: "browser_click_element") == "web"
                && ActivitySupport.kind(tool: "list_dir") == "read" && ActivitySupport.kind(tool: "invoke_subagent") == "agent" && ActivitySupport.kind(tool: "search_web") == "web")
        #expect(CLIStreamParser.antigravityParameters(["TargetFile": "/a", "SearchPath": "/b"]).map { Set($0.keys) } == ["target_file", "search_path"])
    }

    @Test func statusComesFromTheModelListAndThereIsNoSignOut() {
        LocaleOverride.$language.withValue(.en) {
            let signedIn = CLIAccountSupport.antigravityStatus(exitCode: 0, output: "Fetching available models...\ngemini-3.8-flash-low\tGemini 3.8 Flash (Low)\n")
            #expect(signedIn.loggedIn == true && signedIn.methodId == CLIAccountMethod.geminiGoogle && !signedIn.canSignOut && signedIn.detail.contains("/logout"))
            let signedOut = CLIAccountSupport.antigravityStatus(exitCode: 1, output: "Error: authentication required. Run 'agy' to log in, then retry.\n")
            #expect(signedOut.loggedIn == false && !signedOut.canSignOut)
            let offline = CLIAccountSupport.antigravityStatus(exitCode: 1, output: "dial tcp: lookup failed")
            #expect(offline.loggedIn == nil && !offline.detail.isEmpty)
            #expect(CLIAccountSupport.antigravityStatus(exitCode: -1, output: "").loggedIn == nil)
            #expect(CLIAccountSupport.loginCommand(provider: "gemini") == "agy")
            #expect(CLIAccountSupport.statusArguments(provider: "gemini") == ["agy", "models"])
            #expect(CLIAccountSupport.logoutArguments(provider: "gemini") == nil)
            // A live status: only signed out asks for a sign-in; offline (unknown) never opens one.
            #expect(!CLIAuthFailure.signInCanFix(signedIn) && CLIAuthFailure.signInCanFix(signedOut) && !CLIAuthFailure.signInCanFix(offline))
            #expect(!CLIAuthFailure.signInCanFix(CLIAccountStatus(provider: "gemini", installed: false, loggedIn: false)))
        }
    }

    @Test func aFakeAgyStatusAndUpdateUseTheRealCommands() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-agy-\(UUID().uuidString)")
        // Where agy's installer puts it, under this fixture's home.
        let bin = root.appendingPathComponent(".local/bin"); try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let agy = bin.appendingPathComponent("agy")
        try Data("""
        #!/bin/sh
        folder="$(/usr/bin/dirname "$0")"
        case "$1" in
          --version) if [ -e "$folder/updated" ]; then printf '1.3.6\\n'; else printf '1.3.5\\n'; fi; exit 0;;
          models) if [ -e "$folder/signed-out" ]; then printf "Error: authentication required. Run 'agy' to log in, then retry.\\n" >&2; exit 1; fi
                  printf 'Fetching available models...\\ngemini-3.8-flash-low\\tGemini 3.8 Flash (Low)\\n'; exit 0;;
          update) : > "$folder/updated"; exit 0;;
        esac
        exit 2
        """.utf8).write(to: agy)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: agy.path)
        let environment = ["PATH": bin.path + ":/usr/bin:/bin", "HOME": root.path]
        let accounts = CLIAccountService(environment: environment, home: root)
        #expect(await accounts.status(provider: "gemini").loggedIn == true)
        try Data().write(to: bin.appendingPathComponent("signed-out"))
        #expect(await accounts.status(provider: "gemini").loggedIn == false)
        let unchanged = await accounts.logout(provider: "gemini")
        #expect(unchanged.loggedIn == false && !unchanged.canSignOut)

        let updates = CLIUpdateService(environment: environment, homeDirectory: root)
        let installation = await updates.inspect(provider: "gemini")
        #expect(installation.method == "native" && installation.canUpdate && installation.version == "1.3.5" && installation.executablePath == agy.path)
        let result = await updates.update(provider: "gemini")
        #expect(result.status == "updated" && result.beforeVersion == "1.3.5" && result.afterVersion == "1.3.6")

        let service = ProviderService(environment: environment)
        let command = await service.command(provider: "gemini")
        #expect(command?.executable == agy && command?.version == "1.3.6")
        #expect(await service.modelCatalog(provider: "gemini").source == "fallback") // signed out: the list fails
        try FileManager.default.removeItem(at: bin.appendingPathComponent("signed-out"))
        #expect(await service.modelCatalog(provider: "gemini", forceRefresh: true).models.map(\.value) == ["default", "gemini-3.8-flash-low"])
        await service.shutdown()
    }

    @Test func theTerminalSignInWaitsForTheStatus() async {
        let signedOut = CLIAccountStatus(provider: "gemini", loggedIn: false, methodId: CLIAccountMethod.geminiGoogle, canSignOut: false)
        let signedIn = CLIAccountStatus(provider: "gemini", loggedIn: true, methodId: CLIAccountMethod.geminiGoogle, canSignOut: false)
        let reads = Counter()
        let outcome = await CLIAntigravityLogin.wait(status: { reads.next() >= 3 ? signedIn : signedOut }, isOpen: { true }, interval: 0, limit: 20, tick: 0.01)
        #expect(outcome == .loggedIn(signedIn))
        let closed = await CLIAntigravityLogin.wait(status: { signedOut }, isOpen: { false }, interval: 60, limit: 20, tick: 0.01)
        #expect(closed == .exited(signedOut))
        let expired = await CLIAntigravityLogin.wait(status: { signedOut }, isOpen: { true }, interval: 60, limit: 0.05, tick: 0.01)
        #expect(expired == .timedOut(signedOut))
        let cancelled = Task { await CLIAntigravityLogin.wait(status: { signedOut }, isOpen: { true }, interval: 60, limit: 60, tick: 0.01) }
        cancelled.cancel()
        #expect(await cancelled.value == .cancelled)
    }

    @Test func aSavedGeminiCLIPaneIsMovedToAntigravityOnce() throws {
        try LocaleOverride.$language.withValue(.en) { () throws in
            let workspace = Workspace(id: "space", name: "Fixture", path: "/tmp")
            var legacy = RunSession(id: "legacy", workspaceId: workspace.id, title: "Gemini", provider: "gemini", resumeId: "11111111-2222-4333-8444-555555555555")
            legacy.model = "gemini-2.5-pro"
            var current = RunSession(id: "current", workspaceId: workspace.id, title: "Antigravity", provider: "gemini")
            current.model = "gemini-3.8-flash-low"
            var claude = RunSession(id: "claude", workspaceId: workspace.id, title: "Claude", resumeId: "claude-session")
            claude.model = "opus"
            let saved = try JSONEncoder().encode(AppSnapshot(workspaces: [workspace], sessions: [legacy, current, claude]))
            let migrated = StateRepository.decodeSnapshot(saved)
            #expect(migrated.antigravityMigrated == true)
            let pane = try #require(migrated.sessions.first { $0.id == "legacy" })
            #expect(pane.model == "default" && pane.resumeId == nil)
            #expect(pane.logs.map(\.kind) == ["system", "system"] && pane.logs[0].text.contains("gemini-2.5-pro"))
            #expect(migrated.sessions.first { $0.id == "current" }?.model == "gemini-3.8-flash-low")
            #expect(migrated.sessions.first { $0.id == "claude" }.map { $0.model == "opus" && $0.resumeId == "claude-session" } == true)
            // Once: an agy conversation saved after the move is kept.
            var next = migrated; next.sessions[0].resumeId = "agy-conversation"; next.sessions[0].model = "gemini-4-argon-high"
            let again = StateRepository.decodeSnapshot(try JSONEncoder().encode(next))
            #expect(again.antigravityMigrated == true && again.sessions[0].resumeId == "agy-conversation" && again.sessions[0].model == "gemini-4-argon-high")
            #expect(again.sessions[0].logs.count == 2)
            // agy's live list decides at run time; the fallback list never resets a model.
            let live = ProviderService.normalizeAntigravityCatalog("gemini-4-argon-high\tGemini 4 Argon (High)\n")
            #expect(AntigravityMigration.modelUnavailable("gemini-2.5-pro", catalog: live))
            #expect(!AntigravityMigration.modelUnavailable("gemini-4-argon-high", catalog: live) && !AntigravityMigration.modelUnavailable("default", catalog: live))
            #expect(!AntigravityMigration.modelUnavailable("gemini-9-new", catalog: ProviderOptions.fallbackCatalog("gemini")))
        }
    }

    @Test func aMissingConversationIsNotedOnceAndUnknownEventsAreShown() {
        LocaleOverride.$language.withValue(.en) {
            let notes = Recorder()
            let resumed = CLIStreamParser(provider: "gemini", log: { notes.logs.append(($0, $1)) }, resume: { notes.resumes.append($0) }, requestedResume: "old-conversation")
            resumed.receiveStderr("warning: conversation \"old-conversation\" not found\n")
            resumed.push(#"{"event":"init","conversation_id":"new-conversation","init":{}}"# + "\n")
            #expect(notes.logs.map(\.0) == ["system"] && notes.logs[0].1 == "The previous conversation was not found, so Antigravity started a new one.")
            #expect(notes.resumes == ["new-conversation"])
            let same = Recorder()
            let kept = CLIStreamParser(provider: "gemini", log: { same.logs.append(($0, $1)) }, resume: { _ in }, requestedResume: "c1")
            kept.push(#"{"event":"init","conversation_id":"c1","init":{}}"# + "\n")
            #expect(same.logs.isEmpty)
            // A read-only command's payload is left to its result; anything unknown is shown.
            let other = Recorder(); let parser = makeParser(other)
            parser.push(#"{"event":"command_result","command":{"name":"help","data":{}}}"# + "\n" + #"{"event":"result","result":{"conversation_id":"","status":"SUCCESS","response":"/help\tShow available commands\n"}}"# + "\n" + #"{"event":"future_event","future_event":{"x":1}}"# + "\n")
            #expect(other.logs.map(\.0) == ["assistant", "output"] && other.logs[1].1.contains("future_event"))
            #expect(other.resumes.isEmpty)
        }
    }

    @Test func builtinSlashCommandsAreAnsweredByTheApp() {
        let commands = SlashCommandCatalog.builtins(provider: "gemini")
        #expect(commands.map(\.invocation) == ["model", "permissions", "clear", "usage", "config", "rename", "help"])
        #expect(commands.allSatisfy { $0.origin == .app && ($0.action != nil || $0.argument != nil) })
    }
}

private final class Counter: @unchecked Sendable {
    private let lock = NSLock(); private var value = 0
    func next() -> Int { lock.lock(); defer { lock.unlock() }; value += 1; return value }
}
