using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

public sealed class StyleManifestException(string code, string detail = "") : Exception(code + (detail.Length > 0 ? ": " + StyleText.Safe(detail) : ""))
{
    public string Code { get; } = code;
}
public static class StyleText
{
    public static bool Banned(int c) => c <= 31 || c is >= 127 and <= 159 || c is 0xad or 0x61c or 0x2060 or 0xfeff || c is >= 0x200b and <= 0x200f || c is >= 0x2028 and <= 0x202e || c is >= 0x2066 and <= 0x2069;
    public static int Length(string value) => new StringInfo(value).LengthInTextElements;
    public static string Safe(string value, int limit = 64)
    {
        var clean = string.Concat(value.EnumerateRunes().Select(r => Banned(r.Value) ? "�" : r.ToString()));
        var elements = StringInfo.ParseCombiningCharacters(clean);
        return elements.Length <= limit ? clean : clean[..elements[limit - 1]] + "…";
    }
    public static string Folded(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)));
}
public sealed record StyleAction(string Id, string Title, string Help, string PromptTemplate, bool TakesText, bool RequiresText, string? FoldText, string? Match, string? Phase, string? Glyph, string? Icon, string? Tint, string? RequestTitle, string? Scope = null, string[]? Flags = null)
{
    public string Prompt(string text)
    {
        var folded = FoldText == "oneLine" ? string.Join(" ", text.Split(['\r','\n']).Select(s => s.Trim()).Where(s => s.Length > 0)) : text.Trim();
        var at = PromptTemplate.IndexOf("{text}", StringComparison.Ordinal);
        if (at < 0) return PromptTemplate;
        if (folded.Length > 0) return PromptTemplate.Replace("{text}", folded, StringComparison.Ordinal);
        var start = at; while (start > 0 && PromptTemplate[start - 1] == ' ') start--;
        return PromptTemplate[..start] + PromptTemplate[(at + 6)..];
    }
}
public sealed record StylePhase(string Id, string Title, int Order);
public sealed record StyleGroup(string Id, string Title, string? Axis, string? Question, string[] Actions);
public sealed class StyleManifest
{
    public JsonElement Root { get; }
    public string Id => Root.GetProperty("id").GetString()!;
    public string Name => Root.GetProperty("name").GetString()!;
    public string Summary => Root.GetProperty("summary").GetString()!;
    public string Subtitle => Root.GetProperty("subtitle").GetString()!;
    public StyleAction[] Actions { get; }
    public StylePhase[] Phases { get; }
    public StyleGroup[] Groups { get; }
    public JsonElement Rules => Root.GetProperty("rules");
    internal StyleManifest(JsonElement root)
    {
        Root = root.Clone();
        Actions = Items(root, "actions").Select(a => new StyleAction(Text(a,"id")!,Text(a,"title")!,Text(a,"help")!,Text(a,"prompt")!,a.GetProperty("takesText").GetBoolean(),Bool(a,"requiresText"),Text(a,"foldText"),Text(a,"match"),Text(a,"phase"),Text(a,"glyph"),Text(a,"icon"),Text(a,"tint"),Text(a,"requestTitle"),Text(a,"scope"),Strings(a,"flags"))).ToArray();
        Phases = Items(root,"phases").Select(p => new StylePhase(Text(p,"id")!,Text(p,"title")!,(int)p.GetProperty("order").GetDouble())).ToArray();
        Groups = Items(root,"groups").Select(g => new StyleGroup(Text(g,"id")!,Text(g,"title")!,Text(g,"axis"),Text(g,"question"),Strings(g,"actions"))).ToArray();
    }
    public static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name,out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static bool Bool(JsonElement e,string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name,out var v) && v.ValueKind == JsonValueKind.True;
    public static JsonElement[] Items(JsonElement e,string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name,out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToArray() : [];
    public static string[] Strings(JsonElement e,string name) => Items(e,name).Select(v=>v.GetString()!).ToArray();
}

/// Schema 1 is deliberately closed. Raw-key scanning runs before JSON parsing so
/// duplicate/escaped keys cannot present different permissions to the reader.
public static class StyleManifestDecoder
{
    public const int MaximumBytes = 262144;
    private static void Fail(string code,string path="") => throw new StyleManifestException(code,path);
    private static readonly HashSet<string> Icons = ["point.3.connected.trianglepath.dotted","questionmark.bubble","wand.and.stars","leaf","play.fill","checkmark.seal","arrow.triangle.2.circlepath","infinity","gauge.with.dots.needle.33percent","lightbulb","square.grid.2x2","arrow.up.message","questionmark.square.dashed","arrow.right","arrow.clockwise","arrow.triangle.branch","bolt","book","bookmark","calendar","chart.bar","cube","doc.text","flag","folder","hammer","list.bullet","magnifyingglass","map","paintbrush","puzzlepiece","sparkles","tray"];
    private static readonly HashSet<string> Tints = ["accent","purple","teal","indigo","mint","orange","green","red","secondary"];
    private sealed class Reader
    {
        private readonly Dictionary<string,JsonElement> fields;
        public string Path { get; }
        public Reader(JsonElement? e,string path)
        {
            Path=path;
            if(e is null) Fail("E_MISSING_FIELD",path);
            if(e!.Value.ValueKind!=JsonValueKind.Object) Fail("E_TYPE",path);
            fields=e.Value.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value);
        }
        public JsonElement? Take(string name) => fields.Remove(name,out var v)?v:null;
        public string At(string name)=>Path.Length==0?name:Path+"."+name;
        public string S(string name,int min=1,int max=400,bool separator=true)=>Str(Take(name),At(name),min,max,separator);
        public string? O(string name,int min=0,int max=400,bool separator=true){var v=Take(name);return v is null?null:Str(v,At(name),min,max,separator);}
        public void End(){if(fields.Count>0)Fail("E_UNKNOWN_FIELD",At(fields.Keys.Order(StringComparer.Ordinal).First()));}
    }
    private static string Str(JsonElement? v,string p,int min=1,int max=400,bool separator=true)
    {
        if(v is null)Fail("E_MISSING_FIELD",p);
        if(v!.Value.ValueKind!=JsonValueKind.String)Fail("E_TYPE",p);
        var s=v.Value.GetString()!;
        if(s.EnumerateRunes().Any(r=>StyleText.Banned(r.Value)))Fail("E_CONTROL_CHAR",p);
        if(!separator&&s.Contains('·'))Fail("E_RESERVED_SEPARATOR",p);
        if(StyleText.Length(s)<min||StyleText.Length(s)>max||StyleText.Length(s)>400)Fail("E_STRING_LENGTH",p);
        return s;
    }
    private static bool Boolean(JsonElement? v,string p,bool? fallback=null){if(v is null){if(fallback is {} b)return b;Fail("E_MISSING_FIELD",p);}if(v!.Value.ValueKind is not(JsonValueKind.True or JsonValueKind.False))Fail("E_TYPE",p);return v.Value.GetBoolean();}
    private static double Integer(JsonElement? v,string p){if(v is null)Fail("E_MISSING_FIELD",p);if(v!.Value.ValueKind!=JsonValueKind.Number||!v.Value.TryGetDouble(out var d)||!double.IsFinite(d)||d!=Math.Truncate(d)||Math.Abs(d)>9007199254740992)Fail("E_TYPE",p);return v.Value.GetDouble();}
    private static JsonElement[] Array(JsonElement? v,string p,int max,int min=0){if(v is null)Fail("E_MISSING_FIELD",p);if(v!.Value.ValueKind!=JsonValueKind.Array)Fail("E_TYPE",p);var a=v.Value.EnumerateArray().ToArray();if(a.Length<min)Fail("E_MISSING_FIELD",p);if(a.Length>max)Fail("E_LIMIT",p);return a;}
    private static JsonElement[] OptionalArray(JsonElement? v,string p,int max)=>v is null?[]:Array(v,p,max);
    private static string Id(string s,bool action=false){if(!Regex.IsMatch(s,action?"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$":"^[a-z0-9][a-z0-9-]{0,39}$",RegexOptions.CultureInvariant))Fail("E_ID_SHAPE",s);return s;}
    private static void Known(string value,IEnumerable<string> allowed,string code,string p=""){if(!allowed.Contains(value,StringComparer.Ordinal))Fail(code,p.Length==0?value:p);}
    private static void Presentation(Reader r){var i=r.O("icon",1);if(i!=null)Known(i,Icons,"E_UNKNOWN_ICON");var t=r.O("tint",1);if(t!=null)Known(t,Tints,"E_UNKNOWN_TINT");}
    private static void Guidance(string? s,string p){if(s is not null&&s.Replace("{phase}","",StringComparison.Ordinal).IndexOfAny(['{','}'])>=0)Fail("E_PROMPT_PLACEHOLDER",p);}
    private static void Unique(HashSet<string> seen,string id,string p){if(!seen.Add(id))Fail("E_DUPLICATE_ID",p);}
    public static bool SafeRelativePath(string path)=>!string.IsNullOrWhiteSpace(path)&&!path.StartsWith('/')&&!path.StartsWith('~')&&!path.StartsWith('\\')&&!path.Contains(':')&&!path.Split(['/','\\']).Contains("..");
    public static StyleManifest Decode(ReadOnlyMemory<byte> bytes,string source="user")
    {
        if(bytes.Length>MaximumBytes)Fail("E_TOO_LARGE");
        Scan(bytes.Span);
        JsonDocument doc;
        try{doc=JsonDocument.Parse(bytes,new JsonDocumentOptions { MaxDepth=8 });}catch(JsonException){throw new StyleManifestException("E_NOT_JSON");}
        using(doc){if(doc.RootElement.ValueKind!=JsonValueKind.Object)Fail("E_NOT_JSON");Structure(doc.RootElement);var m=new StyleManifest(doc.RootElement);Validate(m,source);return m;}
    }
    private static void Scan(ReadOnlySpan<byte> bytes)
    {
        var stack=new Stack<(bool Object,HashSet<string> Keys)>();
        try
        {
            var reader=new Utf8JsonReader(bytes,new JsonReaderOptions{MaxDepth=256});
            while(reader.Read())
            {
                if(reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray){stack.Push((reader.TokenType==JsonTokenType.StartObject,[]));if(stack.Count>8)Fail("E_TOO_DEEP");}
                else if(reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)stack.Pop();
                else if(reader.TokenType==JsonTokenType.PropertyName)
                {
                    var name=reader.GetString()!;
                    if(reader.ValueIsEscaped)Fail("E_KEY_ESCAPE",name);
                    if(name.EnumerateRunes().Any(r=>StyleText.Banned(r.Value)))Fail("E_CONTROL_CHAR",name);
                    if(StyleText.Length(name)>64)Fail("E_STRING_LENGTH",name);
                    var keys=stack.Peek().Keys;if(keys.Contains(name))Fail("E_DUPLICATE_KEY",name);
                    if(stack.Count==1&&keys.Count==0&&name!="schema")Fail("E_SCHEMA_NOT_FIRST");keys.Add(name);
                }
            }
        }
        catch(JsonException){Fail("E_NOT_JSON");}
    }
    private static void Structure(JsonElement root)
    {
        var r=new Reader(root,"");var schema=r.Take("schema");if(schema is null)Fail("E_SCHEMA_MISSING");if(Integer(schema,"schema")!=1)Fail("E_SCHEMA_VERSION");
        Id(r.S("id",1,40));r.S("name",1,40,false);r.S("summary",1,240);r.S("subtitle",1,80);
        var p=new Reader(r.Take("placeholders"),"placeholders");p.S("idle",0,120);p.S("answering",0,120);p.O("initial",0,120);p.O("running",0,120);p.End();
        p=new Reader(r.Take("guidance"),"guidance");foreach(var k in new[]{"start","next","running"})Guidance(p.O(k,0,160),p.At(k));p.End();
        p=new Reader(r.Take("prerequisites"),"prerequisites");Known(p.S("mode",1,16),["all","any"],"E_UNKNOWN_RULE");var report=p.O("report",1,16);if(report!=null)Known(report,["first","all"],"E_UNKNOWN_RULE");
        int index=0;
        foreach(var e in Array(p.Take("probes"),p.At("probes"),16))
        {
            var q=new Reader(e,$"prerequisites.probes[{index++}]");var kind=q.S("kind",1,32);q.S("missing",1,200);q.O("hint",0,200);Boolean(q.Take("install"),q.At("install"),true);
            Known(kind,["plugin","executable","skill"],"E_UNKNOWN_PROBE");var name=q.S(kind=="plugin"?"prefix":"name",1,64);if(!Regex.IsMatch(name,"^[A-Za-z0-9_][A-Za-z0-9@._-]{0,63}$"))Fail("E_PROBE_NAME_SHAPE",name);
            if(kind=="skill"){var a=Array(q.Take("scopes"),q.At("scopes"),2);var seen=new HashSet<string>();if(a.Length==0)Fail("E_SCOPES");foreach(var s in a){if(s.ValueKind!=JsonValueKind.String||s.GetString() is not("user" or "workspace")||!seen.Add(s.GetString()!))Fail("E_SCOPES");}}
            q.End();
        }
        p.End();
        if(r.Take("install") is {} install){p=new Reader(install,"install");p.S("command",1,400);p.S("paneTitle",1,40,false);p.End();}
        index=0;var ids=new HashSet<string>();var orders=new HashSet<string>();
        foreach(var e in Array(r.Take("phases"),"phases",16)){p=new Reader(e,$"phases[{index++}]");Unique(ids,Id(p.S("id",1,40)),"phases");p.S("title",1,24,false);var n=Integer(p.Take("order"),p.At("order"));if(n<0||n>99)Fail("E_TYPE",p.At("order"));Unique(orders,n.ToString(CultureInfo.InvariantCulture),p.At("order"));p.End();}
        index=0;ids=[];
        foreach(var e in Array(r.Take("groups"),"groups",16,1)){p=new Reader(e,$"groups[{index++}]");Unique(ids,Id(p.S("id",1,40)),"groups");p.S("title",1,24);p.O("axis",0,60);p.O("question",0,200);foreach(var s in Array(p.Take("actions"),p.At("actions"),100,1))Str(s,p.At("actions"),1,64);p.End();}
        index=0;ids=[];
        foreach(var e in Array(r.Take("actions"),"actions",100,1))
        {
            p=new Reader(e,$"actions[{index++}]");var id=Id(p.S("id",1,64),true);p.S("title",1,40);p.S("help",0,400);p.O("scope",0,120);var prompt=p.S("prompt",1,400);var takes=Boolean(p.Take("takesText"),p.At("takesText"));Boolean(p.Take("requiresText"),p.At("requiresText"),false);var fold=p.O("foldText",1,16);p.O("match",1,64);p.O("phase",1,40);
            var flags=new HashSet<string>();foreach(var f in OptionalArray(p.Take("flags"),p.At("flags"),2)){if(f.ValueKind!=JsonValueKind.String||f.GetString() is not("userInvoked" or "readOnly")||!flags.Add(f.GetString()!))Fail("E_UNKNOWN_FLAG");}
            Presentation(p);if(p.Take("glyph") is {} glyph){if(glyph.ValueKind!=JsonValueKind.String||!StyleEmoji.Valid(glyph.GetString()!))Fail("E_TYPE",p.At("glyph"));}
            p.O("requestTitle",1,40,false);p.End();
            if(takes){if(fold==null)Fail("E_MISSING_FIELD",p.At("foldText"));Known(fold!,["trimOnly","oneLine"],"E_UNKNOWN_RULE");}else if(fold!=null)Fail("E_FOLD_TEXT",id);
            var rest=prompt.Replace("{text}","",StringComparison.Ordinal);var count=(prompt.Length-rest.Length)/6;if(count>1||rest.IndexOfAny(['{','}'])>=0)Fail("E_PROMPT_PLACEHOLDER",id);if(takes!=(count==1))Fail("E_TAKES_TEXT_MISMATCH",id);Unique(ids,id,"actions");
        }
        index=0;ids=[];foreach(var e in Array(r.Take("aliases"),"aliases",64)){p=new Reader(e,$"aliases[{index++}]");Unique(ids,p.S("name",1,64),"aliases");p.S("phase",1,40);p.End();}
        p=new Reader(r.Take("recognition"),"recognition");foreach(var e in Array(p.Take("prefixes"),p.At("prefixes"),4,1))Str(e,p.At("prefixes"),1,32);Boolean(p.Take("lowercase"),p.At("lowercase"));p.End();
        Rules(r.Take("rules"));foreach(var e in Array(r.Take("capabilities"),"capabilities",4))Str(e,"capabilities",1,64);
        index=0;foreach(var e in Array(r.Take("autoAllow"),"autoAllow",32)){p=new Reader(e,$"autoAllow[{index++}]");p.O("server",1,64);p.S("tool",1,64);p.End();}
        p=new Reader(r.Take("presentation"),"presentation");Presentation(p);p.End();
        if(r.Take("job") is {} job){p=new Reader(job,"job");foreach(var key in new[]{"open","close"}){index=0;foreach(var e in Array(p.Take(key),p.At(key),32)){var q=new Reader(e,$"job.{key}[{index++}]");q.S("tool",1,64);var a=q.O("contains",1,400);var b=q.O("notContains",1,400);q.End();if(a!=null&&b!=null)Fail("E_JOB_MATCHER_LITERAL",q.Path);}}foreach(var e in OptionalArray(p.Take("whileOpen"),p.At("whileOpen"),100))Str(e,p.At("whileOpen"),1,64);Guidance(p.O("guidance",0,160),p.At("guidance"));p.End();}
        if(r.Take("stateSources") is {} state){p=new Reader(state,"stateSources");index=0;foreach(var e in OptionalArray(p.Take("files"),p.At("files"),8)){var q=new Reader(e,$"stateSources.files[{index++}]");var path=q.S("path",1,400);Known(q.S("parser",1,40),["markdownChecklist","json"],"E_STATE_PARSER");Known(q.S("widget",1,40),["progressBar","list","label"],"E_STATE_WIDGET");q.End();if(!SafeRelativePath(path))Fail("E_STATE_PATH_ESCAPE",path);}index=0;foreach(var e in OptionalArray(p.Take("runEvents"),p.At("runEvents"),8)){var q=new Reader(e,$"stateSources.runEvents[{index++}]");Known(q.S("event",1,40),["subagent.start","subagent.finish","tool.call"],"E_STATE_RUN_EVENT");Known(q.S("aggregate",1,40),["count","lastValue"],"E_STATE_AGGREGATE");Known(q.S("widget",1,40),["progressBar","list","label"],"E_STATE_WIDGET");q.End();}p.End();}
        r.End();
    }
    private static void Rules(JsonElement? value)
    {
        var r=new Reader(value,"rules");
        foreach(var name in new[]{"start","phase","next","enter","recommend","initialGroup"})
        {
            var p=new Reader(r.Take(name),r.At(name));var kind=p.S("kind",1,32);
            var allowed=name switch{"start"=>new[]{"none","actions"},"phase"=>["none","lastRecognisedAction"],"next"=>["byGroup","byPhase"],"enter"=>["verbatim","rewriteBareDraftTo"],"recommend"=>["none","capability"],_=>["fixed","capabilityState"]};Known(kind,allowed,"E_UNKNOWN_RULE",p.Path);
            switch(kind)
            {
                case "actions":p.S("phase",1,40);foreach(var e in Array(p.Take("actions"),p.At("actions"),100,1))Str(e,p.At("actions"),1,64);p.O("resetTitle",1,24);break;
                case "lastRecognisedAction":p.S("default",1,40);int i=0;foreach(var e in OptionalArray(p.Take("stateOverrides"),p.At("stateOverrides"),16)){var q=new Reader(e,p.At($"stateOverrides[{i++}]"));Integer(q.Take("sourceIndex"),q.At("sourceIndex"));Known(q.S("condition",1,32),["fileExists","allChecked"],"E_UNKNOWN_RULE");q.S("phase",1,40);q.End();}break;
                case "byPhase":Map(p.Take("map"),p.At("map"),true);break;
                case "rewriteBareDraftTo":p.S("action",1,64);p.S("phase",1,40);break;
                case "capability":case "capabilityState":p.S("capability",1,64);Map(p.Take("map"),p.At("map"),false);if(kind=="capability")p.O("group",1,40);break;
                case "fixed":p.S("group",1,40);break;
            }
            p.End();
        }
        r.End();
    }
    private static void Map(JsonElement? value,string path,bool arrays){if(value is null)Fail("E_MISSING_FIELD",path);if(value!.Value.ValueKind!=JsonValueKind.Object)Fail("E_TYPE",path);foreach(var e in value.Value.EnumerateObject()){if(arrays)foreach(var s in Array(e.Value,path+"."+e.Name,100))Str(s,path+"."+e.Name,1,64);else Str(e.Value,path+"."+e.Name,1,64);}}
    private static void Validate(StyleManifest m,string source)
    {
        static string? S(JsonElement e,string k)=>StyleManifest.Text(e,k);
        if(source!="bundled"){if(new[]{"cli","ouroboros","paperthin"}.Contains(m.Id))Fail("E_RESERVED_ID",m.Id);if(new[]{"Ouroboros","Paperthin"}.Any(n=>StyleText.Folded(n)==StyleText.Folded(m.Name)))Fail("E_RESERVED_NAME",m.Name);}
        var capabilities=StyleManifest.Strings(m.Root,"capabilities");foreach(var c in capabilities)Known(c,["paperthin.casebook"],"E_UNKNOWN_CAPABILITY");
        foreach(var key in new[]{"recommend","initialGroup"}){var rule=m.Rules.GetProperty(key);if(S(rule,"capability") is {} c){if(!capabilities.Contains(c))Fail("E_CAPABILITY_UNDECLARED",c);var map=rule.GetProperty("map");foreach(var state in new[]{"absent","open","complete"})if(!map.TryGetProperty(state,out _))Fail("E_CAPABILITY_MAP",state);foreach(var pair in map.EnumerateObject())Known(pair.Name,["absent","open","complete"],"E_UNKNOWN_REFERENCE");}}
        var actions=m.Actions.Select(a=>a.Id).ToHashSet();var phases=m.Phases.Select(p=>p.Id).ToHashSet();var groups=m.Groups.Select(g=>g.Id).ToHashSet();
        void Ref(string? s,HashSet<string> ids,string path){if(s!=null&&!ids.Contains(s))Fail("E_UNKNOWN_REFERENCE",path+": "+s);}
        if(m.Root.TryGetProperty("job",out var job))foreach(var a in StyleManifest.Strings(job,"whileOpen"))Ref(a,actions,"job.whileOpen");
        foreach(var g in m.Groups)foreach(var a in g.Actions)Ref(a,actions,"groups."+g.Id);
        foreach(var a in m.Actions)Ref(a.Phase,phases,"actions."+a.Id+".phase");
        foreach(var a in StyleManifest.Items(m.Root,"aliases")){Ref(S(a,"phase"),phases,"aliases.phase");if(actions.Contains(S(a,"name")!)||m.Actions.Any(x=>x.Match==S(a,"name")))Fail("E_ALIAS_COLLISION",S(a,"name")!);}
        var matches=new HashSet<string>();foreach(var a in m.Actions)if(a.Match is {} match&&((actions.Contains(match)&&a.Id!=match)||!matches.Add(match)))Fail("E_ALIAS_COLLISION",match);
        foreach(var a in m.Actions){var recognised=StyleEvaluator.RecognisedName(m,a.Prompt(""));if(recognised is null||recognised!=a.Id&&recognised!=a.Match)Fail("E_PROMPT_RECOGNITION",a.Id);}
        if(StyleManifest.Bool(m.Root.GetProperty("recognition"),"lowercase"))foreach(var a in StyleManifest.Items(m.Root,"aliases"))if(S(a,"name")!=S(a,"name")!.ToLowerInvariant())Fail("E_PROMPT_RECOGNITION",S(a,"name")!);
        var start=m.Rules.GetProperty("start");if(S(start,"kind")=="actions"){if(!phases.Contains(S(start,"phase")!))Fail("E_START_PHASE",S(start,"phase")!);foreach(var a in StyleManifest.Strings(start,"actions"))Ref(a,actions,"rules.start.actions");}
        var phase=m.Rules.GetProperty("phase");if(S(phase,"kind")=="none"){if(phases.Count>0)Fail("E_PHASE_RULE_NONE");}else{Ref(S(phase,"default"),phases,"rules.phase.default");foreach(var o in StyleManifest.Items(phase,"stateOverrides")){var n=o.GetProperty("sourceIndex").GetDouble();var count=m.Root.TryGetProperty("stateSources",out var state)?StyleManifest.Items(state,"files").Length:0;if(n<0||n>=count)Fail("E_UNKNOWN_REFERENCE","rules.phase.stateOverrides.sourceIndex");Ref(S(o,"phase"),phases,"rules.phase.stateOverrides.phase");}}
        var next=m.Rules.GetProperty("next");if(S(next,"kind")=="byPhase"){var map=next.GetProperty("map");foreach(var p in phases)if(!map.TryGetProperty(p,out _))Fail("E_RULE_INCOMPLETE",p);foreach(var pair in map.EnumerateObject()){Ref(pair.Name,phases,"rules.next.map");foreach(var a in pair.Value.EnumerateArray())Ref(a.GetString(),actions,"rules.next.map");}}
        var enter=m.Rules.GetProperty("enter");if(S(enter,"kind")=="verbatim"){if(m.Root.GetProperty("placeholders").TryGetProperty("initial",out _))Fail("E_PLACEHOLDER_INITIAL");}else{var action=S(enter,"action");Ref(action,actions,"rules.enter.action");if(!m.Actions.First(a=>a.Id==action).TakesText)Fail("E_ENTER_ACTION_TEXT",action!);Ref(S(enter,"phase"),phases,"rules.enter.phase");}
        foreach(var name in new[]{"recommend","initialGroup"}){var rule=m.Rules.GetProperty(name);Ref(S(rule,"group"),groups,"rules."+name+".group");if(rule.TryGetProperty("map",out var map))foreach(var pair in map.EnumerateObject())Ref(pair.Value.GetString(),name=="recommend"?actions:groups,"rules."+name+".map");}
        var plugins=StyleManifest.Items(m.Root.GetProperty("prerequisites"),"probes").Where(p=>S(p,"kind")=="plugin").Select(p=>S(p,"prefix")!).Where(p=>p.EndsWith('@')).Select(p=>p[..^1]).ToArray();var wires=new HashSet<string>();
        foreach(var e in StyleManifest.Items(m.Root,"autoAllow")){var tool=S(e,"tool")!;var server=S(e,"server");if(tool=="AskUserQuestion")Fail("E_AUTOALLOW_QUESTION");if(!Regex.IsMatch(tool,"^[A-Za-z0-9_-]{1,64}$")||tool.Contains("__")||tool.StartsWith('_'))Fail("E_AUTOALLOW_SHAPE",tool);if(server is null){if(tool!="ToolSearch")Fail("E_AUTOALLOW_SERVER",tool);if(source!="bundled")Fail("E_AUTOALLOW_TOOLSEARCH_BUNDLED");}else{if(!Regex.IsMatch(server,"^[A-Za-z0-9_-]{1,64}$")||server.Contains("__")||server.EndsWith('_'))Fail("E_AUTOALLOW_SHAPE",server);if(!plugins.Any(p=>server.StartsWith("plugin_"+p+"_",StringComparison.Ordinal)))Fail("E_AUTOALLOW_FOREIGN_SERVER",server);}var wire=server is null?tool:"mcp__"+server+"__"+tool;if(!wires.Add(wire))Fail("E_AUTOALLOW_DUPLICATE",wire);}
    }
}
