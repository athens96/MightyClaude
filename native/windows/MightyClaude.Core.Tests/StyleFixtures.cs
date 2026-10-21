using System.Security.Cryptography;
using System.Text;
using MightyClaude.Core;

/// <summary>
/// Manifests only the tests read (M/Tests/MightyCoreTests/StyleFixtures.swift <c>stateful</c>, <c>catalogue</c>,
/// <c>testCapabilities</c>): since v7 no bundled style declares state sources or a catalogue fed by a built-in feature, so
/// the checks that need one read these instead, byte for byte the Mac's.
/// </summary>
internal static class StyleFixtures
{
    /// <summary>
    /// A four-phase loop that reads its own state (§1.16): the newest current checklist under <c>docs/plans/</c> as a bar,
    /// <c>subagent.start</c> as a count, and two state overrides that move the phase.
    /// </summary>
    internal static readonly byte[] Stateful = Encoding.UTF8.GetBytes("""
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
    """);

    /// <summary>
    /// A catalogue: no phases, two groups drawn as a map, Enter verbatim, and a recommendation and first group read from a
    /// built-in feature. That feature exists only in <see cref="TestCapabilities"/> (§1.8), the only vocabulary it decodes under.
    /// </summary>
    internal static readonly byte[] Catalogue = Encoding.UTF8.GetBytes("""
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
    """);

    /// <summary>The app's own vocabulary of built-in features is empty since v7 (§1.8); the checks of the field and the two rules that name a feature run against this one instead.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> TestCapabilities = new Dictionary<string, string[]> { ["acme.thing"] = ["absent", "open", "complete"] };

    /// <summary>A user style, approved, as the registry would hold it.</summary>
    internal static RegisteredStyle Registered(byte[] bytes, string path, IReadOnlyDictionary<string, string[]>? capabilities = null) =>
        new(StyleManifestDecoder.Decode(bytes, "user", capabilities), "user", path, null, Convert.ToHexStringLower(SHA256.HashData(bytes)), "approved", bytes);

    internal static RegisteredStyle StatefulStyle => Registered(Stateful, "/tmp/loop.json");
    internal static RegisteredStyle CatalogueStyle => Registered(Catalogue, "/tmp/deck.json", TestCapabilities);
}
