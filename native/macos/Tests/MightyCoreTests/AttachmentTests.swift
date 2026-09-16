import Testing
import Foundation
@testable import MightyCore

private final class AttachmentEvents: @unchecked Sendable {
    private let lock = NSLock(); private var entries: [RunEvent] = []
    func add(_ event: RunEvent) { lock.lock(); entries.append(event); lock.unlock() }
    func values() -> [RunEvent] { lock.lock(); defer { lock.unlock() }; return entries }
}

@Suite(.serialized) struct AttachmentTests {
    private let png = Data([137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3])
    private func directory(_ suffix: String = "") throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-attachment-test-\(UUID().uuidString)\(suffix)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); return root
    }
    private func wait(_ condition: () -> Bool) async throws {
        let end = Date().addingTimeInterval(8)
        while !condition() {
            guard Date() < end else { throw MightyError("Attachment fixture timed out") }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
    }

    @Test func validationAndLegacyWire() throws {
        let image = try AttachmentSupport.make(name: "C:\\private\\folder\\image.png", data: png)
        #expect(image.name == "image.png"); #expect(image.mediaType == "image/png")
        let bom = try AttachmentSupport.make(name: String(repeating: "😀", count: 100) + ".txt", data: Data("\u{feff}text 👩‍💻\n".utf8))
        #expect(bom.mediaType == "text/plain"); #expect(bom.name.utf16.count <= 180)
        try AttachmentSupport.validate([bom])
        #expect(try JSONDecoder().decode(RunAttachment.self, from: JSONEncoder().encode(bom)) == bom)
        let request = StartRunRequest(sessionId: "pane", workspaceId: "space", input: "", attachments: [image, bom])
        try CoreValidation.validate(request)
        #expect(try JSONDecoder().decode(StartRunRequest.self, from: JSONEncoder().encode(request)).attachments == request.attachments)
        let empty = StartRunRequest(sessionId: "pane", workspaceId: "space", input: "legacy")
        let json = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(empty)) as? [String: Any])
        #expect(json["attachments"] == nil)
        #expect(try JSONDecoder().decode(StartRunRequest.self, from: JSONEncoder().encode(empty)).attachments.isEmpty)
        #expect(try JSONDecoder().decode(ProviderCapabilities.self, from: Data(#"{"effort":true,"permissionModes":["manual"],"maxTurns":false,"maxBudgetUsd":false,"resume":true}"#.utf8)).attachments == false)
        #expect(throws: (any Error).self) { try CoreValidation.validateCapabilities(request, capabilities: .init()) }
        var shell = request; shell.kind = "shell"
        #expect(throws: (any Error).self) { try CoreValidation.validate(shell) }
        for badName in ["../escape", "a\\b", "a\u{0}b", "..", String(repeating: "😀", count: 91)] {
            var bad = image; bad.name = badName
            #expect(throws: (any Error).self) { try AttachmentSupport.validate([bad]) }
        }
        for badBase64 in ["%%%", "YQ=", image.dataBase64 + "\n"] {
            var bad = image; bad.dataBase64 = badBase64
            #expect(throws: (any Error).self) { try AttachmentSupport.validate([bad]) }
        }
        var spoof = image; spoof.mediaType = "text/plain"
        #expect(throws: (any Error).self) { try AttachmentSupport.validate([spoof]) }
        var tooMany = [RunAttachment]()
        for i in 0..<9 { var item = image; item.id = "item-\(i)"; tooMany.append(item) }
        #expect(throws: (any Error).self) { try AttachmentSupport.validate(tooMany) }
        #expect(throws: (any Error).self) { try AttachmentSupport.make(name: "huge", data: Data(repeating: 65, count: AttachmentSupport.maximumFileBytes + 1)) }
        let large = try AttachmentSupport.make(name: "large.txt", data: Data(repeating: 65, count: AttachmentSupport.maximumFileBytes))
        var other = large; other.id = "other"
        #expect(throws: (any Error).self) { try AttachmentSupport.validate([large, other]) }
        for (data, type) in [(Data([255,216,255]), "image/jpeg"), (Data("GIF89a".utf8), "image/gif"), (Data("RIFF1234WEBP".utf8), "image/webp"), (Data("%PDF-1.7".utf8), "application/pdf"), (Data([0,255]), "application/octet-stream")] {
            #expect(try AttachmentSupport.make(name: "file", data: data).mediaType == type)
        }
    }

    @Test func stagingAndThreeProviderPayloads() throws {
        let parent = try directory(" space,[test]!"); defer { try? FileManager.default.removeItem(at: parent) }
        let attachments = [try AttachmentSupport.make(name: "picture.png", data: png), try AttachmentSupport.make(name: "file.pdf", data: Data("%PDF-1.7".utf8)), try AttachmentSupport.make(name: "source.swift", data: Data("let count = 1".utf8))]
        let prepared = try AttachmentPreparation(attachments, parent: parent)
        let folder = try #require(prepared.directory)
        #expect((try FileManager.default.attributesOfItem(atPath: folder.path)[.posixPermissions] as? NSNumber)?.intValue == 0o700)
        for file in prepared.files {
            #expect(try Data(contentsOf: file.url) == Data(base64Encoded: file.attachment.dataBase64))
            #expect((try FileManager.default.attributesOfItem(atPath: file.url.path)[.posixPermissions] as? NSNumber)?.intValue == 0o600)
            #expect(file.url.lastPathComponent != file.attachment.name)
        }
        #expect(prepared.files.last?.url.pathExtension == "swift")
        for provider in ProviderOptions.ids {
            for resume in [nil, "resume-id"] as [String?] {
                let request = StartRunRequest(sessionId: "pane", workspaceId: "space", input: "Describe files", provider: provider, resumeId: resume, attachments: attachments)
                let input = try ProviderInput.prepare(request, pluginDirectory: parent, attachments: prepared)
                if provider == "claude" {
                    #expect(input.arguments.contains("--input-format")); #expect(input.arguments.contains("--add-dir"))
                    #expect(input.standardInput.last == 10)
                    let json = try #require(JSONSerialization.jsonObject(with: input.standardInput) as? [String: Any])
                    #expect(json["type"] as? String == "user"); #expect(json["parent_tool_use_id"] is NSNull)
                    let message = try #require(json["message"] as? [String: Any]); let blocks = try #require(message["content"] as? [[String: Any]])
                    #expect(blocks.compactMap { $0["type"] as? String } == ["text", "image", "document"])
                    let imageSource = try #require(blocks[1]["source"] as? [String: String])
                    #expect(imageSource["data"] == attachments[0].dataBase64)
                    #expect((blocks[0]["text"] as? String)?.contains("source.swift") == true)
                } else if provider == "codex" {
                    #expect(input.arguments.last == "-")
                    let index = try #require(input.arguments.firstIndex(of: "--image"))
                    #expect(input.arguments[index + 1] == prepared.files[0].url.path)
                    #expect(!input.arguments.contains("--add-dir"))
                    #expect(String(decoding: input.standardInput, as: UTF8.self).contains(prepared.files[1].url.path.replacingOccurrences(of: "/", with: "\\/")) || String(decoding: input.standardInput, as: UTF8.self).contains(prepared.files[1].url.path))
                    if resume != nil { #expect(input.arguments.contains("resume")) }
                } else {
                    #expect(input.arguments.suffix(2) == ["--include-directories", folder.path])
                    let text = String(decoding: input.standardInput, as: UTF8.self)
                    #expect(text.contains("\\ space\\,\\[test\\]\\!")); #expect(text.contains("@/")); #expect(!text.contains("@\""))
                }
            }
        }
        prepared.cleanup(); #expect(!FileManager.default.fileExists(atPath: folder.path))
        #expect(throws: (any Error).self) { try AttachmentPreparation(attachments, parent: parent.appendingPathComponent("missing")) }
    }

    @Test func fakeCLIReadsAttachmentsAndCleansCopiesOnExitErrorAndStop() async throws {
        let root = try directory(); defer { try? FileManager.default.removeItem(at: root) }
        let binary = root.appendingPathComponent("gemini")
        // Gemini metadata is static; this executable only captures local bytes.
        let script = #"""
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf '0.43.0\n'; exit 0; fi
        folder="$(/usr/bin/dirname "$0")"
        printf '%s\n' "$@" > "$folder/args"
        /bin/cat > "$folder/input"
        previous=''
        for value in "$@"; do
          if [ "$previous" = '--include-directories' ]; then
            /bin/cat "$value"/* > "$folder/copied" || exit 1
            # Publish readiness only after the bytes have been copied. The HOLD
            # case stops this process as soon as the stage marker appears.
            printf '%s' "$value" > "$folder/stage.ready"
            /bin/mv "$folder/stage.ready" "$folder/stage"
          fi
          previous="$value"
        done
        if /usr/bin/grep -q HOLD "$folder/input"; then /bin/sleep 60; fi
        if /usr/bin/grep -q FAIL "$folder/input"; then exit 1; fi
        printf '{"type":"result","status":"success"}\n'
        """#
        try Data(script.utf8).write(to: binary); try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        let service = ProviderService(binaryOverrides: ["gemini": binary]); let events = AttachmentEvents()
        let runner = ProcessRunner(providerService: service, pluginDirectory: root, onEvent: { events.add($0) })
        let workspace = Workspace(id: "space", name: "Fixture", path: root.path)
        let attachment = try AttachmentSupport.make(name: "image.png", data: png)
        do {
            for prompt in ["OK", "FAIL", "HOLD"] {
                try? FileManager.default.removeItem(at: root.appendingPathComponent("stage"))
                try await runner.start(request: StartRunRequest(sessionId: prompt, workspaceId: workspace.id, input: prompt, provider: "gemini", attachments: [attachment]), workspace: workspace)
                try await wait { FileManager.default.fileExists(atPath: root.appendingPathComponent("stage").path) }
                if prompt == "HOLD" { await runner.stop(id: prompt) }
                let status = prompt == "OK" ? "completed" : prompt == "FAIL" ? "error" : "stopped"
                try await wait { events.values().contains { $0.sessionId == prompt && $0.status == status } }
                #expect(try Data(contentsOf: root.appendingPathComponent("copied")) == png)
                let stage = try String(contentsOf: root.appendingPathComponent("stage"))
                #expect(!stage.isEmpty && stage.hasPrefix("/"))
                #expect(!FileManager.default.fileExists(atPath: stage))
                #expect(try String(contentsOf: root.appendingPathComponent("input")).contains("@/"))
            }
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }

    @Test func cancelledAttachmentAdmissionThrowsBeforeDraftCanBeCleared() async throws {
        let root = try directory(); defer { try? FileManager.default.removeItem(at: root) }
        let binary = root.appendingPathComponent("gemini")
        try Data("#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then sleep 0.4; printf '0.43.0\\n'; exit 0; fi\nprintf BAD_LAUNCH\n".utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        let service = ProviderService(binaryOverrides: ["gemini": binary]); let events = AttachmentEvents()
        let runner = ProcessRunner(providerService: service, pluginDirectory: root, onEvent: { events.add($0) })
        let workspace = Workspace(id: "space", name: "Fixture", path: root.path)
        let attachment = try AttachmentSupport.make(name: "image.png", data: png)
        let pending = Task { try await runner.start(request: StartRunRequest(sessionId: "cancelled", workspaceId: "space", input: "", provider: "gemini", attachments: [attachment]), workspace: workspace) }
        try await Task.sleep(nanoseconds: 50_000_000); await runner.stop(id: "cancelled")
        do { try await pending.value; Issue.record("Cancelled attachment admission returned success") } catch { #expect(error is CancellationError) }
        #expect(!events.values().contains { $0.entry?.text.contains("BAD_LAUNCH") == true })
        await runner.shutdown(); await service.shutdown()
    }
    @Test func remoteAttachmentRoundtripAndAuthenticatedBodyLimits() async throws {
        let attachment = try AttachmentSupport.make(name: "large.bin", data: Data(repeating: 255, count: 600 * 1024))
        let token = String(repeating: "a", count: 43)
        for modern in [false, true] {
            let root = try directory(); defer { try? FileManager.default.removeItem(at: root) }
            let remoteWorkspace = Workspace(id: "remote-space", name: "Remote", path: "/tmp/fixture")
            var runtime = ProviderOptions.fallbackRuntime("gemini"); runtime.capabilities.attachments = modern
            let info = WireInfo(hostId: "fixture-host", hostName: "Fixture", workspaces: [remoteWorkspace], runtime: RuntimeInfo(providers: [runtime]))
            let infoBody = try JSONEncoder().encode(info); let events = AttachmentEvents()
            let server = HTTPServer(address: "127.0.0.1", port: 0, requestBodyLimit: { head in RemoteService.attachmentBodyLimit(head, token: token, allowLoopback: true) }) { request in
                if request.method == "POST" {
                    events.add(RunEvent(sessionId: "captured", type: "log", entry: LogEntry(kind: "system", text: String(decoding: request.body, as: UTF8.self))))
                    return HTTPResponse(status: 400, body: Data("{}".utf8), headers: ["x-mighty-remote-version": "1"])
                }
                return HTTPResponse(status: 200, body: infoBody, headers: ["x-mighty-remote-version": "1"])
            }
            let unavailable = URL(fileURLWithPath: "/usr/bin/false")
            let providers = ProviderService(binaryOverrides: ["claude": unavailable, "codex": unavailable, "gemini": unavailable])
            let repository = StateRepository(directory: root, legacyStateURL: nil)
            let client = RemoteService(repository: repository, providers: providers, pluginDirectory: root, dataDirectory: root, onEvent: { _ in }, allowLoopbackForTests: true)
            do {
                let port = try await server.start(); let address = "http://127.0.0.1:\(port)"
                let connected = try await client.connectRemote(name: "Fixture", address: address, token: token)
                let connection = try #require(connected.connections.first)
                let imported = try await client.importWorkspace(connectionId: connection.id, workspaceId: remoteWorkspace.id)
                let request = StartRunRequest(sessionId: "attachment-check", workspaceId: imported.id, input: "", provider: "gemini", attachments: [attachment])
                do { try await client.start(request: request, workspace: imported); Issue.record("Capture fixture never accepts jobs") } catch { }
                let captured = try events.values().compactMap(\.entry).map { try JSONDecoder().decode(WireStart.self, from: Data($0.text.utf8)).request }
                #expect(captured.count == (modern ? 1 : 0))
                if modern {
                    #expect(captured.first?.attachments == [attachment]); #expect(captured.first?.workspaceId == remoteWorkspace.id)
                    #expect(try #require(events.values().first?.entry?.text.utf8.count) < 850 * 1024)
                }
                // Oversized uploads without a bearer, or on a different route,
                // must be rejected before the application handler receives bytes.
                for (route, bearer) in [("/v1/runs", "wrong"), ("/v1/runs/job/stop", token)] {
                    // Send only the declared length: the early response proves
                    // rejected bodies were never awaited or buffered in full.
                    let response = try await ProcessCapture.run(executable: URL(fileURLWithPath: "/usr/bin/curl"), arguments: ["--silent", "--max-time", "3", "--output", "/dev/null", "--write-out", "%{http_code}", "--request", "POST", "--header", "Authorization: Bearer " + bearer, "--header", "x-mighty-remote-version: 1", "--header", "Content-Length: 614400", address + route], timeout: 4)
                    #expect(String(decoding: response.stdout, as: UTF8.self) == "413")
                }
                #expect(events.values().count == (modern ? 1 : 0))
            } catch { await client.shutdown(); await server.stop(); await providers.shutdown(); throw error }
            await client.shutdown(); await server.stop(); await providers.shutdown()
        }
    }

}
