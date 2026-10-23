using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace MightyClaude.WinUI;

/// <summary>
/// Prototype: an agent pane's Default conversation drawn by a WebView2 page instead of the RichEditBox, to time
/// against it. RichEdit reads the whole RTF again on every streamed line (170–830 ms on a long conversation); here
/// only the entries that changed are made into HTML and sent, and the page swaps just those nodes. Used only while a
/// <c>transcript-web</c> file stands in the state folder at start (<see cref="Configure"/>); without it nothing changes.
/// </summary>
internal sealed class WebTranscript
{
    /// <summary>Whether the state folder held <c>transcript-web</c> when the app started.</summary>
    internal static bool Enabled { get; private set; }
    /// <summary>Looks once, at start, as <see cref="PerfLog.Configure"/> does.</summary>
    internal static void Configure(string stateDirectory) => Enabled = File.Exists(Path.Combine(stateDirectory, "transcript-web"));

    internal WebView2 View { get; } = new() { MinHeight = 80, DefaultBackgroundColor = Colors.Transparent };
    internal Func<string, int?, Task>? OpenReference { get; set; }
    /// <summary>The words' inset from each side of the view, in epx: the RichEditBox's padding it stands in for.</summary>
    internal double SideInset { get; set; } = TranscriptRtf.Inset;
    private readonly string profile;
    private bool started, navigating, ready;
    private (RunSession Session, bool Light)? last;
    private bool? sentLight;
    /// <summary>The entries the page holds, in its order, and the signature each was sent with.</summary>
    private readonly List<string> shownIds = [];
    private readonly Dictionary<string, int> shownSignatures = [];

    internal WebTranscript(string stateDirectory)
    {
        profile = Path.Combine(stateDirectory, "transcript-web-profile");
        AutomationProperties.SetName(View, Locale.Get("transcript.accessibility"));
        View.Loaded += async (_, _) => { if (!started) { started = true; await Initialize(); } };
        // A new text setting changes the page's fonts and sizes, which travel with its colours.
        AppText.Follow(View, () => { sentLight = null; if (last is { } value) Update(value.Session, value.Light); });
    }

