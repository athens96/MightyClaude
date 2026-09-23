import Testing
import Foundation
@testable import MightyCore

private final class EventRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var entries: [RunEvent] = []
    func append(_ event: RunEvent) { lock.lock(); entries.append(event); lock.unlock() }
    func values() -> [RunEvent] { lock.lock(); defer { lock.unlock() }; return entries }
}

@Suite struct RemoteTests {
    @Test func testTailnetAddressBoundaryAndInputValidation() throws {
        for address in ["100.64.0.1", "100.127.255.254", "fd7a:115c:a1e0::1", "::ffff:100.80.0.1"] { #expect(RemoteIPPolicy.allowed(address)) }
        for address in ["100.63.255.255", "100.128.0.1", "127.0.0.1", "::1", "192.168.1.2", "8.8.8.8", "fd7a:115c:a1e1::1", "100.80.0.1.evil.test"] { #expect(!RemoteIPPolicy.allowed(address)) }
        #expect(RemoteIPPolicy.allowed("127.0.0.1", allowLoopback: true))
        #expect(try ParsedRemoteAddress.parse("http://peer.tail.test:43137/").origin == "http://peer.tail.test:43137")
        for address in ["https://peer:43137", "http://user:secret@peer:43137", "http://peer:43137/path", "http://peer:43137?token=x", "http://peer:43137#fragment", "http://peer", "http://peer:80"] { #expect(throws: (any Error).self) { try ParsedRemoteAddress.parse(address) } }
        #expect(RemoteValidation.workspace(Workspace(name: "Windows", path: "C:\\Projects\\remote")))
        #expect(!RemoteValidation.workspace(Workspace(name: "Relative", path: "relative/path")))
    }

    @Test func testHTTPListenerCloseBeforeReadySettles() async throws {
        let server = HTTPServer(address: "127.0.0.1", port: 0) { _ in .json(200, [:]) }
        let starting = Task { try await server.start() }
        await server.stop()
        _ = try? await starting.value
        await server.stop()
    }

    @Test func testRemoteExecutionSettingsRequireAdvertisedCapabilitiesBeforePOST() async throws {
        for modern in [false, true] {
            let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-settings-remote-\(UUID().uuidString)")
            defer { try? FileManager.default.removeItem(at: directory) }
            let workspace = Workspace(id: "peer-workspace", name: "Remote settings", path: "/tmp/fixture")
            var runtime = ProviderOptions.fallbackRuntime("codex")
            if !modern { runtime.capabilities = ProviderCapabilities(effort: true, permissionModes: ["manual", "acceptEdits"], maxTurns: false, maxBudgetUsd: false) }
            var claude = ProviderOptions.fallbackRuntime("claude")
            if modern { claude.capabilities = ProviderService.capabilities(provider: "claude", version: "2.1.273") }
            let info = WireInfo(hostId: "fixture-host", hostName: "Fixture", workspaces: [workspace], runtime: RuntimeInfo(providers: [runtime, claude]))
            var json = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(info)) as? [String: Any])
            if !modern {
                var runtimeJSON = try #require(json["runtime"] as? [String: Any]); var providers = try #require(runtimeJSON["providers"] as? [[String: Any]])
                var caps = try #require(providers[0]["capabilities"] as? [String: Any])
                for field in ["fastMode", "webSearch", "networkAccess"] { caps.removeValue(forKey: field) }
                providers[0]["capabilities"] = caps; runtimeJSON["providers"] = providers; json["runtime"] = runtimeJSON
            }
            let body = try JSONSerialization.data(withJSONObject: json)
            let recorder = EventRecorder()
            let server = HTTPServer(address: "127.0.0.1", port: 0) { request in
                if request.method == "POST" {
                    recorder.append(RunEvent(sessionId: "captured", type: "log", entry: LogEntry(kind: "system", text: String(decoding: request.body, as: UTF8.self))))
                    // Capture only: this fixture never creates a model turn or job.
                    return HTTPResponse(status: 400, body: Data("{}".utf8), headers: ["x-mighty-remote-version": "1"])
                }
                return HTTPResponse(status: 200, body: body, headers: ["x-mighty-remote-version": "1"])
            }
            let unavailable = URL(fileURLWithPath: "/usr/bin/false")
            let providers = ProviderService(binaryOverrides: ["claude": unavailable, "codex": unavailable, "gemini": unavailable])
            let repository = StateRepository(directory: directory, legacyStateURL: nil)
            let client = RemoteService(repository: repository, providers: providers, pluginDirectory: directory, dataDirectory: directory, onEvent: { _ in }, allowLoopbackForTests: true)
            do {
                let port = try await server.start()
                let connected = try await client.connectRemote(name: "Fixture", address: "http://127.0.0.1:\(port)", token: String(repeating: "a", count: 43))
                let connection = try #require(connected.connections.first)
                let imported = try await client.importWorkspace(connectionId: connection.id, workspaceId: workspace.id)
                let settings = [RunSettings(fastMode: true), RunSettings(webSearch: "live"), RunSettings(permissionMode: "acceptEdits", networkAccess: true), RunSettings(permissionMode: "fullAccess")]
                for setting in settings {
                    let request = StartRunRequest(sessionId: "settings-check", workspaceId: imported.id, input: "capture only", provider: "codex", settings: setting)
                    do { try await client.start(request: request, workspace: imported); Issue.record("Fixture must reject or capture without starting") } catch { }
                }
                let auto = RunSettings(permissionMode: "auto")
                let autoRequest = StartRunRequest(sessionId: "auto-check", workspaceId: imported.id, input: "capture only", provider: "claude", settings: auto)
                do { try await client.start(request: autoRequest, workspace: imported); Issue.record("Fixture must reject or capture without starting") } catch { }
                let captured = try recorder.values().compactMap(\.entry).map { try JSONDecoder().decode(WireStart.self, from: Data($0.text.utf8)).request.settings }
                #expect(captured == (modern ? settings + [auto] : []))
            } catch { await client.shutdown(); await server.stop(); throw error }
            await client.shutdown(); await server.stop()
        }
    }

    @Test func testNativeRemoteHostAndClientRunStopAndAuthenticate() async throws {
        let temporary = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-remote-swift-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temporary) }
        let workdir = temporary.appendingPathComponent("workspace")
        try FileManager.default.createDirectory(at: workdir, withIntermediateDirectories: true)
        let repository = StateRepository(directory: temporary.appendingPathComponent("host-state"), legacyStateURL: nil)
        let workspace = try await repository.approveWorkspace(Workspace(name: "Native host", path: workdir.path))
        try await repository.save(AppSnapshot(workspaces: [workspace], activeWorkspaceId: workspace.id))
        let clientRepository = StateRepository(directory: temporary.appendingPathComponent("client-state"), legacyStateURL: nil)
        let unavailable = URL(fileURLWithPath: "/usr/bin/false")
        let providers = ProviderService(binaryOverrides: ["claude": unavailable, "codex": unavailable, "gemini": unavailable])
        let recorder = EventRecorder()
        let host = RemoteService(repository: repository, providers: providers, pluginDirectory: temporary, dataDirectory: temporary.appendingPathComponent("host-remote"), onEvent: { _ in }, allowLoopbackForTests: true)
        let client = RemoteService(repository: clientRepository, providers: providers, pluginDirectory: temporary, dataDirectory: temporary.appendingPathComponent("client-remote"), onEvent: { recorder.append($0) }, allowLoopbackForTests: true)
        do {
            let before = await host.state()
            #expect(!before.host.enabled)
            let shared = try await host.startSharing(workspaceIds: [workspace.id], port: 0)
            let address = try #require(shared.host.address)
            let token = try #require(shared.host.token)
            #expect(RemoteValidation.token(token))
            let target = try await RemoteTransport.resolve(ParsedRemoteAddress.parse(address), peers: [], allowLoopback: true)
            var browserRequest = URLRequest(url: URL(string: address + "/v1/info")!)
            browserRequest.setValue("Bearer " + token, forHTTPHeaderField: "Authorization")
            browserRequest.setValue("1", forHTTPHeaderField: "x-mighty-remote-version")
            browserRequest.setValue("https://untrusted.example", forHTTPHeaderField: "Origin")
            let (_, browserResponse) = try await URLSession.shared.data(for: browserRequest)
            #expect((browserResponse as? HTTPURLResponse)?.statusCode == 403)
            browserRequest.setValue(nil, forHTTPHeaderField: "Origin")
            browserRequest.setValue(nil, forHTTPHeaderField: "x-mighty-remote-version")
            let (_, versionResponse) = try await URLSession.shared.data(for: browserRequest)
            #expect((versionResponse as? HTTPURLResponse)?.statusCode == 426)
            let oversized = try await ProcessCapture.run(executable: URL(fileURLWithPath: "/usr/bin/curl"), arguments: ["--silent", "--max-time", "3", "--output", "/dev/null", "--write-out", "%{http_code}", "--request", "POST", "--header", "Authorization: Bearer " + token, "--header", "x-mighty-remote-version: 1", "--header", "Content-Length: 524289", address + "/v1/runs/example/stop"], timeout: 4)
            #expect(String(decoding: oversized.stdout, as: UTF8.self) == "413")
            do {
                _ = try await RemoteTransport.request(target, token: String(repeating: "x", count: 43), method: "GET", path: "/v1/info")
                Issue.record("Wrong bearer token was accepted")
            } catch { #expect(error.localizedDescription.contains("연결 키")) }
            let connected = try await client.connectRemote(name: "Loopback host", address: address, token: token)
            let connection = try #require(connected.connections.first)
            let imported = try await client.importWorkspace(connectionId: connection.id, workspaceId: workspace.id)
            #expect(imported.remote?.workspaceId == workspace.id)
            #expect(imported.id != workspace.id)
            try await client.start(request: StartRunRequest(sessionId: "remote-echo", workspaceId: imported.id, kind: "shell", input: "printf MIGHTY_SWIFT_REMOTE_OK"), workspace: imported)
            try await waitFor { recorder.values().contains { $0.sessionId == "remote-echo" && $0.status == "completed" } }
            #expect(recorder.values().contains { $0.entry?.text.contains("MIGHTY_SWIFT_REMOTE_OK") == true })
            try await client.start(request: StartRunRequest(sessionId: "remote-wait", workspaceId: imported.id, kind: "shell", input: "/bin/sleep 60"), workspace: imported)
            await client.stop(id: "remote-wait")
            #expect(recorder.values().contains { $0.sessionId == "remote-wait" && $0.status == "stopped" })
            let after = await host.state()
            #expect(after.host.activeRuns == 0)
            let disconnected = await client.disconnectRemote(id: connection.id)
            #expect(disconnected.connections.first?.status == "disconnected")
            do { try await client.start(request: StartRunRequest(sessionId: "offline-run", workspaceId: imported.id, kind: "shell", input: "echo MUST_NOT_RUN"), workspace: imported); Issue.record("Offline workspace ran") } catch { }
            let restored = try await client.refreshRemote(id: connection.id)
            #expect(restored.connections.first?.status == "connected")
            let saved = try String(contentsOf: temporary.appendingPathComponent("client-remote/remote-connections.json"), encoding: .utf8)
            #expect(!saved.contains(token))
            _ = await host.stopSharing()
            let stopped = await host.state()
            #expect(!stopped.host.enabled)
            #expect(stopped.host.token == nil)
        } catch {
            await client.shutdown(); await host.shutdown()
            throw error
        }
        await client.shutdown(); await host.shutdown()
    }

