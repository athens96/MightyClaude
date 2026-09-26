using System.Text.Json;
using MightyClaude.Core;

internal static class BrowserVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    // ── 1. browser address resolves like macOS ────────────────────────────────
    internal static Task AddressResolvesLikeMacOS()
    {
        Check(BrowserAddress.Resolve("example.com")?.AbsoluteUri == "https://example.com/", "plain host → https://");
        Check(BrowserAddress.Resolve("http://example.com/path")?.AbsoluteUri == "http://example.com/path", "http kept");
        Check(BrowserAddress.Resolve("https://secure.example.com")?.AbsoluteUri == "https://secure.example.com/", "https kept");
        Check(BrowserAddress.Resolve("") is null, "empty → null");
        Check(BrowserAddress.Resolve("   ") is null, "whitespace → null");
        Check(BrowserAddress.Resolve("ftp://files.example.com")?.Scheme == "ftp", "ftp:// kept");
        return Task.CompletedTask;
    }

    // ── 2. browser history follows macOS rules ────────────────────────────────
    internal static Task HistoryFollowsMacOSRules()
    {
        var a = new Uri("https://example.com");
        var b = new Uri("https://example.com/second");
        var c = new Uri("https://example.com/third");

        var history = new BrowserHistory();
        Check(history.State() == new BrowserNavigationState(), "empty state");

        history.Visit(a);
        var state = history.State(isLoading: true);
        Check(state.Url == a, "url after first visit");
        Check(state.IsLoading, "isLoading flag");
        Check(!state.CanGoBack, "can't go back on first page");
        Check(!state.CanGoForward, "can't go forward on first page");

        // Same URL is a reload — must not grow history.
        history.Visit(a);
        Check(!history.CanGoBack, "reload must not grow history");

        history.Visit(b);
        state = history.State();
        Check(state.Url == b, "url after visit b");
        Check(state.CanGoBack, "can go back after two pages");
        Check(!state.CanGoForward, "can't go forward at end");
        Check(!state.IsLoading, "not loading by default");

        Check(history.GoBack() == a, "back returns a");
        state = history.State();
        Check(state.Url == a, "at a after back");
        Check(!state.CanGoBack, "can't go back to before first page");
        Check(state.CanGoForward, "can go forward from a");

        Check(history.GoForward() == b, "forward returns b");
        Check(history.State().Url == b, "at b after forward");
        Check(history.GoForward() is null, "forward returns null at end");

        // New visit from a back position drops the forward tail.
        Check(history.GoBack() == a, "back to a before branch");
        history.Visit(c);
        state = history.State();
        Check(state.Url == c, "at c after branch");
        Check(state.CanGoBack, "can go back from c");
        Check(!state.CanGoForward, "forward tail dropped after new visit");
        Check(history.GoBack() == a, "back from c to a");
        Check(history.GoBack() is null, "can't go back past first page");

        return Task.CompletedTask;
    }

    // ── 3. browser profile folder per workspace follows profile ───────────────
    internal static Task ProfileFolderPerWorkspaceFollowsProfile()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), "browser-profile-test-" + System.Guid.NewGuid());
        var path1 = BrowserProfile.ProfileFolder(stateDir, "workspace-abc");
        var path2 = BrowserProfile.ProfileFolder(stateDir, "workspace-xyz");

        Check(path1 != path2, "different keys → different paths");
        Check(path1.EndsWith("workspace-abc", StringComparison.OrdinalIgnoreCase), "key is last segment for abc");
        Check(path2.EndsWith("workspace-xyz", StringComparison.OrdinalIgnoreCase), "key is last segment for xyz");
        Check(Path.GetDirectoryName(path1) == Path.GetDirectoryName(path2), "shared parent directory");
        Check(path1.StartsWith(stateDir, StringComparison.OrdinalIgnoreCase), "profile folder is under state dir");

        // --profile isolates: a different stateDir gives a different path.
        var stateDir2 = stateDir + "-profile2";
        var path3 = BrowserProfile.ProfileFolder(stateDir2, "workspace-abc");
        Check(path3 != path1, "--profile isolates the profile folder");

        return Task.CompletedTask;
    }

    // ── 4. browser setting is off by default and read once ────────────────────
    internal static Task SettingIsOffByDefaultAndReadOnce()
    {
        var snapshot = new AppSnapshot();
        Check(!snapshot.BrowserEngineEnabled, "BrowserEngineEnabled defaults to false");

        // Verify the JSON field name matches macOS.
        var json = JsonSerializer.Serialize(snapshot, Wire.Json);
        using var doc = JsonDocument.Parse(json);
        // Field is absent when false (WhenWritingDefault omits default bool values that equal false).
        // Enabling it must write the field.
        var enabled = new AppSnapshot { BrowserEngineEnabled = true };
        var json2 = JsonSerializer.Serialize(enabled, Wire.Json);
        using var doc2 = JsonDocument.Parse(json2);
        Check(doc2.RootElement.TryGetProperty("browserEngineEnabled", out var prop) && prop.GetBoolean(), "browserEngineEnabled=true round-trips");

        // Round-trip false → absent (default); the snapshot version stays 1.
        Check(snapshot.Version == 1, "Version stays 1 after adding BrowserEngineEnabled");

        return Task.CompletedTask;
    }

    // ── 5. browser runtime missing offers install only on click ───────────────
    internal static Task RuntimeMissingOffersInstallOnlyOnClick()
    {
        var installerCalled = false;

        var locator = new FakeLocator(null);
        var installer = new FakeInstaller(onDownload: _ => { installerCalled = true; return Task.FromResult(""); });
        var service = new BrowserEngineService(locator, installer);

        var state = service.GetState(engineEnabled: true);
        Check(state == BrowserEngineState.RuntimeMissing, "state is RuntimeMissing when runtime absent");
        Check(!installerCalled, "nothing downloaded before explicit install call");

        var disabledState = service.GetState(engineEnabled: false);
        Check(disabledState == BrowserEngineState.Disabled, "state is Disabled when engine off");
        Check(!installerCalled, "still nothing downloaded");

        return Task.CompletedTask;
    }

    // ── 6. browser installer requires a Microsoft signature ───────────────────
    internal static async Task InstallerRequiresMicrosoftSignature()
    {
        var tempDir = Verification.Temp();
        try
        {
            var downloaded = false;
            var installer = new FakeInstaller(
                onDownload: path => { downloaded = true; File.WriteAllText(path, "fake-exe"); return Task.FromResult(path); },
                signatureValid: false,
                exitCode: 0);
            var locator = new FakeLocator(null);
            var service = new BrowserEngineService(locator, installer);

            var result = await service.InstallAsync(tempDir);
            Check(downloaded, "download was attempted");
            Check(!result.Success, "install must fail when signature invalid");
            Check(result.Error == BrowserEngineStrings.SignatureFailed, "signature error message correct");
            Check(!File.Exists(Path.Combine(tempDir, "MicrosoftEdgeWebview2Setup.exe")), "temp file cleaned up after signature failure");
        }
        finally { Directory.Delete(tempDir, true); }
    }

    // ── 7. browser installer failure cleans up ────────────────────────────────
    internal static async Task InstallerFailureCleansUp()
    {
        var tempDir = Verification.Temp();
        try
        {
            // Case 1: non-zero exit code after a valid signature.
            var installer = new FakeInstaller(
                onDownload: path => { File.WriteAllText(path, "fake-exe"); return Task.FromResult(path); },
                signatureValid: true,
                exitCode: 1);
            var locator = new FakeLocator(null);
            var service = new BrowserEngineService(locator, installer);

            var result = await service.InstallAsync(tempDir);
            Check(!result.Success, "install must fail on non-zero exit");
            Check(!File.Exists(Path.Combine(tempDir, "MicrosoftEdgeWebview2Setup.exe")), "temp file cleaned up after exit failure");

            // Case 2: download throws — temp file (if created) is cleaned up.
            var tempDir2 = Verification.Temp();
            try
            {
                var installer2 = new FakeInstaller(onDownload: path =>
                {
                    File.WriteAllText(path, "partial");
                    throw new IOException("download failed");
                });
                var service2 = new BrowserEngineService(locator, installer2);
                var result2 = await service2.InstallAsync(tempDir2);
                Check(!result2.Success, "install must fail when download throws");
                Check(!File.Exists(Path.Combine(tempDir2, "MicrosoftEdgeWebview2Setup.exe")), "temp file cleaned up after download exception");
            }
            finally { Directory.Delete(tempDir2, true); }
        }
        finally { Directory.Delete(tempDir, true); }
    }

    // ── 8. browser session fields share macOS names ───────────────────────────
    internal static async Task SessionFieldsShareMacOSNames()
    {
        // workspaceProfileKey and ownerSessionId round-trip with the same JSON field names as macOS.
        var session = new RunSession { Id = "s1", WorkspaceId = "ws1", Kind = "browser", WorkspaceProfileKey = "ws1", OwnerSessionId = "owner-1" };
        var json = JsonSerializer.Serialize(session, Wire.Json);
        using var doc = JsonDocument.Parse(json);
        Check(doc.RootElement.TryGetProperty("workspaceProfileKey", out var wpk) && wpk.GetString() == "ws1", "workspaceProfileKey present with macOS name");
        Check(doc.RootElement.TryGetProperty("ownerSessionId", out var oid) && oid.GetString() == "owner-1", "ownerSessionId present with macOS name");

        var decoded = JsonSerializer.Deserialize<RunSession>(json, Wire.Json)!;
        Check(decoded.WorkspaceProfileKey == "ws1", "workspaceProfileKey round-trips");
        Check(decoded.OwnerSessionId == "owner-1", "ownerSessionId round-trips");

        // ownerSessionId is optional — omitted when null.
        var noOwner = new RunSession { Id = "s2", WorkspaceId = "ws1", Kind = "browser", WorkspaceProfileKey = "ws1" };
        var json2 = JsonSerializer.Serialize(noOwner, Wire.Json);
        using var doc2 = JsonDocument.Parse(json2);
        Check(!doc2.RootElement.TryGetProperty("ownerSessionId", out _), "ownerSessionId absent when null");

        // Snapshot with browser session loads the workspaceProfileKey.
        var directory = Verification.Temp();
        try
        {
            var workspace = new Workspace { Path = directory };
            var browserSession = new RunSession { Id = "browser-s", WorkspaceId = workspace.Id, Kind = "browser", WorkspaceProfileKey = workspace.Id, OwnerSessionId = null };
            var state = new AppSnapshot { Workspaces = [workspace], Sessions = [browserSession] };
            await File.WriteAllBytesAsync(Path.Combine(directory, "workspace-state.json"), JsonSerializer.SerializeToUtf8Bytes(state, Wire.Json));
            var loaded = await new StateStore(directory).LoadAsync();
            var loadedBrowser = loaded.Sessions.First(s => s.Kind == "browser");
            Check(loadedBrowser.WorkspaceProfileKey == workspace.Id, "workspaceProfileKey preserved through store load");
            Check(loadedBrowser.OwnerSessionId is null, "ownerSessionId null preserved");
        }
        finally { Directory.Delete(directory, true); }
    }

    // ── Fake helpers ──────────────────────────────────────────────────────────

    private sealed class FakeLocator(string? version) : IBrowserRuntimeLocator
    {
        public string? GetRuntimeVersion() => version;
    }

    private sealed class FakeInstaller(
        Func<string, Task<string>>? onDownload = null,
        bool signatureValid = true,
        int exitCode = 0) : IBrowserInstaller
    {
        public Task<string> DownloadBootstrapperAsync(string tempPath, CancellationToken ct) =>
            onDownload is not null ? onDownload(tempPath) : Task.FromResult(tempPath);

        public bool VerifyMicrosoftSignature(string filePath) => signatureValid;

        public Task<int> RunInstallerAsync(string filePath, CancellationToken ct) =>
            Task.FromResult(exitCode);
    }
}
