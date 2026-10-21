using System.Reflection;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

internal static class StylesVerification
{
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static byte[] Fixture(string suffix)
    {
        var assembly=Assembly.GetExecutingAssembly();var name=assembly.GetManifestResourceNames().Single(n=>n.Replace('\\','/').EndsWith(suffix,StringComparison.Ordinal));using var stream=assembly.GetManifestResourceStream(name)!;using var output=new MemoryStream();stream.CopyTo(output);return output.ToArray();
    }
    /// The conformance corpus as the build embedded it from styles/conformance/&lt;folder&gt;: each file's name and bytes.
    private static (string Name,byte[] Bytes)[] Corpus(string folder)
    {
        var assembly=Assembly.GetExecutingAssembly();var prefix="MightyClaude.Core.Tests.Styles."+folder+"/";
        return assembly.GetManifestResourceNames().Select(n=>(Resource:n,Path:n.Replace('\\','/'))).Where(n=>n.Path.StartsWith(prefix,StringComparison.Ordinal)&&n.Path.EndsWith(".json",StringComparison.Ordinal)).OrderBy(n=>n.Path,StringComparer.Ordinal)
            .Select(n=>{using var stream=assembly.GetManifestResourceStream(n.Resource)!;using var output=new MemoryStream();stream.CopyTo(output);return (n.Path[prefix.Length..],output.ToArray());}).ToArray();
    }
    /// Every file of valid/ decodes, every file of invalid/ is refused with the code it is named after, and the corpus
    /// holds a file for each code a file can stand for (macOS StylesThirdPartyConformanceTests).
    internal static Task Conformance()
    {
        var failures=new List<string>();var valid=Corpus("valid");var invalid=Corpus("invalid");
        Check(valid.Length>0&&invalid.Length>0,$"styles/conformance must be embedded: valid {valid.Length}, invalid {invalid.Length}");
        foreach(var (name,bytes) in valid)
        {
            try{if(StyleManifestDecoder.Decode(bytes).Root.GetProperty("schema").GetInt32()!=1)failures.Add(name+" is not schema 1");}
            catch(StyleManifestException e){failures.Add(name+" rejected "+e.Code);}
        }
        foreach(var (name,bytes) in invalid)
        {
            var expected=name[..^5];
            try{StyleManifestDecoder.Decode(bytes);failures.Add(expected+" was accepted");}
            catch(StyleManifestException e){if(e.Code!=expected)failures.Add(expected+" got "+e.Code);}
        }
        Check(failures.Count==0,string.Join("; ",failures));
        // The size bound and the registry's collision are codes no file in the corpus can stand for, and since v7 the app's empty
        // feature vocabulary refuses a declared feature before its map is read, so no file can stand for E_CAPABILITY_MAP either
        // (BuiltInFeatures checks it with a test vocabulary).
        var codes=invalid.Select(file=>file.Name[..^5]).ToHashSet(StringComparer.Ordinal);var reachable=StyleManifestDecoder.Codes.Except(["E_ID_COLLISION","E_TOO_LARGE","E_CAPABILITY_MAP"]).ToHashSet(StringComparer.Ordinal);
        var missing=reachable.Except(codes).Order(StringComparer.Ordinal).ToArray();var unknown=codes.Except(reachable).Order(StringComparer.Ordinal).ToArray();
        Check(missing.Length==0,"codes with no file in the corpus: "+string.Join(", ",missing));Check(unknown.Length==0,"corpus files named after no code, or after one no file can produce: "+string.Join(", ",unknown));
        Check(invalid.Length==51&&StyleManifestDecoder.Codes.Count==54,$"51 of the 54 codes have a file: {invalid.Length} files, {StyleManifestDecoder.Codes.Count} codes");
        try{StyleManifestDecoder.Decode(new byte[StyleManifestDecoder.MaximumBytes+1]);throw new InvalidOperationException("oversize accepted");}catch(StyleManifestException e){Check(e.Code=="E_TOO_LARGE","size precedes JSON");}
        var minimal=Encoding.UTF8.GetString(Fixture("valid/minimal.json"));
        foreach(var glyph in new[]{"☕","👩‍💻","⚙️","🚀"})StyleManifestDecoder.Decode(Encoding.UTF8.GetBytes(minimal.Replace("\"takesText\":false","\"takesText\":false,\"glyph\":"+JsonSerializer.Serialize(glyph),StringComparison.Ordinal)));
        return Task.CompletedTask;
    }
    /// Built-in features (§1.8, macOS StyleCapabilityTests): the app knows none since v7, so every declared name is refused as
    /// unknown; the field and the two rules that name a feature stay in the schema and are checked here against a test
    /// vocabulary, in the Mac validator's order — unknown name, then undeclared, then the map.
    /// A non-bundled style may not take a bundled style's name, folded (NFKC, case, spaces), and the reserved names are exactly
    /// the bundled styles' names (macOS StyleSecurityTests.reservedNameIsRefused).
    internal static Task ReservedNames()
    {
        var minimal=Encoding.UTF8.GetString(Fixture("valid/minimal.json"));
        string? Code(string name,string source="user"){try{StyleManifestDecoder.Decode(Encoding.UTF8.GetBytes(minimal.Replace("\"name\":\"코퍼스 최소\"","\"name\":"+JsonSerializer.Serialize(name),StringComparison.Ordinal)),source);return null;}catch(StyleManifestException e){return e.Code;}}
        Check(Code("코퍼스 최소")==null,"the fixture decodes as it is");
        Check(StyleManifestDecoder.ReservedNames.SequenceEqual(["Ouroboros","클러드 플랜"]),"the reserved names: "+string.Join(", ",StyleManifestDecoder.ReservedNames));
        var root=Verification.Temp();
        try
        {
            var bundled=StyleRegistry.Load(Path.Combine(root,"profile"),Path.Combine(root,"workspace")).Styles.Where(s=>s.Source=="bundled").Select(s=>s.Manifest.Name).Order(StringComparer.Ordinal);
            Check(bundled.SequenceEqual(StyleManifestDecoder.ReservedNames.Order(StringComparer.Ordinal)),"the reserved names are the bundled styles' names: "+string.Join(", ",bundled));
        }
        finally{Directory.Delete(root,true);}
        foreach(var name in new[]{"Ouroboros","ouroboros","OURO BOROS","ＯＵＲＯＢＯＲＯＳ","클러드 플랜","클러드플랜","클러드　플랜"})
        {
            Check(Code(name)=="E_RESERVED_NAME",name+" passed the reserved-name check: "+Code(name));
            Check(Code(name,"bundled")==null,name+" is refused for a bundled style: "+Code(name,"bundled"));
        }
        Check(Code("Ouroboros Team")==null&&Code("클러드 플랜 2")==null,"a name that only contains a reserved one is free");
        return Task.CompletedTask;
    }
    internal static Task BuiltInFeatures()
    {
        var minimal=Encoding.UTF8.GetString(Fixture("valid/minimal.json"));
        string? Code(string text,string source="user",IReadOnlyDictionary<string,string[]>? vocabulary=null){try{StyleManifestDecoder.Decode(Encoding.UTF8.GetBytes(text),source,vocabulary);return null;}catch(StyleManifestException e){return e.Code;}}
        string With(string? capabilities=null,string? recommend=null,string? initial=null)
        {
            var text=minimal;
            if(capabilities is not null)text=text.Replace("\"capabilities\":[]","\"capabilities\":"+capabilities,StringComparison.Ordinal);
            if(recommend is not null)text=text.Replace("\"recommend\":{\"kind\":\"none\"}","\"recommend\":"+recommend,StringComparison.Ordinal);
            if(initial is not null)text=text.Replace("\"initialGroup\":{\"kind\":\"fixed\",\"group\":\"g\"}","\"initialGroup\":"+initial,StringComparison.Ordinal);
            Check(text!=minimal,"the fixture changed");return text;
        }
        var test=StyleFixtures.TestCapabilities;
        Check(StyleCapabilityID.Vocabulary.Count==0,"the app knows no built-in feature");
        foreach(var name in new[]{"acme.thing","a"})Check(Code(With("[\""+name+"\"]"))=="E_UNKNOWN_CAPABILITY"&&Code(With("[\""+name+"\"]"),"bundled")=="E_UNKNOWN_CAPABILITY",name+" is refused as unknown, for a bundled manifest too");
        Check(Code(minimal) is null,"declaring nothing is still the ordinary case");
        var root=Verification.Temp();
        try{foreach(var style in StyleRegistry.Load(Path.Combine(root,"profile"),Path.Combine(root,"workspace")).Styles.Where(s=>s.Source=="bundled"))Check(StyleManifest.Strings(style.Manifest.Root,"capabilities").Length==0,style.Id+" declares no feature");}
        finally{Directory.Delete(root,true);}
        const string recommend="{\"kind\":\"capability\",\"capability\":\"acme.thing\",\"map\":{\"absent\":\"go\",\"open\":\"go\",\"complete\":\"go\"}}";
        const string initial="{\"kind\":\"capabilityState\",\"capability\":\"acme.thing\",\"map\":{\"absent\":\"g\",\"open\":\"g\",\"complete\":\"g\"}}";
        foreach(var (rule,group) in new (string?,string?)[]{(recommend,null),(null,initial)})
        {
            var undeclared=With(recommend:rule,initial:group);var declared=With("[\"acme.thing\"]",rule,group);
            Check(Code(undeclared)=="E_CAPABILITY_UNDECLARED"&&Code(undeclared,vocabulary:test)=="E_CAPABILITY_UNDECLARED","a rule naming an undeclared feature is refused for that, whatever the vocabulary");
            Check(Code(declared)=="E_UNKNOWN_CAPABILITY"&&Code(declared,vocabulary:test) is null,"a declared feature is read against the vocabulary first: the app refuses it, a vocabulary that knows it accepts the whole map");
        }
        // A map must cover every state and name nothing else; only a test vocabulary still reaches that check.
        var partial=With("[\"acme.thing\"]","{\"kind\":\"capability\",\"capability\":\"acme.thing\",\"map\":{\"absent\":\"go\",\"open\":\"go\"}}");
        var extra=With("[\"acme.thing\"]","{\"kind\":\"capability\",\"capability\":\"acme.thing\",\"map\":{\"absent\":\"go\",\"open\":\"go\",\"complete\":\"go\",\"gone\":\"go\"}}");
        var noMap=With("[\"acme.thing\"]","{\"kind\":\"capability\",\"capability\":\"acme.thing\"}");
        Check(Code(partial,vocabulary:test)=="E_CAPABILITY_MAP"&&Code(partial)=="E_UNKNOWN_CAPABILITY","a partial map is E_CAPABILITY_MAP under a vocabulary that knows the feature, and unknown under the app's");
        Check(Code(extra,vocabulary:test)=="E_UNKNOWN_REFERENCE"&&Code(noMap,vocabulary:test)=="E_MISSING_FIELD","a state the feature lacks is an unknown reference, and a rule without its map is missing a field");
        // The evaluator reads a feature's state as it always did, so a feature added later recommends and seeds a group without engine work.
        var catalogue=StyleFixtures.CatalogueStyle;var evaluator=catalogue.Evaluator;
        Check(evaluator.RecommendedAction(new Dictionary<string,string>{["acme.thing"]="complete"})=="work"&&evaluator.RecommendedAction(new Dictionary<string,string>()) is null,"the recommendation follows the feature's state, and none before it is read");
        Check(evaluator.InitialGroup(new Dictionary<string,string>{["acme.thing"]="open"})?.Id=="coil"&&evaluator.InitialGroup(new Dictionary<string,string>())?.Id=="depth","the first group follows the feature's state; not read yet, the first group");
        // The phone has no group of its own and production passes none, so the recommendation has to survive that (§7.4).
        var panel=StylePanelProjection.Make(catalogue,[],null,new Dictionary<string,string>{["acme.thing"]="complete"},new(true,[],null,null));
        Check(panel.Recommended=="work"&&panel.Groups.Single(g=>g.Selected).Id=="coil","the panel carries the recommendation and the group it seeds");
        // Nothing offers a chip any more, but the field is still sent, empty, for a phone that predates v7.
        using var wire=JsonDocument.Parse(StylePanelProjection.Serialise(panel));
        Check(panel.Attachments.Count==0&&wire.RootElement.GetProperty("attachments").GetArrayLength()==0,"attachments travel, always empty");
        return Task.CompletedTask;
    }
    internal static Task EvaluationAndTrust()
    {
        var root=Verification.Temp();var workspace=Path.Combine(root,"workspace");var profile=Path.Combine(root,"profile");Directory.CreateDirectory(workspace);Directory.CreateDirectory(Path.Combine(profile,"styles"));
        try
        {
            var registry=StyleRegistry.Load(profile,workspace);Check(registry.Styles.Count==2&&registry.Styles.All(s=>s.Runnable),"both bundled styles loaded: "+string.Join(";",registry.Rejections.Select(e=>e.Message)));
            var source=Fixture("valid/with-state-sources.json");var file=Path.Combine(profile,"styles","custom.json");File.WriteAllBytes(file,source);
            registry=StyleRegistry.Load(profile,workspace);var style=registry.Styles.Single(s=>s.Id=="corpus-state");Check(!style.Runnable,"unapproved doesn't run");var trust=new StyleTrustStore(Path.Combine(profile,"style-trust"));trust.Decide(style,"approved");registry=StyleRegistry.Load(profile,workspace);Check(registry.Runnable(style.Id,style.Hash)!=null,"approval binds bytes and place");Check(registry.Runnable(style.Id,"wrong")==null,"pane hash also gates");
            File.WriteAllText(file,Encoding.UTF8.GetString(source)+" ");Check(!StyleRegistry.Unchanged(style),"changed file invalidates preview before approve/run");registry=StyleRegistry.Load(profile,workspace);var changed=registry.Styles.Single(s=>s.Id==style.Id);Check(!changed.Runnable,"one changed byte asks again");trust.Decide(changed,"revoked");File.WriteAllBytes(file,source);registry=StyleRegistry.Load(profile,workspace);Check(registry.Styles.Single(s=>s.Id==style.Id).Approval=="revoked","revocation survives old bytes returning");trust.Decide(style,"unblock");Check(StyleRegistry.Load(profile,workspace).Runnable(style.Id,style.Hash)==null,"unblock requires fresh approval");
            var evaluator=style.Evaluator;Check(evaluator.CurrentPhase([])?.Id=="draft","initial phase");Check(evaluator.CurrentPhase(["/go"],new Dictionary<int,StyleFileState>{{0,new(true,false)}})?.Id=="build","state advances phase");Check(evaluator.CurrentPhase(["/go"],new Dictionary<int,StyleFileState>{{0,new(true,true)}})?.Id=="done","completed checklist advances phase");Check(!evaluator.AutoAllowed("AskUserQuestion"),"question never auto allowed");
            var ouro=registry.Styles.Single(s=>s.Id=="ouroboros").Evaluator;var initial=ouro.CurrentPhase([]);var rewritten=ouro.RewriteAction("한글 요청",initial,false,false,false);Check(rewritten!=null,"first bare draft rewrites");Check(ouro.RewriteAction("한글 요청",initial,false,false,true)==null,"later request cannot rewrite");Check(ouro.RewriteAction("/help",initial,false,false,false)==null,"slash never rewrite");
            var pane=new RunSession{Id="style-pane",WorkspaceId="style-workspace",Provider="claude",AgentViewMode="mighty",MightyStyle="ouroboros"};
            var request=new StartRunRequest(pane.Id,pane.WorkspaceId,"claude","test",[]);
            var bound=StyleRunPermissions.Bind(request,pane,profile,workspace);
            Check(bound.StyleAutoAllow is {Count:>0}&&bound.StyleAutoAllow.All(StyleRunPermissions.ValidWireName),"approved local style grants exact tools");
            Check(ProviderCatalog.Arguments(bound,"/plugin").Contains("--allowedTools"),"grants reach Claude argv");
            var wire=JsonSerializer.Serialize(bound,Wire.Json);Check(!wire.Contains("styleAutoAllow",StringComparison.Ordinal),"tool grants never serialize");
            var injected=JsonSerializer.Deserialize<StartRunRequest>(wire[..^1]+",\"styleAutoAllow\":[\"Bash\"]}",Wire.Json)!;Check(injected.StyleAutoAllow is null,"remote JSON cannot set style grants");
            foreach(var invalid in new[]{"Bash","mcp__plugin_x__*","mcp__plugin_x__AskUserQuestion","mcp__plugin_x__evil__tool"})Check(!StyleRunPermissions.ValidWireName(invalid),"unsafe grant rejected "+invalid);
            var oneLine=new StyleAction("x","x","","/x {text}",true,false,"oneLine",null,null,null,null,null,null);Check(oneLine.Prompt("  alpha  beta\n gamma ")=="/x alpha  beta gamma","oneLine preserves inside spacing");Check(oneLine.Prompt("")=="/x","empty text removes adjacent spaces");
        }
        finally{Directory.Delete(root,true);}return Task.CompletedTask;
    }
    /// A user style whose one tool is its own: the minimal corpus manifest with a plugin probe and an auto-allow entry.
    private static byte[] AutoAllowFixture()=>Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Fixture("valid/minimal.json"))
        .Replace("\"probes\":[]","\"probes\":[{\"kind\":\"plugin\",\"prefix\":\"demo@\",\"missing\":\"demo\"}]",StringComparison.Ordinal).Replace("\"autoAllow\":[]","\"autoAllow\":[{\"server\":\"plugin_demo_srv\",\"tool\":\"go\"}]",StringComparison.Ordinal));
    internal const string AutoAllowTool="mcp__plugin_demo_srv__go";
    /// Removing a user style forgets what was decided for its place (M/AppStore+Styles.swift:609-613): the same id registered
    /// again is asked about afresh. Before, removal left a refusal behind and the new copy came up blocked.
    internal static Task RemovalForgetsTheDecision()
    {
        var root=Verification.Temp();var workspace=Path.Combine(root,"workspace");var profile=Path.Combine(root,"profile");Directory.CreateDirectory(workspace);Directory.CreateDirectory(Path.Combine(profile,"styles"));
        try
        {
            var source=Fixture("valid/with-state-sources.json");var file=Path.Combine(profile,"styles","corpus-state.json");var trust=new StyleTrustStore(Path.Combine(profile,"style-trust"));
            RegisteredStyle Registered()=>StyleRegistry.Load(profile,workspace).Styles.Single(s=>s.Id=="corpus-state");
            File.WriteAllBytes(file,source);var style=Registered();trust.Decide(style,"approved");Check(Registered().Runnable,"the registered style is approved");
            // What removal used to record: a refusal for the place, then the file gone. The same file copied back met it.
            trust.Decide(style,"revoked");File.Delete(file);File.WriteAllBytes(file,source);
            Check(Registered().Approval=="revoked","a refusal left behind meets the same id registered again");
            try{trust.Decide(Registered(),"approved");throw new InvalidOperationException("a refused place was approved");}catch(IOException){}
            // Removal now: the decision is forgotten with the file.
            trust.Forget(Registered());File.Delete(file);Check(trust.Load().Count==0,"nothing is kept for a removed style");
            File.WriteAllBytes(file,source);Check(Registered().Approval=="pending","registered again, it is asked about afresh");
            trust.Decide(Registered(),"approved");Check(StyleRegistry.Load(profile,workspace).Runnable("corpus-state",style.Hash)!=null,"and can be approved");
            // An approval is forgotten too: the same bytes do not come back approved.
            trust.Forget(Registered());File.Delete(file);File.WriteAllBytes(file,source);Check(Registered().Approval=="pending","an approval does not outlive its file's removal");
            // Another style's decision is left alone.
            var other=Path.Combine(profile,"styles","corpus-min.json");File.WriteAllBytes(other,Fixture("valid/minimal.json"));trust.Decide(StyleRegistry.Load(profile,workspace).Styles.Single(s=>s.Id=="corpus-min"),"approved");
            trust.Forget(Registered());Check(StyleRegistry.Load(profile,workspace).Styles.Single(s=>s.Id=="corpus-min").Runnable&&trust.Load().Count==1,"only the removed style's decision goes");
        }
        finally{Directory.Delete(root,true);}return Task.CompletedTask;
    }
    /// A style's own tools are allowed request by request, against the style as it stands right then
    /// (M/AppStore+Styles.swift:539-541): revoking it, changing its bytes or leaving it stops that at once.
    internal static Task AutoAllowIsJudgedAtEachRequest()
    {
        var root=Verification.Temp();var workspace=Path.Combine(root,"workspace");var profile=Path.Combine(root,"profile");Directory.CreateDirectory(workspace);Directory.CreateDirectory(Path.Combine(profile,"styles"));
        try
        {
            var bytes=AutoAllowFixture();var file=Path.Combine(profile,"styles","corpus-min.json");File.WriteAllBytes(file,bytes);var trust=new StyleTrustStore(Path.Combine(profile,"style-trust"));
            var style=StyleRegistry.Load(profile,workspace).Styles.Single(s=>s.Id=="corpus-min");
            var pane=new RunSession{Id="style-pane",WorkspaceId="style-workspace",Provider="claude",AgentViewMode="mighty",MightyStyle=style.Id,MightyStyleHash=style.Hash};
            var request=new ToolPermissionRequest("perm-1",pane.Id,"toolu_1",AutoAllowTool,"{}","go");
            bool Allows(ToolPermissionRequest? asked=null,RunSession? from=null)=>StyleRunPermissions.AutoAllows(asked??request,from??pane,profile,workspace);
            Check(!Allows(),"a style that was not approved allows nothing");
            trust.Decide(style,"approved");Check(Allows(),"the approved style allows its own tool");
            Check(!Allows(request with{ToolName="mcp__plugin_demo_srv__other"})&&!Allows(request with{ToolName="Bash"})&&!Allows(request with{ToolName="AskUserQuestion"})&&!Allows(request with{ToolName="mcp__plugin_demo_srv__AskUserQuestion"}),"only the exact listed tool, never a question");
            Check(!Allows(request with{CanAllow=false})&&!Allows(request with{State="allowed"}),"only a pending request that can be allowed");
            Check(!Allows(from:pane with{AgentViewMode="default"})&&!Allows(from:pane with{MightyStyle=null})&&!Allows(from:pane with{MightyStyle="ouroboros",MightyStyleHash=null})&&!Allows(from:pane with{MightyStyleHash="other"})&&!Allows(from:pane with{Provider="codex"})&&!StyleRunPermissions.AutoAllows(request,null,profile,workspace),"only for the pane's own style, as the pane chose it");
            // The same run, the next request: a changed file, then a revocation, each stops it at once; allowing again starts it again.
            File.WriteAllText(file,Encoding.UTF8.GetString(bytes)+" ");Check(!Allows(),"changed bytes stop it");
            File.WriteAllBytes(file,bytes);Check(Allows(),"the approved bytes back, it allows again");
            trust.Decide(style,"revoked");Check(!Allows(),"a revocation stops it at once");
            trust.Decide(style,"unblock");Check(!Allows(),"unblocking alone does not allow");trust.Decide(style,"approved");Check(Allows(),"a fresh approval does");
            File.Delete(file);Check(!Allows(),"a removed style allows nothing");
            // Launch: a host that shows the card judges each request and so launches without the list; one that cannot still gets it.
            File.WriteAllBytes(file,bytes);
            var bound=StyleRunPermissions.Bind(new StartRunRequest(pane.Id,pane.WorkspaceId,"claude","test",[]),pane,profile,workspace);
            var listed=ProviderCatalog.Arguments(bound,"/plugin");Check(listed[listed.IndexOf("--allowedTools")+1]==AutoAllowTool,"a host without the card launches with the list");
            var hosted=ProviderInput.Prepare(bound,"/plugin",allowPermissionPrompts:true).Arguments;Check(!hosted.Contains("--allowedTools")&&!hosted.Contains(AutoAllowTool)&&hosted[hosted.IndexOf("--permission-prompts")+1]=="host","a host with the card launches without it");
            Check(ProviderInput.Prepare(bound,"/plugin",allowPermissionPrompts:false).Arguments.Contains("--allowedTools"),"prompts off keeps the list");
        }
        finally{Directory.Delete(root,true);}return Task.CompletedTask;
    }
    /// End to end over a real child process, through the service the panes run on: the approved style's tool is allowed
    /// without ever reaching a pane, and once the style is revoked the very next request of the same kind waits for the user.
    internal static async Task ARunAllowsItsStyleToolsUntilTheStyleIsRevoked()
    {
        var directory=Verification.Temp();
        try
        {
            var profile=Path.Combine(directory,"profile");var workspacePath=Path.Combine(directory,"workspace");Directory.CreateDirectory(workspacePath);Directory.CreateDirectory(Path.Combine(profile,"styles"));
            var plugin=Path.Combine(directory,"plugin");Directory.CreateDirectory(Path.Combine(plugin,".claude-plugin"));await File.WriteAllTextAsync(Path.Combine(plugin,".claude-plugin","plugin.json"),"{}");
            File.WriteAllBytes(Path.Combine(profile,"styles","corpus-min.json"),AutoAllowFixture());var trust=new StyleTrustStore(Path.Combine(profile,"style-trust"));
            var style=StyleRegistry.Load(profile,workspacePath).Styles.Single(s=>s.Id=="corpus-min");trust.Decide(style,"approved");
            var record=Path.Combine(directory,"styled");var seen=new List<ToolPermissionRequest>();
            await using var catalog=new ProviderCatalog((_,_)=>Task.FromResult<CliCommand?>(Verification.Self("--fake-cli","claude",record)));
            await using var service=new DesktopService(profile,null,plugin,catalog);await service.InitializeAsync();
            var workspace=await service.AddWorkspaceAsync(workspacePath);
            var pane=new RunSession{WorkspaceId=workspace.Id,Provider="claude",AgentViewMode="mighty",MightyStyle=style.Id,MightyStyleHash=style.Hash};await service.UpdateAsync(s=>s with{Sessions=[pane]});
            service.ToolPermissionChanged+=value=>{lock(seen)seen.Add(value);if(value.State=="pending")service.RespondToToolPermission(pane.Id,value.Id,false);};
            async Task<JsonElement> Run()
            {
                File.Delete(record+".decision");
                await service.StartAsync(new(pane.Id,workspace.Id,"claude","style fixture",[]));await Verification.Until(()=>!service.IsSessionRunning(pane.Id),20000);
                return JsonDocument.Parse(await File.ReadAllTextAsync(record+".decision")).RootElement.GetProperty("response").GetProperty("response");
            }
            var allowed=await Run();
            var launched=JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(record+".args"),Wire.Json)!;
            Check(launched[Array.IndexOf(launched,"--permission-prompts")+1]=="host"&&!launched.Contains("--allowedTools"),"the run asks this host about each tool and lists none at launch");
            Check(allowed.GetProperty("behavior").GetString()=="allow"&&allowed.GetProperty("toolUseID").GetString()=="tool-style-1"&&allowed.GetProperty("updatedInput").GetProperty("step").GetInt32()==1,"the style's own tool is allowed once, with its input as asked");
            Check(!allowed.TryGetProperty("updatedPermissions",out _),"no rule or settings update is ever returned");
            lock(seen)Check(seen.Count==0,"a request the style allows never reaches the pane: "+string.Join(",",seen.Select(r=>r.State)));
            // Revoked between two requests: the next one is the user's to answer.
            trust.Decide(style,"revoked");
            var denied=await Run();
            Check(denied.GetProperty("behavior").GetString()=="deny","after the revocation the same tool is not allowed by the style");
            lock(seen)Check(seen.Count==2&&seen[0] is {State:"pending",ToolName:AutoAllowTool}&&seen[1].State=="denied","the request waited for the user and settled as answered: "+string.Join(",",seen.Select(r=>r.State)));
        }
        finally{try{Directory.Delete(directory,true);}catch(IOException){}}
    }
    /// Which changes under a workspace make a pane read its style again (M/Styles/StyleStateEngine.swift affects).
    internal static Task ChangesThatConcernAStyle()
    {
        // The corpus manifest reads docs/plans/**/*.md and two files at the workspace's root; without those two only the folder counts.
        var whole=StyleManifestDecoder.Decode(Fixture("valid/with-state-sources.json"));var plain=StyleManifestDecoder.Decode(Fixture("valid/minimal.json"));const string rootSources=",{\"path\":\"status.json\",\"parser\":\"json\",\"widget\":\"list\"},{\"path\":\"NOTES.md\",\"parser\":\"markdownChecklist\",\"widget\":\"label\"}";
        var text=Encoding.UTF8.GetString(Fixture("valid/with-state-sources.json"));Check(text.Contains(rootSources,StringComparison.Ordinal),"the fixture's root-level file sources");
        var manifest=StyleManifestDecoder.Decode(Encoding.UTF8.GetBytes(text.Replace(rootSources,"",StringComparison.Ordinal)));
        var workspace=Path.Combine(Path.GetTempPath(),"mighty-watch-workspace");string At(params string[] parts)=>parts.Aggregate(workspace,Path.Combine);
        Check(StyleStateEngine.Affects(whole,workspace,At("src","a.cs"))&&StyleStateEngine.Affects(whole,workspace,At("NOTES.md")),"a source at the workspace's root is moved by any change under it");
        Check(StyleStateEngine.Affects(manifest,workspace,At("docs","plans","plan.md"))&&StyleStateEngine.Affects(manifest,workspace,At("docs","plans"))&&StyleStateEngine.Affects(manifest,workspace,At("docs","plans","deep","x.md")),"a change in the source's folder");
        Check(StyleStateEngine.Affects(manifest,workspace,At("docs")),"a parent of the folder appearing");
        Check(!StyleStateEngine.Affects(manifest,workspace,At("docs","plans2","plan.md"))&&!StyleStateEngine.Affects(manifest,workspace,At("src","a.cs"))&&!StyleStateEngine.Affects(plain,workspace,At("docs","plans","plan.md")),"nothing else, and nothing for a style without file sources");
        Check(StyleChangeWatcher.Concerns([],workspace,null)&&StyleChangeWatcher.Concerns([],workspace,manifest),"a lost watch reads everything again");
        Check(StyleChangeWatcher.Concerns([At(".claude","mighty-styles","x.json")],workspace,null)&&StyleChangeWatcher.Concerns([At(".claude")],workspace,null),"the workspace's own styles, for every pane");
        Check(!StyleChangeWatcher.Concerns([At("src","a.cs"),At(".claude","settings.json")],workspace,manifest)&&!StyleChangeWatcher.Concerns([At("docs","plans","plan.md")],workspace,null),"other files concern no one");
        Check(StyleChangeWatcher.Concerns([At("src","a.cs"),At("docs","plans","plan.md")],workspace,manifest),"one file of a source among others");
        // No built-in feature reads a folder since v7: a style that names one is moved by nothing beyond its own sources.
        Check(!StyleChangeWatcher.Concerns([At(".re0","iteration","0.1.0","DESIGN.local.md")],workspace,StyleFixtures.CatalogueStyle.Manifest)&&!StyleChangeWatcher.Concerns([At(".re0","iteration","0.1.0","DESIGN.local.md")],workspace,manifest),"a folder no source names concerns no style");
        return Task.CompletedTask;
    }
    /// The watcher itself: a folder that is not there yet, a file written in it, the folder taken away and put back, and silence once disposed.
    internal static async Task ChangeWatcherReportsFileChanges()
    {
        var root=Verification.Temp();var folder=Path.Combine(root,"profile","styles");var file=Path.Combine(folder,"a.json");var gate=new object();var reports=new List<IReadOnlyList<string>>();
        int Count(){lock(gate)return reports.Count;}
        bool Saw(string path,int from){lock(gate)return reports.Skip(from).Any(r=>r.Any(p=>string.Equals(Path.GetFullPath(p),Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase)));}
        try
        {
            var watcher=new StyleChangeWatcher(folder,paths=>{lock(gate)reports.Add(paths);},TimeSpan.FromMilliseconds(40));
            try
            {
                using(var standard=new StyleChangeWatcher(root,_=>{}))Check(watcher.Latency==TimeSpan.FromMilliseconds(40)&&standard.Latency==TimeSpan.FromMilliseconds(500),"changes are gathered for half a second unless told otherwise");
                await Task.Delay(150);Check(Count()==0,"nothing is reported while nothing changes");
                Directory.CreateDirectory(folder);await Verification.Until(()=>Saw(folder,0));
                var before=Count();File.WriteAllText(file,"{}");await Verification.Until(()=>Saw(file,before));
                before=Count();File.WriteAllText(file,"{ }");await Verification.Until(()=>Saw(file,before));
                // Many changes at once arrive together, not one report each.
                before=Count();for(var i=0;i<40;i++)File.WriteAllText(Path.Combine(folder,"burst-"+i+".json"),"{}");await Verification.Until(()=>Saw(Path.Combine(folder,"burst-39.json"),before));
                await Task.Delay(120);Check(Count()-before<20,"a burst of forty files is gathered into few reports, got "+(Count()-before));
                // The folder taken away and put back: the watch is set up again and what is written afterwards is still seen.
                before=Count();Directory.Delete(folder,true);await Verification.Until(()=>Count()>before);
                await Verification.Until(()=>{try{Directory.CreateDirectory(folder);return true;}catch(Exception e)when(e is IOException or UnauthorizedAccessException){return false;}});
                before=Count();await Verification.Until(()=>{File.WriteAllText(file,Count().ToString());return Saw(file,before);});
            }
            finally{watcher.Dispose();}
            await Task.Delay(120);var after=Count();File.WriteAllText(file,"late");await Task.Delay(200);Check(Count()==after,"a disposed watcher reports nothing");
            watcher.Dispose();
        }
        finally{Directory.Delete(root,true);}
    }
    internal static Task StateBoundaries()
    {
        var root=Verification.Temp();var outside=Verification.Temp();
        try
        {
            var manifest=StyleManifestDecoder.Decode(Fixture("valid/with-state-sources.json"));Directory.CreateDirectory(Path.Combine(root,"docs","plans"));var file=Path.Combine(root,"docs","plans","plan.md");File.WriteAllText(file,"- [x] done\n```\n- [ ] example\n```\n- [ ] remaining");File.WriteAllText(Path.Combine(outside,"private.md"),"private");
            Check(StyleStateEngine.Read(manifest,root,null).Files[0].Exists==false,"old plan cannot advance new pane");var reading=StyleStateEngine.Read(manifest,root,DateTimeOffset.UtcNow.AddMinutes(-1));Check(reading.Files[0].Exists&&!reading.Files[0].AllChecked&&reading.Widgets[0].Value==1&&reading.Widgets[0].Total==2,"real bounded checklist read");
            File.CreateSymbolicLink(Path.Combine(root,"escape.md"),Path.Combine(outside,"private.md"));Check(StyleFiles.Read(root,"escape.md")==null,"symlink outside root refused");foreach(var path in new[]{"../private.md","C:\\private.md","\\\\host\\share","/private.md","..\\private.md","file:stream"})Check(!StyleManifestDecoder.SafeRelativePath(path),"Windows escape refused "+path);
            File.WriteAllBytes(Path.Combine(root,"large.md"),new byte[StyleManifestDecoder.MaximumBytes+1]);Check(StyleFiles.Read(root,"large.md")==null,"oversize state refused");
            var json=StyleStateEngine.Parse("{\"value\":10,\"total\":4}","json","progressBar");Check(json.Widget.Value==4&&!json.State.AllChecked,"json clamps and never means allChecked");
        }
        finally{Directory.Delete(root,true);Directory.Delete(outside,true);}return Task.CompletedTask;
    }
}
