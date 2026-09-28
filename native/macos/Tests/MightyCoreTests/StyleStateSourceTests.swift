import Foundation
import Testing
@testable import MightyCore

/// §1.16: the closed state-source engine against real files in a temporary
/// workspace — where it may read, what the two parsers see, and what the two
/// aggregates count from one pane's own events.
struct StyleStateSourceTests {
    private let fm = FileManager.default

    /// A workspace and a folder beside it that stands for "outside".
    private func workspace(_ label: String) -> (root: URL, outside: URL) {
        let parent = StyleFixtures.temporaryDirectory("state-source-" + label)
        let root = parent.appendingPathComponent("ws", isDirectory: true)
        let outside = parent.appendingPathComponent("outside", isDirectory: true)
        try? fm.createDirectory(at: root, withIntermediateDirectories: true)
        try? fm.createDirectory(at: outside, withIntermediateDirectories: true)
        return (root, outside)
    }

    private func write(_ text: String, _ path: String, in root: URL, modified: Date? = nil) throws {
        let url = root.appendingPathComponent(path)
        try StyleFixtures.write(Data(text.utf8), to: url)
        if let modified { try fm.setAttributes([.modificationDate: modified], ofItemAtPath: url.path) }
    }

    private func names(_ urls: [URL]) -> [String] { urls.map(\.lastPathComponent).sorted() }

    private func stateSources(_ json: String) -> Data {
        StyleFixtures.data(StyleFixtures.flat, extra: [("stateSources", json)])
    }

    // MARK: - Where a source may read

    @Test func decoderRefusesPathsThatLeaveTheWorkspace() {
        for path in ["/etc/passwd", "~/plan.md", "../plan.md", "docs/../../plan.md", "docs/plans/.."] {
            let data = stateSources("{\"files\":[{\"path\":\"\(path)\",\"parser\":\"markdownChecklist\",\"widget\":\"label\"}]}")
            #expect(StyleFixtures.code(data) == "E_STATE_PATH_ESCAPE", "\(path)")
        }
        let fine = stateSources("{\"files\":[{\"path\":\"docs/plans/**/*.md\",\"parser\":\"markdownChecklist\",\"widget\":\"label\"}]}")
        #expect(StyleFixtures.code(fine) == nil)
    }

    @Test func engineRefusesEscapingPatternsEvenWithoutTheDecoder() throws {
        let (root, outside) = workspace("escape-pattern")
        defer { try? fm.removeItem(at: root.deletingLastPathComponent()) }
        try write("- [ ] secret", "secret.md", in: outside)
        // Each of these would name the file outside if it were followed.
        #expect(StyleStateEngine.matches("../outside/*.md", in: root).isEmpty)
        #expect(StyleStateEngine.matches(outside.path + "/*.md", in: root).isEmpty)
        #expect(StyleStateEngine.matches("~/*.md", in: root).isEmpty)
        #expect(StyleStateEngine.matches("docs/../../outside/secret.md", in: root).isEmpty)
    }

    @Test func globMatchesInsideTheWorkspaceOnly() throws {
        let (root, _) = workspace("glob")
        defer { try? fm.removeItem(at: root.deletingLastPathComponent()) }
        try write("a", "docs/plans/2026-01-01-a.md", in: root)
        try write("b", "docs/plans/2026-01-02-b.md", in: root)
        try write("c", "docs/plans/notes.txt", in: root)
        try write("d", "docs/plans/.hidden.md", in: root)
        try write("e", "docs/plans/old/2025-12-31-e.md", in: root)
        try write("f", "PLAN.md", in: root)

        #expect(names(StyleStateEngine.matches("docs/plans/*.md", in: root)) == ["2026-01-01-a.md", "2026-01-02-b.md"])
        #expect(names(StyleStateEngine.matches("docs/plans/**/*.md", in: root)) == ["2025-12-31-e.md", "2026-01-01-a.md", "2026-01-02-b.md"])
        #expect(names(StyleStateEngine.matches("docs/plans/2026-01-0?-b.md", in: root)) == ["2026-01-02-b.md"])
        #expect(names(StyleStateEngine.matches("docs/plans/.*.md", in: root)) == [".hidden.md"])
        #expect(names(StyleStateEngine.matches("PLAN.md", in: root)) == ["PLAN.md"])
        #expect(StyleStateEngine.matches("docs/missing/*.md", in: root).isEmpty)
        #expect(StyleStateEngine.wildcard("plan.md", matches: "*.md"))
        #expect(!StyleStateEngine.wildcard("plan.mdx", matches: "*.md"))
    }

