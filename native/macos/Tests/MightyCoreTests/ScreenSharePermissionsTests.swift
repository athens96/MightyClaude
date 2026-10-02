import Foundation
import Testing
@testable import MightyCore

/// The onboarding screen's readings: what each raw system answer means, which
/// ones bring the screen up by themselves, and where each button goes.
struct ScreenSharePermissionsTests {
    @Test func screenRecordingIsTheCaptureFlightCheck() {
        #expect(ScreenSharePermissionReading.screenRecording(preflight: true) == .granted)
        #expect(ScreenSharePermissionReading.screenRecording(preflight: false) == .missing)
    }

    @Test func eitherAccessibilityReadingIsEnoughToPostEvents() {
        #expect(ScreenSharePermissionReading.accessibility(postEventAccess: true, processTrusted: false) == .granted)
        #expect(ScreenSharePermissionReading.accessibility(postEventAccess: false, processTrusted: true) == .granted)
        #expect(ScreenSharePermissionReading.accessibility(postEventAccess: false, processTrusted: false) == .missing)
    }

    @Test func notificationsFollowTheAuthorizationStatus() {
        #expect(ScreenSharePermissionReading.notifications(authorizationStatus: 0) == .notDetermined)
        #expect(ScreenSharePermissionReading.notifications(authorizationStatus: 1) == .missing)
        #expect(ScreenSharePermissionReading.notifications(authorizationStatus: 2) == .granted)
        #expect(ScreenSharePermissionReading.notifications(authorizationStatus: 3) == .granted)
        #expect(ScreenSharePermissionReading.notifications(authorizationStatus: 4) == .granted)
        #expect(ScreenSharePermissionReading.notifications(authorizationStatus: 99) == .unknown)
    }

    @Test func localNetworkIsReadFromHowABrowseEnded() {
        #expect(ScreenSharePermissionReading.localNetwork(.ready) == .granted)
        #expect(ScreenSharePermissionReading.localNetwork(.policyDenied) == .missing)
        #expect(ScreenSharePermissionReading.localNetwork(.inconclusive) == .unknown)
    }

    @Test func onlyAMissingOrUnaskedPermissionBringsTheScreenUp() {
        let allGood: [ScreenSharePermission: ScreenSharePermissionStatus] = [
            .screenRecording: .granted, .accessibility: .granted, .localNetwork: .unknown, .notifications: .granted,
        ]
        #expect(!ScreenSharePermissionReading.needsOnboarding(allGood))
        var noCapture = allGood
        noCapture[.screenRecording] = .missing
        #expect(ScreenSharePermissionReading.needsOnboarding(noCapture))
        var unasked = allGood
        unasked[.notifications] = .notDetermined
        #expect(ScreenSharePermissionReading.needsOnboarding(unasked))
        #expect(!ScreenSharePermissionStatus.unknown.needsAttention)
    }

    @Test func eachButtonOpensItsOwnSystemSettingsPane() {
        #expect(ScreenSharePermission.screenRecording.settingsURL().absoluteString
                == "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")
        #expect(ScreenSharePermission.accessibility.settingsURL().absoluteString
                == "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")
        #expect(ScreenSharePermission.localNetwork.settingsURL().absoluteString
                == "x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork")
        #expect(ScreenSharePermission.notifications.settingsURL(bundleIdentifier: "dev.mightyclaude.native").absoluteString
                == "x-apple.systempreferences:com.apple.Notifications-Settings.extension?id=dev.mightyclaude.native")
        #expect(ScreenSharePermission.notifications.settingsURL().absoluteString
                == "x-apple.systempreferences:com.apple.Notifications-Settings.extension")
        #expect(ScreenSharePermission.allCases == [.screenRecording, .accessibility, .localNetwork, .notifications])
    }
}
