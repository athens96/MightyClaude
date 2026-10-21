import Foundation
@testable import MightyCore

/// Manifests are built as ordered key/value text rather than through
/// `JSONSerialization`, because §1.1 requires `schema` to be the first key and
/// a dictionary has no order to give.
enum StyleFixtures {
    static let flat: [(String, String)] = [
        ("schema", "1"),
        ("id", "\"flow\""),
        ("name", "\"Flow\""),
        ("summary", "\"덜어내는 스타일\""),
        ("subtitle", "\"flow\""),
        ("placeholders", "{\"idle\":\"i\",\"answering\":\"a\"}"),
        ("guidance", "{}"),
        ("prerequisites", "{\"mode\":\"all\",\"probes\":[]}"),
        ("phases", "[]"),
        ("groups", "[{\"id\":\"g\",\"title\":\"G\",\"actions\":[\"go\"]}]"),
        ("actions", "[{\"id\":\"go\",\"title\":\"Go\",\"help\":\"h\",\"prompt\":\"/go\",\"takesText\":false}]"),
        ("aliases", "[]"),
        ("recognition", "{\"prefixes\":[\"/\"],\"lowercase\":false}"),
        ("rules", "{\"start\":{\"kind\":\"none\"},\"phase\":{\"kind\":\"none\"},\"next\":{\"kind\":\"byGroup\"},\"enter\":{\"kind\":\"verbatim\"},\"recommend\":{\"kind\":\"none\"},\"initialGroup\":{\"kind\":\"fixed\",\"group\":\"g\"}}"),
        ("capabilities", "[]"),
        ("autoAllow", "[]"),
        ("presentation", "{}"),
    ]

    /// The same shape with two phases, for the rules that need them.
    static let phased: [(String, String)] = flat.map { key, value in
        switch key {
        case "phases": return (key, "[{\"id\":\"one\",\"title\":\"하나\",\"order\":0},{\"id\":\"two\",\"title\":\"둘\",\"order\":1}]")
        case "actions": return (key, "[{\"id\":\"go\",\"title\":\"Go\",\"help\":\"h\",\"prompt\":\"/go\",\"takesText\":false,\"phase\":\"one\"},{\"id\":\"say\",\"title\":\"Say\",\"help\":\"h\",\"prompt\":\"/say {text}\",\"takesText\":true,\"foldText\":\"oneLine\",\"phase\":\"two\"}]")
        case "groups": return (key, "[{\"id\":\"g\",\"title\":\"G\",\"actions\":[\"go\",\"say\"]}]")
        case "rules": return (key, "{\"start\":{\"kind\":\"none\"},\"phase\":{\"kind\":\"lastRecognisedAction\",\"default\":\"one\"},\"next\":{\"kind\":\"byPhase\",\"map\":{\"one\":[\"say\"],\"two\":[\"go\"]}},\"enter\":{\"kind\":\"verbatim\"},\"recommend\":{\"kind\":\"none\"},\"initialGroup\":{\"kind\":\"fixed\",\"group\":\"g\"}}")
        default: return (key, value)
        }
    }

    static func text(_ base: [(String, String)] = flat, _ overrides: [String: String] = [:],
                     drop: [String] = [], extra: [(String, String)] = []) -> String {
        let pairs = base.filter { !drop.contains($0.0) }.map { key, value in (key, overrides[key] ?? value) } + extra
        return "{" + pairs.map { "\"\($0.0)\":\($0.1)" }.joined(separator: ",") + "}"
    }

    static func data(_ base: [(String, String)] = flat, _ overrides: [String: String] = [:],
                     drop: [String] = [], extra: [(String, String)] = []) -> Data {
        Data(text(base, overrides, drop: drop, extra: extra).utf8)
    }

