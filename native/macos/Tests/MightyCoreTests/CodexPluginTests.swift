import Foundation
import Testing
import Darwin
@testable import MightyCore

@Suite(.serialized)
final class CodexPluginTests {
    private var directories: [URL] = []
    deinit { for directory in directories { try? FileManager.default.removeItem(at: directory) } }

    private struct Fixture {
        let root: URL
        let workspace: Workspace
        let executable: URL
        var environment: [String: String] {
            ["PATH": root.path + ":/usr/bin:/bin", "HOME": root.path,
             "CODEX_HOME": root.appendingPathComponent("codex-home").path, "FIXTURE_ROOT": root.path]
        }
        func service(readTimeout: TimeInterval = 3, operationTimeout: TimeInterval = 5, maximumBytes: Int = 8 * 1024 * 1024) -> CodexPluginService {
            CodexPluginService(environment: environment, executable: executable, readTimeout: readTimeout,
                               operationTimeout: operationTimeout, maximumBytes: maximumBytes)
        }
        func text(_ value: String, _ name: String) throws { try Data(value.utf8).write(to: root.appendingPathComponent(name)) }
        func json(_ value: Any, _ name: String) throws { try JSONSerialization.data(withJSONObject: value).write(to: root.appendingPathComponent(name)) }
        func read(_ name: String) -> String { (try? String(contentsOf: root.appendingPathComponent(name), encoding: .utf8)) ?? "" }
        func row(id: String = "format@sample", installed: Bool = false, policy: String = "AVAILABLE") -> [String: Any] {
            let parts = id.split(separator: "@")
            return ["pluginId": id, "name": String(parts[0]), "marketplaceName": String(parts[1]),
                    "version": "1.0.0", "installed": installed, "enabled": installed, "installPolicy": policy,
                    "authPolicy": "ON_INSTALL", "description": "Formats source files", "source": ["source": "local", "path": root.path]]
        }
        func listing(installed: [[String: Any]] = [], available: [[String: Any]]? = nil) throws {
            try json(["installed": installed, "available": available ?? [row()]], "listing")
        }
    }

