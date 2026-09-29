import Foundation
@testable import MightyCore

/// Fake CLIs written in Python run through `/usr/bin/python3`, the xcrun shim.
/// On a fresh CI runner the first launch resolves the selected Xcode's Python
/// and loads its framework from a cold disk, which can take longer than the
/// 4 s `--version` and 6 s model-probe budgets the production code gives a real
/// CLI. A slow first launch then reads as "CLI missing" and the nil command is
/// cached for the rest of the test. Pay that one-time cost here, once per
/// environment, with a bound that only matters on a stalled machine, before a
/// fixture is timed by production code.
enum PythonFixture {
    private static let lock = NSLock()
    private static var launches: [[String: String]: Task<Void, Error>] = [:]

    static func warmUp(environment: [String: String]) async throws {
        let launch: Task<Void, Error> = lock.withLock {
            if let running = launches[environment] { return running }
            let task = Task<Void, Error> {
                // The same modules the fixtures import, so their first import is paid too.
                let result = try await ProcessCapture.run(executable: URL(fileURLWithPath: "/usr/bin/python3"), arguments: ["-c", "import json, os, pathlib, sys, time"], environment: environment, timeout: 120)
                guard result.exitCode == 0 else {
                    throw MightyError("/usr/bin/python3 failed to start (\(result.exitCode)): \(String(decoding: result.stderr, as: UTF8.self))")
                }
            }
            launches[environment] = task
            return task
        }
        try await launch.value
    }
}
