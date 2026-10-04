using System.Text.Json;

namespace MightyClaude.Core;

public sealed record StyleApprovalSection(string Id, string Title, IReadOnlyList<string> Lines, bool Foldable = false, bool Monospaced = false);
public sealed record StyleAttachmentItem(string Title, string Path, string Detail, bool ReadOnly = true);
public sealed record StyleRequestPresentation(string? Prefix, string? Icon, string Tint);

public static class StylePresentation
{
    private static string L(string key) => Locale.Get(key);
    private static string T(string key, params (string Key, string Value)[] args) => Locale.Get(key, args.ToDictionary(p=>p.Key,p=>p.Value));
    public static string Source(string source) => source switch { "bundled"=>L("styles.source.bundled"), "user"=>L("styles.source.user"), _=>L("styles.source.workspace") };
    public static string Name(RegisteredStyle style) => style.Manifest.Name + (style.Source=="bundled" ? "" : " · "+Source(style.Source));
    public static string State(string approval) => approval switch { "preApproved"=>L("styles.state.preApproved"), "approved"=>L("styles.state.approved"), "revoked"=>L("styles.state.revoked"), _=>L("styles.state.pending") };
    public static string Header(RegisteredStyle? style, StylePhase? phase, string mightyLabel) => mightyLabel + (style is null ? "" : " · "+Name(style)+(phase is null ? "" : " · "+phase.Title));
    public static StyleRequestPresentation Request(IEnumerable<RegisteredStyle> styles, string prompt)
    {
        foreach(var style in styles.Where(s=>s.Runnable).OrderBy(s=>s.Source switch{"bundled"=>0,"user"=>1,_=>2}).ThenBy(s=>s.Id,StringComparer.Ordinal))
        {
            var action=style.Evaluator.RecognisedAction(prompt); var name=StyleEvaluator.RecognisedName(style.Manifest,prompt);
            var alias=StyleManifest.Items(style.Manifest.Root,"aliases").FirstOrDefault(a=>StyleManifest.Text(a,"name")==name);
            var phase=style.Manifest.Phases.FirstOrDefault(p=>p.Id==(action?.Phase??StyleManifest.Text(alias,"phase")));
            if(action is null&&alias.ValueKind!=JsonValueKind.Object)continue;
            var prefix=action is null?phase?.Title:action.RequestTitle??(action.Glyph is {} glyph?glyph+" "+action.Title:phase?.Title??action.Title);
            var presentation=style.Manifest.Root.GetProperty("presentation");
            return new(prefix,action?.Icon??StyleManifest.Text(presentation,"icon"),action?.Tint??StyleManifest.Text(presentation,"tint")??"accent");
        }
        return new(null,null,"accent");
    }
    public static string? RequestPrefix(IEnumerable<RegisteredStyle> styles,string prompt) => Request(styles,prompt).Prefix;
    public static string ActionHelp(StyleAction action)
    {
        var values=new List<string>(); if(action.Help.Length>0)values.Add(action.Help);
        if(!string.IsNullOrEmpty(action.Scope))values.Add(T("styles.action.scope",("scope",action.Scope)));
        foreach(var flag in action.Flags??[])values.Add(flag=="readOnly"?L("styles.action.readOnly"):L("styles.action.userInvoked"));
        return string.Join(" · ",values);
    }
    public static string Icon(string? icon) => icon switch
    {
        "play.fill"=>"▷", "checkmark.seal"=>"✓", "arrow.clockwise" or "arrow.triangle.2.circlepath"=>"↻", "infinity"=>"∞",
        "questionmark.bubble" or "questionmark.square.dashed"=>"?", "arrow.up.message"=>"↑", "arrow.right"=>"→", "arrow.triangle.branch"=>"⑂",
        "bolt"=>"ϟ", "book" or "doc.text"=>"▤", "bookmark" or "flag"=>"⚑", "calendar"=>"▦", "chart.bar" or "gauge.with.dots.needle.33percent"=>"▥",
        "folder" or "tray"=>"▱", "list.bullet"=>"☷", "magnifyingglass"=>"⌕", "leaf"=>"♧", "wand.and.stars" or "sparkles"=>"✦",
        "lightbulb"=>"☀", "square.grid.2x2"=>"▦", "cube" or "puzzlepiece"=>"◈", "map"=>"⌘", "paintbrush" or "hammer"=>"⚒",
        "point.3.connected.trianglepath.dotted"=>"△", _=>""
    };
    public static (double Fraction,string Text)? Progress(StyleStateWidget widget)
    {
        if(widget.Value<0)return null;
        if(widget.Total is not {} total||total<0)return(0,widget.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var done=Math.Min(widget.Value,total);return(total==0?0:(double)done/total,$"{done}/{total}");
    }
    public static string Inline(string text) => string.Concat(text.EnumerateRunes().Where(r=>r.Value==0x200d||!StyleText.Banned(r.Value))).Trim().EnumerateRunes().Take(200).Aggregate("",(s,r)=>s+r);
    public static IReadOnlyList<StyleApprovalSection> Approval(RegisteredStyle style)
    {
        var m=style.Manifest; var result=new List<StyleApprovalSection>();
        var origin=new List<string>{Source(style.Source),style.Path,"SHA-256 "+style.Hash}; if(style.WorkspacePath is {} workspace)origin.Add(workspace);
        result.Add(new("origin",L("styles.approval.origin"),origin));
        var automatic=StyleManifest.Items(m.Root,"autoAllow").Select(a=>StyleManifest.Text(a,"server") is {} server?"mcp__"+server+"__"+StyleManifest.Text(a,"tool"):StyleManifest.Text(a,"tool")!).ToArray();
        result.Add(new("autoAllow",L("styles.approval.autoAllow"),automatic.Length==0?[L("styles.approval.noAutoAllow")]:automatic,false,true));
        if(m.Root.TryGetProperty("install",out var install))result.Add(new("install",L("styles.approval.install"),[StyleManifest.Text(install,"command")!,L("styles.approval.installNotice")],false,true));
        var enter=m.Rules.GetProperty("enter");
        result.Add(new("enter",L("styles.approval.enter"),StyleManifest.Text(enter,"kind")=="verbatim"?[L("styles.approval.verbatim")]:[T("styles.approval.rewrite",("phase",m.Phases.First(p=>p.Id==StyleManifest.Text(enter,"phase")).Title)),m.Actions.First(a=>a.Id==StyleManifest.Text(enter,"action")).PromptTemplate],false,true));
        if(m.Root.TryGetProperty("stateSources",out var sources))
        {
            var lines=StyleManifest.Items(sources,"files").Select(f=>Locale.Get("styles.approval.stateFile",new Dictionary<string,string>{{"path",StyleManifest.Text(f,"path")!},{"parser",StyleManifest.Text(f,"parser")!},{"widget",StyleManifest.Text(f,"widget")!}})).ToList();
            lines.AddRange(StyleManifest.Items(sources,"runEvents").Select(f=>Locale.Get("styles.approval.stateRunEvent",new Dictionary<string,string>{{"event",StyleManifest.Text(f,"event")!},{"aggregate",StyleManifest.Text(f,"aggregate")!},{"widget",StyleManifest.Text(f,"widget")!}})));
            var files=StyleManifest.Items(sources,"files");
            lines.AddRange(StyleManifest.Items(m.Rules.GetProperty("phase"),"stateOverrides").Select(o=>Locale.Get("styles.approval.stateOverride",new Dictionary<string,string>{{"phase",StyleManifest.Text(o,"phase")!},{"path",StyleManifest.Text(files[(int)o.GetProperty("sourceIndex").GetDouble()],"path")!},{"condition",StyleManifest.Text(o,"condition")!}})));
            result.Add(new("state",Locale.Get("styles.approval.stateTitle"),lines,false,true));
        }
        result.Add(new("identity",L("styles.approval.identity"),[m.Name,m.Id,m.Summary,m.Subtitle]));
        var structure=m.Phases.OrderBy(p=>p.Order).Select(p=>p.Id+" · "+p.Title).Concat(m.Groups.Select(g=>string.Join(" · ",new[]{g.Id,g.Title,g.Axis,g.Question,string.Join(", ",g.Actions)}.Where(s=>!string.IsNullOrEmpty(s))))).Concat(StyleManifest.Items(m.Root,"aliases").Select(a=>StyleManifest.Text(a,"name")+" → "+StyleManifest.Text(a,"phase"))).Append(Pretty(m.Root.GetProperty("recognition"))).ToArray();
        result.Add(new("structure",L("styles.approval.structure"),structure,true));
        result.Add(new("rules",L("styles.approval.rules"),[Pretty(m.Rules)],true,true));
        result.Add(new("actions",T("styles.approval.actions",("count",m.Actions.Length.ToString())),m.Actions.SelectMany(a=>new[]{a.Title+" ("+a.Id+")"+(a.TakesText?" · "+L("styles.action.takesText"):"")+(a.RequiresText?" · "+L("styles.action.requiresText"):""),ActionHelp(a),a.PromptTemplate}).ToArray(),true,true));
        result.Add(new("presentation",L("styles.approval.presentation"),[Pretty(m.Root.GetProperty("presentation")),Pretty(m.Root.GetProperty("placeholders")),Pretty(m.Root.GetProperty("guidance"))],true));
        return result;
    }
    private static string Pretty(JsonElement value)=>JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true});
}

/// Both explicit approval
/// presses are scoped to one immutable preview and a visible auto-allow block.
public sealed class StyleApprovalDecision(int automaticTools)
{
    public bool Confirming {get;private set;}
    public bool Press(bool autoAllowVisible)
    { if(!autoAllowVisible)return false;if(automaticTools>0&&!Confirming){Confirming=true;return false;}return true; }
}
