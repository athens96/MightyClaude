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
    /// <summary>One widget as the phone payload carries it (M/StylePanelProjection.swift Widget): only the fields its kind has.</summary>
    public static Dictionary<string,object?> Payload(StyleStateWidget widget)
    {
        var value=new Dictionary<string,object?>{["kind"]=widget.Kind};
        switch(widget.Kind)
        {
            case "progressBar":value["value"]=widget.Value;if(widget.Total is {} total)value["total"]=total;break;
            case "list":value["items"]=widget.Items??[];break;
            case "taskList":value["items"]=(widget.Tasks??[]).Select(t=>{var item=new Dictionary<string,object?>{["text"]=t.Text,["kind"]=t.Kind,["status"]=t.Status,["startedAt"]=t.StartedAt};if(t.EndedAt is {} ended)item["endedAt"]=ended;return item;}).ToArray();break;
            default:value["text"]=widget.Text??"";break;
        }
        return value;
    }
    /// <summary>One background task as both platforms draw it (M/StyleWidgetPresentation.swift Task).</summary>
    public sealed record TaskRow(string Text,string Kind,string KindTitle,string Status,string StatusTitle,bool Running,DateTimeOffset? StartedAt,DateTimeOffset? EndedAt)
    {
        /// <summary>To now while it runs, to its end once it ended.</summary>
        public string Elapsed(DateTimeOffset now)=>StartedAt is {} start?StylePresentation.Elapsed(start,Running?now:EndedAt??now):"";
    }
    public static IReadOnlyList<TaskRow> Tasks(StyleStateWidget widget)=>(widget.Tasks??[]).Take(StyleStateEngine.MaximumTaskListItems).Select(t=>
    {
        var kind=t.Kind is "agent" or "shell"?t.Kind:"other";var status=t.Status is "running" or "completed" or "failed" or "stopped"?t.Status:"unknown";var title=TaskKind(kind);var text=Inline(t.Text);
        return new TaskRow(text.Length==0?title:text,kind,title,status,TaskStatus(status),status=="running",AgentRunTiming.Parse(t.StartedAt),AgentRunTiming.Parse(t.EndedAt));
    }).ToArray();
    public static string TaskKind(string kind)=>kind switch{"agent"=>L("styles.state.taskKind.agent"),"shell"=>L("styles.state.taskKind.shell"),_=>L("styles.state.taskKind.other")};
    public static string TaskStatus(string status)=>status switch{"running"=>L("styles.state.taskStatus.running"),"completed"=>L("styles.state.taskStatus.completed"),"failed"=>L("styles.state.taskStatus.failed"),"stopped"=>L("styles.state.taskStatus.stopped"),_=>L("styles.state.taskStatus.unknown")};
    /// <summary>Seconds, minutes and seconds, or hours and minutes (styles.state.elapsed*); a clock that went back reads as 0.</summary>
    public static string Elapsed(DateTimeOffset start,DateTimeOffset end)
    {
        var seconds=Math.Max(0,(long)(end-start).TotalSeconds);string N(long n)=>n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if(seconds<60)return T("styles.state.elapsedSeconds",("seconds",N(seconds)));
        if(seconds<3600)return T("styles.state.elapsedMinutes",("minutes",N(seconds/60)),("seconds",N(seconds%60)));
        return T("styles.state.elapsedHours",("hours",N(seconds/3600)),("minutes",N(seconds%3600/60)));
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
        var planState=StyleManifest.Text(m.Rules.GetProperty("phase"),"kind")=="planState";
        if(m.Root.TryGetProperty("stateSources",out var sources)||planState)
        {
            var lines=StyleManifest.Items(sources,"files").Select(f=>Locale.Get("styles.approval.stateFile",new Dictionary<string,string>{{"path",StyleManifest.Text(f,"path")!},{"parser",StyleManifest.Text(f,"parser")!},{"widget",StyleManifest.Text(f,"widget")!}})).ToList();
            lines.AddRange(StyleManifest.Items(sources,"runEvents").Select(f=>Locale.Get("styles.approval.stateRunEvent",new Dictionary<string,string>{{"event",StyleManifest.Text(f,"event")!},{"aggregate",StyleManifest.Text(f,"aggregate")!},{"widget",StyleManifest.Text(f,"widget")!}})));
            var files=StyleManifest.Items(sources,"files");
            lines.AddRange(StyleManifest.Items(m.Rules.GetProperty("phase"),"stateOverrides").Select(o=>Locale.Get("styles.approval.stateOverride",new Dictionary<string,string>{{"phase",StyleManifest.Text(o,"phase")!},{"path",StyleManifest.Text(files[(int)o.GetProperty("sourceIndex").GetDouble()],"path")!},{"condition",StyleManifest.Text(o,"condition")!}})));
            // §1.17 (v6): the pane's plan-mode state, then the stages that move the phase.
            lines.AddRange(StyleManifest.Items(sources,"runState").Select(r=>Locale.Get("styles.approval.stateRunState",new Dictionary<string,string>{{"source",StyleManifest.Text(r,"source")!},{"widget",StyleManifest.Text(r,"widget")!}})));
            if(planState)lines.AddRange(StyleStateEngine.PlanStages.Select(stage=>Locale.Get("styles.approval.statePlanStage",new Dictionary<string,string>{{"phase",StyleManifest.Text(m.Rules.GetProperty("phase").GetProperty("map"),stage)??""},{"stage",stage}})));
            result.Add(new("state",Locale.Get("styles.approval.stateTitle"),lines,false,true));
        }
        if(m.Root.TryGetProperty("launch",out var launch))result.Add(new("launch",L("styles.approval.launchTitle"),[T("styles.approval.launchPlan",("mode",StyleManifest.Text(launch,"permissionMode")!))]));
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