    private async Task Initialize()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync("", profile, new());
            await View.EnsureCoreWebView2Async(environment);
            var core = View.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultScriptDialogsEnabled = false; core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
            // The page's sizes are the app's text setting; Ctrl+= must not zoom it behind the setting's back.
            core.Settings.IsZoomControlEnabled = false;
            // Only the page written here loads; it has no address of its own to come back to, so nothing navigates once it is up.
            core.NavigationStarting += (_, args) => args.Cancel = ready || !navigating;
            core.FrameNavigationStarting += (_, args) => args.Cancel = true;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += Receive;
            navigating = true;
            core.NavigateToString(Page);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PerfLog.Mark("web transcript failed: " + ex.Message); }
    }

    private async void Receive(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var json = JsonDocument.Parse(args.WebMessageAsJson);
            var value = json.RootElement; var type = value.Text("type");
            if (type == "ready")
            {
                // A page loaded again holds nothing: every entry is sent to it again.
                ready = true; navigating = false; sentLight = null; shownIds.Clear(); shownSignatures.Clear();
                if (last is { } current) Update(current.Session, current.Light);
            }
            else if (type == "ref" && OpenReference is { } open && value.Text("text") is { Length: > 0 and <= 2048 } words && ReferencePreview.TextTargetAt(words, 0) is { } target)
                await open(target.Path, target.Line);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
    }

    /// <summary>
    /// Sends the page what changed since the last call: the palette when the theme did, and each entry that is new or no
    /// longer what was sent. An entry is known by its id and a signature of what its drawing reads; the streaming reply is
    /// the last entry and is replaced in place. Entries taken away or reordered start the page over.
    /// </summary>
    internal void Update(RunSession session, bool light)
    {
        last = (session, light);
        if (!ready || View.CoreWebView2 is not { } core) return;
        using var perf = PerfLog.Measure("web transcript update");
        if (sentLight != light) { sentLight = light; Post(core, Theme(light)); }
        var logs = session.Logs;
        var reset = logs.Count < shownIds.Count;
        for (var index = 0; !reset && index < shownIds.Count; index++) reset = logs[index].Id != shownIds[index];
        if (reset) { shownIds.Clear(); shownSignatures.Clear(); }
        var upserts = new List<object>();
        foreach (var entry in logs)
        {
            var signature = HashCode.Combine(entry.Text, entry.Kind, entry.Activity, entry.Provider, entry.Timestamp, session.Status == "running", session.Kind, session.Provider);
            var known = shownSignatures.TryGetValue(entry.Id, out var sent);
            if (known && sent == signature) continue;
            if (!known) shownIds.Add(entry.Id);
            shownSignatures[entry.Id] = signature;
            upserts.Add(new { id = entry.Id, html = TranscriptRtf.EntryHtml(session, entry) });
        }
        if (reset || upserts.Count > 0) Post(core, new { type = "entries", reset, upserts });
    }

    private static void Post(CoreWebView2 core, object message)
    {
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>The page's CSS variables from the design palette of <paramref name="light"/>'s theme and the text setting's fonts.</summary>
    private object Theme(bool light)
    {
        var p = DesignTokens.Palette(!light); var faces = TranscriptRtf.WebFaces();
        static string Family(string name, string fallback) => "\"" + name.Replace("\"", "").Replace("\\", "") + "\", " + fallback;
        static string Px(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "px";
        return new
        {
            type = "theme",
            dark = !light,
            vars = new Dictionary<string, string>
            {
                ["--surface"] = p.CardRaised.Hex, ["--card"] = p.Card.Hex, ["--line"] = p.Line.Hex, ["--ink"] = p.Ink.Hex, ["--ink2"] = p.Ink2.Hex, ["--ink3"] = p.Ink3.Hex,
                ["--accent"] = p.Accent.Hex, ["--accent-soft"] = p.AccentSoft.Hex, ["--err-text"] = p.ErrText.Hex, ["--wait-text"] = p.WaitText.Hex,
                ["--code-surface"] = p.CodeSurface.Hex, ["--code-text"] = p.CodeText.Hex,
                ["--done"] = p.Done.Hex, ["--err"] = p.Err.Hex, ["--wait"] = p.Wait.Hex, ["--run"] = p.Run.Hex, ["--stop"] = p.Stop.Hex,
                ["--body"] = Family(faces.Body, "\"Segoe UI\", sans-serif"), ["--mono"] = Family(faces.Mono, "Consolas, monospace"),
                ["--size"] = Px(faces.Size), ["--code-size"] = Px(faces.CodeSize), ["--inset"] = Px(SideInset),
            },
        };
    }

    /// <summary>
    /// The page: no address, no remote content (its policy allows only its own style and script), one node per entry
    /// kept by id. It follows the bottom while the reader is at it and sends a click on a file reference back.
    /// </summary>
    private const string Page = """
<!doctype html><html><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'">
<style>
html,body{margin:0;background:var(--surface);color:var(--ink);font-family:var(--body);font-size:var(--size);line-height:1.45}
#log{padding:15px var(--inset)}
.entry{margin:0 0 10px}
p{margin:0 0 8px;white-space:pre-wrap;overflow-wrap:anywhere}
h1,h2,h3,h4,h5,h6{margin:5px 0 10px;font-weight:600;line-height:1.25}
h1{font-size:22px}h2{font-size:18px}h3{font-size:15px}h4,h5,h6{font-size:var(--size)}
ul,ol{margin:0 0 8px;padding-left:22px}li{margin:0 0 6px}li::marker{color:var(--accent)}li.task{list-style:none}li>p{margin:0}
code{font-family:var(--mono);font-size:min(var(--code-size),1em);color:var(--accent);background:var(--accent-soft);border-radius:4px;padding:0 3px}
.code{background:var(--code-surface);color:var(--code-text);border-radius:11px;padding:10px 12px;margin:0 0 12px;overflow-x:auto}
.code pre,details pre{margin:0;font-family:var(--mono);font-size:var(--code-size);white-space:pre-wrap;overflow-wrap:anywhere}
.lang{font-family:var(--mono);font-size:10px;opacity:.7;margin-bottom:5px}
blockquote{margin:0 0 8px;padding:2px 0 2px 12px;border-left:3px solid color-mix(in srgb,var(--accent) 55%,transparent);color:var(--ink2)}
hr{border:0;border-top:1px solid var(--line);margin:8px 0 10px}
table{border-collapse:collapse;margin:0 0 10px;font-size:12px;width:100%}
th,td{border:1px solid var(--line);padding:6px 8px;text-align:left;vertical-align:top}th{font-weight:600;background:color-mix(in srgb,var(--ink) 4%,var(--card))}
.user{display:flex;justify-content:flex-end}
.bubble{max-width:85%;background:var(--ink);color:var(--card);border-radius:11px;padding:8px 12px;white-space:pre-wrap;overflow-wrap:anywhere}
.time{font-size:10.5px;text-align:right;opacity:.85;margin-top:3px}
.reply{background:var(--card);border:1px solid var(--line);border-radius:11px;padding:10px 14px}
.who{font-size:12px;font-weight:600;color:var(--ink2);margin-bottom:10px}.mark{color:var(--accent)}
.tool{display:flex;gap:6px;align-items:baseline;flex-wrap:wrap;background:var(--card);border:1px solid var(--line);border-radius:11px;padding:6px 10px;font-size:12.5px}
.tool.error{border-color:var(--err-text)}
.tool code{color:var(--ink2);background:none;padding:0;font-size:11.5px;overflow-wrap:anywhere}
.square{width:8px;height:8px;border-radius:2px;background:var(--stop);flex:none;align-self:center}
.completed .square{background:var(--done)}.error .square{background:var(--err)}.live-running .square{background:var(--run)}.live-waiting .square{background:var(--wait)}
.meta,.state{font-size:10.5px;color:var(--ink2)}.live-running .state{color:var(--accent)}.live-waiting .state{color:var(--wait-text)}.error .state{color:var(--err-text)}
details{margin:4px 0 0;font-size:10.5px;color:var(--accent)}details pre{color:var(--ink);background:color-mix(in srgb,var(--ink) 3.5%,var(--card));border-radius:8px;padding:8px 10px;margin-top:4px}
summary{cursor:pointer}
.status{font-size:12px;color:var(--ink2)}.status.error{color:var(--err-text)}.note{font-size:10px;color:var(--ink2)}
.quiet{color:var(--ink2)}.link{color:var(--accent)}a.ref{color:var(--accent);text-decoration:underline;cursor:pointer}
</style></head><body><div id="log"></div><script>
const log=document.getElementById('log'),nodes=new Map(),page=document.scrollingElement;
const bottom=()=>page.scrollHeight-page.scrollTop-page.clientHeight<32;
let first=true;
chrome.webview.addEventListener('message',e=>{
 const m=e.data;
 if(m.type==='theme'){for(const k in m.vars)document.documentElement.style.setProperty(k,m.vars[k]);document.documentElement.style.colorScheme=m.dark?'dark':'light';return;}
 if(m.type!=='entries')return;
 const follow=first||bottom();first=false;
 if(m.reset){log.replaceChildren();nodes.clear();}
 for(const u of m.upserts){
  const node=document.createElement('div');node.className='entry';node.innerHTML=u.html;
  const old=nodes.get(u.id);if(old)old.replaceWith(node);else log.appendChild(node);nodes.set(u.id,node);
 }
 if(follow)page.scrollTop=page.scrollHeight;
});
document.addEventListener('click',e=>{
 const a=e.target.closest('a');if(!a)return;e.preventDefault();
 if(a.classList.contains('ref')&&getSelection().isCollapsed)chrome.webview.postMessage({type:'ref',text:a.dataset.ref});
});
chrome.webview.postMessage({type:'ready'});
</script></body></html>
""";
}
