import Foundation
import MightyCore

/// The desktop app's update, seen and started from the phone: the phone's
/// banner shows where it stands (`MobileState.appUpdate`), and its button
/// checks, or installs and restarts the app as the banner's "restart now"
/// does. Requests running then are stopped only after the phone's user agreed
/// (`confirm`); the phone reconnects once the app is back.
extension AppStore {
    /// Where the update stands for the phone; nil when this build cannot update itself.
    func phoneAppUpdateState() -> MobileAppUpdate? {
        guard appUpdateCanRunAutomatically else { return nil }
        let offered = appUpdate.availability.flatMap { $0.isNewer ? $0.manifest : nil }
        let phase: String, progress: Double?
        switch appUpdate.phase {
        case .idle: phase = "idle"; progress = nil
        case .checking: phase = "checking"; progress = nil
        case .upToDate: phase = "upToDate"; progress = nil
        case .available: phase = "available"; progress = nil
        case .downloading(let fraction): phase = "downloading"; progress = fraction
        case .staging: phase = "downloading"; progress = 1
        case .ready: phase = "ready"; progress = nil
        case .installing: phase = "installing"; progress = nil
        case .failed: phase = "failed"; progress = nil
        }
        let error: String? = if case .failed(let message) = appUpdate.phase { message } else { nil }
        return MobileAppUpdate(phase: phase, current: appVersion, available: offered?.version, notes: offered?.notes, progress: progress.map { ($0 * 100).rounded() / 100 },
                               error: error, running: snapshot.sessions.filter { $0.status == "running" || pendingRuns.contains($0.id) }.count)
    }

    /// The phone's button: `install` false checks; true installs a staged
    /// update now, or first checks and downloads, then installs once ready.
    /// Running requests stop only with `confirm`. Where the update stands then.
    func phoneAppUpdate(install: Bool, confirm: Bool) throws -> MobileAppUpdate {
        guard let state = phoneAppUpdateState() else { throw MightyError(L("appUpdate.mobile.unavailable")) }
        guard install else {
            checkForAppUpdate()
            return phoneAppUpdateState() ?? state
        }
        if state.running > 0, !confirm { throw MightyError(L("appUpdate.mobile.runningRequests", ["count": "\(state.running)"])) }
        phoneRequestedAppUpdate = true
        switch appUpdate.phase {
        // What the app knows may be old: look again first, and the steps follow from there.
        case .idle, .upToDate, .failed: checkForAppUpdate()
        default: advancePhoneAppUpdate()
        }
        return phoneAppUpdateState() ?? state
    }

    /// Each change of the update: the phone hears of it, and a phone's request moves on.
    func appUpdateChangedForPhone() {
        if phoneRequestedAppUpdate { advancePhoneAppUpdate() }
        guard mobileBridge != nil, snapshot.mobileRemote?.enabled == true else { return }
        let state = phoneAppUpdateState()
        guard state != phoneAppUpdateSent else { return }
        phoneAppUpdateSent = state
        mobileTracking.stateRevision += 1
        let revision = mobileTracking.stateRevision
        Task { await mobileRemote.notify(scope: "state", revision: revision) }
    }

    /// The phone's install, one step at a time as the update moves: check, download, then install and restart.
    private func advancePhoneAppUpdate() {
        switch appUpdate.phase {
        case .idle, .upToDate:
            // A check found nothing newer: the request is over.
            phoneRequestedAppUpdate = false
        case .available:
            if appUpdate.availability?.isNewer == true { downloadAppUpdate() } else { phoneRequestedAppUpdate = false }
        case .ready:
            phoneRequestedAppUpdate = false
            // After the change that made it ready has been applied.
            Task { @MainActor [weak self] in self?.installAppUpdateAndRelaunch() }
        case .failed:
            phoneRequestedAppUpdate = false
        case .checking, .downloading, .staging, .installing:
            break
        }
    }
}
