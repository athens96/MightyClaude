using System.Text;
using MightyClaude.Core;

internal static class PseudoTerminalVerification
{
    internal static async Task PersistentShellAndShutdown()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This verification needs real Windows ConPTY.");
        var path = Verification.Temp();
        var text = new StringBuilder(); var gate = new object();
        string Output() { lock (gate) return text.ToString(); }
        try
        {
            var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var terminal = PseudoTerminal.Start(path, value => { lock (gate) text.Append(value); }, executable: cmd, arguments: ["/d", "/q"]);
            try
            {
                var marker = "MIGHTY_" + Guid.NewGuid().ToString("N");
                if (!terminal.TryWrite("set MIGHTY_TERMINAL_VALUE=" + marker[..15] + "\r")) throw new InvalidOperationException("First input was rejected.");
                terminal.TryWrite("set MIGHTY_TERMINAL_VALUE=%MIGHTY_TERMINAL_VALUE%" + marker[15..] + "\r");
                if (!terminal.TryWrite("echo %MIGHTY_TERMINAL_VALUE%\r")) throw new InvalidOperationException("Second input was rejected.");
                await Verification.Until(() => Output().Contains(marker), 15000);
                terminal.Resize(80, 24);
                if (terminal.TryWrite(new string('x', 65_537))) throw new InvalidOperationException("Oversized input was accepted.");
                terminal.TryWrite("exit /b 7\r");
                if (await terminal.Completion.WaitAsync(TimeSpan.FromSeconds(15)) != 7) throw new InvalidOperationException("Shell exit code was lost.");
            }
            finally { await terminal.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)); }
            if (terminal.TryWrite("echo after-close\r")) throw new InvalidOperationException("Closed terminal accepted input.");
            await terminal.DisposeAsync();
        }
        finally { Directory.Delete(path, true); }
    }
}
