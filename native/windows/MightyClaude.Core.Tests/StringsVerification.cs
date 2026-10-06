using System.Reflection;
using MightyClaude.Core;

// One check over both Korean copy classes. The rules live in Validate so the
// check can prove each rule rejects its own failure before trusting a pass.
internal static class StringsVerification
{
    // ── The shared locale files ────────────────────────────────────────────────
    // The Settings sections carry no Korean of their own any more: every value
    // comes from locales/ko.json through Locale.Get. So the checks that once
    // held the macOS literal now hold the *key* each field must read, and the
    // expected value is looked up in locales/ko.json itself (the file Core
    // embeds straight from the repository root). macOS reads the same key,
    // which is how "Windows copy is the macOS copy" is enforced from here on.

    /// The copy a class must show, built from locales/ko.json: field name → locale key.
    /// A key that is not in the shared file fails here rather than silently
    /// letting the field fall back to the key string.
    private static Dictionary<string, string> FromShared(string className, params (string Field, string Key)[] map)
    {
        var korean = Locale.Catalogue("ko");
        var expected = new Dictionary<string, string>();
        foreach (var (field, key) in map)
        {
            if (!korean.TryGetValue(key, out var value))
                throw new InvalidOperationException(className + "." + field + " reads " + key + ", which locales/ko.json does not have");
            expected[field] = value;
        }
        return expected;
    }

    // The slash palette's fields and the shared keys macOS reads for the same copy.
    // SlashCommands.swift / SlashCommandPalette.swift / AppStore+SlashCommands.swift
    private static readonly Lazy<Dictionary<string, string>> SlashShared = new(() => FromShared(nameof(SlashCommandStrings),
        ("AppSource", "slash.source.app"),
        ("ModelSource", "composer.label.model"),
        ("PermissionSource", "composer.label.permission"),
        ("UserSkillSource", "slash.source.userSkill"),
        ("ProjectSkillSource", "slash.source.projectSkill"),
        ("UserCommandSource", "slash.source.userCommand"),
        ("ProjectCommandSource", "slash.source.projectCommand"),
        ("CodexSkillSource", "slash.source.codexSkill"),
        ("PaletteMove", "slash.palette.move"),
        ("PaletteSelect", "slash.palette.select"),
        ("PaletteDismiss", "slash.palette.dismiss"),
        ("PaletteCountTemplate", "slash.palette.count"),
        ("PaletteNoDescription", "slash.palette.noDescription"),
        ("PaletteActionTooltip", "slash.palette.actionHelp"),
        ("PaletteArgumentTooltip", "slash.palette.argumentHelp"),
        ("PaletteCurrentSuffix", "slash.currentSuffix"),
        ("NoteNewConversationRunning", "slash.note.newConversationRunning"),
        ("NoteNewConversationNothingToResume", "remote.error.nothingToResume"),
        ("NoteModelRunning", "slash.note.modelRunning"),
        ("NoteModelAlreadyTemplate", "slash.note.modelAlready"),
        ("NotePermissionRunning", "slash.note.permissionRunning"),
        ("NotePermissionAlreadyTemplate", "slash.note.permissionAlready")));

    // StatusLineView.swift / StatusLine.swift: the keys macOS reads for the same copy.
    private static readonly Lazy<Dictionary<string, string>> StatusLineShared = new(() => FromShared(nameof(StatusLineStrings),
        ("TrustPromptTemplate", "statusLine.untrustedPrompt"),
        ("TrustAllow", "statusLine.trust"),
        ("TrustDeny", "statusLine.notNow"),
        ("TrustNote", "statusLine.reaskNote"),
        ("AccessibilityLabel", "composer.statusLine.name"),
        ("ErrorStartTemplate", "statusLine.error.start"),
        ("ErrorTimeout", "statusLine.error.timeout"),
        ("ErrorExitTemplate", "statusLine.error.exitCode")));

    // The StatusLineConfig.Source names stay as macOS StatusLine.swift writes them in every
    // language: the saved trust fingerprint hashes this text. Each shows through its key.
    private static readonly Dictionary<string, string> StatusLineSources = new()
    {
        ["SourceWorkspaceLocal"] = "프로젝트 로컬 설정",
        ["SourceWorkspace"] = "프로젝트 설정",
        ["SourceUser"] = "사용자 설정",
    };
    private static readonly (string Field, string Key)[] StatusLineSourceKeys =
    [
        ("SourceWorkspaceLocal", "statusLine.source.projectLocal"),
        ("SourceWorkspace", "statusLine.source.project"),
        ("SourceUser", "statusLine.source.user"),
    ];

