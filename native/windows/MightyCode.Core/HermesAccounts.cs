using System.Text;
using System.Text.Json;

namespace MightyCode.Core;

/// <summary>
/// What the CLI accounts and updates read of Hermes Agent (Nous Research, <c>hermes</c>; M/MightyCore/HermesSupport.swift). Hermes has no
/// command that prints its sign-in, so its ACP server (<c>hermes acp</c>) is started, sent <c>initialize</c> alone and read for the provider
/// its <c>authMethods</c> name: a name, never a credential. No session is opened, so Hermes's gateway daemon is not started; once the
/// answer is in, closing the server's standard input ends it. <c>hermes --version</c> prints several lines, the first of which is the version.
/// </summary>
public static class HermesAccounts
{
    /// <summary>
    /// What <c>hermes acp</c> answered: the provider whose credentials resolved (null when it has none), whether it answered
    /// <c>initialize</c> at all, whether the wait ran out first, and the version it names.
    /// </summary>
    public sealed record Probe(string? Provider, bool Answered, bool TimedOut, string? Version);

    /// <summary>The ACP auth method Hermes always offers: its terminal setup (<c>hermes acp --setup</c>), not a sign-in.</summary>
    public const string SetupMethod = "hermes-setup";
    /// <summary>The request's id: an ACP channel's first, which the recorded answers in native/contracts/fixtures/hermes-acp-*.jsonl carry.</summary>
    public const string RequestId = "mighty-1";
    /// <summary>The one line written to <c>hermes acp</c>: <c>initialize</c> with the capabilities a run announces.</summary>
    public const string InitializeLine = """{"jsonrpc":"2.0","id":"mighty-1","method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false,"elicitation":{"form":{}}},"clientInfo":{"name":"mighty-code","title":"Mighty Code","version":"1.0"}}}""";
    /// <summary>How long Hermes has to answer (0.23.1 answered in 0.45 s on a warm Mac; a cold start of its Python takes longer).</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(25);
    /// <summary>How long Hermes has to end once its standard input is closed (it took 0.1 s), before it is stopped.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);
    private const int MaximumLines = 64, MaximumOutput = 1_048_576;

    /// <summary>
    /// <c>hermes --version</c> prints its version first (<c>Hermes Agent v0.23.1 (2026.9.24) · stable · local …</c>), then its install
    /// folder, Python and update status: the first line is the version.
    /// </summary>
    public static string VersionLine(string output) => HermesAgent.VersionLine(output);

    /// <summary>
    /// The <c>initialize</c> answer among the lines <c>hermes acp</c> printed (a log line or a warning may come first): the answer to
    /// <see cref="RequestId"/>, an error answer counting as none. Null when none of the first 64 lines is it.
    /// </summary>
    public static Probe? ParseInitialize(string output)
    {
        foreach (var line in output.Split('\n').Take(MaximumLines))
            if (Answer(line) is { } probe) return probe;
        return null;
    }

