import Combine
import Foundation
import MightyCore

/// Phone notifications (docs/mobile-remote.md "푸시 알림"): what the panes show is handed to the
/// mobile service, whose planner decides what to push. The Mac is the reference for Windows.
extension AppStore {
    func configureMobilePush() {
        // @Published fires before the property is assigned: pass the incoming value on.
        $snapshot.sink { [weak self] value in self?.mobilePushObserve(snapshot: value) }.store(in: &mobileSubscriptions)
        $toolPermissions.sink { [weak self] value in self?.mobilePushObserve(permissions: value) }.store(in: &mobileSubscriptions)
        $antigravityBoard.sink { [weak self] value in self?.mobilePushObserve(antigravity: value) }.store(in: &mobileSubscriptions)
        $loginRequired.sink { [weak self] value in self?.mobilePushObserve(login: value) }.store(in: &mobileSubscriptions)
    }

    /// Hands the panes (`MobilePushPane.project`) to the planner, one at a time and in order.
    /// Nothing while phone remote access is off; the first hand-off after it comes on starts the
    /// planner over, so what changed meanwhile is not pushed.
    func mobilePushObserve(snapshot incoming: AppSnapshot? = nil, permissions: [String: [ToolPermissionRequest]]? = nil,
                           antigravity: AntigravityPermissionBoard? = nil, login: [String: String]? = nil) {
        guard !ending, mobileBridge != nil else { return }
        let snapshot = incoming ?? self.snapshot
        guard snapshot.mobileRemote?.enabled == true else { mobilePushLast = nil; return }
        let autoAllowing = guidedAutoAllowing
        let panes = MobilePushPane.project(
            sessions: snapshot.sessions, workspaces: snapshot.workspaces, permissions: permissions ?? toolPermissions,
            autoAllowed: { autoAllowing.contains(self.permissionResponseKey(sessionId: $0, request: $1)) },
            antigravity: antigravity ?? antigravityBoard, login: login ?? loginRequired)
        // The same panes again would decide nothing.
        guard panes != mobilePushLast else { return }
        let restart = mobilePushLast == nil
        mobilePushLast = panes
        let previous = mobilePushTask
        mobilePushTask = Task { [mobileRemote] in
            await previous?.value
            if restart { await mobileRemote.pushRestart() }
            await mobileRemote.pushObserve(panes)
        }
    }
}
