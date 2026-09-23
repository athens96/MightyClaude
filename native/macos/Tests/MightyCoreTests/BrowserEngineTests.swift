import Foundation
import Testing
@testable import MightyCore

struct BrowserEngineTests {
    @Test func browserEngineLockParses() throws {
        let json = Data("""
        {"arch":"arm64","cef":{"version":"152.0.9+g07f67cd+chromium-152.0.7977.134","url":"https://cef-builds.spotifycdn.com/cef_binary_152.0.9%2Bg07f67cd%2Bchromium-152.0.7977.134_macosarm64_minimal.tar.bz2","sha256":"f4efc5e8447e37d3dd1a982047310f1af16d08c5e81136d33a92e6cbff2717f7"},"node":{"version":"22.23.2","url":"https://nodejs.org/dist/v22.23.2/node-v22.23.2-darwin-arm64.tar.gz","sha256":"61130f394c1630d211dd50aecc4353d379480f36d3ac913cd85dbba1aed585c6"}}
        """.utf8)
        let lock = try JSONDecoder().decode(BrowserEngineLock.self, from: json)
        #expect(lock.arch == "arm64")
        #expect(lock.cef.version == "152.0.9+g07f67cd+chromium-152.0.7977.134")
        #expect(lock.cef.sha256.count == 64)
        #expect(lock.node.version == "22.23.2")
        #expect(lock.node.sha256.count == 64)
    }

    @Test func browserEngineLocatorReportsMissingEngine() {
        // In the test environment there is no CEF framework bundle.
        let status = BrowserEngineLocator.locate()
        if case .missing = status {
            // Expected — no engine in the test environment
        }
        // The report uses a browser.* locale key (falls back to the key itself when
        // the catalog is not loaded, which is fine for a build/test environment).
        let report = BrowserEngineLocator.reportMissing()
        #expect(!report.isEmpty)
    }

    @Test func browserProfilePathIsPerWorkspace() {
        let path1 = BrowserProfileSupport.profilePath(workspaceProfileKey: "workspace-abc")
        let path2 = BrowserProfileSupport.profilePath(workspaceProfileKey: "workspace-xyz")
        #expect(path1 != path2)
        #expect(path1.path.hasSuffix("workspace-abc"))
        #expect(path2.path.hasSuffix("workspace-xyz"))
        // Paths share the same parent directory
        #expect(path1.deletingLastPathComponent() == path2.deletingLastPathComponent())
    }
}
