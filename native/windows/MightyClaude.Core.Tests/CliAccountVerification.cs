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
        Check(claude.Method == "Claude 구독" && claude.MethodId == CliAccountMethod.ClaudeSubscription, "claude: claude.ai method and its id");
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
        Check(org.Method == "Anthropic Console" && org.MethodId == CliAccountMethod.ClaudeConsole, "claude: console method and its id");
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
        Check(codex.Method == "ChatGPT" && codex.MethodId == CliAccountMethod.CodexChatGpt, "codex: method and its id");
        Check(codex.Account == "dev@example.com", "codex: account from JWT");
        Check(codex.Plan == "Plus", "codex: plan capitalised");
        // Token blindness: the access_token must not appear in any visible field.
        Check(!codex.Summary.Contains("secret"), "codex: summary must not expose token");
        Check(!(codex.Account ?? "").Contains("secret"), "codex: account must not expose token");

        // Codex: signed out.
        Check(CliAccountSupport.ParseCodexStatus("Not logged in", authJson).LoggedIn == false, "codex: signed out");

        // Codex: API key method.
        Check(CliAccountSupport.ParseCodexStatus("Logged in using an API key - sk-***", null).MethodId == CliAccountMethod.CodexApiKey, "codex: API key method id");

        // Codex: unrecognised output → unknown.
        Check(CliAccountSupport.ParseCodexStatus("error: boom", null).LoggedIn == null, "codex: unknown on unrecognised output");

        // JWT with wrong structure → null.
        Check(CliAccountSupport.JwtClaims("not-a-jwt") == null, "jwt: 2-part token is not a JWT");

        return Task.CompletedTask;
    }

    // Mirrors AntigravityTests.statusComesFromTheModelListAndThereIsNoSignOut
    internal static Task AntigravityStatusIsItsModelListWithNoSignOut()
    {
        var home = Verification.Temp();
        try
        {
            // Antigravity has no status command: its model list stands in for it, and there is no sign-out.
            var signedIn = CliAccountSupport.AntigravityStatus(0, "Fetching available models...\ngemini-3.8-flash-low\tGemini 3.8 Flash (Low)\n");
            Check(signedIn.LoggedIn == true && signedIn.MethodId == CliAccountMethod.GeminiGoogle && !signedIn.CanSignOut && signedIn.Detail.Contains("/logout"), "antigravity: a model list is a sign-in, with no sign-out here");
            var signedOut = CliAccountSupport.AntigravityStatus(1, "Error: authentication required. Run 'agy' to log in, then retry.\n");
            Check(signedOut.LoggedIn == false && !signedOut.CanSignOut, "antigravity: agy's own words are a lost sign-in");
            var offline = CliAccountSupport.AntigravityStatus(1, "dial tcp: lookup failed");
            Check(offline.LoggedIn == null && offline.Detail.Length > 0, "antigravity: anything else is unknown");
            Check(CliAccountSupport.AntigravityStatus(0, "", timedOut: true).LoggedIn == null, "antigravity: a timeout is unknown");

            // BoundedFileText rejects directories and over-sized files.
            Check(CliAccountSupport.BoundedFileText(home) == null, "boundedFileText: rejects directories");

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
        Check(CliAccountSupport.LoginArguments("gemini")!.SequenceEqual(["agy"]), "antigravity: login argv is agy itself");
        Check(CliAccountSupport.LoginArguments("other") == null, "unknown: no login argv");

        // Status argv.
        Check(CliAccountSupport.StatusArguments("claude")!.SequenceEqual(["claude", "auth", "status", "--json"]), "claude: status argv");
        Check(CliAccountSupport.StatusArguments("gemini")!.SequenceEqual(["agy", "models"]), "antigravity: the model list stands in for a status");

        // Logout argv.
        Check(CliAccountSupport.LogoutArguments("codex")!.SequenceEqual(["codex", "logout"]), "codex: logout argv");
        Check(CliAccountSupport.LogoutArguments("gemini") == null, "antigravity: no logout argv");

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

    // Coordinator uses the runner for every CLI: Antigravity's status is its model list, and it has no logout.
    internal static async Task CoordinatorReadsStatusesAndCallsLogout()
    {
        var home = Verification.Temp();
        try
        {
            // Fake PATH pointing to our test dir so all CLI binaries are "found".
            var binDir = Path.Combine(home, "bin");
            Directory.CreateDirectory(binDir);
            string Bin(string name) => Path.Combine(binDir, OperatingSystem.IsWindows() ? name + ".exe" : name);
            File.WriteAllText(Bin("claude"), "");
            File.WriteAllText(Bin("codex"), "");
            File.WriteAllText(Bin("agy"), "");

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
                if (call.Arguments.SequenceEqual(["models"])) return new(1, "", "Error: authentication required. Run 'agy' to log in, then retry.\n", false);
                return new(0, "", "", false);
            });

            var env = new Dictionary<string, string>
            {
                ["PATH"] = binDir,
                ["NO_COLOR"] = "1",
            };
            var coordinator = new CliAccountsCoordinator(fakeRunner, env, home);

            // Refresh all providers.
            await coordinator.RefreshAsync(["claude", "codex", "gemini"]);

            Check(coordinator.Statuses["claude"].LoggedIn == true, "coordinator: claude logged in");
            Check(coordinator.Statuses["claude"].Account == "me@example.com", "coordinator: claude account");
            Check(coordinator.Statuses["codex"].LoggedIn == true, "coordinator: codex logged in");
            // agy models failed with agy's own sign-in words → signed out.
            Check(coordinator.Statuses["gemini"].LoggedIn == false && calls.Any(c => c.Arguments.SequenceEqual(["models"]) && Path.GetFileNameWithoutExtension(c.Executable) == "agy"), "coordinator: antigravity signed out, read from agy models");

            // Runner was called for claude and codex only.
            var runnerProviders = calls.Select(c => c.Arguments.FirstOrDefault() ?? "").ToHashSet();
            Check(runnerProviders.Contains("auth") || calls.Any(c => c.Arguments.Contains("auth")), "coordinator: ran claude auth status");
            Check(calls.Any(c => c.Arguments.Contains("status") && c.Arguments.Contains("login")), "coordinator: ran codex login status");

            // Logout for claude: runner must be called with logout argv.
            var callsBefore = calls.Count;
            await coordinator.LogoutAsync("claude");
            Check(calls.Count > callsBefore, "coordinator: logout called runner");
            Check(calls.Skip(callsBefore).Any(c => c.Arguments.Contains("logout")), "coordinator: logout used logout argv");

            // Antigravity has no logout: only its status is read again, unchanged.
            var callsBeforeAntigravity = calls.Count;
            var unchanged = await coordinator.LogoutAsync("gemini");
            Check(calls.Skip(callsBeforeAntigravity).All(c => c.Arguments.SequenceEqual(["models"])) && unchanged.LoggedIn == false && !unchanged.CanSignOut, "coordinator: antigravity logout runs no logout command");
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
        Check(summaries.Any(s => s == fixtures[2].Detail && s.Contains("Antigravity", StringComparison.Ordinal)), "smoke: not-installed detail present");

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
    // The app reads the account label out of an id token and must keep nothing else:
    // no part of any token may survive in the status, its text form, its JSON or a message.
    internal static Task TokenNeverSurvivesTheRead()
    {
        var idToken = Jwt(new Dictionary<string, object>
        {
            ["email"] = "dev@example.com",
            ["https://api.openai.com/auth"] = new { chatgpt_plan_type = "plus" },
        }).Replace(".sig", ".FIXTURE-SIGNATURE-VALUE");
        var secrets = new[] { idToken, idToken.Split('.')[1], "FIXTURE-SIGNATURE-VALUE", "FIXTURE-ACCESS-VALUE", "FIXTURE-REFRESH-VALUE", "sk-FIXTURE-API-KEY" };
        var authJson = JsonSerializer.Serialize(new { OPENAI_API_KEY = "sk-FIXTURE-API-KEY", tokens = new { id_token = idToken, access_token = "FIXTURE-ACCESS-VALUE", refresh_token = "FIXTURE-REFRESH-VALUE" } });
        var broken = JsonSerializer.Serialize(new { tokens = new { id_token = "aaa.%%%not-base64%%%.FIXTURE-SIGNATURE-VALUE", access_token = "FIXTURE-ACCESS-VALUE" } });
        foreach (var status in new[]
        {
            CliAccountSupport.ParseCodexStatus("Logged in using ChatGPT\n", authJson),
            CliAccountSupport.ParseCodexStatus("Logged in using ChatGPT\n", broken),
            CliAccountSupport.ParseCodexStatus("Not logged in", authJson),
            CliAccountSupport.ParseCodexStatus("error: boom", authJson),
            CliAccountSupport.ParseCodexStatus("Logged in using ChatGPT\n", "{ not json FIXTURE-ACCESS-VALUE"),
        })
        {
            var visible = string.Join("\n", status.ToString(), JsonSerializer.Serialize(status), status.Summary, status.Detail, status.Account, status.Plan, status.Method);
            foreach (var secret in secrets) Check(!visible.Contains(secret, StringComparison.Ordinal), "a token value survived the read: " + secret[..Math.Min(12, secret.Length)]);
        }
        var good = CliAccountSupport.ParseCodexStatus("Logged in using ChatGPT\n", authJson);
        Check(good.Account == "dev@example.com" && good.Plan == "Plus", "the label and the plan are still read");
        var bad = CliAccountSupport.ParseCodexStatus("Logged in using ChatGPT\n", broken);
        Check(bad.LoggedIn == true && bad.Account is null, "a broken id token gives no label and no error");
        return Task.CompletedTask;
    }
}
