using System.Text.Json;

namespace MightyClaude.Core;

/// <summary>
/// What 바로 실행 on a queued row does (docs/superpowers/specs/2026-10-29-queue-run-now-design.md; M/QueueRunNow.swift). Checked
/// against native/contracts/fixtures/queue-run-now.json.
/// </summary>
public enum QueueRunNowPath
{
    /// <summary>A new child pane of the pane's own agent, in its own worktree beside it, runs the row now; a right-click picks another agent.</summary>
    Worktree,
    /// <summary>The pane's own agent cannot have a child in its mode, another agent can: a click opens the agent menu.</summary>
    AgentMenu,
    /// <summary>The row starts now as the pane's next run, the items held for the pane ahead of it.</summary>
    Here,
    /// <summary>The row goes into the pane's run that only waits on background work, as a new turn (중간 요청).</summary>
    Steer,
    /// <summary>The first held row: every item held for the pane goes to it now, together.</summary>
    Held,
    /// <summary>Disabled: a turn runs and neither a new worktree nor a steer can take the row; it runs in its turn.</summary>
    WaitsForTurn,
    /// <summary>Disabled: the row could go into the run that only waits on background work, but items held for the pane go first.</summary>
    WaitsForHeld,
    /// <summary>Disabled: the pane is held for a CLI update.</summary>
    WaitsForUpdate,
    /// <summary>No button: a held row after the first (the first one's button sends them all).</summary>
    Hidden,
}

public static class QueueRunNow
{
    /// <summary>The pane as 바로 실행 reads it.</summary>
    /// <param name="Busy">Running, starting, or held for a CLI update.</param>
    /// <param name="HeldForUpdate">Held for a CLI update.</param>
    /// <param name="SecondSend"><see cref="ComposerSecondSends.Choose"/>: <see cref="ComposerSecondSend.Worktree"/> where a new child of the pane's agent can be made now.</param>
    /// <param name="MenuOnly"><see cref="ComposerSecondSends.MenuOnly"/>.</param>
    /// <param name="WaitsOnBackgroundOnly">A local Claude pane, not stopping, whose turn ended and whose background work still runs.</param>
    /// <param name="Held">Items delegation holds for the pane.</param>
    public sealed record Pane(bool Busy, bool HeldForUpdate, ComposerSecondSend SecondSend, bool MenuOnly, bool WaitsOnBackgroundOnly, int Held);

    /// <summary>
    /// Nothing a person sends joins a running turn, so a busy pane that can make no worktree and is not only waiting on background
    /// work can only say why the row waits. Held items go ahead of the pane's next request, so a human row is never steered past
    /// them. <paramref name="heldIndex"/>: the row's place among the held rows, null for a human row. <paramref name="continuation"/>:
    /// the row continues an Antigravity permission card (<see cref="QueuedComposerInput.AntigravityGrant"/> or
    /// <see cref="QueuedComposerInput.AntigravityCard"/>); its allow rules and card belong to this pane's next run, so it never goes
    /// to a worktree child nor into a running turn: here when idle, else it waits.
    /// </summary>
    public static QueueRunNowPath Decide(int? heldIndex, string text, int attachments, Pane pane, bool continuation = false)
    {
        if (heldIndex is { } index) return index == 0 ? QueueRunNowPath.Held : QueueRunNowPath.Hidden;
        if (!pane.Busy && !pane.HeldForUpdate) return QueueRunNowPath.Here;
        if (pane.HeldForUpdate) return QueueRunNowPath.WaitsForUpdate;
        if (continuation) return QueueRunNowPath.WaitsForTurn;
        // A new worktree needs written words for its task; files alone wait.
        var words = !string.IsNullOrWhiteSpace(text);
        if (words && pane.SecondSend == ComposerSecondSend.Worktree) return QueueRunNowPath.Worktree;
        if (words && pane.MenuOnly) return QueueRunNowPath.AgentMenu;
        if (pane.WaitsOnBackgroundOnly && attachments == 0) return pane.Held == 0 ? QueueRunNowPath.Steer : QueueRunNowPath.WaitsForHeld;
        return QueueRunNowPath.WaitsForTurn;
    }

    /// <summary>Whether the button does something (the rest are drawn disabled, or not at all).</summary>
    public static bool Runs(QueueRunNowPath path) => path is QueueRunNowPath.Worktree or QueueRunNowPath.AgentMenu or QueueRunNowPath.Here or QueueRunNowPath.Steer or QueueRunNowPath.Held;

    /// <summary>
    /// The phone's <c>runNow</c> and <c>runNowReason</c> on a queued row. The reason is a word: the phone words it in its own
    /// language. A phone cannot pick an agent, so <see cref="QueueRunNowPath.AgentMenu"/> is unavailable there. null: no button.
    /// </summary>
    public static (string RunNow, string? Reason)? Wire(QueueRunNowPath path) => path switch
    {
        QueueRunNowPath.Worktree => ("worktree", null),
        QueueRunNowPath.Here or QueueRunNowPath.Steer or QueueRunNowPath.Held => ("here", null),
        QueueRunNowPath.WaitsForTurn => ("unavailable", "turn"),
        QueueRunNowPath.WaitsForHeld => ("unavailable", "held"),
        QueueRunNowPath.WaitsForUpdate => ("unavailable", "update"),
        QueueRunNowPath.AgentMenu => ("unavailable", "agent"),
        _ => null,
    };

    /// <summary>The answer's name in the fixture and the web window: <c>worktree</c>, <c>agentMenu</c>, <c>waitsForTurn</c>, ….</summary>
    public static string Name(QueueRunNowPath path) => JsonNamingPolicy.CamelCase.ConvertName(path.ToString());

    /// <summary>
    /// The follow-up of an allowed Antigravity card: it must start as this pane's own next run, so 바로 실행 never sends it to a
    /// worktree child or steers it (M/QueueRunNow.swift QueuedInput.isContinuation).
    /// </summary>
    public static bool IsContinuation(QueuedComposerInput item) => item.AntigravityGrant is not null || item.AntigravityCard is not null;
}
