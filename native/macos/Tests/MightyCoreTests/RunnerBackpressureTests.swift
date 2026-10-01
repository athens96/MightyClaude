import Foundation
import Darwin
import Testing
@testable import MightyCore

private final class BackpressureRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [RunEvent] = []
    func append(_ event: RunEvent) { lock.lock(); stored.append(event); lock.unlock() }
    func values() -> [RunEvent] { lock.lock(); defer { lock.unlock() }; return stored }
}

/// Runs a blocking call on another thread and reports when it returned.
private final class Blocking: @unchecked Sendable {
    private let lock = NSLock()
    private var done = false
    init(_ body: @escaping @Sendable () -> Void) {
        DispatchQueue.global().async { [self] in body(); lock.lock(); done = true; lock.unlock() }
    }
    var returned: Bool { lock.lock(); defer { lock.unlock() }; return done }
    /// Whether it returned within `seconds`.
    func returns(within seconds: TimeInterval) -> Bool {
        let deadline = Date().addingTimeInterval(seconds)
        while !returned, Date() < deadline { usleep(5_000) }
        return returned
    }
}

/// A child writes far faster than the runner handles its output: the runner
/// must make it wait, never drop what it already read.
@Suite(.serialized)
struct RunnerBackpressureTests {
    private func temporary() throws -> URL {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-backpressure-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }
    private func wait(_ predicate: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(120)
        while !predicate() {
            guard Date() < deadline else { throw MightyError("Timed out waiting for the backpressure fixture") }
            try await Task.sleep(for: .milliseconds(20))
        }
    }

