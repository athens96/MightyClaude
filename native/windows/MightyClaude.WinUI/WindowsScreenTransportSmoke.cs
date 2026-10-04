using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace MightyClaude.WinUI;

/// Exercises the real packaged transport and WebView2 message boundary using
/// two local RTC peers and synthetic pixels. Never instantiates desktop capture,
/// global input hooks, clipboard access or a trusted mobile session.
internal static class WindowsScreenTransportSmoke
{
    private const string Origin = "https://screen.mightyclaude.invalid/";
    internal static async Task<Dictionary<string, object?>> RunAsync(Panel host, string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var token = deadline.Token;
        var view = new WebView2 { Width = 2, Height = 2, Opacity = 0, IsHitTestVisible = false, IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        host.Children.Add(view);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgments = new Dictionary<long, TaskCompletionSource>();
        var failures = new List<string>(); long sequence = 0; var unicode = false; var closed = false;
        async Task Command(string type, object? fields = null)
        {
            token.ThrowIfCancellationRequested(); var id = ++sequence;
            var message = fields is null ? new JsonObject() : JsonSerializer.SerializeToNode(fields, Wire.Json)!.AsObject();
            message["type"] = type; message["id"] = id;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); acknowledgments.Add(id, completion);
            try { view.CoreWebView2.PostWebMessageAsJson(message.ToJsonString(Wire.Json)); await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), token); }
            finally { acknowledgments.Remove(id); }
        }
        async Task<JsonElement> Script(string script)
        {
            token.ThrowIfCancellationRequested(); using var result = JsonDocument.Parse(await view.CoreWebView2.ExecuteScriptAsync(script).AsTask().WaitAsync(token)); return result.RootElement.Clone();
        }
        async Task<JsonElement> Wait(Func<JsonElement, bool> check, int seconds = 12)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                if (failures.Count > 0) throw new InvalidOperationException(failures[0]);
                var status = await Script("window.mightyScreenSmoke.status()");
                if (status.Text("error") is { Length: > 0 } error) throw new InvalidOperationException(error);
                if (check(status)) return status;
                await Task.Delay(100, token);
            }
            throw new TimeoutException("The native screen transport loopback did not reach its expected state.");
        }
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync("", Path.Combine(directory, "screen-transport-smoke"), new()).AsTask().WaitAsync(token);
            await view.EnsureCoreWebView2Async(environment).AsTask().WaitAsync(token);
            var core = view.CoreWebView2; var settings = core.Settings;
            settings.AreHostObjectsAllowed = false; settings.AreDevToolsEnabled = false; settings.AreDefaultContextMenusEnabled = false; settings.AreDefaultScriptDialogsEnabled = false; settings.IsPasswordAutosaveEnabled = false; settings.IsGeneralAutofillEnabled = false;
            core.SetVirtualHostNameToFolderMapping("screen.mightyclaude.invalid", Path.Combine(AppContext.BaseDirectory, "Assets", "ScreenShare"), CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (_, args) => args.Cancel = args.Uri != Origin + "index.html";
            core.FrameNavigationStarting += (_, args) => args.Cancel = true;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, args) => { if (args.Request.Method != "GET" || args.Request.Uri is not (Origin + "index.html" or Origin + "screen.js")) args.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", ""); };
            core.ProcessFailed += (_, _) => failures.Add("The screen loopback WebView2 process exited.");
            core.WebMessageReceived += async (_, args) =>
            {
                if (closed) return;
                try
                {
                    if (args.Source != Origin + "index.html" || Encoding.UTF8.GetByteCount(args.WebMessageAsJson) > 131072) throw new InvalidOperationException("Unexpected loopback bridge message.");
                    using var doc = JsonDocument.Parse(args.WebMessageAsJson); var value = doc.RootElement;
                    switch (value.Text("type"))
                    {
                        case "ready":
                            if (value.GetProperty("h264").GetBoolean()) ready.TrySetResult(); else ready.TrySetException(new NotSupportedException("WebView2 has no H264 sender."));
                            break;
                        case "ack":
                            if (acknowledgments.TryGetValue(value.GetProperty("id").GetInt64(), out var completion))
                            { if (value.GetProperty("ok").GetBoolean()) completion.TrySetResult(); else completion.TrySetException(new InvalidOperationException("The packaged screen bridge rejected a loopback command.")); }
                            break;
                        case "signal":
                            await Script("window.mightyScreenSmoke.receive(" + value.GetProperty("signal").GetRawText() + ")");
                            break;
                        case "smoke-phone-signal":
                            await Command("signal", new { sessionId = "native-smoke", signal = value.GetProperty("signal").Clone() });
                            break;
                        case "data":
                            using (var input = JsonDocument.Parse(value.Text("data")!)) unicode |= input.RootElement.Text("t") == "text" && input.RootElement.Text("text") == "\uD55C\uAE00 😀";
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { if (!closed) failures.Add(ex.Message); }
            };
            core.Navigate(Origin + "index.html"); await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            await Script(Harness);
            await Command("start", new { session = new { sessionId = "native-smoke", deviceId = "synthetic-phone", mode = "control", displayId = 1, codec = "H264", quality = new { width = 128, height = 72, fps = 15, maxBitrateKbps = 1000 } }, iceServers = Array.Empty<object>() });
            var connected = await Wait(v => v.GetProperty("connected").GetBoolean() && v.GetProperty("channelOpen").GetBoolean());
            if (string.Join(",", connected.GetProperty("streams").EnumerateArray().Select(v => v.GetString()).Order()) != "overview,screen") throw new InvalidOperationException("Screen and overview track identities were lost.");
            var frame = await Script("window.mightyScreenSmoke.frame()");
            await Command("frame", new { frame });
            await Command("data", new { sessionId = "native-smoke", message = new { t = "scene", phase = "motion" } });
            await Script("window.mightyScreenSmoke.sendUnicode()");
            await Wait(v => unicode && v.GetProperty("sceneReceived").GetBoolean());
            // Request additional synthetic frames while the local H264 decoder
            // starts. No desktop surface is ever opened by this smoke.
            var decoded = false;
            for (var attempt = 0; attempt < 80; attempt++)
            {
                await Command("frame", new { frame }); await Script("window.mightyScreenSmoke.sampleStats()");
                var status = await Script("window.mightyScreenSmoke.status()");
                if (status.Text("error") is { Length: > 0 } error) throw new InvalidOperationException(error);
                var sizes = status.GetProperty("videoSizes").EnumerateArray().Select(v => string.Join("x", v.EnumerateArray().Select(n => n.GetInt32()))).Order().ToArray();
                if (sizes.SequenceEqual(["128x72", "160x90"])) { decoded = true; break; }
                await Task.Delay(100, token);
            }
            if (!decoded) throw new InvalidOperationException("WebView2 did not decode both synthetic H264 tracks at the requested dimensions.");
            await Command("halt"); await Wait(v => v.GetProperty("channelClosed").GetBoolean(), 5);
            if (failures.Count != 0) throw new InvalidOperationException(failures[0]);
            return new() { ["packagedWebViewBridge"] = true, ["h264Negotiated"] = true, ["screenAndOverviewTracksDecoded"] = true, ["perPeerResolutionLimit"] = true, ["unicodeControlRoundTrip"] = true, ["hostDataRoundTrip"] = true, ["haltClosedChannel"] = true, ["desktopCaptured"] = false, ["nativeInputInjected"] = false };
        }
        finally
        {
            closed = true;
            foreach (var pending in acknowledgments.Values) pending.TrySetCanceled(); acknowledgments.Clear();
            try { if (view.CoreWebView2 is { } core) await core.ExecuteScriptAsync("window.mightyScreenSmoke?.close()").AsTask().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            view.Close(); host.Children.Remove(view);
        }
    }
    private const string Harness = """
        (() => {
          const phone = new RTCPeerConnection({iceServers: []});
          const streams = [], pendingIce = []; let channel, error = '', sceneReceived = false, videoSizes = [], signaling = Promise.resolve(), statsPending = false;
          const fail = e => { error = String(e?.message || e); };
          const signal = value => chrome.webview.postMessage({type:'smoke-phone-signal', signal:value});
          phone.ontrack = e => streams.push(e.streams[0].id);
          phone.ondatachannel = e => { channel = e.channel; channel.onmessage = e => { try { const value = JSON.parse(e.data); sceneReceived ||= value.t === 'scene' && value.phase === 'motion'; } catch(e) { fail(e); } }; };
          phone.onicecandidate = e => signal({type:'screen-ice', ...(e.candidate ? e.candidate.toJSON() : {candidate:''})});
          window.mightyScreenSmoke = {
            receive(value) {
              signaling = signaling.then(async () => {
                if(value.type === 'screen-offer') {
                  await phone.setRemoteDescription({type:'offer', sdp:value.sdp});
                  const answer = await phone.createAnswer(); await phone.setLocalDescription(answer);
                  signal({type:'screen-answer', sdp:answer.sdp});
                  for(const candidate of pendingIce.splice(0)) await phone.addIceCandidate(candidate);
                } else if(value.type === 'screen-ice') {
                  const candidate = value.candidate ? {candidate:value.candidate, sdpMid:value.sdpMid ?? null, sdpMLineIndex:value.sdpMLineIndex ?? null} : null;
                  if(phone.remoteDescription) await phone.addIceCandidate(candidate); else pendingIce.push(candidate);
                }
              }).catch(fail);
            },
            frame() { const canvas=document.createElement('canvas'); canvas.width=160; canvas.height=90; const context=canvas.getContext('2d'); context.fillStyle='#e53b71'; context.fillRect(0,0,160,90); const data=canvas.toDataURL('image/jpeg').split(',')[1]; return {data,mime:'image/jpeg',width:160,height:90,overviewData:data}; },
            sendUnicode() { channel.send(JSON.stringify({t:'text',text:'\uD55C\uAE00 😀'})); },
            sampleStats() { if(statsPending) return; statsPending=true; phone.getStats().then(stats => { videoSizes=[...stats.values()].filter(v => v.type === 'inbound-rtp' && v.kind === 'video' && v.framesDecoded > 0).map(v => [v.frameWidth,v.frameHeight]); }).catch(fail).finally(() => statsPending=false); },
            status() { return {error,connected:phone.connectionState === 'connected',channelOpen:channel?.readyState === 'open',channelClosed:channel?.readyState === 'closed',streams,sceneReceived,videoSizes}; },
            close() { if(channel) channel.close(); phone.close(); }
          };
          return true;
        })()
        """;
}
