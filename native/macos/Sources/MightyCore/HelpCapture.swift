import Foundation

// The help site's screenshots: the list of screens (docs/help/shots.json), the demo
// profile they are taken with (native/contracts/fixtures/demo-profile.json), the
// replay that turns the profile's canned CLI lines into the run events a real run
// sends, and the delegation demo's records. Both files ship byte-identical in this
// module's bundle under Help/, which is what `--help-capture` reads.

/// A screen `--help-capture` knows how to open. shots.json names one per shot; a name
/// that is not here stops the capture before anything is taken.
public enum HelpScreen: String, CaseIterable, Sendable {
    case welcome
    case overview
    case sidebarCollapsed = "sidebar-collapsed"
    case dashboard
    case resumeChoice = "resume-choice"
    case resumeList = "resume-list"
    case agentBasic = "agent-basic"
    case agentMighty = "agent-mighty"
    case agentMightyOverview = "agent-mighty-overview"
    case agentTimeline = "agent-timeline"
    case resultCard = "result-card"
    case composer
    case composerSettings = "composer-settings"
    case sessionInfo = "session-info"
    case planCard = "plan-card"
    case planDocument = "plan-document"
    case questionCard = "question-card"
    case permissionCard = "permission-card"
    case backgroundWork = "background-work"
    case terminalPane = "terminal-pane"
    case filesPane = "files-pane"
    case companionPet = "companion-pet"
    case settingsGeneral = "settings-general"
    case settingsModels = "settings-models"
    case settingsStyles = "settings-styles"
    case settingsTools = "settings-tools"
    case settingsCLI = "settings-cli"
    case settingsMobile = "settings-mobile"
    case settingsCompanion = "settings-companion"
    case settingsAbout = "settings-about"
    case delegationTree = "delegation-tree"
    case delegationHeld = "delegation-held"
    case delegationParentClosed = "delegation-parent-closed"

    /// The demo pane (its `role` in the profile) the screen shows, if it shows one.
    public var paneRole: String? {
        switch self {
        case .agentBasic: "codex"
        case .agentMighty, .agentMightyOverview, .agentTimeline, .composer, .composerSettings, .sessionInfo, .backgroundWork: "mighty"
        case .resultCard: "result"
        case .planCard, .planDocument: "plan"
        case .questionCard: "question"
        case .permissionCard: "permission"
        case .terminalPane: "shell"
        case .delegationTree, .delegationHeld: "parent"
        case .delegationParentClosed: "child-of-closed"
        default: nil
        }
    }

    /// The Settings entry (`SettingsPane` raw value) a settings screen opens.
    public var settingsPane: String? {
        switch self {
        case .settingsGeneral: "general"
        case .settingsModels: "models"
        case .settingsStyles: "styles"
        case .settingsTools: "tools"
        case .settingsCLI: "cli"
        case .settingsMobile: "mobile"
        case .settingsCompanion: "companion"
        case .settingsAbout: "about"
        default: nil
        }
    }
}

public struct HelpShotSize: Codable, Sendable, Equatable {
    public let width: Double
    public let height: Double
}

public struct HelpShot: Decodable, Sendable, Equatable {
    public let id: String
    public let section: String
    public let screen: HelpScreen
    public let window: HelpShotSize?
    /// The themes it is taken in, in order; light and dark when the file names none.
    public let themes: [String]

    private enum CodingKeys: String, CodingKey { case id, section, screen, window, themes }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(String.self, forKey: .id)
        section = try c.decode(String.self, forKey: .section)
        let name = try c.decode(String.self, forKey: .screen)
        guard let screen = HelpScreen(rawValue: name) else {
            throw DecodingError.dataCorruptedError(forKey: .screen, in: c, debugDescription: "shot \(id): unknown screen \(name)")
        }
        self.screen = screen
        window = try c.decodeIfPresent(HelpShotSize.self, forKey: .window)
        themes = try c.decodeIfPresent([String].self, forKey: .themes) ?? HelpShotList.themes
    }
}

