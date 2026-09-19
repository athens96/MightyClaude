import Foundation
import Testing
@testable import MightyCore

struct StylesBundledTests {
    @Test func bundledManifestsAreFoundDecodedAndValidated() throws {
        let directories = BundledStyleSource.directories()
        #expect(!directories.isEmpty, "번들 스타일 폴더를 찾지 못했습니다. Package.swift의 리소스 선언을 확인하세요.")
        let files = StyleSourceScanner.bundled()
        #expect(files.count == 2 && files.allSatisfy { $0.source == .bundled })
        var ids: [String] = []
        for file in files {
            let manifest = try StyleManifestDecoder.decode(file.data, source: .bundled)
            try StyleManifestValidator.validate(manifest, source: .bundled, knownCapabilities: StyleCapabilityID.all)
            ids.append(manifest.id)
            #expect(file.hash.count == 64)
        }
        #expect(ids.sorted() == ["ouroboros", "paperthin"])
        let registry = StyleRegistry(styles: BundledStyles.shared.styles())
        #expect(registry.resolve("ouroboros")?.approval == .preApproved)
        #expect(registry.resolve("paperthin")?.manifest.actions.count == 28)
        // A missing bundle is nothing, never a trap.
        #expect(StyleSourceScanner.read(directory: URL(fileURLWithPath: "/nowhere-at-all"), source: .bundled, workspacePath: nil).isEmpty)
    }
}
