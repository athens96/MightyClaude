using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace MightyClaude.Core;

/// Read through the validated file handle, never reopen a checked path. Network
/// paths, junction escapes, hard links and oversized/growing files are refused.
public static class StyleFiles
{
    public static byte[]? Read(string root,string relative,int maximum=StyleManifestDecoder.MaximumBytes)
    {
        if(!StyleManifestDecoder.SafeRelativePath(relative)||root.StartsWith("\\\\",StringComparison.Ordinal))return null;
        try
        {
            using var file=WorkspaceFiles.OpenFile(relative,root);
            if(file.Size>maximum||!SingleLink(file.Stream.SafeFileHandle))return null;
            using var output=new MemoryStream();var buffer=new byte[8192];int n;
            while((n=file.Stream.Read(buffer,0,Math.Min(buffer.Length,maximum+1-(int)output.Length)))>0){output.Write(buffer,0,n);if(output.Length>maximum)return null;}
            return output.ToArray();
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){return null;}
    }
    internal static bool SingleLink(SafeFileHandle handle)
    {
        if(OperatingSystem.IsWindows())return GetFileInformationByHandle(handle,out var info)&&info.Links==1;
        if(OperatingSystem.IsMacOS())
        {
            var memory=Marshal.AllocHGlobal(256);
            try{return fstat(handle.DangerousGetHandle().ToInt32(),memory)==0&&((ushort)Marshal.ReadInt16(memory,6))==1&&(((ushort)Marshal.ReadInt16(memory,4))&0xf000)==0x8000;}
            finally{Marshal.FreeHGlobal(memory);}
        }
        // Style files are consumed by a Windows app; unsupported hosts fail closed.
        return false;
    }
    [StructLayout(LayoutKind.Sequential, Pack=4)] private struct HandleInfo {public uint Attributes; public long Created,Accessed,Written; public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file,out HandleInfo info);
    [DllImport("libc",SetLastError=true)] private static extern int fstat(int file,nint info);
    public static IReadOnlyList<string> Matches(string root,string pattern)
    {
        if(!StyleManifestDecoder.SafeRelativePath(pattern)||root.StartsWith("\\\\",StringComparison.Ordinal))return [];
        var parts=pattern.Replace('\\','/').Split('/',StringSplitOptions.RemoveEmptyEntries);var found=new HashSet<string>(StringComparer.Ordinal);int budget=1024;
        void Walk(string relative,int part,int depth)
        {
            if(part>=parts.Length||depth>8||found.Count>=32||budget<=0)return;
            if(WorkspaceFiles.Resolve(relative,root) is not {} directory||!Directory.Exists(directory))return;
            var key=parts[part];
            if(key=="**")Walk(relative,part+1,depth);
            IEnumerable<string> entries;
            try{entries=Directory.EnumerateFileSystemEntries(directory).Take(budget).ToArray();}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){return;}
            foreach(var entry in entries)
            {
                if(--budget<0||found.Count>=32)return;var name=Path.GetFileName(entry);
                if(name.StartsWith('.')&&!key.StartsWith('.'))continue;
                var rel=relative.Length==0?name:relative+"/"+name;
                if(WorkspaceFiles.Resolve(rel,root) is not {} safe)continue;
                if(key=="**"){if(Directory.Exists(safe))Walk(rel,part,depth+1);continue;}
                if(!Regex.IsMatch(name,"^"+Regex.Escape(key).Replace("\\*",".*",StringComparison.Ordinal).Replace("\\?",".",StringComparison.Ordinal)+"$",RegexOptions.CultureInvariant))continue;
                if(part==parts.Length-1){if(File.Exists(safe))found.Add(rel);}else if(Directory.Exists(safe))Walk(rel,part+1,depth+1);
            }
        }
        Walk("",0,0);return found.Order(StringComparer.Ordinal).ToArray();
    }
}
public static class StyleStateEngine
{
    public static StyleStateReading Read(StyleManifest manifest,string workspace,DateTimeOffset? since,RunSession? session=null)
    {
        var states=new Dictionary<int,StyleFileState>();var widgets=new List<StyleStateWidget>();
        if(!manifest.Root.TryGetProperty("stateSources",out var sources))return new(states,widgets);
        var index=0;
        foreach(var source in StyleManifest.Items(sources,"files"))
        {
            var kind=StyleManifest.Text(source,"widget")!;var state=new StyleFileState(false,false);var widget=new StyleStateWidget(kind);
            if(since is not null)
            {
                var current=StyleFiles.Matches(workspace,StyleManifest.Text(source,"path")!).Select(path=>new{Path=path,Time=File.GetLastWriteTimeUtc(Path.Combine(workspace,path))}).Where(f=>f.Time>since.Value.UtcDateTime).OrderByDescending(f=>f.Time).ThenByDescending(f=>f.Path,StringComparer.Ordinal).FirstOrDefault();
                if(current!=null){state=new(true,false);if(StyleFiles.Read(workspace,current.Path) is {} bytes){try{(state,widget)=Parse(new UTF8Encoding(false,true).GetString(bytes),StyleManifest.Text(source,"parser")!,kind);}catch(DecoderFallbackException){}}}
            }
            states[index++]=state;widgets.Add(widget);
        }
        foreach(var source in StyleManifest.Items(sources,"runEvents"))
        {
            var type=StyleManifest.Text(source,"event")!;var kind=StyleManifest.Text(source,"widget")!;var aggregate=StyleManifest.Text(source,"aggregate");
            var values=new List<string>();
            if(since!=null&&session!=null)foreach(var log in session.Logs)
            {
                if(log.Activity is not {} a||a.ToolName is null||!DateTimeOffset.TryParse(log.Timestamp,out var at)||at<since)continue;
                var agent=a.Kind=="agent"&&new[]{"task","agent"}.Contains(a.ToolName.ToLowerInvariant());
                if(type=="subagent.start"&&agent||type=="subagent.finish"&&agent&&new[]{"completed","error","stopped"}.Contains(a.State))values.Add(StyleText.Safe(a.Summary,400));
                else if(type=="tool.call"&&new[]{"tool","command","read","edit","search","web"}.Contains(a.Kind))values.Add(StyleText.Safe(a.ToolName,400));
            }
            widgets.Add(kind switch{
                "progressBar"=>new(kind,Value:values.Count),
                "list"=>new(kind,Items:aggregate=="count"?values.TakeLast(8).ToArray():values.TakeLast(1).ToArray()),
                _=>new(kind,aggregate=="lastValue"?values.LastOrDefault()??"":Locale.Get(type=="subagent.start"?"styles.state.subagentStartCount":type=="subagent.finish"?"styles.state.subagentFinishCount":"styles.state.toolCallCount",new Dictionary<string,string>{{"count",values.Count.ToString()}}))});
        }
        return new(states,widgets);
    }
    public static (StyleFileState State,StyleStateWidget Widget) Parse(string text,string parser,string kind)
    {
        var state=new StyleFileState(true,false);
        if(parser=="markdownChecklist")
        {
            bool fence=false;var items=new List<(string Text,bool Checked)>();
            foreach(var raw in text.Split('\n')){var line=raw.Trim();if(line.StartsWith("```",StringComparison.Ordinal)||line.StartsWith("~~~",StringComparison.Ordinal)){fence=!fence;continue;}if(fence)continue;var m=Regex.Match(line,@"^[-*+] +\[([ xX])\](?: +(.*))?$");if(m.Success)items.Add((StyleText.Safe(m.Groups[2].Value.Trim(),400),m.Groups[1].Value!=" "));}
            var count=items.Count(i=>i.Checked);state=new(true,items.Count>0&&count==items.Count);
            return(state,kind switch{"progressBar"=>new(kind,Value:count,Total:items.Count),"list"=>new(kind,Items:items.Where(i=>!i.Checked).Take(8).Select(i=>i.Text).ToArray()),_=>new(kind,Locale.Get("styles.state.checklistLabel",new Dictionary<string,string>{{"checked",count.ToString()},{"total",items.Count.ToString()}}))});
        }
        try
        {
            using var doc=JsonDocument.Parse(text);var root=doc.RootElement;
            static string? Scalar(JsonElement e)=>e.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False?StyleText.Safe(e.ToString(),400):null;
            if(kind=="list"){var list=root.ValueKind==JsonValueKind.Array?root:root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("items",out var items)?items:default;return(state,new(kind,Items:list.ValueKind==JsonValueKind.Array?list.EnumerateArray().Select(Scalar).OfType<string>().Take(8).ToArray():[]));}
            if(kind=="label"){var value=root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("text",out var t)?t:root;return(state,new(kind,Scalar(value)));}
            int? Count(string key)=>root.ValueKind==JsonValueKind.Object&&root.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out var n)&&n>=0?n:null;
            var done=Count("value");var total=Count("total");return(state,new(kind,Value:done is {} d?total is {} bound?Math.Min(d,bound):d:0,Total:total));
        }catch(JsonException){return(state,new(kind));}
    }
}
