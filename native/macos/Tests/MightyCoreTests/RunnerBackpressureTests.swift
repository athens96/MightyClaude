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
    /// above the run's medium consumer. A lower caller signals first
    /// (`signalStop`), as the app does; see the background-priority test.
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

    /// A flooding fixture that records its pid; `exec` keeps that pid.
    private func floodingGemini(in directory: URL) throws -> URL {
        let binary = directory.appendingPathComponent("gemini")
        let source = #"""
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf '0.20.0\n'; exit 0; fi
        /bin/cat > /dev/null
        echo $$ > "$PWD/child.pid"
        printf '%s\n' '{"type":"init","session_id":"fixture-session"}'
        exec /usr/bin/yes '{"type":"message","role":"assistant","content":"flood"}'
        """#
        try Data(source.utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        return binary
    }

    /// A stop from below the consumer's priority (here background, as a Task
    /// started from an unlabeled serial queue can be) still ends the child at
    /// once: the signal does not wait for the runner's actor.
    @Test func aBackgroundStopEndsAFloodingChildAtOnce() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let service = ProviderService(binaryOverrides: ["gemini": try floodingGemini(in: directory)])
        let events = BackpressureRecorder()
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0); if $0.entry?.kind == "assistant" { usleep(1_000) } })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        do {
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "go", provider: "gemini"), workspace: workspace)
            try await wait { events.values().contains { $0.entry?.text == "flood" } }
            let pid = try #require(Int32(String(contentsOf: directory.appendingPathComponent("child.pid"), encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)))
            let began = Date()
            let stopping = Task(priority: .background) { #expect(runner.signalStop(id: "pane")); await runner.stop(id: "pane") }
            try await wait { Darwin.kill(pid, 0) != 0 }
            let died = Date().timeIntervalSince(began)
            await stopping.value
            let returned = Date().timeIntervalSince(began)
            print("background stop: child gone after \(died) s, stop returned after \(returned) s")
            #expect(died < 1)
            #expect(returned < 6)
            #expect(events.values().last(where: { $0.type == "status" })?.status == "stopped")
            // Nothing is left to signal.
            #expect(!runner.signalStop(id: "pane"))
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }

    /// A stop that reaches the actor by itself at the consumer's priority
    /// (medium, what a Task started from a plain serial queue gets) is not
    /// starved either.
    @Test func aMediumStopIsNotStarvedByTheConsumer() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let service = ProviderService(binaryOverrides: ["gemini": try floodingGemini(in: directory)])
        let events = BackpressureRecorder()
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0); if $0.entry?.kind == "assistant" { usleep(1_000) } })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        do {
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "go", provider: "gemini"), workspace: workspace)
            try await wait { events.values().contains { $0.entry?.text == "flood" } }
            let began = Date()
            await Task(priority: .medium) { await runner.stop(id: "pane") }.value
            let returned = Date().timeIntervalSince(began)
            print("medium stop returned after \(returned) s")
            #expect(returned < 8)
            #expect(events.values().last(where: { $0.type == "status" })?.status == "stopped")
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }

    /// Quit signals every run first and stops them together: four flooding
    /// runs with a slow consumer end well inside the app's quit deadline.
    @Test func shutdownEndsFourFloodingRunsWithinTheQuitDeadline() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let service = ProviderService(binaryOverrides: ["gemini": try floodingGemini(in: directory)])
        let events = BackpressureRecorder()
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, onEvent: { events.append($0); if $0.entry?.kind == "assistant" { usleep(1_000) } })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        let panes = (0..<4).map { "pane-\($0)" }
        do {
            for pane in panes {
                try await runner.start(request: StartRunRequest(sessionId: pane, workspaceId: workspace.id, input: "go", provider: "gemini"), workspace: workspace)
            }
            try await wait { Set(events.values().filter { $0.entry?.text == "flood" }.map(\.sessionId)) == Set(panes) }
            let began = Date()
            await Task(priority: .background) { await runner.shutdown() }.value
            let elapsed = Date().timeIntervalSince(began)
            print("shutdown of 4 flooding runs took \(elapsed) s")
            #expect(elapsed < 6)
            for pane in panes { #expect(events.values().last(where: { $0.sessionId == pane && $0.type == "status" })?.status == "stopped") }
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await service.shutdown()
    }

    @Test func theEventBatcherKeepsOrderAndAsksForOneDeliveryAtATime() {
        let batcher = RunEventBatcher(batchLimit: 64)
        let sent = (0..<1_000).map { RunEvent(sessionId: "pane-\($0 % 3)", type: "log", entry: LogEntry(kind: "output", text: "\($0)")) }
        var outstanding = false
        var received: [RunEvent] = []
        func deliver() {
            let (events, more) = batcher.take()
            #expect(events.count <= 64)
            received += events; outstanding = more
        }
        for (index, event) in sent.enumerated() {
            // A push asks for a delivery only when none is outstanding.
            #expect(batcher.push(event) == !outstanding)
            outstanding = true
            // Deliveries interleave with pushes, as the main actor does.
            if index % 150 == 149 { deliver() }
        }
        while outstanding { deliver() }
        #expect(received == sent)
        #expect(batcher.push(sent[0]))
        #expect(!batcher.push(sent[1]))
        let last = batcher.take()
        #expect(last.events == [sent[0], sent[1]]); #expect(!last.more)
    }

    /// Pushes from several threads, delivered on one serial queue as on the
    /// main actor: nothing is lost and each thread's events stay in order.
    @Test func theEventBatcherIsSafeAcrossThreads() {
        let batcher = RunEventBatcher(batchLimit: 32)
        let consumer = DispatchQueue(label: "batcher-consumer")
        let received = BackpressureRecorder()
        @Sendable func deliver() {
            let (events, more) = batcher.take()
            events.forEach(received.append)
            if more { consumer.async { deliver() } }
        }
        let pushing = Blocking {
            DispatchQueue.concurrentPerform(iterations: 4) { thread in
                for index in 0..<2_000 {
                    if batcher.push(RunEvent(sessionId: "t\(thread)", type: "log", entry: LogEntry(kind: "output", text: "\(index)"))) { consumer.async { deliver() } }
                }
            }
        }
        #expect(pushing.returns(within: 20))
        let deadline = Date().addingTimeInterval(20)
        while received.values().count < 8_000, Date() < deadline { usleep(5_000) }
        let values = received.values()
        #expect(values.count == 8_000)
        for thread in 0..<4 { #expect(values.filter { $0.sessionId == "t\(thread)" }.map { $0.entry?.text } == (0..<2_000).map { "\($0)" }) }
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

    /// Once quit signalled the registry, a run that was still starting is
    /// refused before it spawns a child.
    @Test func aStartAfterTheRegistryClosesIsRefused() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let service = ProviderService()
        let events = BackpressureRecorder()
        let liveRuns = LiveRunRegistry()
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, liveRuns: liveRuns, onEvent: { events.append($0) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        liveRuns.signalAll()
        #expect(liveRuns.closing)
        var refusal: Error?
        do { try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, kind: "shell", input: "printf STARTED"), workspace: workspace) }
        catch { refusal = error }
        #expect(refusal as? MightyError == MightyError("앱이 종료 중입니다."))
        #expect(!events.values().contains { $0.type == "status" && $0.status == "running" })
        #expect(!liveRuns.signalStop(id: "pane"))
        await runner.shutdown(); await service.shutdown()
    }

    /// A child that registers after the registry closed is signalled at once.
    @Test func aChildRegisteredWhileClosingIsSignalled() async throws {
        let liveRuns = LiveRunRegistry()
        liveRuns.signalAll()
        let child = try NativeChildProcess(executable: URL(fileURLWithPath: "/bin/sleep"), arguments: ["30"], environment: [:], cwd: FileManager.default.temporaryDirectory, stdout: { _ in }, stderr: { _ in }, exited: { _ in })
        liveRuns.register("pane", child: child, consumer: nil)
        let code = await child.wait(timeout: 5)
        #expect(code == 128 + SIGTERM)
        #expect(child.sentSignals.first == SIGTERM)
    }

    /// A reaped child's pid may already be another process's: stop sends nothing.
    @Test func stoppingAnExitedChildSendsNoSignal() async throws {
        // A job in its own group keeps stdout open, so the readers drain for
        // a second after the exit: stop() arrives before the run's result.
        let child = try NativeChildProcess(executable: URL(fileURLWithPath: "/bin/sh"), arguments: ["-c", "set -m; /bin/sleep 3 & exit 0"], environment: [:], cwd: FileManager.default.temporaryDirectory, stdout: { _ in }, stderr: { _ in }, exited: { _ in })
        let began = Date()
        while !child.hasExited, Date().timeIntervalSince(began) < 5 { try await Task.sleep(for: .milliseconds(10)) }
        #expect(child.hasExited)
        child.stop()
        child.kill()
        try await Task.sleep(for: .milliseconds(400))
        #expect(child.sentSignals.isEmpty)
        #expect(await child.wait(timeout: 10) == 0)
        // A live child still gets them.
        let live = try NativeChildProcess(executable: URL(fileURLWithPath: "/bin/sleep"), arguments: ["30"], environment: [:], cwd: FileManager.default.temporaryDirectory, stdout: { _ in }, stderr: { _ in }, exited: { _ in })
        live.stop()
        _ = await live.wait(timeout: 5)
        #expect(live.sentSignals.first == SIGTERM)
    }
}