    /// Each escape gets its own outside file with a single name, so only the
    /// realpath containment check can refuse it — the hard-link rule cannot.
    @Test func symlinksThatLeaveTheWorkspaceAreDropped() throws {
        let (root, outside) = workspace("symlink")
        defer { try? fm.removeItem(at: root.deletingLastPathComponent()) }
        try write("- [x] outside file", "file/secret.md", in: outside)
        try write("- [x] outside folder", "folder/plan.md", in: outside)
        try write("- [ ] inside", "docs/plans/real.md", in: root)
        try write("- [ ] linked inside", "docs/other/target.md", in: root)
        try fm.createSymbolicLink(at: root.appendingPathComponent("docs/plans/escape.md"),
                                  withDestinationURL: outside.appendingPathComponent("file/secret.md"))
        try fm.createSymbolicLink(at: root.appendingPathComponent("docs/linked"),
                                  withDestinationURL: outside.appendingPathComponent("folder"))
        // A link that stays inside is followed to its target.
        try fm.createSymbolicLink(at: root.appendingPathComponent("docs/plans/inside.md"),
                                  withDestinationURL: root.appendingPathComponent("docs/other/target.md"))
        for name in ["file/secret.md", "folder/plan.md"] {
            let links = try fm.attributesOfItem(atPath: outside.appendingPathComponent(name).path)[.referenceCount] as? Int
            #expect(links == 1, "\(name) must have one name, or the hard-link rule would hide the containment check")
        }

        #expect(names(StyleStateEngine.matches("docs/plans/*.md", in: root)) == ["real.md", "target.md"])
        #expect(StyleStateEngine.matches("docs/plans/escape.md", in: root).isEmpty)
        #expect(StyleStateEngine.matches("docs/linked/*.md", in: root).isEmpty)
        #expect(StyleStateEngine.matches("docs/linked/plan.md", in: root).isEmpty)

        // Through the whole engine, with every file current: neither escape is read.
        for path in ["docs/plans/escape.md", "docs/linked/*.md"] {
            let sources = StyleStateSources(files: [StyleStateFileSource(path: path, parser: .markdownChecklist, widget: .progressBar)])
            let reading = StyleStateEngine.read(sources, workspacePath: root.path, since: .distantPast, session: nil)
            #expect(reading.fileSourceStates[0]?.exists == false, "\(path)")
            #expect(reading.widgets == [.progressBar(value: 0, total: 0)], "\(path)")
        }
    }

    @Test func aHardLinkIsRefusedEvenWhenBothNamesAreInside() throws {
        let (root, _) = workspace("hardlink")
        defer { try? fm.removeItem(at: root.deletingLastPathComponent()) }
        try write("- [x] shared", "docs/notes/shared.md", in: root)
        try fm.createDirectory(at: root.appendingPathComponent("docs/plans"), withIntermediateDirectories: true)
        try fm.linkItem(at: root.appendingPathComponent("docs/notes/shared.md"), to: root.appendingPathComponent("docs/plans/hard.md"))
        #expect(StyleStateEngine.matches("docs/plans/*.md", in: root).isEmpty)
        let sources = StyleStateSources(files: [StyleStateFileSource(path: "docs/plans/*.md", parser: .markdownChecklist, widget: .progressBar)])
        #expect(StyleStateEngine.read(sources, workspacePath: root.path, since: .distantPast, session: nil).fileSourceStates[0]?.exists == false)
        // The descriptor-level read refuses it too, should the walk ever let it through.
        let real = try #require(StyleStateEngine.realPath(root))
        #expect(StyleStateEngine.boundedRead(real.appendingPathComponent("docs/plans/hard.md"), workspace: real, maximumBytes: 1024) == nil)
    }

    @Test func aPlanThatIsNotTextCountsAsPresentButDrawsEmpty() throws {
        let (root, _) = workspace("binary")
        defer { try? fm.removeItem(at: root.deletingLastPathComponent()) }
        try StyleFixtures.write(Data([0xFF, 0xFE, 0x2D, 0x20, 0x5B, 0x78, 0x5D, 0xC3, 0x28]), to: root.appendingPathComponent("docs/plans/bad.md"))
        let sources = StyleStateSources(files: [StyleStateFileSource(path: "docs/plans/*.md", parser: .markdownChecklist, widget: .progressBar)])
        let reading = StyleStateEngine.read(sources, workspacePath: root.path, since: .distantPast, session: nil)
        #expect(reading.fileSourceStates[0] == StyleFileSourceState(exists: true, allChecked: false))
        #expect(reading.widgets == [.progressBar(value: 0, total: 0)])
    }