    @Test func aSlowConsumerReceivesEveryLineInOrder() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let binary = directory.appendingPathComponent("gemini")
        // 5,000 messages, each padded to about 2 KiB: roughly 10 MB of stdout
        // written at once, well past the old 256-chunk buffer.
        let source = #"""
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf '0.20.0\n'; exit 0; fi
        input="$(/bin/cat)"
        printf '%s\n' '{"type":"init","session_id":"fixture-session"}'
        /usr/bin/awk 'BEGIN { pad = sprintf("%2000s", ""); gsub(/ /, "x", pad); for (i = 0; i < 5000; i++) printf "{\"type\":\"message\",\"role\":\"assistant\",\"content\":\"line-%d\",\"pad\":\"%s\"}\n", i, pad }'
        printf '%s\n' '{"type":"result","status":"success"}'
        """#
        try Data(source.utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        let service = ProviderService(binaryOverrides: ["gemini": binary])
        let events = BackpressureRecorder()
        // Every event costs the consumer time, so the reader outruns it.
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0); usleep(150) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        do {
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "go", provider: "gemini"), workspace: workspace)
            try await wait { events.values().contains { $0.type == "status" && ["completed", "error", "stopped"].contains($0.status ?? "") } }
            let lines = events.values().compactMap { $0.entry?.kind == "assistant" ? $0.entry?.text : nil }
            #expect(lines == (0..<5_000).map { "line-\($0)" })
            #expect(events.values().last(where: { $0.type == "status" })?.status == "completed")
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }

    /// The app stops a run from the main actor, at user-initiated priority,
    /// the same as the run's consumer; a lower-priority caller would wait
    /// behind every chunk the consumer queues on the runner's actor.
    private func stop(_ runner: ProcessRunner) async {
        await Task(priority: .userInitiated) { await runner.stop(id: "pane") }.value
    }

    @Test func theBudgetBlocksAtItsLimitAndResumesOnRelease() {
        let budget = ChildOutputBudget(limit: 100)
        budget.acquire(60)
        let reader = Blocking { budget.acquire(50) }
        #expect(!reader.returns(within: 0.3))
        budget.release(60)
        #expect(reader.returns(within: 5))
    }

    @Test func closingTheBudgetFreesABlockedReaderForGood() {
        let budget = ChildOutputBudget(limit: 100)
        budget.acquire(100)
        let reader = Blocking { budget.acquire(1) }
        #expect(!reader.returns(within: 0.3))
        budget.close()
        #expect(reader.returns(within: 5))
        // Closed, it never waits again.
        #expect(Blocking { budget.acquire(1_000) }.returns(within: 5))
    }

    @Test func aChunkLargerThanTheBudgetPassesWhenNothingIsPending() {
        let budget = ChildOutputBudget(limit: 100)
        #expect(Blocking { budget.acquire(500) }.returns(within: 5))
        let next = Blocking { budget.acquire(1) }
        #expect(!next.returns(within: 0.3))
        budget.release(500)
        #expect(next.returns(within: 5))
    }

    @Test func stopEndsAFloodingRunWithASlowConsumerWithinSeconds() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let binary = directory.appendingPathComponent("gemini")
        let source = #"""
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf '0.20.0\n'; exit 0; fi
        /bin/cat > /dev/null
        printf '%s\n' '{"type":"init","session_id":"fixture-session"}'
        exec /usr/bin/yes '{"type":"message","role":"assistant","content":"flood"}'
        """#
        try Data(source.utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        let service = ProviderService(binaryOverrides: ["gemini": binary])
        let events = BackpressureRecorder()
        // Every line costs the consumer a millisecond: the run's whole budget
        // (tens of thousands of lines) is always queued.
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0); if $0.entry?.kind == "assistant" { usleep(1_000) } })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        do {
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "go", provider: "gemini"), workspace: workspace)
            try await wait { events.values().contains { $0.entry?.text == "flood" } }
            let began = Date()
            await stop(runner)
            #expect(Date().timeIntervalSince(began) < 8)
            #expect(events.values().last(where: { $0.type == "status" })?.status == "stopped")
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }

    /// A writer outside the child's process group keeps the inherited stdout
    /// open after the child exits while the consumer applies backpressure.
    /// The run still ends, with a notice, and the writer loses its pipe.
    @Test func aDetachedWriterCannotKeepTheRunAliveAfterExit() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let binary = directory.appendingPathComponent("gemini")
        // `set -m` gives the background job its own process group, so the
        // runner's group kill at exit does not reach it.
        let source = #"""
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf '0.20.0\n'; exit 0; fi
        /bin/cat > /dev/null
        printf '%s\n' '{"type":"init","session_id":"fixture-session"}'
        pad=$(/usr/bin/awk 'BEGIN { s = sprintf("%2000s", ""); gsub(/ /, "x", s); print s }')
        set -m
        /usr/bin/yes "{\"type\":\"message\",\"role\":\"assistant\",\"content\":\"$pad\"}" &
        echo $! > "$(/usr/bin/dirname "$0")/stray.pid"
        exit 0
        """#
        try Data(source.utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        let service = ProviderService(binaryOverrides: ["gemini": binary])
        let events = BackpressureRecorder()
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        var stray: pid_t = 0
        defer { if stray > 0 { _ = Darwin.kill(stray, SIGKILL) } }
        do {
            let began = Date()
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "go", provider: "gemini"), workspace: workspace)
            try await wait { events.values().contains { $0.type == "status" && ["completed", "error", "stopped"].contains($0.status ?? "") } }
            // Well inside the 30 s drain credit: the 16 MB read after exit ends it.
            #expect(Date().timeIntervalSince(began) < 25)
            #expect(events.values().last(where: { $0.type == "status" })?.status == "completed")
            #expect(events.values().contains { $0.entry?.kind == "system" && $0.entry?.text == L("run.notice.outputAbandoned") })
            stray = try #require(Int32(String(contentsOf: directory.appendingPathComponent("stray.pid"), encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)))
            // Its pipe is closed: the next write fails (SIGPIPE / EPIPE) and it ends.
            try await wait { Darwin.kill(stray, 0) != 0 }
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }

    /// `steer` asks whether stdin is open on the runner's actor; that must not
    /// wait behind a write blocked on a child that does not read.
    @Test func inputIsOpenDoesNotWaitForABlockedWrite() async throws {
        let child = try NativeChildProcess(executable: URL(fileURLWithPath: "/bin/sleep"), arguments: ["30"], environment: [:], cwd: FileManager.default.temporaryDirectory, stdout: { _ in }, stderr: { _ in }, exited: { _ in })
        defer { child.stop() }
        child.write(Data(repeating: 65, count: 1_048_576))
        try await Task.sleep(for: .milliseconds(200))
        var began = Date()
        #expect(child.inputIsOpen)
        #expect(Date().timeIntervalSince(began) < 0.5)
        child.closeInput()
        began = Date()
        // The close is still queued behind the blocked write, yet already counts.
        #expect(!child.inputIsOpen)
        #expect(Date().timeIntervalSince(began) < 0.5)
        child.stop()
        _ = await child.wait(timeout: 15)
    }
}