    /// Returns the reason the class fails, or null when the copy matches the
    /// source of truth. `source` names it: the macOS literal for the classes that
    /// still carry Korean of their own, and locales/ko.json for the Settings
    /// sections, which now read the shared file.
    private static string? Validate(string className, IReadOnlyDictionary<string, string> actual, IReadOnlyDictionary<string, string> truth, string source = "macOS")
    {
        var seen = new Dictionary<string, string>();
        foreach (var (name, value) in actual)
        {
            if (value.Length == 0) return className + "." + name + " is empty";
            if (seen.TryGetValue(value, out var twin)) return className + "." + name + " duplicates " + twin;
            seen[value] = name;
            if (Placeholders(value) is { } bad) return className + "." + name + " has a placeholder that is not {name}: " + bad;
            if (!truth.TryGetValue(name, out var expected)) return className + "." + name + " mirrors nothing in " + source;
            if (value != expected) return className + "." + name + " differs from " + source + ": " + value;
        }
        foreach (var name in truth.Keys)
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

    private static Dictionary<string, string> LocalizedProperties(Type type) => type
        .GetProperties(BindingFlags.Public | BindingFlags.Static)
        .Where(property => property.PropertyType == typeof(string))
        .ToDictionary(property => property.Name, property => (string)property.GetValue(null)!);

    // For classes that use static readonly (loaded from locale files at runtime).
    private static Dictionary<string, string> StaticReadonlyStrings(Type type) => type
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsInitOnly && f.FieldType == typeof(string))
        .ToDictionary(f => f.Name, f => (string)f.GetValue(null)!);

    internal static Task MatchMacOS()
    {
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        var slash = StaticReadonlyStrings(typeof(SlashCommandStrings));
        var statusLine = LocalizedProperties(typeof(StatusLineStrings));
        var sources = Constants(typeof(StatusLineStrings));
        Check(Validate(nameof(SlashCommandStrings), slash, SlashShared.Value, "locales/ko.json") is null, Validate(nameof(SlashCommandStrings), slash, SlashShared.Value, "locales/ko.json") ?? "");
        Check(Validate(nameof(StatusLineStrings), statusLine, StatusLineShared.Value, "locales/ko.json") is null, Validate(nameof(StatusLineStrings), statusLine, StatusLineShared.Value, "locales/ko.json") ?? "");
        Check(Validate(nameof(StatusLineStrings), sources, StatusLineSources) is null, Validate(nameof(StatusLineStrings), sources, StatusLineSources) ?? "");
        foreach (var (field, key) in StatusLineSourceKeys)
            Check(StatusLineStrings.SourceLabel(sources[field]) == Locale.Get(key), "StatusLineStrings.SourceLabel(" + field + ") must read " + key);

        // Each rule must reject its own failure, or a pass above would mean nothing.
        Dictionary<string, string> Broken(string field, string value) { var copy = new Dictionary<string, string>(slash) { [field] = value }; return copy; }
        string? Bad(Dictionary<string, string> copy) => Validate(nameof(SlashCommandStrings), copy, SlashShared.Value, "locales/ko.json");
        Check(Bad(Broken("ModelSource", "")) is not null, "an empty value must fail");
        Check(Bad(Broken("ModelSource", SlashCommandStrings.AppSource)) is not null, "a duplicate value inside one class must fail");
        Check(Bad(Broken("PaletteCountTemplate", "{ count }개")) is not null, "a placeholder that is not {name} must fail");
        Check(Bad(Broken("PaletteCountTemplate", "{count개")) is not null, "an unclosed placeholder must fail");
        Check(Bad(Broken("ModelSource", "Model")) is not null, "a value that differs from the shared ko copy must fail");
        Check(Bad(new Dictionary<string, string>(slash) { ["Extra"] = "새 문구" }) is not null, "a field with no shared key must fail");
        var missing = new Dictionary<string, string>(slash); missing.Remove("PaletteDismiss");
        Check(Bad(missing) is not null, "a missing literal must fail");
        return Task.CompletedTask;
    }

