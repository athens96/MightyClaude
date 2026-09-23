import Darwin
import Foundation
import MightyCore

extension AppStore {
    func runUsageResetSmokeTest() async {
        let args = ProcessInfo.processInfo.arguments
        guard args.contains("--profile") else {
            error = "사용량 리셋권 검증은 임시 --profile이 필요합니다."
            return
        }
        var result = await AccountUsageStatusController.runUsageResetSmoke()
        result["aiRequestSent"] = false
        do {
            try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true)
            try JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys])
                .write(to: dataDirectory.appendingPathComponent("usage-reset-smoke-result.json"), options: .atomic)
        } catch { result["passed"] = false; self.error = error.localizedDescription }
        if args.contains("--smoke-exit") {
            await shutdown()
            Darwin.exit(result["passed"] as? Bool == true ? 0 : 1)
        }
    }
}