    private func waitFor(_ predicate: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(30)
        while !predicate() {
            if Date() >= deadline { throw MightyError("Timed out waiting for remote shell output") }
            try await Task.sleep(nanoseconds: 50_000_000)
        }
    }

    @Test(.enabled(if: ProcessInfo.processInfo.environment["MIGHTY_NATIVE_PEER_MANIFEST"] != nil, "Requires the temporary C# fixture host"))
    func testSwiftClientAgainstDotNetHost() async throws {
        let manifestPath = try #require(ProcessInfo.processInfo.environment["MIGHTY_NATIVE_PEER_MANIFEST"])
        let manifest = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath: manifestPath))) as? [String: String])
        let address = try #require(manifest["address"])
        let token = try #require(manifest["token"])
        let workspaceId = try #require(manifest["workspaceId"])
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-swift-dotnet-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let repository = StateRepository(directory: directory, legacyStateURL: nil)
        let unavailable = URL(fileURLWithPath: "/usr/bin/false")
        let providers = ProviderService(binaryOverrides: ["claude": unavailable, "codex": unavailable, "gemini": unavailable])
        let recorder = EventRecorder()
        let client = RemoteService(repository: repository, providers: providers, pluginDirectory: directory, dataDirectory: directory, onEvent: { recorder.append($0) }, allowLoopbackForTests: true)
        do {
            let connected = try await client.connectRemote(name: "C# native host", address: address, token: token)
            let connection = try #require(connected.connections.first)
            let workspace = try await client.importWorkspace(connectionId: connection.id, workspaceId: workspaceId)
            try await client.start(request: StartRunRequest(sessionId: "cross-language-echo", workspaceId: workspace.id, kind: "shell", input: "echo MIGHTY_SWIFT_DOTNET_OK"), workspace: workspace)
            try await waitFor { recorder.values().contains { $0.sessionId == "cross-language-echo" && $0.status == "completed" } }
            #expect(recorder.values().contains { $0.entry?.text.contains("MIGHTY_SWIFT_DOTNET_OK") == true })
            let command = connection.runtime?.platform == "win32" ? "ping -n 60 127.0.0.1 > nul" : "/bin/sleep 60"
            try await client.start(request: StartRunRequest(sessionId: "cross-language-stop", workspaceId: workspace.id, kind: "shell", input: command), workspace: workspace)
            await client.stop(id: "cross-language-stop")
            #expect(recorder.values().contains { $0.sessionId == "cross-language-stop" && $0.status == "stopped" })
            await client.shutdown()
        } catch { await client.shutdown(); throw error }
    }

    @Test func testTransportDoesNotForwardSecretsOnRedirectOrResolvePublicAddresses() async throws {
        for address in ["http://127.0.0.1:43137", "http://8.8.8.8:43137", "http://192.168.1.5:43137"] {
            do {
                _ = try await RemoteTransport.resolve(ParsedRemoteAddress.parse(address), peers: [], allowLoopback: false)
                Issue.record("Non-tailnet target was accepted")
            } catch { }
        }
        let recorder = EventRecorder()
        let destination = HTTPServer(address: "127.0.0.1", port: 0) { request in
            recorder.append(RunEvent(sessionId: "redirect", type: "log", entry: .init(kind: "error", text: request.headers["authorization"] ?? "")))
            return HTTPResponse(status: 200, body: Data("{}".utf8), headers: ["x-mighty-remote-version": "1"])
        }
        let destinationPort = try await destination.start()
        let redirect = HTTPServer(address: "127.0.0.1", port: 0) { _ in
            HTTPResponse(status: 302, body: Data("{}".utf8), headers: ["x-mighty-remote-version": "1", "Location": "http://127.0.0.1:\(destinationPort)/v1/info"])
        }
        do {
            let port = try await redirect.start()
            let target = try await RemoteTransport.resolve(ParsedRemoteAddress.parse("http://127.0.0.1:\(port)"), peers: [], allowLoopback: true)
            do {
                _ = try await RemoteTransport.request(target, token: String(repeating: "a", count: 43), method: "GET", path: "/v1/info")
                Issue.record("Redirect was accepted")
            } catch { #expect(error.localizedDescription.contains("302")) }
            #expect(recorder.values().isEmpty)
        } catch { await redirect.stop(); await destination.stop(); throw error }
        await redirect.stop(); await destination.stop()
    }

    @Test func testHTTPRejectsAmbiguousFramingBeforeCallingHandler() async throws {
        let recorder = EventRecorder()
        let server = HTTPServer(address: "127.0.0.1", port: 0) { _ in
            recorder.append(RunEvent(sessionId: "unexpected", type: "status", status: "running"))
            return .json(200, [:])
        }
        do {
            let port = try await server.start()
            let base = ["--silent", "--max-time", "3", "--output", "/dev/null", "--write-out", "%{http_code}", "--request", "POST"]
            let url = "http://127.0.0.1:\(port)/v1/runs"
            for headers in [
                ["--header", "Content-Length: 0", "--header", "Content-Length: 1"],
                ["--header", "Transfer-Encoding: chunked", "--data", "{}"],
                ["--header", "Authorization: first", "--header", "Authorization: second", "--header", "Content-Length: 0"],
            ] {
                let result = try await ProcessCapture.run(executable: URL(fileURLWithPath: "/usr/bin/curl"), arguments: base + headers + [url], timeout: 4)
                #expect(String(decoding: result.stdout, as: UTF8.self) == "400")
            }
            #expect(recorder.values().isEmpty)
        } catch { await server.stop(); throw error }
        await server.stop()
    }
}