    // ToolPermissionBar.swift / ToolPermissions.swift / ToolPermissionPresentation.swift:
    // the keys macOS reads for the approval bar, the channel notices and the titles and labels.
    private static readonly Lazy<Dictionary<string, string>> ToolPermissionShared = new(() => FromShared(nameof(ToolPermissionStrings),
        ("BarTitleTemplate", "permission.bar.title"),
        ("BarWaitingCountTemplate", "phone.questionnaire.waiting"),
        ("BarPathTemplate", "permission.bar.blockedPath"),
        ("BarRawJson", "styles.approval.raw"),
        ("BarOnceOnlyNote", "permission.bar.thisRequestOnly"),
        ("BarCannotAllowHere", "permission.bar.cannotAllow"),
        ("ButtonDeny", "permission.deny"),
        ("ButtonAllowOnce", "permission.allowOnce"),
        ("InitializeTimedOut", "claude.channel.initTimeout"),
        ("InitializeFailed", "claude.channel.initFailed"),
        ("MalformedControlRequest", "claude.channel.badRequest"),
        ("TooManyRequestsInRun", "claude.channel.tooManyRequests"),
        ("UnsupportedDialog", "claude.channel.dialogUnsupported"),
        ("DeclinedElicitation", "claude.channel.elicitationDeclined"),
        ("DeniedMalformedRequest", "claude.channel.badToolRequest"),
        ("DeniedTooManyPending", "claude.channel.tooManyPending"),
        ("DeniedOversizedInput", "claude.channel.inputTooLarge"),
        ("NeedsSeparateInputScreen", "claude.channel.needsInteraction"),
        ("MetadataTooLarge", "claude.channel.metadataTooLarge"),
        ("AlreadySettled", "permission.error.requestGone"),
        ("CannotAllow", "claude.channel.cannotAllow"),
        ("TitleBash", "permission.tool.bash"),
        ("TitleRead", "permission.tool.read"),
        ("TitleEdit", "permission.tool.edit"),
        ("TitleWrite", "permission.tool.write"),
        ("TitleNotebookEdit", "permission.tool.notebook"),
        ("TitleGlob", "permission.tool.glob"),
        ("TitleGrep", "permission.tool.grep"),
        ("TitleWebFetch", "permission.tool.webFetch"),
        ("TitleWebSearch", "permission.tool.webSearch"),
        ("TitleAgent", "permission.tool.agent"),
        ("TitleTool", "permission.tool.tool"),
        ("TitleMcpTemplate", "permission.tool.mcp"),
        ("FieldCommand", "permission.field.command"),
        ("FieldTimeoutMs", "permission.field.timeout"),
        ("FieldBackground", "permission.field.runInBackground"),
        ("FieldFile", "permission.field.file"),
        ("FieldOffset", "permission.field.offset"),
        ("FieldLimit", "permission.field.limit"),
        ("FieldOldString", "permission.field.oldString"),
        ("FieldNewString", "permission.field.newString"),
        ("FieldReplaceAll", "permission.field.replaceAll"),
        ("FieldEdits", "permission.field.edits"),
        ("FieldContent", "permission.field.content"),
        ("FieldNotebook", "permission.field.notebook"),
        ("FieldCell", "permission.field.cell"),
        ("FieldEditMode", "permission.field.editMode"),
        ("FieldPattern", "permission.field.pattern"),
        ("FieldPath", "permission.field.path"),
        ("FieldGlob", "permission.field.glob"),
        ("FieldUrl", "permission.field.url"),
        ("FieldQuestion", "permission.field.prompt"),
        ("FieldQuery", "permission.field.query"),
        ("FieldSubagentType", "permission.field.subagentType"),
        ("FieldModel", "permission.field.model"),
        ("FieldInstruction", "permission.field.instructions"),
        ("BooleanYes", "permission.value.yes"),
        ("BooleanNo", "permission.value.no")));

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
        var before = Locale.LanguagePreference; Locale.LanguagePreference = "ko";
        try
        {
            var actual = LocalizedProperties(typeof(CompletionNotificationStrings));
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
        finally { Locale.LanguagePreference = before; }
    }

