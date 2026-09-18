import Foundation

/// The Paperthin style of Mighty mode. Paperthin (github.com/LilMGenius/paperthin,
/// MIT) is a catalogue of small skills, no server: each is invoked by name
/// (`/re0`, `/nba`), some only by the human. The app lays the catalogue out
/// along Paperthin's own map (how many artifacts, over how much time), sends
/// the chosen skill as the pane's next request, and shows the iteration
/// casebook the coil skills keep under `.re0/iteration/`.
public enum PaperthinDomain: String, CaseIterable, Sendable, Equatable {
    case depth, breadth, coil, mesh
    public var title: String { rawValue }
    /// Where the domain sits on the map.
    public var axis: String {
        switch self { case .depth: "하나 · 지금"; case .breadth: "여럿 · 지금"; case .coil: "하나 · 반복"; case .mesh: "여러 시선" }
    }
    public var question: String {
        switch self {
        case .depth: "이 하나가 깨끗하고 참인가?"
        case .breadth: "하나의 진실이 모든 곳에서 일관적인가?"
        case .coil: "각 패스가 다음 패스를 가르쳤는가?"
        case .mesh: "집단이 진실로 수렴하는가?"
        }
    }
}

public struct PaperthinSkill: Sendable, Equatable, Identifiable {
    public var name: String
    public var emoji: String
    public var domain: PaperthinDomain
    public var summary: String
    public var scope: String
    /// Only the human can fire it (`disable-model-invocation`); the rest the model may reach for on its own.
    public var userInvoked: Bool
    public var readOnly: Bool
    public var id: String { name }
}

public enum PaperthinCatalog {
    public static let style = "paperthin"
    /// The project's documented install, narrowed to the one agent this style
    /// drives (`claude-code` is the `skills` installer's id for `~/.claude/skills`).
    public static let installCommand = "npx skills@latest add LilMGenius/paperthin --global --agent claude-code"

