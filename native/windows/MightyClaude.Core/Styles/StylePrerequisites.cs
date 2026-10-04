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
    public static (Dictionary<string,string> States,List<(string Title,string Path)> Files) Capabilities(StyleManifest manifest,string workspace)
    {
        workspace=WorkspaceFiles.RealPath(workspace)??workspace;
        var states=new Dictionary<string,string>();var files=new List<(string,string)>();if(!StyleManifest.Strings(manifest.Root,"capabilities").Contains("paperthin.casebook"))return(states,files);
        states["paperthin.casebook"]="absent";var root=WorkspaceFiles.Resolve(".re0/iteration",workspace);if(root is null||!Directory.Exists(root))return(states,files);
        try
        {
            var folders=Directory.EnumerateDirectories(root).Take(1024).Where(p=>(File.GetAttributes(p)&FileAttributes.ReparsePoint)==0).OrderByDescending(Directory.GetLastWriteTimeUtc).Take(24);
            var candidates=folders.Select(folder=>new{Folder=folder,Files=Directory.EnumerateFiles(folder).Take(1024).Where(p=>p.EndsWith(".local.md",StringComparison.Ordinal)&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)==0&&StyleFiles.Read(workspace,Path.GetRelativePath(workspace,p))!=null).Take(24).ToArray()}).Where(c=>c.Files.Length>0).OrderByDescending(c=>c.Files.Max(File.GetLastWriteTimeUtc)).FirstOrDefault();
            if(candidates==null)return(states,files);var names=candidates.Files.Select(Path.GetFileName).ToArray();states["paperthin.casebook"]=names.Contains("DESIGN.local.md")&&names.Contains("RETRO.local.md")?"complete":"open";
            var order=new[]{"DESIGN.local.md","WORKFLOW.local.md","EVIDENCE.local.md","RETRO.local.md"};foreach(var path in candidates.Files.OrderBy(p=>Array.IndexOf(order,Path.GetFileName(p)) is var i&&i>=0?i:4).ThenBy(Path.GetFileName,StringComparer.Ordinal).Take(6))files.Add((StyleText.Safe(Path.GetFileName(path).Replace(".local.md","",StringComparison.Ordinal),80),path));
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){}
        return(states,files);
    }
}