    /// A four-phase loop that reads its own state (§1.16): the newest current
    /// checklist under `docs/plans/` as a bar, `subagent.start` as a count, and
    /// two state overrides that move the phase. No bundled style declares file
    /// or run-event sources since v7, so the tests that need them read this.
    static let stateful = Data(#"""
    {
    "schema":1,
    "id":"loop",
    "name":"Loop",
    "summary":"브레인스토밍 → 계획 → 실행 → 완료 루프",
    "subtitle":"브레인스토밍 → 계획 → 실행 → 완료",
    "placeholders":{"idle":"아이디어나 다음 단계를 적고 위에서 행동을 고르세요…","answering":"직접 답하려면 여기에 적고 Enter…"},
    "guidance":{"start":"무엇을 만들까요? 아이디어를 적으면 브레인스토밍으로 요구를 정리합니다.","next":"{phase} 단계가 끝났습니다. 다음 단계를 고르거나 계속 진행하세요.","running":"{phase} 진행 중 · 질문이 오면 여기에 표시됩니다"},
    "prerequisites":{"mode":"all","probes":[]},
    "phases":[{"id":"brainstorm","title":"브레인스토밍","order":0},{"id":"plan","title":"계획","order":1},{"id":"execute","title":"실행","order":2},{"id":"finish","title":"완료","order":3}],
    "groups":[{"id":"loop","title":"루프","actions":["brainstorming","writing-plans","executing-plans","subagent-driven-development","verification-before-completion","finishing-a-development-branch","systematic-debugging","requesting-code-review"]}],
    "actions":[
      {"id":"brainstorming","title":"브레인스토밍","help":"아이디어를 구체적인 설계와 요구사항으로 발전시킵니다","prompt":"/loop:brainstorming {text}","takesText":true,"foldText":"trimOnly","phase":"brainstorm"},
      {"id":"writing-plans","title":"계획 작성","help":"구현 계획을 단계별 작업으로 작성합니다","prompt":"/loop:writing-plans","takesText":false,"phase":"plan"},
      {"id":"executing-plans","title":"계획 실행","help":"계획을 직접 한 단계씩 실행합니다","prompt":"/loop:executing-plans","takesText":false,"phase":"execute"},
      {"id":"subagent-driven-development","title":"서브에이전트 개발","help":"서브에이전트를 통해 각 태스크를 병렬로 처리합니다","prompt":"/loop:subagent-driven-development","takesText":false,"phase":"execute"},
      {"id":"verification-before-completion","title":"완료 전 검증","help":"완료 전 구현이 올바른지 체계적으로 검증합니다","prompt":"/loop:verification-before-completion","takesText":false,"phase":"finish"},
      {"id":"finishing-a-development-branch","title":"브랜치 완료","help":"개발 브랜치를 마무리하고 PR을 준비합니다","prompt":"/loop:finishing-a-development-branch","takesText":false,"phase":"finish"},
      {"id":"systematic-debugging","title":"디버깅","help":"체계적인 방법으로 버그의 근본 원인을 찾습니다","prompt":"/loop:systematic-debugging","takesText":false},
      {"id":"requesting-code-review","title":"코드 리뷰 요청","help":"코드 리뷰를 요청합니다","prompt":"/loop:requesting-code-review","takesText":false}
    ],
    "aliases":[{"name":"brain","phase":"brainstorm"},{"name":"planning","phase":"plan"},{"name":"run","phase":"execute"},{"name":"done","phase":"finish"}],
    "recognition":{"prefixes":["/loop:"],"lowercase":true},
    "rules":{"start":{"kind":"actions","phase":"brainstorm","actions":["brainstorming","writing-plans"],"resetTitle":"새 아이디어"},"phase":{"kind":"lastRecognisedAction","default":"brainstorm","stateOverrides":[{"sourceIndex":0,"condition":"fileExists","phase":"execute"},{"sourceIndex":0,"condition":"allChecked","phase":"finish"}]},"next":{"kind":"byPhase","map":{"brainstorm":["writing-plans","systematic-debugging"],"plan":["executing-plans","subagent-driven-development"],"execute":["verification-before-completion","systematic-debugging","executing-plans"],"finish":["verification-before-completion","finishing-a-development-branch","requesting-code-review"]}},"enter":{"kind":"verbatim"},"recommend":{"kind":"none"},"initialGroup":{"kind":"fixed","group":"loop"}},
    "capabilities":[],
    "autoAllow":[],
    "presentation":{},
    "stateSources":{"files":[{"path":"docs/plans/*.md","parser":"markdownChecklist","widget":"progressBar"}],"runEvents":[{"event":"subagent.start","aggregate":"count","widget":"label"}]}
    }
    """#.utf8)

    static var statefulStyle: RegisteredStyle {
        do { return try registered(stateful, path: "/tmp/loop.json") } catch { fatalError("stateful fixture refused: \(error)") }
    }

    /// A catalogue: no phases, two groups drawn as a map, Enter verbatim, and
    /// a recommendation and first group read from a built-in feature. That
    /// feature exists only in `testCapabilities` (§1.8), which is the only
    /// vocabulary this fixture decodes under.
    static let catalogue = Data(#"""
    {
    "schema":1,
    "id":"deck",
    "name":"Deck",
    "summary":"그룹으로 고르는 스킬 카탈로그",
    "subtitle":"deck",
    "placeholders":{"idle":"대상을 적고 위에서 스킬을 고르세요 · Enter는 그대로 요청합니다…","answering":"직접 답하려면 여기에 적고 Enter…"},
    "guidance":{"next":"대상(파일 경로나 지시)을 아래에 적고 스킬을 누르세요. 비워 두면 스킬만 보냅니다.","running":"실행 중 · 고른 스킬은 다음 요청으로 대기합니다"},
    "prerequisites":{"mode":"all","probes":[]},
    "phases":[],
    "groups":[{"id":"depth","title":"depth","axis":"하나 · 지금","question":"이 하나가 깨끗하고 참인가?","actions":["tidy","hate"]},{"id":"coil","title":"coil","axis":"하나 · 반복","question":"각 패스가 다음 패스를 가르쳤는가?","actions":["plan","loop","memo","work","nba"]}],
    "actions":[
      {"id":"tidy","title":"tidy","help":"아티팩트 하나를 다듬습니다","prompt":"/tidy {text}","takesText":true,"foldText":"oneLine","scope":"아티팩트 하나","glyph":"♻️"},
      {"id":"hate","title":"hate","help":"친절하기를 거부합니다. 계획을 죽일 수 있는 반론 하나와 가장 싼 테스트를 냅니다","prompt":"/hate {text}","takesText":true,"foldText":"oneLine","scope":"계획 하나","glyph":"😈","flags":["userInvoked"]},
      {"id":"plan","title":"plan","help":"새 사이클을 엽니다","prompt":"/plan {text}","takesText":true,"foldText":"oneLine","scope":"새 사이클 하나","glyph":"🗂️","flags":["userInvoked"]},
      {"id":"loop","title":"loop","help":"사이클 전체를 돌립니다","prompt":"/loop {text}","takesText":true,"foldText":"oneLine","scope":"전체 루프","glyph":"🌀"},
      {"id":"memo","title":"memo","help":"끝난 사이클에서 배운 것을 적습니다","prompt":"/memo {text}","takesText":true,"foldText":"oneLine","scope":"완료된 사이클 하나","glyph":"🧭"},
      {"id":"work","title":"work","help":"배운 것만 들고 다시 시작합니다","prompt":"/work {text}","takesText":true,"foldText":"oneLine","scope":"재시작 하나","glyph":"🧱"},
      {"id":"nba","title":"nba","help":"다음 한 수를 고릅니다","prompt":"/nba {text}","takesText":true,"foldText":"oneLine","scope":"현재 사이클","glyph":"🎯","flags":["readOnly"]}
    ],
    "aliases":[],
    "recognition":{"prefixes":["/"],"lowercase":false},
    "rules":{"start":{"kind":"none"},"phase":{"kind":"none"},"next":{"kind":"byGroup"},"enter":{"kind":"verbatim"},"recommend":{"kind":"capability","capability":"acme.thing","group":"coil","map":{"absent":"plan","open":"loop","complete":"work"}},"initialGroup":{"kind":"capabilityState","capability":"acme.thing","map":{"absent":"depth","open":"coil","complete":"coil"}}},
    "capabilities":["acme.thing"],
    "autoAllow":[],
    "presentation":{}
    }
    """#.utf8)

    static var catalogueStyle: RegisteredStyle {
        do { return try registered(catalogue, path: "/tmp/deck.json", capabilities: testCapabilities) }
        catch { fatalError("catalogue fixture refused: \(error)") }
    }

    /// The app's own vocabulary of built-in features is empty since v7 (§1.8).
    /// The engine still reads the `capabilities` field and the two rules that
    /// name a feature, so their checks run against this one instead.
    static let testCapabilities: [String: [String]] = ["acme.thing": ["absent", "open", "complete"]]

    static func manifest(_ base: [(String, String)] = flat, _ overrides: [String: String] = [:],
                         source: StyleSource = .user,
                         capabilities: [String: [String]] = StyleCapabilityID.vocabulary) throws -> StyleManifest {
        try StyleManifestDecoder.decode(data(base, overrides), source: source, capabilities: capabilities)
    }

    /// The code a manifest is refused with, or nil when it decodes.
    static func code(_ data: Data, source: StyleSource = .user,
                     capabilities: [String: [String]] = StyleCapabilityID.vocabulary) -> String? {
        do { _ = try StyleManifestDecoder.decode(data, source: source, capabilities: capabilities); return nil }
        catch let error as StyleManifestError { return error.code }
        catch { return "E_UNEXPECTED" }
    }

    static func message(_ data: Data, source: StyleSource = .user) -> String? {
        do { _ = try StyleManifestDecoder.decode(data, source: source); return nil }
        catch let error as StyleManifestError { return error.message }
        catch { return nil }
    }

    /// A throwaway directory that never touches the user's real data folder.
    static func temporaryDirectory(_ label: String) -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("\(label)-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    static func write(_ data: Data, to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url)
    }

    static func discovered(_ data: Data, source: StyleSource, url: URL, workspacePath: String? = nil) -> DiscoveredStyleFile {
        DiscoveredStyleFile(source: source, url: url, workspacePath: workspacePath, data: data)
    }

    static func registered(_ data: Data, source: StyleSource = .user, path: String = "/tmp/flow.json",
                           workspacePath: String? = nil, approval: StyleApprovalState = .approved,
                           capabilities: [String: [String]] = StyleCapabilityID.vocabulary) throws -> RegisteredStyle {
        let manifest = try StyleManifestDecoder.decode(data, source: source, capabilities: capabilities)
        return RegisteredStyle(manifest: manifest, source: source, path: path, workspacePath: workspacePath,
                               hash: StyleHash.of(data), approval: approval)
    }

    static func bundled(_ id: String) -> RegisteredStyle {
        if let style = BundledStyles.shared.style(id) { return style }
        // A trap here used to be the only CI evidence. Say what was searched
        // and what was rejected so the public annotation names the cause.
        let files = StyleSourceScanner.bundled()
        let rejections = StyleRegistry.make(files: files, approvals: []).rejections
        fatalError("bundled style '\(id)' missing: directories=\(BundledStyleSource.directories().map(\.path)) "
                   + "files=\(files.count) rejections=\(rejections)")
    }
}
