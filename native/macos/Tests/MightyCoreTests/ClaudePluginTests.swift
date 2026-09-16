import Foundation
import Testing
import Darwin
@testable import MightyCore

@Suite(.serialized)
final class ClaudePluginTests {
    private var directories: [URL] = []
    deinit { for directory in directories { try? FileManager.default.removeItem(at: directory) } }

    private struct Fixture {
        let root: URL
        let workspace: Workspace
        let executable: URL
        var environment: [String: String] {
            ["PATH": root.path + ":/usr/bin:/bin", "HOME": root.path, "FIXTURE_ROOT": root.path,
             "FORCE_AUTOUPDATE_PLUGINS": "1"]
        }
        func service(readTimeout: TimeInterval = 3, operationTimeout: TimeInterval = 5, maximumBytes: Int = 8 * 1024 * 1024) -> ClaudePluginService {
            ClaudePluginService(environment: environment, executable: executable, readTimeout: readTimeout,
                                operationTimeout: operationTimeout, maximumBytes: maximumBytes)
        }
        func text(_ value: String, _ name: String) throws { try Data(value.utf8).write(to: root.appendingPathComponent(name)) }
        func json(_ value: Any, _ name: String) throws { try JSONSerialization.data(withJSONObject: value).write(to: root.appendingPathComponent(name)) }
        func read(_ name: String) -> String { (try? String(contentsOf: root.appendingPathComponent(name), encoding: .utf8)) ?? "" }
        var catalog: [[String: Any]] { [["pluginId": "format@sample", "name": "format", "marketplaceName": "sample", "description": "Formats source files", "source": ["source": "github", "repo": "fixture/format"]]] }
        func listing(_ rows: [[String: Any]] = []) throws { try json(["installed": rows, "available": catalog], "listing") }
    }

