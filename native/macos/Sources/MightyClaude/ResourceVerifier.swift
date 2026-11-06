import Foundation
import GhosttyTerminal
import MightyCore

/// Handles `--verify-resources`. Called before CEF, NSApplication, or any other
/// initialisation so the binary can be invoked headlessly by build and install
/// scripts. Exits 0 with VERIFY_RESOURCES_OK when every locale catalog, the help
/// files, the setup catalog, the pet and Ghostty's runtime resources resolve,
/// exits non-zero otherwise.
enum ResourceVerifier {
    static func run() -> Never {
        var err = StderrStream()
        // Every language's catalog must exist and parse as a JSON object; a complete
        // language's must also hold keys, while a translation may still be empty.
        let catalogs = AppLanguage.allCases.filter { $0 != .system }
            .map { ResourceHealthChecker.checkCatalog($0.rawValue, allowEmpty: !$0.isComplete) }
        let checks: [ResourceCheckResult] = catalogs + [ResourceHealthChecker.checkDefaultPet()]
            + HelpCaptureResources.files.map(ResourceHealthChecker.checkHelpFile)
            + [ResourceHealthChecker.checkSetupCatalog(), checkGhosttyResources()]
        var allFound = true
        for result in checks {
            if result.found {
                print("\(result.resource): \(result.resolvedPath!)")
            } else {
                let tried = result.triedPaths.map { "  \($0)" }.joined(separator: "\n")
                print("\(result.resource): MISSING\n  searched:\n\(tried)", to: &err)
                allFound = false
            }
        }
        if allFound {
            print("VERIFY_RESOURCES_OK")
            exit(0)
        } else {
            let missing = checks.filter { !$0.found }.map { $0.resource }.joined(separator: ", ")
            print("VERIFY_RESOURCES_FAILED: missing \(missing)", to: &err)
            exit(1)
        }
    }

    /// The terminal's shell integration and terminfo. Without them every pane
    /// starts a bare shell, and before the vendored patch the app crashed.
    private static func checkGhosttyResources() -> ResourceCheckResult {
        let resolved = GhosttyRuntimeResources.terminfoDirectoryURL == nil ? nil : GhosttyRuntimeResources.directoryURL?.path
        let roots = [Bundle.main.resourceURL, Bundle.main.bundleURL].compactMap { $0 }
        return ResourceCheckResult(resource: "Ghostty runtime resources", resolvedPath: resolved,
                                   triedPaths: roots.map { $0.appendingPathComponent("GhosttyKit_GhosttyTerminal.bundle").path })
    }
}

private struct StderrStream: TextOutputStream {
    mutating func write(_ string: String) {
        fputs(string, stderr)
    }
}
