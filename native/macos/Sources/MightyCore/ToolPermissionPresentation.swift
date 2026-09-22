import Foundation

/// One labelled value of a tool request, shown instead of raw JSON.
public struct ToolPermissionField: Equatable, Sendable {
    public let label: String
    public let value: String
    /// Monospaced box (commands, paths, code); otherwise running text.
    public let code: Bool
    public init(label: String, value: String, code: Bool = false) { self.label = label; self.value = value; self.code = code }
}

/// A readable form of a `can_use_tool` request. It only rearranges the exact
/// input for display; approval still sends the original, unmodified input.
public struct ToolPermissionPresentation: Equatable, Sendable {
    /// What the tool does, e.g. "명령 실행" or "파일 수정".
    public let title: String
    /// The model's own description of the step, when the input carries one.
    public let headline: String?
    public let fields: [ToolPermissionField]
    public static let maximumFieldBytes = 4_096
    public static let maximumFields = 12

    public init(title: String, headline: String? = nil, fields: [ToolPermissionField] = []) {
        self.title = title; self.headline = headline; self.fields = fields
    }

    /// The first monospaced value, which the compact pet bubble shows.
    public var primaryCode: ToolPermissionField? { fields.first { $0.code } }

    public static func make(toolName: String, inputJSON: String) -> ToolPermissionPresentation {
        let name = toolName.trimmingCharacters(in: .whitespacesAndNewlines)
        let input = (inputJSON.data(using: .utf8).flatMap { try? JSONSerialization.jsonObject(with: $0) } as? [String: Any]) ?? [:]
        if name == "command_execution" || name == "file_change" {
            return codexPresentation(name: name, input: input)
        }
        func text(_ key: String) -> String? {
            guard let raw = input[key] else { return nil }
            let string: String
            if let value = raw as? String { string = value }
            else if let value = raw as? [String] { string = value.joined(separator: "\n") }
            else if let number = raw as? NSNumber { string = CFGetTypeID(number) == CFBooleanGetTypeID() ? (number.boolValue ? "예" : "아니요") : number.stringValue }
            else if JSONSerialization.isValidJSONObject(raw), let data = try? JSONSerialization.data(withJSONObject: raw, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) { string = String(decoding: data, as: UTF8.self) }
            else { return nil }
            let clean = ActivitySupport.clean(string, maximumBytes: maximumFieldBytes)
            guard !clean.isEmpty else { return nil }
            return string.utf8.count > maximumFieldBytes ? clean + "\n…" : clean
        }
        func field(_ key: String, _ label: String, code: Bool = false) -> ToolPermissionField? {
            text(key).map { ToolPermissionField(label: label, value: $0, code: code) }
        }
        let headline = text("description").map { ActivitySupport.clean($0, maximumBytes: 1_000, singleLine: true) }
        var title: String
        var fields: [ToolPermissionField?] = []
        var consumed: Set<String> = ["description"]
        switch name {
        case "Bash":
            title = "명령 실행"
            fields = [field("command", "명령", code: true), field("timeout", "제한 시간(ms)"), field("run_in_background", "백그라운드 실행")]
            consumed.formUnion(["command", "timeout", "run_in_background"])
        case "Read":
            title = "파일 읽기"
            fields = [field("file_path", "파일", code: true), field("offset", "시작 줄"), field("limit", "줄 수")]
            consumed.formUnion(["file_path", "offset", "limit"])
        case "Edit", "MultiEdit":
            title = "파일 수정"
            fields = [field("file_path", "파일", code: true), field("old_string", "바꿀 내용", code: true), field("new_string", "새 내용", code: true), field("replace_all", "모두 바꾸기"), field("edits", "편집 목록", code: true)]
            consumed.formUnion(["file_path", "old_string", "new_string", "replace_all", "edits"])
        case "Write":
            title = "파일 쓰기"
            fields = [field("file_path", "파일", code: true), field("content", "내용", code: true)]
            consumed.formUnion(["file_path", "content"])
        case "NotebookEdit":
            title = "노트북 수정"
            fields = [field("notebook_path", "노트북", code: true), field("cell_id", "셀"), field("edit_mode", "편집 방식"), field("new_source", "새 내용", code: true)]
            consumed.formUnion(["notebook_path", "cell_id", "edit_mode", "new_source", "cell_type"])
        case "Glob", "Grep":
            title = name == "Glob" ? "파일 찾기" : "내용 검색"
            fields = [field("pattern", "패턴", code: true), field("path", "경로", code: true), field("glob", "파일 필터", code: true)]
            consumed.formUnion(["pattern", "path", "glob"])
        case "WebFetch":
            title = "웹 페이지 가져오기"
            fields = [field("url", "주소", code: true), field("prompt", "질문")]
            consumed.formUnion(["url", "prompt"])
        case "WebSearch":
            title = "웹 검색"
            fields = [field("query", "검색어", code: true)]
            consumed.insert("query")
        case "Agent", "Task":
            title = "하위 에이전트 실행"
            fields = [field("subagent_type", "에이전트 종류"), field("model", "모델"), field("prompt", "지시", code: true)]
            consumed.formUnion(["subagent_type", "model", "prompt", "name"])
        default:
            if name.hasPrefix("mcp__") {
                let parts = name.split(separator: "_", omittingEmptySubsequences: true).map(String.init)
                let server = parts.count >= 2 ? parts[1] : name
                title = "MCP 도구 · " + ActivitySupport.clean(server, maximumBytes: 80, singleLine: true)
            } else { title = "도구 실행" }
        }
        // Remaining keys keep their own names so nothing in the input is hidden.
        let preferred = ["command", "file_path", "path", "pattern", "query", "url", "prompt", "content"]
        let rest = input.keys.filter { !consumed.contains($0) }.sorted { lhs, rhs in
            let l = preferred.firstIndex(of: lhs) ?? preferred.count, r = preferred.firstIndex(of: rhs) ?? preferred.count
            return l == r ? lhs < rhs : l < r
        }
        for key in rest { fields.append(field(key, key, code: preferred.contains(key))) }
        return ToolPermissionPresentation(title: title, headline: headline, fields: Array(fields.compactMap { $0 }.prefix(maximumFields)))
    }

