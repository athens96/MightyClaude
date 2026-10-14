using System.Text.RegularExpressions;
namespace MightyClaude.Core;

public static class StyleRunPermissions
{
    // Bundled manifests use &&, unavailable in Windows PowerShell 5.1. Pass
    // the original command as one literal argument to cmd, shown in confirmation.
    public static string InstallCommand(string command,string shell)=>Path.GetFileNameWithoutExtension(shell).Equals("powershell",StringComparison.OrdinalIgnoreCase)
        ? "& $env:ComSpec /d /s /c '"+command.Replace("'","''",StringComparison.Ordinal)+"'" : command;
    public static bool ValidWireName(string name)
    {
        if(name=="ToolSearch")return true;
        var parts=name.Split("__",StringSplitOptions.None);
        return parts.Length==3&&parts[0]=="mcp"&&parts[1].StartsWith("plugin_",StringComparison.Ordinal)&&!parts[1].EndsWith('_')&&Regex.IsMatch(parts[1],"^[A-Za-z0-9_-]{1,64}$",RegexOptions.CultureInvariant)
            &&parts[2]!="AskUserQuestion"&&!parts[2].StartsWith('_')&&Regex.IsMatch(parts[2],"^[A-Za-z0-9_-]{1,64}$",RegexOptions.CultureInvariant);
    }
    /// <summary>
    /// Whether the pane's style allows this request by itself (M/AppStore.swift:1160, M/AppStore+Styles.swift:539-541). Judged
    /// at each request against the style as it stands right then — the pane's own choice, the trust record and the file's
    /// bytes are read again — so a revoked or changed style, or a pane that left it, stops allowing at once (§4.6).
    /// </summary>
    public static bool AutoAllows(ToolPermissionRequest request,RunSession? pane,string profile,string workspace)
    {
        if(request.State!="pending"||!request.CanAllow||request.ToolName=="AskUserQuestion"||!ValidWireName(request.ToolName))return false;
        if(pane is null||pane.Kind!="claude"||pane.Provider!="claude"||pane.AgentViewMode!="mighty"||pane.MightyStyle is null)return false;
        try{return StyleRegistry.Load(profile,workspace).Runnable(pane.MightyStyle,pane.MightyStyleHash) is {} style&&StyleRegistry.Unchanged(style)&&style.Evaluator.AutoAllowed(request.ToolName);}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){return false;}
    }
    /// <summary>
    /// The run-start list a host without the approval card still launches with (<see cref="ProviderCatalog.Arguments"/>'s
    /// <c>--allowedTools</c>). A host that shows the card judges each request instead (<see cref="AutoAllows"/>) and
    /// launches without it (<see cref="ProviderInput.HostPrompts"/>).
    /// </summary>
    public static StartRunRequest Bind(StartRunRequest request,RunSession selected,string profile,string workspace)
    {
        if(selected.Kind!="claude"||selected.Provider!="claude"||selected.AgentViewMode!="mighty"||selected.MightyStyle is null)return request with{StyleAutoAllow=null};
        if(request.SessionId!=selected.Id||request.WorkspaceId!=selected.WorkspaceId||request.Kind!="claude"||request.Provider!="claude")throw new ArgumentException("Style selection belongs to a different pane.");
        var style=StyleRegistry.Load(profile,workspace).Runnable(selected.MightyStyle,selected.MightyStyleHash)??throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
        if(!StyleRegistry.Unchanged(style))throw new IOException(Locale.Get("guidedPanel.approvalRequired"));
        var grants=StyleManifest.Items(style.Manifest.Root,"autoAllow").Select(e=>StyleManifest.Text(e,"server") is {} server?"mcp__"+server+"__"+StyleManifest.Text(e,"tool"):StyleManifest.Text(e,"tool")!).ToArray();
        if(grants.Any(g=>!ValidWireName(g)))throw new IOException("Invalid style tool permission.");
        // §1.17 (v6): a plan-mode style starts every new request in plan mode; the pane's stored mode stays.
        return request with{StyleAutoAllow=Array.AsReadOnly(grants),PermissionModeOverride=style.Evaluator.LaunchPermissionMode=="plan"?"plan":request.PermissionModeOverride};
    }
}