public struct HelpShotList: Decodable, Sendable {
    public static let themes = ["light", "dark"]
    public let version: Int
    /// The help site's sections, in its table of contents order.
    public let sections: [String]
    public let shots: [HelpShot]

    /// Decodes and checks the list: unique, path-safe ids, known sections, known
    /// themes and sane window sizes. Any problem throws; nothing is skipped.
    public static func decode(_ data: Data) throws -> HelpShotList {
        let list = try JSONDecoder().decode(HelpShotList.self, from: data)
        var ids = Set<String>()
        for shot in list.shots {
            guard shot.id.range(of: "^[a-z0-9]+(?:-[a-z0-9]+)*$", options: .regularExpression) != nil else { throw MightyError("shot id \(shot.id) is not lower-case words joined by hyphens") }
            guard ids.insert(shot.id).inserted else { throw MightyError("shot id \(shot.id) is listed twice") }
            guard list.sections.contains(shot.section) else { throw MightyError("shot \(shot.id): unknown section \(shot.section)") }
            guard !shot.themes.isEmpty, shot.themes.allSatisfy(themes.contains), Set(shot.themes).count == shot.themes.count else { throw MightyError("shot \(shot.id): themes must be light and/or dark") }
            if let size = shot.window, !((640...3000).contains(size.width) && (480...2000).contains(size.height)) { throw MightyError("shot \(shot.id): window size out of range") }
        }
        guard !list.shots.isEmpty else { throw MightyError("shots.json lists no shots") }
        return list
    }
}

/// The demo profile for one language: every localized string already resolved, and
/// each run's frames as the JSON lines the CLI would print.
public struct DemoProfile: Decodable, Sendable {
    public struct File: Decodable, Sendable { public let path: String; public let content: String }
    public struct WorkspaceSpec: Decodable, Sendable {
        public let id: String
        public let name: String
        /// The folder made under the profile directory.
        public let directory: String
        public let files: [File]
    }
    public struct Run: Decodable, Sendable {
        public let input: String
        /// "completed", "error" or "stopped" end the run; nil leaves it running.
        public let finish: String?
        public let elapsedSeconds: Double?
        public let frames: [String]
    }
    public struct Pane: Decodable, Sendable {
        public let role: String
        public let workspace: String
        public let kind: String
        public let provider: String
        public let model: String
        public let effort: String
        public let permissionMode: String
        /// "default" or "mighty".
        public let view: String
        public let title: String
        public let draft: String?
        public let runs: [Run]
    }
    public struct Layout: Decodable, Sendable { public let workspace: String; public let preset: String; public let active: String }
    public struct Terminal: Decodable, Sendable { public let role: String; public let command: String; public let output: [String] }
    public struct ResumeRecord: Decodable, Sendable {
        public let provider: String
        public let workspace: String
        public let sessionId: String
        public let minutesAgo: Double
        public let requests: Int
        public let title: String
    }
    /// A CLI as Settings shows it: installed at `version`, signed in to a fictional `account`.
    public struct Provider: Decodable, Sendable {
        public let id: String
        public let version: String
        public let account: String?
        public let plan: String?
    }
    /// The agent-delegation demo (macOS only): parent panes with their delegated
    /// children, and children of a parent whose pane is closed. Its workspace and
    /// panes are seeded when the first delegation shot is taken, so the shots before
    /// it do not show them; the capture then loads its records into the app's
    /// delegation coordinator (`HelpDemoDelegation`).
    public struct Delegation: Decodable, Sendable {
        public struct ChildSpec: Decodable, Sendable {
            /// The child pane's session id, which its notices and its parent's tool results name.
            public let id: String
            /// The child's pane in `panes`; an open child has one, a closed child none.
            public let role: String
            /// The role of its parent's pane, or one of `closedParents`.
            public let parent: String
            public let state: ChildState
            /// The permission mode its parent asked for.
            public let mode: String
            /// Its TASK.md.
            public let task: String
            public let reportRevision: Int
            /// The head its current report was made at; a reported or merged child has one.
            public let reportHead: String?
            /// How its report was fast-forwarded into the parent's branch, for a
            /// merged child or one whose pane closed after its merge.
            public let merge: MergeRecord.Kind?
        }
        public struct NoticeSpec: Decodable, Sendable {
            public let id: String
            /// The role of the child it is from.
            public let child: String
            public let revision: Int
            public let kind: Notice.Kind
            public let lane: DeliveryLane
        }
        public let workspace: String
        /// The parents' branch, and its head when the children were made.
        public let parentBranch: String
        public let baseCommit: String
        /// Parents whose pane is closed: no pane has their role.
        public let closedParents: [String]
        public let children: [ChildSpec]
        public let notices: [NoticeSpec]