    private static Probe? Answer(string line)
    {
        if (line.Trim().Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.Text("id") != RequestId) return null;
            if (MetadataJson.Property(root, "result") is not { ValueKind: JsonValueKind.Object } result) return new Probe(null, false, false, null);
            var version = MetadataJson.Property(result, "agentInfo").Text("version");
            return new Probe(Provider(result) is { } provider ? CliAccountSupport.Clean(provider) : null, true, false, version is null ? null : CliAccountSupport.Clean(version));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The provider an <c>initialize</c> result's <c>authMethods</c> name: Hermes always lists its terminal setup, and an agent method named
    /// after the provider when that provider's credentials resolve (acp_adapter/auth.py). Only ids are read.
    /// </summary>
    public static string? Provider(JsonElement result)
    {
        if (MetadataJson.Property(result, "authMethods") is not { ValueKind: JsonValueKind.Array } methods) return null;
        foreach (var method in methods.EnumerateArray().Take(64))
            if (method.Text("id") is { Length: > 0 } id && id != SetupMethod && (method.Text("type") ?? "agent") == "agent") return id;
        return null;
    }

    /// <summary>
    /// Starts <c>hermes acp</c> in <paramref name="workingDirectory"/>, writes <see cref="InitializeLine"/> and reads the answer, then
    /// closes its standard input, which ends it; one that does not end within two seconds is stopped. Its standard error (its log) is
    /// read and dropped. Null when it cannot be started; not answered when it ended first (an old Hermes without <c>acp</c>, an install
    /// without its ACP packages: "The ACP server can't start: its protocol packages are missing"), answered with an error, or did not
    /// answer within <paramref name="timeout"/> (timed out).
    /// </summary>
    public static async Task<Probe?> ProbeAsync(CliCommand command, IReadOnlyDictionary<string, string> environment, string workingDirectory, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var values = new Dictionary<string, string>(environment) { ["NO_COLOR"] = "1" };
        ChildProcess child;
        try { child = ProviderCatalog.StartCli(command.Binary, [.. command.Prefix, "acp"], workingDirectory, values); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { return null; }
        await using (child)
        {
            _ = DrainQuietlyAsync(child.Error);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            wait.CancelAfter(timeout);
            Probe? answer = null; var timedOut = false;
            try
            {
                // One that is gone before it read the request has nothing to answer: what it printed is still read.
                try { await child.Input.WriteAsync((InitializeLine + "\n").AsMemory(), wait.Token); await child.Input.FlushAsync(wait.Token); }
                catch (IOException) { }
                answer = await ReadAnswerAsync(child.Output, wait.Token);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { timedOut = true; }
            catch (IOException) { }
            try { child.Input.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            try { await child.Completion.WaitAsync(ExitGrace, cancellation); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            return answer ?? new Probe(null, false, timedOut, null);
        }
    }

    /// <summary>Lines until the answer: none after 64 lines or 1 MiB, or once the output ends.</summary>
    private static async Task<Probe?> ReadAnswerAsync(StreamReader output, CancellationToken token)
    {
        var buffer = new char[4096]; var line = new StringBuilder(); int read, lines = 0, total = 0;
        while ((read = await output.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != '\n') { line.Append(buffer[index]); continue; }
                if (Answer(line.ToString()) is { } probe) return probe;
                line.Clear();
                if (++lines >= MaximumLines) return null;
            }
            if ((total += read) > MaximumOutput) return null;
        }
        return Answer(line.ToString());
    }

    private static async Task DrainQuietlyAsync(StreamReader reader)
    {
        try { await ProviderCatalog.DrainAsync(reader, CancellationToken.None); }
        catch (Exception) { }
    }

    /// <summary>
    /// Hermes is signed in when its ACP server names a provider whose credentials resolve (<c>hermes model</c> sets one up); with none it
    /// only offers its terminal setup (M/MightyCore/HermesSupport.swift hermesStatus). No answer (an old Hermes, a missing ACP package, a
    /// timeout) stays unknown. The app signs nothing out: the row says a new <c>hermes model</c> changes the provider and <c>hermes auth</c>
    /// removes credentials, in a terminal.
    /// </summary>
    public static CliAccountStatus Status(Probe? probe)
    {
        if (probe is null) return new CliAccountStatus { Provider = "hermes", Detail = CliAccountStrings.DetailRunFailed, CanSignOut = false };
        if (!probe.Answered)
            return new CliAccountStatus { Provider = "hermes", Detail = Locale.Get(probe.TimedOut ? "settings.cliAccounts.detailHermesTimeout" : "settings.cliAccounts.detailHermesUnknown"), CanSignOut = false };
        if (probe.Provider is not { Length: > 0 } provider)
            return new CliAccountStatus { Provider = "hermes", LoggedIn = false, Detail = Locale.Get("settings.cliAccounts.detailHermesNoProvider"), CanSignOut = false };
        return new CliAccountStatus
        {
            Provider = "hermes", LoggedIn = true, Method = Locale.Get("cliAccounts.method.hermesProvider"), MethodId = CliAccountMethod.HermesProvider,
            Account = provider, Detail = Locale.Get("settings.cliAccounts.detailHermesSignOut"), CanSignOut = false,
        };
    }
}
