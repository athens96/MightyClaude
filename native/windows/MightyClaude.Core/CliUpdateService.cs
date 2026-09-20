using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// One recognised installation of a provider CLI.
/// Method is native, winget, npm, unknown or missing.
public sealed record CliUpdateInstallation(
    string Provider,
    string? Version = null,
    string Method = "unknown",
    string? ExecutablePath = null,
    bool CanUpdate = false,
    string Detail = "");

/// The outcome of one update request.
/// Status is updated, current, skipped, failed, cancelled or busy.
/// Output is bounded diagnostic text, shown only on an explicit details action.
public sealed record CliUpdateResult(
    string Provider,
    string Status,
    string? BeforeVersion = null,
    string? AfterVersion = null,
    string Method = "unknown",
    string Detail = "",
    string Output = "");

/// The single invocation chosen for an installation. Never a shell command line:
/// an executable path plus an argument list, handed to ICliRunner as-is.
internal sealed record CliUpdateInvocation(string Executable, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string>? Environment = null);

/// Updates only an already installed, recognised CLI through that CLI's own
/// installer. It never installs anything new, never asks for elevation, never
/// writes a configuration file and never upgrades an unrelated package.
/// Every process goes through ICliRunner, so a test injects a fake runner and
/// no real claude, codex, gemini, npm or winget process is started.
public sealed partial class CliUpdateService
{
    private readonly ICliRunner runner;
    private readonly IReadOnlyDictionary<string, string> environment;
    private readonly string home;
    private readonly TimeSpan metadataTimeout;
    private readonly TimeSpan updateTimeout;

    private readonly object gate = new();
    private CancellationTokenSource? active;
    private Task<CliUpdateResult>? activeTask;
    private bool closing;

    // 1 MiB captured by the runner, 8 KiB of it kept for the details action.
    public const int DiagnosticCharacterCap = 8192;
    private const int VersionTextCap = 160;

    public CliUpdateService(
        ICliRunner runner,
        IReadOnlyDictionary<string, string>? environment = null,
        string? homeDirectory = null,
        TimeSpan? metadataTimeout = null,
        TimeSpan? updateTimeout = null)
    {
        this.runner = runner;
        this.environment = environment ?? Runtime();
        home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        this.metadataTimeout = metadataTimeout ?? TimeSpan.FromSeconds(10);
        this.updateTimeout = updateTimeout ?? TimeSpan.FromMinutes(5);
    }

