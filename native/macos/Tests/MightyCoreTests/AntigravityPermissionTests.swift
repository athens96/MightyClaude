import Testing
import Foundation
@testable import MightyCore

/// Antigravity permission cards: what a headless agy turn was refused, the
/// card, and the rules an allow puts in agy's settings for the follow-up
/// turn. native/contracts/fixtures/antigravity-permissions.json holds the
/// streams (recorded from agy 1.4.4 where it says so) and the byte-exact
/// settings edits the Windows host must match.
struct AntigravityPermissionTests {
    struct Fixture: Decodable {
        struct Stream: Decodable { let name: String; let lines: [String]; let actions: [AntigravityDeniedAction]; let canAllowAlways: Bool? }
        struct Edit: Decodable {
            let name: String; let before: String; let add: [String]; let added: [String]; let created: [String]; let afterAdd: String; let remove: [String]; let afterRemove: String
            let afterPrune: String
        }
        struct Refused: Decodable { let name: String; let before: String; let error: String }
        struct NewFile: Decodable { let add: [String]; let after: String; let created: [String] }
        struct Settings: Decodable { let newFile: NewFile; let edits: [Edit]; let refused: [Refused] }
        struct RuleCase: Decodable { let rule: String; let verdict: String; let reason: String? }
        struct Context: Decodable { let protected: [String] }
        struct Rules: Decodable { let cases: [RuleCase]; let context: Context }
        struct Tools: Decodable { let map: [String: String]; let unmapped: [String] }
        let streams: [Stream]
        let settings: Settings
        let rules: Rules
        let tools: Tools
    }
    static let fixture: Fixture = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        let data = try! Data(contentsOf: url.appendingPathComponent("native/contracts/fixtures/antigravity-permissions.json"))
        return try! JSONDecoder().decode(Fixture.self, from: data)
    }()

    static func errorName(_ error: Error) -> String? {
        guard let error = error as? AntigravitySettingsError else { return nil }
        switch error {
        case .missingFolder: return "missingFolder"
        case .unreadable: return "unreadable"
        case .notJSON: return "notJSON"
        case .notObject: return "notObject"
        case .permissionsNotObject: return "permissionsNotObject"
        case .allowNotArray: return "allowNotArray"
        case .writeFailed: return "writeFailed"
        case .badRule: return "badRule"
        case .onceOnly: return "onceOnly"
        }
    }

    private final class Recorder: @unchecked Sendable {
        var logs: [(String, String)] = []
        var cards: [AntigravityPermissionRequest] = []
    }

    // MARK: Parsing

    @Test func everyFixtureTurnGivesItsActions() throws {
        try LocaleOverride.$language.withValue(.en) { () throws in
            for stream in Self.fixture.streams {
                let recorder = Recorder()
                let parser = CLIStreamParser(provider: "gemini", log: { recorder.logs.append(($0, $1)) }, resume: { _ in }, activityNamespace: "run-1",
                                             antigravityPermission: { recorder.cards.append($0) })
                parser.push(stream.lines.joined(separator: "\n") + "\n"); parser.flush()
                if stream.actions.isEmpty {
                    #expect(recorder.cards.isEmpty, "\(stream.name)")
                    #expect(!recorder.logs.contains { $0.1.contains("Antigravity declined") }, "\(stream.name)")
                    continue
                }
                #expect(recorder.cards.count == 1, "\(stream.name)")
                let card = try #require(recorder.cards.first)
                #expect(card.actions == stream.actions, "\(stream.name)")
                #expect(card.canAllowAlways == stream.canAllowAlways, "\(stream.name) always")
                #expect(card.runId == "run-1" && card.id.hasPrefix("agy-") && CoreValidation.identifier(card.id))
                // One short transcript line, not the old how-to notice.
                let notices = recorder.logs.filter { $0.0 == "system" && $0.1.contains("Antigravity declined") }
                #expect(notices.count == 1 && notices[0].1.contains("card") && !notices[0].1.contains("permissions.allow"), "\(stream.name)")
            }
        }
    }

    @Test func withoutACardTheOlderNoticeStays() throws {
        try LocaleOverride.$language.withValue(.en) { () throws in
            let stream = try #require(Self.fixture.streams.first { $0.name == "command" })
            var logs: [(String, String)] = []
            let parser = CLIStreamParser(provider: "gemini", log: { logs.append(($0, $1)) }, resume: { _ in })
            parser.push(stream.lines.joined(separator: "\n") + "\n"); parser.flush()
            #expect(logs.contains { $0.0 == "system" && $0.1.contains("permissions.allow") && $0.1.contains("RunCommand") })
        }
    }

    @Test func twoTurnsInOneRunGiveTwoCardsAndNothingCarriesOver() throws {
        let stream = try #require(Self.fixture.streams.first { $0.name == "command" })
        let clean = try #require(Self.fixture.streams.first { $0.name == "noDenial" })
        let recorder = Recorder()
        let parser = CLIStreamParser(provider: "gemini", log: { recorder.logs.append(($0, $1)) }, resume: { _ in }, antigravityPermission: { recorder.cards.append($0) })
        parser.push((stream.lines + clean.lines + stream.lines).joined(separator: "\n") + "\n"); parser.flush()
        #expect(recorder.cards.count == 2 && recorder.cards[0].id != recorder.cards[1].id && recorder.cards.allSatisfy { $0.actions.count == 1 })
        // Another provider never builds a card.
        var claudeCards = 0
        let claude = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, antigravityPermission: { _ in claudeCards += 1 })
        claude.push(stream.lines.joined(separator: "\n") + "\n"); claude.flush()
        #expect(claudeCards == 0)
    }

    @Test func rulesAreWellFormedOrAbsent() {
        #expect(AntigravityAllowGrant.validRule("command(git status)"))
        #expect(AntigravityAllowGrant.validRule("write_file(/tmp/a b.txt)"))
        #expect(AntigravityAllowGrant.validRule("read_url(example.com)"))
        for bad in ["command()", "Command(ls)", "command(ls", "(ls)", "command(a\nb)", "command(\0)", "command(" + String(repeating: "x", count: 4_096) + ")"] {
            #expect(!AntigravityAllowGrant.validRule(bad), "\(bad.prefix(20))")
        }
        #expect(AntigravityDenialCollector.namedRule("x: user denied permission for write_file(/a).\nmore") == "write_file(/a)")
        #expect(AntigravityDenialCollector.namedRule("user denied permission for something") == nil)
        #expect(AntigravityDenialCollector.denialMessage(["error": ["message": "exit status 1"]]) == nil)
        #expect(AntigravityDenialCollector.denialMessage(nil) == nil)
    }

    @Test func whichRulesACardMayOfferFollowsTheSharedCases() {
        for item in Self.fixture.rules.cases {
            #expect(AntigravityRuleSafety.verdict(item.rule, protectedFolders: Self.fixture.rules.context.protected).rawValue == item.verdict, "\(item.rule)")
            #expect(AntigravityRuleSafety.onceReason(item.rule, protectedFolders: Self.fixture.rules.context.protected)?.rawValue == item.reason, "\(item.rule) reason")
        }
        #expect(AntigravityRuleSafety.toolPermissions == Self.fixture.tools.map)
        for tool in Self.fixture.tools.unmapped { #expect(AntigravityRuleSafety.toolPermissions[tool] == nil, "\(tool)") }
        // A grant carries no refused rule, and only rules that may be kept are kept for good.
        #expect(!AntigravityAllowGrant(requestId: "agy-1", rules: ["command(*)"], temporary: true).isValid)
        let chained = AntigravityAllowGrant(requestId: "agy-1", rules: ["command(echo hi && date)"], temporary: false)
        #expect(chained.isValid && !chained.keepsOnlyAllowedRules)
        #expect(AntigravityAllowGrant(requestId: "agy-1", rules: ["command(echo hi && date)"], temporary: true).keepsOnlyAllowedRules)
    }

    @Test func aQueuedItemKeepsItsRecordedModeWhenItsStylePlansEveryRequest() {
        // The run's own decision when it was queued wins, plan-mode style or not.
        let queued = QueuedInput(text: "go", permissionModeOverride: nil)
        #expect(BackgroundQueuePolicy.launchOverride(queued: queued, launchesInPlan: true) == nil)
        #expect(BackgroundQueuePolicy.launchOverride(queued: QueuedInput(text: "go", permissionModeOverride: "plan"), launchesInPlan: false) == "plan")
        // Anything not queued starts in plan mode in such a style.
        #expect(BackgroundQueuePolicy.launchOverride(queued: nil, launchesInPlan: true) == "plan")
        #expect(BackgroundQueuePolicy.launchOverride(queued: nil, launchesInPlan: false) == nil)
    }

    // MARK: Settings bytes

    @Test func settingsEditsChangeOnlyTheAllowList() throws {
        for edit in Self.fixture.settings.edits {
            let added = try AntigravitySettingsText.adding(edit.add, to: Data(edit.before.utf8), path: "s.json")
            #expect(String(decoding: added.data, as: UTF8.self) == edit.afterAdd, "\(edit.name) add")
            #expect(added.added == edit.added, "\(edit.name) added")
            #expect(added.created.map(\.rawValue) == edit.created, "\(edit.name) created")
            let removed = try AntigravitySettingsText.removing(edit.remove, from: added.data, path: "s.json")
            #expect(String(decoding: removed.data, as: UTF8.self) == edit.afterRemove, "\(edit.name) remove")
            let pruned = try AntigravitySettingsText.pruning(Set(added.created), from: removed.data, path: "s.json")
            #expect(String(decoding: pruned.data, as: UTF8.self) == edit.afterPrune, "\(edit.name) prune")
            // Whatever else the file held reads the same.
            let before = try JSONSerialization.jsonObject(with: Data(edit.before.utf8)) as? NSDictionary
            var after = try JSONSerialization.jsonObject(with: added.data) as? [String: Any]
            var permissions = after?["permissions"] as? [String: Any]
            permissions?.removeValue(forKey: "allow")
            after?["permissions"] = permissions
            var original = before as? [String: Any]
            var originalPermissions = original?["permissions"] as? [String: Any]
            originalPermissions?.removeValue(forKey: "allow")
            original?["permissions"] = originalPermissions
            if original?["permissions"] == nil, (permissions ?? [:]).isEmpty { after?.removeValue(forKey: "permissions") }
            #expect(NSDictionary(dictionary: after ?? [:]).isEqual(to: original ?? [:]), "\(edit.name) other keys")
        }
        #expect(String(decoding: AntigravitySettingsText.newFile(Self.fixture.settings.newFile.add), as: UTF8.self) == Self.fixture.settings.newFile.after)
        // A file the app made holds nothing once its rules and their section go.
        let made = Data(Self.fixture.settings.newFile.after.utf8)
        let emptied = try AntigravitySettingsText.removing(Self.fixture.settings.newFile.add, from: made, path: "s.json").data
        #expect(try AntigravitySettingsText.pruning([.file], from: emptied, path: "s.json").empty)
        #expect(Self.fixture.settings.newFile.created == ["file"])
    }

    @Test func settingsThatAreNotTheExpectedShapeAreRefused() {
        for refused in Self.fixture.settings.refused {
            #expect(throws: (any Error).self) { try AntigravitySettingsText.adding(["command(ls)"], to: Data(refused.before.utf8), path: "s.json") }
            do { _ = try AntigravitySettingsText.adding(["command(ls)"], to: Data(refused.before.utf8), path: "s.json") }
            catch { #expect(Self.errorName(error) == refused.error, "\(refused.name)") }
            do { _ = try AntigravitySettingsText.removing(["command(ls)"], from: Data(refused.before.utf8), path: "s.json") }
            catch { #expect(Self.errorName(error) == refused.error, "\(refused.name) remove") }
        }
    }

    // MARK: The rules the app holds

    private struct Folder {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("agy-rules-\(UUID().uuidString)")
        var settings: URL { root.appendingPathComponent(".gemini/antigravity-cli/settings.json") }
        var ledger: URL { root.appendingPathComponent("app/antigravity-temporary-rules.json") }
        func make(_ text: String?) throws {
            try FileManager.default.createDirectory(at: settings.deletingLastPathComponent(), withIntermediateDirectories: true)
            if let text { try Data(text.utf8).write(to: settings) }
        }
        func text() -> String? { (try? Data(contentsOf: settings)).map { String(decoding: $0, as: UTF8.self) } }
        func leases(retryDelays: [Duration] = [.milliseconds(10), .milliseconds(20)], recheckDelay: Duration = .zero) -> AntigravityRuleLeases {
            AntigravityRuleLeases(settingsURL: settings, ledgerURL: ledger, retryDelays: retryDelays, recheckDelay: recheckDelay)
        }
        func cleanup() { try? FileManager.default.removeItem(at: root) }
    }
    static let real = fixture.settings.edits[0].before

    @Test func aTemporaryRuleIsAddedRecordedAndTakenOutLeavingEveryOtherByte() async throws {
        let folder = Folder(); defer { folder.cleanup() }
        try folder.make(Self.real)
        let leases = folder.leases()
        let lease = try #require(try await leases.apply(AntigravityAllowGrant(requestId: "agy-1", rules: ["command(echo hi)"], temporary: true)))
        #expect(folder.text()?.contains("\"command(echo hi)\"") == true)
        #expect((try? String(contentsOf: folder.ledger, encoding: .utf8))?.contains("command(echo hi)") == true)
        #expect(await leases.held() == ["command(echo hi)": 1])
        await leases.release(lease)
        #expect(folder.text() == Self.real)
        // The ledger lets go once a second look finds the rule gone.
        await leases.settled()
        #expect(!FileManager.default.fileExists(atPath: folder.ledger.path))
        #expect(await leases.held().isEmpty)
        // Releasing twice changes nothing.
        await leases.release(lease)
        #expect(folder.text() == Self.real)
    }

    @Test func aRuleTwoPanesHoldStaysUntilTheLastIsDone() async throws {
        let folder = Folder(); defer { folder.cleanup() }
        try folder.make(Self.real)
        let leases = folder.leases()
        let grant = AntigravityAllowGrant(requestId: "agy-1", rules: ["command(echo hi)", "command(echo hi)"], temporary: true)
        let first = try #require(try await leases.apply(grant))
        let second = try #require(try await leases.apply(AntigravityAllowGrant(requestId: "agy-2", rules: ["command(echo hi)"], temporary: true)))
        #expect(await leases.held() == ["command(echo hi)": 2])
        #expect(folder.text()?.components(separatedBy: "command(echo hi)").count == 2)
        await leases.release(first)
        #expect(folder.text()?.contains("command(echo hi)") == true)
        await leases.release(second)
        #expect(folder.text() == Self.real)
    }

    @Test func aRuleTheSettingsAlreadyHadIsNeverRemoved() async throws {
        let folder = Folder(); defer { folder.cleanup() }
        try folder.make(Self.real)
        let leases = folder.leases()
        let lease = try #require(try await leases.apply(AntigravityAllowGrant(requestId: "agy-1", rules: ["command(ls)"], temporary: true)))
        #expect(lease.rules.isEmpty && folder.text() == Self.real)
        await leases.release(lease)
        #expect(folder.text() == Self.real)
        // Always: kept, and a temporary hold on the same rule no longer removes it.
        let held = try #require(try await leases.apply(AntigravityAllowGrant(requestId: "agy-2", rules: ["command(echo hi)"], temporary: true)))
        #expect(try await leases.apply(AntigravityAllowGrant(requestId: "agy-3", rules: ["command(echo hi)", "command(pwd)"], temporary: false)) == nil)
        await leases.release(held)
        await leases.settled()
        #expect(folder.text()?.contains("\"command(echo hi)\"") == true && folder.text()?.contains("\"command(pwd)\"") == true)
        #expect(await leases.held().isEmpty && !FileManager.default.fileExists(atPath: folder.ledger.path))
        // A rule that may be allowed only once is never kept for good.
        do { _ = try await leases.apply(AntigravityAllowGrant(requestId: "agy-4", rules: ["command(rm -rf build)"], temporary: false)); Issue.record("kept a once-only rule") }
        catch { #expect(Self.errorName(error) == "onceOnly") }
        do { _ = try await leases.apply(AntigravityAllowGrant(requestId: "agy-4", rules: ["command(*)"], temporary: true)); Issue.record("added command(*)") }
        catch { #expect(Self.errorName(error) == "badRule") }
    }

    @Test func settingsThatCannotBeReadAreLeftAloneAndRefused() async throws {
        let folder = Folder(); defer { folder.cleanup() }
        let grant = AntigravityAllowGrant(requestId: "agy-1", rules: ["command(echo hi)"], temporary: true)
        // No folder: agy was never set up here.
        do { _ = try await folder.leases().apply(grant); Issue.record("applied without a folder") }
        catch { #expect(Self.errorName(error) == "missingFolder") }
        for (text, expected) in [("{ broken", "notJSON"), ("{\"permissions\": {\"allow\": 3}}", "allowNotArray")] {
            try folder.make(text)
            let leases = folder.leases()
            do { _ = try await leases.apply(grant); Issue.record("applied to \(text)") }
            catch { #expect(Self.errorName(error) == expected) }
            do { try await leases.check(); Issue.record("checked \(text)") }
            catch { #expect(Self.errorName(error) == expected) }
            #expect(folder.text() == text)
            #expect(!FileManager.default.fileExists(atPath: folder.ledger.path))
        }
        // A rule that is not one is refused before the file is read.
        do { _ = try await folder.leases().apply(AntigravityAllowGrant(requestId: "agy-1", rules: ["rm -rf /"], temporary: true)); Issue.record("bad rule") }
        catch { #expect(Self.errorName(error) == "badRule") }
    }

    @Test func noFileRuleReachesAgysOwnFolderOrTheAppsData() async throws {
        let folder = Folder(); defer { folder.cleanup() }
        try folder.make(Self.real)
        let leases = folder.leases()
        let gemini = folder.root.appendingPathComponent(".gemini")
        // agy's own settings, the app's ledger, and anything above them.
        for path in [folder.settings.path, gemini.path, folder.ledger.path, folder.ledger.deletingLastPathComponent().path, folder.root.path,
                     folder.root.appendingPathComponent("project/../.gemini/x").path] {
            for name in ["write_file", "read_file"] {
                do { _ = try await leases.apply(AntigravityAllowGrant(requestId: "agy-1", rules: ["\(name)(\(path))"], temporary: true)); Issue.record("allowed \(name)(\(path))") }
                catch { #expect(Self.errorName(error) == "badRule", "\(name)(\(path))") }
            }
        }
        // Through a link to it too.
        let link = folder.root.appendingPathComponent("linked")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: gemini)
        do { _ = try await leases.apply(AntigravityAllowGrant(requestId: "agy-1", rules: ["write_file(\(link.path)/antigravity-cli/settings.json)"], temporary: true)); Issue.record("allowed through a link") }
        catch { #expect(Self.errorName(error) == "badRule") }
        #expect(folder.text() == Self.real)
        // A file beside them is fine.
        let lease = try #require(try await leases.apply(AntigravityAllowGrant(requestId: "agy-2", rules: ["write_file(\(folder.root.path)/project/a.txt)"], temporary: true)))
        await leases.release(lease); await leases.settled()
        // The real home's .gemini and the app's own data folder are never a rule's, wherever the leases point.
        let home = FileManager.default.homeDirectoryForCurrentUser
        #expect(AntigravityRuleSafety.verdict("write_file(\(home.path)/.gemini/antigravity-cli/settings.json)") == .refused)
        #expect(AntigravityRuleSafety.verdict("read_file(\(home.path))") == .refused)
        AntigravityRuleSafety.appDataFolder = folder.root.appendingPathComponent("app").path
        defer { AntigravityRuleSafety.appDataFolder = nil }
        #expect(AntigravityRuleSafety.verdict("write_file(\(folder.ledger.path))") == .refused)
        #expect(AntigravityRuleSafety.verdict("command(cat \(home.path)/.gemini/antigravity-cli/settings.json)") == .once)
    }

    @Test func aMissingFileIsCreatedWithTheRuleAloneAndGoesWithIt() async throws {
        let folder = Folder(); defer { folder.cleanup() }
        try folder.make(nil)
        let leases = folder.leases()
        let lease = try #require(try await leases.apply(AntigravityAllowGrant(requestId: "agy-1", rules: ["command(echo hi)"], temporary: true)))
        #expect(folder.text() == Self.fixture.settings.newFile.after)
        await leases.release(lease)
        await leases.settled()
        // The file the app made ends empty, so it goes; nothing else was left in the folder.
        #expect(folder.text() == nil)
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.settings.deletingLastPathComponent().path).isEmpty)
        #expect(!FileManager.default.fileExists(atPath: folder.ledger.path))
        // A section the app added to an existing file goes too, byte for byte.
        let noSection = "{\r\n  \"model\": \"x\"\r\n}\r\n"
        try folder.make(noSection)
        let again = try #require(try await leases.apply(AntigravityAllowGrant(requestId: "agy-2", rules: ["command(echo hi)"], temporary: true)))
        #expect(folder.text()?.contains("\r\n    \"allow\": [\r\n") == true)
        await leases.release(again); await leases.settled()
        #expect(folder.text() == noSection)
    }

    @Test func aFailedRemovalIsTriedAgainAndKeptInTheLedgerUntilItWorks() async throws {
        let folder = Folder(); defer { folder.cleanup() }
        try folder.make(Self.real)
        let directory = folder.settings.deletingLastPathComponent().path
        let leases = folder.leases(retryDelays: [.milliseconds(20), .milliseconds(20)])
        let first = try #require(try await leases.apply(AntigravityAllowGrant(requestId: "agy-1", rules: ["command(echo hi)"], temporary: true)))
        // The folder cannot be written: every try fails, and the ledger keeps the rule.
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: directory)
        await leases.release(first)
        await leases.settled()
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: directory)
        #expect(folder.text()?.contains("command(echo hi)") == true)
        #expect((try? String(contentsOf: folder.ledger, encoding: .utf8))?.contains("command(echo hi)") == true)
        // The next release tries it again.
        let other = try #require(try await leases.apply(AntigravityAllowGrant(requestId: "agy-2", rules: ["command(pwd)"], temporary: true)))
        await leases.release(other); await leases.settled()
        #expect(folder.text() == Self.real && !FileManager.default.fileExists(atPath: folder.ledger.path))
        // A pause between tries: the folder comes back during it, and the removal goes through.
        let slow = folder.leases(retryDelays: [.milliseconds(400), .milliseconds(400)])
        let third = try #require(try await slow.apply(AntigravityAllowGrant(requestId: "agy-3", rules: ["command(echo hi)"], temporary: true)))
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: directory)
        let reopen = Task { try? await Task.sleep(for: .milliseconds(150)); try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: directory) }
        await slow.release(third)
        _ = await reopen.value
        await slow.settled()
        #expect(folder.text() == Self.real && !FileManager.default.fileExists(atPath: folder.ledger.path))
    }

    @Test func aRuleAgyWritesBackIsTakenOutAgain() async throws {
        let folder = Folder(); defer { folder.cleanup() }
        try folder.make(Self.real)
        let leases = folder.leases(recheckDelay: .milliseconds(300))
        let lease = try #require(try await leases.apply(AntigravityAllowGrant(requestId: "agy-1", rules: ["command(echo hi)"], temporary: true)))
        let withRule = try #require(folder.text())
        await leases.release(lease)
        #expect(folder.text() == Self.real)
        // A running agy saves the settings it read earlier, the rule with them.
        try Data(withRule.utf8).write(to: folder.settings)
        #expect(FileManager.default.fileExists(atPath: folder.ledger.path))
        await leases.settled()
        #expect(folder.text() == Self.real && !FileManager.default.fileExists(atPath: folder.ledger.path))
    }

    @Test func afterQuitNothingIsAddedAndALinkToNothingIsLeftAlone() async throws {
        let folder = Folder(); defer { folder.cleanup() }
        try folder.make(Self.real)
        let leases = folder.leases()
        await leases.releaseAll()
        do { _ = try await leases.apply(AntigravityAllowGrant(requestId: "agy-1", rules: ["command(echo hi)"], temporary: true)); Issue.record("applied after quit") }
        catch { #expect(error is CancellationError) }
        #expect(folder.text() == Self.real)
        // A dangling link is refused, not replaced by a new file.
        try FileManager.default.removeItem(at: folder.settings)
        try FileManager.default.createSymbolicLink(atPath: folder.settings.path, withDestinationPath: folder.root.appendingPathComponent("gone/settings.json").path)
        do { _ = try await folder.leases().apply(AntigravityAllowGrant(requestId: "agy-1", rules: ["command(echo hi)"], temporary: true)); Issue.record("replaced a dangling link") }
        catch { #expect(Self.errorName(error) == "unreadable") }
        #expect((try? FileManager.default.destinationOfSymbolicLink(atPath: folder.settings.path)) != nil)
    }

    @Test func whatACrashLeftIsRemovedAtTheNextLaunch() async throws {
        let folder = Folder(); defer { folder.cleanup() }
        try folder.make(Self.real)
        do {
            let crashed = folder.leases()
            _ = try await crashed.apply(AntigravityAllowGrant(requestId: "agy-1", rules: ["command(echo hi)", "command(ls)"], temporary: true))
            // The app ends here without releasing.
        }
        #expect(folder.text()?.contains("command(echo hi)") == true)
        let next = folder.leases()
        await next.removeLeftovers()
        await next.settled()
        #expect(folder.text() == Self.real)
        #expect(!FileManager.default.fileExists(atPath: folder.ledger.path))
        // A ledger whose settings cannot be read is kept for a later launch.
        let again = folder.leases()
        _ = try await again.apply(AntigravityAllowGrant(requestId: "agy-2", rules: ["command(echo hi)"], temporary: true))
        let saved = try #require(folder.text())
        try Data("{ broken".utf8).write(to: folder.settings)
        await folder.leases().removeLeftovers()
        #expect(FileManager.default.fileExists(atPath: folder.ledger.path))
        try Data(saved.utf8).write(to: folder.settings)
        let last = folder.leases()
        await last.removeLeftovers(); await last.settled()
        #expect(folder.text() == Self.real && !FileManager.default.fileExists(atPath: folder.ledger.path))
    }

    @Test func quitReleasesEverythingAndALinkedFileStaysLinked() async throws {
        let folder = Folder(); defer { folder.cleanup() }
        try folder.make(nil)
        let target = folder.root.appendingPathComponent("dotfiles/agy-settings.json")
        try FileManager.default.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(Self.real.utf8).write(to: target)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: target.path)
        try FileManager.default.createSymbolicLink(at: folder.settings, withDestinationURL: target)
        let leases = folder.leases()
        _ = try await leases.apply(AntigravityAllowGrant(requestId: "agy-1", rules: ["command(echo hi)"], temporary: true))
        _ = try await leases.apply(AntigravityAllowGrant(requestId: "agy-2", rules: ["command(pwd)"], temporary: true))
        #expect((try? FileManager.default.destinationOfSymbolicLink(atPath: folder.settings.path)) != nil)
        #expect(String(decoding: try Data(contentsOf: target), as: UTF8.self).contains("command(pwd)"))
        #expect((try FileManager.default.attributesOfItem(atPath: target.path)[.posixPermissions] as? NSNumber)?.intValue == 0o600)
        await leases.releaseAll()
        #expect(String(decoding: try Data(contentsOf: target), as: UTF8.self) == Self.real)
        #expect(await leases.held().isEmpty)
    }

    // MARK: The card

    static let card = AntigravityPermissionRequest(id: "agy-card", runId: "run-1", actions: [
        AntigravityDeniedAction(kind: .command, action: "command", displayName: "RunCommand", target: "curl -s https://example.com", rule: "command(curl -s https://example.com)"),
        AntigravityDeniedAction(kind: .other, action: "notebook_execution", displayName: "NotebookExecution"),
    ], receivedAt: "2026-10-22T00:00:00Z")

    @Test func aCardStaysUntilALaterRunEndsOrItIsAnswered() {
        var board = AntigravityPermissionBoard()
        board.received(Self.card, sessionId: "pane")
        // The run that refused it ends: the card stays.
        board.runEnded(sessionId: "pane")
        #expect(board["pane"] == Self.card)
        #expect(board.pending(sessionId: "pane", requestId: "agy-card", runId: "run-1") == Self.card)
        #expect(board.pending(sessionId: "pane", requestId: "agy-card", runId: "run-2") == nil)
        // A later run of the pane ends without one: it goes.
        board.runEnded(sessionId: "pane")
        #expect(board["pane"] == nil)
        // A later run's own card replaces it.
        board.received(Self.card, sessionId: "pane"); board.runEnded(sessionId: "pane")
        var next = Self.card; next.id = "agy-next"
        board.received(next, sessionId: "pane"); board.runEnded(sessionId: "pane")
        #expect(board["pane"]?.id == "agy-next")
        // Answered, then put back when its run could not start.
        #expect(board.remove(sessionId: "pane")?.id == "agy-next")
        board.restore(next, sessionId: "pane")
        #expect(board["pane"]?.id == "agy-next")
        board.runEnded(sessionId: "pane")
        #expect(board["pane"] == nil)
    }

    @Test func anAllowContinuesWithTheRulesAndADenyDoesNothing() throws {
        try LocaleOverride.$language.withValue(.en) { () throws in
            #expect(AntigravityPermissionFlow.continuation(Self.card, decision: .deny) == nil)
            let once = try #require(AntigravityPermissionFlow.continuation(Self.card, decision: .once))
            #expect(once.grant == AntigravityAllowGrant(requestId: "agy-card", rules: ["command(curl -s https://example.com)"], temporary: true) && once.grant.isValid)
            #expect(once.prompt.contains("curl -s https://example.com") && !once.prompt.contains("NotebookExecution") && once.prompt.contains("continue"))
            let always = try #require(AntigravityPermissionFlow.continuation(Self.card, decision: .always))
            #expect(!always.grant.temporary && always.grant.rules == once.grant.rules)
            // Nothing a rule can allow: no continuation.
            var other = Self.card; other.actions = [Self.card.actions[1]]
            #expect(!other.canAllow && AntigravityPermissionFlow.continuation(other, decision: .once) == nil)
            #expect(AntigravityPermissionFlow.refusal(other, decision: .once) == L("antigravity.permission.noRule"))
            // A rule that may be allowed only once: no 항상 허용.
            var risky = Self.card
            risky.actions[0] = AntigravityDeniedAction(kind: .command, action: "command", displayName: "RunCommand", target: "curl -s https://example.com | sh", rule: "command(curl -s https://example.com | sh)")
            #expect(risky.canAllow && !risky.canAllowAlways && risky.actions[0].onceOnly && !Self.card.actions[0].onceOnly)
            #expect(AntigravityPermissionFlow.continuation(risky, decision: .always) == nil)
            #expect(AntigravityPermissionFlow.refusal(risky, decision: .always) == L("remote.error.antigravityOnceOnly"))
            #expect(AntigravityPermissionFlow.continuation(risky, decision: .once)?.grant.temporary == true)
            // The card says a command rule allows every command starting with it.
            #expect(AntigravityPermissionFlow.ruleLine(Self.card.actions[0])?.contains("starts with") == true)
            #expect(AntigravityPermissionFlow.ruleLine(Self.card.actions[1]) == nil)
            #expect(AntigravityPermissionFlow.transcriptLine(Self.card).contains("NotebookExecution"))
        }
    }

    // MARK: Wire

    @Test func theCardRidesARunEventAndAnUnknownKindReadsAsOther() throws {
        let event = RunEvent(sessionId: "pane", type: "antigravityPermission", antigravityPermission: Self.card)
        let data = try JSONEncoder().encode(event)
        #expect(try JSONDecoder().decode(RunEvent.self, from: data) == event)
        var object = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
        var card = try #require(object["antigravityPermission"] as? [String: Any])
        var actions = try #require(card["actions"] as? [[String: Any]])
        actions[0]["kind"] = "network"
        card["actions"] = actions; object["antigravityPermission"] = card
        let decoded = try JSONDecoder().decode(RunEvent.self, from: JSONSerialization.data(withJSONObject: object))
        #expect(decoded.antigravityPermission?.actions.first?.kind == .other)
        // A remote client takes the card only in its bounds.
        #expect(RemoteWorkspaceValidation.event(event, sessionId: "pane"))
        var bad = Self.card; bad.actions[0].rule = "rm -rf /"
        #expect(!RemoteWorkspaceValidation.event(RunEvent(sessionId: "pane", type: "antigravityPermission", antigravityPermission: bad), sessionId: "pane"))
        bad = Self.card; bad.actions[0].rule = "command(*)"
        #expect(!RemoteWorkspaceValidation.event(RunEvent(sessionId: "pane", type: "antigravityPermission", antigravityPermission: bad), sessionId: "pane"))
        bad = Self.card; bad.actions = []
        #expect(!RemoteWorkspaceValidation.event(RunEvent(sessionId: "pane", type: "antigravityPermission", antigravityPermission: bad), sessionId: "pane"))
        // A run start carries the grant beside the request; the request itself does not.
        var request = StartRunRequest(sessionId: "pane", workspaceId: "ws", input: "go on", provider: "gemini")
        request.antigravityGrant = AntigravityAllowGrant(requestId: "agy-card", rules: ["command(ls)"], temporary: true)
        #expect(!String(decoding: try JSONEncoder().encode(request), as: UTF8.self).contains("antigravity"))
    }

    @Test func thePhoneSeesTheCardAsAPermissionAnOlderPhoneCanAnswer() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let permission = MobilePermission(antigravity: Self.card)
            let object = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(permission)) as? [String: Any])
            #expect(object["id"] as? String == "agy-card" && object["runId"] as? String == "run-1" && object["toolName"] as? String == "AntigravityPermission")
            #expect(object["canAllow"] as? Bool == true && object["title"] as? String == "Antigravity가 이 작업을 허용해 달라고 합니다")
            let fields = try #require(object["fields"] as? [[String: String]])
            #expect(fields == [["label": "명령", "value": "curl -s https://example.com"], ["label": "작업", "value": "NotebookExecution"]])
            let actions = try #require((object["antigravity"] as? [String: Any])?["actions"] as? [[String: Any]])
            #expect(actions.count == 2 && actions[0]["kind"] as? String == "command" && actions[0]["rule"] as? String == "command(curl -s https://example.com)")
            #expect(actions[1]["rule"] == nil && actions[1]["target"] == nil && actions[1]["displayName"] as? String == "NotebookExecution")
            #expect(try JSONDecoder().decode(MobilePermission.self, from: JSONEncoder().encode(permission)) == permission)
            // A Claude permission carries no card.
            #expect(MobilePermission(request: ToolPermissionRequest(id: "p", runId: "r", toolUseId: "t", toolName: "Bash", inputJSON: "{}", summary: "ls")).antigravity == nil)
            // The answers.
            let plain = try JSONDecoder().decode(MobilePermissionAnswer.self, from: Data(#"{"requestId":"agy-card","runId":"run-1","allow":true}"#.utf8))
            #expect(plain.always == nil && plain.antigravityDecision == .once)
            let always = try JSONDecoder().decode(MobilePermissionAnswer.self, from: Data(#"{"requestId":"agy-card","runId":"run-1","allow":true,"always":true}"#.utf8))
            #expect(always.antigravityDecision == .always)
            #expect(MobilePermissionAnswer(requestId: "a", runId: "b", allow: false).antigravityDecision == .deny)
        }
    }
}

private final class AgyEventLog: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [RunEvent] = []
    func append(_ event: RunEvent) { lock.lock(); items.append(event); lock.unlock() }
    var values: [RunEvent] { lock.lock(); defer { lock.unlock() }; return items }
    func ended(_ pane: String) -> [RunEvent] { values.filter { $0.sessionId == pane && $0.type == "status" && ["completed", "error", "stopped"].contains($0.status ?? "") } }
}

private func agyWait(_ seconds: Double = 30, _ condition: () async -> Bool) async throws {
    let deadline = Date().addingTimeInterval(seconds)
    while !(await condition()) {
        guard Date() < deadline else { throw MightyError("Timed out waiting for the run") }
        try await Task.sleep(for: .milliseconds(25))
    }
}

/// The card through the real runner, and a remote pane whose host puts the
/// rules in its own settings, with an agy stand-in that is refused a command
/// on the first turn and, continued, says whether its settings allow it.
@Suite(.serialized) struct AntigravityPermissionRunTests {
    private struct Folder {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("agy-run-\(UUID().uuidString)")
        var project: URL { root.appendingPathComponent("project") }
        var settings: URL { root.appendingPathComponent("home/.gemini/antigravity-cli/settings.json") }
        func make() throws -> URL {
            try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)
            try FileManager.default.createDirectory(at: settings.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data(AntigravityPermissionTests.real.utf8).write(to: settings)
            let binary = root.appendingPathComponent("agy")
            let denied = #"{"event":"step_update","step_update":{"step_index":2,"state":"ERROR","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"echo hi"},"error":{"type":"TOOL_ERROR","message":"permission check failed for unsandboxed \"echo hi\": user denied permission to run command:\necho hi\nDo not attempt to circumvent this denial."}}}}"#
            let deniedChain = #"{"event":"step_update","step_update":{"step_index":2,"state":"ERROR","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"echo hi && date"},"error":{"type":"TOOL_ERROR","message":"permission check failed for unsandboxed \"echo hi && date\": user denied permission to run command:\necho hi && date\nDo not attempt to circumvent this denial."}}}}"#
            let source = """
            #!/bin/sh
            if [ "$1" = "--version" ]; then printf '1.4.4\\n'; exit 0; fi
            input="$(/bin/cat)"
            printf '%s\\n' '{"event":"init","conversation_id":"conv-1"}'
            case " $* " in
              *" --conversation "*)
                if /usr/bin/grep -q 'command(echo hi)' '\(settings.path)'; then answer=allowed; else answer=missing; fi
                printf '{"event":"result","result":{"conversation_id":"conv-1","status":"SUCCESS","response":"%s"}}\\n' "$answer" ;;
              *)
                case "$input $*" in
                  *risky*) printf '%s\\n' '\(deniedChain)' ;;
                  *) printf '%s\\n' '\(denied)' ;;
                esac
                printf '%s\\n' '{"event":"result","result":{"conversation_id":"conv-1","status":"SUCCESS","response":"","denied_actions":[{"action":"command","display_name":"RunCommand"}]}}' ;;
            esac
            """
            try Data(source.utf8).write(to: binary)
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
            return binary
        }
        func text() -> String { (try? String(contentsOf: settings, encoding: .utf8)) ?? "" }
        func cleanup() { try? FileManager.default.removeItem(at: root) }
    }

    @Test func theRunnerSendsTheCardBeforeTheRunsEnd() async throws {
        let folder = Folder(); defer { folder.cleanup() }
        let binary = try folder.make()
        let service = ProviderService(binaryOverrides: ["gemini": binary])
        let events = AgyEventLog()
        let runner = ProcessRunner(providerService: service, pluginDirectory: folder.root, onEvent: { events.append($0) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: folder.project.path)
        do {
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "say hi", provider: "gemini"), workspace: workspace)
            try await agyWait { !events.ended("pane").isEmpty }
            let values = events.values
            let cardIndex = try #require(values.firstIndex { $0.type == "antigravityPermission" })
            let endIndex = try #require(values.firstIndex { $0.type == "status" && $0.status == "completed" })
            #expect(cardIndex < endIndex)
            let card = try #require(values[cardIndex].antigravityPermission)
            #expect(card.rules == ["command(echo hi)"] && card.actions.first?.displayName == "RunCommand")
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }

    @Test func aRemotePanesHostPutsTheRulesInItsOwnSettingsForTheContinuation() async throws {
        let folder = Folder(); defer { folder.cleanup() }
        let binary = try folder.make()
        let repository = StateRepository(directory: folder.root.appendingPathComponent("host"), legacyStateURL: nil)
        let workspace = try await repository.approveWorkspace(Workspace(name: "Project", path: folder.project.path))
        try await repository.save(AppSnapshot(workspaces: [workspace]))
        let providers = ProviderService(binaryOverrides: ["gemini": binary])
        let rules = AntigravityRuleLeases(settingsURL: folder.settings, ledgerURL: folder.root.appendingPathComponent("host/antigravity-temporary-rules.json"))
        let host = RemoteHostService(repository: repository, providers: providers, pluginDirectory: folder.root, appVersion: "9.9.9", hostName: "Test host",
                                     allowLoopbackForTests: true, antigravityRules: rules)
        let events = AgyEventLog()
        let client = RemoteClientService(repository: StateRepository(directory: folder.root.appendingPathComponent("client"), legacyStateURL: nil),
                                         dataDirectory: folder.root.appendingPathComponent("client"), allowLoopbackForTests: true) { events.append($0) }
        func cleanup() async { await client.shutdown(); _ = await host.stop(); await providers.shutdown() }
        do {
            let state = try await host.start(workspaceIds: [workspace.id], port: 0)
            let info = try await client.connect(name: "", address: "127.0.0.1:\(try #require(state.port))", key: try #require(state.key))
            let opened = try await client.openWorkspace(connectionId: info.id, hostWorkspaceId: workspace.id)
            try await client.start(request: StartRunRequest(sessionId: "pane", workspaceId: opened.id, input: "say hi", provider: "gemini"), workspace: opened, phaseModels: nil)
            try await agyWait { events.ended("pane").count == 1 }
            let card = try #require(events.values.first { $0.type == "antigravityPermission" }?.antigravityPermission)
            #expect(card.rules == ["command(echo hi)"])
            // A rule the host never reported is refused, and nothing runs.
            var forged = StartRunRequest(sessionId: "pane", workspaceId: opened.id, input: "go on", provider: "gemini", resumeId: "conv-1")
            forged.antigravityGrant = AntigravityAllowGrant(requestId: card.id, rules: ["command(rm -rf /)"], temporary: true)
            do { try await client.start(request: forged, workspace: opened, phaseModels: nil); Issue.record("a forged rule was accepted") }
            catch { #expect((error as? RemoteHTTPFailure)?.status == 403) }
            #expect(folder.text() == AntigravityPermissionTests.real)
            // The card's own rule: in place while the continuation runs, gone after.
            let continuation = try #require(AntigravityPermissionFlow.continuation(card, decision: .once))
            var request = StartRunRequest(sessionId: "pane", workspaceId: opened.id, input: continuation.prompt, provider: "gemini", resumeId: "conv-1")
            request.antigravityGrant = continuation.grant
            // 항상 허용 from another computer needs the host's full-access switch.
            var always = request
            always.antigravityGrant = AntigravityAllowGrant(requestId: card.id, rules: card.rules, temporary: false)
            do { try await client.start(request: always, workspace: opened, phaseModels: nil); Issue.record("always without full access") }
            catch { #expect((error as? RemoteHTTPFailure)?.status == 403) }
            #expect(folder.text() == AntigravityPermissionTests.real)
            try await client.start(request: request, workspace: opened, phaseModels: nil)
            try await agyWait { events.ended("pane").count == 2 }
            #expect(events.values.contains { $0.entry?.kind == "assistant" && $0.entry?.text == "allowed" })
            try await agyWait { folder.text() == AntigravityPermissionTests.real }
            #expect(await rules.held().isEmpty)
            // A card's rules are granted once: the same grant again is refused.
            do { try await client.start(request: request, workspace: opened, phaseModels: nil); Issue.record("a used card was granted again") }
            catch { #expect((error as? RemoteHTTPFailure)?.status == 403) }
            // With full access on, always is taken for a rule that may be kept,
            _ = await host.setAllowsFullAccess(true)
            try await client.start(request: StartRunRequest(sessionId: "pane", workspaceId: opened.id, input: "risky", provider: "gemini"), workspace: opened, phaseModels: nil)
            try await agyWait { events.ended("pane").count == 3 }
            let chained = try #require(events.values.last { $0.type == "antigravityPermission" }?.antigravityPermission)
            #expect(chained.rules == ["command(echo hi && date)"] && !chained.canAllowAlways)
            // and still refused for one that may be allowed only once.
            var risky = request
            risky.antigravityGrant = AntigravityAllowGrant(requestId: chained.id, rules: chained.rules, temporary: false)
            do { try await client.start(request: risky, workspace: opened, phaseModels: nil); Issue.record("kept a once-only rule") }
            catch { #expect((error as? RemoteHTTPFailure)?.status == 409) }
            #expect(folder.text() == AntigravityPermissionTests.real)
            try await client.start(request: StartRunRequest(sessionId: "pane", workspaceId: opened.id, input: "say hi", provider: "gemini"), workspace: opened, phaseModels: nil)
            try await agyWait { events.ended("pane").count == 4 }
            let plain = try #require(events.values.last { $0.type == "antigravityPermission" }?.antigravityPermission)
            var kept = request
            kept.antigravityGrant = AntigravityAllowGrant(requestId: plain.id, rules: plain.rules, temporary: false)
            try await client.start(request: kept, workspace: opened, phaseModels: nil)
            try await agyWait { events.ended("pane").count == 5 }
            #expect(folder.text().contains("\"command(echo hi)\""))
            await cleanup()
        } catch { await cleanup(); throw error }
    }
}

/// The wire samples in the shared fixture: the Mac's own encoding, which the
/// Windows host and the phone read.
struct AntigravityPermissionWireTests {
    static let fixture: [String: Any] = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        let data = try! Data(contentsOf: url.appendingPathComponent("native/contracts/fixtures/antigravity-permissions.json"))
        return try! JSONSerialization.jsonObject(with: data) as! [String: Any]
    }()
    static func plain(_ value: Any) -> Any {
        if let object = value as? [String: Any] { return object.filter { !($0.value is NSNull) }.mapValues(plain) }
        if let array = value as? [Any] { return array.map(plain) }
        return value
    }
    static func roundTrips<T: Codable>(_ value: Any, _ type: T.Type) throws -> T {
        let raw = try JSONSerialization.data(withJSONObject: value)
        let decoded = try JSONDecoder().decode(T.self, from: raw)
        let encoder = JSONEncoder(); encoder.outputFormatting = [.withoutEscapingSlashes]
        let again = try JSONSerialization.jsonObject(with: encoder.encode(decoded))
        #expect(NSDictionary(dictionary: ["v": plain(value)]).isEqual(to: ["v": plain(again)]), "\(T.self)")
        return decoded
    }

    @Test func theRemoteSamplesAreTheMacsOwnAndPassTheClientsChecks() throws {
        let remote = try #require(Self.fixture["remote"] as? [String: Any])
        let poll = try Self.roundTrips(try #require(remote["pollEvent"]), RemoteWirePoll.self)
        #expect(poll.events.allSatisfy { RemoteWorkspaceValidation.event($0.event, sessionId: "job-1") })
        let card = try #require(poll.events.first?.event.antigravityPermission)
        #expect(card.rules == ["command(curl -s https://example.com | grep -i '<title>')"] && card.actions[1].rule == nil)
        let start = try Self.roundTrips(try #require(remote["start"]), RemoteWireStart.self)
        #expect(start.antigravityGrant == AntigravityAllowGrant(requestId: card.id, rules: card.rules, temporary: true) && start.antigravityGrant?.isValid == true)
        #expect(start.request.provider == "gemini" && start.request.resumeId != nil)
    }

    @Test func thePhoneSampleIsWhatTheMacSends() throws {
        try LocaleOverride.$language.withValue(.en) { () throws in
            let mobile = try #require(Self.fixture["mobile"] as? [String: Any])
            let sample = try Self.roundTrips(try #require(mobile["permission"]), MobilePermission.self)
            let remote = try #require(Self.fixture["remote"] as? [String: Any])
            let poll = try JSONDecoder().decode(RemoteWirePoll.self, from: JSONSerialization.data(withJSONObject: try #require(remote["pollEvent"])))
            #expect(MobilePermission(antigravity: try #require(poll.events.first?.event.antigravityPermission)) == sample)
            #expect(sample.antigravity?.actions.first?.onceOnly == true && sample.antigravity?.actions.last?.onceOnly == nil)
            #expect(sample.antigravity?.actions.first?.onceOnlyReason == "risky" && sample.antigravity?.actions.last?.onceOnlyReason == nil)
            // Each reason has its own words on the card.
            let reasons = try #require(mobile["onceOnlyReasons"] as? [String: String])
            #expect(Set(reasons.keys) == Set(AntigravityRuleSafety.OnceReason.allCases.map(\.rawValue)))
            for reason in AntigravityRuleSafety.OnceReason.allCases {
                #expect(AntigravityPermissionFlow.onceOnlyText(reason) == L(try #require(reasons[reason.rawValue])) && !L(reasons[reason.rawValue]!).hasPrefix("antigravity."))
            }
            let interpreter = AntigravityDeniedAction(kind: .command, action: "command", displayName: "RunCommand", target: "bash", rule: "command(bash)")
            #expect(interpreter.onceOnlyReason == .interpreter && AntigravityPermissionFlow.onceOnlyText(interpreter) == L("antigravity.permission.onceOnlyInterpreter"))
            #expect(MobileAntigravityCard(AntigravityPermissionRequest(runId: "r", actions: [interpreter])).actions[0].onceOnlyReason == "interpreter")
            // The recorded command's card: 항상 허용 is offered, and nothing says once-only.
            let plainSample = try #require(mobile["permissionPlain"] as? [String: Any])
            let plain = try Self.roundTrips(try #require(plainSample["permission"]), MobilePermission.self)
            let stream = try #require(AntigravityPermissionTests.fixture.streams.first { $0.name == plainSample["stream"] as? String })
            var made = AntigravityPermissionRequest(id: plain.id, runId: plain.runId, actions: stream.actions, receivedAt: plain.receivedAt)
            #expect(MobilePermission(antigravity: made) == plain && made.canAllowAlways)
            made.actions = []
            #expect(!made.canAllow)
            for answer in try #require(mobile["answers"] as? [[String: Any]]) {
                let body = try JSONDecoder().decode(MobilePermissionAnswer.self, from: JSONSerialization.data(withJSONObject: try #require(answer["body"])))
                if let decision = answer["decision"] as? String { #expect(body.antigravityDecision.rawValue == decision) }
                else { #expect(body.always == true && !body.allow) }
            }
        }
    }
}
