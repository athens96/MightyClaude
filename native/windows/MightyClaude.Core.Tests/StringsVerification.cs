using System.Reflection;
using MightyClaude.Core;

// One check over both Korean copy classes. The rules live in Validate so the
// check can prove each rule rejects its own failure before trusting a pass.
internal static class StringsVerification
{
    // The macOS literal every constant mirrors, by field name.
    // SlashCommands.swift / SlashCommandPalette.swift
    private static readonly Dictionary<string, string> SlashMacOS = new()
    {
        ["AppSource"] = "앱 기능",
        ["ModelSource"] = "모델",
        ["PermissionSource"] = "작업 권한",
        ["UserSkillSource"] = "사용자 스킬",
        ["ProjectSkillSource"] = "프로젝트 스킬",
        ["UserCommandSource"] = "사용자 명령",
        ["ProjectCommandSource"] = "프로젝트 명령",
        ["CodexSkillSource"] = "Codex 스킬",
        ["PaletteMove"] = "↑↓ 이동",
        ["PaletteSelect"] = "Enter · Tab 선택",
        ["PaletteDismiss"] = "Esc 닫기",
        ["PaletteCountTemplate"] = "{count}개",
        ["PaletteNoDescription"] = "설명 없음",
        ["PaletteActionTooltip"] = "앱에서 바로 실행됩니다",
        ["PaletteArgumentTooltip"] = "이어서 선택합니다",
        ["PaletteCurrentSuffix"] = " · 현재",
        // AppStore+SlashCommands.swift performSlashAction
        ["NoteNewConversationRunning"] = "실행이 끝난 뒤에 새 대화로 시작할 수 있습니다.",
        ["NoteNewConversationNothingToResume"] = "이어갈 이전 대화가 없습니다. 다음 입력은 이미 새 대화로 시작합니다.",
        ["NoteModelRunning"] = "실행 중에는 모델을 바꿀 수 없습니다. 실행이 끝난 뒤 다시 고르세요.",
        ["NoteModelAlreadyTemplate"] = "이미 {name} 모델입니다.",
        ["NoteModelChangedTemplate"] = "모델을 {name}{particle} 바꿨습니다. 다음 요청부터 적용됩니다.",
        ["NotePermissionRunning"] = "실행 중에는 작업 권한을 바꿀 수 없습니다. 실행이 끝난 뒤 다시 고르세요.",
        ["NotePermissionAlreadyTemplate"] = "이미 {label} 권한입니다.",
        ["NotePermissionChangedTemplate"] = "작업 권한을 {label}{particle} 바꿨습니다. 다음 요청부터 적용됩니다.",
    };

    // StatusLineView.swift / StatusLine.swift
    private static readonly Dictionary<string, string> StatusLineMacOS = new()
    {
        ["TrustPromptTemplate"] = "{source}에 statusLine 명령이 있습니다. 이 워크스페이스에서 실행할까요?",
        ["TrustAllow"] = "이 워크스페이스에서 허용",
        ["TrustDeny"] = "지금은 안 함",
        ["TrustNote"] = "저장소가 바꾼 명령은 다시 묻습니다.",
        ["AccessibilityLabel"] = "상태 줄",
        ["ErrorStartTemplate"] = "명령을 시작하지 못했습니다: {reason}",
        ["ErrorTimeout"] = "상태 줄 명령이 제한 시간 안에 끝나지 않았습니다.",
        ["ErrorExitTemplate"] = "상태 줄 명령이 종료 코드 {code}로 끝났습니다.",
        ["SourceWorkspaceLocal"] = "프로젝트 로컬 설정",
        ["SourceWorkspace"] = "프로젝트 설정",
        ["SourceUser"] = "사용자 설정",
    };

    /// Returns the reason the class fails, or null when the copy is the macOS copy.
    private static string? Validate(string className, IReadOnlyDictionary<string, string> actual, IReadOnlyDictionary<string, string> macOS)
    {
        var seen = new Dictionary<string, string>();
        foreach (var (name, value) in actual)
        {
            if (value.Length == 0) return className + "." + name + " is empty";
            if (seen.TryGetValue(value, out var twin)) return className + "." + name + " duplicates " + twin;
            seen[value] = name;
            if (Placeholders(value) is { } bad) return className + "." + name + " has a placeholder that is not {name}: " + bad;
            if (!macOS.TryGetValue(name, out var expected)) return className + "." + name + " mirrors no macOS literal";
            if (value != expected) return className + "." + name + " differs from macOS: " + value;
        }
        foreach (var name in macOS.Keys)
            if (!actual.ContainsKey(name)) return className + " is missing " + name;
        return null;
    }

