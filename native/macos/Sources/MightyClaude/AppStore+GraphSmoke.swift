import Foundation
import Darwin
import MightyCore

extension AppStore {
    func runGraphSmokeTest() async {
        let args = ProcessInfo.processInfo.arguments
        guard args.contains("--profile") else {
            error = "그래프 검증은 임시 --profile이 필요합니다."
            return
        }
        var result: [String: Any]
        do {
            try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true)
            let workspace = try await repository.approveWorkspace(Workspace(name: "Graph Resize", path: dataDirectory.path))
            addWorkspace(workspace)
            result = await MightyGraphDiagnostics.run(store: self)
            try JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys])
                .write(to: dataDirectory.appendingPathComponent("graph-smoke-result.json"), options: .atomic)
        } catch { result = ["passed": false, "error": error.localizedDescription]; self.error = error.localizedDescription }
        if args.contains("--smoke-exit") {
            await shutdown()
            Darwin.exit(result["passed"] as? Bool == true ? 0 : 1)
        }
    }
}
