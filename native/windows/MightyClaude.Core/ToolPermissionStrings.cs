namespace MightyClaude.Core;

// The approval bar's copy, read from the shared locale files on demand: the same keys the
// macOS client reads. ToolPermissionBar.swift owns the bar copy, ToolPermissions.swift the
// channel notices and ToolPermissionPresentation.swift the titles and labels. WinUI reads
// these properties; it never types copy of its own.
// StringsVerification checks every value against the key macOS reads.
public static class ToolPermissionStrings
{
    // ToolPermissionBar.swift
    public static string BarTitleTemplate => Locale.Get("permission.bar.title");
    public static string BarWaitingCountTemplate => Locale.Get("phone.questionnaire.waiting");
    public static string BarPathTemplate => Locale.Get("permission.bar.blockedPath");
    public static string BarRawJson => Locale.Get("styles.approval.raw");
    public static string BarOnceOnlyNote => Locale.Get("permission.bar.thisRequestOnly");
    public static string BarCannotAllowHere => Locale.Get("permission.bar.cannotAllow");
    public static string ButtonDeny => Locale.Get("permission.deny");
    public static string ButtonAllowOnce => Locale.Get("permission.allowOnce");

    // ToolPermissions.swift: notices raised by the channel itself.
    public static string InitializeTimedOut => Locale.Get("claude.channel.initTimeout");
    public static string InitializeFailed => Locale.Get("claude.channel.initFailed");
    public static string MalformedControlRequest => Locale.Get("claude.channel.badRequest");
    public static string TooManyRequestsInRun => Locale.Get("claude.channel.tooManyRequests");
    public static string UnsupportedDialog => Locale.Get("claude.channel.dialogUnsupported");
    public static string DeclinedElicitation => Locale.Get("claude.channel.elicitationDeclined");
    public static string DeniedMalformedRequest => Locale.Get("claude.channel.badToolRequest");
    public static string DeniedTooManyPending => Locale.Get("claude.channel.tooManyPending");
    public static string DeniedOversizedInput => Locale.Get("claude.channel.inputTooLarge");
    public static string NeedsSeparateInputScreen => Locale.Get("claude.channel.needsInteraction");
    public static string MetadataTooLarge => Locale.Get("claude.channel.metadataTooLarge");
    public static string AlreadySettled => Locale.Get("permission.error.requestGone");
    public static string CannotAllow => Locale.Get("claude.channel.cannotAllow");

    // ToolPermissionPresentation.swift: titles.
    public static string TitleBash => Locale.Get("permission.tool.bash");
    public static string TitleRead => Locale.Get("permission.tool.read");
    public static string TitleEdit => Locale.Get("permission.tool.edit");
    public static string TitleWrite => Locale.Get("permission.tool.write");
    public static string TitleNotebookEdit => Locale.Get("permission.tool.notebook");
    public static string TitleGlob => Locale.Get("permission.tool.glob");
    public static string TitleGrep => Locale.Get("permission.tool.grep");
    public static string TitleWebFetch => Locale.Get("permission.tool.webFetch");
    public static string TitleWebSearch => Locale.Get("permission.tool.webSearch");
    public static string TitleAgent => Locale.Get("permission.tool.agent");
    public static string TitleTool => Locale.Get("permission.tool.tool");
    public static string TitleMcpTemplate => Locale.Get("permission.tool.mcp");

    // ToolPermissionPresentation.swift: field labels and boolean values.
    public static string FieldCommand => Locale.Get("permission.field.command");
    public static string FieldTimeoutMs => Locale.Get("permission.field.timeout");
    public static string FieldBackground => Locale.Get("permission.field.runInBackground");
    public static string FieldFile => Locale.Get("permission.field.file");
    public static string FieldOffset => Locale.Get("permission.field.offset");
    public static string FieldLimit => Locale.Get("permission.field.limit");
    public static string FieldOldString => Locale.Get("permission.field.oldString");
    public static string FieldNewString => Locale.Get("permission.field.newString");
    public static string FieldReplaceAll => Locale.Get("permission.field.replaceAll");
    public static string FieldEdits => Locale.Get("permission.field.edits");
    public static string FieldContent => Locale.Get("permission.field.content");
    public static string FieldNotebook => Locale.Get("permission.field.notebook");
    public static string FieldCell => Locale.Get("permission.field.cell");
    public static string FieldEditMode => Locale.Get("permission.field.editMode");
    public static string FieldPattern => Locale.Get("permission.field.pattern");
    public static string FieldPath => Locale.Get("permission.field.path");
    public static string FieldGlob => Locale.Get("permission.field.glob");
    public static string FieldUrl => Locale.Get("permission.field.url");
    public static string FieldQuestion => Locale.Get("permission.field.prompt");
    public static string FieldQuery => Locale.Get("permission.field.query");
    public static string FieldSubagentType => Locale.Get("permission.field.subagentType");
    public static string FieldModel => Locale.Get("permission.field.model");
    public static string FieldInstruction => Locale.Get("permission.field.instructions");
    public static string BooleanYes => Locale.Get("permission.value.yes");
    public static string BooleanNo => Locale.Get("permission.value.no");
}
