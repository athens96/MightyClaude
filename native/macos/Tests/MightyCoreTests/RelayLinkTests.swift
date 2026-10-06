import Foundation
import Testing
@testable import MightyCore

/// When the host redials the relay after a network change (docs/relay.md
/// "재접속"). The decisions are pure; the service checks run with no
/// relay at all — a closed port keeps it retrying, and `restarts` counts dials.
struct RelayLinkTests {
    private let wifi = RelayNetworkPath(satisfied: true, interfaces: ["en0"], gateways: ["192.168.0.1"])
    private let otherWifi = RelayNetworkPath(satisfied: true, interfaces: ["en0"], gateways: ["10.0.0.1"])
    private let vpn = RelayNetworkPath(satisfied: true, interfaces: ["en0", "utun4"], gateways: ["192.168.0.1"])
    private let none = RelayNetworkPath(satisfied: false, interfaces: [])

    @Test func theFirstReportIsOnlyABaseline() {
        #expect(RelayLinkPolicy.action(from: nil, to: wifi, forced: false) == .none)
        #expect(RelayLinkPolicy.action(from: nil, to: none, forced: false) == .offline)
    }

    @Test func aDifferentNetworkRedials() {
        #expect(RelayLinkPolicy.action(from: wifi, to: otherWifi, forced: false) == .reconnect)
        #expect(RelayLinkPolicy.action(from: wifi, to: vpn, forced: false) == .reconnect)
        #expect(RelayLinkPolicy.action(from: none, to: wifi, forced: false) == .reconnect)
        #expect(RelayLinkPolicy.action(from: wifi, to: wifi, forced: false) == .none)
    }

    @Test func noNetworkIsOfflineNotARetry() {
        #expect(RelayLinkPolicy.action(from: wifi, to: none, forced: false) == .offline)
        #expect(RelayLinkPolicy.action(from: none, to: none, forced: false) == .none)
        // A wake with no network still shows offline instead of dialling.
        #expect(RelayLinkPolicy.action(from: wifi, to: none, forced: true) == .offline)
    }

    @Test func aWakeRedialsOnTheSameNetwork() {
        #expect(RelayLinkPolicy.action(from: wifi, to: nil, forced: true) == .reconnect)
        #expect(RelayLinkPolicy.action(from: wifi, to: wifi, forced: true) == .reconnect)
        #expect(RelayLinkPolicy.action(from: wifi, to: nil, forced: false) == .none)
    }

    @Test func anOnDemandVPNCountsAsANetwork() {
        // Dialling is what brings such a VPN up; offline would never dial.
        #expect(RelayNetworkPath(status: .requiresConnection, interfaces: ["utun4"]).satisfied)
        #expect(RelayNetworkPath(status: .satisfied, interfaces: ["en0"]).satisfied)
        #expect(!RelayNetworkPath(status: .unsatisfied, interfaces: []).satisfied)
    }

    @Test func theWindowRemembersADropThatEndedOnTheSamePath() {
        let now = Date(timeIntervalSinceReferenceDate: 0)
        // Two routers on the same 192.168.0.1: wifi → nothing → wifi.
        var dropped = RelayLinkPolicy.Window()
        for path in [wifi, none, wifi] { dropped.report(path, settled: wifi, now: now) }
        #expect(RelayLinkPolicy.action(from: wifi, window: dropped) == .reconnect)
        var quiet = RelayLinkPolicy.Window()
        quiet.report(wifi, settled: wifi, now: now)
        #expect(RelayLinkPolicy.action(from: wifi, window: quiet) == .none)
        // Before any baseline nothing counts as a change.
        var first = RelayLinkPolicy.Window()
        for path in [otherWifi, wifi] { first.report(path, settled: nil, now: now) }
        #expect(RelayLinkPolicy.action(from: nil, window: first) == .none)
    }

    @Test func aStreamOfReportsStillSettles() {
        let start = Date(timeIntervalSinceReferenceDate: 0)
        var window = RelayLinkPolicy.Window()
        window.report(wifi, settled: wifi, now: start)
        #expect(window.delay(now: start) == RelayLinkPolicy.pathSettle)
        #expect(window.delay(now: start.addingTimeInterval(4)) == 1)
        #expect(window.delay(now: start.addingTimeInterval(RelayLinkPolicy.pathSettleMaxWait + 1)) == 0)
    }

