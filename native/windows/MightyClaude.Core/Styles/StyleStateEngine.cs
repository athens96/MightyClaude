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
    // ── §1.17 (v6): the pane's plan-mode state ────────────────────────────────

    /// <summary>The closed plan stages, in order.</summary>
    public static readonly string[] PlanStages=["planning","awaitingApproval","executing"];
    /// <summary>A task list shows at most this many tasks.</summary>
    public const int MaximumTaskListItems=8;

    /// <summary>
    /// Where the pane stands in Claude's plan mode (M/StyleStateEngine.swift planStage): a waiting plan is
    /// awaitingApproval; otherwise the latest request's plan answers decide — approved is executing, none
    /// yet, sent back or cancelled is planning, so the next new request starts there again.
    /// </summary>
    public static string PlanStage(RunSession session,bool pendingPlan)
    {
        if(pendingPlan)return "awaitingApproval";
        // The latest request as the Mac reads it: the saved request blocks, or for a pane that never saved any, the blocks
        // its log makes (M/MightyGraph.swift legacyRuns) — the last one's id is its last user entry, or "history-<pane>".
        string? lastId=null,lastSource=null,lastStatus=null;
        if(session.GraphRuns is {} runs){if(runs.Count>0){lastId=runs[^1].Id;lastSource=runs[^1].SourceRunID;lastStatus=runs[^1].Status;}}
        else if(session.Logs.Count>0){lastId=session.Logs.LastOrDefault(l=>l.Kind=="user")?.Id??"history-"+session.Id;lastStatus=session.Status=="idle"?"completed":session.Status;}
        // A run has started but its request block is not there yet (the tracker adds it when the run's graph first reports):
        // that request has no plan answer yet. A turn that is over and only waits on background work is still its own.
        if(session.Status=="running"&&lastStatus is {} status&&MightyGraphSupport.Terminal(status)&&session.BackgroundWork?.TurnEnded!=true)return "planning";
        var history=session.PlanHistory??[];PlanRecord? latest;
        // A record's GraphRunId is its run's SourceRunID on both platforms (the block id is matched too, as the diagram attaches
        // records either way); a Mac record from before GraphRunId names the same id as its RunId.
        if(lastId is not null)latest=history.LastOrDefault(r=>r.GraphRunId==lastId||lastSource is not null&&(r.GraphRunId==lastSource||r.RunId==lastSource));
        else latest=history.LastOrDefault();
        return latest?.Outcome is PlanOutcome.ApprovedAuto or PlanOutcome.ApprovedConfirm?"executing":"planning";
    }

    /// <summary>The cached file and event reading with the pane's live plan stage and run-state widgets added last.</summary>
    public static StyleStateReading Live(StyleStateReading? cached,StyleManifest manifest,RunSession session,bool pendingPlan)
    {
        var evaluator=new StyleEvaluator(manifest);
        var reading=cached??new(new Dictionary<int,StyleFileState>(),[]);
        if(!evaluator.ReadsPlanState)return reading;
        var stage=PlanStage(session,pendingPlan);
        var widgets=reading.Widgets.Concat(RunStateWidgets(manifest,stage,session.TodoProgress,session.BackgroundWork)).ToList();
        return reading with{Widgets=widgets,PlanStage=stage};
    }

    /// <summary>Each declared run-state source as its widget. The checklist belongs to the plan being carried out, so it reads only while executing.</summary>
    public static IReadOnlyList<StyleStateWidget> RunStateWidgets(StyleManifest manifest,string stage,TodoProgress? todos,BackgroundWork? work)
    {
        var widgets=new List<StyleStateWidget>();
        if(!manifest.Root.TryGetProperty("stateSources",out var sources))return widgets;
        foreach(var source in StyleManifest.Items(sources,"runState"))widgets.Add(RunState(StyleManifest.Text(source,"source")!,StyleManifest.Text(source,"widget")!,stage,todos,work));
        return widgets;
    }

    public static StyleStateWidget RunState(string source,string kind,string stage,TodoProgress? todos,BackgroundWork? work)
    {
        static Dictionary<string,string> Count(int count)=>new(){{"count",count.ToString(System.Globalization.CultureInfo.InvariantCulture)}};
        if(source=="todos")
        {
            if(stage!="executing"||todos is not {Items.Count:>0})return Empty(kind);
            return kind switch
            {
                "progressBar"=>new(kind,Value:todos.Completed,Total:todos.Items.Count),
                "list"=>new(kind,Items:todos.Items.Where(i=>i.Status!="completed").Select(i=>StyleText.Safe(i.Content,400)).Take(8).ToArray()),
                _=>new(kind,todos.CurrentText is {} current?Locale.Get("styles.state.todoCurrent",new Dictionary<string,string>{{"item",StyleText.Safe(current,400)}}):""),
            };
        }
        var tasks=work?.Tasks??[];var running=tasks.Count(t=>t.Status=="running");
        return kind switch
        {
            // Running work first, each group in the order it started.
            "taskList"=>new(kind,Tasks:tasks.Where(t=>t.Status=="running").Concat(tasks.Where(t=>t.Status!="running")).Take(MaximumTaskListItems).Select(t=>new StyleTaskItem(StyleText.Safe(t.Description,400),t.Kind,t.Status,t.StartedAt,t.EndedAt)).ToArray()),
            "label"=>new(kind,work is {WaitingOnBackground:true}?Locale.Get("plan.background.status",Count(running)):running>0?Locale.Get("styles.state.backgroundRunning",Count(running)):""),
            _=>new(kind,Value:tasks.Count-running,Total:tasks.Count),
        };
    }

    private static StyleStateWidget Empty(string kind)=>kind switch{"progressBar"=>new(kind,Value:0,Total:0),"list"=>new(kind,Items:[]),"taskList"=>new(kind,Tasks:[]),_=>new(kind,"")};

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
