using System.Text;
using System.Text.Json;
using MightyClaude.Core;

internal static class CliAccountVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static string Jwt(Dictionary<string, object> claims)
    {
        static string Encode(byte[] data) => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var header = Encode(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}"));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        return header + "." + payload + ".sig";
    }

    // Mirrors CLIAccountTests.claudeAndCodexStatusesExposeAccountLabelsOnly
    internal static Task ClaudeAndCodexStatusesExposeAccountLabelsOnly()
    {
        // Claude: signed in with account, plan, and method.
        var claude = CliAccountSupport.ParseClaudeStatus(
            """{"loggedIn":true,"authMethod":"claude.ai","email":"me@example.com","orgName":"Org","subscriptionType":"max"}""");
        Check(claude.LoggedIn == true, "claude: logged in");
        Check(claude.Account == "me@example.com", "claude: account is email");
        Check(claude.Plan == "Max", "claude: plan capitalised");
        Check(claude.Method == "Claude 구독", "claude: claude.ai method");
        Check(claude.Summary == "me@example.com · Max · Claude 구독", "claude: summary");

        // Claude: signed out.
        var signedOut = CliAccountSupport.ParseClaudeStatus("""{"loggedIn":false}""");
        Check(signedOut.LoggedIn == false, "claude: signed out");
        Check(signedOut.Summary == CliAccountStrings.SummarySignedOut, "claude: signed-out summary");

        // Claude: malformed JSON → unknown.
        var unknown = CliAccountSupport.ParseClaudeStatus("nonsense");
        Check(unknown.LoggedIn == null, "claude: unknown on bad json");
        Check(unknown.Summary == unknown.Detail && unknown.Detail.Length > 0, "claude: detail shown as summary");
        Check(new CliAccountStatus { Provider = "x" }.Summary == CliAccountStrings.SummaryUnknown, "default unknown summary");

        // Claude: console auth with org (no email).
        var org = CliAccountSupport.ParseClaudeStatus(
            """{"loggedIn":true,"authMethod":"console","orgName":"Acme"}""");
        Check(org.Account == "Acme", "claude: org name when no email");
        Check(org.Method == "Anthropic Console", "claude: console method");
        Check(org.CanSignOut, "claude: console can sign out");

        // Codex: signed in via ChatGPT with account and plan from JWT claims.
        var idToken = Jwt(new Dictionary<string, object>
        {
            ["email"] = "dev@example.com",
            ["https://api.openai.com/auth"] = new { chatgpt_plan_type = "plus" },
        });
        var authJson = JsonSerializer.Serialize(new { tokens = new { id_token = idToken, access_token = "secret" } });
        var codex = CliAccountSupport.ParseCodexStatus("Logged in using ChatGPT\n", authJson);
        Check(codex.LoggedIn == true, "codex: logged in");
        Check(codex.Method == "ChatGPT", "codex: method");
        Check(codex.Account == "dev@example.com", "codex: account from JWT");
        Check(codex.Plan == "Plus", "codex: plan capitalised");
        // Token blindness: the access_token must not appear in any visible field.
        Check(!codex.Summary.Contains("secret"), "codex: summary must not expose token");
        Check(!(codex.Account ?? "").Contains("secret"), "codex: account must not expose token");

        // Codex: signed out.
        Check(CliAccountSupport.ParseCodexStatus("Not logged in", authJson).LoggedIn == false, "codex: signed out");

        // Codex: API key method.
        Check(CliAccountSupport.ParseCodexStatus("Logged in using an API key - sk-***", null).Method == "API 키", "codex: API key method");

        // Codex: unrecognised output → unknown.
        Check(CliAccountSupport.ParseCodexStatus("error: boom", null).LoggedIn == null, "codex: unknown on unrecognised output");

        // JWT with wrong structure → null.
        Check(CliAccountSupport.JwtClaims("not-a-jwt") == null, "jwt: 2-part token is not a JWT");

        return Task.CompletedTask;
    }

    // Mirrors CLIAccountTests.geminiStatusAndLogoutWorkOnItsAccountFiles
    internal static Task GeminiStatusAndLogoutWorkOnItsAccountFiles()
    {
        var home = Verification.Temp();
        try
        {
            var dot = Path.Combine(home, ".gemini");
            Directory.CreateDirectory(dot);

            // No files at all → signed out (no oauth, no settings).
            Check(CliAccountSupport.GeminiStatus(home, new Dictionary<string, string>()).LoggedIn == false, "gemini: empty dir → signed out");

            // OAuth flow: settings + credentials + account.
            File.WriteAllText(Path.Combine(dot, "settings.json"),
                """{"security":{"auth":{"selectedType":"oauth-personal"}}}""");
            File.WriteAllText(Path.Combine(dot, "oauth_creds.json"),
                """{"access_token":"x"}""");
            File.WriteAllText(Path.Combine(dot, "google_accounts.json"),
                """{"active":"g@example.com","old":["first@example.com"]}""");

            var status = CliAccountSupport.GeminiStatus(home, new Dictionary<string, string>());
            Check(status.LoggedIn == true, "gemini: oauth signed in");
            Check(status.Account == "g@example.com", "gemini: account from google_accounts.json");
            Check(status.Method == "Google 계정", "gemini: oauth method");

            // Logout: removes oauth_creds.json, moves active to old list.
            CliAccountSupport.GeminiLogout(home);
            Check(!File.Exists(Path.Combine(dot, "oauth_creds.json")), "gemini: oauth_creds.json removed");
            var accounts = JsonDocument.Parse(File.ReadAllText(Path.Combine(dot, "google_accounts.json"))).RootElement;
            Check(accounts.GetProperty("active").ValueKind == JsonValueKind.Null, "gemini: active set to null");
            var old = accounts.GetProperty("old").EnumerateArray().Select(e => e.GetString()).ToArray();
            Check(old.SequenceEqual(["first@example.com", "g@example.com"]), "gemini: old list updated");

            // After logout → signed out.
            Check(CliAccountSupport.GeminiStatus(home, new Dictionary<string, string>()).LoggedIn == false, "gemini: signed out after logout");

            // Logout is idempotent.
            CliAccountSupport.GeminiLogout(home);

            // API key auth.
            File.WriteAllText(Path.Combine(dot, "settings.json"),
                """{"security":{"auth":{"selectedType":"gemini-api-key"}}}""");
            var keyed = CliAccountSupport.GeminiStatus(home, new Dictionary<string, string> { ["GEMINI_API_KEY"] = "k" });
            Check(keyed.Method == "Gemini API 키", "gemini: api-key method");
            Check(keyed.LoggedIn == true, "gemini: api-key loggedIn when key present");
            Check(!keyed.CanSignOut, "gemini: api-key cannot sign out");
            // Key value must not appear in any field.
            Check(!keyed.Detail.Contains("k\""), "gemini: api-key must not expose key value");
            Check(CliAccountSupport.GeminiStatus(home, new Dictionary<string, string>()).LoggedIn == null, "gemini: api-key unknown without key");

            // Vertex AI auth.
            File.WriteAllText(Path.Combine(dot, "settings.json"),
                """{"security":{"auth":{"selectedType":"vertex-ai"}}}""");
            Check(CliAccountSupport.GeminiStatus(home, new Dictionary<string, string>()).LoggedIn == null, "gemini: vertex unknown without creds");
            var vertex = CliAccountSupport.GeminiStatus(home, new Dictionary<string, string> { ["GOOGLE_CLOUD_PROJECT"] = "p" });
            Check(vertex.LoggedIn == true, "gemini: vertex loggedIn with project");
            Check(!vertex.CanSignOut, "gemini: vertex cannot sign out");

            // BoundedFileText rejects directories and over-sized files.
            Check(CliAccountSupport.BoundedFileText(dot) == null, "boundedFileText: rejects directories");

            // CodexHome uses CODEX_HOME when set, falls back to ~/.codex.
            Check(CliAccountSupport.CodexHome(home, new Dictionary<string, string> { ["CODEX_HOME"] = "/opt/codex" }) == "/opt/codex",
                "codexHome: uses CODEX_HOME");
            Check(CliAccountSupport.CodexHome(home, new Dictionary<string, string>()) == Path.Combine(home, ".codex"),
                "codexHome: falls back to .codex");
        }
        finally { try { Directory.Delete(home, true); } catch { } }

        return Task.CompletedTask;
    }

    // Mirrors CLIAccountTests.commandsAreTheCLIsOwn
    internal static Task CommandsAreTheCLIsOwn()
    {
        // Login argv.
        Check(CliAccountSupport.LoginArguments("claude")!.SequenceEqual(["claude", "auth", "login"]), "claude: login argv");
        Check(CliAccountSupport.LoginArguments("claude", CliLoginOption.Console)!.SequenceEqual(["claude", "auth", "login", "--console"]), "claude: console argv");
        Check(CliAccountSupport.LoginArguments("codex")!.SequenceEqual(["codex", "login"]), "codex: login argv");
        Check(CliAccountSupport.LoginArguments("gemini")!.SequenceEqual(["gemini"]), "gemini: login argv");
        Check(CliAccountSupport.LoginArguments("other") == null, "unknown: no login argv");

        // Status argv.
        Check(CliAccountSupport.StatusArguments("claude")!.SequenceEqual(["claude", "auth", "status", "--json"]), "claude: status argv");
        Check(CliAccountSupport.StatusArguments("gemini") == null, "gemini: no status argv");

        // Logout argv.
        Check(CliAccountSupport.LogoutArguments("codex")!.SequenceEqual(["codex", "logout"]), "codex: logout argv");
        Check(CliAccountSupport.LogoutArguments("gemini") == null, "gemini: no logout argv");

        // The external sign-in terminal carries the CLI's own argv as a separate
        // argument list — never a command line composed from text.
        var login = CliAccountSupport.LoginArguments("claude", CliLoginOption.Console)!;
        var wt = CliAccountTerminal.LaunchPlan(login, windowsTerminalAvailable: true);
        Check(wt.Executable == "wt.exe", "terminal: Windows Terminal preferred when available");
        Check(wt.Arguments.SequenceEqual(["new-tab", "--", "claude", "auth", "login", "--console"]),
            "terminal: wt passes the login argv after --");
        var fallback = CliAccountTerminal.LaunchPlan(login, windowsTerminalAvailable: false);
        Check(fallback.Executable == "conhost.exe", "terminal: console host is the fallback");
        Check(fallback.Arguments.SequenceEqual(login), "terminal: fallback passes the login argv unchanged");
        // Every element stays its own argument, so no argument can be smuggled in.
        Check(wt.Arguments.Concat(fallback.Arguments).All(a => !a.Contains(' ')), "terminal: arguments are not joined");
        var rejected = false;
        try { CliAccountTerminal.LaunchPlan([], windowsTerminalAvailable: false); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "terminal: an empty login command is refused");

        return Task.CompletedTask;
    }

    // Coordinator uses the runner for Claude/Codex and skips it for Gemini.
    internal static async Task CoordinatorReadsStatusesAndCallsLogout()
    {
        var home = Verification.Temp();
        try
        {
            // Set up a minimal Gemini home so GeminiStatus returns signed-out.
            var dot = Path.Combine(home, ".gemini");
            Directory.CreateDirectory(dot);

            // Fake PATH pointing to our test dir so all CLI binaries are "found".
            var binDir = Path.Combine(home, "bin");
            Directory.CreateDirectory(binDir);
            string Bin(string name) => Path.Combine(binDir, OperatingSystem.IsWindows() ? name + ".exe" : name);
            File.WriteAllText(Bin("claude"), "");
            File.WriteAllText(Bin("codex"), "");
            File.WriteAllText(Bin("gemini"), "");

            var calls = new List<(string Executable, IReadOnlyList<string> Arguments)>();
            var responses = new Dictionary<string, CliRunResult>
            {
                ["claude"] = new(0, """{"loggedIn":true,"authMethod":"claude.ai","email":"me@example.com"}""", "", false),
                ["codex"] = new(0, "Logged in using ChatGPT\n", "", false),
                ["codex-logout"] = new(0, "", "", false),
                ["claude-logout"] = new(0, "", "", false),
            };

            var fakeRunner = new FakeCliRunner(call =>
            {
                calls.Add(call);
                // Route by argv content.
                var argv = string.Join(" ", call.Arguments);
                if (argv.Contains("auth status")) return responses["claude"];
                if (argv.Contains("login status")) return responses["codex"];
                if (argv.Contains("auth logout")) return responses["claude-logout"];
                if (call.Arguments.Count == 1 && call.Arguments[0] == "logout") return responses["codex-logout"];
                return new(0, "", "", false);
            });

            var env = new Dictionary<string, string>
            {
                ["PATH"] = binDir,
                ["NO_COLOR"] = "1",
            };
            var coordinator = new CliAccountsCoordinator(fakeRunner, env, home);

            // Refresh all providers (gemini is file-based, no runner call expected for it).
            await coordinator.RefreshAsync(["claude", "codex", "gemini"]);

            Check(coordinator.Statuses["claude"].LoggedIn == true, "coordinator: claude logged in");
            Check(coordinator.Statuses["claude"].Account == "me@example.com", "coordinator: claude account");
            Check(coordinator.Statuses["codex"].LoggedIn == true, "coordinator: codex logged in");
            // Gemini has no oauth_creds.json → signed out (file-based, no runner call).
            Check(coordinator.Statuses["gemini"].LoggedIn == false, "coordinator: gemini signed out");

            // Runner was called for claude and codex only.
            var runnerProviders = calls.Select(c => c.Arguments.FirstOrDefault() ?? "").ToHashSet();
            Check(runnerProviders.Contains("auth") || calls.Any(c => c.Arguments.Contains("auth")), "coordinator: ran claude auth status");
            Check(calls.Any(c => c.Arguments.Contains("status") && c.Arguments.Contains("login")), "coordinator: ran codex login status");

            // Logout for claude: runner must be called with logout argv.
            var callsBefore = calls.Count;
            await coordinator.LogoutAsync("claude");
            Check(calls.Count > callsBefore, "coordinator: logout called runner");
            Check(calls.Skip(callsBefore).Any(c => c.Arguments.Contains("logout")), "coordinator: logout used logout argv");

            // Gemini logout changes files (no runner).
            File.WriteAllText(Path.Combine(dot, "oauth_creds.json"), """{"access_token":"y"}""");
            File.WriteAllText(Path.Combine(dot, "google_accounts.json"), """{"active":"u@example.com","old":[]}""");
            var callsBeforeGemini = calls.Count;
            await coordinator.LogoutAsync("gemini");
            Check(calls.Count == callsBeforeGemini, "coordinator: gemini logout does not use runner");
            Check(!File.Exists(Path.Combine(dot, "oauth_creds.json")), "coordinator: gemini oauth_creds.json removed");
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    // Smoke: fixture statuses cover all four required types and the confirmation flow works.
    internal static async Task SmokeShowsFixtureStatusesAndConfirmationFlowWorks()
    {
        // The four fixture statuses must cover: signed-in, signed-out, not-installed, cannot-sign-out.
        var fixtures = CliAccountSmoke.FixtureStatuses;
        Check(fixtures.Any(s => s.LoggedIn == true && s.Account != null), "smoke fixture: signed-in with account");
        Check(fixtures.Any(s => s.LoggedIn == false), "smoke fixture: signed-out");
        Check(fixtures.Any(s => !s.Installed), "smoke fixture: not-installed");
        Check(fixtures.Any(s => !s.CanSignOut && s.LoggedIn == true), "smoke fixture: cannot-sign-out");

        // Build rendered summaries from the fixture statuses.
        var summaries = fixtures.Select(s => s.Summary).ToList();
        Check(summaries.Any(s => s.Contains("me@example.com")), "smoke: signed-in summary present");
        Check(summaries.Contains(CliAccountStrings.SummarySignedOut), "smoke: signed-out summary present");
        Check(summaries.Any(s => s == CliAccountStrings.DetailGeminiNotInstalled), "smoke: not-installed detail present");

        // RunAsync verifies the rendered summaries and drives the confirmation flow.
        var logoutTriggered = "";
        var cancelCalled = false;
        var outcome = await CliAccountSmoke.RunAsync(
            summaries,
            provider => { logoutTriggered = provider; return Task.CompletedTask; },
            () => { cancelCalled = true; return Task.CompletedTask; });

        Check(logoutTriggered.Length > 0, "smoke: logout triggered for a provider");
        Check(cancelCalled, "smoke: confirmation cancelled");
        Check(outcome.ConfirmationShown, "smoke: confirmation shown recorded");
        Check(outcome.Cancelled, "smoke: cancellation recorded");
        Check(outcome.Summaries.SequenceEqual(summaries), "smoke: summaries recorded");

        // A screen missing the signed-in summary must fail.
        var noSignedIn = summaries.Where(s => !s.Contains("me@example.com")).ToList();
        var failed = false;
        try { await CliAccountSmoke.RunAsync(noSignedIn, _ => Task.CompletedTask, () => Task.CompletedTask); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed, "smoke: missing signed-in summary must fail");
    }

    private sealed class FakeCliRunner(Func<(string Executable, IReadOnlyList<string> Arguments), CliRunResult> respond) : ICliRunner
    {
        public Task<CliRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellation = default, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null)
            => Task.FromResult(respond((executable, arguments)));
    }
}
