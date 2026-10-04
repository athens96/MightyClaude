using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

/// <summary>In-memory credentials; diagnostic formatting deliberately omits values.</summary>
public sealed class CliEnvironmentSnapshot(Dictionary<string, string> values, bool fromProfile)
{
    public Dictionary<string, string> Values { get; } = values;
    public bool FromProfile { get; } = fromProfile;
    public override string ToString() => FromProfile ? "CLI environment (terminal profile)" : "CLI environment (process/registry fallback)";
}

/// <summary>
/// Loads the same ConsoleHost profiles as the app's terminal, from the user's
/// home rather than an untrusted project. Normal PowerShell execution policy is
/// preserved. A prompt, broken profile or malformed capture falls back after a
/// bounded wait. Neither stream is logged or included in an exception.
/// </summary>
public sealed class WindowsShellEnvironment
{
    public const int MaximumOutput = 1_048_576;
    private readonly SemaphoreSlim gate = new(1);
    private readonly object sync = new();
    private readonly Func<IReadOnlyDictionary<string, string>, string, CancellationToken, Task<string?>> capture;
    private readonly TimeSpan timeout, lifetime;
    private Dictionary<string, string>? saved;
    private DateTimeOffset savedAt;

    public WindowsShellEnvironment(Func<IReadOnlyDictionary<string, string>, string, CancellationToken, Task<string?>>? capture = null,
        TimeSpan? timeout = null, TimeSpan? lifetime = null)
    {
        this.capture = capture ?? CaptureAsync;
        this.timeout = timeout ?? TimeSpan.FromSeconds(4);
        this.lifetime = lifetime ?? TimeSpan.FromSeconds(10);
    }

    public Dictionary<string, string> Current(IReadOnlyDictionary<string, string> baseline)
    {
        lock (sync) return Merge(baseline, saved is not null && DateTimeOffset.UtcNow - savedAt < lifetime ? saved : null);
    }

    public async Task<CliEnvironmentSnapshot> ResolveAsync(IReadOnlyDictionary<string, string> baseline, bool force = false, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            lock (sync) if (!force && saved is not null && DateTimeOffset.UtcNow - savedAt < lifetime) return new(Merge(baseline, saved), true);
            Dictionary<string, string>? next = null;
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation); bounded.CancelAfter(timeout);
            try
            {
                var nonce = Guid.NewGuid().ToString("N");
                var output = await capture(baseline, nonce, bounded.Token).ConfigureAwait(false);
                bounded.Token.ThrowIfCancellationRequested();
                if (output is not null) next = Parse(output, nonce);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or TimeoutException or UnauthorizedAccessException) { }
            cancellation.ThrowIfCancellationRequested();
            // Force failures discard a previous token too; an older successful
            // capture must never silently outlive a changed/broken profile.
            lock (sync) { saved = next; savedAt = DateTimeOffset.UtcNow; }
            return new(Merge(baseline, next), next is not null);
        }
        finally { gate.Release(); }
    }

    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> baseline, IReadOnlyDictionary<string, string>? captured)
    {
        var result = new Dictionary<string, string>(baseline, StringComparer.OrdinalIgnoreCase);
        if (captured is null) return result;
        // ChildProcess overlays its parent's environment. Explicit empty entries
        // prevent credentials unset by the fresh profile from being inherited.
        foreach (var key in result.Keys.ToArray()) if (!captured.ContainsKey(key)) result[key] = "";
        foreach (var pair in captured) result[pair.Key] = pair.Value;
        return result;
    }

    private static bool Nonce(string value) => value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string Command(string nonce)
    {
        if (!Nonce(nonce)) throw new ArgumentException("Invalid environment frame nonce.");
        // No credential values or paths appear in argv. Base64 keeps profile
        // chatter and newlines inside values outside the framing grammar.
        return "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);" +
            "$mightyEnvJson=Microsoft.PowerShell.Utility\\ConvertTo-Json -InputObject ([Environment]::GetEnvironmentVariables('Process')) -Compress;" +
            "$mightyEnvData=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($mightyEnvJson));" +
            "[Console]::Out.Write(([string][char]0)+'MIGHTY_ENV_" + nonce + "'+[char]0+$mightyEnvData+[char]0+'MIGHTY_END_" + nonce + "'+[char]0)";
    }

    public static string[] Arguments(string nonce) => ["-NoLogo", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Command(nonce)))];

    public static Dictionary<string, string>? Parse(string output, string nonce)
    {
        if (!Nonce(nonce) || Encoding.UTF8.GetByteCount(output) > MaximumOutput) return null;
        var start = "\0MIGHTY_ENV_" + nonce + "\0"; var end = "\0MIGHTY_END_" + nonce + "\0";
        var first = output.IndexOf(start, StringComparison.Ordinal);
        if (first < 0 || output.IndexOf(start, first + start.Length, StringComparison.Ordinal) >= 0) return null;
        var last = output.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        if (last < 0 || output.IndexOf(end, last + end.Length, StringComparison.Ordinal) >= 0) return null;
        var encoded = output.AsSpan(first + start.Length, last - first - start.Length);
        if (encoded.IsEmpty || encoded.Length % 4 != 0 || encoded.ContainsAny('\r', '\n', ' ')) return null;
        try
        {
            var bytes = Convert.FromBase64String(encoded.ToString());
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in doc.RootElement.EnumerateObject())
            {
                if (values.Count >= 4096 || pair.Name.Length is 0 or > 32767 || pair.Name.Contains('=') || pair.Name.Any(char.IsControl) || pair.Value.ValueKind != JsonValueKind.String) return null;
                var value = pair.Value.GetString()!;
                if (value.Contains('\0') || value.Length > 131072 || !values.TryAdd(pair.Name, value)) return null;
            }
            return values.TryGetValue("PATH", out var path) && !string.IsNullOrWhiteSpace(path) ? values : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException) { return null; }
    }

    private static async Task<string?> CaptureAsync(IReadOnlyDictionary<string, string> baseline, string nonce, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var shell = PseudoTerminal.DefaultShell;
        if (!Path.GetFileName(shell).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(shell).Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)) return null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Path.IsPathFullyQualified(home) || !Directory.Exists(home)) return null;
        await using var child = ChildProcess.Start(ChildProcess.StartInfo(shell, Arguments(nonce), home, new Dictionary<string, string>(baseline)));
        using var kill = cancellation.Register(child.Kill);
        child.Input.Close();
        var output = ReadAsync(child.Output, cancellation); var error = DiscardAsync(child.Error, cancellation);
        try
        {
            await Task.WhenAll(output, error, child.Completion.WaitAsync(cancellation)).ConfigureAwait(false);
            return child.Completion.Result == 0 ? output.Result : null;
        }
        catch { child.Kill(); throw; }
    }
    private static async Task<string> ReadAsync(StreamReader reader, CancellationToken cancellation)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; int read; var bytes = 0;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) > 0)
        {
            bytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read));
            if (bytes > MaximumOutput) throw new InvalidDataException("Environment capture exceeded its limit.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
    private static async Task DiscardAsync(StreamReader reader, CancellationToken cancellation)
    {
        var buffer = new char[4096]; int read; var count = 0;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) > 0)
        { count += read; if (count > MaximumOutput) throw new InvalidDataException("Environment capture exceeded its limit."); }
    }
}
