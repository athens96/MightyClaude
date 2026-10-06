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
    internal static Task Conformance()
    {
        var assembly=Assembly.GetExecutingAssembly();var failures=new List<string>();int count=0;
        foreach(var name in assembly.GetManifestResourceNames().Where(n=>n.Contains("Styles.invalid",StringComparison.Ordinal)))
        {
            using var stream=assembly.GetManifestResourceStream(name)!;using var output=new MemoryStream();stream.CopyTo(output);
            var expected=name[(name.IndexOf("E_",StringComparison.Ordinal))..^5];
            try{StyleManifestDecoder.Decode(output.ToArray());failures.Add(expected+" was accepted");}
            catch(StyleManifestException e){if(e.Code!=expected)failures.Add(expected+" got "+e.Code);}
            count++;
        }
        foreach(var suffix in new[]{"valid/minimal.json","valid/with-phases.json","valid/with-state-sources.json","valid/with-job-declaration.json","valid/with-plan-state.json","Styles.gstack.json","Styles.oh-my-claudecode.json"})
        {
            try{StyleManifestDecoder.Decode(Fixture(suffix));}catch(StyleManifestException e){failures.Add(suffix+" rejected "+e.Code);}
        }
        Check(count==52,"Expected all 52 committed invalid manifests, got "+count);Check(failures.Count==0,string.Join("; ",failures));
        try{StyleManifestDecoder.Decode(new byte[StyleManifestDecoder.MaximumBytes+1]);throw new InvalidOperationException("oversize accepted");}catch(StyleManifestException e){Check(e.Code=="E_TOO_LARGE","size precedes JSON");}
        var minimal=Encoding.UTF8.GetString(Fixture("valid/minimal.json"));
        foreach(var glyph in new[]{"☕","👩‍💻","⚙️","🚀"})StyleManifestDecoder.Decode(Encoding.UTF8.GetBytes(minimal.Replace("\"takesText\":false","\"takesText\":false,\"glyph\":"+JsonSerializer.Serialize(glyph),StringComparison.Ordinal)));
        return Task.CompletedTask;
    }
    internal static Task EvaluationAndTrust()
    {
        var root=Verification.Temp();var workspace=Path.Combine(root,"workspace");var profile=Path.Combine(root,"profile");Directory.CreateDirectory(workspace);Directory.CreateDirectory(Path.Combine(profile,"styles"));
        try
        {
            var registry=StyleRegistry.Load(profile,workspace);Check(registry.Styles.Count==4&&registry.Styles.All(s=>s.Runnable),"all four bundled styles loaded: "+string.Join(";",registry.Rejections.Select(e=>e.Message)));
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