    private static Dictionary<string, string> Runtime()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) values[key] = value;
        return values;
    }

    /// The app checks at start only when the saved switch is a JSON true.
    public static bool ShouldRunAtStartup(AppSnapshot snapshot) => snapshot.AutoUpdateCLIs == true;

    /// macOS ProviderOptions.label — the name the result row shows.
    public static string ProviderLabel(string provider) => provider switch
    {
        "claude" => "Claude",
        "codex" => "Codex",
        "gemini" => "Gemini",
        _ => "Claude",
    };

    /// The row WinUI shows, so no result copy is assembled outside Core.
    public static string ResultRow(CliUpdateResult result) => CliUpdateStrings.ResultRowTemplate
        .Replace("{provider}", ProviderLabel(result.Provider))
        .Replace("{status}", CliUpdateStrings.StatusLabel(result.Status));

    /// The version line, or null when nothing changed.
    public static string? VersionChange(CliUpdateResult result) =>
        result.BeforeVersion is { Length: > 0 } before && result.AfterVersion is { Length: > 0 } after && before != after
            ? CliUpdateStrings.VersionChangeTemplate.Replace("{before}", before).Replace("{after}", after)
            : null;

    public async Task<CliUpdateInstallation> InspectAsync(string provider, CancellationToken cancellation = default)
    {
        if (Closing()) return new CliUpdateInstallation(provider, Detail: CliUpdateStrings.DetailClosing);
        try { return (await PlanAsync(provider, cancellation)).Installation; }
        catch (OperationCanceledException) { return new CliUpdateInstallation(provider, Detail: CliUpdateStrings.DetailInspectCancelled); }
        catch (Exception) { return new CliUpdateInstallation(provider, Detail: CliUpdateStrings.DetailInspectFailed); }
    }

    /// One update at a time. A second request while one runs reports busy.
    public async Task<CliUpdateResult> UpdateAsync(string provider, CancellationToken cancellation = default)
    {
        CancellationTokenSource source;
        Task<CliUpdateResult> task;
        lock (gate)
        {
            if (closing) return new CliUpdateResult(provider, "cancelled", Detail: CliUpdateStrings.DetailClosing);
            if (active is not null) return new CliUpdateResult(provider, "busy", Detail: CliUpdateStrings.DetailBusy);
            if (cancellation.IsCancellationRequested) return new CliUpdateResult(provider, "cancelled", Detail: CliUpdateStrings.DetailCancelled);
            source = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            active = source;
            task = PerformAsync(provider, source.Token);
            activeTask = task;
        }
        try { return await task; }
        finally
        {
            lock (gate) { if (ReferenceEquals(active, source)) { active = null; activeTask = null; } }
            source.Dispose();
        }
    }

    /// The start-up and "업데이트 하기" pass: one provider after another, never
    /// two installers at once.
    public async Task<IReadOnlyList<CliUpdateResult>> UpdateAllAsync(CancellationToken cancellation = default)
    {
        var results = new List<CliUpdateResult>();
        foreach (var provider in Wire.Providers) results.Add(await UpdateAsync(provider, cancellation));
        return results;
    }

    /// Stops the running installer together with its whole process group and
    /// waits for the result, so the section never shows a half-finished run.
    public async Task CancelAsync()
    {
        Task<CliUpdateResult>? task;
        lock (gate) { active?.Cancel(); task = activeTask; }
        if (task is not null) { try { await task; } catch (Exception) { } }
    }

    /// Closing the app: refuse new work, then cancel cleanly.
    public async Task ShutdownAsync()
    {
        lock (gate) closing = true;
        await CancelAsync();
    }

    private bool Closing() { lock (gate) return closing; }

    private async Task<CliUpdateResult> PerformAsync(string provider, CancellationToken token)
    {
        var installation = new CliUpdateInstallation(provider);
        try
        {
            token.ThrowIfCancellationRequested();
            var (planned, invocation) = await PlanAsync(provider, token);
            installation = planned;
            token.ThrowIfCancellationRequested();
            if (invocation is null)
                return new CliUpdateResult(provider, "skipped", installation.Version, Method: installation.Method, Detail: installation.Detail);

            var run = await runner.RunAsync(invocation.Executable, invocation.Arguments, updateTimeout, token, invocation.Environment);
            token.ThrowIfCancellationRequested();
            var output = Bounded(run);
            if (run.ExitCode != 0)
                return new CliUpdateResult(provider, "failed", installation.Version, Method: installation.Method,
                    Detail: CliUpdateStrings.DetailFailedExitTemplate.Replace("{code}", run.ExitCode.ToString()), Output: output);

            var after = await InstalledCommandAsync(provider, token);
            token.ThrowIfCancellationRequested();
            if (after?.Version is not { Length: > 0 } version)
                return new CliUpdateResult(provider, "failed", installation.Version, Method: installation.Method,
                    Detail: CliUpdateStrings.DetailVersionRecheckFailed, Output: output);

            var changed = installation.Version != version;
            return new CliUpdateResult(provider, changed ? "updated" : "current", installation.Version, version, installation.Method,
                changed ? CliUpdateStrings.DetailUpdated : CliUpdateStrings.DetailUnchanged, output);
        }
        catch (OperationCanceledException)
        {
            return new CliUpdateResult(provider, "cancelled", installation.Version, Method: installation.Method, Detail: CliUpdateStrings.DetailCancelled);
        }
        catch (Exception error)
        {
            var text = error.Message;
            return new CliUpdateResult(provider, "failed", installation.Version, Method: installation.Method,
                Detail: text.Length > 600 ? text[..600] : text);
        }
    }

    private async Task<(CliUpdateInstallation Installation, CliUpdateInvocation? Invocation)> PlanAsync(string provider, CancellationToken token)
    {
        if (!Wire.Providers.Contains(provider))
            return (new CliUpdateInstallation(provider, Detail: CliUpdateStrings.DetailUnsupportedProvider), null);

        var command = await InstalledCommandAsync(provider, token);
        if (command is null)
        {
            var present = Candidates(provider).Any(File.Exists);
            return (new CliUpdateInstallation(provider, Method: present ? "unknown" : "missing",
                Detail: present ? CliUpdateStrings.DetailVersionUnknown : CliUpdateStrings.DetailMissing), null);
        }

        var resolved = Resolve(command.Path);
        var installation = new CliUpdateInstallation(provider, command.Version, "unknown", command.Path, false, CliUpdateStrings.DetailUnknownMethod);

        if (WingetPackage(resolved) is { } package)
        {
            installation = installation with { Method = "winget" };
            if (Candidates("winget").FirstOrDefault(File.Exists) is not { } winget)
                return (installation with { Detail = CliUpdateStrings.DetailWingetRuntimeMissing }, null);
            // Exactly the one package this installation belongs to: --id with
            // --exact, never --all, and no interactive or elevation prompt.
            return (installation with { CanUpdate = true, Detail = CliUpdateStrings.DetailWingetPlan },
                new CliUpdateInvocation(winget, ["upgrade", "--id", package, "--exact", "--silent",
                    "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity"]));
        }

        if (provider == "claude" && NativeClaude(resolved))
            return (installation with { Method = "native", CanUpdate = true, Detail = CliUpdateStrings.DetailNativeClaude },
                new CliUpdateInvocation(command.Path, ["update"]));

        if (NpmPackage(resolved, provider) is { } npm)
        {
            installation = installation with { Method = "npm" };
            if (!Release().IsMatch(npm.Version))
                return (installation with { Detail = CliUpdateStrings.DetailNpmPrerelease }, null);
            if (FindNpmRuntime(npm.Prefix) is not { } runtime)
                return (installation with { Detail = CliUpdateStrings.DetailNpmRuntimeMissing }, null);
            // The official package only, in the prefix it is already installed in.
            return (installation with { CanUpdate = true, Detail = CliUpdateStrings.DetailNpmPlan },
                new CliUpdateInvocation(runtime.Node, [runtime.Script, "install", "--global", "--prefix", npm.Prefix,
                    npm.Name + "@latest", "--no-audit", "--no-fund"]));
        }

        return (installation, null);
    }

    private sealed record InstalledCommand(string Path, string Version);

    private async Task<InstalledCommand?> InstalledCommandAsync(string provider, CancellationToken token)
    {
        if (!Wire.Providers.Contains(provider)) return null;
        foreach (var path in Candidates(provider))
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;
            CliRunResult run;
            try { run = await runner.RunAsync(path, ["--version"], metadataTimeout, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { continue; }
            if (run.ExitCode != 0 || run.TimedOut) continue;
            var text = run.Output.Trim();
            if (text.Length == 0) continue;
            return new InstalledCommand(path, text.Length > VersionTextCap ? text[..VersionTextCap] : text);
        }
        return null;
    }

    // PATH entries, at most 64, each joined with the name and the Windows
    // executable extensions. The bare name is kept last so a fixture without an
    // extension is still found.
    private IEnumerable<string> Candidates(string name)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        environment.TryGetValue("PATH", out var path);
        foreach (var entry in (path ?? "").Split(System.IO.Path.PathSeparator).Take(64))
        {
            if (entry.Length == 0 || entry.Contains('\0') || !System.IO.Path.IsPathRooted(entry) || !seen.Add(entry)) continue;
            foreach (var extension in new[] { ".cmd", ".exe", ".bat", "" })
                yield return System.IO.Path.Combine(entry, name + extension);
        }
    }

    // winget installs a package under ...\WinGet\Packages\<PackageIdentifier>_<hash>\
    // and puts a link in ...\WinGet\Links. Reading the identifier out of the
    // install location means the upgrade names exactly the package that is
    // already installed instead of a guessed identifier.
    private static string? WingetPackage(string executable)
    {
        var parts = executable.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i + 1 < parts.Length; i++)
        {
            if (!parts[i].Equals("Packages", StringComparison.OrdinalIgnoreCase)) continue;
            if (!parts[i - 1].Equals("WinGet", StringComparison.OrdinalIgnoreCase)) continue;
            var folder = parts[i + 1];
            var cut = folder.LastIndexOf('_');
            var id = cut > 0 ? folder[..cut] : folder;
            if (Identifier().IsMatch(id)) return id;
        }
        return null;
    }

    // The native installer keeps its versions under <home>\.local\share\claude\versions
    // (XDG_DATA_HOME when it is set), either as the executable itself or one
    // directory per version, which is the Windows layout.
    private bool NativeClaude(string executable)
    {
        var roots = new List<string> { System.IO.Path.Combine(home, ".local", "share", "claude", "versions") };
        if (environment.TryGetValue("XDG_DATA_HOME", out var data) && data.Length > 0 && System.IO.Path.IsPathRooted(data) && !data.Contains('\0'))
            roots.Add(System.IO.Path.Combine(data, "claude", "versions"));
        var parent = System.IO.Path.GetDirectoryName(executable);
        var grandparent = parent is null ? null : System.IO.Path.GetDirectoryName(parent);
        return roots.Any(root => Same(parent, root) || Same(grandparent, root));
    }

    private static bool Same(string? left, string? right) =>
        left is not null && right is not null &&
        System.IO.Path.TrimEndingDirectorySeparator(Resolve(left)).Equals(System.IO.Path.TrimEndingDirectorySeparator(Resolve(right)), StringComparison.OrdinalIgnoreCase);

    private sealed record NpmInstallation(string Prefix, string Name, string Version);

    // npm --global on Windows puts its shims straight in the prefix folder and
    // the package under <prefix>\node_modules\<name>, so the executable's own
    // folder is the prefix. The package.json must name the official package and
    // declare a bin entry for this CLI, or the install is not recognised.
    private static NpmInstallation? NpmPackage(string executable, string provider)
    {
        var name = provider switch
        {
            "claude" => "@anthropic-ai/claude-code",
            "codex" => "@openai/codex",
            _ => "@google/gemini-cli",
        };
        var prefix = System.IO.Path.GetDirectoryName(executable);
        if (prefix is null) return null;
        var roots = new List<string> { System.IO.Path.Combine(prefix, "node_modules") };
        // A Unix-shaped global prefix keeps the shim in <prefix>/bin.
        var parent = System.IO.Path.GetDirectoryName(prefix);
        if (parent is not null) roots.Add(System.IO.Path.Combine(parent, "lib", "node_modules"));
        foreach (var root in roots)
        {
            var package = System.IO.Path.Combine(root, name.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (Metadata(package) is not { } json) continue;
            if (json.Name != name || !json.Bin.Contains(provider)) continue;
            var target = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(root));
            return new NpmInstallation(root.EndsWith(System.IO.Path.Combine("lib", "node_modules"), StringComparison.OrdinalIgnoreCase) && target is not null
                ? target! : prefix, name, json.Version);
        }
        return null;
    }

    private sealed record PackageJson(string Name, string Version, IReadOnlyCollection<string> Bin);

    private static PackageJson? Metadata(string packageRoot)
    {
        var file = System.IO.Path.Combine(packageRoot, "package.json");
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > 262_144) return null;
            using var json = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var bin = new List<string>();
            if (root.TryGetProperty("bin", out var b))
            {
                if (b.ValueKind == JsonValueKind.Object) bin.AddRange(b.EnumerateObject().Select(p => p.Name));
                else if (b.ValueKind == JsonValueKind.String && name.Length > 0) bin.Add(name[(name.LastIndexOf('/') + 1)..]);
            }
            return new PackageJson(name, version, bin);
        }
        catch (Exception) { return null; }
    }

    private sealed record NpmRuntime(string Node, string Script);

    // npm itself is started through Node with npm's own cli script, never
    // through a .cmd shim, so no shell command line is ever built.
    private NpmRuntime? FindNpmRuntime(string prefix)
    {
        var scripts = new List<string>
        {
            System.IO.Path.Combine(prefix, "node_modules", "npm", "bin", "npm-cli.js"),
            System.IO.Path.Combine(prefix, "lib", "node_modules", "npm", "bin", "npm-cli.js"),
        };
        var script = scripts.FirstOrDefault(File.Exists);
        if (script is null) return null;
        var node = Candidates("node").FirstOrDefault(File.Exists);
        return node is null ? null : new NpmRuntime(node, script);
    }

    private static string Resolve(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var target = File.ResolveLinkTarget(full, true) ?? (Directory.Exists(full) ? Directory.ResolveLinkTarget(full, true) : null);
            return target is null ? full : System.IO.Path.GetFullPath(target.FullName);
        }
        catch (Exception) { return path; }
    }

    // stdout then stderr, control characters dropped, the last 8 KiB kept.
    // Shown only when the user asks for details and never saved.
    private static string Bounded(CliRunResult run)
    {
        var text = run.Output + (run.ErrorOutput.Length == 0 ? "" : "\n" + run.ErrorOutput);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text) if (!char.IsControl(c) || c is '\n' or '\t') builder.Append(c);
        var safe = builder.ToString();
        return safe.Length > DiagnosticCharacterCap ? safe[^DiagnosticCharacterCap..] : safe;
    }

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+(?:\+[0-9A-Za-z.-]+)?\z")]
    private static partial Regex Release();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9.+-]{0,127}\z")]
    private static partial Regex Identifier();
}
