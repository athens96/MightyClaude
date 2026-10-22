using System.Diagnostics;
using System.Globalization;

namespace MightyClaude.Core;

/// <summary>
/// Phone notifications on a Windows host (M/MightyClaude/AppStore+MobilePush.swift and MobileRemoteService push):
/// which phone wants what, which session each has on screen, and what to send. Best effort: failures are traced only,
/// and never with a handle, a token, an envelope or what a notification says.
/// </summary>
public sealed class MobilePushHub : IDisposable
{
    /// <summary>The longest single wait a relay's Retry-After can ask for; two retries bound a send to about a minute.</summary>
    public static readonly TimeSpan MaximumRetryWait = TimeSpan.FromSeconds(30);
    private readonly DesktopService service;
    private readonly MobileRelayHost host;
    private readonly object sync = new();
    private readonly Dictionary<string, MobilePushViewing> viewing = [];
    /// <summary>This PC's pending requests, in the order they came.</summary>
    private readonly List<ToolPermissionRequest> permissions = [];
    /// <summary>Guards the planner (not thread-safe) and the last hand-off: one hand-off at a time, in order.</summary>
    private readonly object planning = new();
    private readonly MobilePushPlanner planner = new();
    /// <summary>The panes last handed to the planner; null while phone remote access is off, so the next hand-off starts it over.</summary>
    private IReadOnlyList<MobilePushPane>? last;
    /// <summary>Set when something a push depends on was reported; the panes are read again at the next <see cref="Observe"/>.</summary>
    private int moved = 1;
    /// <summary>Cancelled when the hub is disposed: nothing is sent or tried again after that.</summary>
    private readonly CancellationTokenSource stopping = new();
    public string HostName { get; }
    /// <summary>Whether the pane's sign-in was confirmed lost (the WinUI login recovery's remembered requests); read on the caller's thread.</summary>
    public Func<string, bool> SignInLost { get; set; } = _ => false;
    /// <summary>Clock for the views and push timestamps; the checks move it.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    /// <summary>The session each phone said is on its screen; a push about it is not sent while it holds.</summary>
    internal IReadOnlyDictionary<string, MobilePushViewing> Viewing { get { lock (sync) return new Dictionary<string, MobilePushViewing>(viewing); } }
    internal IMobilePushTransport Transport { get; set; } = new MobilePushHttpTransport();
    /// <summary>Waits before the second and third attempt; a relay's Retry-After wins when longer, up to <see cref="MaximumRetryWait"/>.</summary>
    internal IReadOnlyList<TimeSpan> RetryDelays { get; set; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)];
    internal Func<TimeSpan, CancellationToken, Task> Wait { get; set; } = Task.Delay;
    internal Func<string> Relay { get; set; }
    /// <summary>Whether phone remote access is on: the host's applied settings.</summary>
    internal Func<bool> RemoteOn { get; set; }
    public MobilePushHub(DesktopService service, MobileRelayHost host, string hostName)
    {
        this.service = service; this.host = host; HostName = hostName; Relay = () => host.RelayForPush; RemoteOn = () => host.Enabled;
        host.Identity.Rotated += Forget;
        service.ToolPermissionChanged += Permission; service.RunEventReceived += Receive; service.AntigravityCardChanged += Card;
    }
    private void Permission(ToolPermissionRequest value)
    {
        // A remote pane's requests are its host's (MobileDesktopRouter.Permission). A request the pane's style allows on its own
        // is held back by DesktopService until it is answered, so it never arrives here as pending.
        if (service.IsRemoteSession(value.RunId)) return;
        lock (sync) { permissions.RemoveAll(p => p.RunId == value.RunId && p.Id == value.Id); if (value.State == "pending") permissions.Add(value); }
        Changed();
    }
    private void Receive(RunEvent value) => Changed();
    private void Card(string sessionId) => Changed();
    /// <summary>Something a push may depend on moved: the panes are read again at the next <see cref="Observe"/>.</summary>
    public void Changed() => Volatile.Write(ref moved, 1);
    public MobilePushRegistrationResult Register(string device, string handle, IReadOnlyList<string> kinds) => host.Identity.SetPush(device, handle, kinds);
    /// <summary>False when the handle could not be cleared from the file: it is still there, and so is the view.</summary>
    public bool Unregister(string device)
    {
        if (!host.Identity.ClearPush(device)) return false;
        lock (sync) viewing.Remove(device);
        return true;
    }
    public void View(string device, string? sessionId)
    {
        lock (sync)
        {
            if (sessionId is null) viewing.Remove(device);
            else viewing[device] = new(sessionId, Clock() + MobilePushRecipients.ViewingWindow);
        }
    }
    /// <summary>A rotated key (a revoke included) makes every phone pair again, so none of them is looking at anything here.</summary>
    private void Forget() { lock (sync) viewing.Clear(); }

    /// <summary>
    /// Reads what a phone may see now and hands it to the planner (<see cref="MobilePushPane.Project"/>). Call on the UI thread:
    /// <see cref="SignInLost"/> reads UI state. Nothing while phone remote access is off; the panes are read again only when
    /// something was reported since the last pass or a pane's sign-in changed. Sending happens off the caller's thread.
    /// </summary>
    public IReadOnlyList<Task> Observe()
    {
        if (stopping.IsCancellationRequested) return [];
        if (!RemoteOn()) { lock (planning) last = null; Changed(); return []; }
        IReadOnlyList<MobilePushPane>? previous;
        lock (planning) previous = last;
        if (Interlocked.Exchange(ref moved, 0) == 0 && previous is not null && previous.All(pane => SignInLost(pane.SessionId) == pane.SignInLost)) return [];
        try
        {
            var snapshot = service.Snapshot;
            Dictionary<string, IReadOnlyList<ToolPermissionRequest>> pending;
            lock (sync) pending = permissions.GroupBy(p => p.RunId).ToDictionary(g => g.Key, g => (IReadOnlyList<ToolPermissionRequest>)g.ToArray());
            // A style's own requests never reach the list (Permission), so none is left out here.
            return Push(MobilePushPane.Project(snapshot.Sessions, snapshot.Workspaces, pending, (_, _) => false, service.AntigravityCard, SignInLost));
        }
        catch { Changed(); throw; }
    }

    /// <summary>
    /// One hand-off: the first after phone remote access came on starts the planner over (what changed meanwhile is not pushed),
    /// the same panes again decide nothing, and what the planner decides is sealed per phone and sent off the caller's thread.
    /// </summary>
    internal IReadOnlyList<Task> Push(IReadOnlyList<MobilePushPane> panes)
    {
        var now = Clock();
        IReadOnlyList<MobilePushMessage> messages;
        lock (planning)
        {
            // Off or shut down, nothing goes out whatever the caller hands on.
            if (stopping.IsCancellationRequested || !RemoteOn()) { last = null; return []; }
            if (last is not null && last.SequenceEqual(panes)) return [];
            if (last is null) planner.Reset();
            last = panes.ToArray();
            messages = planner.Observe(panes, host.HostId, HostName, now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }
        return messages.Count == 0 ? [] : [Task.Run(() => Send(messages, now))];
    }

    private async Task Send(IReadOnlyList<MobilePushMessage> messages, DateTimeOffset now)
    {
        try
        {
            var devices = host.Identity.PushDevices();
            if (devices.Count == 0) return;
            if (MobilePushRelay.SendUri(Relay()) is not { } url) { Trace.TraceInformation("mobile push: no relay to send through"); return; }
            var hostToken = host.Identity.HostToken; var serverId = host.Identity.ServerId;
            Dictionary<string, MobilePushViewing> seen;
            lock (sync)
            {
                foreach (var stale in viewing.Where(v => v.Value.Until <= now).Select(v => v.Key).ToArray()) viewing.Remove(stale);
                seen = new(viewing);
            }
            var deliveries = new List<Task>();
            foreach (var message in messages)
            {
                if (message.EncodedForPush() is not { } plaintext) { Trace.TraceWarning("mobile push: " + message.Id + " is too large even without its summary; skipped"); continue; }
                foreach (var device in MobilePushRecipients.Targets(message, devices, seen, now))
                {
                    if (MobilePushSeal.Key(device.TokenHash, device.DeviceId) is not { } key) { Trace.TraceWarning("mobile push: " + message.Id + " could not be sealed for a phone; skipped"); continue; }
                    deliveries.Add(Deliver(new(serverId, device.Handle, MobilePushSeal.Seal(plaintext, key, device.Handle)), url, hostToken, device.DeviceId, message.Id));
                }
            }
            await Task.WhenAll(deliveries);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceWarning("mobile push: " + ex.GetType().Name); }
    }

    private async Task Deliver(MobilePushSendRequest request, Uri url, string hostToken, string deviceId, string messageId)
    {
        var token = stopping.Token;
        var waits = new Queue<TimeSpan>(RetryDelays);
        while (true)
        {
            // Switched off or closing meanwhile: nothing more goes out.
            if (token.IsCancellationRequested || !RemoteOn()) return;
            MobilePushSendOutcome outcome;
            try { outcome = await Transport.SendAsync(request, url, hostToken, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceWarning("mobile push: " + messageId + " " + ex.GetType().Name); outcome = new(MobilePushSendStatus.Retry); }
            switch (outcome.Status)
            {
                case MobilePushSendStatus.Sent:
                    return;
                case MobilePushSendStatus.Gone:
                    // Only the handle that was sent to: one the phone registered since stays.
                    if (host.Identity.ClearPush(deviceId, request.Handle)) Trace.TraceInformation("mobile push: " + messageId + " the relay no longer knows a phone's handle; dropped it");
                    else Trace.TraceWarning("mobile push: " + messageId + " the relay no longer knows a phone's handle, which could not be forgotten");
                    return;
                case MobilePushSendStatus.Failed:
                    Trace.TraceWarning("mobile push: " + messageId + " refused by the relay (" + outcome.Code.ToString(CultureInfo.InvariantCulture) + ")");
                    return;
                default:
                    if (waits.Count == 0) { Trace.TraceWarning("mobile push: " + messageId + " the relay could not take it; gave up"); return; }
                    if (token.IsCancellationRequested || !RemoteOn()) return;
                    var wait = waits.Dequeue();
                    if (outcome.RetryAfter is { } asked && asked > wait) wait = asked;
                    if (wait > MaximumRetryWait) wait = MaximumRetryWait;
                    if (wait <= TimeSpan.Zero) break;
                    try { await Wait(wait, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    break;
            }
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        host.Identity.Rotated -= Forget;
        service.ToolPermissionChanged -= Permission; service.RunEventReceived -= Receive; service.AntigravityCardChanged -= Card;
    }
}
