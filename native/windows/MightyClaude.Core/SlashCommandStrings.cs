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
}
