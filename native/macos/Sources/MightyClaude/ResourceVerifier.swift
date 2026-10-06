import Foundation
import MightyCore

/// Handles `--verify-resources`. Called before CEF, NSApplication, or any other
/// initialisation so the binary can be invoked headlessly by build and install
/// scripts. Exits 0 with VERIFY_RESOURCES_OK when every catalog and the pet resolve,
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
}

private struct StderrStream: TextOutputStream {
    mutating func write(_ string: String) {
        fputs(string, stderr)
    }
}
