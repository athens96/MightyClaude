using MightyCode.Core;

namespace MightyCode.WinUI;

/// <summary>
/// A transcript's shell block run (spec: run-shell-blocks; the page's half is native/windows/web/src/conversation/ShellBlock.tsx).
/// The page's Run calls <c>shellBlockRun {paneId, key, command}</c>: the command, which must be a line-for-line copy of what that
/// assistant entry shows (<see cref="ShellBlockRunSupport.Shown"/>), runs in the agent pane's own terminal, the one
/// <c>run_in_terminal</c> uses (<see cref="agentTerminals"/>, made here when the agent has not opened it yet), in the workspace
/// folder (a delegated child's worktree), and its terminal pane opens beside the agent. The pane's <c>shellRuns</c> record (<see cref="DesktopService.UpdateShellBlockRun"/>)
/// carries the state and the cleaned output tail to the page, live a few times a second. <c>shellBlockStop {paneId, key}</c>
/// stops it as <c>stop</c> stops an agent's command (Ctrl+C, then the process ended after 3 s). One block runs at a time per pane.
/// The run is the user's own click: it does not pass the agent's tool permissions.
/// </summary>
public sealed partial class MainWindow
{
    private sealed class ShellBlockRunning(string paneId, string key, AgentTerminal terminal)
    {
        internal readonly string PaneId = paneId, Key = key;
        internal readonly AgentTerminal Terminal = terminal;
        internal readonly ShellBlockOutput Output = new();
        internal readonly object Gate = new();
        internal string? Handle;
        internal bool Stopping, FlushQueued;
    }

    /// <summary>The block running from each agent pane, by the pane's id; the UI thread's only.</summary>
    private readonly Dictionary<string, ShellBlockRunning> shellBlockRunning = new(StringComparer.Ordinal);

    private void RegisterShellBlockMethods(WebBridge bridge)
    {
        bridge.Register("shellBlockRun", async args => { await Act(() => RunShellBlock(args.Text("paneId"), args.Text("key"), args.Text("command"))); return null; });
        bridge.Register("shellBlockStop", async args => { await Act(() => StopShellBlock(args.Text("paneId"), args.Text("key"))); return null; });
    }

    private async Task RunShellBlock(string? paneId, string? key, string? command)
    {
        command = command?.TrimEnd('\r', '\n');
        if (closing || !Wire.Identifier(paneId) || !ShellBlockRunSupport.IsKey(key) || !ShellBlockRunSupport.IsCommand(command)) return;
        var state = service.Snapshot;
        if (state.Sessions.FirstOrDefault(p => p.Id == paneId && p.Kind == "claude") is not { } pane) return;
        // No pane tools for a remote workspace (InitializeAgentIO): its folder is on another computer.
        if (LocalPath(state.Workspaces.FirstOrDefault(w => w.Id == pane.WorkspaceId)) is not { } folder) return;
        if (!ShellBlockRunSupport.Shown(pane, key!, command!) || shellBlockRunning.ContainsKey(pane.Id)) return;
        // A delegated child works in its worktree, as its runs and its run_in_terminal do (RunManager: request.WorkingFolder).
        var directory = DelegationPanes.IsWorkingFolder(pane.WorkingFolder) && Directory.Exists(pane.WorkingFolder) ? pane.WorkingFolder! : folder;
        if (!agentTerminals.TryGetValue(pane.Id, out var terminal)) agentTerminals[pane.Id] = terminal = new AgentTerminal(directory);
        await service.UpdateAsync(snapshot => WithAgentIOPane(snapshot, pane.Id, pane.WorkspaceId, AgentIOPaneKind.Terminal));
        HideDashboard(); Render();
        // The await above let another click in: still one block at a time.
        if (closing || shellBlockRunning.ContainsKey(pane.Id)) return;
        var running = new ShellBlockRunning(pane.Id, key!, terminal);
        shellBlockRunning[pane.Id] = running;
        service.UpdateShellBlockRun(pane.Id, running.Key, _ => ShellBlockRunSupport.Started(running.Key, command!));
        try { running.Handle = await terminal.Start(command!, raw => ReceiveShellBlockOutput(running, raw), CancellationToken.None); }
        catch
        {
            shellBlockRunning.Remove(pane.Id);
            service.UpdateShellBlockRun(pane.Id, running.Key, run => run is { State: "running" } ? ShellBlockRunSupport.Finished(run, null, false) : run);
            throw;
        }
        WatchTerminalPrompts();
        _ = FinishShellBlock(running, running.Handle);
        // A Stop pressed while the command was still starting is kept and done now.
        if (running.Stopping) await running.Terminal.Stop(running.Handle);
    }

    /// <summary>On the UI thread once the command ended: the record takes its last output and its end.</summary>
    private async Task FinishShellBlock(ShellBlockRunning running, string handle)
    {
        int? code;
        try { code = await running.Terminal.Exited(handle); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { code = -1; }
        if (shellBlockRunning.GetValueOrDefault(running.PaneId) == running) shellBlockRunning.Remove(running.PaneId);
        // Quitting, the record stays running and the next launch reads it interrupted (ShellBlockRunSupport.Normalized).
        if (closing) return;
        string output; lock (running.Gate) output = running.Output.Text;
        service.UpdateShellBlockRun(running.PaneId, running.Key, run => run is { State: "running" } ? ShellBlockRunSupport.Finished(run with { Output = output }, code, running.Stopping) : run);
    }

    /// <summary>On the terminal's reading thread: the output is cleaned as it comes and the record takes it at most every 200 ms.</summary>
    private void ReceiveShellBlockOutput(ShellBlockRunning running, string raw)
    {
        lock (running.Gate)
        {
            running.Output.Append(raw);
            if (running.FlushQueued) return;
            running.FlushQueued = true;
        }
        _ = Task.Delay(200).ContinueWith(_ =>
        {
            string output;
            lock (running.Gate) { running.FlushQueued = false; output = running.Output.Text; }
            service.UpdateShellBlockRun(running.PaneId, running.Key, run => run is { State: "running" } && run.Output != output ? run with { Output = output } : run);
        }, TaskScheduler.Default);
    }

    private async Task StopShellBlock(string? paneId, string? key)
    {
        if (paneId is null || !shellBlockRunning.TryGetValue(paneId, out var running) || running.Key != key) return;
        running.Stopping = true;
        // Still starting: RunShellBlock stops it once the terminal gave its handle.
        if (running.Handle is { } handle) await running.Terminal.Stop(handle);
    }
}
