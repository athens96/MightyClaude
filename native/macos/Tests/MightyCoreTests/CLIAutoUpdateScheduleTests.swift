import Foundation
import Testing
@testable import MightyCore

struct CLIAutoUpdateScheduleTests {
    private let launch = Date(timeIntervalSince1970: 1_800_000_000)

    @Test func passesRunAtLaunchThenEverySixHoursOnlyWhileEnabled() {
        var schedule = CLIAutoUpdateSchedule()
        #expect(CLIAutoUpdateSchedule.interval == 6 * 60 * 60)
        #expect(!schedule.isDue(at: launch, enabled: false))
        #expect(schedule.isDue(at: launch, enabled: true))
        schedule.passStarted(at: launch)
        #expect(!schedule.isDue(at: launch.addingTimeInterval(CLIAutoUpdateSchedule.interval - 1), enabled: true))
        #expect(schedule.isDue(at: launch.addingTimeInterval(CLIAutoUpdateSchedule.interval), enabled: true))
        #expect(!schedule.isDue(at: launch.addingTimeInterval(CLIAutoUpdateSchedule.interval * 3), enabled: false))
        // The app looks often enough that a due pass never waits long.
        #expect(CLIAutoUpdateSchedule.tick < CLIAutoUpdateSchedule.interval / 12)
    }

    @Test func aProviderMustBeIdleForThreeMinutes() {
        #expect(CLIAutoUpdateSchedule.idleDelay == 180)
        let now = launch
        #expect(CLIAutoUpdateSchedule.idleLongEnough(busy: false, queued: false, lastActive: nil, now: now))
        #expect(!CLIAutoUpdateSchedule.idleLongEnough(busy: true, queued: false, lastActive: nil, now: now))
        #expect(!CLIAutoUpdateSchedule.idleLongEnough(busy: false, queued: true, lastActive: nil, now: now))
        #expect(!CLIAutoUpdateSchedule.idleLongEnough(busy: false, queued: false, lastActive: now.addingTimeInterval(-179), now: now))
        #expect(CLIAutoUpdateSchedule.idleLongEnough(busy: false, queued: false, lastActive: now.addingTimeInterval(-180), now: now))
    }

    @Test func deferredProvidersAreRetriedOnceTheyAreReady() {
        var schedule = CLIAutoUpdateSchedule()
        schedule.passStarted(at: launch)
        schedule.skippedBusy("claude"); schedule.skippedBusy("codex")
        schedule.updated("codex")
        #expect(schedule.deferred == ["claude"])
        let notYet = schedule.dueRetries(enabled: true) { _ in false }
        #expect(notYet.isEmpty && schedule.deferred == ["claude"])
        let due = schedule.dueRetries(enabled: true) { $0 == "claude" }
        #expect(due == ["claude"] && schedule.deferred.isEmpty)
        let again = schedule.dueRetries(enabled: true) { _ in true }
        #expect(again.isEmpty)
        // A retry does not count as the periodic pass.
        #expect(schedule.lastPass == launch)
    }

    @Test func turningTheSettingsOffForgetsDeferredProviders() {
        var schedule = CLIAutoUpdateSchedule()
        schedule.skippedBusy("claude"); schedule.skippedBusy("codex")
        let disabled = schedule.dueRetries(enabled: false) { _ in true }
        #expect(disabled.isEmpty && schedule.deferred.isEmpty)
    }

    @Test func pluginStepIsCappedAndTheLoopLooksEveryMinute() {
        #expect(CLIAutoUpdateSchedule.pluginBudget == 5 * 60)
        #expect(CLIAutoUpdateSchedule.tick == 60)
    }

    @Test func pluginSettingDefaultsOnAndPersistsOnlyAsAJSONBoolean() async throws {
        #expect(AppSnapshot().autoUpdatePlugins == nil)
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-plugin-setting-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let repository = StateRepository(directory: directory, legacyStateURL: nil)
        for enabled in [false, true] {
            try await repository.save(AppSnapshot(autoUpdateCLIs: false, autoUpdatePlugins: enabled))
            let restored = try await StateRepository(directory: directory, legacyStateURL: nil).load()
            #expect(restored.autoUpdatePlugins == enabled)
            #expect(restored.autoUpdateCLIs == false)
        }
        let data = try JSONEncoder().encode(AppSnapshot())
        var object = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
        for invalid: Any in [0, 1, "false", NSNull()] {
            object["autoUpdatePlugins"] = invalid
            #expect(StateRepository.decodeSnapshot(try JSONSerialization.data(withJSONObject: object)).autoUpdatePlugins == nil)
        }
    }

    @Test func pluginsUpdateOnlyWhenIdleAndTheBrowserIsNotManagingThem() {
        #expect(CLIAutoUpdateSchedule.mayUpdatePlugins(busy: false, managingPlugins: false))
        #expect(!CLIAutoUpdateSchedule.mayUpdatePlugins(busy: true, managingPlugins: false))
        #expect(!CLIAutoUpdateSchedule.mayUpdatePlugins(busy: false, managingPlugins: true))
    }
}
