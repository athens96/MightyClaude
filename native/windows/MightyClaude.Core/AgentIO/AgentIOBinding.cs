using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

public static class AgentIOPaneKind
{
    public const string Terminal = "agent-terminal", Browser = "agent-browser";
    public static bool IsAgentIO(string kind) => kind is Terminal or Browser;
    public static string PaneId(string owner, string kind) => kind + ":" + owner;
}

// Intentionally a class with a redacted ToString, never part of saved state.
public sealed class AgentIOBinding(string paneId, string workspaceId, string workspacePath, string provider, string pipeName, string executable)
{
    private readonly CancellationTokenSource lifetime = new();
    public CancellationToken Revoked => lifetime.Token;
    internal void Revoke() => lifetime.Cancel();
    public const string ServerName = "mighty-terminal", TokenKey = "MIGHTY_PANE_TOKEN", PipeKey = "MIGHTY_AGENT_IO_PIPE";
    public string PaneId { get; } = paneId;
    public string WorkspaceId { get; } = workspaceId;
    public string WorkspacePath { get; } = workspacePath;
    public string Provider { get; } = provider;
    public string PipeName { get; } = pipeName;
    public string Executable { get; } = executable;
    internal string Token { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string> { [TokenKey] = Token, [PipeKey] = PipeName };
    public override string ToString() => $"AgentIOBinding({PaneId}, token=<redacted>)";
    public string Redact(string value) => value.Replace(Token, "<pane-token-redacted>", StringComparison.Ordinal);
    public void AppendArguments(List<string> arguments)
    {
        var commandArgs = new[] { "--agent-io-mcp" };
        if (Provider == "claude")
        {
            arguments.AddRange(["--mcp-config", JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { [ServerName] = new { type = "stdio", command = Executable, args = commandArgs } } }, Wire.Json)]);
        }
        else if (Provider == "codex")
        {
            foreach (var pair in new Dictionary<string, object> { ["command"] = Executable, ["args"] = commandArgs, ["env_vars"] = new[] { TokenKey, PipeKey } })
                arguments.AddRange(["-c", $"mcp_servers.{ServerName}.{pair.Key}=" + JsonSerializer.Serialize(pair.Value, Wire.Json)]);
            // Server/tool descriptions carry routing instructions without overriding
            // developer_instructions from the user's Codex configuration.
        }
    }
}

public sealed class AgentIOBindings
{
    private readonly object gate = new();
    private readonly Dictionary<string, AgentIOBinding> bindings = [];
    public AgentIOBinding Bind(string pane, Workspace workspace, string provider, string pipe, string executable)
    {
        if (!Wire.Identifier(pane) || !Wire.Identifier(workspace.Id) || !Path.IsPathFullyQualified(workspace.Path) || !Path.IsPathFullyQualified(executable) || provider is not ("claude" or "codex")) throw new ArgumentException("Invalid pane binding.");
        var value = new AgentIOBinding(pane, workspace.Id, workspace.Path, provider, pipe, executable);
        AgentIOBinding? previous;
        lock (gate) { bindings.TryGetValue(pane, out previous); bindings[pane] = value; }
        previous?.Revoke();
        return value;
    }
    public AgentIOBinding? Resolve(string token)
    {
        if (token.Length != 64) return null;
        var bytes = Encoding.ASCII.GetBytes(token);
        lock (gate) return bindings.Values.FirstOrDefault(value => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(value.Token), bytes));
    }
    public bool IsCurrent(AgentIOBinding value) { lock (gate) return !value.Revoked.IsCancellationRequested && bindings.GetValueOrDefault(value.PaneId) == value; }
    public void Revoke(string pane)
    {
        AgentIOBinding? previous; lock (gate) bindings.Remove(pane, out previous);
        previous?.Revoke();
    }
    public void Clear()
    {
        AgentIOBinding[] previous; lock (gate) { previous = bindings.Values.ToArray(); bindings.Clear(); }
        foreach (var value in previous) value.Revoke();
    }
}

public sealed record AgentIOTool(string Name, string Argument, string Description);
public static class AgentIOTools
{
    public const string Guidance = "Use run_in_terminal for user-visible or long-running commands, including sign-ins and interactive prompts so the user can answer in their terminal. Keep short internal file reads and checks in built-in tools. Use open_url for web pages; the user chooses the browser. Do not drive sign-in prompts by sending keystrokes.";
    public static readonly IReadOnlyList<AgentIOTool> All = [
        new("run_in_terminal", "command", Guidance + " Run a PowerShell command in the workspace. Returns output and exit code within 12 seconds, otherwise a running handle."),
        new("read_latest_output", "handle", "Read up to 64 KiB of new output from this pane's terminal command, including user input. Returns running/done, exit code, and dropped/more flags."),
        new("stop", "handle", "Stop this pane's terminal command: Ctrl+C first, then terminate its Windows job after three seconds. Returns the final status."),
        new("open_url", "url", "Open an http/https page (including localhost) in the app or system browser as chosen by the user for this workspace. Other schemes are refused."),
    ];
    public static string Validate(string tool, JsonElement arguments)
    {
        var spec = All.FirstOrDefault(t => t.Name == tool) ?? throw new ArgumentException("Unknown tool.");
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Count() != 1 || arguments.Text(spec.Argument) is not { } value || string.IsNullOrWhiteSpace(value) || value.Contains('\0')) throw new ArgumentException("Exactly one non-empty string argument is required.");
        var max = tool == "run_in_terminal" ? 65_536 : tool == "open_url" ? 8192 : 128;
        if (Encoding.UTF8.GetByteCount(value) > max) throw new ArgumentException("Tool argument exceeds its limit.");
        if (tool == "open_url" && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || string.IsNullOrEmpty(uri.Host) || value.Any(char.IsControl))) throw new ArgumentException("Only http/https URLs without credentials are accepted.");
        return value;
    }
}
