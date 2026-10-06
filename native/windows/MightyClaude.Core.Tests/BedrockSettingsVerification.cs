using System.Text.Json;
using MightyClaude.Core;

internal static class BedrockSettingsVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static Task ExternalStatusDoesNotClaimVerifiedAccess()
    {
        var status = CliAccountSupport.ParseClaudeStatus("""{"loggedIn":true,"authMethod":"third_party","apiProvider":"bedrock","email":"old-account@example.test","subscriptionType":"max"}""");
        Check(status.Method == "AWS Bedrock" && status.MethodId == CliAccountMethod.ClaudeBedrock && status.AccessVerified == false && !status.CanSignOut, "configuration is not verified access or OAuth signout");
        Check(status.Account is null && status.Plan is null, "leftover subscription identity cannot label Bedrock credentials");
        var signedOut = CliAccountSupport.ParseClaudeStatus("""{"loggedIn":false,"authMethod":"third_party","apiProvider":"bedrock"}""");
        Check(signedOut.Method == "AWS Bedrock" && signedOut.LoggedIn == false, "Bedrock configuration remains visible before login");
        Check(CliAccountSupport.LoginArguments("claude", CliLoginOption.Bedrock)!.SequenceEqual(["claude", "/setup-bedrock"]), "the CLI owns its interactive setup");
        var conflict = BedrockSettings.ConflictDetail(new Dictionary<string, string> { ["AWS_BEARER_TOKEN_BEDROCK"] = "placeholder-one" }, """{"env":{"AWS_BEARER_TOKEN_BEDROCK":"placeholder-two"}}""");
        Check(conflict is not null && !conflict.Contains("placeholder"), "conflicts reveal no key material");
        Check(BedrockSettings.ConflictDetail(new Dictionary<string, string> { ["AWS_BEARER_TOKEN_BEDROCK"] = "same" }, """{"env":{"AWS_BEARER_TOKEN_BEDROCK":"same"}}""") is null, "identical tokens are not a conflict");
        Check(BedrockSettings.ConflictDetail(new Dictionary<string, string> { ["AWS_BEARER_TOKEN_BEDROCK"] = "same" }, "[]") is null, "malformed settings do not break account status");
        return Task.CompletedTask;
    }
    internal static Task ResetPreservesUnrelatedCredentialsAndMakesBackup()
    {
        const string original = """{"env":{"CLAUDE_CODE_USE_BEDROCK":"1","AWS_BEARER_TOKEN_BEDROCK":"fixture-token","AWS_REGION":"ap-northeast-2","AWS_ACCESS_KEY_ID":"unrelated-aws-key","ANTHROPIC_DEFAULT_SONNET_MODEL":"apac.anthropic.claude-sonnet-4-5","CUSTOM_FLAG":"keep"},"model":"apac.anthropic.claude-test","permissions":{"deny":["Bash(rm *)"]}}""";
        using var reset = JsonDocument.Parse(BedrockSettings.ResetUserSettingsJson(original));
        var root = reset.RootElement; var env = root.GetProperty("env");
        Check(!env.TryGetProperty("AWS_BEARER_TOKEN_BEDROCK", out _) && !env.TryGetProperty("CLAUDE_CODE_USE_BEDROCK", out _) && !env.TryGetProperty("AWS_REGION", out _), "Bedrock environment settings are removed");
        Check(env.Text("AWS_ACCESS_KEY_ID") == "unrelated-aws-key" && env.Text("CUSTOM_FLAG") == "keep" && root.TryGetProperty("permissions", out _), "unrelated AWS credentials and permission denies survive");
        Check(!root.TryGetProperty("model", out _) && !env.TryGetProperty("ANTHROPIC_DEFAULT_SONNET_MODEL", out _), "only Bedrock model pins are removed");
        using var other = JsonDocument.Parse(BedrockSettings.ResetUserSettingsJson("""{"env":{"AWS_REGION":"eu-west-1","ANTHROPIC_DEFAULT_SONNET_MODEL":"sonnet"},"model":"sonnet"}"""));
        Check(other.RootElement.Text("model") == "sonnet" && other.RootElement.GetProperty("env").Text("AWS_REGION") == "eu-west-1", "non-Bedrock model and region settings survive");
        var home = Directory.CreateTempSubdirectory("mighty-bedrock-reset-").FullName;
        if (OperatingSystem.IsMacOS() && home.StartsWith("/var/", StringComparison.Ordinal)) home = "/private" + home;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".claude"));
            var path = Path.Combine(home, ".claude", "settings.json"); File.WriteAllText(path, original);
            var backup = BedrockSettings.ResetUserSettings(home, new Dictionary<string, string>());
            Check(backup is not null && File.ReadAllText(backup) == original, "backup preserves the original complete settings");
            Check(!File.ReadAllText(path).Contains("fixture-token"), "reset was actually written");
            Check(BedrockSettings.ResetUserSettings(home, new Dictionary<string, string>()) is null, "repeated reset is a no-op");
        }
        finally { Directory.Delete(home, true); }
        return Task.CompletedTask;
    }
}
