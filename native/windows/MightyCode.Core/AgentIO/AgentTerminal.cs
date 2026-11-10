using System.Text;

namespace MightyCode.Core;

public sealed record AgentTerminalResult(string Handle, string Status, string Output, int? ExitCode, bool OutputDropped, bool MoreRemains);

/// <summary>A pane-scoped terminal: handles and output never cross agent panes.</summary>
public sealed class AgentTerminal : IAsyncDisposable, IPromptTerminal
{
    private sealed class Command(string handle)
    {
        internal readonly string Handle = handle;
        internal PseudoTerminal? Process;
        internal readonly StringBuilder Pending = new();
        internal readonly TerminalTextCleaner Cleaner = new();
        internal bool Dropped, Finished;
        /// <summary>The process's own raw output, the last 1 MiB, for readers with their own cursor (<see cref="ReadOutput"/>); <see cref="LogStart"/> is the offset of its first character.</summary>
        internal readonly StringBuilder Log = new();
        internal int LogStart;
        internal int? ExitCode;
        internal Task Startup = Task.CompletedTask, Completion = Task.CompletedTask;
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Command> commands = [];
    private readonly StringBuilder screen = new();
    private readonly string directory;
    private string? active;
    private bool closing;
    public event Action<string>? Output;
    public AgentTerminal(string directory) { this.directory = directory; }
    public string Replay { get { lock (gate) return screen.ToString(); } }
    public bool IsRunning { get { lock (gate) return commands.Values.Any(c => !c.Finished); } }
    public async Task<AgentTerminalResult> Run(string text, CancellationToken cancellation)
    {
        var command = await Begin(text, null, cancellation);
        await Task.WhenAny(command.Completion, Task.Delay(TimeSpan.FromSeconds(12), cancellation));
        cancellation.ThrowIfCancellationRequested();
        return Read(command.Handle);
    }
    /// <summary>
    /// Starts <paramref name="text"/> as <see cref="Run"/> does, without waiting for it: its handle once the process started.
    /// <paramref name="output"/> gets the process's own output as it comes (not the echoed command, nor the exit line), on the
    /// reading thread. A transcript's shell block runs this way (MainWindow.ShellBlockRuns.cs).
    /// </summary>
    public async Task<string> Start(string text, Action<string>? output, CancellationToken cancellation) => (await Begin(text, output, cancellation)).Handle;
    /// <summary>The exit code of a started command once it ended (-1 when it could not be waited on).</summary>
    public async Task<int?> Exited(string handle)
    {
        Command command;
        lock (gate) command = commands.GetValueOrDefault(handle) ?? throw new ArgumentException("Unknown handle for this agent pane.");
        await command.Completion;
        lock (gate) return command.ExitCode;
    }
    private async Task<Command> Begin(string text, Action<string>? output, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > 65_536 || text.Contains('\0')) throw new ArgumentException("Invalid terminal command.");
        Command command;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            if (commands.Values.Count(c => !c.Finished) >= 8) throw new InvalidOperationException("Too many terminal processes in this pane.");
            while (commands.Count >= 64)
            {
                var oldest = commands.Values.FirstOrDefault(c => c.Finished) ?? throw new InvalidOperationException("Terminal history is full."); commands.Remove(oldest.Handle);
            }
            command = new(Wire.Id()); commands[command.Handle] = command; active = command.Handle;
            // Start is tracked while holding the lock, so closing cannot miss a
            // native process that is still being constructed on a worker thread.
            command.Startup = Task.Run(() =>
            {
                var shell = PseudoTerminal.DefaultShell;
                var args = PseudoTerminal.ShellArguments(shell, text, noProfile: true);
                var process = PseudoTerminal.Start(directory, value => { Receive(command, value); Record(command, value); output?.Invoke(value); }, executable: shell, arguments: args);
                lock (gate) command.Process = process;
            });
            command.Completion = Complete(command);
        }
        Receive(command, "\r\n> " + text + "\r\n");
        try { await command.Startup; }
        catch { lock (gate) { command.Finished = true; command.ExitCode = -1; } throw; }
        return command;
    }
    private void Record(Command command, string text)
    {
        lock (gate)
        {
            command.Log.Append(text);
            if (command.Log.Length > 1_048_576) { var cut = command.Log.Length - 1_048_576; command.Log.Remove(0, cut); command.LogStart += cut; }
        }
    }
    public IReadOnlyList<string> RunningHandles() { lock (gate) return commands.Values.Where(c => !c.Finished).Select(c => c.Handle).ToArray(); }
    public bool IsCommandRunning(string handle) { lock (gate) return commands.TryGetValue(handle, out var c) && !c.Finished; }
    /// <summary>The command's own output from <paramref name="offset"/> on, apart from what <see cref="Read"/> hands the agent (which it does not move).</summary>
    public PromptRead ReadOutput(string handle, int offset, int maxChars)
    {
        lock (gate)
        {
            if (!commands.TryGetValue(handle, out var c)) return new("", offset, false);
            var start = Math.Clamp(offset, c.LogStart, c.LogStart + c.Log.Length);
            var count = Math.Min(c.Log.Length - (start - c.LogStart), maxChars);
            if (count > 0 && char.IsHighSurrogate(c.Log[start - c.LogStart + count - 1])) count--;
            return new(c.Log.ToString(start - c.LogStart, count), start + count, start - c.LogStart + count < c.Log.Length);
        }
    }
    /// <summary>Types <paramref name="text"/> into the command's terminal as the user would have; whether it was taken.</summary>
    public bool WriteTo(string handle, string text) { lock (gate) return commands.TryGetValue(handle, out var c) && !c.Finished && c.Process?.TryWrite(text) == true; }
    private void Receive(Command command, string text)
    {
        lock (gate)
        {
            command.Pending.Append(text); screen.Append(text);
            if (command.Pending.Length > 1_048_576) { command.Pending.Remove(0, command.Pending.Length - 1_048_576); command.Dropped = true; command.Cleaner.Reset(); }
            if (screen.Length > 1_048_576) screen.Remove(0, screen.Length - 1_048_576);
        }
        Output?.Invoke(text);
    }
    private async Task Complete(Command command)
    {
        var code = -1;
        try
        {
            await command.Startup;
            var process = command.Process!;
            code = await process.Completion;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Native startup, wait or teardown failures are terminal states too.
            // Never leave a pollable handle permanently "running" or expose the
            // exception's command, environment or private paths in MCP output.
            code = -1;
        }
        finally
        {
            if (command.Process is { } process)
                try { await process.DisposeAsync(); } catch (Exception ex) when (ex is not OutOfMemoryException) { code = -1; }
            lock (gate) { command.ExitCode = code; command.Finished = true; }
        }
        Receive(command, "\r\n[exit " + code + "]\r\n");
    }
    public AgentTerminalResult Read(string handle)
    {
        lock (gate)
        {
            if (!commands.TryGetValue(handle, out var value)) throw new ArgumentException("Unknown handle for this agent pane.");
            var count = Math.Min(value.Pending.Length, 21_000); // <=63 KiB UTF-8 before cleaning
            if (count > 0 && char.IsHighSurrogate(value.Pending[count - 1])) count--;
            var output = value.Cleaner.Clean(value.Pending.ToString(0, count)); value.Pending.Remove(0, count);
            var result = new AgentTerminalResult(handle, value.Finished ? "done" : "running", output, value.Finished ? value.ExitCode : null, value.Dropped, value.Pending.Length > 0); value.Dropped = false; return result;
        }
    }
    public async Task<AgentTerminalResult> Stop(string handle)
    {
        Command command;
        lock (gate) command = commands.GetValueOrDefault(handle) ?? throw new ArgumentException("Unknown handle for this agent pane.");
        await command.Startup;
        if (!command.Finished && command.Process is { } process)
        {
            process.TryWrite("\u0003");
            if (await Task.WhenAny(command.Completion, Task.Delay(3000)) != command.Completion) await process.DisposeAsync();
            await command.Completion;
        }
        return Read(handle);
    }
    public bool Write(string text) { lock (gate) return active is { } id && commands[id].Process?.TryWrite(text) == true; }
    public void Resize(int columns, int rows) { lock (gate) foreach (var command in commands.Values.Where(c => !c.Finished)) command.Process?.Resize(columns, rows); }
    public async ValueTask DisposeAsync()
    {
        Command[] all; lock (gate) { if (closing) return; closing = true; all = commands.Values.ToArray(); }
        await Task.WhenAll(all.Select(async command => { try { await command.Startup; if (command.Process is { } process) await process.DisposeAsync(); await command.Completion; } catch (Exception ex) when (ex is not OutOfMemoryException) { } }));
    }
}

// Streaming ANSI/OSC removal, including escape sequences split across reads.
public sealed class TerminalTextCleaner(bool keepCarriageReturns = false)
{
    private int state;
    public void Reset() => state = 0;
    public string Clean(string input)
    {
        var result = new StringBuilder();
        foreach (var c in input)
        {
            switch (state)
            {
                case 1: state = c == '[' ? 2 : c is ']' or 'P' or '^' or '_' ? 3 : 0; break;
                case 2: if (c is >= '@' and <= '~') state = 0; break;
                case 3: if (c == '\a') state = 0; else if (c == '\u001b') state = 4; break;
                case 4: state = c == '\\' ? 0 : 3; break;
                default: if (c == '\u001b') state = 1; else if (!char.IsControl(c) || c is '\n' or '\t' || keepCarriageReturns && c == '\r') result.Append(c); break;
            }
        }
        return result.ToString();
    }
}
