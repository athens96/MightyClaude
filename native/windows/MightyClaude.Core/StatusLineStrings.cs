namespace MightyClaude.Core;

// Korean copy for the status line feature, matching macOS StatusLineView.swift literals exactly.
// WinUI refers to these constants by name; no JSON loader or key table is created.
public static class StatusLineStrings
{
    // Trust prompt (StatusLineView.swift)
    public const string TrustPromptTemplate = "{source}에 statusLine 명령이 있습니다. 이 워크스페이스에서 실행할까요?";
    public const string TrustAllow = "이 워크스페이스에서 허용";
    public const string TrustDeny = "지금은 안 함";
    public const string TrustNote = "저장소가 바꾼 명령은 다시 묻습니다.";

    // Accessibility / pane label
    public const string AccessibilityLabel = "상태 줄";

    // Error messages (StatusLine.swift)
    public const string ErrorStartTemplate = "명령을 시작하지 못했습니다: {reason}";
    public const string ErrorTimeout = "상태 줄 명령이 제한 시간 안에 끝나지 않았습니다.";
    public const string ErrorExitTemplate = "상태 줄 명령이 종료 코드 {code}로 끝났습니다.";

    // Source labels (StatusLine.swift: source display strings)
    public const string SourceWorkspaceLocal = "프로젝트 로컬 설정";
    public const string SourceWorkspace = "프로젝트 설정";
    public const string SourceUser = "사용자 설정";
}
