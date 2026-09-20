using MightyClaude.Core;

// The completion list above the composer, proved without a window: every
// decision the WinUI control makes lives in SlashPalette / SlashPaletteState.
internal static class SlashPaletteVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static readonly SlashCommand[] Scanned =
    [
        new("review", "코드 검토", SlashCommandStrings.ProjectSkillSource, SlashCommandOrigin.Project),
        new("deploy", "", SlashCommandStrings.UserCommandSource, SlashCommandOrigin.User),
        new("rename", "스캔된 rename 은 앱 명령에 밀린다", SlashCommandStrings.UserCommandSource, SlashCommandOrigin.User),
    ];

    // Stands in for AppStore.slashArgumentChoices: "/model " lists the models,
    // "/permissions " the permission modes, each invoked as "name value".
    private static SlashCommand[] Choices(SlashArgument argument, string command) => argument switch
    {
        SlashArgument.Model =>
        [
            new(command + " default", "CLI 기본값", SlashCommandStrings.ModelSource, SlashCommandOrigin.App, SlashCommandAction.SetModel, null, "default"),
            new(command + " claude-opus-5", "Opus 5 · 현재", SlashCommandStrings.ModelSource, SlashCommandOrigin.App, SlashCommandAction.SetModel, null, "claude-opus-5"),
            new(command + " claude-sonnet-5", "Sonnet 5", SlashCommandStrings.ModelSource, SlashCommandOrigin.App, SlashCommandAction.SetModel, null, "claude-sonnet-5"),
        ],
        _ =>
        [
            new(command + " manual", "기본 권한", SlashCommandStrings.PermissionSource, SlashCommandOrigin.App, SlashCommandAction.SetPermission, null, "manual"),
            new(command + " plan", "계획", SlashCommandStrings.PermissionSource, SlashCommandOrigin.App, SlashCommandAction.SetPermission, null, "plan"),
        ],
    };

    private static SlashPaletteState State(string draft, string? dismissedFor = null, string kind = "claude", string provider = "claude") =>
        SlashPalette.State(provider, kind, draft, dismissedFor, Scanned, Choices);

    internal static Task Opens()
    {
        var all = State("/");
        Check(all.IsOpen && all.HighlightedIndex == 0, "typing / alone must open the palette on the first row");
        Check(all.Commands.Any(c => c.Invocation == "model") && all.Commands.Any(c => c.Invocation == "review"),
            "the open palette lists built-ins and scanned commands together");
        // A scanned command that shares a built-in's name loses to the built-in.
        Check(all.Commands.Count(c => c.Invocation == "rename") == 1 &&
              all.Commands.First(c => c.Invocation == "rename").Origin == SlashCommandOrigin.App,
            "a scanned command may not shadow a built-in of the same name");
        var typed = State("/re");
        Check(typed.IsOpen && typed.Commands[0].Invocation == "rename" && typed.Commands[1].Invocation == "review",
            "typing filters the list prefix-first, exactly as macOS does");
        Check(State("/rev").Commands.Single().Invocation == "review", "narrowing the query narrows the rows");
        return Task.CompletedTask;
    }

    internal static Task Closes()
    {
        Check(!State("/zzzznope").IsOpen, "a query with no match closes the palette");
        Check(!State("안녕하세요").IsOpen, "a draft that is not a query closes the palette");
        Check(!State("/review 진행해 줘").IsOpen, "a draft that stopped being a query closes the palette");
        Check(!State("/review", kind: "shell").IsOpen, "shell panes have no palette");
        Check(SlashPalette.State("claude", "claude", "/", null, [], Choices).Commands.Any(c => c.Origin == SlashCommandOrigin.App),
              "a pane with nothing scanned yet still lists the built-ins");
        Check(!SlashPalette.State("claude", "claude", "/", null, [], Choices).Commands.Any(c => c.Origin != SlashCommandOrigin.App),
              "a pane with nothing scanned yet lists nothing else");
        Check(SlashPalette.Draft("claude", "/re", null) == "/re", "the palette draft is the draft itself while it is a query");
        Check(SlashPalette.Draft("claude", "안녕", null) is null, "a non-query draft has no palette draft");
        return Task.CompletedTask;
    }

    internal static Task HighlightMoves()
    {
        var state = State("/");
        var count = state.Commands.Length;
        Check(count > 2, "the fixture must have enough rows to move through");
        Check(state.MoveDown().HighlightedIndex == 1, "Down moves to the next row");
        Check(state.MoveUp().HighlightedIndex == count - 1, "Up from the first row wraps to the last");
        Check(state.MoveUp().MoveDown().HighlightedIndex == 0, "Down from the last row wraps to the first");
        // macOS reads the highlight as min(paletteIndex, count - 1).
        var stale = state with { HighlightedIndex = 99 };
        Check(stale.SafeIndex == count - 1 && stale.Highlighted == state.Commands[count - 1],
            "an index past the end is clamped to the last row, never thrown");
        Check(stale.MoveDown().HighlightedIndex == 0, "Down from a clamped index wraps from the last row");
        Check(!SlashPaletteState.Closed.IsOpen && SlashPaletteState.Closed.Highlighted is null &&
              SlashPaletteState.Closed.MoveUp().HighlightedIndex == 0,
            "a closed palette has no highlight and moving does nothing");
        return Task.CompletedTask;
    }

    internal static Task ChoosesPlainCommand()
    {
        var state = State("/rev");
        var choice = state.Choose();
        Check(choice.Effect == SlashChoiceEffect.Insert && choice.Draft == "/review " && choice.Action is null,
            "choosing a plain command leaves /invocation plus a space in the draft");
        // The inserted draft is no longer a query, so the palette closes on it.
        Check(!State(choice.Draft).IsOpen, "the draft a plain command leaves closes the palette");
        return Task.CompletedTask;
    }

    internal static Task ChoosesAppAction()
    {
        var state = State("/clear");
        var choice = state.Choose();
        Check(choice.Effect == SlashChoiceEffect.AppAction && choice.Action == SlashCommandAction.NewConversation && choice.Draft == "",
            "choosing an app action runs it and clears the draft");
        var model = SlashPalette.State("claude", "claude", "/model claude-opus-5", null, Scanned, Choices).Choose();
        Check(model.Effect == SlashChoiceEffect.AppAction && model.Action == SlashCommandAction.SetModel &&
              model.ActionArg == "claude-opus-5" && model.Draft == "",
            "choosing a model carries its value and clears the draft");
        return Task.CompletedTask;
    }

    internal static Task ChoosesArgumentCommand()
    {
        var choice = State("/mod").Choose();
        Check(choice.Effect == SlashChoiceEffect.ArgumentCompletion && choice.Draft == "/model " && choice.Action is null,
            "choosing a built-in that takes an argument leaves /name plus a space");
        // macOS keeps the list open on that built-in's choices.
        var next = State(choice.Draft);
        Check(next.IsOpen && next.Commands.Select(c => c.Invocation).SequenceEqual(
                ["model default", "model claude-opus-5", "model claude-sonnet-5"]),
            "/model continues into argument completion with every model");
        Check(State("/model claude").Commands.Select(c => c.Invocation).SequenceEqual(
                ["model claude-opus-5", "model claude-sonnet-5"]),
            "typing after /model filters the model choices");
        var permission = State("/permissions ");
        Check(permission.IsOpen && permission.Commands.All(c => c.Action == SlashCommandAction.SetPermission),
            "/permissions continues into permission-mode completion");
        Check(!State("/model 없는모델").IsOpen, "an argument query with no match closes the palette");
        Check(!State("/review ").IsOpen, "a command that takes no argument does not continue");
        return Task.CompletedTask;
    }

    internal static Task EscapeClosesWithoutChangingTheDraft()
    {
        const string draft = "/re";
        Check(State(draft).IsOpen, "the palette is open before Esc");
        // Esc records the draft it closed for; the draft itself is untouched.
        var after = State(draft, dismissedFor: draft);
        Check(!after.IsOpen, "Esc closes the palette for the draft it was pressed on");
        Check(draft == "/re", "Esc must not change the draft");
        Check(State("/rev", dismissedFor: draft).IsOpen, "typing on after Esc reopens the palette");
        return Task.CompletedTask;
    }

    internal static Task RowsAndFooterUseMacCopy()
    {
        var state = State("/");
        var review = state.Commands.First(c => c.Invocation == "review");
        var deploy = state.Commands.First(c => c.Invocation == "deploy");
        Check(SlashPalette.Description(review) == "코드 검토", "a row shows its own description");
        Check(SlashPalette.Description(deploy) == SlashCommandStrings.PaletteNoDescription &&
              SlashCommandStrings.PaletteNoDescription == "설명 없음",
            "a row with no description falls back to 설명 없음");
        Check(review.Source == SlashCommandStrings.ProjectSkillSource && deploy.Source == SlashCommandStrings.UserCommandSource,
            "each row carries the source badge it was discovered with");
        Check(SlashPalette.CountLabel(3) == "3개" && SlashPalette.CountLabel(state.Commands.Length) == state.Commands.Length + "개",
            "the footer count uses the {count}개 template");
        Check(SlashCommandStrings.PaletteMove == "↑↓ 이동" && SlashCommandStrings.PaletteSelect == "Enter · Tab 선택" &&
              SlashCommandStrings.PaletteDismiss == "Esc 닫기",
            "the footer hints match the macOS copy");
        Check(SlashCommandStrings.PaletteActionTooltip == "앱에서 바로 실행됩니다" &&
              SlashCommandStrings.PaletteArgumentTooltip == "이어서 선택합니다",
            "the return-arrow and chevron tooltips match the macOS copy");
        // The marks each row draws: ↵ for an app action, › for an argument.
        Check(state.Commands.First(c => c.Invocation == "clear").Action is not null &&
              state.Commands.First(c => c.Invocation == "model").Argument is not null &&
              review.Action is null && review.Argument is null,
            "app actions and argument commands are the only rows that carry a mark");
        return Task.CompletedTask;
    }

    internal static Task LeavesOutActionsWindowsCannotDo()
    {
        // Both Claude and Codex have the plugin window now, so no app action is
        // left out of the Windows palette at all.
        Check(SlashPalette.UnavailableActions.SequenceEqual([]),
            "no action is universally missing; Claude has /plugin and Codex has /plugins");
        foreach (var provider in new[] { "claude", "codex" })
            Check(SlashPalette.Builtins(provider).Any(c => c.Action == SlashCommandAction.OpenPlugins),
                provider + " must offer its plugin row now that the plugin window is built");
        // Gemini has no plugin browser on macOS either.
        Check(!SlashPalette.Builtins("gemini").Any(c => c.Action == SlashCommandAction.OpenPlugins),
            "gemini must not offer a plugin row macOS does not have");
        // Nothing the macOS catalog offers is dropped on the way to the palette.
        foreach (var provider in new[] { "claude", "codex", "gemini" })
            Check(SlashPalette.Builtins(provider).Select(c => c.Invocation)
                    .SequenceEqual(SlashCommandCatalog.Builtins(provider).Select(c => c.Invocation)),
                "the Windows palette leaves no macOS app action out for " + provider);
        Check(SlashCommandCatalog.Builtins("claude").Any(c => c.Action == SlashCommandAction.OpenPlugins),
            "the macOS built-in list itself is left untouched");
        Check(State("/plugin").IsOpen, "/plugin now opens the Claude plugin window");
        Check(SlashPalette.Builtins("codex").Select(c => c.Invocation).SequenceEqual(
                ["plugins", "model", "approvals", "new", "status", "settings", "rename", "help"]),
            "Codex built-ins include /plugins in the macOS order");
        // All Claude built-ins are present; /plugin is back in the macOS order.
        Check(SlashPalette.Builtins("claude").Select(c => c.Invocation).SequenceEqual(
                ["plugin", "model", "permissions", "clear", "cost", "usage", "config", "rename", "help"]),
            "Claude built-ins include /plugin in the macOS order");
        return Task.CompletedTask;
    }

    internal static Task CapsRowsAt60()
    {
        var many = Enumerable.Range(0, 200)
            .Select(i => new SlashCommand($"zz{i:D3}", "", SlashCommandStrings.UserSkillSource, SlashCommandOrigin.User))
            .ToArray();
        var state = SlashPalette.State("claude", "claude", "/zz", null, many, Choices);
        Check(state.Commands.Length == SlashPalette.MaximumRows && SlashPalette.MaximumRows == 60,
            "the palette shows at most the 60 rows macOS shows");
        Check(state.MoveUp().HighlightedIndex == 59, "wrapping uses the capped row count");
        return Task.CompletedTask;
    }
}