    /// Names, summaries and flags follow Paperthin's Korean README index.
    public static let skills: [PaperthinSkill] = [
        PaperthinSkill(name: "re0", emoji: "♻️", domain: .depth, summary: "drift된 아티팩트를 또 다른 패치가 아니라 깨끗한 v0로 다시 씁니다", scope: "아티팩트 하나", userInvoked: false, readOnly: false),
        PaperthinSkill(name: "readchk", emoji: "🧭", domain: .depth, summary: "요청을 어떻게 읽었는지 확인하고, 실제로 남은 갈림길만 드러냅니다", scope: "지시 하나", userInvoked: false, readOnly: true),
        PaperthinSkill(name: "aim", emoji: "🏹", domain: .depth, summary: "넘겨받은 데이터를 읽고, 물어보는 대신 확인할 의도를 먼저 제안합니다", scope: "넘겨받은 데이터 하나", userInvoked: false, readOnly: true),
        PaperthinSkill(name: "modelchk", emoji: "📏", domain: .depth, summary: "충분한 가장 싼 tier와 reasoning effort를 고릅니다", scope: "작업 하나", userInvoked: false, readOnly: true),
        PaperthinSkill(name: "hate", emoji: "😈", domain: .depth, summary: "친절하기를 거부합니다. 계획을 죽일 수 있는 반론 하나와 가장 싼 테스트를 냅니다", scope: "계획 하나", userInvoked: true, readOnly: false),
        PaperthinSkill(name: "macrothink", emoji: "🧠", domain: .depth, summary: "bait를 걷어내고 새 읽기를 펼친 뒤 divergence를 먼저 보고합니다", scope: "방향 하나", userInvoked: true, readOnly: true),
        PaperthinSkill(name: "feynman", emoji: "🧐", domain: .depth, summary: "방금 내린 결정을 설명할 수 있을 때까지 밀어붙이고, 안 되면 그 빈틈을 드러냅니다", scope: "결정 하나", userInvoked: true, readOnly: true),
        PaperthinSkill(name: "autobahn", emoji: "🛣️", domain: .depth, summary: "안전하지 않은 스코프를 앞에서 도려내고, 안전한 나머지는 전력으로 실행한 뒤 descope를 기록합니다", scope: "작업 하나", userInvoked: false, readOnly: false),
        PaperthinSkill(name: "reorder", emoji: "🔃", domain: .depth, summary: "drift된 목록을 하나의 명시된 원칙 아래 논리적 순서로 다시 맞춥니다. 항목만 옮기고, 표현은 바꾸지 않습니다", scope: "목록 하나", userInvoked: true, readOnly: false),
        PaperthinSkill(name: "detool", emoji: "🧰", domain: .depth, summary: "우연히 섞인 도구 이름을 그것이 뜻한 메커니즘으로 바꿉니다", scope: "durable 아티팩트 하나", userInvoked: false, readOnly: false),
        PaperthinSkill(name: "dedash", emoji: "✂️", domain: .depth, summary: "em dash와 비슷한 tell을 지우고, 각 위치에 맞는 문장부호를 고릅니다", scope: "내 문장", userInvoked: true, readOnly: false),
        PaperthinSkill(name: "debloat", emoji: "🗜️", domain: .depth, summary: "bloat된 아티팩트를 load-bearing한 밀도까지 압축합니다. 단어는 잘라내되, 규칙은 절대 잘라내지 않습니다", scope: "아티팩트 하나", userInvoked: true, readOnly: false),
        PaperthinSkill(name: "shower", emoji: "🚿", domain: .depth, summary: "맥락 없는 새 눈으로 차갑게 읽습니다. 이것이 혼자서도 서는가?", scope: "아티팩트 하나", userInvoked: false, readOnly: true),
        PaperthinSkill(name: "factchk", emoji: "🔬", domain: .depth, summary: "주장된 것을 양방향으로 소스에 대조합니다. 말도 안 되는 것이 팩트일 수 있고, 당연한 것이 거짓일 수 있는가?", scope: "클레임 하나", userInvoked: false, readOnly: false),
        PaperthinSkill(name: "mandela", emoji: "🧪", domain: .depth, summary: "leakage가 있는지 audit합니다. 외부 ground truth가 실제로 들어오는가?", scope: "eval 하나", userInvoked: false, readOnly: true),
        PaperthinSkill(name: "sip", emoji: "🥄", domain: .depth, summary: "변경 뒤마다 레포 자체의 clean-and-true 체크로 아웃풋을 맛봅니다", scope: "내 아웃풋", userInvoked: false, readOnly: false),
        PaperthinSkill(name: "re0-git", emoji: "🧾", domain: .depth, summary: "완료된 커밋 메시지를 다시 써서 `git log`만으로 handoff가 되게 합니다", scope: "커밋 하나", userInvoked: true, readOnly: false),
        PaperthinSkill(name: "re0-release", emoji: "🚀", domain: .depth, summary: "shipping·releasing 체크리스트를 실행하고, 확인 후 태그·퍼블리시합니다", scope: "릴리스 하나", userInvoked: true, readOnly: false),
        PaperthinSkill(name: "re0-merge", emoji: "🤝", domain: .depth, summary: "기여를 리뷰하고 반영합니다: gate를 통과시키고, 작성자 크레딧을 유지하고, 닫기 전에 승인하고, 변경 사항을 설명합니다", scope: "기여 하나", userInvoked: true, readOnly: false),
        PaperthinSkill(name: "ssotize", emoji: "🧲", domain: .breadth, summary: "흩어진 곳을 감사한 뒤 한 집으로 모아 나머지가 그곳을 가리키게 합니다", scope: "팩트 하나, 여러 위치", userInvoked: false, readOnly: false),
        PaperthinSkill(name: "re0-upgrade", emoji: "🧰", domain: .breadth, summary: "한 번에 현재 전체 카탈로그로 올립니다: 이름 바뀐 건 정리, 새 건 추가, 전부 먼저 확인", scope: "내 스킬 설치", userInvoked: true, readOnly: false),
        PaperthinSkill(name: "re0-plan", emoji: "🗂️", domain: .coil, summary: "re0-loop의 첫 turn 전에 새 iteration 폴더를 열고 DESIGN/WORKFLOW/EVIDENCE를 씁니다", scope: "새 사이클 하나", userInvoked: true, readOnly: false),
        PaperthinSkill(name: "re0-loop", emoji: "🌀", domain: .coil, summary: "build → QA → re0-memo → re0-work 루프를 돌려 배움이 코드가 아니라 축적되게 합니다", scope: "전체 루프", userInvoked: false, readOnly: false),
        PaperthinSkill(name: "re0-memo", emoji: "🧭", domain: .coil, summary: "끝났거나 실패한 사이클에서 교훈과 anti-pattern을 뽑아냅니다", scope: "완료된 사이클 하나", userInvoked: false, readOnly: false),
        PaperthinSkill(name: "re0-work", emoji: "🧱", domain: .coil, summary: "재사용할 자격을 얻은 교훈만 남기고 v0에서 다시 시작합니다", scope: "재시작 하나", userInvoked: false, readOnly: false),
        PaperthinSkill(name: "catchup", emoji: "🗺️", domain: .coil, summary: "실시간 state에서 잃어버린 context를 재구성합니다: 누구에게 필요한지, 무엇이 바뀌었는지, 새 단어가 무엇을 뜻하는지", scope: "재진입 하나", userInvoked: false, readOnly: true),
        PaperthinSkill(name: "nba", emoji: "🎯", domain: .coil, summary: "살아 있는 사이클 state를 읽고 메뉴가 아니라 단 하나의 다음 최선 행동을 돌려줍니다", scope: "현재 사이클", userInvoked: false, readOnly: true),
        PaperthinSkill(name: "prism", emoji: "🔺", domain: .mesh, summary: "아티팩트 하나를 독립적인 렌즈들로 쪼갠 뒤, 충돌하는 지점과 그것을 푸는 질문을 돌려줍니다", scope: "아티팩트 하나", userInvoked: true, readOnly: true),
    ]

