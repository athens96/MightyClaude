using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

/// <summary>
/// The web shell's Settings, setup screen, sheets and dialogs (native/windows/web/src/native). Each is the window's own WinUI
/// form, built by the same builder the WinUI window uses (SettingsGroups, BuildSetupContent, the plugin browser, the style
/// approval sheet, a ContentDialog's content), kept in the window's tree behind the page and invisible (<see cref="webNativeHost"/>)
/// so its Loaded handlers, refreshes and timers run as on screen, and read for the page by <see cref="WebNativeReader"/>, which
/// also plays the page's clicks, switches, picks and words back on those controls. The page gets
/// <c>{kind:"native", id, place, view}</c> whenever what a view shows changed (looked at with the frame's clock) and
/// <c>view: null</c> when it closed; it answers with <c>nativeAction</c>, <c>nativeNav</c>, <c>nativeButton</c> and <c>nativeClose</c>.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Holds the forms the page draws, under it in the window and invisible; null outside the web shell.</summary>
    private Grid? webNativeHost;
    private readonly Dictionary<string, WebNativeSurface> webSurfaces = [];
    private int webNativeNext;
    private string? webTextSent;

    /// <summary>One view the page draws from a WinUI form: where it goes, how its model is read, and what closing it does.</summary>
    private sealed class WebNativeSurface(string id, string place, WebNativeReader reader)
    {
        public string Id { get; } = id;
        public string Place { get; } = place;
        public WebNativeReader Reader { get; } = reader;
        public Func<object>? Model { get; set; }
        public Action<string?>? Button { get; set; }
        public Action<string>? Navigate { get; set; }
        public List<FrameworkElement> Hosted { get; } = [];
        public string? Sent { get; set; }
    }

    private WebNativeReader NewNativeReader()
    {
        var palette = new Dictionary<Windows.UI.Color, string>();
        var order = Enum.GetValues<DesignToken>().OrderBy(t => t == DesignToken.Accent ? 0 : 1);
        string Kebab(DesignToken token) { var name = token.ToString(); return string.Concat(name.Select((ch, i) => char.IsUpper(ch) ? (i == 0 ? "" : "-") + char.ToLowerInvariant(ch) : ch.ToString())); }
        string? theme = null;
        return new WebNativeReader(color =>
        {
            // The palette follows the theme: read again when the window's theme changed since.
            var now = service.Snapshot.Theme;
            if (theme != now) { theme = now; palette.Clear(); foreach (var token in order) palette.TryAdd(brushes.Brush(token).Color, Kebab(token)); }
            return palette.GetValueOrDefault(color);
        });
    }

    private void HostNative(WebNativeSurface surface, FrameworkElement element)
    {
        if (webNativeHost is null) return;
        var holder = new Border { Child = element, Width = SettingsWindowWidth };
        surface.Hosted.Add(holder); webNativeHost.Children.Add(holder);
    }

    private WebNativeSurface OpenNativeSurface(string id, string place)
    {
        CloseNativeSurface(id, push: false);
        var surface = new WebNativeSurface(id, place, NewNativeReader());
        webSurfaces[id] = surface;
        return surface;
    }

    private void CloseNativeSurface(string id, bool push = true)
    {
        if (!webSurfaces.Remove(id, out var surface)) return;
        foreach (var holder in surface.Hosted) { if (holder is Border border) border.Child = null; webNativeHost?.Children.Remove(holder); }
        if (push) webShell?.Bridge.Push(new { kind = "native", id, view = (object?)null });
    }

    private void PushWebNatives()
    {
        if (closing || webShell is not { } shell || !shell.Bridge.Ready) { foreach (var surface in webSurfaces.Values) surface.Sent = null; webTextSent = null; return; }
        foreach (var surface in webSurfaces.Values.ToList()) PushNative(surface);
        PushWebText();
    }

    private void PushNative(WebNativeSurface surface)
    {
        if (surface.Model is not { } read || webShell is not { } shell) return;
        surface.Reader.Begin();
        var view = read();
        var json = JsonSerializer.Serialize(view, Wire.Json);
        if (json == surface.Sent) return;
        surface.Sent = json;
        shell.Bridge.Push(new { kind = "native", id = surface.Id, place = surface.Place, view });
    }

    /// <summary>The text setting the page follows with its CSS variables (MainWindow.TextAppearance.cs).</summary>
    private void PushWebText()
    {
        var text = AppText.Current;
        var model = new { kind = "text", scale = text.Scale, family = text.Family, transcriptFamily = text.ResolvedTranscriptFamily, codeFamily = text.CodeFamily };
        var json = JsonSerializer.Serialize(model, Wire.Json);
        if (json == webTextSent) return;
        webTextSent = json; webShell?.Bridge.Push(model);
    }

    private void RegisterSettingsMethods(WebBridge bridge)
    {
        webNativeHost = new Grid { Opacity = 0, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAccessibilityView(webNativeHost, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        bridge.Register("nativeAction", args =>
        {
            if (args.Text("view") is not { } id || !webSurfaces.TryGetValue(id, out var surface) || ArgNumber(args, "id") is not { } element) return Task.FromResult<object?>(false);
            var value = args.TryGetProperty("value", out var given) ? given.Clone() : default;
            var done = surface.Reader.Act((int)element, args.Text("op") ?? "", value);
            // The change shows at once, not on the next tick; a handler that runs on shows in the ticks after.
            DispatcherQueue.TryEnqueue(() => { if (webSurfaces.TryGetValue(id, out var still)) PushNative(still); });
            return Task.FromResult<object?>(done);
        });
        bridge.Register("nativeNav", args =>
        {
            if (args.Text("view") is { } id && webSurfaces.TryGetValue(id, out var surface) && args.Text("id") is { } to) surface.Navigate?.Invoke(to);
            return Task.FromResult<object?>(null);
        });
        bridge.Register("nativeButton", args =>
        {
            if (args.Text("view") is { } id && webSurfaces.TryGetValue(id, out var surface)) surface.Button?.Invoke(args.Text("button"));
            return Task.FromResult<object?>(null);
        });
        bridge.Register("nativeClose", args =>
        {
            if (args.Text("view") is { } id && webSurfaces.TryGetValue(id, out var surface)) surface.Button?.Invoke(null);
            return Task.FromResult<object?>(null);
        });
        bridge.Register("nativeState", _ =>
        {
            foreach (var surface in webSurfaces.Values) surface.Sent = null;
            webTextSent = null; PushWebNatives();
            return Task.FromResult<object?>(null);
        });
        bridge.Register("companionStatus", _ => Task.FromResult<object?>(OpenWebCompanionStatus()));
        bridge.Register("companionToggle", async _ => { await ToggleCompanionPet(); return null; });
        webShellClock!.Tick += (_, _) => PushWebNatives();
    }

    // ── Settings ──

    private TaskCompletionSource? webSettingsClosed;

    /// <summary>
    /// ShowCategorizedSettingsAsync in the page: the same categories, the same saved selection (SettingsPane), each category's
    /// sections built by SettingsGroups and drawn again as they change; the window counts as holding a dialog while it is open, as
    /// with the settings window. Completes when the page's Settings closes.
    /// </summary>
    private async Task ShowWebSettingsAsync()
    {
        if (webSurfaces.ContainsKey("settings")) return;
        if (dialogOpen) return;
        dialogOpen = true;
        var closed = webSettingsClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var surface = OpenNativeSurface("settings", "surface");
            settingsCategory = SettingsNavigation.Available.Any(c => c.Id == service.Snapshot.SettingsPane) ? service.Snapshot.SettingsPane : SettingsNavigation.Available[0].Id;
            var content = new StackPanel();
            var sections = new List<(string Title, bool Beta, Border Card)>();
            void Display(string category)
            {
                settingsCategory = category; content.Children.Clear(); sections.Clear();
                var reads = category == "cli" && !options.SmokeTest;
                if (reads) settingsAccountRefreshes++;
                foreach (var group in SettingsGroups(category))
                {
                    var wrapper = BuildSectionContainer(group.Title, group.Build(), group.Beta);
                    content.Children.Add(wrapper);
                    if (wrapper.Children.OfType<Border>().FirstOrDefault() is { } card) sections.Add((group.Title, group.Beta, card));
                }
                if (reads) _ = RefreshVisibleSettingsAccountsAsync(content);
            }
            HostNative(surface, content);
            Display(settingsCategory);
            surface.Navigate = to =>
            {
                if (to == settingsCategory || SettingsNavigation.Available.All(c => c.Id != to)) return;
                Display(to); PushNative(surface);
                _ = Act(() => service.UpdateAsync(s => s with { SettingsPane = to }));
            };
            surface.Button = _ => closed.TrySetResult();
            surface.Model = () => new
            {
                kind = "settings", title = Locale.Get("settings.title"), subtitle = "Windows · WinUI", close = Locale.Get("settings.closeButton"),
                nav = SettingsNavigation.Available.Select(c => new { id = c.Id, title = c.Title, glyph = SettingsCategoryGlyph(c.Id) }).ToList(),
                selected = settingsCategory,
                sections = sections.Select(s => new { title = s.Title, beta = s.Beta, node = surface.Reader.Node(s.Card.Child) }).ToList(),
                width = SettingsWindowWidth, height = SettingsWindowHeight,
            };
            PushNative(surface);
            await closed.Task;
        }
        finally
        {
            CloseNativeSurface("settings");
            if (ReferenceEquals(webSettingsClosed, closed)) webSettingsClosed = null;
            dialogOpen = false;
        }
    }

    /// <summary>SelectSettingsCategory for the page's Settings: true when it is open (and now on <paramref name="id"/>).</summary>
    private bool SelectWebSettingsCategory(string id)
    {
        if (!webSurfaces.TryGetValue("settings", out var surface)) return false;
        surface.Navigate?.Invoke(id);
        return true;
    }

    private void CloseWebSettings() => webSettingsClosed?.TrySetResult();

    // ── The setup screen ──

    /// <summary>OpenSetup's window in the page: the same list (BuildSetupContent), its never-show box and its Later button.</summary>
    private void ShowWebSetup()
    {
        if (webSurfaces.ContainsKey("setup")) { RedrawSetup(); return; }
        var surface = OpenNativeSurface("setup", "surface");
        var list = BuildSetupContent();
        var neverShow = new CheckBox { Content = Locale.Get("setup.button.neverShow"), MinWidth = 0 };
        AutomationProperties.SetAutomationId(neverShow, "setup-never-show");
        HostNative(surface, list); HostNative(surface, neverShow);
        surface.Button = _ =>
        {
            if (neverShow.IsChecked == true) SetupFirstRun.SetNeverShow();
            ForgetSetupList(list); setupFocus = null;
            CloseNativeSurface("setup");
        };
        surface.Model = () => new
        {
            kind = "setup", title = Locale.Get("setup.title"), subtitle = Locale.Get("setup.windows.subtitle"), close = Locale.Get("setup.button.later"),
            sections = new[] { new { title = (string?)null, beta = false, node = surface.Reader.Node(list) } },
            footer = surface.Reader.Node(neverShow), width = SetupWindowWidth, height = SetupWindowHeight,
        };
        PushNative(surface);
    }

    // ── Dialogs ──

    private readonly Dictionary<ContentDialog, (TaskCompletionSource<ContentDialogResult> Answer, Func<bool>? CanClose)> webContentDialogs = [];

    /// <summary>
    /// A ContentDialog of the window's flows: shown as the WinUI dialog, or in the web shell drawn by the page from the same
    /// content (its controls live, its buttons the dialog's own words and enabled states). <paramref name="opened"/> runs where
    /// the dialog's Opened would; <paramref name="canClose"/> refuses Esc and the buttons as a Closing handler would;
    /// <paramref name="accept"/> refuses a button as a PrimaryButtonClick handler setting Cancel would (the WinUI path keeps its
    /// own handlers).
    /// </summary>
    internal async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, Action? opened = null, Func<bool>? canClose = null, Func<ContentDialogResult, bool>? accept = null)
    {
        if (webShell is null) return await dialog.ShowAsync();
        var id = "dialog-" + ++webNativeNext;
        var surface = OpenNativeSurface(id, "dialog");
        var answer = new TaskCompletionSource<ContentDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = dialog.Content; dialog.Content = null;
        if (content is FrameworkElement element) HostNative(surface, element);
        double? Size(string key) => dialog.Resources.TryGetValue(key, out var value) && value is double size ? size : null;
        object Button(string name, string? label, bool enabled, bool chosen) => new { id = name, label, disabled = !enabled, accent = chosen };
        surface.Model = () => new
        {
            kind = "dialog", title = dialog.Title as string ?? WebNativeReader.Words(dialog.Title),
            sections = new[] { new { title = (string?)null, beta = false, node = content is string words ? new Dictionary<string, object?> { ["t"] = "text", ["text"] = words, ["selectable"] = true } : surface.Reader.Node(content) } },
            buttons = new[]
            {
                string.IsNullOrEmpty(dialog.CloseButtonText) ? null : Button("close", dialog.CloseButtonText, true, dialog.DefaultButton == ContentDialogButton.Close),
                string.IsNullOrEmpty(dialog.SecondaryButtonText) ? null : Button("secondary", dialog.SecondaryButtonText, dialog.IsSecondaryButtonEnabled, dialog.DefaultButton == ContentDialogButton.Secondary),
                string.IsNullOrEmpty(dialog.PrimaryButtonText) ? null : Button("primary", dialog.PrimaryButtonText, dialog.IsPrimaryButtonEnabled, dialog.DefaultButton == ContentDialogButton.Primary),
            }.OfType<object>().ToList(),
            width = Size("ContentDialogMaxWidth"), height = Size("ContentDialogMaxHeight"),
        };
        surface.Button = name =>
        {
            var result = name switch { "primary" => ContentDialogResult.Primary, "secondary" => ContentDialogResult.Secondary, _ => ContentDialogResult.None };
            if (result == ContentDialogResult.Primary && !dialog.IsPrimaryButtonEnabled || result == ContentDialogResult.Secondary && !dialog.IsSecondaryButtonEnabled) return;
            if (canClose?.Invoke() == false || accept?.Invoke(result) == false) { PushNative(surface); return; }
            answer.TrySetResult(result);
        };
        webContentDialogs[dialog] = (answer, canClose);
        try
        {
            PushNative(surface);
            opened?.Invoke();
            return await answer.Task;
        }
        finally
        {
            webContentDialogs.Remove(dialog);
            CloseNativeSurface(id);
            if (content is not null && dialog.Content is null) { if (content is FrameworkElement hosted && hosted.Parent is Border holder) holder.Child = null; dialog.Content = content; }
        }
    }

    /// <summary>ContentDialog.Hide for a dialog shown by <see cref="ShowDialogAsync"/>: the page's copy closes as None (unless its canClose refuses, as Closing would).</summary>
    internal void HideDialog(ContentDialog? dialog)
    {
        if (dialog is null) return;
        if (webContentDialogs.TryGetValue(dialog, out var shown)) { if (shown.CanClose?.Invoke() != false) shown.Answer.TrySetResult(ContentDialogResult.None); }
        else dialog.Hide();
    }

    // ── The status bar's companion controls ──

    /// <summary>The agent status popover in the page (BuildCompanionControls' body): the same rows and pet switch, read live.</summary>
    private object? OpenWebCompanionStatus()
    {
        if (companionStatusBody is null) return null;
        var surface = OpenNativeSurface("companion", "popover");
        companionStatusVisible = true; RefreshCompanionControls();
        surface.Button = _ => { companionStatusVisible = false; CloseNativeSurface("companion"); };
        surface.Model = () => new { kind = "companion", title = Locale.Get("companion.status.title"), sections = new[] { new { title = (string?)null, beta = false, node = surface.Reader.Node(companionStatusBody) } } };
        PushNative(surface);
        return true;
    }

    /// <summary>The paw and the capsule of the status bar (RefreshCompanionControls), for the frame's model.</summary>
    private object? WebCompanionControls() => companionToggleControl is null ? null : new
    {
        petOn = companionPreferences.Enabled, petLabel = AutomationProperties.GetName(companionToggleControl), petHelp = ToolTipService.GetToolTip(companionToggleControl) as string,
        busy = companionStatusBusy?.Visibility == Visibility.Visible, count = companionStatusNumber.Visibility == Visibility.Visible ? companionStatusNumber.Text : null,
        statusLabel = AutomationProperties.GetName(companionStatusControl) is { Length: > 0 } name ? name : Locale.Get("companion.status.title"),
    };

    // ── Smoke ──

    /// <summary>
    /// The web smoke's Settings part: the sidebar's gear opens Settings in the page; every category is visited and each of its
    /// sections (SettingsGroups' titles) must show its title and something read off its form, the category at least one control;
    /// the theme picker is switched to the other theme (the state and the page follow) and back; Settings closes. Then the plugin
    /// browser over the smoke's fake reader (no CLI runs), the setup screen over fixed probe results (nothing on this PC is probed),
    /// the terminal history dialog and a model-id-style dialog with a typed field and a refused button are drawn by the page and
    /// answered there; the companion popover opens from the status bar. No network, no CLI, no clipboard.
    /// </summary>
    private async Task<bool> RunWebSettingsSmoke(Microsoft.Web.WebView2.Core.CoreWebView2 core, Workspace workspace, string shellPane, Dictionary<string, object?> result)
    {
        async Task<JsonElement> Eval(string script) => JsonDocument.Parse(await core.ExecuteScriptAsync(script)).RootElement.Clone();
        async Task<JsonElement> Probe(string script) => JsonDocument.Parse(JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("JSON.stringify(" + script + ")")) ?? "null").RootElement.Clone();
        async Task<bool> Until(Func<Task<bool>> done, double seconds = 15)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) { if (await done()) return true; await Task.Delay(50); }
            return false;
        }
        var checks = new Dictionary<string, object?>(); result["settings"] = checks;
        const string Form = "(() => { const v = document.querySelector('[data-native-view=settings]'); if (!v) return null; const form = v.querySelector('[data-testid=settings-form]'); "
            + "return { category: form?.dataset.category ?? null, nav: [...v.querySelectorAll('.native-nav-row[data-category]')].map(e => e.dataset.category), "
            + "sections: [...(form?.querySelectorAll('[data-testid=native-section]') ?? [])].map(s => ({ title: s.dataset.title ?? null, heading: s.querySelector('.native-section-title')?.textContent ?? '', controls: s.querySelectorAll('[data-native-id]').length, nodes: s.querySelectorAll('.native-node').length })) }; })()";
        var passed = true;
        // Settings from the sidebar's gear.
        await Eval("document.querySelector('[data-testid=sidebar-settings]')?.click()");
        var opened = await Until(async () => (await Probe(Form)).ValueKind == JsonValueKind.Object && webSurfaces.ContainsKey("settings"));
        checks["opened"] = opened; passed &= opened;
        var categories = new Dictionary<string, object?>(); checks["categories"] = categories;
        foreach (var category in SettingsNavigation.Available)
        {
            var titles = SettingsGroups(category.Id).Select(g => g.Title).ToList();
            await Eval("document.querySelector('[data-native-view=settings] .native-nav-row[data-category=\"" + category.Id + "\"]')?.click()");
            JsonElement page = default;
            bool Shown(JsonElement form)
            {
                if (form.ValueKind != JsonValueKind.Object || form.Text("category") != category.Id || !form.TryGetProperty("sections", out var list)) return false;
                var sections = list.EnumerateArray().ToList();
                return titles.All(title => sections.Any(s => s.Text("title") == title && s.Text("heading")?.StartsWith(title, StringComparison.Ordinal) == true && s.GetProperty("nodes").GetInt32() > 0))
                    && sections.Sum(s => s.GetProperty("controls").GetInt32()) > 0;
            }
            var shown = await Until(async () => Shown(page = await Probe(Form)), 10);
            categories[category.Id] = new Dictionary<string, object?> { ["shown"] = shown, ["saved"] = service.Snapshot.SettingsPane == category.Id, ["form"] = page.ValueKind == JsonValueKind.Undefined ? null : page.Clone() };
            passed &= shown;
        }
        // One harmless setting: the theme picker (Settings › General › Display), switched and switched back.
        await Eval("document.querySelector('[data-native-view=settings] .native-nav-row[data-category=\"general\"]')?.click()");
        await Until(async () => (await Probe(Form)).Text("category") == "general", 10);
        var before = service.Snapshot.Theme == "light" ? "light" : "dark"; var other = before == "light" ? "dark" : "light";
        // The picker's segments are dark then light (BuildDisplaySection).
        static string Pick(string theme) => "(() => { const b = [...document.querySelectorAll('[data-native-view=settings] [data-aid=settings-theme] button[role=radio]')]; const target = b[" + (theme == "dark" ? "0" : "1") + "]; if (!target) return false; target.click(); return true; })()";
        var clicked = (await Eval(Pick(other))).ValueKind == JsonValueKind.True;
        var switched = clicked && await Until(async () => service.Snapshot.Theme == other && (await Probe("document.documentElement.dataset.theme")).GetString() == other, 10);
        await Eval(Pick(before));
        var restored = await Until(async () => (service.Snapshot.Theme == "light" ? "light" : "dark") == before && (await Probe("document.documentElement.dataset.theme")).GetString() == before, 10);
        checks["themeSwitched"] = switched; checks["themeRestored"] = restored; passed &= switched && restored;
        // Closed from its footer: the page's view goes and the window holds no dialog.
        await Eval("document.querySelector('[data-native-view=settings] [data-testid=settings-close]')?.click()");
        var closedSettings = await Until(async () => !webSurfaces.ContainsKey("settings") && !dialogOpen && (await Probe("!!document.querySelector('[data-native-view=settings]')")).ValueKind == JsonValueKind.False);
        checks["closed"] = closedSettings; passed &= closedSettings;

        const string Top = "(() => { const v = [...document.querySelectorAll('[data-native-view^=dialog-]')].pop(); return v ? { title: v.querySelector('.sheet-title')?.textContent ?? '', controls: v.querySelectorAll('[data-native-id]').length, text: v.textContent } : null; })()";
        // The plugin browser over the fake reader: drawn in the page, its fixture loaded, closed through the dialog's Hide.
        var readerBefore = smokeReaderFactory;
        try
        {
            smokeReaderFactory = _ => new FakeMarketplaceReader(PluginSmokeSnapshot);
            var showing = ShowPluginBrowser("claude", workspace);
            var plugins = await Until(async () => { var page = await Probe(Top); return page.ValueKind == JsonValueKind.Object && page.GetProperty("controls").GetInt32() >= 2; });
            checks["pluginsView"] = (await Probe(Top)).Clone();
            await Until(() => Task.FromResult(webContentDialogs.Count > 0 && webContentDialogs.Values.All(d => d.CanClose?.Invoke() != false)), 10);
            foreach (var dialog in webContentDialogs.Keys.ToList()) HideDialog(dialog);
            var browser = await showing.WaitAsync(TimeSpan.FromSeconds(10));
            checks["plugins"] = plugins && browser.IsReady; passed &= plugins && browser.IsReady;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { checks["pluginsError"] = ex.Message; passed = false; }
        finally { smokeReaderFactory = readerBefore; }

        // The setup screen over fixed probe results: drawn in the page, closed with Later.
        try
        {
            if (LoadSetupCatalog() is { } catalog)
            {
                setupFixedResults = catalog.Items.Where(item => item.Entry(SetupPlatform.Windows) is not null).ToDictionary(item => item.Id, _ => SetupProbeResult.Missing, StringComparer.Ordinal);
                setupResults.Clear(); setupSelection.Clear(); setupDidPreselect = false;
                OpenSetup();
                const string Setup = "(() => { const v = document.querySelector('[data-native-view=setup]'); return v ? { controls: v.querySelectorAll('[data-native-id]').length, title: v.querySelector('.sheet-title')?.textContent ?? '' } : null; })()";
                var setup = await Until(async () => { var page = await Probe(Setup); return page.ValueKind == JsonValueKind.Object && page.GetProperty("controls").GetInt32() >= 2; });
                checks["setupView"] = (await Probe(Setup)).Clone();
                await Eval("document.querySelector('[data-native-view=setup] [data-testid=native-close]')?.click()");
                var setupClosed = await Until(async () => !webSurfaces.ContainsKey("setup") && (await Probe("!!document.querySelector('[data-native-view=setup]')")).ValueKind == JsonValueKind.False);
                checks["setup"] = setup && setupClosed; passed &= setup && setupClosed;
            }
            else { checks["setup"] = false; passed = false; }
        }
        finally { setupFixedResults = null; setupResults.Clear(); setupSelection.Clear(); setupDidPreselect = false; setupFocus = null; }

        // The terminal history: the page's dialog, closed with its Close button.
        var history = ShowTerminalHistory(shellPane);
        var historyShown = await Until(async () => { var page = await Probe(Top); return page.ValueKind == JsonValueKind.Object && page.Text("title") == Locale.Get("terminal.history.title") && page.Text("text")?.Contains("fixture answer 2", StringComparison.Ordinal) == true; });
        await Eval("[...document.querySelectorAll('[data-native-view^=dialog-]')].pop()?.querySelector('[data-testid=native-button-close]')?.click()");
        var historyClosed = await Until(async () => history.IsCompleted && (await Probe("document.querySelectorAll('[data-native-view^=dialog-]').length")).GetInt32() == 0);
        checks["terminalHistory"] = historyShown && historyClosed; passed &= historyShown && historyClosed;

        // A model-id-style dialog: the typed words reach the TextBox, a refused Primary keeps it open, accepted words close it as Primary.
        var field = new TextBox { Header = Locale.Get("composer.model.idHeader") }; var note = new TextBlock();
        var entry = new ContentDialog { Title = Locale.Get("composer.model.enterIdTitle"), Content = new StackPanel { Children = { field, note } }, PrimaryButtonText = Locale.Get("composer.model.select"), CloseButtonText = Locale.Get("settings.run.cancelButton"), XamlRoot = root.XamlRoot };
        bool Valid() { if (Wire.Model(field.Text.Trim())) return true; note.Text = Locale.Get("composer.model.invalidName"); return false; }
        var asked = ShowDialogAsync(entry, accept: answer => answer != ContentDialogResult.Primary || Valid());
        static string Type(string words) => "(() => { const f = [...document.querySelectorAll('[data-native-view^=dialog-]')].pop()?.querySelector('input.native-field'); if (!f) return false; "
            + "Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(f, " + JsonSerializer.Serialize(words) + "); f.dispatchEvent(new Event('input', { bubbles: true })); return true; })()";
        const string Primary = "[...document.querySelectorAll('[data-native-view^=dialog-]')].pop()?.querySelector('[data-testid=native-button-primary]')?.click()";
        await Until(async () => (await Eval(Type("bad model id"))).ValueKind == JsonValueKind.True, 10);
        var typed = await Until(() => Task.FromResult(field.Text == "bad model id"), 10);
        await Eval(Primary);
        var refused = await Until(async () => note.Text.Length > 0 && (await Probe(Top)).Text("text")?.Contains(note.Text, StringComparison.Ordinal) == true, 10) && !asked.IsCompleted;
        await Eval(Type("claude-smoke-model"));
        await Until(() => Task.FromResult(field.Text == "claude-smoke-model"), 10);
        await Eval(Primary);
        var accepted = await Until(() => Task.FromResult(asked.IsCompletedSuccessfully && asked.Result == ContentDialogResult.Primary), 10);
        checks["modelDialog"] = new Dictionary<string, object?> { ["typed"] = typed, ["refused"] = refused, ["accepted"] = accepted };
        passed &= typed && refused && accepted;

        // The status bar's companion controls: the popover opens in the page and Esc closes it (when the window built the controls).
        if (companionStatusBody is not null)
        {
            await Eval("document.querySelector('[data-testid=companion-status]')?.click()");
            var popover = await Until(async () => (await Probe("!!document.querySelector('[data-native-view=companion] [data-native-id]')")).ValueKind == JsonValueKind.True, 10);
            await Eval("document.querySelector('[data-native-view=companion]')?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))");
            var gone = await Until(async () => !webSurfaces.ContainsKey("companion") && (await Probe("!!document.querySelector('[data-native-view=companion]')")).ValueKind == JsonValueKind.False, 10);
            checks["companion"] = popover && gone; passed &= popover && gone;
        }
        checks["passed"] = passed;
        return passed;
    }
}