    // StatusBarUsage.swift (chips, popover, toggle, footnote), SessionUsage.swift
    // RateLimitWindowLabel (the window names) and AccountUsageService.swift
    // (the detail sentences).
    // ToggleLabel, ToggleDescription and SharedLimitsNote carry the Windows
    // values here because the OS-bound substitution (no Keychain on Windows)
    // is documented in docs/windows-account-usage.md.
    private static readonly Dictionary<string, string> AccountUsageMacOS = new()
    {
        ["Title"] = "계정 사용 한도",
        ["ChipsTooltip"] = "계정 사용 한도 · 클릭해 상세 보기",
        ["RefreshTooltip"] = "계정 한도 다시 확인",
        ["RefreshAccessibilityLabel"] = "계정 한도 새로고침",
        ["ChipBeforeFirstRun"] = "실행 후 표시",
        ["ChipChecking"] = "확인 중",
        ["ChipEmpty"] = "—",
        // OS-bound substitutions: Windows has no Keychain.
        ["ToggleLabel"] = "Claude 한도를 직접 조회",
        ["ToggleDescription"] = "끄면 앱이 Anthropic에 직접 조회하지 않습니다. Claude 실행 때 CLI가 보고하는 한도만 표시합니다.",
        ["SharedLimitsNote"] = "계정 한도는 같은 계정을 사용하는 앱·세션에서 공유됩니다. 자동 조회는 직접 조회를 켜기 전에는 일어나지 않습니다.",
        ["UsedPercentTemplate"] = "{percent}% 사용",
        ["ResetTemplate"] = "초기화 {date}",
        ["CheckedAtTemplate"] = "{time} 확인",
        ["LastKnownPrefix"] = "마지막 확인값 · ",
        ["WindowFiveHourSuffix"] = " (5시간)",
        ["WindowSevenDaySuffix"] = " (7일)",
        ["ClaudeBeforeFirstRunNote"] = "Claude를 한 번 실행하면 CLI가 보고한 세션·주간 한도가 여기에 표시됩니다.",
        ["CardChecking"] = "계정 사용 한도를 확인하고 있습니다…",
        ["CardNotCheckedYet"] = "아직 확인하지 않았습니다.",
        ["WindowSession"] = "세션",
        ["WindowWeekly"] = "주간",
        ["WindowDaily"] = "일간",
        ["WindowMonthly"] = "월간",
        ["WindowSpendLimit"] = "지출 한도",
        ["DetailNotCheckedYet"] = "계정 사용량을 아직 확인하지 않았습니다.",
        ["DetailShutdown"] = "계정 조회를 종료했습니다.",
        ["DetailCancelled"] = "계정 조회를 취소했습니다.",
        ["DetailRefreshFailed"] = "계정 사용량을 갱신하지 못했습니다. 잠시 후 다시 확인하세요.",
        ["DetailAuthentication"] = "CLI 로그인을 다시 확인하세요. 계정 한도 조회 권한이 없거나 로그인이 만료되었습니다.",
        ["DetailRateLimited"] = "조회가 제한되었습니다. 잠시 후 자동으로 다시 확인합니다.",
        ["DetailLastKnownSuffix"] = " 마지막으로 확인한 값입니다.",
        ["DetailGeminiUnavailable"] = "Gemini CLI는 이 연결 방식에서 계정 한도를 제공하지 않습니다. CLI의 /stats에서 확인하세요.",
        ["DetailUnsupportedProvider"] = "지원하지 않는 계정입니다.",
        ["DetailCodexNotInstalled"] = "Codex CLI를 설치하고 로그인하세요.",
        ["DetailCodexNeedsChatGPT"] = "ChatGPT로 Codex CLI에 로그인하면 계정 한도를 확인할 수 있습니다.",
        ["DetailCustomAuthentication"] = "사용자 지정 인증의 계정 한도는 CLI에서 확인하세요.",
        ["DetailCodexNoWindows"] = "이 계정에서 사용량 한도 창을 제공하지 않습니다.",
        ["DetailCodex"] = "Codex 계정 한도",
        ["DetailClaudeNoWindows"] = "이 Claude 계정에서 구독 한도를 제공하지 않습니다.",
        ["DetailClaude"] = "Claude 계정 한도",
        ["DetailSessionReportedStale"] = "세션에서 마지막으로 받은 계정 한도입니다.",
        ["DetailSessionReported"] = "실행 중인 세션에서 받은 계정 한도입니다.",
        // 리셋권 rows — shared usage.reset.* locale keys.
        ["ResetTitle"] = "리셋권",
        ["ResetLink"] = "claude.ai에서 리셋",
        ["ResetProgramCedarEmber"] = "지급된 리셋권",
        ["ResetProgramJuniperTide"] = "한도 도달 리셋",
        ["ResetAvailableCedarEmber"] = "남은 리셋권 {count}회 · {expiry}까지",
        ["ResetAvailableJuniperTide"] = "지금 리셋 가능",
        ["ResetHeld"] = "리셋권은 한도에 도달했을 때 쓸 수 있습니다.",
        ["ResetCooldownCedarEmber"] = "{time}부터 다시 쓸 수 있습니다.",
        ["ResetCooldownJuniperTide"] = "{time}부터 가능 · 주 {count}회",
        ["ResetExhausted"] = "이번 기간의 리셋권을 모두 썼습니다.",
        ["ResetNone"] = "현재 사용 가능한 리셋권이 없습니다.",
        ["ResetIneligible"] = "이 계정에는 현재 리셋권이 제공되지 않습니다.",
        ["ResetUnknown"] = "이 앱의 연결 방식에서는 리셋권 정보를 제공하지 않습니다. claude.ai 설정 > 사용량에서 확인하세요.",
    };