    /// Null when every brace pair in the text is a {name}; otherwise the offending fragment.
    private static string? Placeholders(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '}') return "}";
            if (text[i] != '{') continue;
            var close = text.IndexOf('}', i);
            if (close < 0) return text[i..];
            var name = text[(i + 1)..close];
            if (name.Length == 0 || !char.IsAsciiLetter(name[0]) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return "{" + name + "}";
            i = close;
        }
        return null;
    }

    private static Dictionary<string, string> Constants(Type type) => type
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

    internal static Task MatchMacOS()
    {
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        var slash = Constants(typeof(SlashCommandStrings));
        var statusLine = Constants(typeof(StatusLineStrings));
        Check(Validate(nameof(SlashCommandStrings), slash, SlashMacOS) is null, Validate(nameof(SlashCommandStrings), slash, SlashMacOS) ?? "");
        Check(Validate(nameof(StatusLineStrings), statusLine, StatusLineMacOS) is null, Validate(nameof(StatusLineStrings), statusLine, StatusLineMacOS) ?? "");

        // Each rule must reject its own failure, or a pass above would mean nothing.
        Dictionary<string, string> Broken(string field, string value) { var copy = new Dictionary<string, string>(slash) { [field] = value }; return copy; }
        string? Bad(Dictionary<string, string> copy) => Validate(nameof(SlashCommandStrings), copy, SlashMacOS);
        Check(Bad(Broken("ModelSource", "")) is not null, "an empty value must fail");
        Check(Bad(Broken("ModelSource", SlashCommandStrings.AppSource)) is not null, "a duplicate value inside one class must fail");
        Check(Bad(Broken("PaletteCountTemplate", "{ count }개")) is not null, "a placeholder that is not {name} must fail");
        Check(Bad(Broken("PaletteCountTemplate", "{count개")) is not null, "an unclosed placeholder must fail");
        Check(Bad(Broken("ModelSource", "Model")) is not null, "a value that differs from the macOS literal must fail");
        Check(Bad(new Dictionary<string, string>(slash) { ["Extra"] = "새 문구" }) is not null, "a constant with no macOS literal must fail");
        var missing = new Dictionary<string, string>(slash); missing.Remove("PaletteDismiss");
        Check(Bad(missing) is not null, "a missing literal must fail");
        return Task.CompletedTask;
    }

    // ToolPermissionBar.swift / ToolPermissions.swift / ToolPermissionPresentation.swift
    private static readonly Dictionary<string, string> ToolPermissionMacOS = new()
    {
        ["BarTitleTemplate"] = "{title} · 승인 요청",
        ["BarWaitingCountTemplate"] = "{count}개 대기",
        ["BarPathTemplate"] = "접근 경로: {path}",
        ["BarRawJson"] = "원본 JSON",
        ["BarOnceOnlyNote"] = "이 요청에만 적용",
        ["BarCannotAllowHere"] = "이 요청은 현재 승인 화면에서 허용할 수 없습니다. 거부하거나 실행을 중지하세요.",
        ["ButtonDeny"] = "거부",
        ["ButtonAllowOnce"] = "이번만 허용",
        ["InitializeTimedOut"] = "Claude 승인 채널 초기화 시간이 초과되었습니다.",
        ["InitializeFailed"] = "Claude 승인 채널을 초기화하지 못했습니다.",
        ["MalformedControlRequest"] = "Claude 제어 요청 형식이 올바르지 않습니다.",
        ["TooManyRequestsInRun"] = "한 실행의 Claude 승인 요청 수 제한을 초과했습니다.",
        ["UnsupportedDialog"] = "현재 앱에서 표시할 수 없는 Claude 대화상자 요청입니다. 실행을 중지할 수 있습니다.",
        ["DeclinedElicitation"] = "현재 앱에서 지원하지 않는 MCP 입력 요청을 거부했습니다.",
        ["DeniedMalformedRequest"] = "형식이 올바르지 않은 도구 승인 요청을 거부했습니다.",
        ["DeniedTooManyPending"] = "대기 중인 도구 승인 요청이 16개를 넘어 추가 요청을 거부했습니다.",
        ["DeniedOversizedInput"] = "도구 인자가 64 KiB 표시 제한을 넘어 승인하지 않았습니다. 전체 내용을 표시할 수 없는 요청은 허용하지 않습니다.",
        ["NeedsSeparateInputScreen"] = "이 도구에는 별도의 입력 화면이 필요합니다. 현재 앱에서는 한 번 허용할 수 없으며 거부하거나 실행을 중지할 수 있습니다.",
        ["MetadataTooLarge"] = "승인 설명이 표시 한도를 넘어 허용할 수 없습니다.",
        ["AlreadySettled"] = "이미 처리되었거나 종료된 승인 요청입니다.",
        ["CannotAllow"] = "이 요청에는 별도의 입력 화면이 필요하거나 전체 내용을 표시할 수 없어 허용할 수 없습니다.",
        ["TitleBash"] = "명령 실행",
        ["TitleRead"] = "파일 읽기",
        ["TitleEdit"] = "파일 수정",
        ["TitleWrite"] = "파일 쓰기",
        ["TitleNotebookEdit"] = "노트북 수정",
        ["TitleGlob"] = "파일 찾기",
        ["TitleGrep"] = "내용 검색",
        ["TitleWebFetch"] = "웹 페이지 가져오기",
        ["TitleWebSearch"] = "웹 검색",
        ["TitleAgent"] = "하위 에이전트 실행",
        ["TitleTool"] = "도구 실행",
        ["TitleMcpTemplate"] = "MCP 도구 · {server}",
        ["FieldCommand"] = "명령",
        ["FieldTimeoutMs"] = "제한 시간(ms)",
        ["FieldBackground"] = "백그라운드 실행",
        ["FieldFile"] = "파일",
        ["FieldOffset"] = "시작 줄",
        ["FieldLimit"] = "줄 수",
        ["FieldOldString"] = "바꿀 내용",
        ["FieldNewString"] = "새 내용",
        ["FieldReplaceAll"] = "모두 바꾸기",
        ["FieldEdits"] = "편집 목록",
        ["FieldContent"] = "내용",
        ["FieldNotebook"] = "노트북",
        ["FieldCell"] = "셀",
        ["FieldEditMode"] = "편집 방식",
        ["FieldPattern"] = "패턴",
        ["FieldPath"] = "경로",
        ["FieldGlob"] = "파일 필터",
        ["FieldUrl"] = "주소",
        ["FieldQuestion"] = "질문",
        ["FieldQuery"] = "검색어",
        ["FieldSubagentType"] = "에이전트 종류",
        ["FieldModel"] = "모델",
        ["FieldInstruction"] = "지시",
        ["BooleanYes"] = "예",
        ["BooleanNo"] = "아니요",
    };

    // AgentCompanion.swift CompletionNotifications.send / AgentCompanionViews.swift.
    // ToggleLabel uses the Windows value here because the OS-bound substitution
    // (Windows instead of Mac) is documented in docs/windows-completion-notification.md.
    private static readonly Dictionary<string, string> CompletionNotificationMacOS = new()
    {
        ["NotificationTitle"] = "MightyClaude · 작업 완료",
        ["NotificationBodyTemplate"] = "{title}의 작업이 완료되었습니다.",
        ["ToggleLabel"] = "작업 완료 시 Windows 알림",
        ["StatusAllowed"] = "허용됨",
        ["StatusDenied"] = "시스템 설정에서 알림을 허용하세요",
        ["StatusNeedPermission"] = "권한 필요",
        ["StatusVerificationMode"] = "검증 모드",
        ["SettingsButton"] = "알림 설정",
    };

    internal static Task CompletionNotificationStringsMatchMacOS()
    {
        var actual = Constants(typeof(CompletionNotificationStrings));
        var reason = Validate(nameof(CompletionNotificationStrings), actual, CompletionNotificationMacOS);
        if (reason is not null) throw new InvalidOperationException(reason);

        string? Bad(Dictionary<string, string> copy) => Validate(nameof(CompletionNotificationStrings), copy, CompletionNotificationMacOS);
        Dictionary<string, string> Broken(string field, string value) => new(actual) { [field] = value };
        if (Bad(Broken("NotificationTitle", "")) is null) throw new InvalidOperationException("empty title must fail");
        if (Bad(Broken("NotificationBodyTemplate", "{ title }의 작업이 완료되었습니다.")) is null) throw new InvalidOperationException("malformed placeholder must fail");
        if (Bad(Broken("StatusAllowed", CompletionNotificationStrings.StatusDenied)) is null) throw new InvalidOperationException("duplicate value must fail");
        var missing = new Dictionary<string, string>(actual); missing.Remove("SettingsButton");
        if (Bad(missing) is null) throw new InvalidOperationException("missing literal must fail");
        return Task.CompletedTask;
    }

    // CLIUpdateSettingsView.swift (section, toggle, button, status labels) and
    // CLIUpdateService.swift (every detail sentence).
    // SectionDescription carries the one recorded OS-bound substitution
    // (이 PC에 for 이 Mac에), documented in docs/windows-cli-update.md.
    // DetailWingetPlan and DetailWingetRuntimeMissing have no macOS literal —
    // Windows has no Homebrew — and are 보류 rows in docs/windows-parity.md.
    private static readonly Dictionary<string, string> CliUpdateMacOS = new()
    {
        ["SectionTitle"] = "CLI 업데이트",
        ["AutoUpdateToggle"] = "앱 시작 시 CLI 자동 업데이트",
        ["SectionDescription"] = "이 PC에 설치된 Claude Code·Codex·Gemini CLI를 기존 설치 방식으로 업데이트합니다.",
        ["ProgressInspecting"] = "설치 정보 확인 중…",
        ["ProgressProviderTemplate"] = "{provider} 업데이트 중…",
        ["LastRunTemplate"] = "마지막 실행 {time}",
        ["UpdateButton"] = "업데이트 하기",
        ["UpdatingButton"] = "업데이트 중…",
        ["ResultRowTemplate"] = "{provider} · {status}",
        ["VersionChangeTemplate"] = "{before} → {after}",
        ["StatusUpdated"] = "업데이트 완료",
        ["StatusCurrent"] = "변경 없음",
        ["StatusFailed"] = "업데이트 실패",
        ["StatusCancelled"] = "취소됨",
        ["StatusBusy"] = "다른 업데이트 진행 중",
        ["StatusSkipped"] = "건너뜀",
        ["DetailClosing"] = "앱이 종료 중입니다.",
        ["DetailInspectCancelled"] = "설치 확인을 취소했습니다.",
        ["DetailInspectFailed"] = "CLI 설치 정보를 확인하지 못했습니다.",
        ["DetailBusy"] = "다른 CLI를 업데이트하고 있습니다.",
        ["DetailCancelled"] = "업데이트를 취소했습니다.",
        ["DetailUnsupportedProvider"] = "지원하지 않는 CLI입니다.",
        ["DetailVersionUnknown"] = "CLI 버전을 확인하지 못해 업데이트하지 않았습니다.",
        ["DetailMissing"] = "설치된 CLI가 없어 건너뜁니다. 새로 설치하지 않습니다.",
        ["DetailUnknownMethod"] = "수동 설치 또는 확인할 수 없는 설치 방식입니다. 기존 설치 방법으로 직접 업데이트하세요.",
        ["DetailNativeClaude"] = "Claude Code의 기본 업데이트 명령을 사용합니다.",
        ["DetailNpmPrerelease"] = "시험판 또는 확인할 수 없는 npm 채널은 자동 변경하지 않습니다. 기존 채널에서 직접 업데이트하세요.",
        ["DetailNpmRuntimeMissing"] = "npm 설치는 확인했지만 해당 설치를 업데이트할 Node.js/npm을 찾지 못했습니다.",
        ["DetailNpmPlan"] = "기존 npm 설치 위치에서 공식 패키지만 업데이트합니다.",
        ["DetailFailedExitTemplate"] = "업데이트 명령이 종료 코드 {code}로 실패했습니다. 설치 권한이나 네트워크 상태를 확인하세요.",
        ["DetailVersionRecheckFailed"] = "업데이트 명령은 끝났지만 CLI 버전을 다시 확인하지 못했습니다.",
        ["DetailUpdated"] = "CLI를 업데이트했습니다.",
        ["DetailUnchanged"] = "업데이트 명령을 완료했습니다. 설치된 버전은 동일합니다.",
        // 보류: no macOS literal, recorded in docs/windows-parity.md.
        ["DetailWingetPlan"] = "설치된 winget의 해당 패키지만 업데이트합니다.",
        ["DetailWingetRuntimeMissing"] = "winget 설치이지만 winget 실행 파일을 찾지 못했습니다.",
    };

    /// The CLI update copy is the macOS copy apart from the recorded
    /// substitution, and the status labels come from the same table.
    internal static Task CliUpdateStringsMatchMacOS()
    {
        var actual = Constants(typeof(CliUpdateStrings));
        var reason = Validate(nameof(CliUpdateStrings), actual, CliUpdateMacOS);
        if (reason is not null) throw new InvalidOperationException(reason);

        string? Bad(Dictionary<string, string> copy) => Validate(nameof(CliUpdateStrings), copy, CliUpdateMacOS);
        Dictionary<string, string> Broken(string field, string value) => new(actual) { [field] = value };
        if (Bad(Broken("SectionDescription", "이 Mac에 설치된 Claude Code·Codex·Gemini CLI를 기존 설치 방식으로 업데이트합니다.")) is null)
            throw new InvalidOperationException("an unrecorded OS name must fail");
        if (Bad(Broken("StatusUpdated", "Updated")) is null) throw new InvalidOperationException("English copy must fail");
        if (Bad(Broken("VersionChangeTemplate", "{ before } → {after}")) is null) throw new InvalidOperationException("a placeholder that is not {name} must fail");
        if (Bad(Broken("StatusCurrent", CliUpdateStrings.StatusSkipped)) is null) throw new InvalidOperationException("a duplicate value must fail");
        var missing = new Dictionary<string, string>(actual); missing.Remove("DetailMissing");
        if (Bad(missing) is null) throw new InvalidOperationException("a missing literal must fail");

        // Every status the service can report has its own macOS label.
        foreach (var (status, label) in new[]
        {
            ("updated", CliUpdateStrings.StatusUpdated), ("current", CliUpdateStrings.StatusCurrent),
            ("skipped", CliUpdateStrings.StatusSkipped), ("failed", CliUpdateStrings.StatusFailed),
            ("cancelled", CliUpdateStrings.StatusCancelled), ("busy", CliUpdateStrings.StatusBusy),
        })
            if (CliUpdateStrings.StatusLabel(status) != label) throw new InvalidOperationException("status label for " + status + " changed");
        if (CliUpdateStrings.StatusLabel("something-else") != CliUpdateStrings.StatusSkipped)
            throw new InvalidOperationException("an unknown status must read 건너뜀, as it does on macOS");
        return Task.CompletedTask;
    }

    /// The approval bar copy is the macOS copy, and nothing else is typed anywhere.
    internal static Task ToolPermissionsMatchMacOS()
    {
        var actual = Constants(typeof(ToolPermissionStrings));
        var reason = Validate(nameof(ToolPermissionStrings), actual, ToolPermissionMacOS);
        if (reason is not null) throw new InvalidOperationException(reason);

        // The same rules must still reject their own failures on this class.
        string? Bad(Dictionary<string, string> copy) => Validate(nameof(ToolPermissionStrings), copy, ToolPermissionMacOS);
        Dictionary<string, string> Broken(string field, string value) => new(actual) { [field] = value };
        if (Bad(Broken("ButtonAllowOnce", "Allow once")) is null) throw new InvalidOperationException("English copy must fail");
        if (Bad(Broken("BarWaitingCountTemplate", "{ count }개 대기")) is null) throw new InvalidOperationException("a placeholder that is not {name} must fail");
        if (Bad(Broken("ButtonDeny", ToolPermissionStrings.ButtonAllowOnce)) is null) throw new InvalidOperationException("a duplicate value must fail");
        var missing = new Dictionary<string, string>(actual); missing.Remove("BarCannotAllowHere");
        if (Bad(missing) is null) throw new InvalidOperationException("a missing literal must fail");
        return Task.CompletedTask;
    }
}
