namespace MightyClaude.Core;

public enum BrowserEngineState { Disabled, RuntimeMissing, Installing, Ready, Failed }

// Checks for the WebView2 Evergreen runtime.
public interface IBrowserRuntimeLocator
{
    string? GetRuntimeVersion();
}

// Downloads and runs the Microsoft per-user bootstrapper.
// Implementations must check the Authenticode signature before executing.
public interface IBrowserInstaller
{
    Task<string> DownloadBootstrapperAsync(string tempPath, CancellationToken ct);
    bool VerifyMicrosoftSignature(string filePath);
    Task<int> RunInstallerAsync(string filePath, CancellationToken ct);
}

public sealed record BrowserInstallResult(bool Success, string? Error = null);

public static class BrowserEngineStrings
{
    public static readonly string RuntimeMissing = Locale.Get("browser.runtime.missing");
    public static readonly string Install = Locale.Get("browser.runtime.install");
    public static readonly string Installing = Locale.Get("browser.runtime.installing");
    public static readonly string InstallSuccess = Locale.Get("browser.runtime.installSuccess");
    public static readonly string InstallFailed = Locale.Get("browser.runtime.installFailed");
    public static readonly string SignatureFailed = Locale.Get("browser.runtime.signatureFailed");
    public static readonly string EngineDisabled = Locale.Get("browser.engine.disabled");
    public static readonly string EngineFailed = Locale.Get("browser.engine.failed");
}

// Orchestrates the install flow.
// Nothing is downloaded or run until InstallAsync is explicitly called.
public sealed class BrowserEngineService(IBrowserRuntimeLocator locator, IBrowserInstaller installer)
{
    public BrowserEngineState GetState(bool engineEnabled)
    {
        if (!engineEnabled) return BrowserEngineState.Disabled;
        return locator.GetRuntimeVersion() is not null
            ? BrowserEngineState.Ready
            : BrowserEngineState.RuntimeMissing;
    }

    public async Task<BrowserInstallResult> InstallAsync(string tempDirectory, CancellationToken ct = default)
    {
        var tempFile = Path.Combine(tempDirectory, "MicrosoftEdgeWebview2Setup.exe");
        try
        {
            await installer.DownloadBootstrapperAsync(tempFile, ct);
            if (!installer.VerifyMicrosoftSignature(tempFile))
                return new(false, BrowserEngineStrings.SignatureFailed);
            var exit = await installer.RunInstallerAsync(tempFile, ct);
            if (exit != 0)
                return new(false, BrowserEngineStrings.InstallFailed);
            return new(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, BrowserEngineStrings.InstallFailed);
        }
        finally
        {
            if (File.Exists(tempFile)) try { File.Delete(tempFile); } catch { }
        }
    }
}
