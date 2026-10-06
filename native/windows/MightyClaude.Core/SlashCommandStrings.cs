namespace MightyClaude.Core;

// The slash-command palette's copy, read from the shared locale files with the
// keys macOS reads (locales/*.json). WinUI refers to these fields by name.
public static class SlashCommandStrings
{
    // Source badges (SlashCommands.swift: appSource / modelSource / permissionSource)
    public static readonly string AppSource = Locale.Get("slash.source.app");
    public static readonly string ModelSource = Locale.Get("composer.label.model");
    public static readonly string PermissionSource = Locale.Get("composer.label.permission");

    // Discovery badges (SlashCommands.swift: skills/commandFiles/pluginCommands)
    public static readonly string UserSkillSource = Locale.Get("slash.source.userSkill");
    public static readonly string ProjectSkillSource = Locale.Get("slash.source.projectSkill");
    public static readonly string UserCommandSource = Locale.Get("slash.source.userCommand");
    public static readonly string ProjectCommandSource = Locale.Get("slash.source.projectCommand");
    public static readonly string CodexSkillSource = Locale.Get("slash.source.codexSkill");

    // Palette keyboard hints (SlashCommandPalette.swift)
    public static readonly string PaletteMove = Locale.Get("slash.palette.move");
    public static readonly string PaletteSelect = Locale.Get("slash.palette.select");
    public static readonly string PaletteDismiss = Locale.Get("slash.palette.dismiss");
    public static readonly string PaletteCountTemplate = Locale.Get("slash.palette.count");
    public static readonly string PaletteNoDescription = Locale.Get("slash.palette.noDescription");
    public static readonly string PaletteActionTooltip = Locale.Get("slash.palette.actionHelp");
    public static readonly string PaletteArgumentTooltip = Locale.Get("slash.palette.argumentHelp");
    public static readonly string PaletteCurrentSuffix = Locale.Get("slash.currentSuffix");

    // What a built-in says in the pane's log (AppStore+SlashCommands.swift
    // performSlashAction). {name} / {label} is the chosen model or permission
    // mode. The "changed" notes read slash.note.modelChanged and
    // slash.note.permissionChanged through Locale.Get with their values, as macOS does.
    public static readonly string NoteNewConversationRunning = Locale.Get("slash.note.newConversationRunning");
    public static readonly string NoteNewConversationNothingToResume = Locale.Get("remote.error.nothingToResume");
    public static readonly string NoteModelRunning = Locale.Get("slash.note.modelRunning");
    public static readonly string NoteModelAlreadyTemplate = Locale.Get("slash.note.modelAlready");
    public static readonly string NotePermissionRunning = Locale.Get("slash.note.permissionRunning");
    public static readonly string NotePermissionAlreadyTemplate = Locale.Get("slash.note.permissionAlready");
}