    /// The account usage copy is the macOS copy apart from the recorded
    /// Keychain substitutions, and the rules reject their own failures.
    internal static Task AccountUsageStringsMatchMacOS()
    {
        var before = Locale.LanguagePreference; Locale.LanguagePreference = "ko";
        try
        {
            var actual = LocalizedProperties(typeof(AccountUsageStrings));
            var reason = Validate(nameof(AccountUsageStrings), actual, AccountUsageMacOS);
            if (reason is not null) throw new InvalidOperationException(reason);
            string? Bad(Dictionary<string, string> copy) => Validate(nameof(AccountUsageStrings), copy, AccountUsageMacOS);
            Dictionary<string, string> Broken(string field, string value) => new(actual) { [field] = value };
            if (Bad(Broken("Title", "")) is null) throw new InvalidOperationException("an empty value must fail");
            if (Bad(Broken("UsedPercentTemplate", "{ percent }% 사용")) is null) throw new InvalidOperationException("a malformed placeholder must fail");
            if (Bad(Broken("ChipChecking", AccountUsageStrings.ChipBeforeFirstRun)) is null) throw new InvalidOperationException("a duplicate value must fail");
            if (Bad(Broken("WindowWeekly", "Weekly")) is null) throw new InvalidOperationException("a value that differs from macOS must fail");
            var missing = new Dictionary<string, string>(actual); missing.Remove("DetailClaude");
            if (Bad(missing) is null) throw new InvalidOperationException("a missing literal must fail");
            return Task.CompletedTask;
        }
        finally { Locale.LanguagePreference = before; }
    }