    @Test func pathRedialsKeepAGapThatGrowsWhileTheyRepeat() {
        let t0 = Date(timeIntervalSinceReferenceDate: 0)
        func at(_ seconds: TimeInterval) -> Date { t0.addingTimeInterval(seconds) }
        var pacing = RelayLinkPolicy.Pacing()
        #expect(pacing.wait(now: t0) == 0)
        pacing.record(now: t0)
        #expect(pacing.wait(now: at(1)) == 4)
        pacing.record(now: at(5))
        #expect(pacing.wait(now: at(5)) == 10)
        pacing.record(now: at(15))
        #expect(pacing.wait(now: at(15)) == 20)
        pacing.record(now: at(35))
        #expect(pacing.wait(now: at(35)) == RelayLinkPolicy.restartGapMax)
        pacing.record(now: at(65))
        #expect(pacing.wait(now: at(65)) == RelayLinkPolicy.restartGapMax)
        // Calm for a while: back to the shortest gap.
        pacing.record(now: at(200))
        #expect(pacing.wait(now: at(200)) == RelayLinkPolicy.restartGapMin)
    }

    /// A host with the remote on, pointed at a port nobody listens on. Its path
    /// settle and redial pacing run on `clock`, which only the test moves.
    private func withRetryingHost(_ body: (MobileRemoteService, RelayManualClock) async throws -> Void) async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("relay-link-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let service = MobileRemoteService(dataDirectory: directory, hostName: "Link Mac", defaultRelayURL: "", watchesNetwork: false)
        let clock = RelayManualClock()
        await service.setPathClock(clock.clock)
        _ = await service.apply(settings: MobileRemoteSettings(enabled: true, relayURL: "ws://127.0.0.1:1"))
        do { try await body(service, clock) } catch { await service.shutdown(); throw error }
        await service.shutdown()
    }

    /// Moves the path clock on and, when that makes the scheduled settle due,
    /// waits until it has decided, so what follows sees the result rather than
    /// racing it.
    private func advance(_ service: MobileRemoteService, _ clock: RelayManualClock, by seconds: TimeInterval) async throws {
        let passes = await service.pathSettlePasses
        let due = await service.pathSettleDeadline.map { $0 <= clock.now.addingTimeInterval(seconds) } ?? false
        clock.advance(by: seconds)
        if due { try #require(await waitUntil { await service.pathSettlePasses > passes }) }
    }

    /// One whole settle on the path clock.
    private func settle(_ service: MobileRemoteService, _ clock: RelayManualClock) async throws {
        try await advance(service, clock, by: RelayLinkPolicy.pathSettle + 1)
    }

    /// Polls a condition on real time; the bound only ends a test that broke.
    private func waitUntil(timeout: TimeInterval = 60, _ condition: @Sendable () async -> Bool) async -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if await condition() { return true }
            try? await Task.sleep(nanoseconds: 2_000_000)
        }
        return await condition()
    }

    @Test func aBurstOfPathReportsRedialsOnce() async throws {
        try await withRetryingHost { service, clock in
            await service.networkPathChanged(wifi)
            try await settle(service, clock)
            let before = await service.restarts
            for path in [none, otherWifi, vpn, otherWifi] { await service.networkPathChanged(path) }
            try await settle(service, clock)
            #expect(await service.restarts == before + 1)
            try await settle(service, clock)
            #expect(await service.restarts == before + 1)
        }
    }

    @Test func noNetworkShowsOfflineAndTheNetworkComingBackRedials() async throws {
        try await withRetryingHost { service, clock in
            await service.networkPathChanged(wifi)
            try await settle(service, clock)
            await service.networkPathChanged(none)
            try await settle(service, clock)
            let offline = await service.status()
            #expect(!offline.relayConnected && offline.detail == L("settings.mobileRemote.detail.networkOffline"))
            // Settings "다시 연결" dials anyway: the report may be wrong, and
            // dialling is what brings an on-demand VPN up.
            let manual = await service.restarts
            await service.reconnect()
            #expect(await service.restarts == manual + 1)
            #expect(await service.status().detail != L("settings.mobileRemote.detail.networkOffline"))
            // A wake meanwhile still waits for the network.
            await service.reconnectSoon()
            try await settle(service, clock)
            #expect(await service.restarts == manual + 1)

            let before = await service.restarts
            await service.networkPathChanged(wifi)
            try await settle(service, clock)
            #expect(await service.restarts == before + 1)
            #expect(await service.status().detail != L("settings.mobileRemote.detail.networkOffline"))
        }
    }

    @Test func aDropInsideTheWindowRedialsOnTheSamePath() async throws {
        try await withRetryingHost { service, clock in
            await service.networkPathChanged(wifi)
            try await settle(service, clock)
            let before = await service.restarts
            for path in [wifi, none, wifi] { await service.networkPathChanged(path) }
            try await settle(service, clock)
            #expect(await service.restarts == before + 1)
        }
    }

    /// A link flapping every ~2 s for ~11 s: unpaced that is six redials; the
    /// 5 s gap, doubling on repeats, allows two.
    @Test func aFlappingLinkKeepsTheGapBetweenRedials() async throws {
        try await withRetryingHost { service, clock in
            await service.networkPathChanged(wifi)
            try await settle(service, clock)
            let before = await service.restarts
            for flap in 0..<6 {
                await service.networkPathChanged(flap.isMultiple(of: 2) ? otherWifi : wifi)
                try await advance(service, clock, by: RelayLinkPolicy.pathSettle + 0.3)
            }
            // On the manual clock the count is exact: one redial at once, the
            // next after the 5 s gap, and the doubled 10 s gap outlasts the rest.
            #expect(await service.restarts - before == 2)
        }
    }

    /// A stop that lands while a path-triggered restart waits on its phones'
    /// sockets: the restart must neither dial nor bring the path watch back.
    @Test func aStopDuringASettleTriggeredStartNeitherDialsNorWatches() async throws {
        try await LocaleOverride.$language.withValue(.ko) { () async throws in
            try await withRetryingHost { service, clock in
                await service.networkPathChanged(wifi)
                try await settle(service, clock)
                let (entered, signal) = AsyncStream.makeStream(of: Void.self)
                await service.setDisconnectPause { signal.yield(); try? await Task.sleep(for: .seconds(1)) }
                let before = await service.restarts
                let passes = await service.pathSettlePasses
                await service.networkPathChanged(otherWifi)
                // Not `settle`: this settle's decision is held inside `disconnect`
                // until after the stop below.
                #expect(await service.pathSettleDeadline != nil)
                clock.advance(by: RelayLinkPolicy.pathSettle + 1)
                var inside = entered.makeAsyncIterator()
                _ = await inside.next() // the settle's start is now inside `disconnect`
                await service.stop()
                #expect(await waitUntil { await service.pathSettlePasses > passes })
                #expect(await service.restarts == before)
                #expect(await !service.watchingNetwork)
                #expect(await service.status().detail == "모바일 리모트가 꺼져 있습니다.")
                await service.setDisconnectPause(nil)
            }
        }
    }

    @Test func aWakeAndAPathChangeTogetherRedialOnce() async throws {
        try await withRetryingHost { service, clock in
            await service.networkPathChanged(wifi)
            try await settle(service, clock)
            let before = await service.restarts
            await service.reconnectSoon()
            await service.networkPathChanged(otherWifi)
            try await settle(service, clock)
            #expect(await service.restarts == before + 1)
        }
    }

    @Test func aWakeAloneRedials() async throws {
        try await withRetryingHost { service, clock in
            let before = await service.restarts
            await service.reconnectSoon()
            try await settle(service, clock)
            #expect(await service.restarts == before + 1)
        }
    }

    @Test func nothingRedialsWhileTheRemoteIsOff() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("relay-link-off-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let service = MobileRemoteService(dataDirectory: directory, hostName: "Link Mac", defaultRelayURL: "", watchesNetwork: false)
        let clock = RelayManualClock()
        await service.setPathClock(clock.clock)
        await service.networkPathChanged(wifi)
        await service.networkPathChanged(otherWifi)
        await service.reconnectSoon()
        await service.reconnect()
        // Nothing was even scheduled to settle.
        #expect(await service.pathSettleDeadline == nil)
        clock.advance(by: RelayLinkPolicy.pathSettle + 1)
        #expect(await service.restarts == 0)
        await service.shutdown()
    }
}