        public func child(_ role: String) -> ChildSpec? { children.first { $0.role == role } }
    }

    public let version: Int
    public let languages: [String]
    public let providers: [Provider]
    public let workspaces: [WorkspaceSpec]
    public let panes: [Pane]
    public let layouts: [Layout]
    public let activeWorkspace: String
    public let terminal: Terminal
    public let resumeSessions: [ResumeRecord]
    public let delegation: Delegation

    public func pane(_ role: String) -> Pane? { panes.first { $0.role == role } }

    /// The profile as `language` reads it. A localized object missing that language
    /// (or any other the file declares) throws, as does an unknown pane reference.
    public static func load(_ data: Data, language: String) throws -> DemoProfile {
        guard let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let languages = root["languages"] as? [String], !languages.isEmpty else { throw MightyError("demo profile: no languages") }
        guard languages.contains(language) else { throw MightyError("demo profile: no \(language) copy") }
        let resolved = try resolve(root, language: language, languages: Set(languages), path: "$")
        let profile = try JSONDecoder().decode(DemoProfile.self, from: JSONSerialization.data(withJSONObject: resolved))
        let workspaceIds = Set(profile.workspaces.map(\.id))
        let roles = profile.panes.map(\.role)
        guard Set(roles).count == roles.count else { throw MightyError("demo profile: a pane role is used twice") }
        for pane in profile.panes where !workspaceIds.contains(pane.workspace) { throw MightyError("demo profile: pane \(pane.role) names an unknown workspace") }
        for layout in profile.layouts where !workspaceIds.contains(layout.workspace) || profile.pane(layout.active) == nil { throw MightyError("demo profile: a layout names an unknown workspace or pane") }
        guard workspaceIds.contains(profile.activeWorkspace), profile.pane(profile.terminal.role)?.kind == "shell" else { throw MightyError("demo profile: unknown active workspace or terminal pane") }
        for pane in profile.panes {
            for run in pane.runs where run.finish.map({ !MightyGraphSupport.terminal($0) }) == true { throw MightyError("demo profile: pane \(pane.role) ends a run as \(run.finish!)") }
        }
        for workspace in profile.workspaces {
            for file in workspace.files where file.path.hasPrefix("/") || file.path.split(separator: "/").contains("..") { throw MightyError("demo profile: \(file.path) leaves its workspace") }
        }
        try profile.delegation.check(in: profile)
        return profile
    }

    /// Every localized object in the file with its JSON path, for checks that look at
    /// the copy itself.
    public static func localizedObjects(in data: Data) throws -> [(path: String, values: [String: Any])] {
        guard let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let languages = root["languages"] as? [String] else { throw MightyError("demo profile: no languages") }
        var found: [(String, [String: Any])] = []
        func visit(_ value: Any, _ path: String) {
            if let object = value as? [String: Any] {
                if isLocalized(object, languages: Set(languages)) { found.append((path, object)); return }
                for (key, child) in object { visit(child, path + "." + key) }
            } else if let array = value as? [Any] {
                for (index, child) in array.enumerated() { visit(child, path + "[\(index)]") }
            }
        }
        visit(root, "$")
        return found
    }

    /// An object whose keys are all language codes is one localized string.
    static func isLocalized(_ object: [String: Any], languages: Set<String>) -> Bool {
        !object.isEmpty && Set(object.keys).isSubset(of: languages)
    }

