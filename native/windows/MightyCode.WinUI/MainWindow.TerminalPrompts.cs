using System.Text.Json;
using MightyCode.Core;

namespace MightyCode.WinUI;

/// <summary>
/// A command in an agent pane's terminal that waits for input (a yes/no question, a password, a line, a list) becomes a card in
/// that pane and on the phone; the user's answer is typed into the command's terminal as they would have typed it there. Nothing is
/// typed without an answer (docs/terminal-prompts.md; M/AppStore+TerminalPrompts.swift). The page's half is web/src/cards (TerminalPrompt).
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan TerminalPromptPollInterval = TimeSpan.FromMilliseconds(400);
    /// <summary>Polls with no command running before the watch ends: a command is started a moment after the watch is asked for.</summary>
    private const int TerminalPromptIdlePolls = 8;
    private readonly Dictionary<string, TerminalPromptMonitor> terminalPromptMonitors = new(StringComparer.Ordinal);
    /// <summary>The card each agent pane shows now, by the pane's id; the UI thread's only.</summary>
    private readonly Dictionary<string, TerminalPrompt> terminalPrompts = new(StringComparer.Ordinal);
    private bool terminalPromptWatching;

    private void RegisterTerminalPromptMethods(WebBridge bridge)
    {
        bridge.Register("terminalPromptAnswer", args =>
        {
            var answer = TerminalPromptAnswer.FromPhone(args.Text("promptId"), args.Text("answer"), args.Text("text")) ?? throw new ArgumentException(Locale.Get("terminalPrompt.error.badAnswer"));
            if (!AnswerTerminalPrompt(args.Text("paneId") ?? "", args.Text("promptId") ?? "", answer)) throw new InvalidOperationException(Locale.Get("terminalPrompt.error.gone"));
            return Task.FromResult<object?>(null);
        });
        bridge.Register("terminalPromptShow", async args => { await OpenAgentTerminalPane(args.Text("paneId") ?? ""); return null; });
    }

    /// <summary>Starts following the agent terminals for prompts, unless it already does; it ends by itself once no agent terminal runs a command.</summary>
    private async void WatchTerminalPrompts()
    {
        if (terminalPromptWatching || closing) return;
        terminalPromptWatching = true;
        try
        {
            for (var idle = 0; idle < TerminalPromptIdlePolls;)
            {
                await Task.Delay(TerminalPromptPollInterval);
                if (closing) return;
                idle = PollTerminalPrompts() ? 0 : idle + 1;
            }
        }
        finally { terminalPromptWatching = false; }
    }

    /// <summary>One look at every agent terminal; whether any command still runs.</summary>
    private bool PollTerminalPrompts()
    {
        var found = new Dictionary<string, TerminalPrompt>(StringComparer.Ordinal); var running = false;
        foreach (var (paneId, terminal) in agentTerminals)
        {
            if (terminal.RunningHandles().Count > 0) running = true;
            if (!terminalPromptMonitors.TryGetValue(paneId, out var monitor)) terminalPromptMonitors[paneId] = monitor = new TerminalPromptMonitor();
            if (monitor.Poll(terminal) is { } prompt) found[paneId] = prompt;
        }
        foreach (var gone in terminalPromptMonitors.Keys.Where(id => !agentTerminals.ContainsKey(id)).ToArray()) terminalPromptMonitors.Remove(gone);
        var changed = found.Keys.Union(terminalPrompts.Keys).Where(id => found.GetValueOrDefault(id)?.Id != terminalPrompts.GetValueOrDefault(id)?.Id).ToArray();
        if (changed.Length == 0) return running;
        terminalPrompts.Clear();
        foreach (var (id, prompt) in found) terminalPrompts[id] = prompt;
        foreach (var id in changed) TerminalPromptChanged(id);
        return running;
    }

    /// <summary>The user answered the card <paramref name="promptId"/> of the agent pane <paramref name="paneId"/>: the answer goes to the waiting command, and the card goes until it asks again. Whether it was typed.</summary>
    private bool AnswerTerminalPrompt(string paneId, string promptId, TerminalPromptAnswer answer)
    {
        if (!terminalPrompts.TryGetValue(paneId, out var prompt) || prompt.Id != promptId || !agentTerminals.TryGetValue(paneId, out var terminal)) return false;
        if (!terminal.WriteTo(prompt.Handle, answer.Input)) return false;
        terminalPromptMonitors.GetValueOrDefault(paneId)?.Answered(prompt);
        terminalPrompts.Remove(paneId);
        TerminalPromptChanged(paneId);
        return true;
    }

    /// <summary>The page redraws the pane's cards and the phone reads the pane again.</summary>
    private void TerminalPromptChanged(string paneId)
    {
        if (views.TryGetValue(paneId, out var view)) WebCardsChanged(view);
        mobileRouter?.Changed();
    }
}
