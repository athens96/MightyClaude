using System.Text.Json;
using MightyClaude.Core;

internal static class SettingsVerification
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Unsupported settings were accepted.");
    }

    internal static Task Run()
    {
        var legacy = JsonSerializer.Deserialize<RunSettings>("""{"effort":"high","permissionMode":"acceptEdits","maxTurns":null,"maxBudgetUsd":null}""", Wire.Json)!;
        Check(!legacy.FastMode && legacy.WebSearch == "default" && !legacy.NetworkAccess, "Legacy settings defaults changed.");
        using (var wire = JsonDocument.Parse(JsonSerializer.Serialize(new RunSettings(), Wire.Json)))
        {
            Check(wire.RootElement.EnumerateObject().Count() == 4, "Default settings must retain the strict legacy four-field shape.");
            Check(wire.RootElement.GetProperty("maxTurns").ValueKind == JsonValueKind.Null, "Nullable limits must remain explicit.");
        }
        var selected = new RunSettings("high", "acceptEdits", FastMode: true, WebSearch: "cached", NetworkAccess: true);
        Check(Wire.Clone(selected) == selected, "Selected options were lost in wire round trip.");
        var oldCaps = JsonSerializer.Deserialize<ProviderCapabilities>("""{"effort":true,"permissionModes":["manual","acceptEdits"],"maxTurns":false,"maxBudgetUsd":false,"resume":true}""", Wire.Json)!;
        Check(!oldCaps.FastMode && !oldCaps.WebSearch && !oldCaps.NetworkAccess, "Legacy hosts must not advertise new capabilities.");
        foreach (var settings in new[] { new RunSettings(FastMode: true), new RunSettings(WebSearch: "disabled"), new RunSettings(PermissionMode: "acceptEdits", NetworkAccess: true), new RunSettings(PermissionMode: "fullAccess") })
        {
            Check(ProviderCatalog.RemoteSettingsProblem(settings, oldCaps) is not null, "A legacy host accepted an unsupported extension.");
            Check(ProviderCatalog.RemoteSettingsProblem(settings, ProviderCatalog.Capabilities("codex")) is null, "Modern Codex capabilities rejected a supported option.");
        }

        var request = new StartRunRequest("pane", "workspace", "claude", "argument fixture only", Provider: "codex");
        var defaults = ProviderCatalog.Arguments(request, "/fixture-plugin");
        Check(defaults.Contains("service_tier=\"default\"") && defaults.Contains("features.fast_mode=false"), "Fast off must override a global Fast preference.");
        Check(defaults.Contains("sandbox_mode=\"read-only\"") && defaults.Contains("sandbox_workspace_write.network_access=false"), "Default permissions became more permissive.");
        Check(!defaults.Any(x => x.StartsWith("web_search=")), "Default web search must preserve CLI configuration.");
        foreach (var resume in new string?[] { null, "existing-session" })
        {
            var arguments = ProviderCatalog.Arguments(request with { Settings = selected, ResumeId = resume }, "/fixture-plugin");
            Check(arguments.Contains("features.fast_mode=true") && arguments.Contains("service_tier=\"fast\""), "Fast on must select the actual service tier.");
            Check(arguments.Contains("web_search=\"cached\"") && arguments.Contains("sandbox_workspace_write.network_access=true"), "Search/network options were not forwarded.");
            Check(arguments.Contains("approval_policy=\"never\"") && arguments.Contains("sandbox_mode=\"workspace-write\""), "Headless execution policy changed.");
            Check(arguments.Last() == "-", "The prompt must continue to use stdin.");
        }
        foreach (var mode in new[] { "disabled", "cached", "live" })
            Check(ProviderCatalog.Arguments(request with { Settings = new(WebSearch: mode) }, "/fixture-plugin").Contains($"web_search=\"{mode}\""), "Web search mode was not forwarded.");
        foreach (var provider in Wire.Providers)
        {
            var args = ProviderCatalog.Arguments(request with { Provider = provider, Settings = new(PermissionMode: "fullAccess") }, "/fixture-plugin");
            var expected = provider switch { "claude" => "bypassPermissions", "gemini" => "yolo", _ => "sandbox_mode=\"danger-full-access\"" };
            Check(args.Contains(expected), "Full access mapped to the wrong provider policy.");
        }
        foreach (var provider in new[] { "claude", "gemini" })
            foreach (var setting in new[] { new RunSettings(FastMode: true), new RunSettings(WebSearch: "live"), new RunSettings(PermissionMode: "acceptEdits", NetworkAccess: true) })
                Reject(() => (request with { Provider = provider, Settings = setting }).Validate());
        foreach (var mode in new[] { "manual", "fullAccess" })
            Reject(() => (request with { Settings = new(PermissionMode: mode, NetworkAccess: true) }).Validate());
        Reject(() => (request with { Settings = new(WebSearch: "unknown") }).Validate());
        Reject(() => (request with { Kind = "shell", Settings = new(FastMode: true) }).Validate());
        Check(!ProviderCatalog.NormalizeSettings("codex", selected with { PermissionMode = "manual" }).NetworkAccess, "Changing permissions retained a hidden network grant.");
        Check(!ProviderCatalog.NormalizeSettings("claude", selected).FastMode, "Switching providers retained Codex-only settings.");
        return Task.CompletedTask;
    }
}
