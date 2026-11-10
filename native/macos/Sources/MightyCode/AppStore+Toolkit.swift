import Foundation
import MightyCore

extension AppStore {

    // MARK: - Load

    func refreshToolkit() async {
        toolkitProbeEnvironment = await toolkitEnvironment()
        let (entries, error) = await toolkitStore.list()
        var approvals: [String: ToolkitApproval] = [:]
        for entry in entries {
            if let approval = await toolkitStore.approval(for: entry) {
                approvals[entry.entryId] = approval
            }
        }
        toolkitEntries = entries
        toolkitFileError = error.map { $0.localizedDescription }
        toolkitApprovals = approvals
        toolkitRunResults = nil
    }

    // MARK: - Approval

    func approveToolkitEntry(_ id: String) {
        guard !toolkitRunning else { return }
        Task {
            do {
                let environment = await toolkitEnvironment()
                try await toolkitStore.approve(entryId: id, executor: LiveToolkitCommandExecutor(environment: environment))
                await refreshToolkit()
            } catch {
                toolkitFileError = error.localizedDescription
            }
        }
    }

    // MARK: - Add / remove / export / import

    /// Adds one entry read from a JSON file.  The entry lands unapproved.
    func addToolkitEntry(fromFile url: URL) {
        guard !toolkitRunning else { return }
        Task {
            do {
                let data = try Data(contentsOf: url)
                guard var object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
                    throw MightyError(L("settings.toolkit.errorEntryFileTemplate", ["name": url.lastPathComponent]))
                }
                object.removeValue(forKey: "approval")
                let entry = try ToolkitEntryDecoder.decode(object)
                try await toolkitStore.addEntry(entry)
                await refreshToolkit()
            } catch {
                toolkitFileError = error.localizedDescription
            }
        }
    }

    /// Delists a user entry.  Nothing is uninstalled or deleted on disk.
    func removeToolkitEntry(_ id: String) {
        guard !toolkitRunning else { return }
        Task {
            do {
                try await toolkitStore.removeEntry(id: id)
                await refreshToolkit()
            } catch {
                toolkitFileError = error.localizedDescription
            }
        }
    }

    /// Writes the user entries as a plain array with no approval data.
    func exportToolkit(to url: URL) {
        Task {
            do {
                let data = try await toolkitStore.exportData()
                try data.write(to: url, options: .atomic)
            } catch {
                toolkitFileError = error.localizedDescription
            }
        }
    }

    /// Adds every entry of an exported array as unapproved.
    func importToolkit(from url: URL) {
        guard !toolkitRunning else { return }
        Task {
            do {
                try await toolkitStore.importData(try Data(contentsOf: url))
                await refreshToolkit()
            } catch {
                toolkitFileError = error.localizedDescription
            }
        }
    }

    // MARK: - Install plan

    func planToolkitInstall() {
        guard !toolkitRunning else { return }
        Task {
            let context = makeProbeContext(environment: await toolkitEnvironment())
            let runner = ToolkitRunner(store: toolkitStore, probeContext: context)
            let plan = await runner.plan()
            toolkitPlan = plan
            toolkitShowConfirmation = true
        }
    }

    // MARK: - Run

    func runToolkitInstall() {
        guard !toolkitRunning else { return }
        toolkitRunning = true
        toolkitShowConfirmation = false
        let plan = toolkitPlan
        Task {
            defer { toolkitRunning = false }
            let environment = await toolkitEnvironment()
            let context = makeProbeContext(environment: environment)
            let runner = ToolkitRunner(store: toolkitStore, probeContext: context)
            let results = await runner.run(plan: plan, executor: LiveToolkitRunnerExecutor(environment: environment))
            toolkitRunResults = results
            await refreshToolkit()
        }
    }

    // MARK: - Helpers

    /// The login shell's environment, the one the agent CLIs run with.
    func toolkitEnvironment() async -> [String: String] {
        await providers.executionEnvironment().values
    }

    func makeProbeContext(environment: [String: String]) -> ToolkitProbeContext {
        ToolkitProbeContext(
            home: FileManager.default.homeDirectoryForCurrentUser,
            environment: environment,
            appDataDir: dataDirectory
        )
    }
}

// MARK: - Live executors (app target only, never used in tests)

/// Both run with the login shell's environment, so a tool installed where the
/// shell finds it (Homebrew, npm's global folder, ~/.local/bin) is found here too.
private struct LiveToolkitCommandExecutor: ToolkitCommandExecutor {
    let environment: [String: String]
    func run(_ argv: [String]) throws -> String {
        guard let name = argv.first else { return "" }
        guard ToolkitProcess.resolveExecutable(name, environment: environment) != nil else {
            throw MightyError(L("settings.toolkit.errorExecutableTemplate", ["name": name]))
        }
        return ToolkitProcess.run(argv, environment: environment).output
    }
}

private struct LiveToolkitRunnerExecutor: ToolkitRunnerExecutor {
    let environment: [String: String]
    func run(_ argv: [String]) -> ToolkitCommandOutput { ToolkitProcess.run(argv, environment: environment) }
}
