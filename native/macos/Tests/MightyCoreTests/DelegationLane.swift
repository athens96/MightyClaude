import Testing

/// The delegation suites take turns: one of them runs at a time, each with
/// all its tests. Each makes git repositories and runs git and the fake
/// Claude, and side by side they starved the time-bound process tests of
/// other suites on a CI runner's few cores. Other suites still run alongside
/// the lane.
struct DelegationLane: SuiteTrait, TestScoping {
    /// The suite as a whole waits its turn, once; its tests then run as the
    /// suite runs them.
    func scopeProvider(for test: Test, testCase: Test.Case?) -> Self? { test.isSuite ? self : nil }

    func provideScope(for test: Test, testCase: Test.Case?, performing function: @Sendable () async throws -> Void) async throws {
        await Self.turns.take()
        do { try await function() } catch {
            await Self.turns.pass()
            throw error
        }
        await Self.turns.pass()
    }

    private static let turns = Turns()

    /// One turn at a time, handed on in the order they were asked for.
    private actor Turns {
        private var taken = false
        private var waiting: [CheckedContinuation<Void, Never>] = []

        func take() async {
            guard taken else { taken = true; return }
            await withCheckedContinuation { waiting.append($0) }
        }

        func pass() {
            if waiting.isEmpty { taken = false } else { waiting.removeFirst().resume() }
        }
    }
}

extension Trait where Self == DelegationLane {
    /// Runs the suite in the delegation lane: one such suite at a time.
    static var delegationLane: Self { Self() }
}