    // The CLI update section reads locales/ko.json. SectionDescription reads the
    // ...Windows key (이 PC에 for 이 Mac에, docs/windows-cli-update.md), and the two
    // winget rows are Windows-only 보류 rows in docs/windows-parity.md.
    private static readonly Lazy<Dictionary<string, string>> CliUpdateShared = new(() => FromShared(nameof(CliUpdateStrings),
        ("SectionTitle", "settings.cliUpdate.sectionTitle"),
        ("AutoUpdateToggle", "settings.cliUpdate.autoUpdateToggle"),
        ("SectionDescription", "settings.cliUpdate.sectionDescriptionWindows"),
        ("ProgressInspecting", "settings.cliUpdate.progressInspecting"),
        ("ProgressProviderTemplate", "settings.cliUpdate.progressProviderTemplate"),
        ("LastRunTemplate", "settings.cliUpdate.lastRunTemplate"),
        ("UpdateButton", "settings.cliUpdate.updateButton"),
        ("UpdatingButton", "settings.cliUpdate.updatingButton"),
        ("ResultRowTemplate", "settings.cliUpdate.resultRowTemplate"),
        ("VersionChangeTemplate", "settings.cliUpdate.versionChangeTemplate"),
        ("StatusUpdated", "settings.cliUpdate.statusUpdated"),
        ("StatusCurrent", "settings.cliUpdate.statusCurrent"),
        ("StatusFailed", "settings.cliUpdate.statusFailed"),
        ("StatusCancelled", "settings.cliUpdate.statusCancelled"),
        ("StatusBusy", "settings.cliUpdate.statusBusy"),
        ("StatusSkipped", "settings.cliUpdate.statusSkipped"),
        ("DetailClosing", "settings.cliUpdate.detailClosing"),
        ("DetailInspectCancelled", "settings.cliUpdate.detailInspectCancelled"),
        ("DetailInspectFailed", "settings.cliUpdate.detailInspectFailed"),
        ("DetailBusy", "settings.cliUpdate.detailBusy"),
        ("DetailCancelled", "settings.cliUpdate.detailCancelled"),
        ("DetailUnsupportedProvider", "settings.cliUpdate.detailUnsupportedProvider"),
        ("DetailVersionUnknown", "settings.cliUpdate.detailVersionUnknown"),
        ("DetailMissing", "settings.cliUpdate.detailMissing"),
        ("DetailUnknownMethod", "settings.cliUpdate.detailUnknownMethod"),
        ("DetailNativeClaude", "settings.cliUpdate.detailNativeClaude"),
        ("DetailNpmPrerelease", "settings.cliUpdate.detailNpmPrerelease"),
        ("DetailNpmRuntimeMissing", "settings.cliUpdate.detailNpmRuntimeMissing"),
        ("DetailNpmPlan", "settings.cliUpdate.detailNpmPlan"),
        ("DetailFailedExitTemplate", "settings.cliUpdate.detailFailedExitTemplate"),
        ("DetailVersionRecheckFailed", "settings.cliUpdate.detailVersionRecheckFailed"),
        ("DetailUpdated", "settings.cliUpdate.detailUpdated"),
        ("DetailUnchanged", "settings.cliUpdate.detailUnchanged"),
        ("DetailWingetPlan", "settings.cliUpdate.detailWingetPlan"),
        ("DetailWingetRuntimeMissing", "settings.cliUpdate.detailWingetRuntimeMissing")
    ));

    /// The CLI update copy is the macOS copy apart from the recorded
    /// substitution, and the status labels come from the same table.
    internal static Task CliUpdateStringsMatchMacOS()
    {
        var actual = StaticReadonlyStrings(typeof(CliUpdateStrings));
        var reason = Validate(nameof(CliUpdateStrings), actual, CliUpdateShared.Value, "locales/ko.json");
        if (reason is not null) throw new InvalidOperationException(reason);

        string? Bad(Dictionary<string, string> copy) => Validate(nameof(CliUpdateStrings), copy, CliUpdateShared.Value, "locales/ko.json");
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
        var actual = LocalizedProperties(typeof(ToolPermissionStrings));
        var reason = Validate(nameof(ToolPermissionStrings), actual, ToolPermissionShared.Value, "locales/ko.json");
        if (reason is not null) throw new InvalidOperationException(reason);

        // The same rules must still reject their own failures on this class.
        string? Bad(Dictionary<string, string> copy) => Validate(nameof(ToolPermissionStrings), copy, ToolPermissionShared.Value, "locales/ko.json");
        Dictionary<string, string> Broken(string field, string value) => new(actual) { [field] = value };
        if (Bad(Broken("ButtonAllowOnce", "Allow once")) is null) throw new InvalidOperationException("English copy must fail");
        if (Bad(Broken("BarWaitingCountTemplate", "{ count }개 대기")) is null) throw new InvalidOperationException("a placeholder that is not {name} must fail");
        if (Bad(Broken("ButtonDeny", ToolPermissionStrings.ButtonAllowOnce)) is null) throw new InvalidOperationException("a duplicate value must fail");
        var missing = new Dictionary<string, string>(actual); missing.Remove("BarCannotAllowHere");
        if (Bad(missing) is null) throw new InvalidOperationException("a missing literal must fail");
        return Task.CompletedTask;
    }