/// A clock only the test moves. `sleep` returns once `advance` has reached its
/// deadline (at once when it already has), or throws when its task is
/// cancelled (a newer report replaced it).
final class RelayManualClock: @unchecked Sendable {
    private let lock = NSLock()
    private var current = Date(timeIntervalSinceReferenceDate: 0)
    private var sleepers: [UUID: (deadline: Date, continuation: CheckedContinuation<Void, Error>)] = [:]

    var clock: RelayLinkPolicy.Clock {
        RelayLinkPolicy.Clock(now: { [self] in now }, sleep: { [self] deadline in try await sleep(until: deadline) })
    }
    var now: Date { lock.lock(); defer { lock.unlock() }; return current }

    func sleep(until deadline: Date) async throws {
        let id = UUID()
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                lock.lock()
                if Task.isCancelled { lock.unlock(); continuation.resume(throwing: CancellationError()); return }
                guard deadline > current else { lock.unlock(); continuation.resume(); return }
                sleepers[id] = (deadline, continuation)
                lock.unlock()
            }
        } onCancel: {
            lock.lock(); let sleeper = sleepers.removeValue(forKey: id); lock.unlock()
            sleeper?.continuation.resume(throwing: CancellationError())
        }
    }

    /// Moves the time on and wakes every sleeper now due.
    func advance(by seconds: TimeInterval) {
        lock.lock()
        current = current.addingTimeInterval(seconds)
        let due = sleepers.filter { $0.value.deadline <= current }
        for id in due.keys { sleepers.removeValue(forKey: id) }
        lock.unlock()
        for sleeper in due.values { sleeper.continuation.resume() }
    }
}
