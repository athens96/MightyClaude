import Testing
import Foundation
@testable import MightyCore

/// The loopback HTTP server under ModBridge and the browser smoke check.
@Suite struct HTTPServerTests {
    private final class CallCounter: @unchecked Sendable {
        private let lock = NSLock()
        private var count = 0
        func hit() { lock.lock(); count += 1; lock.unlock() }
        var value: Int { lock.lock(); defer { lock.unlock() }; return count }
    }

    @Test func testHTTPListenerCloseBeforeReadySettles() async throws {
        let server = HTTPServer(address: "127.0.0.1", port: 0) { _ in .json(200, [:]) }
        let starting = Task { try await server.start() }
        await server.stop()
        _ = try? await starting.value
        await server.stop()
    }

    @Test func testHTTPRejectsAmbiguousFramingBeforeCallingHandler() async throws {
        let calls = CallCounter()
        let server = HTTPServer(address: "127.0.0.1", port: 0) { _ in
            calls.hit()
            return .json(200, [:])
        }
        do {
            let port = try await server.start()
            // The server's framing answer is under test, not curl's latency: the
            // first curl launch on a cold, busy CI runner can take seconds, and a
            // curl that gives up prints 000 instead of the server's status.
            let base = ["--silent", "--max-time", "30", "--output", "/dev/null", "--write-out", "%{http_code}", "--request", "POST"]
            let url = "http://127.0.0.1:\(port)/events"
            for headers in [
                ["--header", "Content-Length: 0", "--header", "Content-Length: 1"],
                ["--header", "Transfer-Encoding: chunked", "--data", "{}"],
                ["--header", "Authorization: first", "--header", "Authorization: second", "--header", "Content-Length: 0"],
            ] {
                let result = try await ProcessCapture.run(executable: URL(fileURLWithPath: "/usr/bin/curl"), arguments: base + headers + [url], timeout: 45)
                #expect(String(decoding: result.stdout, as: UTF8.self) == "400")
            }
            #expect(calls.value == 0)
        } catch { await server.stop(); throw error }
        await server.stop()
    }
}