    private static func resolve(_ value: Any, language: String, languages: Set<String>, path: String) throws -> Any {
        if let object = value as? [String: Any] {
            if isLocalized(object, languages: languages) {
                guard Set(object.keys) == languages, object.values.allSatisfy({ ($0 as? String)?.isEmpty == false }) else {
                    throw MightyError("demo profile: \(path) must give every language a text")
                }
                return object[language]!
            }
            var out: [String: Any] = [:]
            for (key, child) in object {
                let resolved = try resolve(child, language: language, languages: languages, path: path + "." + key)
                if key == "frames", let frames = resolved as? [Any] {
                    // The CLI prints one JSON object per line.
                    out[key] = try frames.map { frame -> String in
                        guard frame is [String: Any] else { throw MightyError("demo profile: \(path).frames holds a non-object") }
                        return String(decoding: try JSONSerialization.data(withJSONObject: frame, options: [.sortedKeys, .withoutEscapingSlashes]), as: UTF8.self)
                    }
                } else { out[key] = resolved }
            }
            return out
        }
        if let array = value as? [Any] {
            return try array.enumerated().map { try resolve($1, language: language, languages: languages, path: path + "[\($0)]") }
        }
        return value
    }
}

extension DemoProfile.Delegation {
    /// Refuses a demo the capture could not seed or the app could not reach: a
    /// workspace that is not its own (the active one is seeded first), a pane that
    /// is not a Claude pane, an open child without its pane or a closed one with
    /// one, a parent that is neither a pane of the demo nor closed, a parent with
    /// more open children than delegate allows, a report or merge without its
    /// revision and head, or a notice from a child or revision it does not have.
    func check(in profile: DemoProfile) throws {
        guard profile.workspaces.contains(where: { $0.id == workspace }), workspace != profile.activeWorkspace else { throw MightyError("demo profile: the delegation demo needs a workspace of its own") }
        let roles = children.map(\.role)
        guard Set(roles).count == roles.count, Set(children.map(\.id)).count == children.count, Set(notices.map(\.id)).count == notices.count else {
            throw MightyError("demo profile: a delegation child or notice is listed twice")
        }
        for pane in profile.panes where pane.workspace == workspace && (pane.kind != SessionKind.claude || pane.provider != "claude") {
            throw MightyError("demo profile: the delegation pane \(pane.role) is not a Claude pane")
        }
        for child in children {
            let pane = profile.pane(child.role)
            let placed = child.state.isOpen ? pane?.workspace == workspace : pane == nil
            guard placed, ChildWorktreeMaker.isSafeSessionId(child.id), DelegationCoordinator.startingModes.contains(child.mode) else {
                throw MightyError("demo profile: the delegation child \(child.role) is open without a pane, closed with one, or has an id or mode delegate never gives")
            }
            let openParent = profile.pane(child.parent).map { $0.workspace == workspace && !roles.contains(child.parent) } ?? false
            let closedParent = closedParents.contains(child.parent) && profile.pane(child.parent) == nil
            guard openParent || closedParent else { throw MightyError("demo profile: the delegation child \(child.role) names an unknown parent") }
            if [.reported, .merged].contains(child.state) || child.merge != nil, child.reportRevision < 1 || child.reportHead == nil { throw MightyError("demo profile: the delegation child \(child.role) reported without a revision and head") }
            // The demo's merges are fast-forwards: the merged commit is the reported head.
            if child.merge == .cardMergeCommit || (child.state == .merged && child.merge == nil) { throw MightyError("demo profile: the delegation child \(child.role) is merged without a fast-forward merge") }
        }
        for parent in Set(children.map(\.parent)) where children.filter({ $0.parent == parent && $0.state.isOpen }).count > DelegationCoordinator.maximumOpenChildren {
            throw MightyError("demo profile: the delegation parent \(parent) has more open children than delegate allows")
        }
        for notice in notices where child(notice.child).map({ !(0...$0.reportRevision).contains(notice.revision) }) ?? true {
            throw MightyError("demo profile: the delegation notice \(notice.id) names an unknown child or revision")
        }
    }
}

/// The delegation records of the demo (macOS only): the delegation file once the
/// demo's parents have delegated, which `--help-capture` loads into the app's
/// coordinator after it opened the panes.
public enum HelpDemoDelegation {
    /// The run each delivered notice went to, as its receipt names it.
    public static let deliveredRunId = "help-demo-run"

