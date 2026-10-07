using MightyClaude.Core;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private CompanionPreferences companionPreferences = new();
    private IReadOnlyList<CompanionPet> companionPets = [];
    private CompanionOverlay? companionOverlay;
    private DispatcherTimer? companionTimer;
    private readonly Dictionary<string, ToolPermissionRequest> companionPermissions = [];
    private readonly Dictionary<string, CompanionQuestionDraft> companionQuestions = [];
    private string? companionPinned, companionShown, companionLoadedPet, companionCardKey, companionAutomatic;
    private string? companionError;
    /// <summary>What went wrong answering the request the bubble shows, by that request's key (M/AgentCompanion.swift:66, 248).</summary>
    private (string Key, string Message)? companionRequestError;
    /// <summary>Whether Windows animates at all: off, the pet stands still, as under the Mac's Reduce Motion (M/AgentCompanionViews.swift:125, 136).</summary>
    private bool companionSystemMotion = true;
    private bool CompanionStill => companionPreferences.ReducedMotion || !companionSystemMotion;
    private DesignPalette? companionPalette;
    private bool companionBubble, companionInitialized;
    private int companionRow, companionTicks;
    private int companionLoadGeneration;
    private ToolPermissionRequest? companionPresentedPermission;
    private DateTimeOffset companionAnimationStart = DateTimeOffset.UtcNow;
    private DateTimeOffset? companionHideAt;
    private (string? Id, DateTimeOffset? Started, bool Completed)? companionIdentity;
    private Window? companionQuestionWindow;
    private string? companionQuestionWindowKey;
    /// <summary>The plan whose answer is on its way (by request key): the bubble's and the plan window's answers are off until it lands.</summary>
    private string? companionPlanSending;
    /// <summary>The open plan window's answers, turned on or off with <see cref="companionPlanSending"/>.</summary>
    private Action? companionPlanWindowSync;
    private sealed class CompanionQuestionDraft(string input)
    {
        internal readonly string Input = input;
        internal int Step;
        internal readonly Dictionary<int, HashSet<string>> Picks = [];
        internal readonly Dictionary<int, string> Custom = [];
    }
    private static string PermissionKey(ToolPermissionRequest request) => request.RunId + "|" + request.Id;
    private async Task InitializeCompanionAsync()
    {
        if (companionInitialized || closing) return;
        companionInitialized = true; companionPreferences = CompanionPreferences.Load(StateDirectory);
        companionBubble = companionPreferences.ShowsTask;
        await ReloadCompanionPets();
        if (closing) return;
        // The sprite plays at 12 frames a second (M/AgentCompanionViews.swift:136); what the bubble says is read once a second.
        companionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 12) };
        var system = new Windows.UI.ViewManagement.UISettings(); companionSystemMotion = system.AnimationsEnabled;
        companionTimer.Tick += (_, _) =>
        {
            if (closing) return;
            try
            {
                // The bubble, and a window it opened, follow the theme toggle with the main window, not a second later.
                var themed = !ReferenceEquals(companionPalette, brushes.Palette);
                if (themed && companionQuestionWindow?.Content is FrameworkElement opened) opened.RequestedTheme = root.RequestedTheme;
                if (++companionTicks % 12 == 0 || themed) { companionPalette = brushes.Palette; companionSystemMotion = system.AnimationsEnabled; RefreshCompanion(); }
                if (companionPreferences.Enabled) DrawCompanion();
            }
            catch (Exception ex) { companionError = ex.Message; companionOverlay?.Dispose(); companionOverlay = null; }
        };
        companionTimer.Start(); RefreshCompanion();
    }
    private async Task ReloadCompanionPets()
    {
        var generation = ++companionLoadGeneration;
        var catalog = await Task.Run(() => CompanionPet.Catalog(Path.Combine(AppContext.BaseDirectory, "Assets", "pets"), StateDirectory,
            options.SmokeTest ? Path.Combine(StateDirectory, "smoke-codex") : null));
        if (generation != companionLoadGeneration || closing) return;
        companionPets = catalog;
        companionOverlay?.Dispose(); companionOverlay = null; companionLoadedPet = null;
        if (!companionPreferences.Enabled || closing) return;
        var selected = companionPets.FirstOrDefault(p => p.Id == companionPreferences.SelectedPet) ?? companionPets.FirstOrDefault();
        // Without a pet image the window still stands, with the paw in its place (M/AgentCompanionViews.swift:137-140).
        byte[] data = []; string? loaded = null; var failure = selected is null ? Locale.Get("companion.error.noPet") : null;
        if (selected is not null)
        {
            try { var pet = await Task.Run(() => CompanionPet.Load(selected.Source, selected.Id)); data = await DecodeCompanionPet(pet); loaded = pet.Id; }
            catch (Exception ex) { failure = ex.Message; }
        }
        if (closing || generation != companionLoadGeneration || !companionPreferences.Enabled) return;
        CompanionOverlay? overlay = null;
        try
        {
            overlay = new CompanionOverlay(companionPreferences); overlay.SetAtlas(data); overlay.Action += CompanionAction;
            overlay.Moved += (left, top) => { companionPreferences = companionPreferences with { Left = left, Top = top }; SaveCompanionPreferences(); };
            overlay.Resized += (width, height) => { companionPreferences = companionPreferences with { BubbleWidth = width, BubbleHeight = height }; SaveCompanionPreferences(); };
            companionOverlay = overlay; companionLoadedPet = loaded; companionError = failure; RefreshCompanion();
            // Its first picture before it shows: a layered window with none takes every click over its whole rectangle.
            DrawCompanion(); overlay.Show(true);
        }
        // A window that could not be finished is taken down with what it holds, not left hidden with no one to draw it.
        catch (Exception ex) { companionError = ex.Message; overlay?.Dispose(); if (ReferenceEquals(companionOverlay, overlay)) companionOverlay = null; }
    }
    /// <summary>One frame of the pet as it now stands.</summary>
    private void DrawCompanion() => companionOverlay?.Draw(companionRow, CompanionPet.Frame(companionRow, (DateTimeOffset.UtcNow - companionAnimationStart).TotalSeconds, CompanionStill), CompanionStill);
    private static async Task<byte[]> DecodeCompanionPet(CompanionPet pet)
    {
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input.GetOutputStreamAt(0))) { writer.WriteBytes(pet.Image); await writer.StoreAsync(); await writer.FlushAsync(); writer.DetachStream(); }
        input.Seek(0);
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(input);
            if (decoder.PixelWidth != pet.Width || decoder.PixelHeight != pet.Height) throw new IOException(Locale.Get("companion.error.decode"));
            var decoded = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var pixels = decoded.DetachPixelData();
            if (pixels.Length != pet.Width * pet.Height * 4 || !Enumerable.Range(0, pet.Width * pet.Height).Any(i => pixels[i * 4 + 3] < 255)) throw new IOException(Locale.Get("companion.error.decode"));
            return pixels;
        }
        catch (Exception ex) when (ex is not IOException) { throw new IOException(Locale.Get("companion.error.decode"), ex); }
    }
    private void SaveCompanionPreferences()
    {
        try { companionPreferences.Save(StateDirectory); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { companionError = ex.Message; }
    }
    private void ReceiveCompanionPermission(ToolPermissionRequest request)
    {
        var key = PermissionKey(request);
        if (request.State == "pending") companionPermissions[key] = request;
        else { companionPermissions.Remove(key); companionQuestions.Remove(key); if (companionRequestError?.Key == key) companionRequestError = null; if (companionQuestionWindowKey == key) companionQuestionWindow?.Close(); }
        RefreshCompanion();
    }
    private void RefreshCompanion()
    {
        var snapshot = service.Snapshot;
        foreach (var (key, request) in companionPermissions.ToArray()) if (!snapshot.Sessions.Any(s => s.Id == request.RunId && s.Status is "running" or "waiting" or "starting"))
        { companionPermissions.Remove(key); companionQuestions.Remove(key); if (companionQuestionWindowKey == key) companionQuestionWindow?.Close(); }
        RefreshCompanionControls();
        if (companionOverlay is null || !companionPreferences.Enabled) return;
        string Status(RunSession s) => companionPermissions.Values.Any(p => p.RunId == s.Id) || s.CurrentActivity?.State == "waiting" ? "waiting" : s.Status;
        var agents = snapshot.Sessions.Where(s => s.Kind == "claude").ToArray();
        var active = agents.Where(s => Status(s) is "running" or "waiting" or "starting" or "queued").Select(s => s.Id).ToArray();
        var automatic = agents.OrderByDescending(s => CompanionAnimation.Priority(Status(s))).ThenByDescending(s => s.RunTiming?.LastObservedAt ?? DateTimeOffset.MinValue).FirstOrDefault();
        companionAutomatic = automatic?.Id;
        companionShown = CompanionCarousel.Shown(companionPinned, active, companionAutomatic);
        if (companionPinned is not null && !active.Contains(companionPinned)) companionPinned = null;
        var current = agents.FirstOrDefault(s => s.Id == companionShown); var status = current is null ? "idle" : Status(current);
        // A new request, or another agent, opens the bubble. A finished request shows its result for six seconds, the pet
        // jumping, and then puts the bubble away; a click on the pet cancels that (M/CompanionBubbleController.swift:4-38,
        // M/AgentCompanionViews.swift:146-155).
        var identity = (current?.Id, current?.RunTiming?.StartedAt, status == "completed");
        if (identity != companionIdentity)
        {
            companionIdentity = identity; companionBubble = current is not null && companionPreferences.ShowsTask;
            companionHideAt = status == "completed" ? DateTimeOffset.UtcNow.AddSeconds(6) : null;
        }
        var row = CompanionAnimation.Row(status, current?.CurrentActivity, companionHideAt > DateTimeOffset.UtcNow);
        if (row != companionRow) { companionRow = row; companionAnimationStart = DateTimeOffset.UtcNow; }
        if (companionHideAt is { } hide && hide <= DateTimeOffset.UtcNow) { companionBubble = false; companionHideAt = null; }
        var permission = companionPermissions.Values.FirstOrDefault(p => p.RunId == current?.Id);
        companionPresentedPermission = permission;
        companionCardKey = permission is null ? companionShown ?? "idle" : PermissionKey(permission) + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(permission.ToolName + permission.InputJson)));
        var position = active.Length > 1 ? CompanionCarousel.Position(companionShown, active) ?? 0 : 0;
        var pending = companionPermissions.Values.Any(p => p.RunId != current?.Id && active.Contains(p.RunId));
        // No agent, no bubble: the pet stands alone until one has something to say. A request shows itself whatever the
        // task bubble's state and leaves that state as it was; the task shows only while its setting is on and the
        // bubble has not been put away (M/AgentCompanionViews.swift:130-134).
        companionOverlay.SetCard(CompanionCard(companionCardKey, snapshot, current, status, permission, position, position > 0 ? active.Length : 0, pending),
            current is not null && (permission is not null || companionBubble && companionPreferences.ShowsTask));
    }
    /// <summary>
    /// What the bubble says about an agent (M/AgentCompanion.swift:162-173, M/AgentCompanionViews.swift:213-245): the
    /// workspace over its title, its state word and clock, the request and the work; or the request waiting on the user.
    /// </summary>
    private CompanionOverlayCard CompanionCard(string key, AppSnapshot snapshot, RunSession? current, string status, ToolPermissionRequest? permission, int position = 0, int count = 0, bool pending = false)
    {
        var workspace = snapshot.Workspaces.FirstOrDefault(w => w.Id == current?.WorkspaceId)?.Name ?? "";
        // The bubble's own word for waiting; every other state takes the pane's (M/AgentCompanionViews.swift:117-119).
        var state = status == "waiting" ? Locale.Get("graph.state.waiting") : StateLabel(status);
        var work = current?.CurrentActivity?.Summary is { Length: > 0 } summary ? summary : status switch {
            "running" => Locale.Get("companion.status.running"), "waiting" => Locale.Get("companion.status.waiting"),
            "completed" => Locale.Get("companion.status.completed"), "error" => Locale.Get("companion.status.error"),
            "stopped" => Locale.Get("companion.status.stopped"), _ => Locale.Get("companion.status.idle") };
        // One line of the last request, as the Mac keeps it (M/AgentCompanion.swift:192-194).
        var request = current?.Logs.LastOrDefault(l => l.Kind == "user") is { } prompt ? ActivitySupport.Clean(prompt.Text, 2048, true) : null;
        return new(key, snapshot.Theme == "light", status, workspace, current?.Title ?? "", state, current?.RunTiming, request, work, position, count, pending,
            permission is null ? null : CompanionRequest(permission, workspace.Length == 0 ? current?.Title ?? "" : workspace + " · " + current?.Title));
    }
    /// <summary>The approval or the question in the bubble (M/AgentCompanionViews.swift:299-373), with the buttons Windows answers it by.</summary>
    private CompanionOverlayRequest CompanionRequest(ToolPermissionRequest permission, string origin)
    {
        var error = companionRequestError is { } failed && failed.Key == PermissionKey(permission) ? failed.Message : null;
        if (permission.CanAnswerPlan && permission.Plan is { } plan)
        {
            // A finished plan (M/AgentCompanionViews.swift CompanionPlanBubble): when it came, its first line and the few
            // after it, and the plan's own answers. Cancel is the plan's cancel, not a generic deny; a change request needs
            // the keyboard, which the pet never takes, so 계획 검토 opens the plan in a window of its own.
            var lines = plan.Split('\n').Select(line => line.Trim().TrimStart('#').Trim()).Where(line => line.Length > 0).Skip(1).Take(3).ToArray();
            // While its answer is on its way the plan's own buttons are off (M/AgentCompanion.swift approvalBusy).
            var sending = companionPlanSending == PermissionKey(permission);
            return new(false, Locale.Get("plan.card.title"), origin, permission.ReceivedAt is { } received ? PlanCardSupport.ReceivedText(received) : "", PlanCardSupport.Headline(plan),
                lines.Length == 0 ? null : string.Join('\n', lines), false, false, [],
                [new("open", Locale.Get("companion.button.open"), Glyph: true), new("plan-cancel", Locale.Get("plan.card.cancel"), Disabled: sending), new("plan-review", Locale.Get("companion.plan.review"), Disabled: sending),
                    new("plan-approve", Locale.Get("plan.card.approveAuto"), Prominent: true, OwnRow: true, Disabled: sending)], error);
        }
        if (permission.CanAnswerQuestions && UserQuestionnaire.Parse(permission.InputJson) is { } questionnaire)
        {
            var draft = CompanionDraft(permission); var step = Math.Clamp(draft.Step, 0, questionnaire.Questions.Count - 1); var question = questionnaire.Questions[step];
            var picks = draft.Picks.GetValueOrDefault(step);
            // "선택 요청 2/3" while stepping through several questions (M/AgentCompanionViews.swift:376-380).
            var progress = Locale.Get("phone.questionnaire.title") + (questionnaire.Questions.Count > 1 ? " " + (step + 1) + "/" + questionnaire.Questions.Count : "");
            // The full questionnaire opens as its own window only after this
            // explicit action; automatic requests never acquire keyboard focus.
            return new(true, progress, origin, "", question.Question, null, false, question.MultiSelect,
                question.Options.Select(option => new CompanionOverlayOption(option.Label, option.Description, picks?.Contains(option.Label) == true)).ToArray(),
                [new("open", Locale.Get("companion.button.open"), Glyph: true), new("questions", Locale.Get("companion.button.answer"), Prominent: true), new("deny", Locale.Get("phone.questionnaire.cancel"))], error);
        }
        var presentation = ToolPermissionPresentation.Make(permission.ToolName, permission.InputJson);
        var code = presentation.Fields.FirstOrDefault(field => field.Code)?.Value;
        var buttons = new List<CompanionOverlayButton> { new("open", Locale.Get("companion.button.open")), new("deny", ToolPermissionStrings.ButtonDeny) };
        // The native preview can be ellipsized at any DPI or bubble size.
        // Approval always opens the complete scrollable request first.
        if (permission.CanAllow) buttons.Add(new("review", Locale.Get("companion.button.review"), Prominent: true));
        return new(false, Locale.Get("companion.approval.title"), origin, presentation.Title + " · " + permission.ToolName, presentation.Headline,
            code ?? (presentation.Headline is null ? permission.Summary : null), code is not null, false, [], buttons, error);
    }
    private CompanionQuestionDraft CompanionDraft(ToolPermissionRequest request)
    {
        var key = PermissionKey(request);
        if (!companionQuestions.TryGetValue(key, out var draft) || draft.Input != request.InputJson) companionQuestions[key] = draft = new(request.InputJson);
        return draft;
    }
    private void CompanionAction(string key, string action)
    {
        if (closing || key != companionCardKey) return;
        if (action == "hide") { companionPreferences = companionPreferences with { Enabled = false }; SaveCompanionPreferences(); companionOverlay?.Show(false); RefreshCompanionControls(); return; }
        if (action == "toggle") { if (!companionPreferences.ShowsTask) { companionPreferences = companionPreferences with { ShowsTask = true }; SaveCompanionPreferences(); companionBubble = true; } else companionBubble = !companionBubble; companionHideAt = null; RefreshCompanion(); return; }
        if (action == "pending") { companionPinned = companionPermissions.Values.FirstOrDefault(p => p.RunId != companionShown)?.RunId; RefreshCompanion(); return; }
        if (action is "previous" or "next")
        {
            var agents = service.Snapshot.Sessions.Where(s => s.Kind == "claude" && (s.Status is "running" or "waiting" or "starting" or "queued" || companionPermissions.Values.Any(p => p.RunId == s.Id))).Select(s => s.Id).ToArray();
            var next = CompanionCarousel.Step(companionShown, agents, action == "next" ? 1 : -1);
            if (next is not null) companionPinned = next == companionAutomatic ? null : next;
            RefreshCompanion(); return;
        }
        // With no agent to turn to, the menu's "에이전트 열기" still brings the window forward (M/AgentCompanion.swift:295-303).
        if (action == "open") { if (companionShown is { } shown) FocusSession(shown); else AppWindow.Show(); return; }
        if (companionPresentedPermission is not { } presented || !companionPermissions.TryGetValue(PermissionKey(presented), out var request)
            || request != presented || request.State != "pending" || request.RunId != companionShown) return;
        if (action == "questions" && request.CanAnswerQuestions) { ShowCompanionQuestions(request); return; }
        if (action == "review" && request.CanAllow && !request.CanAnswerQuestions) { ShowCompanionApproval(request); return; }
        if (request.CanAnswerPlan && request.Plan is not null)
        {
            if (action == "plan-review") ShowCompanionPlan(request);
            else if (action is "plan-cancel" or "plan-approve") _ = AnswerCompanionPlan(request, action == "plan-cancel" ? PlanDecision.Cancel : PlanDecision.ApproveAutoEdit);
            return;
        }
        if (action != "deny") return;
        try { service.RespondToToolPermission(request.RunId, request.Id, false); }
        catch (Exception ex) { companionError = ex.Message; companionRequestError = (PermissionKey(request), ex.Message); RefreshCompanion(); }
    }
    /// <summary>
    /// The frame of a window the pet's bubble opens: the app's page in the app's theme, and on it the wait card
    /// the pane draws for the same request (M/PaneChrome.swift:185-190): the card inside a 2pt amber edge, radius 16, padding 14.
    /// </summary>
    private Grid CompanionWindowFrame(UIElement body) => new()
    {
        RequestedTheme = root.RequestedTheme, Language = WindowLanguage(), Background = WindowBackground(), Padding = new Thickness(DesignMetrics.Spacing.Lg),
        Children = { new Border { Child = body, CornerRadius = new CornerRadius(DesignMetrics.Radius.Composer), BorderThickness = new Thickness(DesignMetrics.Stroke.Active), BorderBrush = brushes.Brush(DesignToken.Wait), Background = brushes.Brush(DesignToken.Card), Padding = new Thickness(DesignMetrics.Spacing.Md) } },
    };
    /// <summary>A button of those windows, as on the pane's question and permission cards (M/PaneChrome.swift:145-169): 12 bold, <see cref="CardButtonHeight"/> high at radius 9; the answer in ink behind the card's colour, the others on the raised card with a line.</summary>
    private Button CompanionCardButton(Button button, bool prominent)
    {
        button.CornerRadius = new CornerRadius(DesignMetrics.Radius.CardButton); button.FontSize = 12; button.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        button.Padding = CardButtonPadding; button.MinHeight = CardButtonHeight; button.Height = CardButtonHeight; button.BorderThickness = new Thickness(prominent ? 0 : DesignMetrics.Stroke.Line);
        var fill = brushes.Brush(prominent ? DesignToken.Ink : DesignToken.CardRaised);
        PaintPlainButton(button, fill, fill, prominent ? null : brushes.Brush(DesignToken.Line), brushes.Brush(prominent ? DesignToken.Card : DesignToken.Ink), brushes.Brush(DesignToken.Ink3));
        return button;
    }
    /// <param name="aside">Shown without being brought forward, for the GUI smoke.</param>
    private void ShowCompanionQuestions(ToolPermissionRequest request, bool aside = false)
    {
        var key = PermissionKey(request);
        if (companionQuestionWindowKey == key && companionQuestionWindow is { } existing) { existing.Activate(); return; }
        companionQuestionWindow?.Close();
        if (UserQuestionnaire.Parse(request.InputJson) is not { } questionnaire) return;
        var draft = CompanionDraft(request); var window = new Window { Title = Locale.Get("phone.questionnaire.title") }; var host = new StackPanel { Spacing = DesignMetrics.Spacing.Md };
        window.Content = CompanionWindowFrame(new ScrollViewer { Content = host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); window.AppWindow.Resize(new(460, 580)); brushes.ApplyTitleBar(window.AppWindow);
        companionQuestionWindow = window; companionQuestionWindowKey = key;
        window.Closed += (_, _) => { if (companionQuestionWindow == window) { companionQuestionWindow = null; companionQuestionWindowKey = null; } };
        void RenderQuestion()
        {
            host.Children.Clear(); draft.Step = Math.Clamp(draft.Step, 0, questionnaire.Questions.Count - 1); var step = draft.Step; var question = questionnaire.Questions[step];
            // The pane's question card in a window of its own (MainWindow.Questionnaire.cs): where it comes from and how far along in the
            // secondary ink at 11, the question at 14 semibold, each choice's label over its description, the error in the error ink.
            var ink = brushes.Brush(DesignToken.Ink); var ink2 = brushes.Brush(DesignToken.Ink2);
            if (request.RunId == companionShown && service.Snapshot.Sessions.FirstOrDefault(s => s.Id == request.RunId)?.Title is { Length: > 0 } asking) host.Children.Add(new TextBlock { Text = asking, FontSize = 11, Foreground = ink2 });
            host.Children.Add(new TextBlock { Text = (step + 1) + " / " + questionnaire.Questions.Count + " · " + question.Header, FontSize = 11, Foreground = ink2 });
            host.Children.Add(new TextBlock { Text = question.Question, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            if (!draft.Picks.TryGetValue(step, out var picks)) draft.Picks[step] = picks = [];
            var radios = new List<CheckBox>();
            TextBox? customInput = null;
            foreach (var option in question.Options)
            {
                var content = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs }; content.Children.Add(new TextBlock { Text = option.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink, TextWrapping = TextWrapping.Wrap }); content.Children.Add(new TextBlock { Text = option.Description, FontSize = 11, Foreground = ink2, TextWrapping = TextWrapping.Wrap });
                var choice = new CheckBox { Content = content, IsChecked = picks.Contains(option.Label), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                radios.Add(choice); choice.Checked += (_, _) => { if (!question.MultiSelect) { picks.Clear(); foreach (var other in radios.Where(r => r != choice)) other.IsChecked = false; draft.Custom.Remove(step); if (customInput is not null) customInput.Text = ""; } picks.Add(option.Label); };
                choice.Unchecked += (_, _) => picks.Remove(option.Label); host.Children.Add(choice);
            }
            var custom = new TextBox { Header = Locale.Get("companion.question.custom"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxLength = 8192, Text = draft.Custom.GetValueOrDefault(step) ?? "" }; customInput = custom;
            custom.TextChanged += (_, _) => { draft.Custom[step] = custom.Text; if (!question.MultiSelect && !string.IsNullOrWhiteSpace(custom.Text)) { picks.Clear(); foreach (var choice in radios) choice.IsChecked = false; } }; host.Children.Add(custom);
            var error = new TextBlock { FontSize = 11, Foreground = brushes.Brush(DesignToken.ErrText), TextWrapping = TextWrapping.Wrap }; host.Children.Add(error);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, HorizontalAlignment = HorizontalAlignment.Right };
            if (step > 0) buttons.Children.Add(CompanionCardButton(Button(Locale.Get("companion.question.back"), () => { draft.Step--; RenderQuestion(); return Task.CompletedTask; }), prominent: false));
            buttons.Children.Add(CompanionCardButton(Button(Locale.Get(step + 1 == questionnaire.Questions.Count ? "companion.question.submit" : "companion.question.next"), () =>
            {
                if (picks.Count == 0 && string.IsNullOrWhiteSpace(draft.Custom.GetValueOrDefault(step))) { error.Text = Locale.Get("questionnaire.error.selection"); return Task.CompletedTask; }
                if (step + 1 < questionnaire.Questions.Count) { draft.Step++; RenderQuestion(); return Task.CompletedTask; }
                try
                {
                    if (!companionPermissions.TryGetValue(key, out var live) || live.State != "pending" || live.InputJson != request.InputJson || !live.CanAnswerQuestions) { window.Close(); return Task.CompletedTask; }
                    var answers = questionnaire.Questions.Select((q, i) => (q.Question, Answer: new UserQuestionAnswer((draft.Picks.GetValueOrDefault(i) ?? []).ToArray(), draft.Custom.GetValueOrDefault(i)))).ToDictionary(x => x.Question, x => x.Answer, StringComparer.Ordinal);
                    questionnaire.ValidateAnswers(answers); service.AnswerQuestionnaire(request.RunId, request.Id, answers); window.Close();
                }
                catch (Exception ex) { error.Text = ex.Message; }
                return Task.CompletedTask;
            }), prominent: true));
            host.Children.Add(buttons);
        }
        RenderQuestion(); if (aside) window.AppWindow.Show(false); else window.Activate();
    }
    /// <param name="aside">Shown without being brought forward, for the GUI smoke.</param>
    private void ShowCompanionApproval(ToolPermissionRequest request, bool aside = false)
    {
        var key = PermissionKey(request); companionQuestionWindow?.Close();
        var window = new Window { Title = ToolPermissionPresentation.Make(request.ToolName, request.InputJson).Title };
        var content = new Grid { RowSpacing = DesignMetrics.Spacing.Md };
        content.RowDefinitions.Add(new() { Height = GridLength.Auto }); content.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        // The pane's permission card in a window of its own (MainWindow.ToolPermission.cs): its title 12 semibold, the whole request in the mono face.
        content.Children.Add(new TextBlock { Text = ToolPermissionStrings.BarTitleTemplate.Replace("{title}", window.Title), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap });
        var text = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(DesignMetrics.Font.Mono), FontSize = DesignMetrics.Type.Mono, Text = request.InputJson };
        var scroll = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); content.Children.Add(scroll);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, HorizontalAlignment = HorizontalAlignment.Right }; var error = new TextBlock { FontSize = 11, Foreground = brushes.Brush(DesignToken.ErrText), TextWrapping = TextWrapping.Wrap };
        Task Respond(bool allow)
        {
            if (!companionPermissions.TryGetValue(key, out var live) || live != request || live.State != "pending" || allow && !live.CanAllow) { window.Close(); return Task.CompletedTask; }
            try { service.RespondToToolPermission(request.RunId, request.Id, allow); window.Close(); } catch (Exception ex) { error.Text = ex.Message; }
            return Task.CompletedTask;
        }
        buttons.Children.Add(CompanionCardButton(Button(ToolPermissionStrings.ButtonDeny, () => Respond(false)), prominent: false)); buttons.Children.Add(CompanionCardButton(Button(ToolPermissionStrings.ButtonAllowOnce, () => Respond(true)), prominent: true));
        var footer = new StackPanel { Spacing = DesignMetrics.Spacing.Sm }; footer.Children.Add(error); footer.Children.Add(buttons); Grid.SetRow(footer, 2); content.Children.Add(footer);
        window.Content = CompanionWindowFrame(content); window.AppWindow.Resize(new(520, 500)); brushes.ApplyTitleBar(window.AppWindow); companionQuestionWindow = window; companionQuestionWindowKey = key;
        window.Closed += (_, _) => { if (companionQuestionWindow == window) { companionQuestionWindow = null; companionQuestionWindowKey = null; } };
        if (aside) window.AppWindow.Show(false); else window.Activate();
    }
    /// <summary>
    /// One of the plan's answers from the bubble or its window (M/AgentCompanion.swift answerPlan), through the service or the
    /// smoke's stand-in, while the request is still the one waiting. The service is called off the UI thread, and while it is
    /// out the bubble's and the plan window's answers are off (<see cref="companionPlanSending"/>), as the pane's card does
    /// (MainWindow.PlanCard.cs AnswerPlanCard). Once sent the request settles in the pane and the bubble at once; an error
    /// stays on the request and is returned.
    /// </summary>
    private async Task<string?> AnswerCompanionPlan(ToolPermissionRequest request, PlanDecision decision)
    {
        var key = PermissionKey(request);
        if (companionPlanSending is not null || !companionPermissions.TryGetValue(key, out var live) || live != request || live.State != "pending" || !live.CanAnswerPlan) return null;
        companionPlanSending = key; companionPlanWindowSync?.Invoke(); RefreshCompanion();
        string? failed = null;
        try
        {
            if (smokePlanAnswerer is { } fake) fake(request.RunId, request.Id, decision);
            else { var runId = request.RunId; var requestId = request.Id; await Task.Run(() => service.AnswerPlan(runId, requestId, decision)); }
        }
        catch (Exception ex) { failed = ex.Message; }
        finally { companionPlanSending = null; companionPlanWindowSync?.Invoke(); }
        if (failed is not null) { companionError = failed; companionRequestError = (key, failed); RefreshCompanion(); return failed; }
        var settled = request with { State = PlanOutcome.PaneMode(decision.Outcome) is null ? "denied" : "allowed" };
        if (views.TryGetValue(request.RunId, out var pane)) pane.ReceiveToolPermission(settled);
        ReceiveCompanionPermission(settled);
        return null;
    }
    /// <summary>
    /// The plan the bubble shows, in a window that takes the keyboard (M/CompanionPlanWindow.swift): the plan's Markdown, a change
    /// request box and the plan's four answers on the wait card. It closes with its request, as the question window does.
    /// </summary>
    /// <param name="aside">Shown without being brought forward, for the GUI smoke.</param>
    private void ShowCompanionPlan(ToolPermissionRequest request, bool aside = false)
    {
        var key = PermissionKey(request);
        if (companionQuestionWindowKey == key && companionQuestionWindow is { } existing) { if (!aside) existing.Activate(); return; }
        companionQuestionWindow?.Close();
        if (request.Plan is not { } plan) return;
        var ink2 = brushes.Brush(DesignToken.Ink2);
        var window = new Window { Title = Locale.Get("plan.card.title") };
        var content = new Grid { RowSpacing = DesignMetrics.Spacing.Md };
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) content.RowDefinitions.Add(new() { Height = height });
        // The pane's plan card in a window of its own (MainWindow.PlanCard.cs): 계획 at 13 bold, when it came and where from in the secondary ink.
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        header.Children.Add(new TextBlock { Text = window.Title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = brushes.Brush(DesignToken.Ink), VerticalAlignment = VerticalAlignment.Center });
        var session = service.Snapshot.Sessions.FirstOrDefault(s => s.Id == request.RunId);
        var place = string.Join(" · ", new[] { request.ReceivedAt is { } at ? PlanCardSupport.ReceivedText(at) : "", session?.Title ?? "" }.Where(part => part.Length > 0));
        header.Children.Add(new TextBlock { Text = place, FontSize = 11, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        content.Children.Add(header);
        // The plan's Markdown. Its RTF bakes the theme's colours in and the box paints its own over them, so it is drawn
        // again in the window's theme once shown and whenever the theme changes (MainWindow.PlanCard.cs PlanMarkdown).
        var document = PaneView.MarkdownView(TranscriptRtf.RenderMarkdown(plan, !DarkTheme));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(document, "pet-plan-text");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(document, window.Title);
        void Rerender() => document.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (document.IsLoaded) PaneView.SetMarkdownRtf(document, TranscriptRtf.RenderMarkdown(plan, !DarkTheme));
        });
        document.ActualThemeChanged += (_, _) => Rerender(); document.Loaded += (_, _) => Rerender();
        var page = new Border { Child = document, CornerRadius = new CornerRadius(DesignMetrics.Radius.Entry), Background = brushes.Brush(DesignToken.CardRaised) };
        Grid.SetRow(page, 1); content.Children.Add(page);
        var revise = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
        var field = new TextBox { AcceptsReturn = true, PlaceholderText = Locale.Get("plan.card.revisePlaceholder"), PlaceholderForeground = brushes.Tertiary, FontSize = 12, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxHeight = 120 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(field, field.PlaceholderText); Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(field, "pet-plan-revise-text");
        var tooLong = new TextBlock { Text = Locale.Get("plan.error.feedbackTooLong"), FontSize = 11, Foreground = brushes.Brush(DesignToken.ErrText), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        revise.Children.Add(field); revise.Children.Add(tooLong);
        Grid.SetRow(revise, 2); content.Children.Add(revise);
        var error = new TextBlock { FontSize = 11, Foreground = brushes.Brush(DesignToken.ErrText), TextWrapping = TextWrapping.Wrap };
        async Task Respond(PlanDecision decision)
        {
            if (companionPlanSending is not null || decision.Kind == "revise" && !PlanCardSupport.CanSendRevise(decision.Feedback)) return;
            if (await AnswerCompanionPlan(request, decision) is { } failed) error.Text = failed;
            else if (companionQuestionWindow == window) window.Close();
        }
        // The four answers as on the pane's card, the accent one last; tiles in one row where the window is wide, two where not.
        var actions = new AdaptiveGridPanel { Minimum = 150, Gap = DesignMetrics.Spacing.Md };
        Button Answer(string label, string automation, Func<Task> action, bool prominent)
        {
            var button = CompanionCardButton(Button(Locale.Get(label), action), prominent); button.HorizontalAlignment = HorizontalAlignment.Stretch;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, automation); actions.Children.Add(button); return button;
        }
        var cancel = Answer("plan.card.cancel", "pet-plan-cancel", () => Respond(PlanDecision.Cancel), false);
        var send = Answer("plan.card.revise", "pet-plan-revise", () => Respond(PlanDecision.Revise(field.Text)), false);
        var confirm = Answer("plan.card.approveConfirm", "pet-plan-approve-confirm", () => Respond(PlanDecision.ApproveConfirmEach), false);
        var approve = Answer("plan.card.approveAuto", "pet-plan-approve-auto", () => Respond(PlanDecision.ApproveAutoEdit), true);
        // Every answer is off while one is on its way, from here or from the bubble; the change request also needs its words.
        void Check()
        {
            var idle = companionPlanSending is null;
            tooLong.Visibility = PlanCardSupport.FeedbackTooLong(field.Text) ? Visibility.Visible : Visibility.Collapsed;
            send.IsEnabled = idle && PlanCardSupport.CanSendRevise(field.Text);
            cancel.IsEnabled = confirm.IsEnabled = approve.IsEnabled = field.IsEnabled = idle;
        }
        field.TextChanged += (_, _) => Check(); Check();
        var footer = new StackPanel { Spacing = DesignMetrics.Spacing.Sm }; footer.Children.Add(error); footer.Children.Add(actions); Grid.SetRow(footer, 3); content.Children.Add(footer);
        window.Content = CompanionWindowFrame(content); window.AppWindow.Resize(new(560, 620)); brushes.ApplyTitleBar(window.AppWindow); companionQuestionWindow = window; companionQuestionWindowKey = key;
        Action sync = Check; companionPlanWindowSync = sync;
        window.Closed += (_, _) =>
        {
            if (companionPlanWindowSync == sync) companionPlanWindowSync = null;
            if (companionQuestionWindow == window) { companionQuestionWindow = null; companionQuestionWindowKey = null; }
        };
        if (aside) window.AppWindow.Show(false); else window.Activate();
    }
    // 펫과 알림 (M/AgentCompanionViews.swift:70-105): the pet switch; the chosen pet's first frame (58×64)
    // beside the pet pop-up over the import, reload and reset buttons; the 10pt note; the bubble, motion
    // and completion-notification switches; the notification state beside the settings button; then the
    // last message. The Mac keeps completion notifications here, so the Windows switch lives here too.
    private StackPanel BuildCompanionSection()
    {
        var rows = new StackPanel();
        var message = SettingsText(companionError ?? "", 11, DesignToken.Ink2); Border messageRow = null!;
        void Say(string? text) { message.Text = text ?? ""; messageRow.Visibility = message.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
        var preview = new Image { Width = 58, Height = 64, VerticalAlignment = VerticalAlignment.Center }; Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(preview, "settings-companion-preview");
        int previewGeneration = 0;
        async Task RefreshPreview()
        {
            var generation = ++previewGeneration;
            var selected = companionPets.FirstOrDefault(p => p.Id == companionPreferences.SelectedPet) ?? companionPets.FirstOrDefault();
            if (selected is null) { preview.Source = null; return; }
            try { var pet = await Task.Run(() => CompanionPet.Load(selected.Source, selected.Id)); var image = await CompanionPreview(pet); if (generation == previewGeneration) preview.Source = image; }
            catch (Exception ex) { if (generation == previewGeneration) { preview.Source = null; companionError = ex.Message; } }
        }
        rows.Loaded += async (_, _) => await RefreshPreview();
        ToggleButton Switch(string label, bool on, string id, Func<bool, Task> changed)
        {
            var toggle = SettingsSwitch(label, on, id);
            async void Changed() => await changed(toggle.IsChecked == true);
            toggle.Checked += (_, _) => Changed(); toggle.Unchecked += (_, _) => Changed();
            SettingsRow(rows, SettingsLabeled(SettingsText(label), toggle));
            return toggle;
        }
        Switch(Locale.Get("companion.settings.enabled"), companionPreferences.Enabled, "settings-companion-enabled", async on =>
        {
            // Turned off, the pet goes and the status bar's paw says so at once; the catalog is read again behind them.
            companionPreferences = companionPreferences with { Enabled = on }; SaveCompanionPreferences();
            if (!on) companionOverlay?.Show(false);
            RefreshCompanionControls(); await ReloadCompanionPets(); RefreshCompanionControls();
        });

        var petLabel = Locale.Get("companion.settings.pet");
        var picker = SettingsPopup(new ComboBox()); picker.HorizontalAlignment = HorizontalAlignment.Right;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(picker, "settings-companion-pet"); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, petLabel);
        void Populate() { picker.Items.Clear(); foreach (var pet in companionPets) picker.Items.Add(new ComboBoxItem { Content = pet.Name + (pet.Id.StartsWith("codex:", StringComparison.Ordinal) ? " · Codex" : ""), Tag = pet.Id }); picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (companionLoadedPet ?? companionPreferences.SelectedPet)); }
        Populate(); picker.SelectionChanged += async (_, _) => { if (picker.SelectedItem is not ComboBoxItem { Tag: string id } || id == companionPreferences.SelectedPet) return; companionPreferences = companionPreferences with { SelectedPet = id }; SaveCompanionPreferences(); await ReloadCompanionPets(); await RefreshPreview(); };
        // The Windows labels are longer than the Mac's and there is one more button, so the row wraps where it must.
        var buttons = new PillWrapPanel();
        buttons.Children.Add(SettingsPush(Button(Locale.Get("companion.settings.import"), async () =>
        {
            var files = new FileOpenPicker(); foreach (var extension in new[] { ".json", ".png", ".webp" }) files.FileTypeFilter.Add(extension); WinRT.Interop.InitializeWithWindow.Initialize(files, WinRT.Interop.WindowNative.GetWindowHandle(settingsWindow ?? this));
            var chosen = await files.PickSingleFileAsync(); if (chosen is null) return;
            try { var pet = await Task.Run(() => CompanionPet.Load(chosen.Path, "import")); await DecodeCompanionPet(pet); var installed = await Task.Run(() => pet.Install(StateDirectory)); companionPreferences = companionPreferences with { SelectedPet = installed.Id }; SaveCompanionPreferences(); await ReloadCompanionPets(); Populate(); await RefreshPreview(); Say(companionError); }
            catch (Exception ex) { Say(ex.Message); }
        }), SettingsControlSize.Small));
        buttons.Children.Add(SettingsPush(Button(Locale.Get("companion.settings.reload"), async () => { await ReloadCompanionPets(); Populate(); await RefreshPreview(); Say(companionError); }), SettingsControlSize.Small));
        buttons.Children.Add(SettingsPush(Button(Locale.Get("companion.settings.resetPosition"), async () => { companionPreferences = companionPreferences with { Left = null, Top = null, BubbleWidth = null, BubbleHeight = null }; SaveCompanionPreferences(); await ReloadCompanionPets(); }), SettingsControlSize.Small));
        var choice = new StackPanel { Spacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
        // The pop-up's chip ends at the row's edge, as the Mac's does; what a ComboBox keeps after it is clear and stands past the edge.
        var petPicker = SettingsPopupFrame(picker); petPicker.Margin = new Thickness(0, 0, -SettingsPopupChipInset, 0);
        choice.Children.Add(SettingsLabeled(SettingsText(petLabel), petPicker)); choice.Children.Add(buttons);
        var petRow = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
        petRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); petRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        petRow.Children.Add(preview); Grid.SetColumn(choice, 1); petRow.Children.Add(choice);
        SettingsRow(rows, petRow);
        SettingsRow(rows, SettingsText(Locale.Get("companion.settings.description"), 10, DesignToken.Ink2));

        Switch(Locale.Get("companion.settings.task"), companionPreferences.ShowsTask, "settings-companion-task", on => { companionPreferences = companionPreferences with { ShowsTask = on }; companionBubble = on; SaveCompanionPreferences(); RefreshCompanion(); return Task.CompletedTask; });
        Switch(Locale.Get("companion.settings.motion"), companionPreferences.ReducedMotion, "settings-companion-motion", on => { companionPreferences = companionPreferences with { ReducedMotion = on }; SaveCompanionPreferences(); return Task.CompletedTask; });
        // One switch for the one setting: the section's own label, as before (the separate notification box showed the same setting a second time).
        Switch(CompletionNotificationStrings.ToggleLabel, service.Snapshot.CompletionNotificationsEnabled, "settings-completion-notifications", async on => { companionPreferences = companionPreferences with { Notifications = on }; SaveCompanionPreferences(); await service.UpdateAsync(s => s with { CompletionNotificationsEnabled = on }); });
        // Whether Windows lets the app notify, beside the way to its notification settings (M/AgentCompanionViews.swift:95-101).
        var permission = notifierPermission switch { NotificationPermission.Allowed => CompletionNotificationStrings.StatusAllowed, NotificationPermission.NeedPermission => CompletionNotificationStrings.StatusNeedPermission, _ => CompletionNotificationStrings.StatusVerificationMode };
        var open = SettingsPush(Button(CompletionNotificationStrings.SettingsButton, async () => await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:notifications"))), SettingsControlSize.Small);
        SettingsRow(rows, SettingsLabeled(SettingsText(permission, 11, DesignToken.Ink2), open));
        messageRow = SettingsRow(rows, message); Say(companionError);
        return rows;
    }
    private void ShutdownCompanion()
    {
        companionLoadGeneration++;
        companionTimer?.Stop(); companionTimer = null; companionQuestionWindow?.Close(); companionOverlay?.Dispose(); companionOverlay = null; companionPermissions.Clear(); companionQuestions.Clear();
    }
    private async Task PrepareCompanionSettingsSmoke()
    {
        if (!options.SmokeTest) throw new InvalidOperationException("Companion fixtures require an isolated smoke profile.");
        companionPets = await Task.Run(() => CompanionPet.Catalog(Path.Combine(AppContext.BaseDirectory, "Assets", "pets"), StateDirectory, Path.Combine(StateDirectory, "smoke-no-codex")));
        Require(companionPets.Count > 0, "The packaged companion catalog is empty.");
        companionPreferences = companionPreferences with { SelectedPet = companionPets[0].Id };
        companionLoadedPet = companionPets[0].Id;
    }
    private async Task<Dictionary<string, object?>> RunCompanionSmoke()
    {
        var pet = CompanionPet.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "pets", "mighty-raccoon"), "mighty-raccoon");
        var pixels = await DecodeCompanionPet(pet);
        var preview = await CompanionPreview(pet); Require(preview.PixelWidth == 192 && preview.PixelHeight == 208, "Companion preview must crop exactly the first atlas frame.");
        using var overlay = new CompanionOverlay(new CompanionPreferences()) { SmokeTexts = [], SmokeKeepsMenu = true }; overlay.SetAtlas(pixels);
        var clicks = new List<string>(); overlay.Action += (key, action) => clicks.Add(key + ":" + action);
        // The pet's window is never the one in front, and nothing it is told to do moves the front window. The front window is
        // compared around each step, not across the stage: the smoke shares its desktop, and another program may come forward
        // while a picture is being written.
        var foreground = CompanionOverlay.ForegroundWindow;
        void Watch() => foreground = CompanionOverlay.ForegroundWindow;
        bool Aside() => overlay.NonActivating && CompanionOverlay.ForegroundWindow != overlay.Handle && !overlay.TookThread;
        bool Kept() => CompanionOverlay.ForegroundWindow == foreground && Aside();
        // The fixture's own agent as the app shows it, waiting on the user: the state of the Mac's picture (docs/design-system/screens/12-pet-dark.webp).
        var snapshot = service.Snapshot; var agent = snapshot.Sessions.First(s => s.Kind == "claude");
        overlay.SetCard(CompanionCard("smoke-pet", snapshot, agent, "waiting", null, 1, 2), true);
        var area = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea; var density = overlay.SmokeGeometry.Scale;
        Require(overlay.SmokeLayout.Right == area.X + area.Width - (int)Math.Round(300 * density) + (int)Math.Ceiling(282 * density) && overlay.SmokeLayout.Bottom == area.Y + area.Height - (int)Math.Round(24 * density),
            "The pet must first stand 300 in from the screen's right edge and 24 above its bottom (M/AgentCompanionViews.swift:438-439).");
        for (var row = 0; row < 9; row++) overlay.Draw(row, CompanionPet.FrameCounts[row] - 1);
        Watch(); overlay.Show(true); Require(Kept(), "Companion stole foreground focus.");
        await Task.Delay(120); Require(Aside(), "Companion stole foreground focus.");
        Watch(); Require(overlay.SmokeClick("next") && overlay.SmokeClick("toggle") && clicks.SequenceEqual(new[] { "smoke-pet:next", "smoke-pet:toggle" }), "Companion pointer action did not match the displayed card.");
        Require(Kept(), "Companion click stole foreground focus.");
        // The pet's menu is the Mac's two items, in its order (M/AgentCompanionViews.swift:144); choosing one acts and puts it away, a click beside it only puts it away.
        Watch(); overlay.SmokeContextMenu(); overlay.Draw(0, 0);
        Require(overlay.SmokeMenu.SequenceEqual(new[] { ("hide", Locale.Get("menu.hidePet")), ("open", Locale.Get("menu.openAgent")) }), "The pet's menu must offer hide and open, in the Mac's order.");
        Require(Kept(), "Companion context actions stole focus or could not close.");
        await CaptureCompanionSmoke(overlay, "smoke-companion-menu-dark.png");
        Watch(); Require(overlay.SmokeClick("open") && clicks.Last() == "smoke-pet:open" && overlay.SmokeMenu.Count == 0 && Kept(), "Companion context actions stole focus or could not close.");
        overlay.SmokeContextMenu(); Require(overlay.SmokeMenu.Count == 2 && overlay.SmokeClick("toggle") && overlay.SmokeMenu.Count == 0 && clicks.Count == 3 && Kept(), "A click beside the pet's menu must only put it away.");
        // One line of request and of work, then two of each: the height follows the content (lineLimit(2), M/AgentCompanionViews.swift:199).
        overlay.SetCard(CompanionCard("smoke-compact", snapshot, agent, agent.Status, null), true);
        var compact = overlay.SmokeLayout;
        var wordy = agent with { Title = string.Join(" ", Enumerable.Repeat("A long pane title", 6)), Logs = [new(Wire.Id(), "user", string.Join(" ", Enumerable.Repeat("Requested task", 30)), Wire.Now())], CurrentActivity = new("smoke-work", agent.Provider, "command", "running", string.Join(" ", Enumerable.Repeat("Current work", 30))) };
        overlay.SetCard(CompanionCard("smoke-layout", snapshot, wordy, "running", null), true); overlay.Draw(7, 0);
        var automatic = overlay.SmokeLayout;
        Require(automatic.AutomaticHeight && automatic.Height != compact.Height && automatic.Height < 480, "Companion default height did not follow measured content.");
        // Nothing is cut through: a long title ends in an ellipsis on its one line, the request and the work on their second (lineLimit, M/AgentCompanionViews.swift:223, 234, 240).
        var longTitle = overlay.SmokeTexts!.Single(text => text.Size == 11 && text.Weight == 600);
        Require(longTitle.Lines == 1 && longTitle.Trimmed && longTitle.Bounds.Right <= overlay.SmokeTexts!.Single(text => text.Size == 9 && text.Weight == 400).Bounds.Left - 14 + 0.01, $"The bubble's long title must end in an ellipsis before the state word; got {longTitle.Lines} lines, trimmed {longTitle.Trimmed}, {longTitle.Bounds}.");
        foreach (var words in overlay.SmokeTexts!.Where(text => text.Size == 11 && text.Weight == 400))
            Require(words.Lines == 2 && words.Trimmed && words.Bounds.Bottom <= overlay.SmokeGeometry.Bubble.Bottom - 12, $"The bubble's long words must end in an ellipsis on their second line, inside the padding; got {words.Lines} lines, trimmed {words.Trimmed}.");
        await CaptureCompanionSmoke(overlay, "smoke-companion-running-dark.png");
        Require(overlay.SmokeResizeHit(1, automatic.Height / 2) == CompanionResizeEdges.Left && overlay.SmokeResizeHit(automatic.Width / 2, 1) == CompanionResizeEdges.Top, "Companion top/side resize strips are missing.");
        Require(overlay.SmokeResizeHit(automatic.Width - 2, automatic.Height - 2) == CompanionResizeEdges.Right && overlay.SmokeResizeHit(automatic.Width / 2, automatic.Height - 2) == CompanionResizeEdges.None, "The bubble's bottom meets the pet and must not resize (M/AgentCompanionViews.swift:484-505).");
        Watch(); overlay.SmokeResize(CompanionResizeEdges.Left, -60, 0); var wider = overlay.SmokeLayout;
        Require(wider.Width == automatic.Width + 60 && wider.Right == automatic.Right && wider.AutomaticHeight, "Side resize moved its opposite edge or disabled automatic height.");
        overlay.SmokeResize(CompanionResizeEdges.Top, 0, -40); var taller = overlay.SmokeLayout;
        Require(taller.Height == wider.Height + 40 && taller.Bottom == wider.Bottom && !taller.AutomaticHeight, "Top resize moved the pet instead of pinning the bottom.");
        overlay.Draw(7, 0); Require(Kept(), "Companion resize acquired foreground focus.");
        await CaptureCompanionSmoke(overlay, "smoke-companion-taller-dark.png");
        Require(overlay.SmokeTexts!.Where(text => text.Size == 11 && text.Weight == 400).Sum(text => text.Lines) > 4, "A taller bubble must show more of the request and the work (M/AgentCompanionViews.swift:198-199).");
        Watch(); overlay.SmokeResize(CompanionResizeEdges.Right, 40, 0, cancel: true); Require(overlay.SmokeLayout == taller, "Cancelled resize changed saved geometry.");
        overlay.SmokeResetSize(); Require(overlay.SmokeLayout.AutomaticHeight && overlay.SmokeLayout.Width == CompanionBubbleLayout.DefaultWidth, "Double-click reset must restore automatic sizing.");
        Require(Kept(), "Companion resize acquired foreground focus.");
        await RunCompanionDesignSmoke(overlay, snapshot, agent, clicks);
        Require(Aside(), "Companion requests acquired foreground focus.");
        // On a display at 150% and at 200% the same card is the same points: only the pixels under them grow.
        foreach (var dpi in new[] { 144, 192 })
        {
            overlay.SmokeDpi(dpi); overlay.SetCard(CompanionCard("smoke-scale-" + dpi, snapshot, agent, "waiting", null) with { Light = false }, true); overlay.Draw(7, 0);
            Require(overlay.SmokeGeometry.Scale == dpi / 96.0 && overlay.CaptureForSmoke().Width == (uint)Math.Ceiling(282 * dpi / 96.0), $"The pet's window must follow a display at {dpi} dpi.");
            Require(Aside(), $"The pet's window acquired foreground focus as its display changed to {dpi} dpi.");
            await CaptureCompanionSmoke(overlay, "smoke-companion-overlay-dark-" + dpi * 100 / 96 + ".png");
            CheckCompanionBubble(overlay, agent, false);
        }
        overlay.SmokeDpi((int)Math.Round(96 * density));
        var captures = new List<string>();
        foreach (var light in new[] { false, true })
        {
            overlay.SetCard(CompanionCard("smoke-design-" + light, snapshot, agent, "waiting", null) with { Light = light }, true);
            overlay.Draw(7, 0); captures.Add(await CaptureCompanionSmoke(overlay, "smoke-companion-overlay-" + (light ? "light" : "dark") + ".png"));
            CheckCompanionBubble(overlay, agent, light);
        }
        // To the end of the stage: not through a change of scale or of theme either.
        Require(Aside(), "The pet's window acquired foreground focus as its scale or theme changed.");
        Require(companionStatusControl is not null && companionStatusFlyout is not null, "Companion keyboard status controls are missing.");
        companionStatusFlyout!.ShowAt(companionStatusControl!);
        try
        {
            await WaitUI(() => companionStatusVisible && companionStatusItems.Count > 0);
            var statusItem = companionStatusItems.First(); var button = statusItem.Value.Button;
            await WaitUI(() => button.IsLoaded && button.ActualWidth > 0);
            Require(button.IsTabStop && button.Focus(FocusState.Keyboard), "Companion status rows are not keyboard focusable.");
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
            Require(peer.GetName().Contains(statusItem.Value.Activity.Text, StringComparison.Ordinal), "Accessible agent status omits its current work.");
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await WaitUI(() => service.Snapshot.ActiveSessionId == statusItem.Key && !companionStatusVisible);
        }
        finally { companionStatusFlyout.Hide(); }
        return new() { ["bundledAtlasDecoded"] = pixels.Length == pet.Width * pet.Height * 4, ["allAnimationRowsRendered"] = true,
            ["nonActivatingWindow"] = true, ["pointerActionBoundToCard"] = true, ["selectedPetPreview"] = true, ["contextMenuNonActivating"] = true,
            ["contentDrivenHeightAndEdgeResize"] = true, ["planBubbleAndWindow"] = true, ["keyboardAccessibleAgentStatus"] = true, ["renderedOverlaySnapshots"] = captures.Count == 2, ["screenshots"] = captures, ["physicalInputTested"] = false };
    }
    private async Task<string> CaptureCompanionSmoke(CompanionOverlay overlay, string name)
    {
        var frame = overlay.CaptureForSmoke();
        Require(frame.Pixels.Length == frame.Width * frame.Height * 4 && frame.Pixels.Any(value => value != 0), "Companion rendered buffer is empty.");
        using var stream = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, frame.Width, frame.Height, 96, 96, frame.Pixels); await encoder.FlushAsync();
        using var input = stream.GetInputStreamAt(0); using var reader = new DataReader(input); await reader.LoadAsync((uint)stream.Size);
        var png = new byte[(int)stream.Size]; reader.ReadBytes(png); var path = Path.Combine(options.ProfileDirectory!, name); await File.WriteAllBytesAsync(path, png); return path;
    }
    /// <summary>
    /// The task bubble as the Mac draws it: the window and the pet's frame (M/ResizeEdges.swift:46-56,
    /// M/AgentCompanionViews.swift:129-145), each text's size, weight and ink (M/AgentCompanionViews.swift:213-245,
    /// M/AgentElapsedView.swift:19), and the card with its round corner and hairline (M/AgentCompanionViews.swift:190-193).
    /// </summary>
    private void CheckCompanionBubble(CompanionOverlay overlay, RunSession agent, bool light)
    {
        var theme = light ? "light" : "dark"; var palette = DesignTokens.Palette(!light);
        var (width, height, bubble, pet, scale) = overlay.SmokeGeometry;
        Require(width == 282 && height == 330, $"the pet's window must be 282 × 330 around a bubble of the default width; got {width} × {height}");
        Require(bubble.X == 12 && bubble.Width == 258 && bubble.Bottom == 185, $"the bubble must stand 12 in from the window's sides and 2 above the pet; got {bubble}");
        Require(pet.X == 78.5 && pet.Y == 187 && pet.Width == 125 && pet.Height == 135, $"the pet's frame must be 125 × 135, centred, 8 above the window's bottom; got {pet}");
        var fonts = CompanionOverlay.SmokeFonts; var texts = overlay.SmokeTexts!;
        Require(DesignMetrics.Font.Body.Split(',').Select(name => name.Trim()).Contains(fonts.Body) && DesignMetrics.Font.Mono.Split(',').Select(name => name.Trim()).Contains(fonts.Mono), $"the bubble must draw in the app's fonts; got {fonts.Body} and {fonts.Mono}");
        CompanionOverlayText Drawn(string value, double size, int weight, DesignToken ink, string what, string? family = null)
        {
            var text = texts.FirstOrDefault(item => item.Value == value) ?? throw new InvalidOperationException($"the pet's bubble ({theme}) does not draw {what}: \"{value}\"");
            Require(text.Size == size && text.Weight == weight && text.Ink == ink && text.Family == (family ?? fonts.Body), $"the pet's bubble ({theme}): {what} must be {size} at weight {weight} in {ink}; got {text.Size} at {text.Weight} in {text.Ink}, {text.Family}");
            Require(text.Bounds.Left >= bubble.Left + 12 - 0.01 && text.Bounds.Right <= bubble.Right - 12 + 0.01 && text.Bounds.Top >= bubble.Top + 12 - 0.01 && text.Bounds.Bottom <= bubble.Bottom - 12 + 0.01, $"the pet's bubble ({theme}): {what} must stay inside the 12 of padding; got {text.Bounds} in {bubble}");
            return text;
        }
        var workspace = Drawn(service.Snapshot.Workspaces.First(w => w.Id == agent.WorkspaceId).Name, 9, 500, DesignToken.Ink2, "the workspace");
        var title = Drawn(agent.Title, 11, 600, DesignToken.Ink, "the title");
        var state = Drawn(Locale.Get("graph.state.waiting"), 9, 400, DesignToken.Ink2, "the state word");
        var clock = Drawn(agent.RunTiming!.Label(), 10, 400, DesignToken.Ink2, "the clock", fonts.Mono);
        var asked = Drawn(Locale.Get("graph.timeline.kind.request"), 9, 500, DesignToken.Ink2, "the request's label");
        var request = Drawn(ActivitySupport.Clean(agent.Logs.Last(entry => entry.Kind == "user").Text, 2048, true), 11, 400, DesignToken.Ink, "the request");
        var doing = Drawn(Locale.Get("transcript.tool.fallback"), 9, 500, DesignToken.Ink2, "the work's label");
        var work = Drawn(Locale.Get("companion.status.waiting"), 11, 400, DesignToken.Ink, "the work");
        // The header's columns: 15 for the mark and 7 before the names; the state and the clock end at the padding.
        Require(Math.Abs(workspace.Bounds.Left - (bubble.Left + 12 + 15 + 7)) < 0.01 && Math.Abs(title.Bounds.Left - workspace.Bounds.Left) < 0.01 && Math.Abs(title.Bounds.Top - workspace.Bounds.Bottom - 1) < 0.01,
            $"the pet's bubble ({theme}): the workspace must stand 1 above the title, after the mark; got {workspace.Bounds} and {title.Bounds}");
        Require(Math.Abs(state.Bounds.Right - (bubble.Right - 12)) < 0.01 && Math.Abs(clock.Bounds.Right - state.Bounds.Right) < 0.01 && Math.Abs(clock.Bounds.Top - state.Bounds.Bottom - 2) < 0.01,
            $"the pet's bubble ({theme}): the state word must stand 2 above the clock, both ending at the padding; got {state.Bounds} and {clock.Bounds}");
        // Each label starts the row, two below the top of its words, which follow 6 after it; the request is above the work.
        Require(Math.Abs(asked.Bounds.Left - (bubble.Left + 12)) < 0.01 && Math.Abs(asked.Bounds.Top - request.Bounds.Top - 2) < 0.01 && Math.Abs(request.Bounds.Left - Math.Ceiling(asked.Bounds.Right) - 6) < 0.01
            && Math.Abs(doing.Bounds.Top - work.Bounds.Top - 2) < 0.01 && request.Bounds.Bottom < work.Bounds.Top && request.Bounds.Top > title.Bounds.Bottom,
            $"the pet's bubble ({theme}): the request and the work must each follow their label; got {asked.Bounds} {request.Bounds}, {doing.Bounds} {work.Bounds}");
        Require(request.Lines <= 2 && work.Lines <= 2 && !request.Trimmed, $"the pet's bubble ({theme}): a short request must be whole, in two lines at most; got {request.Lines} lines, trimmed {request.Trimmed}");
        var frame = overlay.CaptureForSmoke();
        (byte B, byte G, byte R, byte A) At(double x, double y) { var index = ((int)(y * scale) * (int)frame.Width + (int)(x * scale)) * 4; return (frame.Pixels[index], frame.Pixels[index + 1], frame.Pixels[index + 2], frame.Pixels[index + 3]); }
        Require(At(bubble.Left + 5, bubble.Top + bubble.Height / 2) == (palette.Card.B, palette.Card.G, palette.Card.R, (byte)255), $"the pet's bubble ({theme}) must be the card colour {palette.Card.Hex}; got {At(bubble.Left + 5, bubble.Top + bubble.Height / 2)}");
        Require(At(bubble.Left + 1, bubble.Top + 1).A == 0 && At(bubble.Right - 2, bubble.Top + 1).A == 0 && At(bubble.Left + 1, bubble.Bottom - 2).A == 0 && At(bubble.Left + 7, bubble.Top + 7).A == 255,
            $"the pet's bubble ({theme}) must have corners of radius 18");
        for (var x = bubble.Left; x < bubble.Right; x += 6) Require(At(x, bubble.Bottom + 0.5).A == 0 && At(x, bubble.Bottom + 1.5).A == 0, $"the pet's bubble ({theme}) must leave 2 clear above the pet's frame");
        if (scale == Math.Floor(scale))
        {
            // The hairline: the primary ink at 10% on the card.
            var edge = At(bubble.Left + bubble.Width / 2, bubble.Top); byte Mixed(byte card, byte ink) => (byte)Math.Round(card + (ink - card) * 26 / 255.0);
            Require(edge.A == 255 && Math.Abs(edge.R - Mixed(palette.Card.R, palette.Ink.R)) <= 2 && Math.Abs(edge.G - Mixed(palette.Card.G, palette.Ink.G)) <= 2 && Math.Abs(edge.B - Mixed(palette.Card.B, palette.Ink.B)) <= 2,
                $"the pet's bubble ({theme}) must have a hairline of the ink at 10%; got {edge}");
        }
    }
    /// <summary>
    /// The bubble's other faces against the Mac: the pager under it while several agents are busy
    /// (M/AgentCompanionViews.swift:254-295), and an approval and a question in the taller window (M:299-373,
    /// M/ResizeEdges.swift:56-65). Each is drawn in both themes and its buttons answer for the card they are on.
    /// </summary>
    private async Task RunCompanionDesignSmoke(CompanionOverlay overlay, AppSnapshot snapshot, RunSession agent, List<string> clicks)
    {
        var approval = new ToolPermissionRequest("smoke-approval", agent.Id, "smoke-tool", "Bash",
            "{\"command\":\"dotnet test native/windows/MightyClaude.Core.Tests -c Release --no-build --logger trx\",\"description\":\"Run the Windows core tests before the commit\"}", "dotnet test");
        var question = new ToolPermissionRequest("smoke-question", agent.Id, "smoke-ask", "AskUserQuestion",
            "{\"questions\":[{\"header\":\"Layout\",\"question\":\"Which layout should the pet's bubble follow?\",\"multiSelect\":true,\"options\":[{\"label\":\"The Mac's\",\"description\":\"The same order, type and paddings\"},{\"label\":\"The earlier Windows card\",\"description\":\"\"}]},"
            + "{\"header\":\"Next\",\"question\":\"And after that?\",\"multiSelect\":false,\"options\":[{\"label\":\"Ship it\",\"description\":\"\"},{\"label\":\"Look again\",\"description\":\"\"}]}]}", "question", CanAllow: false, CanAnswerQuestions: true);
        var plan = new ToolPermissionRequest("smoke-plan", agent.Id, "smoke-exit-plan", ClaudePlanMode.ToolName,
            "{\"plan\":\"# Ship the pet's plan bubble\\n\\n1. Draw the plan in the bubble\\n2. Route its buttons\\n3. Open the plan window\\n4. Fake an answer\"}", "plan",
            CanAllow: false, CanAnswerPlan: true, ReceivedAt: "2027-01-15T08:05:00.000Z");
        var fonts = CompanionOverlay.SmokeFonts;
        void Drawn(string face, string value, double size, int weight, DesignToken ink, string? family = null)
        {
            var text = overlay.SmokeTexts!.FirstOrDefault(item => item.Value == value) ?? throw new InvalidOperationException($"the pet's {face} does not draw \"{value}\"");
            Require(text.Size == size && text.Weight == weight && text.Ink == ink && text.Family == (family ?? fonts.Body), $"the pet's {face}: \"{value}\" must be {size} at weight {weight} in {ink}; got {text.Size} at {text.Weight} in {text.Ink}, {text.Family}");
            var bubble = overlay.SmokeGeometry.Bubble;
            Require(text.Bounds.Left >= bubble.Left + 12 - 0.01 && text.Bounds.Right <= bubble.Right - 12 + 0.01 && text.Bounds.Top >= bubble.Top + 12 - 0.01 && text.Bounds.Bottom <= bubble.Bottom - 12 + 0.01, $"the pet's {face}: \"{value}\" must stay inside the 12 of padding; got {text.Bounds} in {bubble}");
        }
        try
        {
            CompanionDraft(question).Picks[0] = ["The Mac's"];
            var origin = snapshot.Workspaces.First(w => w.Id == agent.WorkspaceId).Name + " · " + agent.Title;
            foreach (var light in new[] { false, true })
            {
                var theme = light ? "light" : "dark";
                overlay.SetCard(CompanionCard("smoke-pager", snapshot, agent, agent.Status, null, 2, 3, true) with { Light = light }, true); overlay.Draw(0, 0);
                await CaptureCompanionSmoke(overlay, "smoke-companion-pager-" + theme + ".png");
                Drawn("pager", "2 / 3", 9, 500, DesignToken.Ink2);
                Require(overlay.SmokeGeometry.Height == CompanionBubbleLayout.BaseHeight, "the pager must fit the base window");

                overlay.SetCard(CompanionCard("smoke-approval", snapshot, agent, "waiting", approval) with { Light = light }, true); overlay.Draw(6, 0);
                await CaptureCompanionSmoke(overlay, "smoke-companion-approval-" + theme + ".png");
                Require(overlay.SmokeGeometry.Width == 282 && overlay.SmokeGeometry.Height == CompanionBubbleLayout.TallHeight && overlay.SmokeGeometry.Bubble.Bottom == CompanionBubbleLayout.TallHeight - 145,
                    $"an approval must stand in the taller window, on the pet; got {overlay.SmokeGeometry.Width} × {overlay.SmokeGeometry.Height}");
                Drawn("approval", Locale.Get("companion.approval.title"), 11, 600, DesignToken.Ink); Drawn("approval", origin, 9, 400, DesignToken.Ink2);
                Drawn("approval", ToolPermissionStrings.TitleBash + " · Bash", 10, 400, DesignToken.Ink2); Drawn("approval", "Run the Windows core tests before the commit", 11, 500, DesignToken.Ink);
                Drawn("approval", "dotnet test native/windows/MightyClaude.Core.Tests -c Release --no-build --logger trx", 10, 400, DesignToken.Ink, fonts.Mono);
                Drawn("approval", Locale.Get("companion.button.open"), 11, 400, DesignToken.Ink); Drawn("approval", ToolPermissionStrings.ButtonDeny, 11, 400, DesignToken.Ink); Drawn("approval", Locale.Get("companion.button.review"), 11, 400, DesignToken.OnAccent);
                if (overlay.SmokeGeometry.Scale == Math.Floor(overlay.SmokeGeometry.Scale))
                {
                    // Around a request the hairline is the amber at 45% (M/AgentCompanionViews.swift:367), on the card.
                    var asked = overlay.CaptureForSmoke(); var (_, _, card, _, zoom) = overlay.SmokeGeometry; var palette = DesignTokens.Palette(!light);
                    var edge = ((int)(card.Top * zoom) * (int)asked.Width + (int)((card.Left + card.Width / 2) * zoom)) * 4; byte Mixed(byte under, byte over) => (byte)Math.Round(under + (over - under) * 115 / 255.0);
                    Require(asked.Pixels[edge + 3] == 255 && Math.Abs(asked.Pixels[edge] - Mixed(palette.Card.B, palette.Wait.B)) <= 2 && Math.Abs(asked.Pixels[edge + 1] - Mixed(palette.Card.G, palette.Wait.G)) <= 2 && Math.Abs(asked.Pixels[edge + 2] - Mixed(palette.Card.R, palette.Wait.R)) <= 2,
                        $"an approval ({theme}) must have a hairline of the amber at 45%; got {asked.Pixels[edge + 2]},{asked.Pixels[edge + 1]},{asked.Pixels[edge]}");
                }

                overlay.SetCard(CompanionCard("smoke-question", snapshot, agent, "waiting", question) with { Light = light }, true); overlay.Draw(6, 0);
                await CaptureCompanionSmoke(overlay, "smoke-companion-question-" + theme + ".png");
                Drawn("question", Locale.Get("phone.questionnaire.title") + " 1/2", 11, 600, DesignToken.Ink); Drawn("question", "Which layout should the pet's bubble follow?", 11, 500, DesignToken.Ink);
                Drawn("question", "The Mac's", 11, 500, DesignToken.Ink); Drawn("question", "The same order, type and paddings", 9, 400, DesignToken.Ink2); Drawn("question", "The earlier Windows card", 11, 500, DesignToken.Ink);
                Drawn("question", Locale.Get("companion.button.answer"), 11, 400, DesignToken.OnAccent); Drawn("question", Locale.Get("phone.questionnaire.cancel"), 11, 400, DesignToken.Ink);

                // A finished plan in the taller window: when it came, its first line and the next three, and its answers, the approve on a row of its own.
                overlay.SetCard(CompanionCard("smoke-plan", snapshot, agent, "waiting", plan) with { Light = light }, true); overlay.Draw(6, 0);
                await CaptureCompanionSmoke(overlay, "smoke-companion-plan-" + theme + ".png");
                Require(overlay.SmokeGeometry.Width == 282 && overlay.SmokeGeometry.Height == CompanionBubbleLayout.TallHeight && overlay.SmokeGeometry.Bubble.Bottom == CompanionBubbleLayout.TallHeight - 145,
                    $"a plan must stand in the taller window, on the pet; got {overlay.SmokeGeometry.Width} × {overlay.SmokeGeometry.Height}");
                Drawn("plan", Locale.Get("plan.card.title"), 11, 600, DesignToken.Ink); Drawn("plan", origin, 9, 400, DesignToken.Ink2);
                Drawn("plan", PlanCardSupport.ReceivedText(plan.ReceivedAt!), 10, 400, DesignToken.Ink2); Drawn("plan", "Ship the pet's plan bubble", 11, 500, DesignToken.Ink);
                Drawn("plan", "1. Draw the plan in the bubble\n2. Route its buttons\n3. Open the plan window", 10, 400, DesignToken.Ink, fonts.Mono);
                Drawn("plan", Locale.Get("plan.card.cancel"), 11, 400, DesignToken.Ink); Drawn("plan", Locale.Get("companion.plan.review"), 11, 400, DesignToken.Ink);
                Drawn("plan", Locale.Get("plan.card.approveAuto"), 11, 400, DesignToken.OnAccent);
                var approve = overlay.SmokeTexts!.First(item => item.Value == Locale.Get("plan.card.approveAuto")); var cancel = overlay.SmokeTexts!.First(item => item.Value == Locale.Get("plan.card.cancel"));
                Require(approve.Bounds.Top > cancel.Bounds.Bottom && !approve.Trimmed, $"the plan's approve ({theme}) must be whole, on a row under the others; got {approve.Bounds} under {cancel.Bounds}, trimmed {approve.Trimmed}");
            }
            // A choice, the answer button and the cancel all belong to the question shown; the approval's to the approval. None of them brings a window forward.
            // Each card is put up again before its clicks: the faces above end on the plan, not the question.
            overlay.SetCard(CompanionCard("smoke-question", snapshot, agent, "waiting", question), true); overlay.Draw(6, 0);
            var before = clicks.Count; var front = CompanionOverlay.ForegroundWindow;
            Require(overlay.SmokeClick("questions") && overlay.SmokeClick("deny") && overlay.SmokeClick("open") && clicks.Skip(before).SequenceEqual(new[] { "smoke-question:questions", "smoke-question:deny", "smoke-question:open" }), "The question's buttons did not answer for the question shown.");
            overlay.SetCard(CompanionCard("smoke-approval", snapshot, agent, "waiting", approval), true); overlay.Draw(6, 0); before = clicks.Count;
            Require(overlay.SmokeClick("review") && overlay.SmokeClick("deny") && clicks.Skip(before).SequenceEqual(new[] { "smoke-approval:review", "smoke-approval:deny" }), "The approval's buttons did not answer for the approval shown.");
            overlay.SetCard(CompanionCard("smoke-plan", snapshot, agent, "waiting", plan), true); overlay.Draw(6, 0); before = clicks.Count;
            Require(overlay.SmokeClick("plan-approve") && overlay.SmokeClick("plan-review") && overlay.SmokeClick("plan-cancel") && overlay.SmokeClick("open") && !overlay.SmokeClick("deny")
                && clicks.Skip(before).SequenceEqual(new[] { "smoke-plan:plan-approve", "smoke-plan:plan-review", "smoke-plan:plan-cancel", "smoke-plan:open" }), "The plan's buttons did not answer for the plan shown.");
            Require(CompanionOverlay.ForegroundWindow == front && front != overlay.Handle && !overlay.TookThread, "Companion requests acquired foreground focus.");
            // Without a pet image the paw stands in the pet's frame, in the accent with 35 clear around it (M/AgentCompanionViews.swift:137-140).
            using var bare = new CompanionOverlay(new CompanionPreferences()); bare.SetAtlas([]);
            bare.SetCard(CompanionCard("smoke-paw", snapshot, agent, agent.Status, null) with { Light = false }, false); bare.Draw(0, 0);
            // Shown, as the app shows it once it has a picture: a window that is never shown could not take the front.
            front = CompanionOverlay.ForegroundWindow; bare.Show(true);
            Require(CompanionOverlay.ForegroundWindow == front && front != bare.Handle && !bare.TookThread && bare.NonActivating, "The pet's window without a pet image acquired foreground focus.");
            await CaptureCompanionSmoke(bare, "smoke-companion-paw-dark.png");
            var frame = bare.CaptureForSmoke(); var (_, _, _, pet, scale) = bare.SmokeGeometry; var accent = DesignTokens.Dark.Accent;
            (byte B, byte G, byte R, byte A) At(double x, double y) { var index = ((int)(y * scale) * (int)frame.Width + (int)(x * scale)) * 4; return (frame.Pixels[index], frame.Pixels[index + 1], frame.Pixels[index + 2], frame.Pixels[index + 3]); }
            Require(At(pet.X + pet.Width / 2, pet.Y + 40 + 39.5) == (accent.B, accent.G, accent.R, (byte)255) && At(pet.X + 20, pet.Y + 20).A == 0 && At(pet.Right - 20, pet.Bottom - 20).A == 0,
                $"Without a pet image the paw must stand in the pet's frame, in the accent; got {At(pet.X + pet.Width / 2, pet.Y + 40 + 39.5)}.");
            // The two windows the bubble's buttons open: the wait card on the app's page, in the app's theme. Shown aside, so the smoke's own window keeps the keyboard.
            foreach (var (name, open) in new (string, Action)[] { ("review", () => ShowCompanionApproval(approval, aside: true)), ("answer", () => ShowCompanionQuestions(question, aside: true)), ("plan", () => ShowCompanionPlan(plan, aside: true)) })
            {
                open(); var opened = companionQuestionWindow ?? throw new InvalidOperationException("The pet's " + name + " window did not open.");
                try
                {
                    var page = (Grid)opened.Content; await WaitUI(() => page.IsLoaded && page.ActualWidth > 0);
                    Require(page.RequestedTheme == root.RequestedTheme && ReferenceEquals(page.Background, WindowBackground()) && page.Children[0] is Border { BorderThickness.Left: DesignMetrics.Stroke.Active } waitCard
                        && ReferenceEquals(waitCard.BorderBrush, brushes.Brush(DesignToken.Wait)) && ReferenceEquals(waitCard.Background, brushes.Brush(DesignToken.Card)) && waitCard.CornerRadius == new CornerRadius(DesignMetrics.Radius.Composer),
                        "The pet's " + name + " window must be the wait card on the app's page, in the app's theme.");
                    await CaptureElement(page, Path.Combine(options.ProfileDirectory!, "smoke-companion-" + name + ".png"));
                    if (name == "plan") await CheckCompanionPlanWindow(opened, page, plan, agent);
                }
                finally { if (companionQuestionWindow == opened) opened.Close(); }
            }
        }
        finally
        {
            // Whatever step failed, the app's own pet starts clean: no draft, no stand-in request or answerer, no answer marked
            // as on its way, and no plan window left open (its Closed handler lets go of the window and its sync).
            companionQuestions.Remove(PermissionKey(question)); companionPermissions.Remove(PermissionKey(plan)); smokePlanAnswerer = null;
            companionPlanSending = null;
            if (companionQuestionWindowKey == PermissionKey(plan)) companionQuestionWindow?.Close();
            companionPlanWindowSync = null;
        }
    }
    /// <summary>
    /// The plan's own window: the plan's Markdown rendered (not its marks), the change request held back until it has words,
    /// and an approve that answers through the smoke's stand-in and takes the window and the request with it.
    /// </summary>
    private async Task CheckCompanionPlanWindow(Window opened, Grid page, ToolPermissionRequest plan, RunSession agent)
    {
        RichEditBox? text = null; Button? approve = null, revise = null; TextBox? field = null;
        await WaitUI(() =>
        {
            var all = VisualChildren(page).ToArray();
            text = all.OfType<RichEditBox>().FirstOrDefault(view => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(view) == "pet-plan-text");
            approve = all.OfType<Button>().FirstOrDefault(button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(button) == "pet-plan-approve-auto");
            revise = all.OfType<Button>().FirstOrDefault(button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(button) == "pet-plan-revise");
            field = all.OfType<TextBox>().FirstOrDefault(box => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(box) == "pet-plan-revise-text");
            return text is { IsLoaded: true } && approve is not null && revise is not null && field is not null;
        });
        text!.Document.GetText(Microsoft.UI.Text.TextGetOptions.None, out var rendered);
        Require(rendered.Contains("Ship the pet's plan bubble", StringComparison.Ordinal) && rendered.Contains("Route its buttons", StringComparison.Ordinal) && !rendered.Contains("# Ship", StringComparison.Ordinal),
            "The pet's plan window must render the plan's Markdown: " + rendered);
        Require(!revise!.IsEnabled, "The pet's plan window must hold back an empty change request.");
        field!.Text = "Write the tests first";
        await WaitUI(() => revise.IsEnabled);
        var answers = new List<(string Pane, string Request, PlanDecision Decision)>();
        smokePlanAnswerer = (pane, request, decision) => answers.Add((pane, request, decision));
        companionPermissions[PermissionKey(plan)] = plan;
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(approve!).GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await WaitUI(() => answers.Count == 1 && companionQuestionWindow != opened && !companionPermissions.ContainsKey(PermissionKey(plan)));
        Require(answers[0] == (agent.Id, plan.Id, PlanDecision.ApproveAutoEdit), "The pet's plan window must answer approveAutoEdit through the stand-in.");
    }
}
