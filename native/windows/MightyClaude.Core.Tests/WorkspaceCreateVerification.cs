using System.Collections.Concurrent;
using System.Text.Json;
using MightyClaude.Core;
using static DelegationSupport;

/// <summary>
/// Adding a workspace from a new folder or a Git address (docs/superpowers/specs/2026-10-26-workspace-create-design.md), held to
/// native/contracts/fixtures/workspace-create.json as the Mac's WorkspaceCreateTests are, and the folder, probe and clone runners
/// against a local bare repository.
/// </summary>
internal static class WorkspaceCreateVerification
{
    private static readonly Lazy<JsonElement> Fixture = new(() =>
    {
        const string name = "MightyClaude.Core.Tests.WorkspaceCreate.json";
        using var stream = typeof(WorkspaceCreateVerification).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is not embedded in MightyClaude.Core.Tests");
        return JsonDocument.Parse(stream).RootElement.Clone();
    });

    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetString();

    internal static Task TheSharedRulesHold()
    {
        RepoNamesFollowTheFixture();
        GitUrlsFollowTheFixture();
        FolderNamesFollowTheFixture();
        GitErrorsFollowTheFixture();
        CloneProgressFollowsTheFixture();
        return Task.CompletedTask;
    }

    private static void RepoNamesFollowTheFixture()
    {
        foreach (var row in Fixture.Value.GetProperty("repoNames").EnumerateArray())
        {
            var url = row.GetProperty("url").GetString()!;
            Same(WorkspaceCreate.RepoName(url), Text(row.GetProperty("name")), $"the folder name '{url}' suggests");
        }
    }

    private static void GitUrlsFollowTheFixture()
    {
        var urls = Fixture.Value.GetProperty("gitURLs");
        foreach (var url in urls.GetProperty("valid").EnumerateArray().Select(value => value.GetString()!)) Check(WorkspaceCreate.LooksLikeGitUrl(url), $"'{url}' is a Git address");
        foreach (var url in urls.GetProperty("invalid").EnumerateArray().Select(value => value.GetString()!)) Check(!WorkspaceCreate.LooksLikeGitUrl(url), $"'{url}' is no Git address");
    }

    private static void FolderNamesFollowTheFixture()
    {
        foreach (var row in Fixture.Value.GetProperty("names").EnumerateArray())
        {
            var name = row.GetProperty("name").GetString()!;
            var shown = name.Length > 40 ? name[..40] + "…" : name;
            Same(WorkspaceCreate.CheckName(name, windows: true), Text(row.GetProperty("windows")), $"the Windows rule for '{shown}'");
            Same(WorkspaceCreate.CheckName(name, windows: false), Text(row.GetProperty("mac")), $"the Mac rule for '{shown}'");
        }
    }

    private static void GitErrorsFollowTheFixture()
    {
        foreach (var row in Fixture.Value.GetProperty("errors").EnumerateArray())
        {
            var stderr = row.GetProperty("stderr").GetString()!;
            Same(WorkspaceCreate.ErrorKind(stderr), row.GetProperty("kind").GetString(), $"the kind of '{stderr}'");
        }
    }

    private static void CloneProgressFollowsTheFixture()
    {
        foreach (var row in Fixture.Value.GetProperty("progress").EnumerateArray())
        {
            var line = row.GetProperty("line").GetString()!;
            var progress = WorkspaceCreate.Progress(line);
            if (Text(row.GetProperty("phase")) is not { } phase) { Check(progress is null, $"'{line}' gives no progress"); continue; }
            Check(progress is not null, $"'{line}' gives progress");
            Same(progress!.Phase, phase, $"the phase of '{line}'");
            Same(progress.Percent, row.GetProperty("percent").GetInt32(), $"the percent of '{line}'");
            var fraction = row.GetProperty("fraction").GetDouble();
            Check(Math.Abs(progress.Fraction - fraction) < 0.001, $"the fraction of '{line}': expected {fraction} but was {progress.Fraction}");
        }
    }