    // The CLI accounts section reads locales/ko.json. SectionDescription reads the
    // ...Windows key ("외부 터미널 창에서" for "터미널 실행 창에서", docs/windows-cli-accounts.md).
    private static readonly Lazy<Dictionary<string, string>> CliAccountShared = new(() => FromShared(nameof(CliAccountStrings),
        ("SectionTitle", "settings.cliAccounts.sectionTitle"),
        ("SectionDescription", "settings.cliAccounts.sectionDescriptionWindows"),
        ("StatusChecking", "settings.cliAccounts.statusChecking"),
        ("StatusPending", "settings.cliAccounts.statusPending"),
        ("StatusNotInstalled", "settings.cliAccounts.statusNotInstalled"),
        ("ButtonCancelWait", "settings.cliAccounts.buttonCancelWait"),
        ("ButtonChange", "settings.cliAccounts.buttonChange"),
        ("ButtonLogout", "settings.cliAccounts.buttonLogout"),
        ("ButtonLogin", "settings.cliAccounts.buttonLogin"),
        ("ButtonLoginClaude", "settings.cliAccounts.buttonLoginClaude"),
        ("ButtonLoginConsole", "settings.cliAccounts.buttonLoginConsole"),
        ("ButtonCancel", "settings.cliAccounts.buttonCancel"),
        ("RefreshTooltip", "settings.cliAccounts.refreshTooltip"),
        ("ConfirmLogoutTitleTemplate", "settings.cliAccounts.confirmLogoutTitleTemplate"),
        ("ConfirmChangeTitleTemplate", "settings.cliAccounts.confirmChangeTitleTemplate"),
        ("ConfirmMessageTemplate", "settings.cliAccounts.confirmMessageTemplate"),
        ("SummarySignedOut", "settings.cliAccounts.summarySignedOut"),
        ("SummaryUnknown", "settings.cliAccounts.summaryUnknown"),
        ("SummarySignedIn", "settings.cliAccounts.summarySignedIn"),
        ("DetailClaudeParseError", "settings.cliAccounts.detailClaudeParseError"),
        ("DetailCodexParseError", "settings.cliAccounts.detailCodexParseError"),
        ("DetailGeminiNotInstalled", "settings.cliAccounts.detailGeminiNotInstalled"),
        ("DetailNotInstalledTemplate", "settings.cliAccounts.detailNotInstalledTemplate"),
        ("DetailUnsupportedProvider", "settings.cliAccounts.detailUnsupportedProvider"),
        ("DetailGeminiApiKeyPresent", "settings.cliAccounts.detailGeminiApiKeyPresent"),
        ("DetailGeminiApiKeyAbsent", "settings.cliAccounts.detailGeminiApiKeyAbsent"),
        ("DetailVertexPresent", "settings.cliAccounts.detailVertexPresent"),
        ("DetailVertexAbsent", "settings.cliAccounts.detailVertexAbsent"),
        ("DetailClaudeTimeout", "settings.cliAccounts.detailClaudeTimeout"),
        ("DetailClaudeUnknown", "settings.cliAccounts.detailClaudeUnknown"),
        ("DetailRunFailed", "settings.cliAccounts.detailRunFailed"),
        ("DetailGeminiLogoutFailedTemplate", "settings.cliAccounts.detailGeminiLogoutFailedTemplate")
    ));

    // The app update section reads locales/ko.json; the keys below are the ones
    // each field must read. ManifestUrlHint and NoPublicKeyNotice read the
    // ...Windows keys — the OS-bound rows recorded in docs/windows-parity.md.
    private static readonly Lazy<Dictionary<string, string>> AppUpdateShared = new(() => FromShared(nameof(AppUpdateStrings),
        ("SectionTitle", "settings.appUpdate.sectionTitle"),
        ("CurrentVersionLabel", "settings.appUpdate.currentVersionLabel"),
        ("ManifestUrlPlaceholder", "settings.appUpdate.manifestUrlPlaceholder"),
        ("BuiltInAddressTemplate", "settings.appUpdate.builtInAddressTemplate"),
        ("ManifestUrlHint", "settings.appUpdate.manifestUrlHintWindows"),
        ("AutoCheckToggle", "settings.appUpdate.autoCheckToggle"),
        ("SignatureVerified", "settings.appUpdate.signatureVerified"),
        ("NotCheckedYet", "settings.appUpdate.notCheckedYet"),
        ("LastCheckedTemplate", "settings.appUpdate.lastCheckedTemplate"),
        ("Checking", "settings.appUpdate.checking"),
        ("UpToDate", "settings.appUpdate.upToDate"),
        ("AvailableTemplate", "settings.appUpdate.availableTemplate"),
        ("DownloadingTemplate", "settings.appUpdate.downloadingTemplate"),
        ("StagingProgress", "settings.appUpdate.stagingProgress"),
        ("ReadyTemplate", "settings.appUpdate.readyTemplate"),
        ("Installing", "settings.appUpdate.installing"),
        ("CheckButton", "settings.appUpdate.checkButton"),
        ("DownloadButton", "settings.appUpdate.downloadButton"),
        ("CancelButton", "settings.appUpdate.cancelButton"),
        ("InstallButton", "settings.appUpdate.installButton"),
        ("InProgressButton", "settings.appUpdate.inProgressButton"),
        ("NoPublicKeyNotice", "settings.appUpdate.noPublicKeyNoticeWindows")
    ));

