import AppKit
import Darwin
import MightyCore

extension AppStore {
    /// `--setup-smoke-test --profile <dir>`: opens the setup sheet over fixed probe
    /// results (nothing on the machine is probed), reads its rows, picks a tool
    /// that needs Homebrew, and opens the confirmation without running it.
    /// Writes setup-smoke-result.json; nothing is installed and no script is written.
    func runSetupSmokeTest() async {
        let args = ProcessInfo.processInfo.arguments
        var result: [String: Any] = ["passed": false, "installRan": false, "aiRequestSent": false]
        do {
            guard args.contains("--profile") else { throw MightyError("the setup smoke needs a temporary --profile") }
            try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true)
            guard let catalog = loadSetupCatalog() else { throw MightyError(setupMessage ?? "no setup catalog") }
            var fixed: [String: SetupProbeResult] = [:]
            for item in catalog.items { fixed[item.id] = .missing }
            fixed["claude"] = .outdated(version: "2.0.0 (Claude Code)", minimum: "2.1.271")
            fixed["codex"] = .installed(version: "codex-cli 0.160.0")
            fixed["git"] = .installed(version: nil)
            setupFixedResults = fixed
            await refreshSetup()
            func require(_ condition: Bool, _ message: String) throws { if !condition { throw MightyError(message) } }
            try require(SetupFirstRun.shouldPresent(catalog: catalog, results: setupResults, platform: .macos, neverShow: false), "an out-of-date required Claude does not open the screen")
            try require(!SetupFirstRun.shouldPresent(catalog: catalog, results: setupResults, platform: .macos, neverShow: true), "don't-show-again does not keep the screen closed")
            result["firstRunDecision"] = true

            openSetup(focus: "agy")
            try await Task.sleep(for: .milliseconds(1500))
            toggleSetupSelection("gh")
            try await Task.sleep(for: .milliseconds(700))
            guard let sheet = NSApp.windows.compactMap(\.attachedSheet).first, let content = sheet.contentView else { throw MightyError("the setup sheet did not open") }
            let probes = Self.setupProbeStates(content)
            result["probes"] = probes.keys.sorted()
            let rows = ["claude-outdated", "codex-installed", "agy-missing", "git-installed", "homebrew-missing", "gh-missing", "tailscale-missing", "glab-missing"]
            let missingRows = rows.filter { probes["setup-row-" + $0] == nil }
            try require(missingRows.isEmpty, "rows not shown as expected: \(missingRows)")
            result["rows"] = true
            // The focused CLI and the picked tool are checked, Homebrew comes with gh, glab stays unchecked.
            try require(probes["setup-check-agy"] == true && probes["setup-check-gh"] == true && probes["setup-check-homebrew"] == true && probes["setup-check-glab"] == false,
                        "checkboxes: agy \(String(describing: probes["setup-check-agy"])), gh \(String(describing: probes["setup-check-gh"])), homebrew \(String(describing: probes["setup-check-homebrew"])), glab \(String(describing: probes["setup-check-glab"]))")
            try require(probes["setup-update-claude"] != nil, "the out-of-date Claude offers no update")
            try require(probes["setup-page-tailscale"] != nil, "Tailscale offers no download page")
            try require(probes["setup-install"] == true, "the install button is not enabled")
            result["actions"] = true
            try require(setupPlanIds == ["agy", "homebrew", "gh"], "plan order \(setupPlanIds)")
            result["planOrder"] = setupPlanIds
            result["sheetScreenshot"] = try captureSmokeWindow(sheet, filename: "setup-sheet.png").path

            requestSetupInstall()
            try await Task.sleep(for: .milliseconds(900))
            let steps = setupConfirmation?.steps ?? []
            try require(steps.map(\.itemId) == ["agy", "homebrew", "gh"], "confirmation steps \(steps.map(\.itemId))")
            try require(steps.allSatisfy { !$0.body.contains("sudo") }, "a step uses sudo")
            result["confirmation"] = steps.map { ["item": $0.itemId, "command": $0.body] }
            if let confirm = sheet.attachedSheet { result["confirmationScreenshot"] = try captureSmokeWindow(confirm, filename: "setup-confirmation.png").path }
            // Cancelled: the smoke never runs an install.
            setupConfirmation = nil
            try await Task.sleep(for: .milliseconds(400))
            dismissSetup(neverShowAgain: false)
            try require(setupRun == nil, "an install started")
            result["passed"] = true
        } catch { result["error"] = error.localizedDescription }
        do {
            try JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys])
                .write(to: dataDirectory.appendingPathComponent("setup-smoke-result.json"), options: .atomic)
        } catch { NSLog("Setup smoke report: %@", error.localizedDescription) }
        if args.contains("--smoke-exit") {
            await shutdown()
            Darwin.exit(result["passed"] as? Bool == true ? 0 : 1)
        }
    }

    /// The AppKit twins of the sheet's rows and controls (`AccessibilityStateProbe`):
    /// identifier → enabled. SwiftUI's own accessibility nodes are not built on a
    /// runner with no assistive client, so the smoke reads these.
    private static func setupProbeStates(_ view: NSView) -> [String: Bool] {
        var states: [String: Bool] = [:]
        if let probe = view as? AccessibilityStateProbeView, let id = probe.accessibilityIdentifier() as String?, id.hasPrefix("setup-") { states[id] = probe.enabled }
        for child in view.subviews { states.merge(setupProbeStates(child)) { first, _ in first } }
        return states
    }
}
