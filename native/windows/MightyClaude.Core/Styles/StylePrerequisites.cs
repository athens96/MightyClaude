using System.Text.Json;
namespace MightyClaude.Core;

public sealed record StylePrerequisiteResult(bool Ready,IReadOnlyList<string> Missing,string? Hint,string? InstallCommand);
public static class StylePrerequisites
{
    public static StylePrerequisiteResult Read(StyleManifest manifest,string workspace,string? home=null)
    {
        home??=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);var p=manifest.Root.GetProperty("prerequisites");var probes=StyleManifest.Items(p,"probes");var missing=new List<JsonElement>();
        foreach(var probe in probes)
        {
            var kind=StyleManifest.Text(probe,"kind");var name=StyleManifest.Text(probe,"name")??StyleManifest.Text(probe,"prefix")!;bool ready=false;
            if(kind=="executable")ready=(Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator).Any(dir=>new[]{"", ".exe", ".cmd", ".bat"}.Any(suffix=>File.Exists(Path.Combine(dir,name+suffix))));
            else if(kind=="skill")ready=StyleManifest.Strings(probe,"scopes").Any(scope=>File.Exists(Path.Combine(scope=="user"?home:workspace,".claude","skills",name,"SKILL.md")));
            else if(kind=="plugin"&&StyleFiles.Read(home,".claude/plugins/installed_plugins.json",4*1024*1024) is {} bytes)
            {
                try{using var doc=JsonDocument.Parse(bytes);ready=doc.RootElement.TryGetProperty("plugins",out var plugins)&&plugins.ValueKind==JsonValueKind.Object&&plugins.EnumerateObject().Any(pair=>pair.Name.StartsWith(name,StringComparison.Ordinal)&&pair.Value.ValueKind==JsonValueKind.Array&&pair.Value.EnumerateArray().Any(item=>StyleManifest.Text(item,"scope")=="user"));}catch(JsonException){}
            }
            if(!ready)missing.Add(probe);
        }
        if(probes.Length==0||StyleManifest.Text(p,"mode")=="all"&&missing.Count==0||StyleManifest.Text(p,"mode")=="any"&&missing.Count<probes.Length)return new(true,[],null,null);
        var shown=StyleManifest.Text(p,"report")=="all"?missing:missing.Take(1);
        return new(false,shown.Select(p=>StyleManifest.Text(p,"missing")!).ToArray(),missing.Select(p=>StyleManifest.Text(p,"hint")).FirstOrDefault(),manifest.Root.TryGetProperty("install",out var install)&&missing.Any(p=>!p.TryGetProperty("install",out var v)||v.ValueKind==JsonValueKind.True)?StyleManifest.Text(install,"command"):null);
    }
}
