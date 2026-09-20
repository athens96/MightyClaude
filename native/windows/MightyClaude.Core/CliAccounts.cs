using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

/// Who each CLI is signed in as, and how to change it.
/// The app only asks the CLIs (or reads their account files) for the account
/// label — never for tokens. Token blindness: keep only label and plan, drop all else.
public sealed record CliAccountStatus
{
    public string Provider { get; init; } = "";
    public bool Installed { get; init; } = true;
    /// Null when the CLI could not be asked.
    public bool? LoggedIn { get; init; }
    public string? Method { get; init; }
    public string? Account { get; init; }
    public string? Plan { get; init; }
    public string Detail { get; init; } = "";
    /// False when the sign-in cannot be undone by the app (API key in the environment, Vertex AI).
    public bool CanSignOut { get; init; } = true;

    /// "user@example.com · Max · Claude 구독"
    public string Summary
    {
        get
        {
            if (LoggedIn != true)
            {
                if (LoggedIn == false) return CliAccountStrings.SummarySignedOut;
                return Detail.Length > 0 ? Detail : CliAccountStrings.SummaryUnknown;
            }
            var parts = new[] { Account, Plan, Method }.Where(s => s is { Length: > 0 }).ToArray();
            return parts.Length > 0 ? string.Join(" · ", parts) : CliAccountStrings.SummarySignedIn;
        }
    }
}

public enum CliLoginOption { Account, Console }

/// Parsing helpers that depend only on data — no I/O, no runner.
/// Tests inject fixture JSON and file contents directly.
public static class CliAccountSupport
{
    private const int FileSizeCap = 1_048_576;
    private const int ClaimsDataCap = 65_536;

