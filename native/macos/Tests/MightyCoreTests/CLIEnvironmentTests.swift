import Foundation
import Testing
@testable import MightyCore

struct CLIEnvironmentTests {
    private func temporary() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("cli-environment-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }
    @discardableResult private func script(_ text: String, at url: URL) throws -> URL {
        try Data(("#!/bin/sh\n" + text + "\n").utf8).write(to: url)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
        return url
    }
    private func fakeShell(_ root: URL) throws -> URL {
        try script(#"""
        [ "$1" = '-ilc' ] || exit 10
        printf 'startup chatter\n'
        printf 'call\n' >> "$PWD/shell-calls"
        . "$PWD/shell-settings"
        printf 'ready\n' > "$PWD/shell-ready"
        [ ! -f "$PWD/shell-delay" ] || /bin/sleep 0.3
        exec /bin/sh -c "$2"
        """#, at: root.appendingPathComponent("login-shell"))
    }
    private func settings(_ root: URL, model: String = "model-one", bin: URL? = nil) throws {
        let path = bin?.path ?? "/usr/bin:/bin"
        try Data("export PATH='\(path)'\nexport FIXTURE_MODEL='\(model)'\nexport AWS_BEARER_TOKEN_BEDROCK='fixture-rotated'\nunset AWS_ACCESS_KEY_ID\n".utf8).write(to: root.appendingPathComponent("shell-settings"))
    }
    private func resolver(_ root: URL, shell: URL, timeout: TimeInterval = 2) -> CLIEnvironmentResolver {
        CLIEnvironmentResolver(baseEnvironment: ["HOME": root.path, "PATH": "/usr/bin:/bin", "AWS_ACCESS_KEY_ID": "fixture-stale", "AWS_BEARER_TOKEN_BEDROCK": "fixture-old"], shell: shell, home: root, timeout: timeout)
    }
    private func waitForFile(_ file: URL) async throws {
        for _ in 0..<200 {
            if FileManager.default.fileExists(atPath: file.path) { return }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        throw MightyError("Fixture did not start")
    }

    @Test func shellSnapshotReplacesUnsetValuesAndRefreshesPerWorkspace() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let shell = try fakeShell(root); try settings(root)
        let resolver = resolver(root, shell: shell)
        let first = await resolver.resolve()
        #expect(first.source == .loginShell && first.fallbackDetail == nil)
        #expect(first.values["AWS_ACCESS_KEY_ID"] == nil && first.values["AWS_BEARER_TOKEN_BEDROCK"] == "fixture-rotated")
        #expect(!String(describing: first).contains("fixture-") && !String(reflecting: first).contains("AWS_"))
        try settings(root, model: "model-two")
        #expect(await resolver.resolve().values["FIXTURE_MODEL"] == "model-one")
        #expect(await resolver.resolve(forceRefresh: true).values["FIXTURE_MODEL"] == "model-two")
        let workspace = root.appendingPathComponent("workspace")
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        try settings(workspace, model: "workspace-model")
        #expect(await resolver.resolve(workspacePath: workspace.path).values["FIXTURE_MODEL"] == "workspace-model")
        #expect(await resolver.resolve().values["FIXTURE_MODEL"] == "model-two")
    }

    @Test func concurrentRefreshesShareWorkAndInvalidationUsesTheNewEnvironment() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let shell = try fakeShell(root); try settings(root)
        try Data().write(to: root.appendingPathComponent("shell-delay"))
        let resolver = resolver(root, shell: shell)
        let first = Task { await resolver.resolve(forceRefresh: true) }
        let second = Task { await resolver.resolve(forceRefresh: true) }
        #expect(await first.value.values["FIXTURE_MODEL"] == "model-one")
        #expect(await second.value.values["FIXTURE_MODEL"] == "model-one")
        #expect(try String(contentsOf: root.appendingPathComponent("shell-calls"), encoding: .utf8).split(separator: "\n").count == 1)
        try FileManager.default.removeItem(at: root.appendingPathComponent("shell-ready"))
        let old = Task { await resolver.resolve(forceRefresh: true) }
        try await waitForFile(root.appendingPathComponent("shell-ready"))
        try settings(root, model: "model-two")
        let current = await resolver.resolve(forceRefresh: true)
        #expect(current.source == .loginShell && current.values["FIXTURE_MODEL"] == "model-two")
        #expect(await old.value.values["FIXTURE_MODEL"] == "model-two")
    }

    @Test func malformedFailedAndTimedOutShellsFallBackWithoutEchoingTheirOutput() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        for (index, content) in ["printf fixture-secret; exit 1", "printf fixture-secret", "/bin/sleep 5"].enumerated() {
            let shell = try script(content, at: root.appendingPathComponent("bad-shell-\(index)"))
            let resolver = resolver(root, shell: shell, timeout: 0.05)
            let snapshot = await resolver.resolve()
            #expect(snapshot.source == .processFallback && snapshot.values["AWS_ACCESS_KEY_ID"] == "fixture-stale")
            #expect(snapshot.fallbackDetail != nil && !String(describing: snapshot).contains("fixture-secret"))
        }
        let framed = Data("noise\0start\0PATH=/bin\0MULTI=line1\nline2=끝\0\0end\0trailing".utf8)
        let parsed = CLIEnvironmentResolver.parse(framed, start: "start", end: "end")
        #expect(parsed?["MULTI"] == "line1\nline2=끝")
        #expect(CLIEnvironmentResolver.parse(Data("\0start\0PATH=/bin\0PATH=/usr/bin\0\0end\0".utf8), start: "start", end: "end") == nil)
        #expect(CLIEnvironmentResolver.parse(Data("\0start\0PATH=/bin".utf8), start: "start", end: "end") == nil)
    }

    @Test func providerDiscoveryMetadataAndAccountCommandsUseResolvedShellEnvironment() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let shell = try fakeShell(root)
        let firstBin = root.appendingPathComponent("first"), secondBin = root.appendingPathComponent("second")
        for bin in [firstBin, secondBin] {
            try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
            try script(#"""
            [ "$AWS_BEARER_TOKEN_BEDROCK" = 'fixture-rotated' ] || exit 11
            [ -z "${AWS_ACCESS_KEY_ID+x}" ] || exit 12
            if [ "$1" = '--version' ]; then printf '0.153.4\n'; exit 0; fi
            if [ "$1" = 'logout' ]; then printf done > "$PWD/logout-marker"; exit 0; fi
            if [ "$1" = 'login' ]; then
              if [ -f "$PWD/logout-marker" ]; then printf 'Not logged in\n'; else printf 'Logged in using ChatGPT\n'; fi
              exit 0
            fi
            case " $* " in
              *' exec '* )
                IFS= read -r prompt
                printf done > "$PWD/run-marker"
                printf '{"type":"thread.started","thread_id":"fixture-thread"}\n{"type":"item.completed","item":{"id":"answer","type":"agent_message","text":"Fixture completed"}}\n'
                exit 0;;
            esac
            IFS= read -r init
            printf '{"id":1,"result":{}}\n'
            IFS= read -r notification; IFS= read -r models
            printf '{"id":2,"result":{"data":[{"model":"%s","displayName":"Fixture"}]}}\n' "$FIXTURE_MODEL"
            /bin/cat >/dev/null
            """#, at: bin.appendingPathComponent("codex"))
        }
        try settings(root, bin: firstBin)
        let resolver = resolver(root, shell: shell)
        let service = ProviderService(environmentResolver: resolver)
        let first = await service.providerRuntime(provider: "codex", workspacePath: root.path)
        #expect(first.available && first.modelCatalog.models.last?.value == "model-one")
        #expect(await service.command(provider: "codex", workspacePath: root.path)?.executable == firstBin.appendingPathComponent("codex"))
        try settings(root, model: "model-two", bin: secondBin)
        let fresh = await service.providerRuntime(provider: "codex", workspacePath: root.path, forceRefresh: true)
        #expect(fresh.modelCatalog.models.last?.value == "model-two")
        #expect(await service.command(provider: "codex", workspacePath: root.path)?.executable == secondBin.appendingPathComponent("codex"))
        let accounts = CLIAccountService(home: root, environmentResolver: resolver)
        #expect(await accounts.status(provider: "codex").loggedIn == true)
        #expect(await accounts.logout(provider: "codex").loggedIn == false)
        #expect(FileManager.default.fileExists(atPath: root.appendingPathComponent("logout-marker").path))
        let runner = ProcessRunner(providerService: service, pluginDirectory: root, onEvent: { _ in })
        let workspace = Workspace(id: "fixture-workspace", name: "Fixture", path: root.path)
        try await runner.start(request: StartRunRequest(sessionId: "fixture-session", workspaceId: workspace.id, input: "Fixture input", provider: "codex"), workspace: workspace)
        try await waitForFile(root.appendingPathComponent("run-marker"))
        await runner.shutdown()
        let fixed = ProviderService(environment: ["PATH": "/bin"], environmentResolver: resolver)
        #expect(await fixed.executionEnvironment(workspacePath: root.path).source == .provided)
        #expect(await fixed.command(provider: "codex", workspacePath: root.path) == nil)
        await service.shutdown(); await fixed.shutdown()
    }
}