    @Test func changeDetectionOnlyWakesForASourceFolder() throws {
        let (root, _) = workspace("affects")
        defer { try? fm.removeItem(at: root.deletingLastPathComponent()) }
        let real = try #require(StyleStateEngine.realPath(root)).path
        let sources = StyleStateSources(files: [StyleStateFileSource(path: "docs/superpowers/plans/*.md", parser: .markdownChecklist, widget: .progressBar)])
        #expect(StyleStateEngine.affects(sources, workspacePath: root.path, changedPath: real + "/docs/superpowers/plans"))
        #expect(StyleStateEngine.affects(sources, workspacePath: root.path, changedPath: real + "/docs/superpowers/plans/a.md"))
        // The folder being created on the way.
        #expect(StyleStateEngine.affects(sources, workspacePath: root.path, changedPath: real + "/docs"))
        #expect(!StyleStateEngine.affects(sources, workspacePath: root.path, changedPath: real + "/src"))
        #expect(!StyleStateEngine.affects(sources, workspacePath: root.path, changedPath: real + "/docs/other"))
    }

    // MARK: - Parsers

    @Test func markdownChecklistCountsItemsOutsideCode() {
        let text = """
        # Plan
        - [ ] **Step 1: Write the failing test**
        - [x] Step 2
          * [X] nested, star bullet
        + [ ] plus bullet
        -[ ] no space after the bullet is not an item
        - [ ]x no space after the box is not an item
        - [y] unknown mark is not an item
        ```markdown
        - [ ] inside a fence is an example, not an item
        ```
        """
        let items = StyleStateEngine.checklistItems(text)
        #expect(items.map(\.text) == ["**Step 1: Write the failing test**", "Step 2", "nested, star bullet", "plus bullet"])
        #expect(items.map(\.checked) == [false, true, true, false])
        #expect(StyleStateEngine.checklistItems("").isEmpty)
    }

    @Test func markdownChecklistFillsEachWidgetKind() {
        let text = "- [x] one\n- [ ] two\n- [x] three\n- [ ] four\n"
        func parse(_ widget: StyleStateWidget) -> StyleFileReading {
            StyleStateEngine.parse(text, source: StyleStateFileSource(path: "p.md", parser: .markdownChecklist, widget: widget))
        }
        #expect(parse(.progressBar).widget == .progressBar(value: 2, total: 4))
        #expect(parse(.list).widget == .list(items: ["two", "four"]))
        #expect(parse(.label).widget == .label(text: L("styles.state.checklistLabel", ["checked": "2", "total": "4"])))
        #expect(parse(.progressBar).state == StyleFileSourceState(exists: true, allChecked: false))
        let done = StyleStateEngine.parse("- [x] a\n- [X] b", source: StyleStateFileSource(path: "p.md", parser: .markdownChecklist, widget: .progressBar))
        #expect(done.state.allChecked && done.widget == .progressBar(value: 2, total: 2))
        // No items at all is not "all checked".
        let none = StyleStateEngine.parse("# nothing to do", source: StyleStateFileSource(path: "p.md", parser: .markdownChecklist, widget: .progressBar))
        #expect(!none.state.allChecked && none.state.exists)
    }

    @Test func jsonReadsOnlyTheFixedShapes() {
        func parse(_ text: String, _ widget: StyleStateWidget) -> StylePanel.Widget {
            StyleStateEngine.parse(text, source: StyleStateFileSource(path: "s.json", parser: .json, widget: widget)).widget
        }
        #expect(parse("[\"a\", 2, {\"x\":1}]", .list) == .list(items: ["a", "2"]))
        #expect(parse("{\"items\":[\"x\",\"y\"]}", .list) == .list(items: ["x", "y"]))
        #expect(parse("\"shipping\"", .label) == .label(text: "shipping"))
        #expect(parse("{\"text\":\"ready\"}", .label) == .label(text: "ready"))
        #expect(parse("{\"value\":3,\"total\":5}", .progressBar) == .progressBar(value: 3, total: 5))
        #expect(parse("{\"value\":9,\"total\":5}", .progressBar) == .progressBar(value: 5, total: 5))
        #expect(parse("{\"value\":2}", .progressBar) == .progressBar(value: 2, total: nil))
        // Anything else draws the empty widget, never a guess.
        #expect(parse("{\"value\":true}", .progressBar) == .progressBar(value: 0, total: 0))
        #expect(parse("{\"list\":[1]}", .list) == .list(items: []))
        #expect(parse("not json", .label) == .label(text: ""))
        // JSON has no checklist, so it never says "all checked".
        let state = StyleStateEngine.parse("[]", source: StyleStateFileSource(path: "s.json", parser: .json, widget: .list)).state
        #expect(state == StyleFileSourceState(exists: true, allChecked: false))
    }