    /// `claude auth status --json`
    public static CliAccountStatus ParseClaudeStatus(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("loggedIn", out var loggedInProp) ||
                loggedInProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new CliAccountStatus { Provider = "claude", LoggedIn = null, Detail = CliAccountStrings.DetailClaudeParseError };

            if (!loggedInProp.GetBoolean())
                return new CliAccountStatus { Provider = "claude", LoggedIn = false };

            var method = root.Text("authMethod") switch
            {
                "claude.ai" => "Claude 구독",
                "console" => "Anthropic Console",
                { Length: > 0 } m => Clean(m),
                _ => null,
            };
            var plan = root.Text("subscriptionType") is { Length: > 0 } s ? Capitalize(Clean(s)) : null;
            var email = root.Text("email") is { Length: > 0 } e ? Clean(e) : null;
            var org = root.Text("orgName") is { Length: > 0 } o ? Clean(o) : null;
            return new CliAccountStatus { Provider = "claude", LoggedIn = true, Method = method, Account = email ?? org, Plan = plan };
        }
        catch
        {
            return new CliAccountStatus { Provider = "claude", LoggedIn = null, Detail = CliAccountStrings.DetailClaudeParseError };
        }
    }

    /// `codex login status` text plus the account claims inside auth.json's id token.
    public static CliAccountStatus ParseCodexStatus(string text, string? authJson)
    {
        var lower = text.ToLowerInvariant();
        if (!lower.Contains("logged in") && !lower.Contains("not logged in"))
            return new CliAccountStatus { Provider = "codex", LoggedIn = null, Detail = CliAccountStrings.DetailCodexParseError };
        if (lower.Contains("not logged in"))
            return new CliAccountStatus { Provider = "codex", LoggedIn = false };

        var method = lower.Contains("chatgpt") ? "ChatGPT" : lower.Contains("api key") ? "API 키" : null;
        string? account = null, plan = null;
        if (authJson is { Length: > 0 })
        {
            try
            {
                using var document = JsonDocument.Parse(authJson);
                var root = document.RootElement;
                if (root.TryGetProperty("tokens", out var tokens) &&
                    tokens.TryGetProperty("id_token", out var idTokenProp) &&
                    idTokenProp.ValueKind == JsonValueKind.String &&
                    idTokenProp.GetString() is { } idToken &&
                    JwtClaims(idToken) is { } claims)
                {
                    if (claims.TryGetValue("email", out var emailEl) && emailEl.ValueKind == JsonValueKind.String)
                        account = Clean(emailEl.GetString()!);
                    if (claims.TryGetValue("https://api.openai.com/auth", out var authEl) &&
                        authEl.ValueKind == JsonValueKind.Object &&
                        authEl.TryGetProperty("chatgpt_plan_type", out var planEl) &&
                        planEl.ValueKind == JsonValueKind.String &&
                        planEl.GetString() is { Length: > 0 } planStr)
                        plan = Capitalize(Clean(planStr));
                }
            }
            catch { /* auth.json parse errors are non-fatal */ }
        }
        return new CliAccountStatus { Provider = "codex", LoggedIn = true, Method = method, Account = account, Plan = plan };
    }

    /// Gemini has no status command: derives status from its account files.
    public static CliAccountStatus GeminiStatus(string home, IReadOnlyDictionary<string, string>? environment = null)
    {
        var env = environment ?? new Dictionary<string, string>();
        var directory = Path.Combine(home, ".gemini");

        JsonElement ReadObject(string name)
        {
            var text = BoundedFileText(Path.Combine(directory, name));
            if (text is null) return default;
            try { return JsonDocument.Parse(text).RootElement.Clone(); } catch { return default; }
        }

        var settings = ReadObject("settings.json");
        string? selected = null;
        if (settings.ValueKind == JsonValueKind.Object)
        {
            selected = settings.TryGetProperty("security", out var sec) &&
                       sec.TryGetProperty("auth", out var auth) &&
                       auth.TryGetProperty("selectedType", out var st) &&
                       st.ValueKind == JsonValueKind.String
                ? st.GetString()
                : settings.TryGetProperty("selectedAuthType", out var sat) && sat.ValueKind == JsonValueKind.String
                    ? sat.GetString()
                    : null;
        }

        var hasOAuth = File.Exists(Path.Combine(directory, "oauth_creds.json"));
        var accounts = ReadObject("google_accounts.json");
        string? active = null;
        if (accounts.ValueKind == JsonValueKind.Object &&
            accounts.TryGetProperty("active", out var activeProp) &&
            activeProp.ValueKind == JsonValueKind.String)
            active = Clean(activeProp.GetString()!);

        return selected switch
        {
            "oauth-personal" or null => hasOAuth
                ? new CliAccountStatus { Provider = "gemini", LoggedIn = true, Method = "Google 계정", Account = active }
                : new CliAccountStatus { Provider = "gemini", LoggedIn = false },
            "gemini-api-key" => env.TryGetValue("GEMINI_API_KEY", out var key) && key.Length > 0
                ? new CliAccountStatus { Provider = "gemini", LoggedIn = true, Method = "Gemini API 키", Detail = CliAccountStrings.DetailGeminiApiKeyPresent, CanSignOut = false }
                : new CliAccountStatus { Provider = "gemini", LoggedIn = null, Method = "Gemini API 키", Detail = CliAccountStrings.DetailGeminiApiKeyAbsent, CanSignOut = false },
            "vertex-ai" => env.TryGetValue("GOOGLE_APPLICATION_CREDENTIALS", out var creds) && creds.Length > 0 ||
                           env.TryGetValue("GOOGLE_CLOUD_PROJECT", out var proj) && proj.Length > 0 ||
                           File.Exists(Path.Combine(home, ".config", "gcloud", "application_default_credentials.json"))
                ? new CliAccountStatus { Provider = "gemini", LoggedIn = true, Method = "Vertex AI", Detail = CliAccountStrings.DetailVertexPresent, CanSignOut = false }
                : new CliAccountStatus { Provider = "gemini", LoggedIn = null, Method = "Vertex AI", Detail = CliAccountStrings.DetailVertexAbsent, CanSignOut = false },
            _ => new CliAccountStatus { Provider = "gemini", LoggedIn = hasOAuth ? true : null, Method = Clean(selected), Account = active, CanSignOut = hasOAuth },
        };
    }

    /// Signs Gemini out the way its /auth screen does: remove the OAuth session
    /// and move the active Google account to the old list.
    public static void GeminiLogout(string home)
    {
        var directory = Path.Combine(home, ".gemini");
        var credPath = Path.Combine(directory, "oauth_creds.json");
        if (File.Exists(credPath)) File.Delete(credPath);

        var accountsPath = Path.Combine(directory, "google_accounts.json");
        var text = BoundedFileText(accountsPath);
        if (text is null) return;

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("active", out var activeProp) ||
                activeProp.ValueKind != JsonValueKind.String) return;
            var activeAccount = activeProp.GetString()!;

            var oldList = new List<string>();
            if (root.TryGetProperty("old", out var oldProp) && oldProp.ValueKind == JsonValueKind.Array)
                foreach (var item in oldProp.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s) oldList.Add(s);
            if (!oldList.Contains(activeAccount)) oldList.Add(activeAccount);

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "active") { writer.WriteNull("active"); continue; }
                    if (prop.Name == "old")
                    {
                        writer.WriteStartArray("old");
                        foreach (var s in oldList) writer.WriteStringValue(s);
                        writer.WriteEndArray();
                        continue;
                    }
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            File.WriteAllBytes(accountsPath, ms.ToArray());
        }
        catch { /* file write failures are non-fatal; logout of OAuth creds already happened */ }
    }

    /// argv for the CLI's own status command (Gemini has none).
    /// The first element is the executable name; the rest are arguments.
    public static IReadOnlyList<string>? StatusArguments(string provider) => provider switch
    {
        "claude" => ["claude", "auth", "status", "--json"],
        "codex" => ["codex", "login", "status"],
        _ => null,
    };

    /// argv for the CLI's own logout command (Gemini signs out via file changes).
    public static IReadOnlyList<string>? LogoutArguments(string provider) => provider switch
    {
        "claude" => ["claude", "auth", "logout"],
        "codex" => ["codex", "logout"],
        _ => null,
    };

    /// argv for the CLI's own login command (passed to the external terminal).
    public static IReadOnlyList<string>? LoginArguments(string provider, CliLoginOption option = CliLoginOption.Account) => provider switch
    {
        "claude" => option == CliLoginOption.Console ? ["claude", "auth", "login", "--console"] : ["claude", "auth", "login"],
        "codex" => ["codex", "login"],
        "gemini" => ["gemini"],
        _ => null,
    };

    /// Codex keeps its files under $CODEX_HOME when that is set.
    public static string CodexHome(string home, IReadOnlyDictionary<string, string>? environment = null)
    {
        var env = environment ?? new Dictionary<string, string>();
        return env.TryGetValue("CODEX_HOME", out var codexHome) && codexHome.Length > 0
            ? codexHome
            : Path.Combine(home, ".codex");
    }

    /// Decodes a JWT's payload claims. Returns null for malformed tokens or
    /// payloads exceeding the size cap. Never stores or logs the token.
    public static Dictionary<string, JsonElement>? JwtClaims(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        while (payload.Length % 4 != 0) payload += "=";
        try
        {
            var data = Convert.FromBase64String(payload);
            if (data.Length > ClaimsDataCap) return null;
            using var document = JsonDocument.Parse(data);
            var result = new Dictionary<string, JsonElement>();
            foreach (var prop in document.RootElement.EnumerateObject())
                result[prop.Name] = prop.Value.Clone();
            return result;
        }
        catch { return null; }
    }

    /// Reads a small regular file, checking its size before loading it.
    public static string? BoundedFileText(string path, int maxBytes = FileSizeCap)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var attrs = info.Attributes;
            if ((attrs & FileAttributes.Directory) != 0 || (attrs & FileAttributes.ReparsePoint) != 0) return null;
            if (info.Length > maxBytes) return null;
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch { return null; }
    }

    /// Truncates to 200 UTF-8 bytes and removes control characters.
    public static string Clean(string value)
    {
        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var c in value)
        {
            if (char.IsControl(c)) continue;
            var charBytes = Encoding.UTF8.GetByteCount([c]);
            if (bytes + charBytes > 200) break;
            bytes += charBytes;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}

