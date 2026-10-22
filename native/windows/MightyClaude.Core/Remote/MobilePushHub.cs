namespace MightyClaude.Core;

/// <summary>
/// Phone notifications on a Windows host (M/MightyClaude/AppStore+MobilePush.swift and MobileRemoteService push):
/// which phone wants what, which session each has on screen, and what to send.
/// </summary>
public sealed class MobilePushHub : IDisposable
{
    private readonly DesktopService service;
    private readonly MobileRelayHost host;
    private readonly object sync = new();
    private readonly Dictionary<string, MobilePushViewing> viewing = [];
    public string HostName { get; }
    /// <summary>Clock for the views and push timestamps; the checks move it.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    /// <summary>The session each phone said is on its screen; a push about it is not sent while it holds.</summary>
    internal IReadOnlyDictionary<string, MobilePushViewing> Viewing { get { lock (sync) return new Dictionary<string, MobilePushViewing>(viewing); } }
    public MobilePushHub(DesktopService service, MobileRelayHost host, string hostName)
    {
        this.service = service; this.host = host; HostName = hostName;
        host.Identity.Rotated += Forget;
    }
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
    public void Dispose() => host.Identity.Rotated -= Forget;
}
