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
        var panel=new StackPanel { Spacing=8 };var rows=new StackPanel { Spacing=8 };
        panel.Children.Add(new TextBlock{Text=Locale.Get("settings.styles.description"),TextWrapping=TextWrapping.Wrap});
        var reload=Button(Locale.Get("settings.styles.rescanButton"),async()=>await Populate());
        panel.Children.Add(reload);
        panel.Children.Add(Button(Locale.Get("settings.styles.registerButton"),async()=>
        {
            var picker=new FileOpenPicker();picker.FileTypeFilter.Add(".json");WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(settingsWindow ?? this));var file=await picker.PickSingleFileAsync();if(file is null)return;
            await Act(async()=>
            {
                var bytes=await Task.Run(()=>StyleFiles.Read(Path.GetDirectoryName(file.Path)!,Path.GetFileName(file.Path))??throw new IOException("Cannot read style manifest."));
                var manifest=StyleManifestDecoder.Decode(bytes);var folder=Path.Combine(StateDirectory,"styles");
                Directory.CreateDirectory(folder);
                var profileRoot=WorkspaceFiles.RealPath(StateDirectory);var stylesRoot=WorkspaceFiles.RealPath(folder);
                if(profileRoot is null||stylesRoot is null||!WorkspaceFiles.Contains(stylesRoot,profileRoot)||(File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)
                    throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
                var destination=Path.Combine(stylesRoot,manifest.Id+".json");
                // Registration cannot overwrite a different reviewed manifest.
                using(var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None))await output.WriteAsync(bytes);
                await Populate();
            });
        }));
        panel.Children.Add(rows);
        async Task Populate()
        {
            var workspace=service.Snapshot.Workspaces.FirstOrDefault(w=>w.Id==service.Snapshot.ActiveWorkspaceId)?.Path??StateDirectory;
            var registry=await Task.Run(()=>StyleRegistry.Load(StateDirectory,workspace));rows.Children.Clear();
            foreach(var style in registry.Styles)
            {
                var row=new StackPanel { Spacing=5 };row.Children.Add(new TextBlock { Text=style.Manifest.Name+" · "+style.Source,FontWeight=FontWeights.SemiBold });row.Children.Add(new TextBlock{Text=style.Manifest.Summary,TextWrapping=TextWrapping.Wrap});
                var details=new StackPanel { Spacing=5,Visibility=Visibility.Collapsed };
                details.Children.Add(new TextBlock{Text=StyleText.Safe(style.Path,400)+"\nSHA-256 "+style.Hash,TextWrapping=TextWrapping.Wrap});
                details.Children.Add(StyleContents(style));
                var controls=new StackPanel { Orientation=Orientation.Horizontal,Spacing=5 };
                controls.Children.Add(Button(Locale.Get("settings.styles.viewButton"),()=>{details.Visibility=details.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;return Task.CompletedTask;}));
                if(style.Source!="bundled")
                {
                    if(style.Approval=="revoked")controls.Children.Add(Button(Locale.Get("settings.styles.unblockButton"),()=>Act(async()=>{await Task.Run(()=>new StyleTrustStore(Path.Combine(StateDirectory,"style-trust")).Decide(style,"unblock"));await Populate();})));
                    else if(style.Runnable)controls.Children.Add(Button(Locale.Get("settings.styles.revokeButton"),()=>Act(async()=>{await Task.Run(()=>new StyleTrustStore(Path.Combine(StateDirectory,"style-trust")).Decide(style,"revoked"));await Populate();})));
                    else
                    {
                        // Permission is physically beneath the complete manifest. Opening
                        // the disclosure alone cannot approve or bind a running pane.
                        details.Children.Add(Button(Locale.Get("settings.styles.allowButton"),()=>Act(async()=>{await ApproveStyle(style);await Populate();})));
                    }
                }
                    if(style.Source=="user")controls.Children.Add(Button(Locale.Get("settings.styles.removeButton"),()=>Act(async()=>
                    {
                        var confirm=new ContentDialog{Title=Locale.Get("settings.styles.removeButton"),Content=new TextBlock{Text=StyleText.Safe(style.Path,400),TextWrapping=TextWrapping.Wrap},XamlRoot=SettingsXamlRoot,PrimaryButtonText=Locale.Get("settings.styles.removeButton"),CloseButtonText=Locale.Get("guidedPanel.cancelButton"),DefaultButton=ContentDialogButton.Close};
                        if(await confirm.ShowAsync()!=ContentDialogResult.Primary)return;
                        await Task.Run(()=>
                        {
                            if(!StyleRegistry.Unchanged(style))throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
                            var root=WorkspaceFiles.RealPath(Path.Combine(StateDirectory,"styles"));if(root is null||!WorkspaceFiles.Contains(style.Path,root))throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
                            new StyleTrustStore(Path.Combine(StateDirectory,"style-trust")).Decide(style,"revoked");File.Delete(style.Path);
                        });
                        await Populate();
                    })));
                row.Children.Add(controls);row.Children.Add(details);rows.Children.Add(row);
            }
            foreach(var rejection in registry.Rejections)rows.Children.Add(new TextBlock{Text=rejection.Message,TextWrapping=TextWrapping.Wrap});
        }
        panel.Loaded+=async(_,_)=>await Act(Populate);return panel;
    }
    private static TextBox StyleContents(RegisteredStyle style)=>new(){Text=Encoding.UTF8.GetString(style.Bytes.Span),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=380,MinWidth=300,FontFamily=new FontFamily("Consolas")};
    private Task ApproveStyle(RegisteredStyle style)=>Task.Run(()=>
    {
        if(!StyleRegistry.Unchanged(style))throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
        new StyleTrustStore(Path.Combine(StateDirectory,"style-trust")).Decide(style,"approved");
    });
    private async Task<bool> ShowStyleApproval(RegisteredStyle style)
    {
        var content=new StackPanel{Spacing=8};content.Children.Add(new TextBlock{Text=StyleText.Safe(style.Path,400)+"\nSHA-256 "+style.Hash,TextWrapping=TextWrapping.Wrap});content.Children.Add(StyleContents(style));
        var dialog=new ContentDialog { Title=style.Manifest.Name,Content=content,XamlRoot=SettingsXamlRoot,PrimaryButtonText=Locale.Get("settings.styles.allowButton"),CloseButtonText=Locale.Get("guidedPanel.cancelButton"),DefaultButton=ContentDialogButton.Close };
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return false;await ApproveStyle(style);return true;
    }
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
        private IReadOnlyList<(string Title,string Path)> styleCapabilityFiles=[];
        private string? styleGroup;
        private bool styleStartingNew,styleLoading;
        private readonly DispatcherTimer styleTimer=new(){Interval=TimeSpan.FromSeconds(2)};
        private string? loadedStyleKey;
        private void InitializeStyles()
        {
            AutomationProperties.SetAutomationId(stylePicker,"mighty-style-"+id);AutomationProperties.SetName(stylePicker,Locale.Get("guidedPanel.stylesMenuAccessibility"));ToolTipService.SetToolTip(stylePicker,Locale.Get("guidedPanel.stylesMenuHelp"));styleHost.Children.Add(stylePicker);styleHost.Children.Add(guidedBody);
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
                    return (Registry:registry,Chosen:chosen,Reading:chosen is null?null:StyleStateEngine.Read(chosen.Manifest,workspace,since,pane),Prerequisites:chosen is null?null:StylePrerequisites.Read(chosen.Manifest,workspace,owner.options.SmokeTest?owner.StateDirectory:null),Capabilities:chosen is null?(new Dictionary<string,string>(),new List<(string Title,string Path)>()):StylePrerequisites.Capabilities(chosen.Manifest,workspace));
                });
                if(owner.closing||!owner.service.Snapshot.Sessions.Any(p=>p.Id==id)||Session.MightyStyle!=pane.MightyStyle||Session.MightyStyleHash!=pane.MightyStyleHash||Session.MightyStyleSince!=pane.MightyStyleSince)return;
                styleRegistry=data.Registry;activeStyle=data.Chosen;styleReading=data.Reading;stylePrerequisites=data.Prerequisites;styleCapabilities=data.Capabilities.Item1;styleCapabilityFiles=data.Capabilities.Item2;loadedStyleKey=key;
                var menu=new MenuFlyout();menu.Items.Add(Item("CLI",()=>ChooseStyle(null),pane.MightyStyle is null));
                foreach(var candidate in data.Registry.Styles){var item=Item(candidate.Manifest.Name+" · "+candidate.Source+(candidate.Approval=="pending"?" · "+Locale.Get("guidedPanel.approvalRequired"):""),()=>ChooseStyle(candidate),candidate.Id==pane.MightyStyle);item.IsEnabled=candidate.Approval!="revoked";menu.Items.Add(item);}
                stylePicker.Flyout=menu;RenderGuidedStyle();
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
        private void RenderGuidedStyle()
        {
            if(owner.closing||!owner.service.Snapshot.Sessions.Any(p=>p.Id==id))return;
            var pane=Session;stylePicker.IsEnabled=pane.Status!="running"&&!starting;stylePicker.Content=(activeStyle?.Manifest.Name??"CLI")+" ⌄";guidedBody.Children.Clear();
            if(activeStyle is not {} style)return;
            var evaluator=style.Evaluator;var phase=GuidedPhase(style);var busy=pane.Status=="running"||starting;var job=evaluator.JobOpen(pane);
            var placeholder=evaluator.Placeholder(phase,busy,false,job);if(placeholder.Length>0)input.PlaceholderText=placeholder;
            guidedBody.Children.Add(new TextBlock{Text=style.Manifest.Subtitle,FontSize=11,Opacity=.7,TextWrapping=TextWrapping.Wrap});
            if(style.Manifest.Phases.Length>0)guidedBody.Children.Add(new TextBlock{Text=string.Join("  →  ",style.Manifest.Phases.OrderBy(p=>p.Order).Select(p=>(phase?.Id==p.Id?"● ":"")+p.Title)),FontSize=11,TextWrapping=TextWrapping.Wrap});
            if(evaluator.Guidance(phase,busy,job) is {} guidance)guidedBody.Children.Add(new TextBlock{Text=guidance,FontSize=11,TextWrapping=TextWrapping.Wrap});
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
                        await owner.RunTerminalCommandAsync(actual,StyleManifest.Text(style.Manifest.Root.GetProperty("install"),"paneTitle")??style.Manifest.Name);
                    })));
                }
                guidedBody.Children.Add(Button(Locale.Get("guidedPanel.recheckButton"),LoadStyles));return;
            }
            var group=style.Manifest.Groups.FirstOrDefault(g=>g.Id==styleGroup)??evaluator.InitialGroup(styleCapabilities);
            if(style.Manifest.Groups.Length>1)
            {
                var picker=new ComboBox{FontSize=11,SelectedValuePath="Tag"};foreach(var g in style.Manifest.Groups)picker.Items.Add(new ComboBoxItem{Content=g.Title,Tag=g.Id});picker.SelectedValue=group?.Id;
                picker.SelectionChanged+=(_,_)=>{styleGroup=picker.SelectedValue as string;RenderGuidedStyle();};guidedBody.Children.Add(picker);
            }
            if(group?.Question is {} question)guidedBody.Children.Add(new TextBlock{Text=question,FontSize=11,TextWrapping=TextWrapping.Wrap});
            var chips=new VariableSizedWrapGrid{Orientation=Orientation.Horizontal,ItemWidth=145,ItemHeight=36};
            foreach(var action in evaluator.VisibleActions(phase,group,busy,job,styleStartingNew))
            {
                var button=Button((action.Glyph is {} glyph?glyph+" ":"")+action.Title,()=>InvokeStyleAction(action.Id));button.FontSize=11;button.Margin=new(2);button.Padding=new(5);ToolTipService.SetToolTip(button,action.Help);AutomationProperties.SetAutomationId(button,"style-action-"+action.Id);chips.Children.Add(button);
            }
            guidedBody.Children.Add(chips);
            foreach(var widget in styleReading?.Widgets??[])
            {
                if(widget.Kind=="progressBar")guidedBody.Children.Add(new ProgressBar{Minimum=0,Maximum=Math.Max(1,widget.Total??widget.Value),Value=widget.Value,IsIndeterminate=false});
                else if(widget.Kind=="list")foreach(var value in widget.Items??[])guidedBody.Children.Add(new TextBlock{Text="• "+value,FontSize=10,TextWrapping=TextWrapping.Wrap});
                else if(!string.IsNullOrEmpty(widget.Text))guidedBody.Children.Add(new TextBlock{Text=widget.Text,FontSize=10,TextWrapping=TextWrapping.Wrap});
            }
            foreach(var file in styleCapabilityFiles)guidedBody.Children.Add(Button(file.Title,()=>{owner.service.OpenResultFile(file.Path,Workspace.Path);return Task.CompletedTask;}));
            if(StyleManifest.Text(style.Manifest.Rules.GetProperty("start"),"resetTitle") is {} reset&&!busy)guidedBody.Children.Add(Button(reset,()=>{styleStartingNew=true;RenderGuidedStyle();return Task.CompletedTask;}));
        }
        private Task InvokeStyleAction(string actionId)=>owner.Act(async()=>
        {
            if(composingInput)return;
            var style=activeStyle??throw new InvalidOperationException(Locale.Get("guidedPanel.approvalRequired"));
            if(!StyleRegistry.Unchanged(style))throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
            var action=style.Manifest.Actions.First(a=>a.Id==actionId);if(action.RequiresText&&string.IsNullOrWhiteSpace(input.Text)){input.Focus(FocusState.Programmatic);return;}
            input.Text=action.Prompt(input.Text);input.SelectionStart=input.Text.Length;input.Focus(FocusState.Programmatic);
            if(Session.Status!="running")await Send();
        });
        internal async Task<Dictionary<string,object?>> RunStylesSmoke()
        {
            var original=Session;var draft=input.Text;
            try
            {
                await Change(p=>p with{Provider="claude",AgentViewMode="mighty",MightyStyle="ouroboros",MightyStyleHash=null,MightyStyleSince=null});
                await WaitUI(()=>!styleLoading);
                await LoadStyles();
                Require(activeStyle is {Id:"ouroboros"},"bundled style did not resolve");
                stylePrerequisites=new(true,[],null,null);RenderGuidedStyle();
                var phase=GuidedPhase(activeStyle!);var actions=activeStyle!.Evaluator.VisibleActions(phase,activeStyle.Manifest.Groups.FirstOrDefault(),false);
                Require(stylePicker.Content?.ToString()?.Contains("Ouroboros",StringComparison.Ordinal)==true,"style picker label missing");
                Require(guidedBody.Children.OfType<VariableSizedWrapGrid>().Any(grid=>grid.Children.Count>0),"style actions not drawn");
                var content=StyleContents(activeStyle!);Require(content.IsReadOnly&&content.Text.Contains("autoAllow",StringComparison.Ordinal),"approval omits manifest permissions");
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
            var phase=GuidedPhase(style);var action=style.Evaluator.RewriteAction(draft,phase,attachments,pane.Status=="running",pane.Logs.Any(l=>l.Kind=="user")||pane.GraphRuns is {Count:>0},styleStartingNew);
            var result=action is null?draft:style.Manifest.Actions.First(a=>a.Id==action).Prompt(draft);
            if(pane.MightyStyleSince is null||styleStartingNew)await Change(p=>p with{MightyStyleSince=Wire.Now()});styleStartingNew=false;return result;
        }
    }
}