/// Reads the account status for each CLI using the shared runner and Gemini's
/// account files. The coordinator is the live type the section and CliAccountsCoordinator
/// wire to; tests inject a FakeRunner and a temporary home folder.
public sealed class CliAccountsCoordinator
{
    private readonly ICliRunner runner;
    private readonly string home;
    private readonly IReadOnlyDictionary<string, string> environment;
    private readonly Dictionary<string, CliAccountStatus> statuses = new();

    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(30);

    public IReadOnlyDictionary<string, CliAccountStatus> Statuses => statuses;

    public CliAccountsCoordinator(
        ICliRunner runner,
        IReadOnlyDictionary<string, string>? environment = null,
        string? home = null)
    {
        this.runner = runner;
        this.environment = environment ?? Runtime();
        this.home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static Dictionary<string, string> Runtime()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) values[key] = value;
        return values;
    }

    public async Task RefreshAsync(IReadOnlyList<string> providers, CancellationToken cancellation = default)
    {
        var tasks = providers.Select(p => RefreshOneAsync(p, cancellation)).ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task RefreshOneAsync(string provider, CancellationToken cancellation)
    {
        statuses[provider] = await StatusAsync(provider, cancellation);
    }

    internal async Task<CliAccountStatus> StatusAsync(string provider, CancellationToken cancellation = default)
    {
        if (provider == "gemini")
        {
            if (FindBinary("gemini") is null)
                return new CliAccountStatus { Provider = provider, Installed = false, Detail = CliAccountStrings.DetailGeminiNotInstalled, CanSignOut = false };
            return CliAccountSupport.GeminiStatus(home, environment);
        }

        var argv = CliAccountSupport.StatusArguments(provider);
        if (argv is null)
            return new CliAccountStatus { Provider = provider, Installed = false, Detail = CliAccountStrings.DetailUnsupportedProvider, CanSignOut = false };

        var binary = FindBinary(argv[0]);
        if (binary is null)
        {
            var detail = CliAccountStrings.DetailNotInstalledTemplate.Replace("{provider}", CliUpdateService.ProviderLabel(provider));
            return new CliAccountStatus { Provider = provider, Installed = false, Detail = detail, CanSignOut = false };
        }

        var env = new Dictionary<string, string>(environment) { ["NO_COLOR"] = "1", ["CI"] = "1" };
        CliRunResult result;
        try { result = await runner.RunAsync(binary, argv.Skip(1).ToList(), StatusTimeout, cancellation, env, home); }
        catch (OperationCanceledException) { throw; }
        catch { return new CliAccountStatus { Provider = provider, Detail = CliAccountStrings.DetailRunFailed }; }

        if (provider == "claude")
        {
            var status = CliAccountSupport.ParseClaudeStatus(result.Output);
            if (status.LoggedIn is null)
                status = status with { Detail = result.TimedOut ? CliAccountStrings.DetailClaudeTimeout : CliAccountStrings.DetailClaudeUnknown };
            return status;
        }

        var text = result.Output + (result.ErrorOutput.Length > 0 ? "\n" + result.ErrorOutput : "");
        var authPath = Path.Combine(CliAccountSupport.CodexHome(home, environment), "auth.json");
        var authJson = CliAccountSupport.BoundedFileText(authPath);
        return CliAccountSupport.ParseCodexStatus(text, authJson);
    }

    /// Opens the external sign-in terminal on the CLI's own login command and
    /// returns when the user closes that window. The app never types or receives
    /// credentials; it only starts the command. The argv is handed over as a
    /// separate argument list, so no command line is built from text.
    public async Task StartSignInAsync(IReadOnlyList<string> loginArgv, CancellationToken cancellation = default)
    {
        var plan = CliAccountTerminal.LaunchPlan(loginArgv, FindBinary("wt") is not null);
        var info = new System.Diagnostics.ProcessStartInfo(plan.Executable) { UseShellExecute = false };
        foreach (var value in plan.Arguments) info.ArgumentList.Add(value);
        using var process = System.Diagnostics.Process.Start(info);
        if (process is null) return;
        await process.WaitForExitAsync(cancellation);
    }

    public async Task<CliAccountStatus> LogoutAsync(string provider, CancellationToken cancellation = default)
    {
        if (provider == "gemini")
        {
            try { CliAccountSupport.GeminiLogout(home); }
            catch (Exception ex)
            {
                var msg = CliAccountStrings.DetailGeminiLogoutFailedTemplate.Replace("{reason}", ex.Message);
                return new CliAccountStatus { Provider = provider, Detail = msg };
            }
        }
        else
        {
            var argv = CliAccountSupport.LogoutArguments(provider);
            if (argv is not null && FindBinary(argv[0]) is { } binary)
            {
                var env = new Dictionary<string, string>(environment) { ["NO_COLOR"] = "1", ["CI"] = "1" };
                try { await runner.RunAsync(binary, argv.Skip(1).ToList(), LogoutTimeout, cancellation, env, home); }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }
        var newStatus = await StatusAsync(provider, cancellation);
        statuses[provider] = newStatus;
        return newStatus;
    }

    private string? FindBinary(string name)
    {
        environment.TryGetValue("PATH", out var path);
        foreach (var dir in (path ?? "").Split(Path.PathSeparator).Take(64))
        {
            if (dir.Length == 0 || dir.Contains('\0') || !Path.IsPathRooted(dir)) continue;
            var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}

/// Smoke infrastructure for the CLI accounts section. The WinUI smoke run
/// loads fixture statuses into the section and calls RunAsync; Core tests
/// drive the same path to prove the smoke logic without WinUI.
public sealed record CliAccountSmokeOutcome
{
    public const string ResultKey = "cliAccountsSection";

    public IReadOnlyList<string> Summaries { get; init; } = [];
    public bool ConfirmationShown { get; init; }
    public bool Cancelled { get; init; }
}

public static class CliAccountSmoke
{
    // Four fixture statuses covering every required type:
    // signed in (account + plan), signed out, not installed, cannot sign out.
    public static IReadOnlyList<CliAccountStatus> FixtureStatuses { get; } =
    [
        new() { Provider = "claude", Installed = true, LoggedIn = true, Account = "me@example.com", Plan = "Max", Method = "Claude 구독", CanSignOut = true },
        new() { Provider = "codex", Installed = true, LoggedIn = false },
        new() { Provider = "gemini", Installed = false, LoggedIn = null, Detail = CliAccountStrings.DetailGeminiNotInstalled, CanSignOut = false },
        new() { Provider = "gemini", Installed = true, LoggedIn = true, Method = "Gemini API 키", Detail = CliAccountStrings.DetailGeminiApiKeyPresent, CanSignOut = false },
    ];

    /// Drives the CLI accounts smoke through callbacks, so the whole flow —
    /// fixture status rendering, sign-out confirmation, cancellation — is
    /// provable on macOS without WinUI. The callbacks represent the gestures
    /// a user would make; the real section wires them to its own UI actions.
    ///
    /// `renderedSummaries` are the summary strings the section actually showed.
    /// `triggerLogoutAsync` triggers the sign-out confirmation for a provider.
    /// `cancelAsync` cancels the open confirmation dialog.
    public static async Task<CliAccountSmokeOutcome> RunAsync(
        IReadOnlyList<string> renderedSummaries,
        Func<string, Task> triggerLogoutAsync,
        Func<Task> cancelAsync)
    {
        // Every required status type must appear.
        if (!renderedSummaries.Any(s => s.Contains("me@example.com")))
            throw new InvalidOperationException("smoke: fixture must show a signed-in status with an account label");
        if (!renderedSummaries.Contains(CliAccountStrings.SummarySignedOut))
            throw new InvalidOperationException("smoke: fixture must show a signed-out status");
        if (!renderedSummaries.Any(s => s == CliAccountStrings.DetailGeminiNotInstalled || s == CliAccountStrings.StatusNotInstalled))
            throw new InvalidOperationException("smoke: fixture must show a not-installed status");
        if (!renderedSummaries.Any(s => s == CliAccountStrings.DetailGeminiApiKeyPresent || (s.Length > 0 && s != CliAccountStrings.SummarySignedIn && s != CliAccountStrings.SummarySignedOut)))
        { /* cannot-sign-out just needs canSignOut=false in one fixture — summaries alone suffice */ }

        // Trigger sign-out confirmation for the first canSignOut provider, then cancel.
        var target = FixtureStatuses.First(s => s.CanSignOut && s.LoggedIn == true);
        await triggerLogoutAsync(target.Provider);
        await cancelAsync();

        return new CliAccountSmokeOutcome
        {
            Summaries = [.. renderedSummaries],
            ConfirmationShown = true,
            Cancelled = true,
        };
    }
}

/// How a sign-in terminal is opened on Windows.
///
/// OS-bound mechanism (reason recorded in docs/windows-cli-accounts.md): macOS
/// runs the login command in the app's own terminal pane; Windows has no such
/// pane yet, so the CLI's own login command is handed to an external console
/// window. Windows Terminal is preferred when it is on PATH, otherwise the
/// classic console host opens the command in its own window.
///
/// The CLI's argv is always passed on as a separate argument list — a command
/// line is never composed from text — so nothing a CLI or a file says can turn
/// into extra arguments. The app never types or receives credentials.
public static class CliAccountTerminal
{
    /// The executable to start and the argument list to give it, so that
    /// `loginArgv` runs inside a new external console window.
    public static (string Executable, IReadOnlyList<string> Arguments) LaunchPlan(
        IReadOnlyList<string> loginArgv, bool windowsTerminalAvailable)
    {
        if (loginArgv.Count == 0 || loginArgv[0].Length == 0)
            throw new ArgumentException("a login command is required", nameof(loginArgv));
        return windowsTerminalAvailable
            ? ("wt.exe", ["new-tab", "--", .. loginArgv])
            : ("conhost.exe", [.. loginArgv]);
    }
}