    /// The app update copy matches the macOS literals plus one Windows-only notice.
    internal static Task AppUpdateStringsMatchMacOS()
    {
        var actual = StaticReadonlyStrings(typeof(AppUpdateStrings));
        var reason = Validate(nameof(AppUpdateStrings), actual, AppUpdateShared.Value, "locales/ko.json");
        if (reason is not null) throw new InvalidOperationException(reason);

        string? Bad(Dictionary<string, string> copy) => Validate(nameof(AppUpdateStrings), copy, AppUpdateShared.Value, "locales/ko.json");
        Dictionary<string, string> Broken(string field, string value) => new(actual) { [field] = value };
        if (Bad(Broken("SectionTitle", "")) is null) throw new InvalidOperationException("an empty value must fail");
        if (Bad(Broken("SectionTitle", AppUpdateStrings.UpToDate)) is null) throw new InvalidOperationException("a duplicate value must fail");
        if (Bad(Broken("AvailableTemplate", "새 버전 { version } 이 있습니다.")) is null) throw new InvalidOperationException("a placeholder that is not {name} must fail");
        if (Bad(Broken("CheckButton", "Check for Updates")) is null) throw new InvalidOperationException("English copy must fail");
        var missing = new Dictionary<string, string>(actual); missing.Remove("Installing");
        if (Bad(missing) is null) throw new InvalidOperationException("a missing literal must fail");
        return Task.CompletedTask;
    }

    /// The CLI accounts copy is the macOS copy apart from the one recorded
    /// OS-bound substitution in SectionDescription.
    internal static Task CliAccountStringsMatchMacOS()
    {
        var actual = StaticReadonlyStrings(typeof(CliAccountStrings));
        var reason = Validate(nameof(CliAccountStrings), actual, CliAccountShared.Value, "locales/ko.json");
        if (reason is not null) throw new InvalidOperationException(reason);

        string? Bad(Dictionary<string, string> copy) => Validate(nameof(CliAccountStrings), copy, CliAccountShared.Value, "locales/ko.json");
        Dictionary<string, string> Broken(string field, string value) => new(actual) { [field] = value };
        // The macOS description must fail — "터미널 실행 창" is not the Windows copy.
        if (Bad(Broken("SectionDescription", "로그인은 터미널 실행 창에서 진행됩니다. 앱이 명령을 실행해 두면 CLI가 브라우저를 엽니다. 다른 계정으로 바꿀 때는 브라우저에서 원하는 계정을 고르세요. 바꾼 계정은 다음 요청부터 적용됩니다.")) is null)
            throw new InvalidOperationException("the macOS description must fail; only the Windows wording is accepted");
        if (Bad(Broken("SectionTitle", "")) is null) throw new InvalidOperationException("an empty value must fail");
        if (Bad(Broken("ButtonLogin", CliAccountStrings.ButtonLogout)) is null) throw new InvalidOperationException("a duplicate value must fail");
        if (Bad(Broken("ConfirmMessageTemplate", "{ provider }...")) is null) throw new InvalidOperationException("a placeholder that is not {name} must fail");
        var missing = new Dictionary<string, string>(actual); missing.Remove("SummarySignedOut");
        if (Bad(missing) is null) throw new InvalidOperationException("a missing literal must fail");
        return Task.CompletedTask;
    }
}
