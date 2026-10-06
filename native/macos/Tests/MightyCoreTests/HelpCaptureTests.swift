import Foundation
import Testing
@testable import MightyCore

/// docs/help/shots.json and native/contracts/fixtures/demo-profile.json, which the
/// macOS `--help-capture` mode reads, checked without launching the app.
struct HelpCaptureTests {
    static let root: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url
    }()
    static let profileURL = root.appendingPathComponent("native/contracts/fixtures/demo-profile.json")
    static let shotsURL = root.appendingPathComponent("docs/help/shots.json")
    static let languages = ["ko", "en", "zh", "ja"]

    static func profile(_ language: String) throws -> DemoProfile {
        try DemoProfile.load(Data(contentsOf: profileURL), language: language)
    }

    /// The pane's runs replayed into a session the way AppStore.apply records them.
    static func replay(_ pane: DemoProfile.Pane, id: String = "pane") -> (session: RunSession, events: [RunEvent]) {
        var session = RunSession(id: id, workspaceId: "ws", title: pane.title, kind: pane.kind, provider: pane.provider, model: pane.model,
                                 settings: RunSettings(effort: pane.effort, permissionMode: pane.permissionMode))
        var all: [RunEvent] = []
        for (index, run) in pane.runs.enumerated() {
            let events = HelpDemoReplay.events(sessionId: id, provider: pane.provider, model: pane.model, permissionMode: pane.permissionMode,
                                               inputId: "\(pane.role)-\(index)", input: run.input, frames: run.frames, finish: run.finish)
            for event in events {
                session.recordGraph(event)
                session.recordPlanMode(event)
                session.recordSessionUsage(event)
                switch event.type {
                case "log": if let entry = event.entry {
                    if let at = session.logs.firstIndex(where: { $0.id == entry.id }) { session.logs[at] = entry } else { session.logs.append(entry) }
                }
                case "status": if let status = event.status { session.status = status }
                case "resume": session.resumeId = event.resumeId
                default: break
                }
            }
            all += events
        }
        return (session, all)
    }

    /// The pane's workspace written to a temporary folder, for the result-card file links.
    static func workspaceFolder(_ profile: DemoProfile, _ id: String) throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-help-" + UUID().uuidString, isDirectory: true)
        let workspace = try #require(profile.workspaces.first { $0.id == id })
        for file in workspace.files {
            let url = folder.appendingPathComponent(file.path)
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data(file.content.utf8).write(to: url)
        }
        return folder
    }

    @Test func theShotListDecodesAndEveryScreenIsOneTheCaptureKnows() throws {
        let list = try HelpShotList.decode(Data(contentsOf: Self.shotsURL))
        // The help site's table of contents (the four-language help plan, stage 4).
        #expect(list.sections == ["getting-started", "layout", "agent-pane", "styles", "approvals", "terminal-files-browser", "settings", "phone", "windows", "faq"])
        // Every screen the capture can open is listed, so none is dead code.
        #expect(Set(list.shots.map(\.screen)) == Set(HelpScreen.allCases))
        let profile = try Self.profile("en")
        for shot in list.shots {
            #expect(shot.themes == ["light", "dark"], "\(shot.id)")
            if let role = shot.screen.paneRole { #expect(profile.pane(role) != nil, "\(shot.id) needs a \(role) pane") }
        }
        // Raw JSON as well: the screen names are exactly the enum's raw values.
        let raw = try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: Self.shotsURL)) as? [String: Any])
        for shot in try #require(raw["shots"] as? [[String: Any]]) {
            #expect(HelpScreen(rawValue: shot["screen"] as? String ?? "") != nil)
        }
    }

    @Test func anUnknownScreenOrSectionStopsTheList() {
        let unknownScreen = #"{"version":1,"sections":["layout"],"shots":[{"id":"a","section":"layout","screen":"no-such-screen"}]}"#
        #expect(throws: (any Error).self) { try HelpShotList.decode(Data(unknownScreen.utf8)) }
        let unknownSection = #"{"version":1,"sections":["layout"],"shots":[{"id":"a","section":"faq","screen":"overview"}]}"#
        #expect(throws: (any Error).self) { try HelpShotList.decode(Data(unknownSection.utf8)) }
        let twice = #"{"version":1,"sections":["layout"],"shots":[{"id":"a","section":"layout","screen":"overview"},{"id":"a","section":"layout","screen":"dashboard"}]}"#
        #expect(throws: (any Error).self) { try HelpShotList.decode(Data(twice.utf8)) }
        let badTheme = #"{"version":1,"sections":["layout"],"shots":[{"id":"a","section":"layout","screen":"overview","themes":["sepia"]}]}"#
        #expect(throws: (any Error).self) { try HelpShotList.decode(Data(badTheme.utf8)) }
    }

    @Test func everyLocalizedStringCarriesAllFourLanguages() throws {
        let data = try Data(contentsOf: Self.profileURL)
        let objects = try DemoProfile.localizedObjects(in: data)
        #expect(objects.count >= 60)
        for (path, values) in objects {
            #expect(Set(values.keys) == Set(Self.languages), "\(path)")
            for language in Self.languages {
                let text = values[language] as? String
                #expect(text?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == false, "\(path).\(language)")
            }
        }
        for language in Self.languages { _ = try Self.profile(language) }
        // A string with a language missing is refused, not shown in another language.
        let partial = #"{"version":1,"languages":["ko","en","zh","ja"],"title":{"ko":"가","en":"a"}}"#
        #expect(throws: (any Error).self) { try DemoProfile.load(Data(partial.utf8), language: "en") }
    }

    @Test func theDemoIsFictionalAndStaysInsideItsWorkspaces() throws {
        let text = try String(contentsOf: Self.profileURL, encoding: .utf8)
        for forbidden in ["/Users/", "/home/", "/private/", "@gbike", "sk-ant-", "ghp_"] {
            #expect(!text.contains(forbidden), "\(forbidden)")
        }
        let profile = try Self.profile("en")
        #expect(profile.workspaces.map(\.name) == ["my-app", "docs-site"])
        for workspace in profile.workspaces {
            #expect(!workspace.directory.contains("/") && !workspace.directory.contains(".."))
            for file in workspace.files { #expect(!file.path.hasPrefix("/") && !file.path.contains("..")) }
        }
    }

    @Test func theBundledCopiesAreByteIdentical() throws {
        let bundled = Self.root.appendingPathComponent("native/macos/Sources/MightyCore/Resources/Help")
        #expect(try Data(contentsOf: bundled.appendingPathComponent(HelpCaptureResources.demoProfile)) == Data(contentsOf: Self.profileURL))
        #expect(try Data(contentsOf: bundled.appendingPathComponent(HelpCaptureResources.shots)) == Data(contentsOf: Self.shotsURL))
    }

    @Test(arguments: ["ko", "en", "zh", "ja"])
    func theMightyPaneReplaysIntoADiagramResultTodosAndBackgroundWork(language: String) throws {
        let profile = try Self.profile(language)
        let pane = try #require(profile.pane("mighty"))
        let (session, events) = Self.replay(pane)
        let runs = try #require(session.graphRuns)
        #expect(runs.count == 2)
        // The finished request: tool steps, a sub-agent with its own steps, a result.
        let finished = runs[0]
        #expect(finished.status == "completed" && finished.settled)
        let kinds = Set(finished.rootEntries.compactMap(\.activity?.kind))
        #expect(kinds.isSuperset(of: ["read", "edit", "command"]))
        let agent = try #require(finished.agents.first { MightyGraphSupport.blockKind($0) == "agent" })
        #expect(agent.status == "completed" && agent.entries.contains { $0.activity?.kind == "read" } && !agent.title.isEmpty)
        let result = try #require(finished.resultEntries.first)
        let folder = try Self.workspaceFolder(profile, pane.workspace)
        defer { try? FileManager.default.removeItem(at: folder) }
        let files = ReferenceLinkSupport.resultFiles(in: [result.text], root: folder).map(\.path)
        #expect(files == ["src/components/LoginForm.tsx", "src/api/session.ts", "src/api/session.test.ts"])
        // The next request: its turn is over while background work still runs.
        #expect(runs[1].input == pane.runs[1].input)
        #expect(session.status == "running")
        let todos = try #require(session.todoProgress)
        #expect(todos.total == 3 && todos.completed == 1 && todos.current?.status == "in_progress")
        let work = try #require(session.backgroundWork)
        #expect(work.waitingOnBackground && work.running.count == 1)
        #expect(runs[1].agents.contains { $0.isTask && $0.status == "running" })
        // Usage for the session details: the model's context window and a cost.
        let usage = try #require(session.sessionUsage)
        #expect(usage.contextWindowTokens == 200_000 && (usage.contextUsedTokens ?? 0) > 0 && usage.costUSD != nil)
        #expect(!events.contains { $0.entry?.kind == "error" })
        #expect(session.resumeId != nil)
    }

    @Test(arguments: ["ko", "en", "zh", "ja"])
    func theApprovalPanesWaitOnAPlanAQuestionAndAToolPermission(language: String) throws {
        let profile = try Self.profile(language)
        func pending(_ role: String) throws -> (RunSession, ToolPermissionRequest) {
            let (session, events) = Self.replay(try #require(profile.pane(role)), id: role)
            let permission = try #require(events.compactMap(\.permission).last)
            #expect(permission.state == "pending" && session.status == "running", "\(role)")
            return (session, permission)
        }
        let (planSession, plan) = try pending("plan")
        #expect(plan.canAnswerPlan && plan.plan?.isEmpty == false && !plan.canAllow)
        #expect(planSession.settings.permissionMode == "plan")
        let (_, question) = try pending("question")
        #expect(question.canAnswerQuestions && question.questionnaire?.questions.count == 2)
        #expect(question.questionnaire?.questions.contains { $0.multiSelect } == true)
        let (_, tool) = try pending("permission")
        #expect(tool.toolName == "Bash" && tool.canAllow && tool.summary == "npm run deploy:preview")
    }

    @Test(arguments: ["ko", "en", "zh", "ja"])
    func theCodexAndResultPanesFinishWithTheirOutput(language: String) throws {
        let profile = try Self.profile(language)
        let codexPane = try #require(profile.pane("codex"))
        let (codex, _) = Self.replay(codexPane)
        #expect(codex.status == "completed" && codex.resumeId != nil)
        #expect(codex.logs.filter { $0.kind == "assistant" }.count == 2)
        #expect(codex.logs.contains { $0.activity?.kind == "command" && $0.activity?.state == "completed" })
        #expect(codex.logs.contains { $0.activity?.kind == "edit" })

        let resultPane = try #require(profile.pane("result"))
        let (docs, _) = Self.replay(resultPane)
        let run = try #require(docs.graphRuns?.last)
        #expect(run.status == "completed" && run.settled && run.agents.count == 1)
        let folder = try Self.workspaceFolder(profile, resultPane.workspace)
        defer { try? FileManager.default.removeItem(at: folder) }
        #expect(ReferenceLinkSupport.resultFiles(in: run.resultEntries.map(\.text), root: folder).map(\.path) == ["docs/getting-started.md", "docs/faq.md"])
    }

    @Test func theResumeRecordsAreListedForTheirWorkspace() throws {
        let profile = try Self.profile("ja")
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-help-resume-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: base) }
        let workspace = base.appendingPathComponent("my-app", isDirectory: true)
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        let path = workspace.resolvingSymlinksInPath().path
        let config = base.appendingPathComponent("claude-config", isDirectory: true)
        let now = Date()
        for record in profile.resumeSessions {
            let folder = HelpDemoRecords.claudeFolder(config: config, workspacePath: path)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let file = folder.appendingPathComponent(record.sessionId + ".jsonl")
            try Data(HelpDemoRecords.claudeLines(record, workspacePath: path, now: now).utf8).write(to: file)
            try FileManager.default.setAttributes([.modificationDate: now.addingTimeInterval(-record.minutesAgo * 60)], ofItemAtPath: file.path)
        }
        let listing = ResumableSessions.listing(ResumableSessionQuery(workspacePath: path, environment: ["CLAUDE_CONFIG_DIR": config.path], now: now), provider: "claude")
        #expect(listing.items.map(\.sessionID) == profile.resumeSessions.map(\.sessionId))
        #expect(listing.items.map(\.title) == profile.resumeSessions.map(\.title))
        #expect(listing.items.map(\.requests) == profile.resumeSessions.map(\.requests))
        #expect(listing.items.allSatisfy { $0.model == "claude-sonnet-5-5" && !ResumableSessions.mayBeRunning($0, now: now) })
    }

    @Test func theBundleCarriesBothFiles() {
        for name in HelpCaptureResources.files {
            let result = ResourceHealthChecker.checkHelpFile(name)
            #expect(result.found, "\(name): \(result.triedPaths)")
        }
    }

    @Test func theProfileNamesItsPanesAndLayouts() throws {
        let profile = try Self.profile("en")
        #expect(Set(profile.panes.map(\.role)) == ["mighty", "plan", "codex", "shell", "result", "question", "permission"])
        #expect(profile.pane("mighty")?.view == "mighty" && profile.pane("codex")?.view == "default" && profile.pane("codex")?.provider == "codex")
        #expect(profile.pane("mighty")?.draft?.isEmpty == false)
        #expect(profile.resumeSessions.count == 3 && profile.resumeSessions.allSatisfy { UUID(uuidString: $0.sessionId) != nil })
        #expect(profile.terminal.output.count > 3)
    }
}
