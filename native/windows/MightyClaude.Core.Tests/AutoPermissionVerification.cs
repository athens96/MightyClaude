using System.Text.Json;
using MightyClaude.Core;

internal static class AutoPermissionVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("Unsupported Auto permission was accepted."); }
    internal static async Task Run()
    {
        var selected = new RunSettings(PermissionMode: "auto");
        var request = new StartRunRequest("pane", "workspace", "claude", "metadata fixture only", [], Settings: selected);
        request.Validate();
        Check(new RunSettings().PermissionMode == "manual", "The safe default changed.");
        Check(ProviderCatalog.NormalizeSettings("claude", selected) == selected, "Saved Auto was lost before discovery.");
        foreach (var provider in new[] { "codex", "gemini" })
        {
            Reject(() => (request with { Provider = provider }).Validate());
            Check(ProviderCatalog.NormalizeSettings(provider, selected).PermissionMode == "manual", "Auto leaked to another provider.");
        }
        Reject(() => (request with { Kind = "shell" }).Validate());
        foreach (var version in new string?[] { null, "unknown", "2.1.82", "2.1.270", "2.1.273-preview" })
        {
            var caps = ProviderCatalog.Capabilities("claude", version);
            Check(!caps.PermissionModes.Contains("auto"), "Unsupported CLI advertised Auto.");
            Check(ProviderCatalog.RemoteSettingsProblem(selected, caps) is not null, "Legacy host accepted Auto.");
        }
        foreach (var version in new[] { "2.1.271", "2.1.273 (Claude Code)", "3.0.0" })
        {
            var caps = ProviderCatalog.Capabilities("claude", version);
            Check(caps.PermissionModes.Contains("auto"), "Supported CLI omitted Auto.");
            Check(ProviderCatalog.RemoteSettingsProblem(selected, caps) is null, "Advertised Auto was refused.");
        }
        Check(ProviderCatalog.RemoteSettingsProblem(selected, null) is not null, "Missing capabilities accepted Auto.");
        var args = ProviderCatalog.Arguments(request, "/tmp/plugin");
        Check(args[args.IndexOf("--permission-mode") + 1] == "auto", "Auto was translated into another mode.");
        Check(args[args.IndexOf("--permission-prompts") + 1] == "none", "Windows silently claimed an approval channel.");
        Check(!args.Contains("bypassPermissions") && !args.Contains("--allowedTools"), "Auto weakened explicit policy.");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(selected, Wire.Json));
        Check(json.RootElement.EnumerateObject().Count() == 4, "New wire fields broke legacy shape.");
        Check(JsonSerializer.Deserialize<RunSettings>(json.RootElement, Wire.Json) == selected, "Auto wire value was lost.");
        var directory = Path.Combine(Path.GetTempPath(), "mighty-auto-" + Wire.Id()); Directory.CreateDirectory(directory);
        try
        {
            var store = new StateStore(directory); await store.LoadAsync();
            var workspace = store.ApproveLocal(directory);
            await store.SaveAsync(new AppSnapshot { Workspaces = [workspace], Sessions = [new RunSession { Id = "auto-pane", WorkspaceId = workspace.Id, Title = "Auto", Settings = selected }] });
            var restored = await new StateStore(directory).LoadAsync();
            Check(restored.Sessions.Single().Settings == selected, "Auto was lost after restart.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