    /// The file of `profile`'s delegation demo. `parents` gives the session id of
    /// each parent role: an open parent's pane, or an id no pane has for a closed
    /// one. The children keep the profile's order and ids, work in worktrees under
    /// `worktrees` with `checkout` as their parent's checkout, and keep their TASK
    /// copies. A merged child has the record of its fast-forward from the base
    /// commit to its reported head; one whose pane then closed keeps that head as
    /// the head it was closed at, as closing a merged child does, so either card
    /// offers undo. Notices keep their lanes, a delivered one with its receipt.
    public static func file(_ profile: DemoProfile, parents: [String: String], worktrees: ChildWorktreeMaker, checkout: String, now: Date = Date()) throws -> DelegationFile {
        let demo = profile.delegation
        var file = DelegationFile()
        for child in demo.children {
            guard let parentId = parents[child.parent] else { throw MightyError("the delegation demo has no session for \(child.parent)") }
            var record = ChildRecord(id: child.id, parentSessionId: parentId, worktreePath: worktrees.worktreePath(for: child.id), parentBranch: demo.parentBranch,
                                     baseCommit: demo.baseCommit, startingMode: child.mode,
                                     requestKey: DelegationRequestKey.make(parentSessionId: parentId, parentRunId: deliveredRunId, task: child.task, startingMode: child.mode),
                                     reportRevision: child.reportRevision, reportHead: child.reportHead, state: child.state, parentCheckout: checkout)
            file.setCopy(DelegationCopy(childId: child.id, kind: .task, revision: 0, contents: Data(child.task.utf8)))
            if let kind = child.merge, let head = child.reportHead {
                file.merges.append(MergeRecord(childId: child.id, kind: kind, parentBranch: demo.parentBranch, preMergeCommit: demo.baseCommit, mergedCommit: head, childHead: head))
                if child.state == .closed { record.closedHead = head }
            }
            file.children.append(record)
        }
        let time = ISO8601DateFormatter().string(from: now)
        for notice in demo.notices {
            guard let child = demo.child(notice.child) else { throw MightyError("the delegation demo has no child \(notice.child)") }
            let receipt = notice.lane == .delivered ? DeliveryReceipt(time: time, route: .queue, runId: deliveredRunId) : nil
            file.notices.append(Notice(id: notice.id, childId: child.id, reportRevision: notice.revision, kind: notice.kind, lane: notice.lane, receipt: receipt))
        }
        return file
    }
}

/// Where `--help-capture` finds its two files: the MightyCore bundle's Help/ folder,
/// searched like the locale catalogs (`ResourceHealthChecker.checkHelpFile`).
public enum HelpCaptureResources {
    public static let demoProfile = "demo-profile.json"
    public static let shots = "shots.json"
    public static let files = [demoProfile, shots]
}

/// Claude session records for the demo's "continue a session" list.
public enum HelpDemoRecords {
    /// The folder a record goes in, under a Claude config folder.
    public static func claudeFolder(config: URL, workspacePath: String) -> URL {
        config.appendingPathComponent("projects/" + SessionHistory.claudeProjectFolder(workspacePath), isDirectory: true)
    }

    /// The record's JSON lines: `record.requests` requests, each answered, all in
    /// `workspacePath`, the last one `minutesAgo` before `now`.
    public static func claudeLines(_ record: DemoProfile.ResumeRecord, workspacePath: String, now: Date = Date()) throws -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let modified = now.addingTimeInterval(-record.minutesAgo * 60)
        var lines: [[String: Any]] = []
        let count = max(1, record.requests)
        for index in 0..<count {
            let stamp = formatter.string(from: modified.addingTimeInterval(Double(index - count) * 90))
            lines.append(["type": "user", "uuid": "u\(index)-" + record.sessionId, "sessionId": record.sessionId, "cwd": workspacePath, "timestamp": stamp,
                          "message": ["role": "user", "content": record.title]])
            lines.append(["type": "assistant", "uuid": "a\(index)-" + record.sessionId, "sessionId": record.sessionId, "cwd": workspacePath, "timestamp": stamp,
                          "message": ["role": "assistant", "model": "claude-sonnet-5-5", "content": [["type": "text", "text": record.title]]]])
        }
        return try lines.map { String(decoding: try JSONSerialization.data(withJSONObject: $0, options: [.sortedKeys, .withoutEscapingSlashes]), as: UTF8.self) }
            .joined(separator: "\n") + "\n"
    }
}