    /// Codex's channel rejects oversized requests before they reach the card.
    /// Show every approved command/diff byte here instead of a truncated preview.
    private static func codexPresentation(name: String, input: [String: Any]) -> ToolPermissionPresentation {
        func display(_ raw: Any?) -> String? {
            guard let raw, !(raw is NSNull) else { return nil }
            let text: String
            if let value = raw as? String { text = value }
            else if JSONSerialization.isValidJSONObject(raw),
                    let data = try? JSONSerialization.data(withJSONObject: raw, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) {
                text = String(decoding: data, as: UTF8.self)
            } else { text = String(describing: raw) }
            // Keep line breaks/tab layout, but render other control characters
            // and invisible direction/format marks literally rather than hiding them.
            return text.unicodeScalars.map { scalar in
                let category = scalar.properties.generalCategory
                if category == .format || (category == .control && scalar.value != 10 && scalar.value != 9) {
                    return String(scalar).utf16.map { String(format: "\\u%04x", $0) }.joined()
                }
                return String(scalar)
            }.joined()
        }
        var fields: [ToolPermissionField] = []
        func append(_ raw: Any?, _ label: String, code: Bool = true) {
            if let value = display(raw), !value.isEmpty { fields.append(ToolPermissionField(label: label, value: value, code: code)) }
        }
        var consumed: Set<String> = ["threadId", "turnId", "itemId", "reason"]
        if name == "command_execution" {
            append(input["command"], "명령")
            append(input["cwd"], "작업 폴더")
            if let network = input["networkApprovalContext"] as? [String: Any] {
                append(network["host"], "네트워크 호스트")
                append(network["protocol"], "네트워크 프로토콜")
                let extra = network.filter { $0.key != "host" && $0.key != "protocol" }
                if !extra.isEmpty { append(extra, "추가 네트워크 조건") }
            } else { append(input["networkApprovalContext"], "네트워크 조건") }
            consumed.formUnion(["command", "cwd", "networkApprovalContext"])
        } else {
            append(input["changes"], "파일 변경 전체")
            append(input["grantRoot"], "추가 권한 경로")
            consumed.formUnion(["changes", "grantRoot"])
        }
        append(input["reason"], "승인 요청 이유", code: false)
        let remaining = input.filter { !consumed.contains($0.key) }
        if !remaining.isEmpty { append(remaining, "추가 요청 정보") }
        return ToolPermissionPresentation(title: name == "command_execution" ? "명령 실행" : "파일 수정", fields: fields)
    }

}
