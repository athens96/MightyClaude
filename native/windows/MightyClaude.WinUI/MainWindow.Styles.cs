using System.Text;
using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    // 마이티 스타일 (M/StyleSettingsSection.swift:16-108): the lock banner while the trust file cannot be
    // read, the register and rescan buttons, the 11pt explanation, then a row per style — its name (12
    // medium) beside the state capsule, the 10pt figures, the path in 10pt mono, its small buttons — and
    // a faded row per file that was refused.
    private StackPanel BuildStylesSection()
    {
        var rows = new StackPanel(); const string styleRow = "style", lockRow = "style-lock";
        AutomationProperties.SetAutomationId(rows, "settings-style-rows");
        Button register = null!; Border actionsRow = null!;
        register = Button(Locale.Get("settings.styles.registerButton"), async () => await Act(async () =>
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(settingsWindow ?? this));
            var file = await picker.PickSingleFileAsync(); if (file is null) return;
            var bytes = await Task.Run(() => StyleFiles.Read(Path.GetDirectoryName(file.Path)!, Path.GetFileName(file.Path)) ?? throw new IOException("Cannot read style manifest."));
            var manifest = StyleManifestDecoder.Decode(bytes);
            var candidate = new RegisteredStyle(manifest, "user", file.Path, null, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), "pending", bytes);
            await ShowStyleApproval(candidate, onApprove: async () =>
            {
                await Task.Run(() =>
                {
                    var folder = Path.Combine(StateDirectory, "styles"); Directory.CreateDirectory(folder);
                    var profileRoot = WorkspaceFiles.RealPath(StateDirectory); var stylesRoot = WorkspaceFiles.RealPath(folder);
                    if (profileRoot is null || stylesRoot is null || !WorkspaceFiles.Contains(stylesRoot, profileRoot) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
                    var workspace = service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == service.Snapshot.ActiveWorkspaceId)?.Path ?? StateDirectory;
                    var registry = StyleRegistry.Load(StateDirectory, workspace);
                    if (registry.TrustLocked) throw new IOException(Locale.Get("settings.styles.lockBanner"));
                    if (registry.Styles.Any(s => s.Id == manifest.Id)) throw new IOException(Locale.Get("styles.registration.duplicate"));
                    var destination = Path.Combine(stylesRoot, manifest.Id + ".json");
                    // Only the reviewed snapshot is copied, after both consent
                    // presses. Cancellation leaves no installed manifest.
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
                    var installed = candidate with { Path = destination };
                    if (!StyleRegistry.Unchanged(installed)) throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
                    new StyleTrustStore(Path.Combine(StateDirectory, "style-trust")).Decide(installed, "approved");
                });
            });
            await Populate();
        }));
        SettingsPush(register); AutomationProperties.SetAutomationId(register, "settings-style-register");
        var rescan = SettingsPush(Button(Locale.Get("settings.styles.rescanButton"), () => Act(Populate)));
        AutomationProperties.SetAutomationId(rescan, "settings-style-rescan");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        actions.Children.Add(register); actions.Children.Add(rescan);
        actionsRow = SettingsRow(rows, actions);
        SettingsRow(rows, SettingsText(Locale.Get("settings.styles.description"), 11, DesignToken.Ink2));
        // A file's path: 10pt mono in the tertiary ink (M/StyleSettingsSection.swift:69, 103).
        TextBlock Path10(string path)
        {
            var words = SettingsTertiary(path, mono: true, selectable: true);
            words.TextWrapping = TextWrapping.NoWrap; words.TextTrimming = TextTrimming.CharacterEllipsis;
            return words;
        }
        async Task Populate()
        {
            var workspace = service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == service.Snapshot.ActiveWorkspaceId)?.Path ?? StateDirectory;
            var registry = await Task.Run(() => StyleRegistry.Load(StateDirectory, workspace)); register.IsEnabled = !registry.TrustLocked;
            var banners = new List<FrameworkElement>();
            if (registry.TrustLocked)
            {
                // M/StyleSettingsSection.swift:42-51: the file that is locked, in 11pt medium waitText after its mark, over what to do about it.
                var banner = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
                AutomationProperties.SetAutomationId(banner, "settings-style-locked");
                var file = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs };
                file.Children.Add(SettingsSymbol("", 11, DesignToken.WaitText));
                file.Children.Add(SettingsText(Path.Combine(StateDirectory, "style-trust", "approvals.json"), 11, DesignToken.WaitText, medium: true, selectable: true));
                banner.Children.Add(file); banner.Children.Add(SettingsText(Locale.Get("settings.styles.lockBanner"), 11, DesignToken.Ink2, selectable: true));
                banners.Add(banner);
            }
            ReplaceSettingsRows(rows, lockRow, banners, actionsRow);
            var contents = new List<FrameworkElement>();
            foreach (var style in registry.Styles)
            {
                var row = new StackPanel { Spacing = DesignMetrics.Spacing.Xs, Margin = new Thickness(0, DesignMetrics.Spacing.Xxs, 0, DesignMetrics.Spacing.Xxs) };
                AutomationProperties.SetAutomationId(row, "settings-style-" + style.Id);
                var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
                head.Children.Add(SettingsText(StylePresentation.Name(style), 12, medium: true));
                // The capsule of a style the app ships reads the bundled badge's one word here, as the Mac's list does (M/Styles/StyleSurfaces.swift:248).
                head.Children.Add(SettingsCapsule(style.Approval == "preApproved" ? Locale.Get("settings.toolkit.bundledBadge") : StylePresentation.State(style.Approval), brushes.Brush(DesignToken.Ink2), brushes.Subtle));
                row.Children.Add(head);
                row.Children.Add(SettingsText(style.Id + " · " + Locale.Get("settings.styles.hashDetailTemplate", new Dictionary<string,string>{{"hash",style.Hash[..12]}}) + " · " + Locale.Get("settings.styles.actionCountTemplate", new Dictionary<string,string>{{"count",style.Manifest.Actions.Length.ToString()}}) + " · " + Locale.Get("settings.styles.autoAllowCountTemplate", new Dictionary<string,string>{{"count",StyleManifest.Items(style.Manifest.Root,"autoAllow").Length.ToString()}}), 10, DesignToken.Ink2));
                row.Children.Add(Path10(style.Path));
                var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
                controls.Children.Add(SettingsPush(Button(Locale.Get("settings.styles.viewButton"), async () => { await ShowStyleApproval(style, readOnly: true); }), SettingsControlSize.Small));
                if (style.Source != "bundled")
                {
                    var action = style.Approval == "revoked" ? "unblock" : style.Runnable ? "revoked" : "approved";
                    var key = action == "unblock" ? "settings.styles.unblockButton" : action == "revoked" ? "settings.styles.revokeButton" : "settings.styles.allowButton";
                    var decide = SettingsPush(Button(Locale.Get(key), () => Act(async () =>
                    {
                        if (action == "approved") await ShowStyleApproval(style);
                        else await Task.Run(() => new StyleTrustStore(Path.Combine(StateDirectory, "style-trust")).Decide(style, action));
                        await Populate();
                    })), SettingsControlSize.Small); decide.IsEnabled = !registry.TrustLocked; controls.Children.Add(decide);
                }
                if (style.Source == "user")
                {
                    var remove = SettingsPush(Button(Locale.Get("settings.styles.removeButton"), () => Act(async () =>
                    {
                        var confirm = StyledDialog(new ContentDialog { Title = Locale.Get("settings.styles.removeButton"), Content = new TextBlock { Text = style.Path, TextWrapping = TextWrapping.Wrap }, XamlRoot = SettingsXamlRoot, PrimaryButtonText = Locale.Get("settings.styles.removeButton"), CloseButtonText = Locale.Get("guidedPanel.cancelButton"), DefaultButton = ContentDialogButton.Close });
                        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                        await Task.Run(() =>
                        {
                            if (!StyleRegistry.Unchanged(style)) throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
                            var root = WorkspaceFiles.RealPath(Path.Combine(StateDirectory, "styles")); if (root is null || !WorkspaceFiles.Contains(style.Path, root)) throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
                            new StyleTrustStore(Path.Combine(StateDirectory, "style-trust")).Decide(style, "revoked"); File.Delete(style.Path);
                        }); await Populate();
                    })), SettingsControlSize.Small, destructive: true); remove.IsEnabled = !registry.TrustLocked; controls.Children.Add(remove);
                }
                row.Children.Add(controls); contents.Add(row);
            }
            foreach (var rejection in registry.Rejections)
            {
                // M/StyleSettingsSection.swift:99-108: why the file was refused over its path, at 0.7.
                var refused = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs, Margin = new Thickness(0, DesignMetrics.Spacing.Xxs, 0, DesignMetrics.Spacing.Xxs), Opacity = 0.7 };
                AutomationProperties.SetAutomationId(refused, "settings-style-rejected");
                refused.Children.Add(SettingsText(rejection.Code + " · " + rejection.Message, 11, DesignToken.Ink2, selectable: true)); refused.Children.Add(Path10(rejection.Path));
                contents.Add(refused);
            }
            ReplaceSettingsRows(rows, styleRow, contents);
        }
        rows.Loaded += async (_, _) => await Act(Populate); return rows;
    }
    private static TextBox StyleContents(RegisteredStyle style) => new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 380, MinWidth = 300, FontFamily = new FontFamily(DesignMetrics.Font.Mono), Text = Encoding.UTF8.GetString(style.Bytes.Span) };
    private Task ApproveStyle(RegisteredStyle style) => Task.Run(() =>
    {
        if (!StyleRegistry.Unchanged(style)) throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
        new StyleTrustStore(Path.Combine(StateDirectory, "style-trust")).Decide(style, "approved");
    });
    private sealed partial class PaneView
    {
        /// <summary>The Mighty rows over the editor (M/SessionPaneView.swift:520-541): the style row, a pending style's strip and the guide panel, 9 apart like the rest of the composer.</summary>
        private readonly StackPanel styleHost=new(){Spacing=DesignMetrics.Spacing.Sm,Visibility=Visibility.Collapsed};
        /// <summary>The style menu's chip (M/GuidedPanel.swift:35-43): the style's name in 10 medium, its source badge and a 7pt chevron, padding h7 v3 on the subtle wash at radius 6.</summary>
        private readonly Button stylePicker=new(){HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,MinWidth=0,MinHeight=0,Padding=new(DesignMetrics.Spacing.Sm,DesignMetrics.Spacing.Xxs,DesignMetrics.Spacing.Sm,DesignMetrics.Spacing.Xxs),BorderThickness=new(0),CornerRadius=new(DesignMetrics.Radius.Segment)};
        private readonly TextBlock stylePickerLabel=new(){Text="CLI",FontSize=10,FontWeight=FontWeights.Medium,TextWrapping=TextWrapping.NoWrap,VerticalAlignment=VerticalAlignment.Center};
        private readonly Border stylePickerBadge=new(){Visibility=Visibility.Collapsed,VerticalAlignment=VerticalAlignment.Center};
        private readonly StackPanel stylePickerFace=new(){Orientation=Orientation.Horizontal,Spacing=DesignMetrics.Spacing.Xs};
        /// <summary>What the chosen style is for, beside its chip: the manifest's subtitle in 10pt in the tertiary ink (M/SessionPaneView.swift:523).</summary>
        private readonly TextBlock styleHint=new(){FontSize=10,TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};
        /// <summary>The one-line strip a style that still needs a yes leaves in the panel (M/GuidedActionChip.swift:62-80).</summary>
        private readonly StackPanel styleNotice=new(){Orientation=Orientation.Horizontal,Spacing=DesignMetrics.Spacing.Sm,Margin=new(DesignMetrics.Inset.ComposerInnerH,0,DesignMetrics.Inset.ComposerInnerH,0),Visibility=Visibility.Collapsed};
        /// <summary>The guide panel (M/GuidedPanel.swift:101-123): its blocks 8 apart, padding h12 t8.</summary>
        private readonly StackPanel guidedBody=new(){Spacing=DesignMetrics.Spacing.Sm,Margin=new(DesignMetrics.Inset.ComposerInnerH,DesignMetrics.Spacing.Sm,DesignMetrics.Inset.ComposerInnerH,0),Visibility=Visibility.Collapsed};
        /// <summary>The command Enter is about to send, before the editor (M/SessionPaneView.swift:567-574): 10pt mono accent on accent × 0.12, padding h6 v3, radius 5.</summary>
        private readonly Border styleEnterChip=new(){Padding=new(DesignMetrics.Spacing.Sm,DesignMetrics.Spacing.Xxs,DesignMetrics.Spacing.Sm,DesignMetrics.Spacing.Xxs),CornerRadius=new(DesignMetrics.Radius.FileRow),VerticalAlignment=VerticalAlignment.Top,Visibility=Visibility.Collapsed};
        /// <summary>The armed-prefix chip's accent tint (M/SessionPaneView.swift:571).</summary>
        private const double StyleEnterTint=0.12;
        private StyleRegistry? styleRegistry;
        private RegisteredStyle? activeStyle;
        private StyleStateReading? styleReading;
        private StylePrerequisiteResult? stylePrerequisites;
        private Dictionary<string,string> styleCapabilities=[];
        private IReadOnlyList<StyleAttachmentItem> styleCapabilityFiles=[];
        private readonly Dictionary<string,Button> styleActionButtons=[];
        private readonly TextBlock styleEnterPrefix=new(){FontSize=10,FontFamily=new FontFamily(DesignMetrics.Font.Mono),TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=260};
        private string? styleGroup;
        private bool styleStartingNew,styleLoading;
        private readonly DispatcherTimer styleTimer=new(){Interval=TimeSpan.FromSeconds(2)};
        private string? loadedStyleKey;
        private void InitializeStyles()
        {
            var b=owner.brushes;
            AutomationProperties.SetAutomationId(stylePicker,"mighty-style-"+id);AutomationProperties.SetName(stylePicker,Locale.Get("guidedPanel.stylesMenuAccessibility"));ToolTipService.SetToolTip(stylePicker,Locale.Get("guidedPanel.stylesMenuHelp"));
            stylePickerLabel.Foreground=b.Brush(DesignToken.Ink);
            stylePickerFace.Children.Add(stylePickerLabel);stylePickerFace.Children.Add(stylePickerBadge);stylePickerFace.Children.Add(ComposerGlyph.ChevronDown(7,0.9).Ink(b.Brush(DesignToken.Ink2)).View);
            stylePicker.Content=stylePickerFace;owner.PaintPlainButton(stylePicker,b.Subtle,b.Subtle,ink:b.Brush(DesignToken.Ink));
            // A running pane's picker is off, and a disabled plain button is drawn at half strength (M/GuidedPanel.swift:45).
            stylePicker.IsEnabledChanged+=(_,_)=>stylePicker.Opacity=stylePicker.IsEnabled?1:DisabledDim;
            styleHint.Foreground=b.Tertiary;
            var row=new Grid{ColumnSpacing=DesignMetrics.Spacing.Sm,Margin=new(DesignMetrics.Inset.ComposerInnerH,DesignMetrics.Spacing.Sm,DesignMetrics.Inset.ComposerInnerH,0)};row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
            row.Children.Add(stylePicker);Grid.SetColumn(styleHint,1);row.Children.Add(styleHint);
            styleEnterChip.Child=styleEnterPrefix;styleEnterChip.Background=b.Brush(DesignToken.Accent,StyleEnterTint);styleEnterPrefix.Foreground=b.Brush(DesignToken.Accent);
            AutomationProperties.SetAutomationId(styleEnterChip,"mighty-enter-armed-"+id);
            styleHost.Children.Add(row);styleHost.Children.Add(styleNotice);styleHost.Children.Add(guidedBody);
            styleTimer.Tick+=async(_,_)=>await LoadStyles();styleHost.Loaded+=(_,_)=>{if(!owner.options.SmokeTest)styleTimer.Start();};styleHost.Unloaded+=(_,_)=>styleTimer.Stop();
        }
        private void RefreshStyles()
        {
            if(owner.closing||!owner.service.Snapshot.Sessions.Any(p=>p.Id==id))return;
            var pane=Session;var visible=pane.Kind=="claude"&&pane.Provider=="claude"&&pane.AgentViewMode=="mighty";
            styleHost.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
            if(!visible){activeStyle=null;return;}
            var key=pane.MightyStyle+"|"+pane.MightyStyleHash+"|"+pane.MightyStyleSince+"|"+pane.Logs.Count+"|"+pane.Status;
            if(loadedStyleKey!=key&&!styleLoading)_=LoadStyles();
            RenderGuidedStyle();
        }
        private async Task LoadStyles()
        {
            if(styleLoading||owner.closing||!owner.service.Snapshot.Sessions.Any(p=>p.Id==id))return;
            var pane=Session;if(pane.Kind!="claude"||pane.Provider!="claude"||pane.AgentViewMode!="mighty")return;
            styleLoading=true;var workspace=Workspace.Path;var key=pane.MightyStyle+"|"+pane.MightyStyleHash+"|"+pane.MightyStyleSince+"|"+pane.Logs.Count+"|"+pane.Status;
            try
            {
                var data=await Task.Run(()=>
                {
                    var registry=StyleRegistry.Load(owner.StateDirectory,workspace);var chosen=registry.Runnable(pane.MightyStyle,pane.MightyStyleHash);
                    var since=DateTimeOffset.TryParse(pane.MightyStyleSince,out var date)?date:(DateTimeOffset?)null;
                    return (Registry:registry,Chosen:chosen,Reading:chosen is null?null:StyleStateEngine.Read(chosen.Manifest,workspace,since,pane),Prerequisites:chosen is null?null:StylePrerequisites.Read(chosen.Manifest,workspace,owner.options.SmokeTest?owner.StateDirectory:null),Capabilities:chosen is null?(new Dictionary<string,string>(),new List<StyleAttachmentItem>()):StylePrerequisites.Capabilities(chosen.Manifest,workspace));
                });
                if(owner.closing||!owner.service.Snapshot.Sessions.Any(p=>p.Id==id)||Session.MightyStyle!=pane.MightyStyle||Session.MightyStyleHash!=pane.MightyStyleHash||Session.MightyStyleSince!=pane.MightyStyleSince)return;
                styleRegistry=data.Registry;activeStyle=data.Chosen;styleReading=data.Reading;stylePrerequisites=data.Prerequisites;styleCapabilities=data.Capabilities.Item1;styleCapabilityFiles=data.Capabilities.Item2;loadedStyleKey=key;
                if(styleGroup is null&&activeStyle is {} chosen)styleGroup=chosen.Evaluator.InitialGroup(styleCapabilities)?.Id;
                // The style menu (M/GuidedPanel.swift:26-33, MightyCore/Styles/StyleSurfaces.swift:195-222): CLI, then every style this
                // pane may see — its name, its source unless built in and, while it waits for a yes or is blocked, why — the current one checked.
                var menu=new MenuFlyout{Placement=Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft};menu.Items.Add(Choice("CLI",()=>ChooseStyle(null),pane.MightyStyle is null));
                foreach(var candidate in data.Registry.Styles)
                {
                    var why=candidate.Approval switch
                    {
                        "pending"=>" · "+StylePresentation.State(candidate.Approval)+" · "+Locale.Get("settings.styles.actionCountTemplate",new Dictionary<string,string>{{"count",candidate.Manifest.Actions.Length.ToString()}})+" · "+Locale.Get("settings.styles.autoAllowCountTemplate",new Dictionary<string,string>{{"count",StyleManifest.Items(candidate.Manifest.Root,"autoAllow").Length.ToString()}}),
                        "revoked"=>" · "+StylePresentation.State(candidate.Approval)+" · "+Locale.Get("styles.menu.allowAgainInSettings"),
                        _=>""
                    };
                    var item=Choice(StylePresentation.Name(candidate)+why,()=>ChooseStyle(candidate),candidate.Id==pane.MightyStyle,candidate.Manifest.Summary);item.IsEnabled=candidate.Approval!="revoked"&&(!data.Registry.TrustLocked||candidate.Runnable);menu.Items.Add(item);
                }
                stylePicker.Flyout=menu;RenderGuidedStyle();RefreshMightyView(Session);
            }
            catch(Exception ex){owner.error.Text=ex.Message;activeStyle=null;}
            finally{styleLoading=false;}
        }
        private Task ChooseStyle(RegisteredStyle? selected)=>owner.Act(async()=>
        {
            if(Session.Status=="running"||starting)return;
            var boundId=id;var before=Session.MightyStyle;
            if(selected!=null&&!selected.Runnable&&!await owner.ShowStyleApproval(selected))return;
            if(owner.closing||!owner.service.Snapshot.Sessions.Any(p=>p.Id==boundId)||Session.Status=="running"||Session.MightyStyle!=before)return;
            if(selected!=null)
            {
                if(!StyleRegistry.Unchanged(selected))throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
                var registry=await Task.Run(()=>StyleRegistry.Load(owner.StateDirectory,Workspace.Path));if(registry.Runnable(selected.Id,selected.Hash)==null)throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
            }
            await Change(p=>p with{MightyStyle=selected?.Id,MightyStyleHash=selected?.Hash,MightyStyleSince=p.MightyStyle==selected?.Id?p.MightyStyleSince:null});styleStartingNew=false;styleGroup=null;loadedStyleKey=null;await LoadStyles();Refresh();
        });
        private StylePhase? GuidedPhase(RegisteredStyle style)=>style.Evaluator.CurrentPhase(Session.GraphRuns is {Count:>0} runs?runs.Select(r=>r.Input):Session.Logs.Where(l=>l.Kind=="user").Select(l=>l.Text),styleReading?.Files,LiveStyleReading(style).PlanStage);
        /// <summary>
        /// The cached file and event reading with the pane's own plan stage and run-state widgets added (§1.17): they
        /// come from the pane and its waiting plan, never from disk, so they are read live on every render.
        /// </summary>
        private StyleStateReading LiveStyleReading(RegisteredStyle style)=>StyleStateEngine.Live(styleReading,style.Manifest,Session,PendingPlan is not null);
        private void RefreshStyleComposer()
        {
            if(owner.closing||!owner.service.Snapshot.Sessions.Any(p=>p.Id==id))return;
            styleEnterChip.Visibility=Visibility.Collapsed;
            if(activeStyle is not {} style||Session.AgentViewMode!="mighty")return;
            var pane=Session;var evaluator=style.Evaluator;var phase=evaluator.EffectivePhase(GuidedPhase(style),styleStartingNew);
            var answering=toolPermissions.Any(p=>p.State=="pending"&&p.CanAnswerQuestions);
            // An empty placeholder is the manifest asking for the app's own (M/SessionPaneView.swift:92-95).
            if(evaluator.Placeholder(phase,pane.Status=="running"||starting,answering,evaluator.JobOpen(pane)) is {Length:>0} placeholder)input.PlaceholderText=placeholder;
            foreach(var action in style.Manifest.Actions)if(styleActionButtons.TryGetValue(action.Id,out var button))button.IsEnabled=!answering&&(!action.RequiresText||!string.IsNullOrWhiteSpace(input.Text));
            if(!answering&&evaluator.RewriteAction(input.Text,phase,pendingAttachments.Count>0,pane.Status=="running"||starting,pane.Logs.Any(l=>l.Kind=="user")||pane.GraphRuns is {Count:>0},styleStartingNew) is {} actionId)
            {
                // The chip says which command Enter is about to send (M/SessionPaneView.swift:567-574).
                var prompt=style.Manifest.Actions.First(a=>a.Id==actionId).Prompt("");
                styleEnterPrefix.Text=prompt.Trim();AutomationProperties.SetName(styleEnterChip,Locale.Get("styles.enterPrefix",new Dictionary<string,string>{{"prompt",prompt}}));styleEnterChip.Visibility=Visibility.Visible;
            }
        }
        /// <summary>
        /// A style manifest's tint name as the D ink that plays its role (M/GuidedActionChip.swift:8-20):
        /// purple agentText, teal taskText, indigo questionText, mint compactText, orange steerText,
        /// green doneText, red errText, secondary ink2, accent (or none) accent.
        /// </summary>
        /// <summary>The emphasised guided chip's tint opacity (M/GuidedActionChip.swift:46).</summary>
        private const double StyleChipTint=0.18;
        private static DesignToken StyleTint(string? tint)=>tint switch
        {
            "purple"=>DesignToken.AgentText,"teal"=>DesignToken.TaskText,"indigo"=>DesignToken.QuestionText,
            "mint"=>DesignToken.CompactText,"orange"=>DesignToken.SteerText,"green"=>DesignToken.DoneText,
            "red"=>DesignToken.ErrText,"secondary"=>DesignToken.Ink2,_=>DesignToken.Accent
        };
        /// <summary>The phase bar's inks beside the accent of the current phase: a phase already reached, and one still ahead (M/GuidedPanel.swift:148).</summary>
        private const double StepReached=0.75,StepAhead=0.6;
        /// <summary>The chosen group's tile in the group map: accent × 0.16 inside accent × 0.7 (M/GuidedPanel.swift:232-233).</summary>
        private const double GroupChosenFill=0.16,GroupChosenEdge=0.7;
        /// <summary>A chip that cannot be pressed yet (M/GuidedActionChip.swift:52).</summary>
        private const double ChipDisabled=0.45;
        /// <summary>The badge after a style that is not built in (M/GuidedActionChip.swift:83-93): 9pt <c>ink2</c> on the subtle capsule, padding h5 v1.</summary>
        private Border SourceBadge(string text)=>new(){Child=new TextBlock{Text=text,FontSize=9,Foreground=owner.brushes.Brush(DesignToken.Ink2)},Padding=new(DesignMetrics.Spacing.Xs,1,DesignMetrics.Spacing.Xs,1),CornerRadius=new(8),Background=owner.brushes.Subtle,VerticalAlignment=VerticalAlignment.Center};
        /// <summary>Tall enough for the chip rows at the narrowest pane, capped so a long catalogue scrolls (M/GuidedPanel.swift:366-369).</summary>
        private static double ChipGridHeight(int count){var rows=Math.Max(1,(count+2)/3);return Math.Min(92,rows*27+(rows-1)*5);}
        private Task InstallStyle(RegisteredStyle style,string command)=>owner.Act(async()=>
        {
            if(!StyleRegistry.Unchanged(style))throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
            var current=await Task.Run(()=>StyleRegistry.Load(owner.StateDirectory,Workspace.Path));
            if(current.Runnable(style.Id,style.Hash)==null)throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
            var actual=StyleRunPermissions.InstallCommand(command,PseudoTerminal.DefaultShell);
            var confirm=owner.StyledDialog(new ContentDialog{Title=Locale.Get("guidedPanel.installButton"),Content=new TextBox{Text=actual,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true},XamlRoot=owner.root.XamlRoot,PrimaryButtonText=Locale.Get("guidedPanel.installButton"),CloseButtonText=Locale.Get("guidedPanel.cancelButton"),DefaultButton=ContentDialogButton.Close});
            if(await confirm.ShowAsync()!=ContentDialogResult.Primary)return;
            if(!StyleRegistry.Unchanged(style)||Session.MightyStyle!=style.Id||Session.MightyStyleHash!=style.Hash||owner.service.Snapshot.ActiveWorkspaceId!=Session.WorkspaceId)
                throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
            var approved=await Task.Run(()=>StyleRegistry.Load(owner.StateDirectory,Workspace.Path));
            if(approved.Runnable(style.Id,style.Hash)==null)throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
            await owner.RunTerminalCommandAsync(actual,(StyleManifest.Text(style.Manifest.Root.GetProperty("install"),"paneTitle")??style.Manifest.Name)+" · "+StylePresentation.Name(style),autoRun:false);
        });
        /// <summary>
        /// Draws the Mighty rows over the editor (M/SessionPaneView.swift:520-541, M/GuidedPanel.swift:94-127): the style chip with
        /// what the style is for; a strip for a style that still needs a yes; and the chosen style's guide panel — the phase
        /// bar, what is missing, the state widgets, the group map, the built-in feature's files, the action chips (or a
        /// spinner while a sequence runs) and the guidance line.
        /// </summary>
        private void RenderGuidedStyle()
        {
            if(owner.closing||!owner.service.Snapshot.Sessions.Any(p=>p.Id==id))return;
            var b=owner.brushes;var ink=b.Brush(DesignToken.Ink);var ink2=b.Brush(DesignToken.Ink2);var tertiary=b.Tertiary;var accent=b.Brush(DesignToken.Accent);
            var pane=Session;stylePicker.IsEnabled=pane.Status!="running"&&!starting;
            stylePickerLabel.Text=activeStyle?.Manifest.Name??"CLI";
            var badged=activeStyle is {Source:not "bundled"};stylePickerBadge.Visibility=badged?Visibility.Visible:Visibility.Collapsed;
            if(badged){stylePickerBadge.Child=new TextBlock{Text=StylePresentation.Source(activeStyle!.Source),FontSize=9,Foreground=ink2};stylePickerBadge.Padding=new(DesignMetrics.Spacing.Xs,1,DesignMetrics.Spacing.Xs,1);stylePickerBadge.CornerRadius=new(8);stylePickerBadge.Background=b.Subtle;}
            styleHint.Text=activeStyle?.Manifest.Subtitle??Locale.Get("composer.style.free");
            guidedBody.Children.Clear();styleActionButtons.Clear();styleNotice.Children.Clear();styleNotice.Visibility=Visibility.Collapsed;guidedBody.Visibility=Visibility.Collapsed;
            if(activeStyle is not {} style)
            {
                if(pane.MightyStyle is not null)
                {
                    // The pane aims at a style that has not been said yes to: one line, and the button that opens its card (M/GuidedActionChip.swift:62-80).
                    var pending=styleRegistry?.Styles.FirstOrDefault(s=>s.Id==pane.MightyStyle);
                    styleNotice.Children.Add(new FontIcon{Glyph="",FontSize=10,Foreground=ink2,VerticalAlignment=VerticalAlignment.Center});
                    styleNotice.Children.Add(new TextBlock{Text=pending?.Manifest.Name??pane.MightyStyle,FontSize=11,FontWeight=FontWeights.Medium,Foreground=ink,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=220,VerticalAlignment=VerticalAlignment.Center});
                    if(pending is {Source:not "bundled"})styleNotice.Children.Add(SourceBadge(StylePresentation.Source(pending.Source)));
                    styleNotice.Children.Add(new TextBlock{Text=Locale.Get("guidedPanel.approvalRequired"),FontSize=11,Foreground=ink2,VerticalAlignment=VerticalAlignment.Center});
                    if(pending is not null&&pending.Approval!="revoked"&&styleRegistry?.TrustLocked!=true)styleNotice.Children.Add(SmallButton(Locale.Get("guidedPanel.viewContentsButton"),()=>ChooseStyle(pending)));
                    AutomationProperties.SetAutomationId(styleNotice,"mighty-approval-strip-"+id);styleNotice.Visibility=Visibility.Visible;
                }
                styleEnterChip.Visibility=Visibility.Collapsed;return;
            }
            var evaluator=style.Evaluator;var phase=evaluator.EffectivePhase(GuidedPhase(style),styleStartingNew);var busy=pane.Status=="running"||starting;var job=evaluator.JobOpen(pane);
            RefreshStyleComposer();
            if(style.Manifest.Phases.Length>0)
            {
                // The phase bar (M/GuidedPanel.swift:139-152): 10pt names 4 apart with 7pt chevrons in the tertiary ink between; the current one accent semibold, those reached ink × 0.75, those ahead ink2 × 0.6.
                var phases=new StackPanel{Orientation=Orientation.Horizontal,Spacing=DesignMetrics.Spacing.Xs};var ordered=style.Manifest.Phases.OrderBy(p=>p.Order).ToArray();
                for(var index=0;index<ordered.Length;index++)
                {
                    var item=ordered[index];var current=item.Id==phase?.Id;var reached=phase is not null&&item.Order<=phase.Order;
                    phases.Children.Add(new TextBlock{Text=item.Title,FontSize=10,FontWeight=current?FontWeights.SemiBold:FontWeights.Normal,Foreground=current?accent:reached?b.Brush(DesignToken.Ink,StepReached):b.Brush(DesignToken.Ink2,StepAhead),VerticalAlignment=VerticalAlignment.Center});
                    if(index<ordered.Length-1)phases.Children.Add(ComposerGlyph.ChevronRight(7,0.9).Ink(tertiary).View);
                }
                AutomationProperties.SetAutomationId(phases,"mighty-phases-"+id);AutomationProperties.SetName(phases,Locale.Get("guidedPanel.phasesAccessibility"));
                guidedBody.Children.Add(new ScrollViewer{Content=phases,HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden,HorizontalScrollMode=ScrollMode.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollMode=ScrollMode.Disabled});
            }
            guidedBody.Visibility=guidedBody.Children.Count>0?Visibility.Visible:Visibility.Collapsed;
            // The questionnaire already has its own native renderer. No style
            // action may replace it or rewrite an answer in the main composer.
            if(toolPermissions.Any(p=>p.State=="pending"&&p.CanAnswerQuestions))return;
            if(stylePrerequisites is {Ready:false} prerequisites)
            {
                // What the style still needs (M/GuidedPanel.swift:161-178): each missing thing in 11 medium, the first behind a box mark, the hint in ink2, then small buttons.
                var setup=new StackPanel{Spacing=DesignMetrics.Spacing.Xs};var first=true;
                foreach(var line in prerequisites.Missing)
                {
                    var words=new TextBlock{Text=line,FontSize=11,FontWeight=FontWeights.Medium,Foreground=ink,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
                    if(first){var lead=new StackPanel{Orientation=Orientation.Horizontal,Spacing=DesignMetrics.Spacing.Xs};lead.Children.Add(new FontIcon{Glyph="",FontSize=11,Foreground=ink,VerticalAlignment=VerticalAlignment.Center});lead.Children.Add(words);setup.Children.Add(lead);first=false;}
                    else setup.Children.Add(words);
                }
                if(prerequisites.Hint is {Length:>0} hint)setup.Children.Add(new TextBlock{Text=hint,FontSize=11,Foreground=ink2,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true});
                var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=DesignMetrics.Spacing.Sm};
                if(prerequisites.InstallCommand is {} command)
                {
                    setup.Children.Add(new Border{Child=new TextBlock{Text=command,FontSize=10,FontFamily=new FontFamily(DesignMetrics.Font.Mono),Foreground=ink,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true},Padding=new(DesignMetrics.Spacing.Sm),CornerRadius=new(DesignMetrics.Radius.Segment),Background=b.Subtle});
                    buttons.Children.Add(SmallButton(Locale.Get("guidedPanel.installButton"),()=>InstallStyle(style,command)));
                }
                buttons.Children.Add(SmallButton(Locale.Get("guidedPanel.recheckButton"),LoadStyles));setup.Children.Add(buttons);
                AutomationProperties.SetAutomationId(setup,"mighty-setup-"+id);guidedBody.Children.Add(setup);
            }

            // The style's state sources, as the phone draws them (M/GuidedPanel.swift:185-218): a 6pt bar with its count, a list, or a label.
            var widgets=new StackPanel{Spacing=DesignMetrics.Spacing.Xs};
            ForgetElapsed(guidedElapsed);
            foreach(var widget in LiveStyleReading(style).Widgets)
            {
                if(widget.Kind=="taskList")
                {
                    // §1.17: the pane's background agents and shells, with their elapsed time.
                    var tasks=StylePresentation.Tasks(widget);
                    if(tasks.Count>0)widgets.Children.Add(BackgroundRows(tasks,guidedElapsed));
                    continue;
                }
                if(widget.Kind=="progressBar"&&StylePresentation.Progress(widget) is {} progress)
                {
                    var bar=new Grid{ColumnSpacing=DesignMetrics.Spacing.Sm};bar.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});bar.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
                    var fill=new Border{Height=6,CornerRadius=new(3),HorizontalAlignment=HorizontalAlignment.Left,Background=accent,Width=0};
                    var track=new Grid{Height=6,CornerRadius=new(3),Background=b.Brush(DesignToken.Line),VerticalAlignment=VerticalAlignment.Center};track.Children.Add(fill);
                    var part=Math.Clamp(progress.Fraction,0,1);track.SizeChanged+=(_,args)=>fill.Width=args.NewSize.Width*part;
                    bar.Children.Add(track);var count=new TextBlock{Text=progress.Text,FontSize=10,FontWeight=FontWeights.SemiBold,Foreground=accent,VerticalAlignment=VerticalAlignment.Center};
                    Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(count,FontNumeralAlignment.Tabular);Grid.SetColumn(count,1);bar.Children.Add(count);widgets.Children.Add(bar);
                }
                else if(widget.Kind=="list")
                {
                    var list=new StackPanel{Spacing=1};
                    foreach(var value in (widget.Items??[]).Select(StylePresentation.Inline).Where(v=>v.Length>0).Take(20))list.Children.Add(new TextBlock{Text=value,FontSize=10,Foreground=ink2,TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis});
                    if(list.Children.Count>0)widgets.Children.Add(list);
                }
                else if(!string.IsNullOrEmpty(widget.Text))widgets.Children.Add(new TextBlock{Text=StylePresentation.Inline(widget.Text),FontSize=11,Foreground=ink2,TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis});
            }
            if(widgets.Children.Count>0){AutomationProperties.SetAutomationId(widgets,"mighty-state-"+id);AutomationProperties.SetName(widgets,Locale.Get("guidedPanel.stateAccessibility"));guidedBody.Children.Add(widgets);}
            var group=style.Manifest.Groups.FirstOrDefault(g=>g.Id==styleGroup)??evaluator.InitialGroup(styleCapabilities);
            if(evaluator.DrawsGroupMap)
            {
                // The group map (M/GuidedPanel.swift:222-241): equal tiles 5 apart, the title in 11 semibold mono over its axis in 9pt ink2, padding h9 v5 at radius 7.
                var map=new Grid{ColumnSpacing=DesignMetrics.Spacing.Xs};var column=0;
                foreach(var item in style.Manifest.Groups)
                {
                    map.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});var chosen=group?.Id==item.Id;
                    var words=new StackPanel{Spacing=1};words.Children.Add(new TextBlock{Text=item.Title,FontWeight=FontWeights.SemiBold,FontSize=11,FontFamily=new FontFamily(DesignMetrics.Font.Mono),Foreground=ink,TextTrimming=TextTrimming.CharacterEllipsis});words.Children.Add(new TextBlock{Text=item.Axis??"",FontSize=9,Foreground=ink2,TextTrimming=TextTrimming.CharacterEllipsis});
                    var tile=new Border{Child=words,Padding=new(DesignMetrics.Spacing.Sm,DesignMetrics.Spacing.Xs,DesignMetrics.Spacing.Sm,DesignMetrics.Spacing.Xs),CornerRadius=new(7),BorderThickness=new(DesignMetrics.Stroke.Line),Background=chosen?b.Brush(DesignToken.Accent,GroupChosenFill):b.Subtle,BorderBrush=chosen?b.Brush(DesignToken.Accent,GroupChosenEdge):b.Transparent};
                    var button=Button(item.Title,()=>{styleGroup=item.Id;RenderGuidedStyle();return Task.CompletedTask;});button.Content=tile;button.Padding=new(0);button.MinWidth=0;button.MinHeight=0;button.BorderThickness=new(0);button.CornerRadius=new(7);button.HorizontalAlignment=HorizontalAlignment.Stretch;button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
                    owner.PaintPlainButton(button,b.Transparent,b.Transparent);
                    ToolTipService.SetToolTip(button,item.Question??item.Title);AutomationProperties.SetAutomationId(button,"mighty-group-"+item.Id+"-"+id);Grid.SetColumn(button,column++);map.Children.Add(button);
                }
                guidedBody.Children.Add(map);
                if(group?.Question is {Length:>0} question)guidedBody.Children.Add(new TextBlock{Text=question,FontSize=11,Foreground=ink2,TextWrapping=TextWrapping.Wrap});
            }
            if(StyleManifest.Strings(style.Manifest.Root,"capabilities").Length>0&&(evaluator.RecommendGroupId is null||group?.Id==evaluator.RecommendGroupId))
            {
                // What the built-in feature found (M/GuidedPanel.swift:257-294): a folder mark, the empty line or up to six file chips behind their detail, and reload.
                var files=new Grid{ColumnSpacing=DesignMetrics.Spacing.Sm};files.ColumnDefinitions.Add(new(){Width=GridLength.Auto});files.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});files.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
                files.Children.Add(new FontIcon{Glyph="",FontSize=10,Foreground=ink2,VerticalAlignment=VerticalAlignment.Center});
                if(styleCapabilityFiles.Count==0){var none=new TextBlock{Text=Locale.Get("styles.casebook.empty"),FontSize=11,Foreground=ink2,TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(none,1);files.Children.Add(none);}
                else
                {
                    var chips=new StackPanel{Orientation=Orientation.Horizontal,Spacing=DesignMetrics.Spacing.Sm};
                    if(styleCapabilityFiles[0].Detail is {Length:>0} detail)chips.Children.Add(new Border{Child=new TextBlock{Text=detail,FontSize=10,Foreground=ink2,TextWrapping=TextWrapping.NoWrap},Padding=new(DesignMetrics.Spacing.Xs,1,DesignMetrics.Spacing.Xs,1),CornerRadius=new(8),Background=b.Subtle,VerticalAlignment=VerticalAlignment.Center});
                    foreach(var file in styleCapabilityFiles.Take(6))
                    {
                        var open=Button(file.Title,()=>{owner.service.OpenResultFile(file.Path,Workspace.Path);return Task.CompletedTask;});open.FontSize=10;open.FontFamily=new FontFamily(DesignMetrics.Font.Mono);open.Padding=new(0);open.MinWidth=0;open.MinHeight=0;open.BorderThickness=new(0);open.VerticalAlignment=VerticalAlignment.Center;
                        owner.PaintPlainButton(open,b.Transparent,b.Transparent,ink:accent);ToolTipService.SetToolTip(open,Locale.Get("guidedPanel.openPathPrefix")+file.Path);chips.Children.Add(open);
                    }
                    var scroll=new ScrollViewer{Content=chips,HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden,HorizontalScrollMode=ScrollMode.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollMode=ScrollMode.Disabled};Grid.SetColumn(scroll,1);files.Children.Add(scroll);
                }
                var reload=Button("↻",LoadStyles);reload.Content=new FontIcon{Glyph="",FontSize=9};reload.Padding=new(DesignMetrics.Spacing.Xxs);reload.MinWidth=0;reload.MinHeight=0;reload.BorderThickness=new(0);reload.CornerRadius=new(DesignMetrics.Radius.FileRow);reload.VerticalAlignment=VerticalAlignment.Center;
                owner.PaintPlainButton(reload,b.Transparent,b.Subtle,ink:ink2);ToolTipService.SetToolTip(reload,Locale.Get("guidedPanel.reloadHelp"));AutomationProperties.SetName(reload,Locale.Get("guidedPanel.reloadHelp"));Grid.SetColumn(reload,2);files.Children.Add(reload);
                AutomationProperties.SetAutomationId(files,"mighty-attachments-"+id);guidedBody.Children.Add(files);
            }
            if(busy&&evaluator.DrawsPhaseProgress&&!job)
            {
                // A sequence in flight offers nothing to press: a small spinner, and the guidance line under it says what runs (M/GuidedPanel.swift:305-312).
                var spinner=new ProgressRing{IsActive=true,Width=16,Height=16,MinWidth=0,MinHeight=0,HorizontalAlignment=HorizontalAlignment.Left,Foreground=ink2};
                AutomationProperties.SetAutomationId(spinner,"mighty-progress-"+id);AutomationProperties.SetName(spinner,Locale.Get("guidedPanel.progressAccessibility"));guidedBody.Children.Add(spinner);
            }
            else
            {
                var actions=evaluator.VisibleActions(phase,group,busy,job,styleStartingNew);var recommended=evaluator.RecommendedAction(styleCapabilities);
                var prominent=job||evaluator.AtStart(phase)||evaluator.DrawsPhaseProgress?actions.FirstOrDefault()?.Id:null;
                var grid=evaluator.DrawsGroupMap;var chips=new List<Button>();
                foreach(var action in actions)
                {
                    // A chip (M/GuidedActionChip.swift:36-56): its glyph or icon, its title in 11 (semibold when emphasised) and its flags, 5 apart,
                    // padding h8 v5 at radius 6; an emphasised chip on its tint × 0.18, the others on the subtle wash. Each chip is built anew
                    // with every render, so its look is painted once, before it is shown.
                    var flags=action.Flags??[];var label=(action.Glyph??StylePresentation.Icon(action.Icon))+" "+action.Title+(flags.Contains("userInvoked")?" ♙":"")+(flags.Contains("readOnly")?" ◉":"");
                    var emphasised=action.Id==prominent||action.Id==recommended;
                    var face=new StackPanel{Orientation=Orientation.Horizontal,Spacing=DesignMetrics.Spacing.Xs};
                    if(action.Glyph is {Length:>0} glyph)face.Children.Add(new TextBlock{Text=glyph,FontSize=11,VerticalAlignment=VerticalAlignment.Center});
                    else if(StylePresentation.Icon(action.Icon) is {Length:>0} icon)face.Children.Add(new TextBlock{Text=icon,FontSize=10,VerticalAlignment=VerticalAlignment.Center});
                    face.Children.Add(new TextBlock{Text=action.Title,FontSize=11,FontWeight=emphasised?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center});
                    if(flags.Contains("userInvoked"))face.Children.Add(new FontIcon{Glyph="",FontSize=8,Foreground=ink2,VerticalAlignment=VerticalAlignment.Center});
                    if(flags.Contains("readOnly"))face.Children.Add(new FontIcon{Glyph="",FontSize=8,Foreground=ink2,VerticalAlignment=VerticalAlignment.Center});
                    var button=Button(label.Trim(),()=>InvokeStyleAction(action.Id));button.Content=face;button.Padding=new(DesignMetrics.Spacing.Sm,DesignMetrics.Spacing.Xs,DesignMetrics.Spacing.Sm,DesignMetrics.Spacing.Xs);button.MinWidth=0;button.MinHeight=0;button.HorizontalAlignment=HorizontalAlignment.Left;button.IsEnabled=!action.RequiresText||!string.IsNullOrWhiteSpace(input.Text);
                    button.CornerRadius=new(DesignMetrics.Radius.Segment);button.BorderThickness=new(0);
                    var chipFill=emphasised?b.Brush(StyleTint(action.Tint),StyleChipTint):b.Subtle;owner.PaintPlainButton(button,chipFill,chipFill,ink:ink,disabledInk:ink);
                    button.Opacity=button.IsEnabled?1:ChipDisabled;button.IsEnabledChanged+=(_,_)=>button.Opacity=button.IsEnabled?1:ChipDisabled;
                    ToolTipService.SetToolTip(button,StylePresentation.ActionHelp(action));AutomationProperties.SetAutomationId(button,"style-action-"+action.Id);chips.Add(button);styleActionButtons[action.Id]=button;
                }
                // The reset chip: back to the start row, or out of it again (M/GuidedPanel.swift:349-362).
                Button? reset=null;
                if(styleStartingNew){reset=SmallButton(Locale.Get("guidedPanel.cancelButton"),()=>{styleStartingNew=false;RenderGuidedStyle();return Task.CompletedTask;});AutomationProperties.SetAutomationId(reset,"mighty-reset-cancel-"+id);}
                else if(!evaluator.AtStart(phase)&&evaluator.ResetTitle is {} title)
                {
                    reset=SmallButton(title,()=>{styleStartingNew=true;RenderGuidedStyle();return Task.CompletedTask;});
                    var again=new StackPanel{Orientation=Orientation.Horizontal,Spacing=DesignMetrics.Spacing.Xs};again.Children.Add(new FontIcon{Glyph="",FontSize=9,VerticalAlignment=VerticalAlignment.Center});again.Children.Add(new TextBlock{Text=title,FontSize=11,VerticalAlignment=VerticalAlignment.Center});
                    reset.Content=again;ToolTipService.SetToolTip(reset,Locale.Get("guidedPanel.resetHelp"));AutomationProperties.SetAutomationId(reset,"mighty-reset-"+id);
                }
                if(grid)
                {
                    // Under a group map the chips are a catalogue: an adaptive grid (104–170 wide, 5 apart) that scrolls past its height, the reset chip under it.
                    var cells=new AdaptiveGridPanel{Minimum=104,Maximum=170,Gap=5};foreach(var chip in chips)cells.Children.Add(chip);
                    var catalogue=new StackPanel{Spacing=DesignMetrics.Spacing.Xs};catalogue.Children.Add(new ScrollViewer{Content=cells,MaxHeight=ChipGridHeight(chips.Count),VerticalScrollBarVisibility=ScrollBarVisibility.Hidden,VerticalScrollMode=ScrollMode.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,HorizontalScrollMode=ScrollMode.Disabled});
                    if(reset is not null){reset.HorizontalAlignment=HorizontalAlignment.Left;catalogue.Children.Add(reset);}
                    guidedBody.Children.Add(catalogue);
                }
                else if(chips.Count>0||reset is not null)
                {
                    // A phase's row is one line that scrolls sideways, the chips 6 apart and the reset chip last.
                    var line=new StackPanel{Orientation=Orientation.Horizontal,Spacing=DesignMetrics.Spacing.Sm};foreach(var chip in chips)line.Children.Add(chip);if(reset is not null)line.Children.Add(reset);
                    guidedBody.Children.Add(new ScrollViewer{Content=line,HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden,HorizontalScrollMode=ScrollMode.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollMode=ScrollMode.Disabled});
                }
            }
            // The guidance line: 10pt in the tertiary ink (M/GuidedPanel.swift:119).
            if(evaluator.Guidance(phase,busy,job) is {} guidance)guidedBody.Children.Add(new TextBlock{Text=guidance,FontSize=10,Foreground=tertiary,TextWrapping=TextWrapping.Wrap});
            guidedBody.Visibility=guidedBody.Children.Count>0?Visibility.Visible:Visibility.Collapsed;
        }
        private Task InvokeStyleAction(string actionId)=>owner.Act(async()=>
        {
            if(composingInput||toolPermissions.Any(p=>p.State=="pending"&&p.CanAnswerQuestions))return;
            var style=activeStyle??throw new InvalidOperationException(Locale.Get("guidedPanel.approvalRequired"));
            if(!StyleRegistry.Unchanged(style))throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
            var action=style.Manifest.Actions.First(a=>a.Id==actionId);if(action.RequiresText&&string.IsNullOrWhiteSpace(input.Text)){input.Focus(FocusState.Programmatic);return;}
            var original=input.Text;var prompt=action.Prompt(action.TakesText?original:"");
            input.Text=prompt;input.SelectionStart=input.Text.Length;input.Focus(FocusState.Programmatic);
            try{await Send();}
            finally
            {
                if(!owner.closing&&owner.service.Snapshot.Sessions.Any(p=>p.Id==id)&&(input.Text==prompt||!action.TakesText&&input.Text.Length==0))
                {input.Text=original;await Change(p=>p with{Draft=original});}
            }
        });
        internal async Task<Dictionary<string,object?>> RunStylesSmoke()
        {
            var original=Session;var draft=input.Text;
            try
            {
                await Change(p=>p with{Provider="claude",AgentViewMode="mighty",MightyStyle="ouroboros",MightyStyleHash=null,MightyStyleSince=null});
                Refresh();
                await WaitUI(()=>!styleLoading);
                await LoadStyles();
                Require(activeStyle is {Id:"ouroboros"},"bundled style did not resolve");
                stylePrerequisites=new(true,[],null,null);RenderGuidedStyle();
                var phase=GuidedPhase(activeStyle!);var actions=activeStyle!.Evaluator.VisibleActions(phase,activeStyle.Manifest.Groups.FirstOrDefault(),false);
                Require(ReferenceEquals(stylePicker.Content,stylePickerFace)&&stylePickerLabel.Text.Contains("Ouroboros",StringComparison.Ordinal),"style picker label missing: '"+stylePickerLabel.Text+"'");
                Require(styleHint.Text==activeStyle!.Manifest.Subtitle,"the style row must say what the style is for ('"+activeStyle.Manifest.Subtitle+"'); got '"+styleHint.Text+"'");
                Require(styleActionButtons.Count>0,"style actions not drawn");
                await SettleDesktopCapture(owner.root);await CaptureElement(Container,Path.Combine(owner.options.ProfileDirectory!,"smoke-composer-style.png"));
                var content=StyleContents(activeStyle!);guidedBody.Children.Add(content);await WaitUI(()=>content.IsLoaded&&content.ActualHeight>0);
                Require(content.IsReadOnly&&content.AcceptsReturn&&content.Text.Contains("autoAllow",StringComparison.Ordinal),"approval omits manifest permissions (readOnly="+content.IsReadOnly+", multiline="+content.AcceptsReturn+", textLength="+content.Text.Length+", sourceBytes="+activeStyle!.Bytes.Length+")");
                Require(content.Text.Replace("\r\n","\n",StringComparison.Ordinal).Replace('\r','\n')==Encoding.UTF8.GetString(activeStyle!.Bytes.Span).Replace("\r\n","\n",StringComparison.Ordinal).Replace('\r','\n'),"Approval text differs from the immutable reviewed bytes.");
                guidedBody.Children.Remove(content);
                var request=await PrepareStyleRunRequest(new StartRunRequest(id,Session.WorkspaceId,"claude","fixture",[]));
                Require(request.StyleAutoAllow is {Count:>0},"run does not snapshot selected style tools");
                return new(){["bundledCount"]=styleRegistry!.Styles.Count(s=>s.Source=="bundled"),["picker"]=true,["guidedActions"]=actions.Length,["fullApprovalContents"]=true,["localRunPermissionSnapshot"]=true,["userFilesRead"]=false};
            }
            finally
            {
                await Change(p=>p with{Provider=original.Provider,AgentViewMode=original.AgentViewMode,MightyStyle=original.MightyStyle,MightyStyleHash=original.MightyStyleHash,MightyStyleSince=original.MightyStyleSince});
                input.Text=draft;activeStyle=null;loadedStyleKey=null;Refresh();
            }
        }
        private async Task<StartRunRequest> PrepareStyleRunRequest(StartRunRequest request)
        {
            var pane=Session;
            if(pane.Kind!="claude"||pane.Provider!="claude"||pane.AgentViewMode!="mighty"||pane.MightyStyle is null)return request;
            var profile=owner.StateDirectory;var workspace=Workspace.Path;
            var prepared=await Task.Run(()=>StyleRunPermissions.Bind(request,pane,profile,workspace));
            if(owner.closing||!owner.service.Snapshot.Sessions.Any(p=>p.Id==id)||Session.MightyStyle!=pane.MightyStyle||Session.MightyStyleHash!=pane.MightyStyleHash)
                throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
            return prepared;
        }
        // Root Send calls this before creating StartRunRequest. Approval is checked
        // again on the worker; a stale menu or file edit cannot execute a style.
        private async Task<string> PrepareStyleSubmission(string draft,bool attachments)
        {
            var pane=Session;if(pane.Kind!="claude"||pane.Provider!="claude"||pane.AgentViewMode!="mighty"||pane.MightyStyle is null)return draft;
            var registry=await Task.Run(()=>StyleRegistry.Load(owner.StateDirectory,Workspace.Path));var style=registry.Runnable(pane.MightyStyle,pane.MightyStyleHash)??throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
            if(!StyleRegistry.Unchanged(style))throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
            var phase=style.Evaluator.EffectivePhase(GuidedPhase(style),styleStartingNew);var action=style.Evaluator.RewriteAction(draft,phase,attachments,pane.Status=="running",pane.Logs.Any(l=>l.Kind=="user")||pane.GraphRuns is {Count:>0},styleStartingNew);
            var result=action is null?draft:style.Manifest.Actions.First(a=>a.Id==action).Prompt(draft);
            if(pane.MightyStyleSince is null||styleStartingNew)await Change(p=>p with{MightyStyleSince=Wire.Now()});styleStartingNew=false;return result;
        }
    }
}
