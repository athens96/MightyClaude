import CoreServices
import Foundation
import Testing
@testable import MightyCore

struct AppUpdateAutoStepsTests {
    private let asset = AppUpdateAsset(url: URL(string: "https://example.com/a.zip")!)

    private func found(_ version: String, current: String = "0.4.34", macos: Bool = true, minimumSystem: String? = nil) -> AppUpdateAvailability {
        AppUpdateAvailability(current: current, manifest: AppUpdateManifest(version: version, minimumSystemVersion: minimumSystem, macos: macos ? asset : nil), systemVersion: "15.6.0")
    }

    @Test func theSameVersionStagedStaysReadyAndIsNotDownloadedAgain() {
        #expect(AppUpdateAuto.afterCheck(found("0.4.35"), stagedVersion: "0.4.35", automatic: true) == .keepStaged)
        #expect(AppUpdateAuto.afterCheck(found("0.4.35"), stagedVersion: "0.4.35", automatic: false) == .keepStaged)
    }

    @Test func aNewerVersionThanTheStagedOneIsDownloadedWhenAutomatic() {
        #expect(AppUpdateAuto.afterCheck(found("0.4.36"), stagedVersion: "0.4.35", automatic: true) == .available(download: true))
        #expect(AppUpdateAuto.afterCheck(found("0.4.36"), stagedVersion: nil, automatic: true) == .available(download: true))
    }

    @Test func turnedOffANewerVersionIsOnlyOffered() {
        #expect(AppUpdateAuto.afterCheck(found("0.4.36"), stagedVersion: nil, automatic: false) == .available(download: false))
    }

    @Test func nothingNewerOrNoMacPackageIsUpToDate() {
        #expect(AppUpdateAuto.afterCheck(found("0.4.34"), stagedVersion: nil, automatic: true) == .upToDate)
        #expect(AppUpdateAuto.afterCheck(found("0.4.36", macos: false), stagedVersion: nil, automatic: true) == .upToDate)
    }

    @Test func aVersionThisMacCannotRunIsBlocked() {
        guard case .blocked(let reason) = AppUpdateAuto.afterCheck(found("0.4.36", minimumSystem: "99.0"), stagedVersion: nil, automatic: true) else {
            Issue.record("expected blocked"); return
        }
        #expect(!reason.isEmpty)
    }

    @Test func aStagedUpdateStaysReadyWhenTheNewerVersionCannotRunOnThisMac() {
        #expect(AppUpdateAuto.afterCheck(found("0.4.36", minimumSystem: "99.0"), stagedVersion: "0.4.35", automatic: true) == .keepStaged)
        #expect(AppUpdateAuto.afterCheck(found("0.4.36", minimumSystem: "99.0"), stagedVersion: "0.4.35", automatic: false) == .keepStaged)
    }

    @Test func onlyALogoutRestartOrShutdownQuitReasonKeepsTheUpdateForLater() {
        let powerOff = [kAELogOut, kAEReallyLogOut, kAEShowRestartDialog, kAEShowShutdownDialog, kAERestart, kAEShutDown].map { UInt32($0) }
        #expect(AppUpdateAuto.powerOffQuitReasons == Set(powerOff))
        for code in powerOff { #expect(AppUpdateAuto.quitReasonPowersOff(code)) }
        // An ordinary quit (no reason) or any other reason installs.
        #expect(!AppUpdateAuto.quitReasonPowersOff(nil))
        #expect(!AppUpdateAuto.quitReasonPowersOff(UInt32(kAEQuitApplication)))
        #expect(!AppUpdateAuto.quitReasonPowersOff(0))
    }

    @Test func quittingInstallsOnlyAReadyUpdateWithTheSwitchOnAndNoPowerOff() {
        #expect(AppUpdateAuto.installsOnQuit(ready: true, automatic: true, hasKey: true, poweringOff: false))
        // An install under way (not ready), the switch off, a keyless build or
        // smoke test, and a logout or shutdown each keep the update staged.
        #expect(!AppUpdateAuto.installsOnQuit(ready: false, automatic: true, hasKey: true, poweringOff: false))
        #expect(!AppUpdateAuto.installsOnQuit(ready: true, automatic: false, hasKey: true, poweringOff: false))
        #expect(!AppUpdateAuto.installsOnQuit(ready: true, automatic: true, hasKey: false, poweringOff: false))
        #expect(!AppUpdateAuto.installsOnQuit(ready: true, automatic: true, hasKey: true, poweringOff: true))
    }

    @Test func atLaunchStagedCopiesOfTheRunningOrAnOlderVersionAreDiscarded() {
        let names = ["0.4.35", "0.4.34", "0.4.36", "unknown", "0.4.35-beta.1"]
        #expect(AppUpdateAuto.staleStagedFolders(names, runningVersion: "0.4.35") == ["0.4.35", "0.4.34", "0.4.35-beta.1"])
        #expect(AppUpdateAuto.staleStagedFolders([], runningVersion: "0.4.35").isEmpty)
    }

    @Test func versionsThatDoNotParseNeverKeepAStagedUpdate() {
        #expect(AppUpdateAuto.afterCheck(found("0.4.36"), stagedVersion: "not-a-version", automatic: true) == .available(download: true))
    }
}
