import Foundation
import MightyCore

extension AppStore {

    // MARK: - Load

    func refreshToolkit() async {
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
                try await toolkitStore.approve(entryId: id, executor: LiveToolkitCommandExecutor())
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
                    throw MightyError("도구 파일은 JSON 객체 하나여야 합니다: \(url.lastPathComponent)")
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
            let context = makeProbeContext()
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
            let context = makeProbeContext()
            let runner = ToolkitRunner(store: toolkitStore, probeContext: context)
            let results = await runner.run(plan: plan, executor: LiveToolkitRunnerExecutor())
            toolkitRunResults = results
            await refreshToolkit()
        }
    }

    // MARK: - Helpers

    private func makeProbeContext() -> ToolkitProbeContext {
        ToolkitProbeContext(
            home: FileManager.default.homeDirectoryForCurrentUser,
            environment: ProcessInfo.processInfo.environment,
            appDataDir: dataDirectory
        )
    }
}

// MARK: - Live executors (app target only, never used in tests)

private struct LiveToolkitCommandExecutor: ToolkitCommandExecutor {
    func run(_ argv: [String]) throws -> String {
        guard let name = argv.first else { return "" }
        guard let exeURL = resolveExecutable(name) else {
            throw MightyError("실행 파일을 찾을 수 없습니다: \(name)")
        }
        let process = Process()
        process.executableURL = exeURL
        process.arguments = Array(argv.dropFirst())
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        try process.run()
        process.waitUntilExit()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        return String(data: data, encoding: .utf8) ?? ""
    }

    private func resolveExecutable(_ name: String) -> URL? {
        if name.hasPrefix("/") { return URL(fileURLWithPath: name) }
        let paths = (ProcessInfo.processInfo.environment["PATH"] ?? "/usr/bin:/usr/local/bin")
            .split(separator: ":").map(String.init)
        for dir in paths {
            let url = URL(fileURLWithPath: dir).appendingPathComponent(name)
            if FileManager.default.isExecutableFile(atPath: url.path) { return url }
        }
        return nil
    }
}

private struct LiveToolkitRunnerExecutor: ToolkitRunnerExecutor {
    func run(_ argv: [String]) -> ToolkitCommandOutput {
        guard let name = argv.first, let exeURL = resolveExecutable(name) else {
            return .failure(output: "executable not found")
        }
        let process = Process()
        process.executableURL = exeURL
        process.arguments = Array(argv.dropFirst())
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        do {
            try process.run()
            process.waitUntilExit()
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            let output = String(data: data, encoding: .utf8) ?? ""
            return ToolkitCommandOutput(exitCode: process.terminationStatus, output: output)
        } catch {
            return .failure(output: error.localizedDescription)
        }
    }

    private func resolveExecutable(_ name: String) -> URL? {
        if name.hasPrefix("/") { return URL(fileURLWithPath: name) }
        let paths = (ProcessInfo.processInfo.environment["PATH"] ?? "/usr/bin:/usr/local/bin")
            .split(separator: ":").map(String.init)
        for dir in paths {
            let url = URL(fileURLWithPath: dir).appendingPathComponent(name)
            if FileManager.default.isExecutableFile(atPath: url.path) { return url }
        }
        return nil
    }
}
