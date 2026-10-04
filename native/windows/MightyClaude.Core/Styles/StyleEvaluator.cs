using System.Text.Json;

namespace MightyClaude.Core;

public sealed class StyleEvaluator(StyleManifest manifest)
{
    public StyleManifest Manifest { get; } = manifest;
    private static string? S(JsonElement e,string k)=>StyleManifest.Text(e,k);
    public static string? RecognisedName(StyleManifest m,string prompt)
    {
        var value=prompt.Trim();var rule=m.Root.GetProperty("recognition");
        var prefix=StyleManifest.Strings(rule,"prefixes").FirstOrDefault(p=>value.StartsWith(p,StringComparison.Ordinal));
        if(prefix is null)return null;var name=string.Concat(value[prefix.Length..].TakeWhile(c=>!char.IsWhiteSpace(c)));
        return name.Length==0?null:StyleManifest.Bool(rule,"lowercase")?name.ToLowerInvariant():name;
    }
    public StyleAction? RecognisedAction(string prompt){var n=RecognisedName(Manifest,prompt);return Manifest.Actions.FirstOrDefault(a=>a.Id==n)??Manifest.Actions.FirstOrDefault(a=>a.Match is not null&&a.Match==n);}
    public StylePhase? CurrentPhase(IEnumerable<string> prompts,IReadOnlyDictionary<int,StyleFileState>? states=null)
    {
        var rule=Manifest.Rules.GetProperty("phase");if(S(rule,"kind")=="none")return null;
        var id=S(rule,"default");
        foreach(var prompt in prompts.Reverse())
        {
            var action=RecognisedAction(prompt);var phase=action?.Phase;
            if(action is null){var n=RecognisedName(Manifest,prompt);phase=StyleManifest.Items(Manifest.Root,"aliases").Where(a=>S(a,"name")==n).Select(a=>S(a,"phase")).FirstOrDefault();}
            if(phase is not null){id=phase;break;}
        }
        var current=Manifest.Phases.FirstOrDefault(p=>p.Id==id);
        foreach(var o in StyleManifest.Items(rule,"stateOverrides"))
        {
            var candidate=Manifest.Phases.First(p=>p.Id==S(o,"phase"));var index=(int)o.GetProperty("sourceIndex").GetDouble();
            if(states!=null&&states.TryGetValue(index,out var state)&&candidate.Order>(current?.Order??-1)&&(S(o,"condition")=="fileExists"?state.Exists:state.AllChecked))current=candidate;
        }
        return current;
    }
    public StyleGroup? InitialGroup(IReadOnlyDictionary<string,string>? capabilities=null)
    {
        var rule=Manifest.Rules.GetProperty("initialGroup");var id=S(rule,"group");
        if(id is null&&capabilities!=null&&capabilities.TryGetValue(S(rule,"capability")!,out var state))id=S(rule.GetProperty("map"),state);
        return Manifest.Groups.FirstOrDefault(g=>g.Id==id)??Manifest.Groups.FirstOrDefault();
    }
    public StyleAction[] VisibleActions(StylePhase? phase,StyleGroup? group,bool running,bool jobOpen=false,bool startingNew=false)
    {
        var start=Manifest.Rules.GetProperty("start");var next=Manifest.Rules.GetProperty("next");string[] ids;
        if(jobOpen)ids=Manifest.Root.TryGetProperty("job",out var job)?StyleManifest.Strings(job,"whileOpen"):[];
        else if(running&&S(next,"kind")=="byPhase")ids=[];
        else if(S(start,"kind")=="actions"&&(phase?.Id==S(start,"phase")||startingNew))ids=StyleManifest.Strings(start,"actions");
        else ids=S(next,"kind")=="byGroup"?group?.Actions??[]:phase is null?[]:StyleManifest.Strings(next.GetProperty("map"),phase.Id);
        return ids.Select(id=>Manifest.Actions.First(a=>a.Id==id)).ToArray();
    }
    public string? RewriteAction(string draft,StylePhase? phase,bool attachments,bool running,bool hasRequests,bool startingNew=false)
    {
        var rule=Manifest.Rules.GetProperty("enter");
        return S(rule,"kind")=="rewriteBareDraftTo"&&!attachments&&!running&&(!hasRequests||startingNew)&&!draft.TrimStart().StartsWith('/')&&RecognisedName(Manifest,draft)==null&&phase?.Id==S(rule,"phase")?S(rule,"action"):null;
    }
    public string Placeholder(StylePhase? phase,bool running,bool answering=false,bool jobOpen=false)
    {
        var p=Manifest.Root.GetProperty("placeholders");
        return S(p,answering?"answering":running||jobOpen?"running":S(Manifest.Rules.GetProperty("enter"),"phase")==phase?.Id&&p.TryGetProperty("initial",out _)?"initial":"idle")??"";
    }
    public string? Guidance(StylePhase? phase,bool running,bool jobOpen=false)
    {
        string? text;
        if(jobOpen)text=Manifest.Root.TryGetProperty("job",out var job)?S(job,"guidance"):null;
        else text=S(Manifest.Root.GetProperty("guidance"),running?"running":S(Manifest.Rules.GetProperty("start"),"phase")==phase?.Id?"start":"next");
        if(string.IsNullOrEmpty(text))return null;
        return phase!=null?text.Replace("{phase}",phase.Title,StringComparison.Ordinal):text.Replace("{phase} ","",StringComparison.Ordinal).Replace("{phase}","",StringComparison.Ordinal);
    }
    public bool AutoAllowed(string toolName)=>toolName.Split("__").Last()!="AskUserQuestion"&&StyleManifest.Items(Manifest.Root,"autoAllow").Any(e=>(S(e,"server") is {} server?"mcp__"+server+"__"+S(e,"tool"):S(e,"tool"))==toolName);
    public bool JobOpen(RunSession session)
    {
        if(!Manifest.Root.TryGetProperty("job",out var job))return false;
        int lastOpen=-1,lastClose=-1,index=0;
        foreach(var log in session.Logs)
        {
            if(log.Activity is {} a&&a.Kind!="turn"&&a.ToolName is {} name)
            {
                bool Matches(JsonElement m)=>S(m,"tool")==name.Split("__").Last()&&(S(m,"contains") is not {} yes||(a.Output??"").Contains(yes,StringComparison.Ordinal))&&(S(m,"notContains") is not {} no||!(a.Output??"").Contains(no,StringComparison.Ordinal));
                if(StyleManifest.Items(job,"open").Any(Matches))lastOpen=index;
                if(StyleManifest.Items(job,"close").Any(Matches))lastClose=index;
            }
            index++;
        }
        return lastOpen>=0&&lastOpen>lastClose;
    }
}
public sealed record StyleFileState(bool Exists,bool AllChecked);
public sealed record StyleStateWidget(string Kind,string? Text=null,int Value=0,int? Total=null,IReadOnlyList<string>? Items=null);
public sealed record StyleStateReading(IReadOnlyDictionary<int,StyleFileState> Files,IReadOnlyList<StyleStateWidget> Widgets);
