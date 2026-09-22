import Foundation
import Testing
@testable import MightyCore

@Suite struct CodexApprovalRemoteTests {
    @Test func hostExportsOnlyRemoteCapableModesAndRejectsApprovalRuns() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-codex-remote-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let binary = root.appendingPathComponent("codex")
        let launched = root.appendingPathComponent("turn-launched")
        // Only local model discovery is implemented. Any turn attempt records a
        // marker and fails without calling a real CLI, credentials, or inference.
        let source = #"""
        #!/usr/bin/python3
        import json, pathlib, sys
        if "--version" in sys.argv:
            print("codex-cli 0.153.4"); sys.exit(0)
        marker = pathlib.Path(__file__).parent / "turn-launched"
        if "exec" in sys.argv:
            marker.touch(); sys.exit(31)
        for line in sys.stdin:
            frame = json.loads(line)
            method = frame.get("method")
            if method == "initialize":
                print(json.dumps({"id": frame["id"], "result": {"userAgent": "fixture"}}), flush=True)
            elif method == "initialized":
                pass
            elif method == "model/list":
                print(json.dumps({"id": frame["id"], "result": {"data": [], "nextCursor": None}}), flush=True)
            else:
                marker.touch(); sys.exit(32)
        """#
        try Data(source.utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        let unavailable = URL(fileURLWithPath: "/usr/bin/false")
        let providers = ProviderService(binaryOverrides: ["claude": unavailable, "codex": binary, "gemini": unavailable], environment: ["PATH": "/usr/bin:/bin"])
        let repository = StateRepository(directory: root.appendingPathComponent("host-state"), legacyStateURL: nil)
        let workspace = try await repository.approveWorkspace(Workspace(name: "Codex remote fixture", path: root.path))
        try await repository.save(AppSnapshot(workspaces: [workspace]))
        let clientRepository = StateRepository(directory: root.appendingPathComponent("client-state"), legacyStateURL: nil)
        let host = RemoteService(repository: repository, providers: providers, pluginDirectory: root, dataDirectory: root.appendingPathComponent("host-remote"), onEvent: { _ in }, allowLoopbackForTests: true)
        let client = RemoteService(repository: clientRepository, providers: providers, pluginDirectory: root, dataDirectory: root.appendingPathComponent("client-remote"), onEvent: { _ in }, allowLoopbackForTests: true)
        do {
            let local = await providers.providerRuntime(provider: "codex")
            #expect(local.available)
            #expect(local.capabilities.permissionModes.contains("onRequest"))
            let shared = try await host.startSharing(workspaceIds: [workspace.id], port: 0)
            let address = try #require(shared.host.address)
            let token = try #require(shared.host.token)
            let target = try await RemoteTransport.resolve(ParsedRemoteAddress.parse(address), peers: [], allowLoopback: true)
            let data = try await RemoteTransport.request(target, token: token, method: "GET", path: "/v1/info")
            var info = try JSONDecoder().decode(WireInfo.self, from: data)
            try RemoteValidation.info(info)
            let codex = try #require(info.runtime.providers?.first { $0.id == "codex" })
            #expect(codex.available)
            #expect(codex.capabilities.permissionModes == local.capabilities.permissionModes.filter { $0 != "onRequest" })
            #expect(codex.capabilities.networkAccess == local.capabilities.networkAccess)
            let connected = try await client.connectRemote(name: "Codex fixture", address: address, token: token)
            let connection = try #require(connected.connections.first)
            #expect(connection.status == "connected")
            let imported = try await client.importWorkspace(connectionId: connection.id, workspaceId: workspace.id)
            let request = StartRunRequest(sessionId: "remote-approval", workspaceId: imported.id, input: "must not launch", provider: "codex", settings: RunSettings(permissionMode: "onRequest"))
            await #expect(throws: MightyError.self) { try await client.start(request: request, workspace: imported) }
            // A client bypassing the capability check is rejected before job creation.
            var direct = request; direct.workspaceId = workspace.id
            var http = URLRequest(url: try #require(URL(string: address + "/v1/runs")))
            http.httpMethod = "POST"
            http.setValue("Bearer " + token, forHTTPHeaderField: "Authorization")
            http.setValue("1", forHTTPHeaderField: "x-mighty-remote-version")
            http.setValue("application/json", forHTTPHeaderField: "Content-Type")
            http.httpBody = try JSONEncoder().encode(WireStart(request: direct))
            let (_, response) = try await URLSession.shared.data(for: http)
            #expect((response as? HTTPURLResponse)?.statusCode == 400)
            #expect(await host.state().host.activeRuns == 0)
            #expect(!FileManager.default.fileExists(atPath: launched.path))
            // The wire validator remains strict, including for an unexpected host.
            let index = try #require(info.runtime.providers?.firstIndex { $0.id == "codex" })
            info.runtime.providers?[index].capabilities.permissionModes.append("onRequest")
            #expect(throws: RemoteFailure.self) { try RemoteValidation.info(info) }
            let after = await providers.providerRuntime(provider: "codex")
            #expect(after.capabilities.permissionModes.contains("onRequest"))
        } catch {
            await client.shutdown(); await host.shutdown(); await providers.shutdown(); throw error
        }
        await client.shutdown(); await host.shutdown(); await providers.shutdown()
    }
}
