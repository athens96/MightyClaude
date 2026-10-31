using System.Diagnostics;
using System.Text.Json;
using MightyClaude.Core;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// The web smoke's Git sheet: the sidebar's add-workspace menu opens Git으로 생성 in the page, the address of a bare repository
    /// made in the smoke profile is typed into it key by key (CDP insertText, as a person types), the check must read reachable,
    /// the folder name must follow the address and 생성 must be enabled; the sheet is then cancelled. No network, no clipboard.
    /// </summary>
    private async Task<bool> RunWebGitCreateSmoke(Microsoft.Web.WebView2.Core.CoreWebView2 core, Dictionary<string, object?> result)
    {
        async Task<JsonElement> Probe(string script) => JsonDocument.Parse(JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("JSON.stringify(" + script + ")")) ?? "null").RootElement.Clone();
        async Task<bool> Until(Func<Task<bool>> done, double seconds = 15)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) { if (await done()) return true; await Task.Delay(50); }
            return false;
        }
        var checks = new Dictionary<string, object?>(); result["gitCreate"] = checks;
        var directory = options.ProfileDirectory ?? throw new InvalidOperationException("smoke profile missing");
        if (GitTool.Executable is not { } git) { checks["git"] = false; return false; }
        var remote = Path.Combine(directory, "git-remote", "smoke-remote.git");
        Directory.CreateDirectory(remote);
        using (var init = Process.Start(new ProcessStartInfo(git) { ArgumentList = { "init", "--bare", "--quiet", remote }, UseShellExecute = false, CreateNoWindow = true }))
            if (init is null || !init.WaitForExit(20_000) || init.ExitCode != 0) { checks["bareRepository"] = false; return false; }
        const string Sheet = "(() => { const u = document.querySelector('[data-testid=workspace-git-url]'); if (!u) return null; return { url: u.value, name: document.querySelector('[data-testid=workspace-git-name]')?.value ?? null, "
            + "probe: document.querySelector('[data-testid=workspace-git-probe]')?.textContent ?? null, reachable: !!document.querySelector('.create-probe .tone-text-done'), path: document.querySelector('[data-testid=workspace-create-path]')?.textContent ?? null, "
            + "problem: document.querySelector('[data-testid=workspace-create-problem]')?.textContent ?? null, enabled: document.querySelector('[data-testid=workspace-git-create]')?.disabled === false }; })()";
        async Task<bool> Open()
        {
            await core.ExecuteScriptAsync("document.querySelector('[data-testid=sidebar-add-workspace]')?.click()");
            if (!await Until(async () => (await Probe("!!document.querySelector('[data-testid=workspace-add-fromGit]')")).ValueKind == JsonValueKind.True, 5)) return false;
            await core.ExecuteScriptAsync("document.querySelector('[data-testid=workspace-add-fromGit]')?.click()");
            return await Until(async () => (await Probe("document.activeElement?.dataset?.testid === 'workspace-git-url'")).ValueKind == JsonValueKind.True, 10);
        }
        async Task Type(string text, bool perCharacter)
        {
            foreach (var piece in perCharacter ? text.Select(c => c.ToString()) : [text])
            {
                await core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = piece }));
                if (perCharacter) await Task.Delay(30);
            }
        }
        async Task<bool> Closed()
        {
            await core.ExecuteScriptAsync("document.querySelector('[data-testid=workspace-git-cancel]')?.click()");
            return await Until(async () => !dialogOpen && (await Probe("!!document.querySelector('[data-testid=workspace-git-url]')")).ValueKind == JsonValueKind.False, 10);
        }
        var passed = true;
        // The path typed key by key, then its file:// address inserted at once as a paste would.
        foreach (var (name, url, perCharacter) in new[] { ("typed", remote, true), ("pasted", new Uri(remote).AbsoluteUri, false) })
        {
            var step = new Dictionary<string, object?>(); checks[name] = step;
            var opened = await Open(); step["opened"] = opened;
            if (!opened) { passed = false; break; }
            await Type(url, perCharacter);
            JsonElement sheet = default;
            // The name follows the address, the check reads reachable and 생성 follows the host's answer for that name.
            var ready = await Until(async () => (sheet = await Probe(Sheet)).ValueKind == JsonValueKind.Object && sheet.GetProperty("reachable").GetBoolean()
                && sheet.Text("name") == "smoke-remote" && sheet.GetProperty("enabled").GetBoolean(), 25);
            step["ready"] = ready; step["sheet"] = sheet;
            var closed = await Closed(); step["closed"] = closed;
            passed &= ready && closed;
        }
        checks["passed"] = passed;
        return passed;
    }
}
