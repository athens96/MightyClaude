import Foundation
import Testing
@testable import MightyCore

/// 적합성 코퍼스(`styles/conformance/`)를 고정된 엔진으로 인증한다.
///
/// - `valid/` 폴더의 모든 `.json` 파일은 `.user` 출처로 오류 없이 디코딩돼야 한다.
/// - `invalid/` 폴더의 모든 `.json` 파일은 파일 이름 줄기(예: `E_RESERVED_ID`)와
///   같은 오류 코드로 거절돼야 한다.
/// - `E_TOO_LARGE`(256 KB 초과 파일)는 정적 파일로 보관하기 어려워 테스트 내에서
///   동적으로 생성한다. `E_ID_COLLISION`은 디코더가 아닌 레지스트리가 판정하므로
///   코퍼스에 없고 `StyleManifestTests`가 별도로 검증한다.
struct StylesThirdPartyConformanceTests {
    private static var conformanceDirectory: URL {
        StyleGolden.stylesDirectory.appendingPathComponent("conformance", isDirectory: true)
    }
    private static var validDirectory: URL {
        conformanceDirectory.appendingPathComponent("valid", isDirectory: true)
    }
    private static var invalidDirectory: URL {
        conformanceDirectory.appendingPathComponent("invalid", isDirectory: true)
    }

    /// 코퍼스 폴더가 존재하고 비어 있지 않음을 먼저 확인한다.
    /// 잘못된 경로로 `valid/`가 빈 채로 통과하는 일을 막는다.
    @Test func corpusDirectoriesExistAndArePopulated() throws {
        let fm = FileManager.default
        #expect(fm.fileExists(atPath: Self.validDirectory.path), "valid/ 폴더를 찾지 못했습니다: \(Self.validDirectory.path)")
        #expect(fm.fileExists(atPath: Self.invalidDirectory.path), "invalid/ 폴더를 찾지 못했습니다: \(Self.invalidDirectory.path)")
        let validFiles = try fm.contentsOfDirectory(atPath: Self.validDirectory.path).filter { $0.hasSuffix(".json") }
        let invalidFiles = try fm.contentsOfDirectory(atPath: Self.invalidDirectory.path).filter { $0.hasSuffix(".json") }
        #expect(!validFiles.isEmpty, "valid/ 폴더에 .json 파일이 없습니다")
        #expect(!invalidFiles.isEmpty, "invalid/ 폴더에 .json 파일이 없습니다")
    }

    /// 유효 벡터: 엔진이 오류 없이 받아들여야 한다.
    @Test func validCorpusFilesDecodeWithoutError() throws {
        let fm = FileManager.default
        guard fm.fileExists(atPath: Self.validDirectory.path) else { return }
        let files = try fm.contentsOfDirectory(at: Self.validDirectory,
                                               includingPropertiesForKeys: nil,
                                               options: [.skipsHiddenFiles])
            .filter { $0.pathExtension == "json" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        #expect(!files.isEmpty, "valid/ 폴더에 테스트할 파일이 없습니다")
        for url in files {
            let data = try Data(contentsOf: url)
            let name = url.lastPathComponent
            do {
                let manifest = try StyleManifestDecoder.decode(data, source: .user)
                #expect(manifest.schema == 1, "\(name): schema가 1이 아닙니다")
            } catch let error as StyleManifestError {
                Issue.record("\(name): 유효 파일에서 예상치 못한 오류 \(error.code) — \(error.message)")
            }
        }
    }

    /// 무효 벡터: 파일 이름 줄기와 같은 오류 코드로 거절돼야 한다.
    @Test func invalidCorpusFilesProduceExpectedErrorCodes() throws {
        let fm = FileManager.default
        guard fm.fileExists(atPath: Self.invalidDirectory.path) else { return }
        let files = try fm.contentsOfDirectory(at: Self.invalidDirectory,
                                               includingPropertiesForKeys: nil,
                                               options: [.skipsHiddenFiles])
            .filter { $0.pathExtension == "json" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        #expect(!files.isEmpty, "invalid/ 폴더에 테스트할 파일이 없습니다")
        for url in files {
            let expectedCode = url.deletingPathExtension().lastPathComponent
            let data = try Data(contentsOf: url)
            let name = url.lastPathComponent
            do {
                _ = try StyleManifestDecoder.decode(data, source: .user)
                Issue.record("\(name): 거절돼야 할 파일이 통과했습니다 (기대: \(expectedCode))")
            } catch let error as StyleManifestError {
                #expect(error.code == expectedCode,
                        "\(name): 오류 코드가 다릅니다 — 기대 \(expectedCode), 실제 \(error.code)")
            } catch {
                Issue.record("\(name): StyleManifestError가 아닌 오류 — \(error)")
            }
        }
    }

    /// E_TOO_LARGE: 256 KB를 넘는 파일은 정적으로 보관하기 어려워 동적으로 생성한다.
    @Test func tooLargeIsRejectedDynamically() {
        let data = Data(repeating: UInt8(ascii: " "), count: StyleLimits.maximumBytes + 1)
        do {
            _ = try StyleManifestDecoder.decode(data, source: .user)
            Issue.record("256 KB 초과 데이터가 통과했습니다")
        } catch let error as StyleManifestError {
            #expect(error.code == "E_TOO_LARGE")
        } catch {
            Issue.record("StyleManifestError가 아닌 오류 — \(error)")
        }
    }

    /// 코퍼스 무효 벡터 수가 예상 범위 안에 있고, 디코더 접근 코드 전체를
    /// 아우른다(E_TOO_LARGE와 E_ID_COLLISION 두 개 제외).
    @Test func invalidCorpusCoversExpectedErrorCodes() throws {
        let fm = FileManager.default
        guard fm.fileExists(atPath: Self.invalidDirectory.path) else {
            Issue.record("invalid/ 폴더를 찾지 못했습니다")
            return
        }
        let files = try fm.contentsOfDirectory(atPath: Self.invalidDirectory.path)
            .filter { $0.hasSuffix(".json") }
        let codesInCorpus = Set(files.map { String($0.dropLast(5)) })  // ".json" 제거

        // 레지스트리 판정 코드와 크기 초과 코드는 정적 파일로 다룰 수 없다.
        let excluded: Set<String> = ["E_ID_COLLISION", "E_TOO_LARGE"]
        let decoderCodes = StyleErrorCodes.all.subtracting(excluded)

        let missing = decoderCodes.subtracting(codesInCorpus)
        #expect(missing.isEmpty, "코퍼스에 없는 오류 코드: \(missing.sorted().joined(separator: ", "))")

        let extra = codesInCorpus.subtracting(StyleErrorCodes.all)
        #expect(extra.isEmpty, "알 수 없는 오류 코드가 코퍼스에 있습니다: \(extra.sorted().joined(separator: ", "))")
    }
}
