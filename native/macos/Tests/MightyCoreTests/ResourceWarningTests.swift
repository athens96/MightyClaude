import Foundation
import Testing
@testable import MightyCore

struct ResourceWarningTests {
    // MARK: - Catalog warnings

    @Test func missingCatalogProducesWarning() {
        // A language code that will never exist in the bundle
        let result = ResourceHealthChecker.checkCatalog("__test_absent__")
        #expect(!result.found, "non-existent language must not resolve")
        let warning = result.warning
        #expect(warning != nil, "missing catalog must produce a warning")
        #expect(warning!.contains("__test_absent__.json"), "warning must name the missing resource")
        #expect(!result.triedPaths.isEmpty, "must report paths tried")
        // Warning text must mention the search paths
        for path in result.triedPaths.prefix(2) {
            #expect(warning!.contains(path) || warning!.contains("searched"),
                    "warning must reference paths tried")
        }
        // logWarning must not throw
        ResourceHealthChecker.logWarning(result)
    }

    @Test func emptyCatalogCandidateProducesWarning() throws {
        // Create a temp directory with an empty ko.json ({}) to simulate
        // the zero-key-catalog case that causes raw-key regression.
        let tmp = FileManager.default.temporaryDirectory
            .appendingPathComponent("ResourceWarningTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: tmp, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: tmp) }
        let emptyJSON = tmp.appendingPathComponent("ko.json")
        try "{}".data(using: .utf8)!.write(to: emptyJSON)
        let result = ResourceHealthChecker.checkCatalog("ko", overrideCandidates: [emptyJSON])
        #expect(!result.found, "empty JSON object must be treated as a miss")
        #expect(result.warning != nil)
        #expect(result.warning!.contains("ko.json"))
        ResourceHealthChecker.logWarning(result)
    }

    @Test func resolvedCatalogProducesNoWarning() {
        // The ko and en catalogs are bundled with the test target via Package.swift.
        let koResult = ResourceHealthChecker.checkCatalog("ko")
        let enResult = ResourceHealthChecker.checkCatalog("en")
        // Both must resolve in the test environment (SwiftPM copies them alongside the .xctest).
        #expect(koResult.found, "ko catalog must resolve in test bundle")
        #expect(enResult.found, "en catalog must resolve in test bundle")
        #expect(koResult.warning == nil, "found ko catalog must produce no warning")
        #expect(enResult.warning == nil, "found en catalog must produce no warning")
        ResourceHealthChecker.logWarning(koResult)
        ResourceHealthChecker.logWarning(enResult)
    }

    // MARK: - Pet warnings

    @Test func missingDefaultPetProducesWarning() throws {
        // Point the checker at an empty temp directory so the pet is guaranteed absent.
        let tmp = FileManager.default.temporaryDirectory
            .appendingPathComponent("PetWarningTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: tmp, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: tmp) }
        let fakePetDir = tmp.appendingPathComponent("pets/mighty-raccoon")
        let result = ResourceHealthChecker.checkDefaultPet(overrideCandidates: [fakePetDir])
        #expect(!result.found, "absent pet directory must not resolve")
        let warning = result.warning
        #expect(warning != nil, "missing default pet must produce a warning")
        #expect(warning!.contains("mighty-raccoon"), "warning must name the missing resource")
        #expect(!result.triedPaths.isEmpty, "must report paths tried")
        #expect(result.triedPaths.contains(fakePetDir.path), "tried paths must include the candidate")
        ResourceHealthChecker.logWarning(result)
    }

    @Test func resolvedDefaultPetProducesNoWarning() {
        // The pet lives under assets/pets/mighty-raccoon relative to the project root.
        // Tests launched with --package-path native/macos run from the repo root (cwd),
        // so the cwd-relative candidate in checkDefaultPet() resolves.
        let result = ResourceHealthChecker.checkDefaultPet()
        if result.found {
            #expect(result.warning == nil, "found default pet must produce no warning")
        }
        // Whether or not the pet is found in this environment, logWarning must not throw.
        ResourceHealthChecker.logWarning(result)
    }

    // MARK: - ResourceCheckResult invariants

    @Test func warningContainsResourceNameAndPaths() {
        let paths = ["/a/b/ko.json", "/c/d/ko.json"]
        let result = ResourceCheckResult(resource: "ko.json", resolvedPath: nil, triedPaths: paths)
        let warning = result.warning
        #expect(warning != nil)
        #expect(warning!.contains("ko.json"))
        #expect(warning!.contains("/a/b/ko.json"))
        #expect(warning!.contains("/c/d/ko.json"))
    }

    @Test func noWarningWhenResourceFound() {
        let result = ResourceCheckResult(resource: "ko.json",
                                         resolvedPath: "/some/path/ko.json",
                                         triedPaths: ["/some/path/ko.json"])
        #expect(result.found)
        #expect(result.warning == nil)
        ResourceHealthChecker.logWarning(result)
    }

    @Test func nothingThrows() {
        // Exercise both paths without any assertions that can fail — proves no traps.
        _ = ResourceHealthChecker.checkCatalog("ko")
        _ = ResourceHealthChecker.checkCatalog("en")
        _ = ResourceHealthChecker.checkDefaultPet()
    }
}
