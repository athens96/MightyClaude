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

    /// The panes as the planner sees them. Handed on even while phone remote access is off: the
    /// service keeps watching and only sends while it is on, so switching it on later pushes what
    /// is new from then, not everything that happened meanwhile.
    func mobilePushObserve(snapshot incoming: AppSnapshot? = nil, permissions: [String: [ToolPermissionRequest]]? = nil,
                           antigravity: AntigravityPermissionBoard? = nil, login: [String: String]? = nil) {
        guard !ending, mobileBridge != nil else { return }
        let snapshot = incoming ?? self.snapshot
        let permissions = permissions ?? toolPermissions
        let antigravity = antigravity ?? antigravityBoard
        // Only a confirmed lost sign-in (`loginRequired`), never a raw auth failure.
        let login = login ?? loginRequired
        let names = Dictionary(snapshot.workspaces.map { ($0.id, $0.name) }, uniquingKeysWith: { first, _ in first })
        // The panes the phone can see, as in `mobileObserve`: no files panes, no remote workspaces.
        let sessions = FilePaneKind.phoneVisible(RemoteWorkspacePhone.sessions(snapshot.sessions, workspaces: snapshot.workspaces))
        let panes = sessions.map { session -> MobilePushPane in
            // A request a style is answering on its own never reaches the phone.
            var requests = (permissions[session.id] ?? [])
                .filter { $0.state == "pending" && !guidedAutoAllowing.contains(permissionResponseKey(sessionId: session.id, request: $0)) }
                .map { MobilePushRequest(MobilePermission(request: $0)) }
            if let card = antigravity[session.id] { requests.append(MobilePushRequest(MobilePermission(antigravity: card))) }
            let last = session.logs.last(where: { $0.activity == nil && !$0.text.isEmpty })
            // The raw run status: a pane waiting on a permission is still "running".
            return MobilePushPane(sessionId: session.id, workspace: names[session.workspaceId] ?? "", title: session.title, status: session.status,
                                  preview: last.map { String($0.text.prefix(400)) }, requests: requests, signInLost: login[session.id] != nil)
        }
        // One at a time, in order: each hand-off waits for the one before.
        let previous = mobilePushTask
        mobilePushTask = Task { [mobileRemote] in
            await previous?.value
            await mobileRemote.pushObserve(panes)
        }
    }
}