    public static func skills(in domain: PaperthinDomain) -> [PaperthinSkill] { skills.filter { $0.domain == domain } }
    public static func skill(_ name: String) -> PaperthinSkill? { skills.first { $0.name == name } }

    /// `/re0 docs/spec.md`; bare `/nba` when nothing was typed.
    public static func prompt(skill name: String, text: String = "") -> String? {
        guard skill(name) != nil else { return nil }
        // One line: a skill reads its arguments up to the first line break.
        let argument = text.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }.joined(separator: " ")
        return "/" + name + (argument.isEmpty ? "" : " " + argument)
    }

    /// The Paperthin skill a prompt starts with, if any.
    public static func skill(inPrompt prompt: String) -> PaperthinSkill? {
        let trimmed = prompt.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.hasPrefix("/") else { return nil }
        let name = String(trimmed.dropFirst().prefix { !$0.isWhitespace })
        return skill(name)
    }
    public static func requestTitle(forInput input: String) -> String? { skill(inPrompt: input).map { $0.emoji + " " + $0.name } }

    /// Installed when the skills are in the user's or the project's Claude
    /// skills folder (the `skills` installer, global or project scope) or the
    /// plugin registry has a `paperthin@…` entry.
    public static func installed(home: URL = FileManager.default.homeDirectoryForCurrentUser, workspacePath: String? = nil) -> Bool {
        let roots = [home] + (workspacePath.map { [URL(fileURLWithPath: $0, isDirectory: true)] } ?? [])
        let probes = roots.flatMap { root in ["re0", "nba", "re0-loop"].map { root.appendingPathComponent(".claude/skills/\($0)/SKILL.md").path } }
        if probes.contains(where: FileManager.default.fileExists(atPath:)) { return true }
        let registry = home.appendingPathComponent(".claude/plugins/installed_plugins.json")
        guard let data = CLIAccountSupport.boundedData(registry, maximumBytes: 4 * 1024 * 1024), let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let plugins = object["plugins"] as? [String: Any] else { return false }
        return plugins.keys.contains { $0.hasPrefix("paperthin@") }
    }

    /// What the coil panel recommends first, from the casebook on disk.
    public static func recommendedCoilSkill(casebook: PaperthinCasebook?) -> String {
        guard let casebook else { return "re0-plan" }
        return casebook.files.contains("RETRO.local.md") && casebook.files.contains("DESIGN.local.md") ? "re0-work" : "re0-loop"
    }
}

/// The newest iteration folder the coil skills wrote: `.re0/iteration/<version>-<workname>/`
/// with `DESIGN`/`WORKFLOW`/`EVIDENCE`/`RETRO` `.local.md` and flat `REF-*.local.md`.
public struct PaperthinCasebook: Sendable, Equatable {
    public var name: String
    public var path: String
    public var files: [String]
    public var modifiedAt: Date
    /// `full` cycles carry a DESIGN; `lightweight` ones only a RETRO note.
    public var weight: String { files.contains("DESIGN.local.md") ? "full" : "lightweight" }

    public static let knownOrder = ["DESIGN.local.md", "WORKFLOW.local.md", "EVIDENCE.local.md", "RETRO.local.md"]
    static let scannedFolders = 24

    public static func latest(workspacePath: String) -> PaperthinCasebook? {
        let root = URL(fileURLWithPath: workspacePath, isDirectory: true).appendingPathComponent(".re0/iteration", isDirectory: true)
        let keys: Set<URLResourceKey> = [.isDirectoryKey, .isSymbolicLinkKey, .contentModificationDateKey]
        guard let listing = try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: Array(keys), options: [.skipsHiddenFiles]) else { return nil }
        // Real folders only (a link could point outside the workspace), newest
        // first, so the scan below stays small however long the history is.
        let folders = listing.compactMap { url -> (URL, Date)? in
            guard let values = try? url.resourceValues(forKeys: keys), values.isDirectory == true, values.isSymbolicLink != true else { return nil }
            return (url, values.contentModificationDate ?? .distantPast)
        }.sorted { $0.1 > $1.1 }.prefix(scannedFolders).map(\.0)
        var best: PaperthinCasebook?
        for folder in folders {
            let entries = ((try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.contentModificationDateKey], options: [.skipsHiddenFiles])) ?? [])
                .filter { $0.lastPathComponent.hasSuffix(".local.md") }
            guard !entries.isEmpty else { continue }
            let newest = entries.compactMap { try? $0.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate }.max() ?? .distantPast
            let names = entries.map(\.lastPathComponent)
            let ordered = knownOrder.filter(names.contains) + names.filter { !knownOrder.contains($0) }.sorted()
            let candidate = PaperthinCasebook(name: String(folder.lastPathComponent.prefix(120)), path: folder.path, files: Array(ordered.prefix(24)), modifiedAt: newest)
            if best == nil || candidate.modifiedAt > best!.modifiedAt { best = candidate }
        }
        return best
    }
}
