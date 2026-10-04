using MightyClaude.Core;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private bool companionBubble, companionInitialized;
    private int companionRow, companionTicks;
    private int companionLoadGeneration;
    private ToolPermissionRequest? companionPresentedPermission;
    private DateTimeOffset companionAnimationStart = DateTimeOffset.UtcNow;
    private DateTimeOffset? companionHideAt;
    private string? companionRequestStamp;
    private Window? companionQuestionWindow;
    private string? companionQuestionWindowKey;
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
        companionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        companionTimer.Tick += (_, _) =>
        {
            if (closing) return;
            try
            {
                if (++companionTicks % 16 == 0) RefreshCompanion();
                if (companionPreferences.Enabled) companionOverlay?.Draw(companionRow, CompanionPet.Frame(companionRow, (DateTimeOffset.UtcNow - companionAnimationStart).TotalSeconds, companionPreferences.ReducedMotion), companionPreferences.ReducedMotion);
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
        if (selected is null) { companionError = Locale.Get("companion.error.noPet"); return; }
        try
        {
            var pet = await Task.Run(() => CompanionPet.Load(selected.Source, selected.Id));
            var data = await DecodeCompanionPet(pet);
            if (closing || generation != companionLoadGeneration || !companionPreferences.Enabled) return;
            var overlay = new CompanionOverlay(companionPreferences); overlay.SetAtlas(data); overlay.Action += CompanionAction;
            overlay.Moved += (left, top) => { companionPreferences = companionPreferences with { Left = left, Top = top }; SaveCompanionPreferences(); };
            overlay.Resized += (width, height) => { companionPreferences = companionPreferences with { BubbleWidth = width, BubbleHeight = height }; SaveCompanionPreferences(); };
            companionOverlay = overlay; companionLoadedPet = pet.Id; companionError = null; RefreshCompanion(); overlay.Show(true);
        }
        catch (Exception ex) { if (!closing && generation == companionLoadGeneration) companionError = ex.Message; }
    }
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
        else { companionPermissions.Remove(key); companionQuestions.Remove(key); if (companionQuestionWindowKey == key) companionQuestionWindow?.Close(); }
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
        var stamp = current?.Id + "|" + current?.Logs.LastOrDefault(l => l.Kind == "user")?.Id;
        if (stamp != companionRequestStamp) { companionRequestStamp = stamp; companionHideAt = null; if (status is "running" or "waiting") companionBubble = companionPreferences.ShowsTask; }
        if (status == "completed" && companionRow is not (0 or 4)) companionHideAt = DateTimeOffset.UtcNow.AddSeconds(6);
        var row = CompanionAnimation.Row(status, current?.CurrentActivity, companionHideAt > DateTimeOffset.UtcNow);
        if (row != companionRow) { companionRow = row; companionAnimationStart = DateTimeOffset.UtcNow; }
        if (companionHideAt is { } hide && hide <= DateTimeOffset.UtcNow) { companionBubble = false; companionHideAt = null; }
        var permission = companionPermissions.Values.FirstOrDefault(p => p.RunId == current?.Id);
        companionPresentedPermission = permission;
        var title = current?.Title ?? "Mighty Claude";
        var workspace = snapshot.Workspaces.FirstOrDefault(w => w.Id == current?.WorkspaceId)?.Name ?? "";
        var subtitle = string.Join(" · ", new[] { workspace, current?.Provider, current?.RunTiming?.Label() }.Where(v => !string.IsNullOrEmpty(v)));
        var body = current?.CurrentActivity?.Summary ?? (status switch {
            "running" => Locale.Get("companion.status.running"), "waiting" => Locale.Get("companion.status.waiting"),
            "completed" => Locale.Get("companion.status.completed"), "error" => Locale.Get("companion.status.error"),
            "stopped" => Locale.Get("companion.status.stopped"), _ => Locale.Get("companion.status.idle") });
        var promptText = companionPreferences.ShowsTask && current?.Logs.LastOrDefault(l => l.Kind == "user") is { } prompt ? Wire.Clean(prompt.Text, 400) : null;
        var page = active.Length > 1 && CompanionCarousel.Position(companionShown, active) is { } pos ? pos + " / " + active.Length : null;
        var buttons = new List<CompanionOverlayButton>();
        companionCardKey = permission is null ? companionShown ?? "idle" : PermissionKey(permission) + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(permission.ToolName + permission.InputJson)));
        if (permission is not null)
        {
            companionBubble = true;
            if (permission.CanAnswerQuestions && UserQuestionnaire.Parse(permission.InputJson) is { } questionnaire)
            {
                var draft = CompanionDraft(permission); var question = questionnaire.Questions[Math.Clamp(draft.Step, 0, questionnaire.Questions.Count - 1)];
                title = Locale.Get("phone.questionnaire.title") + " · " + (draft.Step + 1) + "/" + questionnaire.Questions.Count;
                body = question.Question;
                // The full questionnaire opens as its own window only after this
                // explicit action; automatic requests never acquire keyboard focus.
                buttons.Add(new("questions", Locale.Get("companion.button.answer")));
                buttons.Add(new("deny", ToolPermissionStrings.ButtonDeny));
            }
            else
            {
                title = ToolPermissionPresentation.Make(permission.ToolName, permission.InputJson).Title;
                body = permission.InputJson;
                // The native preview can be ellipsized at any DPI or bubble size.
                // Approval always opens the complete scrollable request first.
                if (permission.CanAllow) buttons.Add(new("review", Locale.Get("companion.button.review")));
                buttons.Add(new("deny", ToolPermissionStrings.ButtonDeny));
            }
        }
        if (companionPermissions.Values.Any(p => p.RunId != current?.Id && active.Contains(p.RunId))) buttons.Add(new("pending", Locale.Get("companion.button.pending")));
        if (current is not null) buttons.Add(new("open", Locale.Get("companion.button.open")));
        companionOverlay.SetCard(new(companionCardKey, title, subtitle, body, page, buttons, snapshot.Theme == "light", permission is null ? promptText : null, permission is not null), companionBubble);
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
        if (action == "open" && companionShown is { } shown) { FocusSession(shown); return; }
        if (companionPresentedPermission is not { } presented || !companionPermissions.TryGetValue(PermissionKey(presented), out var request)
            || request != presented || request.State != "pending" || request.RunId != companionShown) return;
        if (action == "questions" && request.CanAnswerQuestions) { ShowCompanionQuestions(request); return; }
        if (action == "review" && request.CanAllow && !request.CanAnswerQuestions) { ShowCompanionApproval(request); return; }
        if (action != "deny") return;
        try { service.RespondToToolPermission(request.RunId, request.Id, false); }
        catch (Exception ex) { companionError = ex.Message; }
    }
    private void ShowCompanionQuestions(ToolPermissionRequest request)
    {
        var key = PermissionKey(request);
        if (companionQuestionWindowKey == key && companionQuestionWindow is { } existing) { existing.Activate(); return; }
        companionQuestionWindow?.Close();
        if (UserQuestionnaire.Parse(request.InputJson) is not { } questionnaire) return;
        var draft = CompanionDraft(request); var window = new Window { Title = Locale.Get("phone.questionnaire.title") }; var host = new StackPanel { Spacing = 12, Padding = new Thickness(18) };
        window.Content = new ScrollViewer { Content = host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; window.AppWindow.Resize(new(460, 580));
        companionQuestionWindow = window; companionQuestionWindowKey = key;
        window.Closed += (_, _) => { if (companionQuestionWindow == window) { companionQuestionWindow = null; companionQuestionWindowKey = null; } };
        void RenderQuestion()
        {
            host.Children.Clear(); draft.Step = Math.Clamp(draft.Step, 0, questionnaire.Questions.Count - 1); var step = draft.Step; var question = questionnaire.Questions[step];
            host.Children.Add(new TextBlock { Text = request.RunId == companionShown ? service.Snapshot.Sessions.FirstOrDefault(s => s.Id == request.RunId)?.Title ?? "" : "", Opacity = .7 });
            host.Children.Add(new TextBlock { Text = (step + 1) + " / " + questionnaire.Questions.Count + " · " + question.Header, FontSize = 13 });
            host.Children.Add(new TextBlock { Text = question.Question, FontSize = 17, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            if (!draft.Picks.TryGetValue(step, out var picks)) draft.Picks[step] = picks = [];
            var radios = new List<CheckBox>();
            TextBox? customInput = null;
            foreach (var option in question.Options)
            {
                var content = new StackPanel { Spacing = 3 }; content.Children.Add(new TextBlock { Text = option.Label, TextWrapping = TextWrapping.Wrap }); content.Children.Add(new TextBlock { Text = option.Description, TextWrapping = TextWrapping.Wrap, Opacity = .7 });
                var choice = new CheckBox { Content = content, IsChecked = picks.Contains(option.Label), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                radios.Add(choice); choice.Checked += (_, _) => { if (!question.MultiSelect) { picks.Clear(); foreach (var other in radios.Where(r => r != choice)) other.IsChecked = false; draft.Custom.Remove(step); if (customInput is not null) customInput.Text = ""; } picks.Add(option.Label); };
                choice.Unchecked += (_, _) => picks.Remove(option.Label); host.Children.Add(choice);
            }
            var custom = new TextBox { Header = Locale.Get("companion.question.custom"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxLength = 8192, Text = draft.Custom.GetValueOrDefault(step) ?? "" }; customInput = custom;
            custom.TextChanged += (_, _) => { draft.Custom[step] = custom.Text; if (!question.MultiSelect && !string.IsNullOrWhiteSpace(custom.Text)) { picks.Clear(); foreach (var choice in radios) choice.IsChecked = false; } }; host.Children.Add(custom);
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; host.Children.Add(error);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (step > 0) buttons.Children.Add(Button(Locale.Get("companion.question.back"), () => { draft.Step--; RenderQuestion(); return Task.CompletedTask; }));
            buttons.Children.Add(Button(Locale.Get(step + 1 == questionnaire.Questions.Count ? "companion.question.submit" : "companion.question.next"), () =>
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
            }));
            host.Children.Add(buttons);
        }
        RenderQuestion(); window.Activate();
    }
    private void ShowCompanionApproval(ToolPermissionRequest request)
    {
        var key = PermissionKey(request); companionQuestionWindow?.Close();
        var window = new Window { Title = ToolPermissionPresentation.Make(request.ToolName, request.InputJson).Title };
        var content = new Grid { Padding = new Thickness(16), RowSpacing = 10, RequestedTheme = root.RequestedTheme };
        content.RowDefinitions.Add(new() { Height = GridLength.Auto }); content.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.Children.Add(new TextBlock { Text = window.Title, FontSize = 17, TextWrapping = TextWrapping.Wrap });
        var text = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), Text = request.InputJson };
        var scroll = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); content.Children.Add(scroll);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Task Respond(bool allow)
        {
            if (!companionPermissions.TryGetValue(key, out var live) || live != request || live.State != "pending" || allow && !live.CanAllow) { window.Close(); return Task.CompletedTask; }
            try { service.RespondToToolPermission(request.RunId, request.Id, allow); window.Close(); } catch (Exception ex) { error.Text = ex.Message; }
            return Task.CompletedTask;
        }
        buttons.Children.Add(Button(ToolPermissionStrings.ButtonDeny, () => Respond(false))); buttons.Children.Add(Button(ToolPermissionStrings.ButtonAllowOnce, () => Respond(true)));
        var footer = new StackPanel { Spacing = 6 }; footer.Children.Add(error); footer.Children.Add(buttons); Grid.SetRow(footer, 2); content.Children.Add(footer);
        window.Content = content; window.AppWindow.Resize(new(520, 500)); companionQuestionWindow = window; companionQuestionWindowKey = key;
        window.Closed += (_, _) => { if (companionQuestionWindow == window) { companionQuestionWindow = null; companionQuestionWindowKey = null; } }; window.Activate();
    }
    private StackPanel BuildCompanionSection()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = Locale.Get("companion.settings.description"), TextWrapping = TextWrapping.Wrap });
        var preview = new Image { Width = 58, Height = 64, HorizontalAlignment = HorizontalAlignment.Left }; Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(preview, "settings-companion-preview"); panel.Children.Add(preview);
        int previewGeneration = 0;
        async Task RefreshPreview()
        {
            var generation = ++previewGeneration;
            var selected = companionPets.FirstOrDefault(p => p.Id == companionPreferences.SelectedPet) ?? companionPets.FirstOrDefault();
            if (selected is null) { preview.Source = null; return; }
            try { var pet = await Task.Run(() => CompanionPet.Load(selected.Source, selected.Id)); var image = await CompanionPreview(pet); if (generation == previewGeneration) preview.Source = image; }
            catch (Exception ex) { if (generation == previewGeneration) { preview.Source = null; companionError = ex.Message; } }
        }
        panel.Loaded += async (_, _) => await RefreshPreview();
        var enabled = new ToggleSwitch { Header = Locale.Get("companion.settings.enabled"), IsOn = companionPreferences.Enabled };
        enabled.Toggled += async (_, _) => { companionPreferences = companionPreferences with { Enabled = enabled.IsOn }; SaveCompanionPreferences(); await ReloadCompanionPets(); }; panel.Children.Add(enabled);
        var task = new ToggleSwitch { Header = Locale.Get("companion.settings.task"), IsOn = companionPreferences.ShowsTask };
        task.Toggled += (_, _) => { companionPreferences = companionPreferences with { ShowsTask = task.IsOn }; companionBubble = task.IsOn; SaveCompanionPreferences(); RefreshCompanion(); }; panel.Children.Add(task);
        var motion = new ToggleSwitch { Header = Locale.Get("companion.settings.motion"), IsOn = companionPreferences.ReducedMotion };
        motion.Toggled += (_, _) => { companionPreferences = companionPreferences with { ReducedMotion = motion.IsOn }; SaveCompanionPreferences(); }; panel.Children.Add(motion);
        var alerts = new ToggleSwitch { Header = Locale.Get("companion.settings.notifications"), IsOn = service.Snapshot.CompletionNotificationsEnabled };
        alerts.Toggled += async (_, _) => { companionPreferences = companionPreferences with { Notifications = alerts.IsOn }; SaveCompanionPreferences(); await service.UpdateAsync(s => s with { CompletionNotificationsEnabled = alerts.IsOn }); }; panel.Children.Add(alerts);
        var picker = new ComboBox { Header = Locale.Get("companion.settings.pet"), HorizontalAlignment = HorizontalAlignment.Stretch }; Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(picker, "settings-companion-pet");
        void Populate() { picker.Items.Clear(); foreach (var pet in companionPets) picker.Items.Add(new ComboBoxItem { Content = pet.Name + (pet.Id.StartsWith("codex:", StringComparison.Ordinal) ? " · Codex" : ""), Tag = pet.Id }); picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (companionLoadedPet ?? companionPreferences.SelectedPet)); }
        Populate(); picker.SelectionChanged += async (_, _) => { if (picker.SelectedItem is not ComboBoxItem { Tag: string id } || id == companionPreferences.SelectedPet) return; companionPreferences = companionPreferences with { SelectedPet = id }; SaveCompanionPreferences(); await ReloadCompanionPets(); await RefreshPreview(); }; panel.Children.Add(picker);
        var message = new TextBlock { Text = companionError ?? "", TextWrapping = TextWrapping.Wrap }; panel.Children.Add(message);
        panel.Children.Add(Button(Locale.Get("companion.settings.import"), async () =>
        {
            var files = new FileOpenPicker(); foreach (var extension in new[] { ".json", ".png", ".webp" }) files.FileTypeFilter.Add(extension); WinRT.Interop.InitializeWithWindow.Initialize(files, WinRT.Interop.WindowNative.GetWindowHandle(settingsWindow ?? this));
            var chosen = await files.PickSingleFileAsync(); if (chosen is null) return;
            try { var pet = await Task.Run(() => CompanionPet.Load(chosen.Path, "import")); await DecodeCompanionPet(pet); var installed = await Task.Run(() => pet.Install(StateDirectory)); companionPreferences = companionPreferences with { SelectedPet = installed.Id }; SaveCompanionPreferences(); await ReloadCompanionPets(); Populate(); await RefreshPreview(); message.Text = companionError ?? ""; }
            catch (Exception ex) { message.Text = ex.Message; }
        }));
        panel.Children.Add(Button(Locale.Get("companion.settings.reload"), async () => { await ReloadCompanionPets(); Populate(); await RefreshPreview(); message.Text = companionError ?? ""; }));
        panel.Children.Add(Button(Locale.Get("companion.settings.resetPosition"), async () => { companionPreferences = companionPreferences with { Left = null, Top = null, BubbleWidth = null, BubbleHeight = null }; SaveCompanionPreferences(); await ReloadCompanionPets(); }));
        return panel;
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
        var pixels = await DecodeCompanionPet(pet); var foreground = CompanionOverlay.ForegroundWindow;
        var preview = await CompanionPreview(pet); Require(preview.PixelWidth == 192 && preview.PixelHeight == 208, "Companion preview must crop exactly the first atlas frame.");
        using var overlay = new CompanionOverlay(new CompanionPreferences()); overlay.SetAtlas(pixels);
        var clicks = new List<string>(); overlay.Action += (key, action) => clicks.Add(key + ":" + action);
        overlay.SetCard(new("smoke-pet", "Mighty Claude", "Claude · 1 / 2", "\uD55C\uAD6D\uC5B4 \uC785\uB825 \uD3EC\uCEE4\uC2A4\uB97C \uC720\uC9C0\uD569\uB2C8\uB2E4.", "1 / 2", [new("answer", "Answer / \uC751\uB2F5")]), true);
        for (var row = 0; row < 9; row++) overlay.Draw(row, CompanionPet.FrameCounts[row] - 1);
        overlay.Show(true); await Task.Delay(120);
        Require(overlay.NonActivating && CompanionOverlay.ForegroundWindow == foreground, "Companion stole foreground focus.");
        Require(overlay.SmokeClick("answer") && clicks.SequenceEqual(new[] { "smoke-pet:answer" }), "Companion pointer action did not match the displayed card.");
        Require(CompanionOverlay.ForegroundWindow == foreground, "Companion click stole foreground focus.");
        overlay.SmokeContextMenu(); overlay.Draw(0, 0);
        Require(overlay.SmokeClick("menu-close") && CompanionOverlay.ForegroundWindow == foreground, "Companion context actions stole focus or could not close.");
        var compact = overlay.SmokeLayout;
        overlay.SetCard(new("smoke-layout", "Mighty Claude", "Claude", string.Join(" ", Enumerable.Repeat("Current work", 30)), null, [], Request: string.Join(" ", Enumerable.Repeat("Requested task", 30))), true);
        var automatic = overlay.SmokeLayout;
        Require(automatic.AutomaticHeight && automatic.Height != compact.Height && automatic.Height < 480, "Companion default height did not follow measured content.");
        Require(overlay.SmokeResizeHit(1, automatic.Height / 2) == CompanionResizeEdges.Left && overlay.SmokeResizeHit(automatic.Width / 2, 1) == CompanionResizeEdges.Top, "Companion top/side resize strips are missing.");
        overlay.SmokeResize(CompanionResizeEdges.Left, -60, 0); var wider = overlay.SmokeLayout;
        Require(wider.Width == automatic.Width + 60 && wider.Right == automatic.Right && wider.AutomaticHeight, "Side resize moved its opposite edge or disabled automatic height.");
        overlay.SmokeResize(CompanionResizeEdges.Top, 0, -40); var taller = overlay.SmokeLayout;
        Require(taller.Height == wider.Height + 40 && taller.Bottom == wider.Bottom && !taller.AutomaticHeight, "Top resize moved the pet instead of pinning the bottom.");
        overlay.SmokeResize(CompanionResizeEdges.Right, 40, 0, cancel: true); Require(overlay.SmokeLayout == taller, "Cancelled resize changed saved geometry.");
        overlay.SmokeResetSize(); Require(overlay.SmokeLayout.AutomaticHeight && overlay.SmokeLayout.Width == CompanionBubbleLayout.DefaultWidth, "Double-click reset must restore automatic sizing.");
        Require(CompanionOverlay.ForegroundWindow == foreground, "Companion resize acquired foreground focus.");
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
            ["contentDrivenHeightAndEdgeResize"] = true, ["keyboardAccessibleAgentStatus"] = true, ["physicalInputTested"] = false };
    }
}