    internal static async Task MakeFolderAndCloneWorkOnALocalBareRepository()
    {
        if (GitTool.Executable is null) return;
        var root = Verification.Temp();
        try
        {
            var work = Path.Combine(root, "work"); Directory.CreateDirectory(work);
            await FixtureGit(root, work, "init", "-q");
            await File.WriteAllTextAsync(Path.Combine(work, "README.md"), "hello\n");
            await FixtureGit(root, work, "add", "README.md");
            await FixtureGit(root, work, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-q", "-m", "first");
            var bare = Path.Combine(root, "remote.git");
            await FixtureGit(root, root, "clone", "-q", "--bare", work, bare);

            var reachable = await GitTool.ProbeAsync(bare);
            Check(reachable.Reachable, $"the bare repository is reachable: {reachable.Kind} {reachable.Message}");
            var missing = await GitTool.ProbeAsync(Path.Combine(root, "none.git"));
            Check(!missing.Reachable && missing.Kind == "notFound", $"a missing repository is notFound: {missing.Reachable} {missing.Kind} {missing.Message}");

            var copy = Path.Combine(root, "copy"); var readme = Path.Combine(copy, "README.md");
            var steps = new ConcurrentQueue<CloneProgress>();
            var done = await new GitCloneJob(bare, copy, steps.Enqueue).RunAsync();
            Check(done.Outcome == "done" && File.Exists(readme), $"the clone is done with README.md: {done}");
            Check(steps.All(step => step.Fraction is >= 0.0 and <= 1.0), "every progress step is within 0 and 1");

            var notStarted = Path.Combine(root, "cancelled");
            var job = new GitCloneJob(bare, notStarted, _ => { });
            job.Cancel();
            var cancelled = await job.RunAsync();
            Check(cancelled.Outcome == "cancelled" && !Directory.Exists(notStarted), $"a clone cancelled before it runs leaves no folder: {cancelled}");

            var again = await new GitCloneJob(bare, copy, _ => { }).RunAsync();
            Check(again.Outcome == "failed" && File.Exists(readme), $"a clone into an existing folder fails and leaves it: {again}");

            // As a file:// address, so git answers through its transport (and makes the folder first) as for a server; a plain
            // missing path is refused before any folder ("does not exist"), and the probe already names that one notFound.
            var failedTarget = Path.Combine(root, "failed");
            var missingUrl = "file:///" + Path.Combine(root, "none.git").Replace('\\', '/').TrimStart('/');
            var failed = await new GitCloneJob(missingUrl, failedTarget, _ => { }).RunAsync();
            Check(failed.Outcome == "failed" && failed.Kind == "notFound" && !Directory.Exists(failedTarget), $"a clone of a missing repository fails as notFound and leaves no folder: {failed}");

            var repo = Path.Combine(root, "repo");
            var made = await GitTool.MakeFolderAsync(repo, gitInit: true);
            Check(made.Outcome == "made" && Directory.Exists(Path.Combine(repo, ".git")), $"the new folder is a Git repository: {made}");
            var exists = await GitTool.MakeFolderAsync(repo, gitInit: false);
            Check(exists.Outcome == "failed", $"an existing folder is never made again: {exists}");
        }
        finally { RemoveFolder(root); }
    }

    /// <summary>The fixture's own git, with no user or system configuration, so the repositories are the same on every machine.</summary>
    private static async Task FixtureGit(string root, string folder, params string[] arguments)
    {
        var settings = Path.Combine(root, "fixture.gitconfig");
        if (!File.Exists(settings)) await File.WriteAllTextAsync(settings, "");
        var info = ChildProcess.StartInfo(GitTool.Executable!, ["-C", folder, .. arguments], folder);
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["GIT_CONFIG_GLOBAL"] = settings; info.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        await using var process = ChildProcess.Start(info); process.Input.Close();
        var stdout = process.Output.ReadToEndAsync(); var stderr = process.Error.ReadToEndAsync();
        // Generous: the first git launch on a cold CI runner can take seconds.
        var exit = await process.Completion.WaitAsync(TimeSpan.FromSeconds(120));
        await stdout; var errors = await stderr;
        Check(exit == 0, $"git {string.Join(" ", arguments)}: {errors}");
    }
}
