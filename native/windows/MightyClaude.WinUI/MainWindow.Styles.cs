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
    private StackPanel BuildStylesSection()
    {
        var panel = new StackPanel { Spacing = 8 }; var rows = new StackPanel { Spacing = 10 };
        AutomationProperties.SetAutomationId(rows, "settings-style-rows");
        panel.Children.Add(new TextBlock { Text = Locale.Get("settings.styles.description"), TextWrapping = TextWrapping.Wrap });
        Button register = null!;
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
        panel.Children.Add(register);
        panel.Children.Add(Button(Locale.Get("settings.styles.rescanButton"), () => Act(Populate))); panel.Children.Add(rows);
        async Task Populate()
        {
            var workspace = service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == service.Snapshot.ActiveWorkspaceId)?.Path ?? StateDirectory;
            var registry = await Task.Run(() => StyleRegistry.Load(StateDirectory, workspace)); rows.Children.Clear(); register.IsEnabled = !registry.TrustLocked;
            if (registry.TrustLocked) rows.Children.Add(new TextBlock { Text = Locale.Get("settings.styles.lockBanner") + "\n" + Path.Combine(StateDirectory, "style-trust", "approvals.json"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            foreach (var style in registry.Styles)
            {
                var row = new StackPanel { Spacing = 5 };
                AutomationProperties.SetAutomationId(row, "settings-style-" + style.Id);
                row.Children.Add(new TextBlock { Text = StylePresentation.Name(style) + " · " + StylePresentation.State(style.Approval), FontWeight = FontWeights.SemiBold });
                row.Children.Add(new TextBlock { Text = style.Manifest.Summary, TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = style.Id + " · " + Locale.Get("settings.styles.hashDetailTemplate", new Dictionary<string,string>{{"hash",style.Hash[..12]}}) + " · " + Locale.Get("settings.styles.actionCountTemplate", new Dictionary<string,string>{{"count",style.Manifest.Actions.Length.ToString()}}) + " · " + Locale.Get("settings.styles.autoAllowCountTemplate", new Dictionary<string,string>{{"count",StyleManifest.Items(style.Manifest.Root,"autoAllow").Length.ToString()}}), FontSize = 11, TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = style.Path, FontSize = 10, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                controls.Children.Add(Button(Locale.Get("settings.styles.viewButton"), async () => { await ShowStyleApproval(style, readOnly: true); }));
                if (style.Source != "bundled")
                {
                    var action = style.Approval == "revoked" ? "unblock" : style.Runnable ? "revoked" : "approved";
                    var key = action == "unblock" ? "settings.styles.unblockButton" : action == "revoked" ? "settings.styles.revokeButton" : "settings.styles.allowButton";
                    var decide = Button(Locale.Get(key), () => Act(async () =>
                    {
                        if (action == "approved") await ShowStyleApproval(style);
                        else await Task.Run(() => new StyleTrustStore(Path.Combine(StateDirectory, "style-trust")).Decide(style, action));
                        await Populate();
                    })); decide.IsEnabled = !registry.TrustLocked; controls.Children.Add(decide);
                }
                if (style.Source == "user")
                {
                    var remove = Button(Locale.Get("settings.styles.removeButton"), () => Act(async () =>
                    {
                        var confirm = new ContentDialog { Title = Locale.Get("settings.styles.removeButton"), Content = new TextBlock { Text = style.Path, TextWrapping = TextWrapping.Wrap }, XamlRoot = SettingsXamlRoot, PrimaryButtonText = Locale.Get("settings.styles.removeButton"), CloseButtonText = Locale.Get("guidedPanel.cancelButton"), DefaultButton = ContentDialogButton.Close };
                        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                        await Task.Run(() =>
                        {
                            if (!StyleRegistry.Unchanged(style)) throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
                            var root = WorkspaceFiles.RealPath(Path.Combine(StateDirectory, "styles")); if (root is null || !WorkspaceFiles.Contains(style.Path, root)) throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
                            new StyleTrustStore(Path.Combine(StateDirectory, "style-trust")).Decide(style, "revoked"); File.Delete(style.Path);
                        }); await Populate();
                    })); remove.IsEnabled = !registry.TrustLocked; controls.Children.Add(remove);
                }
                row.Children.Add(controls); rows.Children.Add(row);
            }
            foreach (var rejection in registry.Rejections) rows.Children.Add(new TextBlock { Text = rejection.Code + " · " + rejection.Message + "\n" + rejection.Path, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 11 });
        }
        panel.Loaded += async (_, _) => await Act(Populate); return panel;
    }
    private static TextBox StyleContents(RegisteredStyle style) => new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 380, MinWidth = 300, FontFamily = new FontFamily(DesignMetrics.Font.Mono), Text = Encoding.UTF8.GetString(style.Bytes.Span) };
    private Task ApproveStyle(RegisteredStyle style) => Task.Run(() =>
    {
        if (!StyleRegistry.Unchanged(style)) throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
        new StyleTrustStore(Path.Combine(StateDirectory, "style-trust")).Decide(style, "approved");
    });
    private sealed partial class PaneView
    {
        private readonly StackPanel styleHost=new(){Spacing=5,Visibility=Visibility.Collapsed};
        private readonly Button stylePicker=new(){Content="CLI",HorizontalAlignment=HorizontalAlignment.Left,FontSize=10,Padding=new(7,3,7,3)};
        private readonly StackPanel guidedBody=new(){Spacing=5};
        private StyleRegistry? styleRegistry;
        private RegisteredStyle? activeStyle;
        private StyleStateReading? styleReading;
        private StylePrerequisiteResult? stylePrerequisites;
        private Dictionary<string,string> styleCapabilities=[];
        private IReadOnlyList<StyleAttachmentItem> styleCapabilityFiles=[];
        private readonly Dictionary<string,Button> styleActionButtons=[];
        private readonly TextBlock styleEnterPrefix=new(){FontSize=10,TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed};
        private string? styleGroup;
        private bool styleStartingNew,styleLoading;
        private readonly DispatcherTimer styleTimer=new(){Interval=TimeSpan.FromSeconds(2)};
        private string? loadedStyleKey;
        private void InitializeStyles()
        {
            AutomationProperties.SetAutomationId(stylePicker,"mighty-style-"+id);AutomationProperties.SetName(stylePicker,Locale.Get("guidedPanel.stylesMenuAccessibility"));ToolTipService.SetToolTip(stylePicker,Locale.Get("guidedPanel.stylesMenuHelp"));styleHost.Children.Add(stylePicker);styleHost.Children.Add(guidedBody);styleHost.Children.Add(styleEnterPrefix);
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
                var menu=new MenuFlyout();menu.Items.Add(Item("CLI",()=>ChooseStyle(null),pane.MightyStyle is null));
                foreach(var candidate in data.Registry.Styles){var item=Item(StylePresentation.Name(candidate)+" · "+StylePresentation.State(candidate.Approval)+" · "+Locale.Get("settings.styles.actionCountTemplate",new Dictionary<string,string>{{"count",candidate.Manifest.Actions.Length.ToString()}})+" · "+Locale.Get("settings.styles.autoAllowCountTemplate",new Dictionary<string,string>{{"count",StyleManifest.Items(candidate.Manifest.Root,"autoAllow").Length.ToString()}}),()=>ChooseStyle(candidate),candidate.Id==pane.MightyStyle);item.IsEnabled=candidate.Approval!="revoked"&&(!data.Registry.TrustLocked||candidate.Runnable);menu.Items.Add(item);}
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
        private StylePhase? GuidedPhase(RegisteredStyle style)=>style.Evaluator.CurrentPhase(Session.GraphRuns is {Count:>0} runs?runs.Select(r=>r.Input):Session.Logs.Where(l=>l.Kind=="user").Select(l=>l.Text),styleReading?.Files);
        private void RefreshStyleComposer()
        {
            if(owner.closing||!owner.service.Snapshot.Sessions.Any(p=>p.Id==id))return;
            styleEnterPrefix.Visibility=Visibility.Collapsed;
            if(activeStyle is not {} style||Session.AgentViewMode!="mighty")return;
            var pane=Session;var evaluator=style.Evaluator;var phase=evaluator.EffectivePhase(GuidedPhase(style),styleStartingNew);
            var answering=toolPermissions.Any(p=>p.State=="pending"&&p.CanAnswerQuestions);
            input.PlaceholderText=evaluator.Placeholder(phase,pane.Status=="running"||starting,answering,evaluator.JobOpen(pane));
            foreach(var action in style.Manifest.Actions)if(styleActionButtons.TryGetValue(action.Id,out var button))button.IsEnabled=!answering&&(!action.RequiresText||!string.IsNullOrWhiteSpace(input.Text));
            if(!answering&&evaluator.RewriteAction(input.Text,phase,pendingAttachments.Count>0,pane.Status=="running"||starting,pane.Logs.Any(l=>l.Kind=="user")||pane.GraphRuns is {Count:>0},styleStartingNew) is {} actionId)
            {
                styleEnterPrefix.Text=Locale.Get("styles.enterPrefix",new Dictionary<string,string>{{"prompt",style.Manifest.Actions.First(a=>a.Id==actionId).Prompt("")}});styleEnterPrefix.Visibility=Visibility.Visible;
            }
        }
        private static Windows.UI.Color StyleColor(string? tint)=>tint switch
        {
            "purple"=>Microsoft.UI.Colors.MediumPurple,"teal"=>Microsoft.UI.Colors.Teal,"indigo"=>Microsoft.UI.Colors.SlateBlue,
            "mint"=>Microsoft.UI.Colors.MediumSeaGreen,"orange"=>Microsoft.UI.Colors.DarkOrange,"green"=>Microsoft.UI.Colors.ForestGreen,
            "red"=>Microsoft.UI.Colors.IndianRed,"secondary"=>Microsoft.UI.Colors.Gray,_=>Microsoft.UI.Colors.CornflowerBlue
        };
        private void RenderGuidedStyle()
        {
            if(owner.closing||!owner.service.Snapshot.Sessions.Any(p=>p.Id==id))return;
            var pane=Session;stylePicker.IsEnabled=pane.Status!="running"&&!starting;stylePicker.Content=(activeStyle is {} selected?StylePresentation.Name(selected):"CLI")+" ⌄";guidedBody.Children.Clear();styleActionButtons.Clear();
            if(activeStyle is not {} style)
            {
                if(pane.MightyStyle is not null)
                {
                    var pending=styleRegistry?.Styles.FirstOrDefault(s=>s.Id==pane.MightyStyle);
                    guidedBody.Children.Add(new TextBlock{Text=(pending is null?pane.MightyStyle:StylePresentation.Name(pending))+" · "+Locale.Get("guidedPanel.approvalRequired"),TextWrapping=TextWrapping.Wrap});
                    if(pending is not null&&pending.Approval!="revoked"&&styleRegistry?.TrustLocked!=true)guidedBody.Children.Add(Button(Locale.Get("guidedPanel.viewContentsButton"),()=>ChooseStyle(pending)));
                }
                styleEnterPrefix.Visibility=Visibility.Collapsed;return;
            }
            var evaluator=style.Evaluator;var phase=evaluator.EffectivePhase(GuidedPhase(style),styleStartingNew);var busy=pane.Status=="running"||starting;var job=evaluator.JobOpen(pane);
            RefreshStyleComposer();
            if(style.Manifest.Phases.Length>0)
            {
                var phases=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5};
                foreach(var item in style.Manifest.Phases.OrderBy(p=>p.Order))
                {
                    if(phases.Children.Count>0)phases.Children.Add(new TextBlock{Text="›",Opacity=.5});
                    var label=new TextBlock{Text=item.Title,FontSize=10,FontWeight=item.Id==phase?.Id?FontWeights.SemiBold:FontWeights.Normal,Opacity=(phase is null||item.Order>phase.Order) ? .55 : 1};
                    if(item.Id==phase?.Id)label.Foreground=new SolidColorBrush(StyleColor("accent"));phases.Children.Add(label);
                }
                guidedBody.Children.Add(new ScrollViewer{Content=phases,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});
            }
            // The questionnaire already has its own native renderer. No style
            // action may replace it or rewrite an answer in the main composer.
            if(toolPermissions.Any(p=>p.State=="pending"&&p.CanAnswerQuestions))return;
            if(stylePrerequisites is {Ready:false} prerequisites)
            {
                guidedBody.Children.Add(new TextBlock{Text=string.Join(" · ",prerequisites.Missing)+(prerequisites.Hint is {} hint?"\n"+hint:""),FontSize=11,TextWrapping=TextWrapping.Wrap});
                if(prerequisites.InstallCommand is {} command)
                {
                    guidedBody.Children.Add(new TextBox{Text=command,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FontSize=10});
                    guidedBody.Children.Add(Button(Locale.Get("guidedPanel.installButton"),()=>owner.Act(async()=>
                    {
                        if(!StyleRegistry.Unchanged(style))throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
                        var current=await Task.Run(()=>StyleRegistry.Load(owner.StateDirectory,Workspace.Path));
                        if(current.Runnable(style.Id,style.Hash)==null)throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
                        var actual=StyleRunPermissions.InstallCommand(command,PseudoTerminal.DefaultShell);
                        var confirm=new ContentDialog{Title=Locale.Get("guidedPanel.installButton"),Content=new TextBox{Text=actual,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true},XamlRoot=owner.root.XamlRoot,PrimaryButtonText=Locale.Get("guidedPanel.installButton"),CloseButtonText=Locale.Get("guidedPanel.cancelButton"),DefaultButton=ContentDialogButton.Close};
                        if(await confirm.ShowAsync()!=ContentDialogResult.Primary)return;
                        if(!StyleRegistry.Unchanged(style)||Session.MightyStyle!=style.Id||Session.MightyStyleHash!=style.Hash||owner.service.Snapshot.ActiveWorkspaceId!=Session.WorkspaceId)
                            throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
                        var approved=await Task.Run(()=>StyleRegistry.Load(owner.StateDirectory,Workspace.Path));
                        if(approved.Runnable(style.Id,style.Hash)==null)throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
                        await owner.RunTerminalCommandAsync(actual,(StyleManifest.Text(style.Manifest.Root.GetProperty("install"),"paneTitle")??style.Manifest.Name)+" · "+StylePresentation.Name(style),autoRun:false);
                    })));
                }
                guidedBody.Children.Add(Button(Locale.Get("guidedPanel.recheckButton"),LoadStyles));
            }

            foreach(var widget in styleReading?.Widgets??[])
            {
                if(widget.Kind=="progressBar"&&StylePresentation.Progress(widget) is {} progress)
                {
                    var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};row.Children.Add(new ProgressBar{Minimum=0,Maximum=1,Value=progress.Fraction,Width=120,IsIndeterminate=false});row.Children.Add(new TextBlock{Text=progress.Text,FontSize=10});guidedBody.Children.Add(row);
                }
                else if(widget.Kind=="list")foreach(var value in (widget.Items??[]).Select(StylePresentation.Inline).Where(v=>v.Length>0).Take(20))guidedBody.Children.Add(new TextBlock{Text="• "+value,FontSize=10,TextTrimming=TextTrimming.CharacterEllipsis});
                else if(!string.IsNullOrEmpty(widget.Text))guidedBody.Children.Add(new TextBlock{Text=StylePresentation.Inline(widget.Text),FontSize=10,TextTrimming=TextTrimming.CharacterEllipsis});
            }
            var group=style.Manifest.Groups.FirstOrDefault(g=>g.Id==styleGroup)??evaluator.InitialGroup(styleCapabilities);
            if(evaluator.DrawsGroupMap)
            {
                var groups=new VariableSizedWrapGrid{Orientation=Orientation.Horizontal,ItemWidth=150,ItemHeight=55};
                foreach(var item in style.Manifest.Groups)
                {
                    var content=new StackPanel{Spacing=2};content.Children.Add(new TextBlock{Text=item.Title,FontWeight=FontWeights.SemiBold,FontSize=11});content.Children.Add(new TextBlock{Text=item.Axis??"",FontSize=9,Opacity=.7});
                    var button=Button("",()=>{styleGroup=item.Id;RenderGuidedStyle();return Task.CompletedTask;});button.Content=content;button.Margin=new(2);button.HorizontalAlignment=HorizontalAlignment.Stretch;
                    if(group?.Id==item.Id)button.BorderBrush=new SolidColorBrush(StyleColor("accent"));ToolTipService.SetToolTip(button,item.Question??item.Title);AutomationProperties.SetAutomationId(button,"mighty-group-"+item.Id+"-"+id);groups.Children.Add(button);
                }
                guidedBody.Children.Add(groups);
                if(group?.Question is {} question)guidedBody.Children.Add(new TextBlock{Text=question,FontSize=11,TextWrapping=TextWrapping.Wrap});
            }
            if(StyleManifest.Strings(style.Manifest.Root,"capabilities").Length>0&&(evaluator.RecommendGroupId is null||group?.Id==evaluator.RecommendGroupId))
            {
                var files=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6};
                files.Children.Add(new TextBlock{Text=styleCapabilityFiles.FirstOrDefault()?.Detail??Locale.Get("styles.casebook.empty"),FontSize=10,MaxWidth=300,TextTrimming=TextTrimming.CharacterEllipsis});
                foreach(var file in styleCapabilityFiles.Take(6))files.Children.Add(Button(file.Title,()=>{owner.service.OpenResultFile(file.Path,Workspace.Path);return Task.CompletedTask;}));
                files.Children.Add(Button("↻",LoadStyles));ToolTipService.SetToolTip(files.Children.Last(),Locale.Get("guidedPanel.reloadHelp"));
                guidedBody.Children.Add(new ScrollViewer{Content=files,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});
            }
            if(busy&&evaluator.DrawsPhaseProgress&&!job)guidedBody.Children.Add(new ProgressRing{IsActive=true,Width=18,Height=18,HorizontalAlignment=HorizontalAlignment.Left});
            else
            {
                var actions=evaluator.VisibleActions(phase,group,busy,job,styleStartingNew);var recommended=evaluator.RecommendedAction(styleCapabilities);
                var prominent=job||evaluator.AtStart(phase)||evaluator.DrawsPhaseProgress?actions.FirstOrDefault()?.Id:null;
                var chips=new VariableSizedWrapGrid{Orientation=Orientation.Horizontal,ItemWidth=145,ItemHeight=36};
                foreach(var action in actions)
                {
                    var flags=action.Flags??[];var label=(action.Glyph??StylePresentation.Icon(action.Icon))+" "+action.Title+(flags.Contains("userInvoked")?" ♙":"")+(flags.Contains("readOnly")?" ◉":"");
                    var button=Button(label.Trim(),()=>InvokeStyleAction(action.Id));button.FontSize=11;button.Margin=new(2);button.Padding=new(5);button.IsEnabled=!action.RequiresText||!string.IsNullOrWhiteSpace(input.Text);
                    if(action.Id==prominent||action.Id==recommended){var color=StyleColor(action.Tint);button.BorderBrush=new SolidColorBrush(color);color.A=40;button.Background=new SolidColorBrush(color);button.FontWeight=FontWeights.SemiBold;}
                    ToolTipService.SetToolTip(button,StylePresentation.ActionHelp(action));AutomationProperties.SetAutomationId(button,"style-action-"+action.Id);chips.Children.Add(button);styleActionButtons[action.Id]=button;
                }
                guidedBody.Children.Add(new ScrollViewer{Content=chips,MaxHeight=evaluator.DrawsGroupMap?92:76,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
                if(styleStartingNew)guidedBody.Children.Add(Button(Locale.Get("guidedPanel.cancelButton"),()=>{styleStartingNew=false;RenderGuidedStyle();return Task.CompletedTask;}));
                else if(!evaluator.AtStart(phase)&&evaluator.ResetTitle is {} reset)guidedBody.Children.Add(Button(reset,()=>{styleStartingNew=true;RenderGuidedStyle();return Task.CompletedTask;}));
            }
            if(evaluator.Guidance(phase,busy,job) is {} guidance)guidedBody.Children.Add(new TextBlock{Text=guidance,FontSize=10,Opacity=.7,TextWrapping=TextWrapping.Wrap});
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
                Require(stylePicker.Content?.ToString()?.Contains("Ouroboros",StringComparison.Ordinal)==true,"style picker label missing");
                Require(styleActionButtons.Count>0,"style actions not drawn");
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
