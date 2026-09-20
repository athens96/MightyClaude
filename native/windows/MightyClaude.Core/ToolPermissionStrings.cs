namespace MightyClaude.Core;

// The Korean copy of the approval bar, mirrored from the macOS client.
// ToolPermissionBar.swift owns the bar copy, ToolPermissions.swift the
// channel notices and ToolPermissionPresentation.swift the titles and
// labels. WinUI reads these constants; it never types Korean of its own.
// StringsVerification checks every value against its macOS literal.
public static class ToolPermissionStrings
{
    // ToolPermissionBar.swift
    public const string BarTitleTemplate = "{title} · 승인 요청";
    public const string BarWaitingCountTemplate = "{count}개 대기";
    public const string BarPathTemplate = "접근 경로: {path}";
    public const string BarRawJson = "원본 JSON";
    public const string BarOnceOnlyNote = "이 요청에만 적용";
    public const string BarCannotAllowHere = "이 요청은 현재 승인 화면에서 허용할 수 없습니다. 거부하거나 실행을 중지하세요.";
    public const string ButtonDeny = "거부";
    public const string ButtonAllowOnce = "이번만 허용";

    // ToolPermissions.swift — notices raised by the channel itself.
    public const string InitializeTimedOut = "Claude 승인 채널 초기화 시간이 초과되었습니다.";
    public const string InitializeFailed = "Claude 승인 채널을 초기화하지 못했습니다.";
    public const string MalformedControlRequest = "Claude 제어 요청 형식이 올바르지 않습니다.";
    public const string TooManyRequestsInRun = "한 실행의 Claude 승인 요청 수 제한을 초과했습니다.";
    public const string UnsupportedDialog = "현재 앱에서 표시할 수 없는 Claude 대화상자 요청입니다. 실행을 중지할 수 있습니다.";
    public const string DeclinedElicitation = "현재 앱에서 지원하지 않는 MCP 입력 요청을 거부했습니다.";
    public const string DeniedMalformedRequest = "형식이 올바르지 않은 도구 승인 요청을 거부했습니다.";
    public const string DeniedTooManyPending = "대기 중인 도구 승인 요청이 16개를 넘어 추가 요청을 거부했습니다.";
    public const string DeniedOversizedInput = "도구 인자가 64 KiB 표시 제한을 넘어 승인하지 않았습니다. 전체 내용을 표시할 수 없는 요청은 허용하지 않습니다.";
    public const string NeedsSeparateInputScreen = "이 도구에는 별도의 입력 화면이 필요합니다. 현재 앱에서는 한 번 허용할 수 없으며 거부하거나 실행을 중지할 수 있습니다.";
    public const string MetadataTooLarge = "승인 설명이 표시 한도를 넘어 허용할 수 없습니다.";
    public const string AlreadySettled = "이미 처리되었거나 종료된 승인 요청입니다.";
    public const string CannotAllow = "이 요청에는 별도의 입력 화면이 필요하거나 전체 내용을 표시할 수 없어 허용할 수 없습니다.";

    // ToolPermissionPresentation.swift — what the tool does.
    public const string TitleBash = "명령 실행";
    public const string TitleRead = "파일 읽기";
    public const string TitleEdit = "파일 수정";
    public const string TitleWrite = "파일 쓰기";
    public const string TitleNotebookEdit = "노트북 수정";
    public const string TitleGlob = "파일 찾기";
    public const string TitleGrep = "내용 검색";
    public const string TitleWebFetch = "웹 페이지 가져오기";
    public const string TitleWebSearch = "웹 검색";
    public const string TitleAgent = "하위 에이전트 실행";
    public const string TitleTool = "도구 실행";
    public const string TitleMcpTemplate = "MCP 도구 · {server}";

    // ToolPermissionPresentation.swift — field labels and boolean values.
    public const string FieldCommand = "명령";
    public const string FieldTimeoutMs = "제한 시간(ms)";
    public const string FieldBackground = "백그라운드 실행";
    public const string FieldFile = "파일";
    public const string FieldOffset = "시작 줄";
    public const string FieldLimit = "줄 수";
    public const string FieldOldString = "바꿀 내용";
    public const string FieldNewString = "새 내용";
    public const string FieldReplaceAll = "모두 바꾸기";
    public const string FieldEdits = "편집 목록";
    public const string FieldContent = "내용";
    public const string FieldNotebook = "노트북";
    public const string FieldCell = "셀";
    public const string FieldEditMode = "편집 방식";
    public const string FieldPattern = "패턴";
    public const string FieldPath = "경로";
    public const string FieldGlob = "파일 필터";
    public const string FieldUrl = "주소";
    public const string FieldQuestion = "질문";
    public const string FieldQuery = "검색어";
    public const string FieldSubagentType = "에이전트 종류";
    public const string FieldModel = "모델";
    public const string FieldInstruction = "지시";
    public const string BooleanYes = "예";
    public const string BooleanNo = "아니요";
}
