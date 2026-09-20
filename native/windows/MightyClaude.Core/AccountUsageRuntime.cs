using System.Net;
using System.Net.Http;

namespace MightyClaude.Core;

/// The transports the running app uses. Every check injects its own instead, so
/// nothing here is ever exercised by a test or by the smoke run.
public static class AccountUsageRuntime
{
    /// One ephemeral request: no cookies, no credential store, no redirect
    /// followed, a 10 second timeout and a 1 MiB body cap. There is no retry.
    public static async Task<AccountUsageHttpResponse> SendAsync(AccountUsageHttpRequest request, CancellationToken cancellation)
    {
        ClaudeAccountProbe.Guard(request.Url);
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
            Credentials = null,
        };
        using var client = new HttpClient(handler) { Timeout = request.Timeout, MaxResponseContentBufferSize = AccountUsageSupport.MaximumBodyBytes };
        using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);
        foreach (var (name, value) in request.Headers) message.Headers.TryAddWithoutValidation(name, value);
        using var reply = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation);
        var body = (int)reply.StatusCode is >= 200 and < 300 ? await reply.Content.ReadAsStringAsync(cancellation) : "";
        var redirect = reply.StatusCode is >= (HttpStatusCode)300 and < (HttpStatusCode)400 ? reply.Headers.Location?.ToString() ?? "refused" : null;
        return new AccountUsageHttpResponse((int)reply.StatusCode, body, reply.Headers.TryGetValues("Retry-After", out var retry) ? retry.FirstOrDefault() : null, redirect);
    }

    /// `codex app-server` over stdio. The CLI owns its own sign-in; the app
    /// never sees it, and the child dies with the probe.
    private sealed class CodexStdio(CliCommand command, string workingDirectory) : IAccountUsageStdio
    {
        private ChildProcess? child = ChildProcess.Start(ChildProcess.StartInfo(command.Binary,
            [.. command.Prefix, "app-server", "--listen", "stdio://"], workingDirectory));
        public Task WriteLineAsync(string line, CancellationToken cancellation) => child is null ? Task.CompletedTask : child.Input.WriteLineAsync(line.AsMemory(), cancellation).ContinueWith(_ => child.Input.FlushAsync(), cancellation).Unwrap();
        public Task<string?> ReadLineAsync(CancellationToken cancellation) => child is null ? Task.FromResult<string?>(null) : child.Output.ReadLineAsync(cancellation).AsTask();
        public async ValueTask DisposeAsync() { var running = child; child = null; if (running is not null) { running.Kill(); await running.DisposeAsync(); } }
    }

    /// The probe the running app gives AccountUsageService. Claude is only ever
    /// reached when the caller already checked the saved switch.
    public static Func<string, CancellationToken, Task<AccountUsageSnapshot>> Probe(
        Func<string, CancellationToken, Task<CliCommand?>> find,
        Func<IReadOnlyDictionary<string, string>> environment,
        Func<string> home,
        string workingDirectory) => async (provider, cancellation) =>
    {
        switch (provider)
        {
            case "claude":
            {
                var env = environment();
                return await ClaudeAccountProbe.ReadAsync(env,
                    () => ClaudeCredentialFile.Read(home(), env, DateTimeOffset.UtcNow),
                    SendAsync, () => DateTimeOffset.UtcNow, cancellation);
            }
            case "codex":
            {
                var command = await find("codex", cancellation)
                    ?? throw new AccountUsageFailure(AccountUsageFailureKind.Unavailable, AccountUsageStrings.DetailCodexNotInstalled);
                return await CodexAccountProbe.ReadAsync(() => new CodexStdio(command, workingDirectory), CodexAccountProbe.Timeout, cancellation);
            }
            case "gemini":
                throw new AccountUsageFailure(AccountUsageFailureKind.Unavailable, AccountUsageStrings.DetailGeminiUnavailable);
            default:
                throw new AccountUsageFailure(AccountUsageFailureKind.Unavailable, AccountUsageStrings.DetailUnsupportedProvider);
        }
    };
}
