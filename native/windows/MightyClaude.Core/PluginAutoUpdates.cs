using System.Diagnostics;
using System.Text.Json;

namespace MightyClaude.Core;

public sealed partial class ClaudePluginReader
{
    public async Task<PluginAutoUpdateResult> UpdateInstalledAsync(Workspace workspace, TimeSpan budget, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            if (closed || cancellation.IsCancellationRequested) return PluginAutoUpdateResult.Cancelled;
            if (operating || running is { IsCompleted: false }) return PluginAutoUpdateResult.Busy;
            operating = true;
        }
        try
        {
            var cwd = LocalDirectory(workspace); var (executable, version) = await CommandAsync(cwd, cancellation);
            var snapshot = await ListAsync(executable, cwd, version, cancellation);
            if (snapshot.Status != ClaudePluginStatus.Ready) return new(snapshot.Status is "missing" or "unsupported" ? "skipped" : "failed", snapshot.Detail);
            var targets = snapshot.Installed.Where(p => p.Scope == "user" && p.Name != "mighty-bridge" && ClaudePluginSupport.PluginParts(p.PluginId) is not null).DistinctBy(p => p.PluginId).ToArray();
            var updated = new List<string>(); var approval = new List<string>(); var failed = new List<string>(); var postponed = 0; var elapsed = Stopwatch.StartNew();
            foreach (var plugin in targets)
            {
                cancellation.ThrowIfCancellationRequested();
                if (elapsed.Elapsed >= budget) { postponed++; continue; }
                // No --yes/-y: a marketplace's changed command requires the
                // user's later review in the plugin browser, never auto-consent.
                var result = await runner.RunAsync(executable, ["plugin", "update", plugin.PluginId, "--scope", "user", "--json"], OperationTimeout, cancellation, environment, cwd);
                cancellation.ThrowIfCancellationRequested();
                switch (PluginAutoUpdateResult.ClaudeOutcome(result))
                { case "updated": updated.Add(plugin.PluginId); break; case "needsApproval": approval.Add(plugin.PluginId); break; default: failed.Add(plugin.PluginId); break; }
            }
            var details = new List<string> { targets.Length == 0 ? Locale.Get("pluginAutoUpdate.none") : PluginAutoUpdateResult.Count("pluginAutoUpdate.checkedTemplate", targets.Length) };
            if (approval.Count > 0) details.Add(PluginAutoUpdateResult.Names("pluginAutoUpdate.needsApprovalTemplate", approval));
            if (failed.Count > 0) details.Add(PluginAutoUpdateResult.Names("pluginAutoUpdate.failedTemplate", failed));
            if (postponed > 0) details.Add(PluginAutoUpdateResult.Count("pluginAutoUpdate.postponedTemplate", postponed));
            return new(failed.Count == 0 ? "succeeded" : "failed", string.Join(" ", details), updated, approval, failed);
        }
        catch (OperationCanceledException) { return PluginAutoUpdateResult.Cancelled; }
        catch (PluginFailure ex) { return new(ex.Status is "missing" or "unsupported" ? "skipped" : "failed", ex.Detail); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return PluginAutoUpdateResult.Failure; }
        finally { lock (gate) operating = false; }
    }
}

public sealed partial class CodexPluginReader
{
    public async Task<PluginAutoUpdateResult> UpgradeMarketplacesAsync(Workspace workspace, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            if (closed || cancellation.IsCancellationRequested) return PluginAutoUpdateResult.Cancelled;
            if (operating || running is { IsCompleted: false }) return PluginAutoUpdateResult.Busy;
            operating = true;
        }
        try
        {
            var cwd = LocalDirectory(workspace); var (executable, version) = await CommandAsync(cwd, cancellation);
            var snapshot = await ListAsync(executable, cwd, version, cancellation); cancellation.ThrowIfCancellationRequested();
            if (snapshot.Status != ClaudePluginStatus.Ready) return new(snapshot.Status is "missing" or "unsupported" ? "skipped" : "failed", snapshot.Detail);
            if (!snapshot.Marketplaces.Any(m => m.SourceKind == "git")) return new("skipped", Locale.Get("pluginAutoUpdate.noGitMarketplaces"));
            var result = await runner.RunAsync(executable, ["plugin", "marketplace", "upgrade", "--json"], OperationTimeout, cancellation, environment, cwd); cancellation.ThrowIfCancellationRequested();
            if (result.ExitCode != 0 || result.TimedOut || System.Text.Encoding.UTF8.GetByteCount(result.Output) > 1_048_576) return PluginAutoUpdateResult.Failure;
            var selected = new List<string>(); var errors = 0;
            try
            {
                using var doc = JsonDocument.Parse(result.Output);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("selectedMarketplaces", out var rows) && rows.ValueKind == JsonValueKind.Array)
                        selected.AddRange(rows.EnumerateArray().Where(row => row.ValueKind == JsonValueKind.String).Select(row => Wire.Clean(row.GetString(), 160)).Take(128));
                    if (doc.RootElement.TryGetProperty("errors", out var values) && values.ValueKind == JsonValueKind.Array) errors = values.GetArrayLength();
                }
            }
            catch (JsonException) { /* CLI exit code is authoritative; newer versions may omit JSON detail. */ }
            return errors > 0 ? new("failed", PluginAutoUpdateResult.Count("pluginAutoUpdate.marketplaceErrorsTemplate", errors), Failed: selected)
                : new("succeeded", selected.Count > 0 ? PluginAutoUpdateResult.Count("pluginAutoUpdate.marketplacesTemplate", selected.Count) : Locale.Get("pluginAutoUpdate.marketplacesUpgraded"), selected);
        }
        catch (OperationCanceledException) { return PluginAutoUpdateResult.Cancelled; }
        catch (PluginFailure ex) { return new(ex.Status is "missing" or "unsupported" ? "skipped" : "failed", ex.Detail); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return PluginAutoUpdateResult.Failure; }
        finally { lock (gate) operating = false; }
    }
}