/// Replays one demo run through the same parser and Claude permission channel a real
/// run uses, and returns the events the store would receive, in order: the request,
/// `running`, everything the frames produce, and for a finished run its end.
public enum HelpDemoReplay {
    public static func events(sessionId: String, provider: String, model: String, permissionMode: String,
                              inputId: String, input: String, frames: [String], finish: String?) -> [RunEvent] {
        final class Box { var events: [RunEvent] = [] }
        let box = Box()
        let runId = "demo-run-" + inputId
        func emit(_ event: RunEvent) { box.events.append(event) }
        emit(RunEvent(sessionId: sessionId, type: "log", entry: LogEntry(id: inputId, kind: "user", text: input)))
        emit(RunEvent(sessionId: sessionId, type: "status", status: "running"))
        // A steady fake clock gives tool rows believable durations.
        var tick: TimeInterval = 0
        var parser: CLIStreamParser?
        let permissions: ClaudePermissionChannel? = provider == "claude" ? ClaudePermissionChannel(
            runId: runId, prompt: Data(), write: { _ in },
            emit: { emit(RunEvent(sessionId: sessionId, type: "permission", permission: $0)) },
            activity: { permission, state in parser?.permissionActivity(permission, state: state) },
            warning: { emit(RunEvent(sessionId: sessionId, type: "log", entry: LogEntry(kind: "system", text: $0, provider: provider))) },
            fail: { emit(RunEvent(sessionId: sessionId, type: "log", entry: LogEntry(kind: "error", text: $0, provider: provider))) },
            plan: { record in var value = record; value.graphRunId = runId; emit(RunEvent(sessionId: sessionId, type: "plan", plan: value)) },
            paneMode: permissionMode) : nil
        func activity(_ value: AgentActivity) {
            guard let value = ActivitySupport.normalized(value) else { return }
            if value.kind != "turn" {
                emit(RunEvent(sessionId: sessionId, type: "log", entry: LogEntry(id: value.id, kind: "system", text: value.summary, provider: value.provider, activity: value)))
            }
            emit(RunEvent(sessionId: sessionId, type: "activity", activity: value))
        }
        let stream = CLIStreamParser(provider: provider,
            log: { kind, text in
                guard !text.isEmpty else { return }
                emit(RunEvent(sessionId: sessionId, type: "log", entry: LogEntry(kind: kind, text: text, provider: provider)))
            },
            resume: { emit(RunEvent(sessionId: sessionId, type: "resume", resumeId: $0)) },
            activityNamespace: runId,
            activity: activity,
            control: { permissions?.receive($0) },
            activityClock: { tick += 1.4; return tick },
            usage: { emit(RunEvent(sessionId: sessionId, type: "usage", usage: $0)) },
            graph: { emit(RunEvent(sessionId: sessionId, type: "graph", graph: $0)) },
            graphInput: input, configuredModel: model,
            todos: { emit(RunEvent(sessionId: sessionId, type: "todos", todos: $0)) },
            background: { emit(RunEvent(sessionId: sessionId, type: "background", background: $0)) })
        parser = stream
        for frame in frames { stream.push(frame + "\n") }
        stream.flush()
        if let finish {
            permissions?.cancelAll()
            stream.finishActivities(stopped: finish == "stopped")
            stream.finishGraph(state: finish)
            stream.finishBackground()
            let summary = finish == "completed" ? L("run.activity.completed") : finish == "stopped" ? L("run.activity.stopped") : L("run.activity.error")
            activity(AgentActivity(id: runId, provider: provider, kind: "turn", state: finish, summary: summary))
            emit(RunEvent(sessionId: sessionId, type: "status", status: finish))
        }
        return box.events
    }
}