    @Test func aFileTooLargeToReadStillCountsAsPresent() throws {
        let (root, _) = workspace("large")
        defer { try? fm.removeItem(at: root.deletingLastPathComponent()) }
        try write(String(repeating: "- [x] a\n", count: StyleLimits.maximumBytes / 8 + 1), "PLAN.md", in: root)
        let sources = StyleStateSources(files: [StyleStateFileSource(path: "PLAN.md", parser: .markdownChecklist, widget: .progressBar)])
        let reading = StyleStateEngine.read(sources, workspacePath: root.path, since: .distantPast, session: nil)
        #expect(reading.fileSourceStates[0] == StyleFileSourceState(exists: true, allChecked: false))
        #expect(reading.widgets == [.progressBar(value: 0, total: 0)])
    }

    // MARK: - Run events

    private func activity(_ tool: String, state: String = "completed", summary: String = "", at date: Date) -> LogEntry {
        LogEntry(kind: "output", text: "", timestamp: ISO8601DateFormatter().string(from: date),
                 activity: AgentActivity(provider: "claude", kind: ActivitySupport.kind(tool: tool), state: state, toolName: tool, summary: summary))
    }

    @Test func runEventsComeFromThisPaneSinceItsFirstRequestInTheStyle() {
        let since = Date(timeIntervalSince1970: 1_800_000_000)
        var session = RunSession(workspaceId: "w", title: "t")
        session.logs = [
            activity("Task", summary: "earlier style", at: since.addingTimeInterval(-60)),
            activity("Bash", at: since.addingTimeInterval(-30)),
            activity("Task", state: "running", summary: "탐색", at: since.addingTimeInterval(5)),
            activity("Task", state: "completed", summary: "구현", at: since.addingTimeInterval(10)),
            // Talking to an agent that already exists does not start one.
            activity("send_input", summary: "follow-up", at: since.addingTimeInterval(12)),
            activity("Bash", at: since.addingTimeInterval(20)),
            activity("Read", at: since.addingTimeInterval(30)),
        ]
        let events = StyleStateEngine.runEvents(from: session, since: since)
        #expect(events.filter { $0.event == .subagentStart }.map(\.value) == ["탐색", "구현"])
        #expect(events.filter { $0.event == .subagentFinish }.map(\.value) == ["구현"])
        #expect(events.filter { $0.event == .toolCall }.map(\.value) == ["Bash", "Read"])
        // No request in the style yet: nothing is this style's.
        #expect(StyleStateEngine.runEvents(from: session, since: nil).isEmpty)

        let sources = StyleStateSources(runEvents: [
            StyleStateRunEventSource(event: .subagentStart, aggregate: .count, widget: .label),
            StyleStateRunEventSource(event: .subagentFinish, aggregate: .count, widget: .progressBar),
            StyleStateRunEventSource(event: .toolCall, aggregate: .lastValue, widget: .label),
            StyleStateRunEventSource(event: .toolCall, aggregate: .count, widget: .list),
            StyleStateRunEventSource(event: .subagentStart, aggregate: .lastValue, widget: .list),
        ])
        let reading = StyleStateEngine.reading(sources: sources, files: [], runEvents: events)
        #expect(reading.widgets == [
            .label(text: L("styles.state.subagentStartCount", ["count": "2"])),
            .progressBar(value: 1, total: nil),
            .label(text: "Read"),
            .list(items: ["Bash", "Read"]),
            .list(items: ["구현"]),
        ])
        #expect(reading.fileSourceStates.isEmpty)
    }

    @Test func everyDeclaredSourceDrawsOneWidgetInOrderEvenWhenEmpty() throws {
        let (root, _) = workspace("empty")
        defer { try? fm.removeItem(at: root.deletingLastPathComponent()) }
        let sources = StyleStateSources(
            files: [StyleStateFileSource(path: "docs/plans/*.md", parser: .markdownChecklist, widget: .progressBar),
                    StyleStateFileSource(path: "status.json", parser: .json, widget: .list)],
            runEvents: [StyleStateRunEventSource(event: .subagentStart, aggregate: .lastValue, widget: .label)])
        let reading = StyleStateEngine.read(sources, workspacePath: root.path, since: Date(), session: RunSession(workspaceId: "w", title: "t"))
        #expect(reading.widgets == [.progressBar(value: 0, total: 0), .list(items: []), .label(text: "")])
        #expect(reading.fileSourceStates == [0: StyleFileSourceState(exists: false, allChecked: false),
                                             1: StyleFileSourceState(exists: false, allChecked: false)])
        // A workspace that is not there reads as nothing, not as a crash.
        let missing = StyleStateEngine.read(sources, workspacePath: root.path + "-gone", since: Date(), session: nil)
        #expect(missing.widgets.count == 3)
    }
}
