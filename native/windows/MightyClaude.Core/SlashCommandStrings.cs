namespace MightyClaude.Core;

// Korean copy for slash-command palette, matching the macOS client literals exactly.
// WinUI refers to these constants by name; no JSON loader or key table is created.
public static class SlashCommandStrings
{
    // Source badges (SlashCommands.swift: appSource / modelSource / permissionSource)
    public const string AppSource = "앱 기능";
    public const string ModelSource = "모델";
    public const string PermissionSource = "작업 권한";

    // Discovery badges (SlashCommands.swift: skills/commandFiles/pluginCommands)
    public const string UserSkillSource = "사용자 스킬";
    public const string ProjectSkillSource = "프로젝트 스킬";
    public const string UserCommandSource = "사용자 명령";
    public const string ProjectCommandSource = "프로젝트 명령";
    public const string CodexSkillSource = "Codex 스킬";

    // Palette keyboard hints (SlashCommandPalette.swift)
    public const string PaletteMove = "↑↓ 이동";
    public const string PaletteSelect = "Enter · Tab 선택";
    public const string PaletteDismiss = "Esc 닫기";
    public const string PaletteCountTemplate = "{count}개";
    public const string PaletteNoDescription = "설명 없음";
    public const string PaletteActionTooltip = "앱에서 바로 실행됩니다";
    public const string PaletteArgumentTooltip = "이어서 선택합니다";
    public const string PaletteCurrentSuffix = " · 현재";

    // What a built-in says in the pane's log (AppStore+SlashCommands.swift
    // performSlashAction). {name} / {label} is the chosen model or permission
    // mode and {particle} the Korean 로/으로 KoreanParticle.Ro picks for it.
    public const string NoteNewConversationRunning = "실행이 끝난 뒤에 새 대화로 시작할 수 있습니다.";
    public const string NoteNewConversationNothingToResume = "이어갈 이전 대화가 없습니다. 다음 입력은 이미 새 대화로 시작합니다.";
    public const string NoteModelRunning = "실행 중에는 모델을 바꿀 수 없습니다. 실행이 끝난 뒤 다시 고르세요.";
    public const string NoteModelAlreadyTemplate = "이미 {name} 모델입니다.";
    public const string NoteModelChangedTemplate = "모델을 {name}{particle} 바꿨습니다. 다음 요청부터 적용됩니다.";
    public const string NotePermissionRunning = "실행 중에는 작업 권한을 바꿀 수 없습니다. 실행이 끝난 뒤 다시 고르세요.";
    public const string NotePermissionAlreadyTemplate = "이미 {label} 권한입니다.";
    public const string NotePermissionChangedTemplate = "작업 권한을 {label}{particle} 바꿨습니다. 다음 요청부터 적용됩니다.";
}
