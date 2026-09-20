namespace MightyClaude.Core;

/// <summary>
/// Swappable clock so tests fast-forward time without waiting on real delays.
/// </summary>
public interface IStatusLineClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the per-session status-line loop: debounce, generation counter, re-run when a
/// refresh was requested while one was running, nothing after the session closed, and never
/// two commands at once for one session. The trust rule is unchanged — a workspace command
/// never runs before the user allows it; the user-level command runs meanwhile.
/// Mirrors macOS AppStore+StatusLine.swift refreshStatusLine(for:force:).
/// </summary>
public sealed class StatusLineRefresher
{
    /// <summary>Minimum interval between runs — mirrors macOS statusLineMinimumInterval = 2 s.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(2);

    private readonly Func<StatusLineDiscovery> _discover;
    private readonly Func<AppSnapshot> _getSnapshot;
    private readonly string _workspaceId;
    private readonly Func<StatusLineConfig, StatusLineContext, CancellationToken, Task<StatusLineResult>> _runner;
    private readonly IStatusLineClock _clock;
    private readonly object _gate = new();

    // All fields below are protected by _gate.
    private StatusLineConfig? _config;
    private StatusLineConfig? _untrusted;
    private StatusLineResult? _result;
    private DateTimeOffset? _updatedAt;
    private bool _running;
    private bool _pending;
    private int _generation;
    private bool _closed;
    private StatusLineContext? _pendingContext;

    /// <summary>The command that was resolved and run (or null when gated / no config).</summary>
    public StatusLineConfig? Config { get { lock (_gate) return _config; } }
    /// <summary>A workspace command found but not yet allowed by the user.</summary>
    public StatusLineConfig? Untrusted { get { lock (_gate) return _untrusted; } }
    /// <summary>The last output from the command.</summary>
    public StatusLineResult? Result { get { lock (_gate) return _result; } }

    /// <summary>Raised when Config, Untrusted or Result changes. May fire off the caller's thread.</summary>
    public event Action? StateChanged;

    public StatusLineRefresher(
        Func<StatusLineDiscovery> discover,
        Func<AppSnapshot> getSnapshot,
        string workspaceId,
        Func<StatusLineConfig, StatusLineContext, CancellationToken, Task<StatusLineResult>> runner,
        IStatusLineClock? clock = null)
    {
        _discover = discover;
        _getSnapshot = getSnapshot;
        _workspaceId = workspaceId;
        _runner = runner;
        _clock = clock ?? new DefaultStatusLineClock();
    }

    /// <summary>
    /// Requests a refresh. If a run is in progress the request is remembered and served
    /// when it finishes. If the last run was recent the request is debounced (unless
    /// force=true). Ignored once the session is closed.
    /// </summary>
    public void RequestRefresh(StatusLineContext context, bool force = false)
    {
        bool startRun = false;
        int generation = 0;
        bool scheduleDebounce = false;
        TimeSpan debounceDelay = default;

        lock (_gate)
        {
            if (_closed) return;
            if (_running)
            {
                _pending = true;
                _pendingContext = context;
                return;
            }
            if (!force && _updatedAt.HasValue)
            {
                var elapsed = _clock.UtcNow - _updatedAt.Value;
                if (elapsed < MinimumInterval)
                {
                    if (_pending) return; // debounce already scheduled
                    _pending = true;
                    _pendingContext = context;
                    debounceDelay = MinimumInterval - elapsed;
                    scheduleDebounce = true;
                }
            }
            if (!scheduleDebounce)
            {
                _running = true;
                _pending = false;
                _pendingContext = null;
                _generation++;
                generation = _generation;
                startRun = true;
            }
        }

        if (scheduleDebounce) { _ = DebounceAsync(context, debounceDelay); return; }
        if (startRun) _ = RunAsync(context, generation);
    }

    /// <summary>Clears the trust question without running anything.</summary>
    public void Dismiss()
    {
        lock (_gate) _untrusted = null;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Marks the session closed; discards any in-flight run and ignores future requests.
    /// </summary>
    public void Close()
    {
        lock (_gate) { _closed = true; _generation++; }
    }

    private async Task DebounceAsync(StatusLineContext context, TimeSpan delay)
    {
        await _clock.DelayAsync(delay);
        StatusLineContext ctx;
        lock (_gate)
        {
            if (_closed || !_pending) return;
            _pending = false;
            ctx = _pendingContext ?? context;
            _pendingContext = null;
        }
        RequestRefresh(ctx, force: true);
    }

    private async Task RunAsync(StatusLineContext context, int generation)
    {
        try
        {
            var discovery = _discover();
            AppSnapshot snapshot;
            lock (_gate) snapshot = _getSnapshot();
            var (config, untrusted) = StatusLineTrust.Resolve(discovery, snapshot, _workspaceId);

            StatusLineResult? result = null;
            if (config is not null)
            {
                var ctx = context with
                {
                    OutputStyle = config.OutputStyle ?? discovery.User?.OutputStyle,
                    ThinkingEnabled = config.ThinkingEnabled ?? discovery.User?.ThinkingEnabled,
                };
                result = await _runner(config, ctx, CancellationToken.None);
            }
            Complete(generation, config, untrusted, result, context);
        }
        catch { Complete(generation, null, null, null, context); }
    }

    private void Complete(int generation, StatusLineConfig? config, StatusLineConfig? untrusted, StatusLineResult? result, StatusLineContext context)
    {
        bool rerun;
        StatusLineContext? pendingCtx;
        lock (_gate)
        {
            if (generation != _generation || _closed) return;
            _config = config;
            _untrusted = untrusted;
            _result = result;
            _updatedAt = _clock.UtcNow;
            _running = false;
            rerun = _pending;
            pendingCtx = _pendingContext;
            _pending = false;
            _pendingContext = null;
        }
        StateChanged?.Invoke();
        if (rerun) { lock (_gate) { if (_closed) return; } RequestRefresh(pendingCtx ?? context, force: false); }
    }
}

internal sealed class DefaultStatusLineClock : IStatusLineClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        => Task.Delay(delay, cancellationToken);
}