    private func fixture() throws -> Fixture {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-plugins-\(UUID().uuidString)")
        let directory = root.appendingPathComponent("workspace $(touch SENTINEL) with spaces")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        directories.append(root)
        let value = Fixture(root: root, workspace: Workspace(name: "Fixture", path: directory.path), executable: root.appendingPathComponent("claude"))
        try value.text("2.1.273 (Claude Code)\n", "version")
        try value.text("ok", "mode"); try value.listing()
        try value.json([["name": "sample", "source": "github", "repo": "fixture/catalog"]], "markets")
        try value.text("{\"command\":\"install\",\"outcome\":\"ok\",\"message\":\"Installed\"}\n", "install-response")
        let script = #"""
        #!/bin/sh
        printf 'cwd=%s\n' "$PWD" >> "$FIXTURE_ROOT/commands"
        printf 'arg=%s\n' "$@" >> "$FIXTURE_ROOT/commands"
        if [ "$1" = '--version' ]; then /bin/cat "$FIXTURE_ROOT/version"; exit 0; fi
        if [ "$1 $2 $3" = 'plugin marketplace list' ]; then /bin/cat "$FIXTURE_ROOT/markets"; exit 0; fi
        if [ "$1 $2" = 'plugin list' ]; then /bin/cat "$FIXTURE_ROOT/listing"; exit 0; fi
        printf 'mutation=%s\n' "$*" >> "$FIXTURE_ROOT/mutations"
        printf '%s|%s|%s|%s|%s\n' "$DISABLE_AUTOUPDATER" "$CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL" "$CLAUDE_CODE_DISABLE_BACKGROUND_TASKS" "$GIT_TERMINAL_PROMPT" "${FORCE_AUTOUPDATE_PLUGINS-unset}" > "$FIXTURE_ROOT/environment"
        mode=$(/bin/cat "$FIXTURE_ROOT/mode")
        if [ "$mode" = 'hang' ]; then
          printf '%s' "$$" > "$FIXTURE_ROOT/pid"
          /bin/sleep 30
          exit 0
        fi
        if [ "$1 $2" = 'plugin install' ]; then
          /bin/cat "$FIXTURE_ROOT/install-response"
          if [ "$mode" = 'failure' ]; then exit 7; fi
          exit 0
        fi
        if [ "$1 $2 $3" = 'plugin marketplace update' ]; then
          printf 'Updated selected marketplace\n'
          if [ "$mode" = 'failure' ]; then exit 9; fi
          exit 0
        fi
        exit 91
        """#
        try Data(script.utf8).write(to: value.executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: value.executable.path)
        return value
    }

    private func waitForPID(_ fixture: Fixture) async throws -> pid_t {
        for _ in 0..<500 {
            if let pid = Int32(fixture.read("pid")), pid > 0 { return pid }
            try await Task.sleep(for: .milliseconds(10))
        }
        throw MightyError("Plugin fixture did not start")
    }

    @Test func listsCurrentWorkspaceScopesAndCachedCatalogWithoutMutation() async throws {
        let f = try fixture()
        let other = f.root.appendingPathComponent("other"); try FileManager.default.createDirectory(at: other, withIntermediateDirectories: true)
        let rows: [[String: Any]] = [
            ["id": "format@sample", "scope": "user", "version": "1.0.0", "enabled": false],
            ["id": "format@sample", "scope": "project", "projectPath": f.workspace.path, "enabled": true, "errors": ["fixture warning"]],
            ["id": "format@sample", "scope": "local", "projectPath": f.root.path, "enabled": true],
            ["id": "format@sample", "scope": "local", "projectPath": other.path, "enabled": true],
            ["id": "invalid;touch@sample", "scope": "user", "enabled": true]
        ]
        try f.listing(rows + [rows[0]])
        let service = f.service(); let result = await service.snapshot(workspace: f.workspace)
        #expect(result.status == "ready")
        #expect(result.installed.count == 3)
        #expect(Set(result.installed.map(\.id)).count == 3)
        #expect(result.installed.first(where: { $0.scope == "user" })?.enabled == false)
        #expect(result.installed.allSatisfy { $0.description == "Formats source files" })
        #expect(result.installed.first(where: { $0.scope == "project" })?.errors == ["fixture warning"])
        #expect(result.available.first?.id == "format@sample")
        #expect(result.marketplaces == [ClaudePluginMarketplace(name: "sample", sourceKind: "github")])
        #expect(f.read("mutations").isEmpty)
        #expect(f.read("commands").contains("arg=--available"))
        await service.shutdown()
    }

    @Test func installUsesExactArgumentsLocalDefaultAndWorkspaceCWD() async throws {
        let f = try fixture(); let service = f.service()
        let result = await service.install(pluginID: "format@sample", workspace: f.workspace)
        #expect(result.status == "succeeded")
        #expect(f.read("mutations") == "mutation=plugin install format@sample --scope local --json\n")
        let actualCWD = try #require(f.read("commands").split(whereSeparator: \.isNewline).first(where: { $0.hasPrefix("cwd=") })).dropFirst(4)
        let actualDirectory = try FileManager.default.attributesOfItem(atPath: String(actualCWD))
        let expectedDirectory = try FileManager.default.attributesOfItem(atPath: f.workspace.path)
        let actualDevice = try #require(actualDirectory[.systemNumber] as? NSNumber)
        let expectedDevice = try #require(expectedDirectory[.systemNumber] as? NSNumber)
        let actualFile = try #require(actualDirectory[.systemFileNumber] as? NSNumber)
        let expectedFile = try #require(expectedDirectory[.systemFileNumber] as? NSNumber)
        #expect(actualDevice == expectedDevice)
        #expect(actualFile == expectedFile)
        #expect(f.read("environment") == "1|1|1|0|unset\n")
        #expect(!f.read("commands").contains("arg=--yes"))
        #expect(!f.read("commands").contains("arg=--accept-command"))
        #expect(!FileManager.default.fileExists(atPath: f.root.appendingPathComponent("SENTINEL").path))
        for scope in ["project", "user"] {
            #expect(await service.install(pluginID: "format@sample", scope: scope, workspace: f.workspace).status == "succeeded")
            #expect(f.read("mutations").contains("--scope " + scope + " --json"))
        }
        await service.shutdown()
    }

    @Test func rejectsRemoteInvalidNamesAndUnknownCatalogWithoutMutation() async throws {
        let f = try fixture(); let service = f.service()
        var remote = f.workspace; remote.remote = RemoteWorkspaceReference(connectionId: "peer", workspaceId: "there", hostName: "Remote")
        #expect(await service.snapshot(workspace: remote).status == "remote")
        #expect(await service.install(pluginID: "format@sample", workspace: remote).status == "remote")
        #expect(await service.refreshMarketplace(name: "sample", workspace: remote).status == "remote")
        #expect(f.read("commands").isEmpty)
        for id in ["-y@sample", "format@sample\n", "a@b@c", "format@$(touch)", "../format@sample"] {
            #expect(await service.install(pluginID: id, workspace: f.workspace).status == "failed")
        }
        #expect(await service.install(pluginID: "format@sample", scope: "managed", workspace: f.workspace).status == "failed")
        #expect(f.read("commands").isEmpty)
        #expect(await service.install(pluginID: "unknown@sample", workspace: f.workspace).status == "failed")
        #expect(await service.refreshMarketplace(name: "unknown", workspace: f.workspace).status == "failed")
        #expect(f.read("mutations").isEmpty)
        await service.shutdown()
    }

    @Test func duplicateInstallIsScopedAndOtherWorkspacesDoNotBlock() async throws {
        let f = try fixture(); let service = f.service()
        try f.listing([["id": "format@sample", "scope": "local", "projectPath": f.workspace.path, "enabled": true]])
        #expect(await service.install(pluginID: "format@sample", workspace: f.workspace).status == "skipped")
        #expect(f.read("mutations").isEmpty)
        #expect(await service.install(pluginID: "format@sample", scope: "user", workspace: f.workspace).status == "succeeded")
        // An ancestor's enabled plugin is visible, but installing explicitly
        // into this nested workspace is a different local setting.
        try f.listing([["id": "format@sample", "scope": "local", "projectPath": f.root.path, "enabled": true]])
        #expect(await service.snapshot(workspace: f.workspace).installed.count == 1)
        #expect(await service.install(pluginID: "format@sample", workspace: f.workspace).status == "succeeded")
        try f.listing([["id": "format@sample", "scope": "local", "projectPath": f.root.appendingPathComponent("elsewhere").path, "enabled": true]])
        #expect(await service.install(pluginID: "format@sample", workspace: f.workspace).status == "succeeded")
        await service.shutdown()
    }

    @Test func refreshRunsOnlyNamedKnownMarketplaceAndFailureRemainsVisible() async throws {
        let f = try fixture(); let service = f.service()
        #expect(await service.snapshot(workspace: f.workspace).status == "ready")
        #expect(f.read("mutations").isEmpty)
        #expect(await service.refreshMarketplace(name: "sample", workspace: f.workspace).status == "succeeded")
        #expect(f.read("mutations") == "mutation=plugin marketplace update sample\n")
        try f.text("failure", "mode")
        #expect(await service.refreshMarketplace(name: "sample", workspace: f.workspace).status == "failed")
        await service.shutdown()
    }

    @Test func structuredInstallParsesLastLineAndNeverAutoAcceptsCommands() async throws {
        let f = try fixture(); let service = f.service()
        try f.text("Informational preamble\n{\"command\":\"install\",\"outcome\":\"ok\",\"message\":\"ok\",\"pluginId\":\"format@sample\",\"scope\":\"local\"}\n", "install-response")
        #expect(await service.install(pluginID: "format@sample", workspace: f.workspace).status == "succeeded")
        try f.text("Displayed command\n{\"command\":\"install\",\"outcome\":\"failed\",\"message\":\"confirmation required\",\"shownCommand\":{\"sha256\":\"fake-digest\"}}\n", "install-response")
        try f.text("failure", "mode")
        let confirmation = await service.install(pluginID: "format@sample", workspace: f.workspace)
        #expect(confirmation.status == "failed")
        #expect(confirmation.detail.contains("명령 실행 동의"))
        #expect(!f.read("commands").contains("arg=--accept-command"))
        try f.text("ok", "mode")
        for response in ["{broken", "{\"command\":\"update\",\"outcome\":\"ok\"}", "{\"command\":\"install\",\"outcome\":\"ok\",\"pluginId\":\"other@sample\"}"] {
            try f.text(response, "install-response")
            #expect(await service.install(pluginID: "format@sample", workspace: f.workspace).status == "failed")
        }
        await service.shutdown()
    }

    @Test func malformedOrOversizedCatalogDoesNotBecomeReadyEmpty() async throws {
        let f = try fixture(); let service = f.service(maximumBytes: 1024)
        for value in ["{broken", "[]", "{\"installed\":[],\"available\":\"wrong\"}", String(repeating: "x", count: 4096)] {
            try f.text(value, "listing")
            #expect(await service.snapshot(workspace: f.workspace).status == "failed")
        }
        try f.listing(); try f.text("{}", "markets")
        #expect(await service.snapshot(workspace: f.workspace).status == "failed")
        #expect(f.read("mutations").isEmpty)
        await service.shutdown()
    }

    @Test func cancellationRejectsDuplicatesStopsProcessAndPreventsShutdownAdmission() async throws {
        let f = try fixture(); try f.text("hang", "mode"); let service = f.service(operationTimeout: 20)
        let pending = Task { await service.install(pluginID: "format@sample", workspace: f.workspace) }
        let pid = try await waitForPID(f)
        #expect(await service.install(pluginID: "format@sample", workspace: f.workspace).status == "busy")
        #expect(await service.snapshot(workspace: f.workspace).status == "busy")
        await service.shutdown()
        #expect(await pending.value.status == "cancelled")
        #expect(Darwin.kill(pid, 0) != 0)
        #expect(await service.install(pluginID: "format@sample", workspace: f.workspace).status == "cancelled")
        #expect(await service.snapshot(workspace: f.workspace).status == "cancelled")
        #expect(f.read("mutations").split(whereSeparator: \.isNewline).count == 1)
    }

    @Test func timeoutStopsTheInstallerAndAllowsNextOperation() async throws {
        let f = try fixture(); try f.text("hang", "mode"); let service = f.service(operationTimeout: 1.5)
        let pending = Task { await service.install(pluginID: "format@sample", workspace: f.workspace) }
        let pid = try await waitForPID(f)
        #expect(await pending.value.status == "failed")
        #expect(Darwin.kill(pid, 0) != 0)
        try f.text("ok", "mode")
        #expect(await service.install(pluginID: "format@sample", workspace: f.workspace).status == "succeeded")
        await service.shutdown()
    }

    @Test func missingOrOldCLIAndEmptySourcesAreExplainedWithoutInstalling() async throws {
        let f = try fixture()
        let missing = ClaudePluginService(environment: f.environment, executable: f.root.appendingPathComponent("missing"))
        #expect(await missing.snapshot(workspace: f.workspace).status == "missing")
        await missing.shutdown()
        try f.text("2.1.267 (Claude Code)\n", "version")
        let service = f.service()
        #expect(await service.snapshot(workspace: f.workspace).status == "unsupported")
        try f.text("2.1.273 (Claude Code)\n", "version")
        try f.json([], "markets"); try f.json(["installed": [], "available": []], "listing")
        let result = await service.snapshot(workspace: f.workspace)
        #expect(result.status == "ready")
        #expect(result.detail.contains("marketplace add"))
        #expect(result.marketplaces.isEmpty)
        #expect(f.read("mutations").isEmpty)
        await service.shutdown()
    }
}