    private func fixture() throws -> Fixture {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-codex-plugins-\(UUID().uuidString)")
        let directory = root.appendingPathComponent("workspace $(touch SENTINEL) with spaces")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        directories.append(root)
        let value = Fixture(root: root, workspace: Workspace(name: "Fixture", path: directory.path), executable: root.appendingPathComponent("codex"))
        try value.text("codex-cli 0.153.4\n", "version")
        try value.text("ok", "mode"); try value.listing()
        try value.json(["installed": [value.row(installed: true)], "available": []], "post-listing")
        try value.json(["marketplaces": [["name": "sample", "root": root.path, "marketplaceSource": ["sourceType": "git", "source": "https://example.invalid/catalog"]]]], "markets")
        try value.json(["pluginId": "format@sample", "name": "format", "marketplaceName": "sample", "version": "1.0.0", "installedPath": root.path, "authPolicy": "ON_INSTALL"], "install-response")
        try value.json(["selectedMarketplaces": ["sample"], "upgradedRoots": [root.path], "errors": []], "upgrade-response")
        let script = #"""
        #!/bin/sh
        printf 'cwd=%s\n' "$PWD" >> "$FIXTURE_ROOT/commands"
        printf 'arg=%s\n' "$@" >> "$FIXTURE_ROOT/commands"
        mode=$(/bin/cat "$FIXTURE_ROOT/mode")
        if [ "$1" = '--version' ]; then /bin/cat "$FIXTURE_ROOT/version"; exit 0; fi
        case "$*" in *--help)
          if [ "$mode" = 'unsupported' ]; then printf 'unknown subcommand\n'; exit 2; fi
          printf 'Usage: codex plugin --json --available\n'; exit 0;;
        esac
        if [ "$1 $2 $3" = 'plugin marketplace list' ]; then /bin/cat "$FIXTURE_ROOT/markets"; exit 0; fi
        if [ "$1 $2" = 'plugin list' ]; then
          if [ -f "$FIXTURE_ROOT/warning" ]; then /bin/cat "$FIXTURE_ROOT/warning" >&2; fi
          /bin/cat "$FIXTURE_ROOT/listing"; exit 0
        fi
        printf 'mutation=%s\n' "$*" >> "$FIXTURE_ROOT/mutations"
        printf '%s|%s\n' "$GIT_TERMINAL_PROMPT" "$CODEX_HOME" > "$FIXTURE_ROOT/environment"
        if [ "$mode" = 'hang' ]; then
          printf '%s' "$$" > "$FIXTURE_ROOT/pid"
          /bin/sleep 30
          exit 0
        fi
        if [ "$1 $2" = 'plugin add' ]; then
          /bin/cat "$FIXTURE_ROOT/install-response"
          if [ "$mode" = 'failure' ]; then exit 7; fi
          if [ "$mode" != 'unverified' ]; then /bin/cp "$FIXTURE_ROOT/post-listing" "$FIXTURE_ROOT/listing"; fi
          exit 0
        fi
        if [ "$1 $2 $3" = 'plugin marketplace upgrade' ]; then
          /bin/cat "$FIXTURE_ROOT/upgrade-response"
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
        for _ in 0..<3000 {
            if let pid = Int32(fixture.read("pid")), pid > 0 { return pid }
            try await Task.sleep(for: .milliseconds(10))
        }
        throw MightyError("Codex plugin fixture did not start")
    }

    @Test func listsUserPluginsAndRemoteCatalogPreservingWarningsWithoutMutation() async throws {
        let f = try fixture(); let service = f.service()
        var installed = f.row(installed: true); installed["enabled"] = false
        try f.listing(installed: [installed], available: [f.row(), f.row(id: "remote@openai-curated-remote")])
        try f.text("Remote catalog unavailable; using cache", "warning")
        let result = await service.snapshot(workspace: f.workspace)
        #expect(result.status == "ready")
        #expect(result.installed.count == 1)
        #expect(result.installed.first?.scope == "user")
        #expect(result.installed.first?.enabled == false)
        #expect(result.available.count == 2)
        #expect(result.available.first?.sourceKind == "local")
        #expect(result.marketplaces == [ClaudePluginMarketplace(name: "sample", sourceKind: "git")])
        #expect(result.diagnosticOutput.contains("Remote catalog unavailable"))
        #expect(result.detail.contains("CLI 경고"))
        #expect(f.read("mutations").isEmpty)
        await service.shutdown()
    }

    @Test func installUsesExactArgumentsUserScopeAndVerifiesRegistry() async throws {
        let f = try fixture(); let service = f.service()
        #expect(await service.install(pluginID: "format@sample", workspace: f.workspace).status == "succeeded")
        #expect(f.read("mutations") == "mutation=plugin add format@sample --json\n")
        #expect(!f.read("commands").contains("arg=--scope"))
        #expect(!f.read("commands").contains("arg=--yes"))
        let actualCWD = try #require(f.read("commands").split(whereSeparator: \.isNewline).first(where: { $0.hasPrefix("cwd=") })).dropFirst(4)
        let actualDirectory = try FileManager.default.attributesOfItem(atPath: String(actualCWD))
        let expectedDirectory = try FileManager.default.attributesOfItem(atPath: f.workspace.path)
        #expect(try #require(actualDirectory[.systemNumber] as? NSNumber) == #require(expectedDirectory[.systemNumber] as? NSNumber))
        #expect(try #require(actualDirectory[.systemFileNumber] as? NSNumber) == #require(expectedDirectory[.systemFileNumber] as? NSNumber))
        #expect(f.read("environment") == "0|\(f.root.appendingPathComponent("codex-home").path)\n")
        #expect(!FileManager.default.fileExists(atPath: f.root.appendingPathComponent("SENTINEL").path))
        #expect(!FileManager.default.fileExists(atPath: URL(fileURLWithPath: f.workspace.path).appendingPathComponent("SENTINEL").path))
        #expect(await service.install(pluginID: "format@sample", workspace: f.workspace).status == "skipped")
        #expect(f.read("mutations").split(whereSeparator: \.isNewline).count == 1)
        await service.shutdown()
    }

    @Test func remoteCatalogCanInstallWithoutRegisteredRemoteMarket() async throws {
        let f = try fixture(); let service = f.service(); let id = "format@openai-curated-remote"
        try f.listing(available: [f.row(id: id)])
        try f.json(["installed": [f.row(id: id, installed: true)], "available": []], "post-listing")
        try f.json(["pluginId": id, "name": "format", "marketplaceName": "openai-curated-remote", "installedPath": f.root.path], "install-response")
        #expect(await service.install(pluginID: id, workspace: f.workspace).status == "succeeded")
        await service.shutdown()
    }

    @Test func rejectsRemoteInvalidScopeNamesUnknownCatalogAndBlockedPolicies() async throws {
        let f = try fixture(); let service = f.service()
        var remote = f.workspace; remote.remote = RemoteWorkspaceReference(connectionId: "peer", workspaceId: "there", hostName: "Remote")
        #expect(await service.snapshot(workspace: remote).status == "remote")
        #expect(await service.install(pluginID: "format@sample", workspace: remote).status == "remote")
        #expect(await service.refreshMarketplace(name: "sample", workspace: remote).status == "remote")
        for id in ["-y@sample", "format@sample\n", "a@b@c", "format@$(touch)", "../format@sample"] {
            #expect(await service.install(pluginID: id, workspace: f.workspace).status == "failed")
        }
        for scope in ["local", "project", "managed"] {
            #expect(await service.install(pluginID: "format@sample", scope: scope, workspace: f.workspace).status == "failed")
        }
        #expect(f.read("commands").isEmpty)
        #expect(await service.install(pluginID: "unknown@sample", workspace: f.workspace).status == "failed")
        #expect(await service.refreshMarketplace(name: "unknown", workspace: f.workspace).status == "failed")
        for policy in ["NOT_AVAILABLE", "UNKNOWN", ""] {
            try f.listing(available: [f.row(policy: policy)])
            #expect(await service.snapshot(workspace: f.workspace).available.isEmpty)
            #expect(await service.install(pluginID: "format@sample", workspace: f.workspace).status == "failed")
        }
        #expect(f.read("mutations").isEmpty)
        await service.shutdown()
    }

    @Test func refreshValidatesStructuredResultAndSkipsNonGitSources() async throws {
        let f = try fixture(); let service = f.service()
        #expect(await service.refreshMarketplace(name: "sample", workspace: f.workspace).status == "succeeded")
        #expect(f.read("mutations") == "mutation=plugin marketplace upgrade sample --json\n")
        try f.json(["selectedMarketplaces": ["sample"], "upgradedRoots": [], "errors": [["marketplaceName": "sample", "message": "network error"]]], "upgrade-response")
        #expect(await service.refreshMarketplace(name: "sample", workspace: f.workspace).status == "failed")
        let mutations = f.read("mutations")
        try f.json(["marketplaces": [["name": "sample", "root": f.root.path, "marketplaceSource": ["sourceType": "local", "source": f.root.path]]]], "markets")
        #expect(await service.refreshMarketplace(name: "sample", workspace: f.workspace).status == "skipped")
        #expect(f.read("mutations") == mutations)
        await service.shutdown()
    }

    @Test func installFailureMalformedResponseAndUnverifiedSuccessStayFailed() async throws {
        let f = try fixture(); let service = f.service()
        for mode in ["failure", "unverified"] {
            try f.text(mode, "mode")
            #expect(await service.install(pluginID: "format@sample", workspace: f.workspace).status == "failed")
        }
        try f.text("ok", "mode")
        for response in ["{broken", "{}", "{\"pluginId\":\"other@sample\",\"installedPath\":\"/tmp\"}"] {
            try f.listing(); try f.text(response, "install-response")
            #expect(await service.install(pluginID: "format@sample", workspace: f.workspace).status == "failed")
        }
        await service.shutdown()
    }

    @Test func malformedOversizedOrConflictingCatalogDoesNotBecomeReadyEmpty() async throws {
        let f = try fixture(); let service = f.service(maximumBytes: 1024)
        for value in ["{broken", "[]", "{\"installed\":[],\"available\":\"wrong\"}", String(repeating: "x", count: 4096)] {
            try f.text(value, "listing")
            #expect(await service.snapshot(workspace: f.workspace).status == "failed")
        }
        try f.listing(available: [f.row(), f.row(policy: "NOT_AVAILABLE")])
        #expect(await service.snapshot(workspace: f.workspace).status == "failed")
        try f.listing(available: [["pluginId": "format@sample"]])
        #expect(await service.snapshot(workspace: f.workspace).status == "failed")
        try f.listing(); try f.text("[]", "markets")
        #expect(await service.snapshot(workspace: f.workspace).status == "failed")
        #expect(f.read("mutations").isEmpty)
        await service.shutdown()
    }

    @Test func cancellationStopsProcessRejectsDuplicatesAndClosesService() async throws {
        let f = try fixture(); try f.text("hang", "mode"); let service = f.service(operationTimeout: 20)
        let pending = Task { await service.install(pluginID: "format@sample", workspace: f.workspace) }
        let pid = try await waitForPID(f)
        #expect(await service.install(pluginID: "format@sample", workspace: f.workspace).status == "busy")
        #expect(await service.snapshot(workspace: f.workspace).status == "busy")
        await service.shutdown()
        #expect(await pending.value.status == "cancelled")
        #expect(Darwin.kill(pid, 0) != 0)
        #expect(await service.snapshot(workspace: f.workspace).status == "cancelled")
        #expect(await service.install(pluginID: "format@sample", workspace: f.workspace).status == "cancelled")
    }

    @Test func timeoutStopsInstallerAndAllowsRetry() async throws {
        let f = try fixture(); try f.text("hang", "mode"); let service = f.service(operationTimeout: 1.5)
        let pending = Task { await service.install(pluginID: "format@sample", workspace: f.workspace) }
        let pid = try await waitForPID(f)
        #expect(await pending.value.status == "failed")
        #expect(Darwin.kill(pid, 0) != 0)
        try f.text("ok", "mode")
        #expect(await service.install(pluginID: "format@sample", workspace: f.workspace).status == "succeeded")
        await service.shutdown()
    }

    @Test func missingUnsupportedCLIAndEmptySourcesAreExplained() async throws {
        let f = try fixture()
        let missing = CodexPluginService(environment: f.environment, executable: f.root.appendingPathComponent("missing"))
        #expect(await missing.snapshot(workspace: f.workspace).status == "missing")
        await missing.shutdown()
        let service = f.service(); try f.text("unsupported", "mode")
        #expect(await service.snapshot(workspace: f.workspace).status == "unsupported")
        try f.text("ok", "mode")
        try f.json(["marketplaces": []], "markets"); try f.json(["installed": [], "available": []], "listing")
        let result = await service.snapshot(workspace: f.workspace)
        #expect(result.status == "ready")
        #expect(result.detail.contains("marketplace add"))
        #expect(f.read("mutations").isEmpty)
        await service.shutdown()
    }
}
