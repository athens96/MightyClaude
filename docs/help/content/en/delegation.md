---
title: Agent delegation
order: 6
section: delegation
---
With agent delegation, a Claude pane (the parent) splits its work and hands parts of it to other Claude panes (its children). Each child works in its own git worktree and leaves a report when it is done. The parent gets a notice, reads the report and decides whether to merge the result. It works in the Mac app and the Windows app. On the Mac you can also start a child yourself, from the composer of a running Claude pane ([Run in a new worktree](#worktree)).

> [!warning]
> This is an experimental feature. On the Mac it is on by default and can be turned off in Settings; on Windows it is off by default and has no settings screen. Try it in a practice repository first.

## Turn it on or off {#enable}
{{platform:mac}} It is on unless you turn it off. Switch {{ui:settings.delegation.toggle}} on the {{ui:settings.nav.general}} tab of Settings; it applies from the next run that starts, with no relaunch. The same switch is the defaults key `delegation.enabled`, so this command in Terminal turns it off too, and `defaults delete dev.mightyclaude.native delegation.enabled` turns it back on:

```sh
defaults write dev.mightyclaude.native delegation.enabled -bool false
```

{{platform:mac}} Earlier versions were off unless that key was set, and turning it off meant `defaults delete …`. That now leaves it on, so to keep it off, switch off {{ui:settings.delegation.toggle}} in Settings.

{{platform:windows}} It is off by default. Quit the app, run this command in Command Prompt or PowerShell, and open the app again:

```
reg add "HKCU\Software\MightyClaudeNative" /v delegation.enabled /t REG_DWORD /d 1 /f
```

On Windows it is on while the `delegation.enabled` value (REG_DWORD) under `HKEY_CURRENT_USER\Software\MightyClaudeNative` is not zero. A missing value or zero means off.

- Once it is on, every new run in a Claude pane gets the delegation tools. A run that is already going does not.
- The parent's tools are `delegate` (hand over a task), `list_children` (list the children), `child_status` (state and report), `merge` and `follow_up` (send a follow-up). The parent can never use `discard`; only you can discard, from a child's card.
- To turn it off on Windows, run `reg delete "HKCU\Software\MightyClaudeNative" /v delegation.enabled /f` and open the app again. Turned off, only the tools go away (and on the Mac {{kbd:⌘Enter}} sends to the running Claude again); existing child panes, their cards and their cleanup keep working.

## Hand over work {#delegate}
1. Open a git repository folder as a workspace and open a Claude pane. That pane is the parent.
2. Name the tool in your request, for example "use the delegate tool to open two Mighty child panes and split this between them". If you only say "split this up", Claude may use its own built-in sub-agents instead.
3. The child panes open next to the parent, and the sidebar lists them one step in under it.

![[delegation-tree]]

- Each child gets a branch `mighty/<child id>` and a worktree in `~/.mightyclaude/worktrees/<child id>` on the Mac, `%USERPROFILE%\.mightyclaude\worktrees\<child id>` on Windows. The worktree lives outside your project folder, so it never touches the parent's work.
- A child starts from the parent's last commit. Changes you have not committed in the parent's folder do not reach it.
- The task goes into TASK.md and the result into REPORT.md. Both sit in a folder git ignores inside the worktree, so they are never committed.
- A child starts in the permission mode the parent chose, which can be no wider than the parent pane's own mode. Only you can widen it, between runs, from the permission menu in the child's composer or in the phone's settings.
- {{platform:mac}} A child pane opens in the parent pane's view ({{ui:graph.view.default}} | {{ui:graph.view.mighty}}, and in Mighty the same one of {{ui:graph.view.diagram}}, {{ui:graph.view.timeline}}, {{ui:graph.view.structure}}). If the parent was on {{ui:graph.view.structure}} in the Mighty view, the child shows {{ui:graph.view.structure}} too.
- The parent cannot answer a child's approval cards. When a card appears in a child pane, answer it there yourself.
- A parent can have up to 3 open children made by its `delegate` tool (on the Mac, plus up to 3 you start yourself, see [Run in a new worktree](#worktree)), and a child cannot delegate further. Children that reported or were merged still count while their panes are open, so close finished child panes to make room. Delegation is refused when less than 10 GB of disk is free.

## Run in a new worktree {#worktree}
{{platform:mac}} You can start a child yourself. While a Claude pane is running, type a request and press {{kbd:⌘Enter}} or the round {{ui:queue.worktree}} button (the branch icon) beside stop. The running task goes on undisturbed, and your request runs right away in a new child pane with its own worktree, listed under the pane in the sidebar. The new pane is shown at once, unless you have started typing in the parent pane meanwhile.

- {{kbd:⌘Enter}} offers a new worktree only where one can be made: a git repository with a branch checked out that has a commit. Anywhere else (not a git repository, no commit yet, a commit checked out instead of a branch) it sends to the running Claude as before.
- The child is made the way the parent's `delegate` makes one: a branch `mighty/<child id>` from the parent's last commit, a worktree outside your project, and 10 GB of free disk. A pane can have up to 3 open children you started, apart from the 3 its `delegate` may make; neither kind blocks the other.
- It runs in the parent pane's permission mode, with its model and {{ui:composer.effort.label}}. In a style that starts every request in plan mode, it starts in plan mode. It opens in the parent's view, and when the parent was using a style in the Mighty view it starts in the same style (the style's progress is counted on its own from the child's first request). Your attachments go with it.
- Its first message is your text, followed by a note from the app on where the child works, asking it to commit and write REPORT.md when it is done, so that its card can merge it. A slash command (text starting with `/`) runs alone, as you typed it, and the note follows as the child's next request, so it never becomes the command's arguments.
- The parent gets no notices about a child you started, since it never asked for it, and its tools neither list it nor merge it or send it follow-ups (`human_started`). Follow its state in the sidebar and use its card's {{ui:delegation.card.merge}} and {{ui:delegation.card.discard}} buttons yourself.
- When the child cannot be made or cannot start after all (the pane already has 3 open children you started, the disk is low, its worktree or first run fails, and so on), the pane's transcript says why. Your text then goes to the running Claude if it can take it (no attachments), and otherwise becomes the pane's next request, as {{kbd:Enter}} would make it; with the queue full it goes back to the composer. A child that failed to start stays listed until you discard it from its card.
- In a child pane, or with delegation turned off, {{kbd:⌘Enter}} sends to the running Claude as before, and Codex and Antigravity panes keep queuing. The phone's send modes are unchanged: sending now from the phone still goes to the running Claude.

## A child's state {#states}
The sidebar shows each child's state.

| State | Meaning |
|---|---|
| {{ui:delegation.child.state.creating}} | Its worktree and pane are being made. |
| {{ui:delegation.child.state.running}} | It is working. |
| {{ui:delegation.child.state.waiting}} | It waits for you to answer an approval or a question. |
| {{ui:delegation.child.state.reported}} | It left a REPORT.md and finished its run. |
| {{ui:delegation.child.state.merged}} | It was merged into the parent's branch. |
| {{ui:delegation.child.state.ended}} | Its run ended without a new report. |
| {{ui:delegation.child.state.interrupted}} | Its run stopped because the app quit. |
| {{ui:delegation.child.state.failed}} | The child pane could not start. |
| {{ui:delegation.child.state.closed}} | Its pane is closed. It stays listed until it is discarded. |

When a child starts a new run, its {{ui:delegation.child.state.reported}} state clears; its next report gets the next report number.

## Notices and held items {#notices}
When a child reports or fails to start, the parent gets one notice. It also gets one when a run the parent started (the first task or a follow-up) ends without a report. A notice is only a short pointer starting with `[Mighty Claude notice …]`; the parent reads the report itself with `child_status`.

- If the parent is running, the notice goes straight into that run.
- If the parent finished its last run normally since the app opened and is now idle, the notice starts its next run.
- Otherwise the notice is held: after a stop, after an error, after the app reopens, or while a CLI update runs. Once something is held, new notices are held behind it, so the order is kept.

Held notices show at the top of the queue above the parent's composer, as rows with a pin. These rows have no remove button and, unlike ordinary queued requests, they survive a stop or a relaunch.

![[delegation-held]]

- The next time you send something, all held items go first, oldest first, ahead of your text, in one run.
- The {{ui:queue.runNext}} button sends only the held items, in one run.
- Each notice is delivered exactly once, even across a relaunch. Held rows also show in the phone's queue, and a send from the phone releases them too.

## Follow-ups {#follow-up}
The parent can send a child up to two follow-ups with its `follow_up` tool. A follow-up reaches the child pane by the same rules as a notice, and each can be up to 48 KiB.

## Merging {#merge}
There are two ways to bring a child's result into the parent's branch.

1. **The parent's `merge` tool** fast-forwards only. The parent passes the child's head that `child_status` gave it. The merge is refused, changing nothing, when the child has not reported, when the child's or the parent's folder has uncommitted changes, when the child's head moved, when the parent's branch has moved on since (diverged), or when the parent's branch is not checked out. Whether an approval card appears for this tool follows the parent pane's permission mode.
2. **The card's {{ui:delegation.card.merge}} button** is for you. It fast-forwards when it can and otherwise makes exactly one merge commit. It checks for conflicts first and shows the reason on the card when it refuses.
   Once the merge goes through, you are asked whether to discard that child right away. {{ui:delegation.mergedDiscard.confirm}} closes the child pane and removes its worktree and branch; the merged work stays in the parent's branch, but the merge can no longer be undone. {{ui:delegation.mergedDiscard.later}} keeps the card.

> [!tip]
> Merging one child moves the parent's branch forward, so the `merge` tool usually refuses the other children as diverged. Merge those with the card's {{ui:delegation.card.merge}} button.

Until the child starts a new run, the card keeps an {{ui:delegation.card.undo}} button after a merge, even when the child's pane is closed. It works only while the parent's branch is still at that merge, the parent pane is idle and there are no uncommitted changes. Undoing puts the files back as they were before the merge, and a child whose pane is open becomes {{ui:delegation.child.state.reported}} again.

> [!note]
> If a merge goes through but the delegation record cannot be saved, the card says so. The merge stays, and the record is written again with the next save.

## Discarding {#discard}
The card's {{ui:delegation.card.discard}} button removes the child's worktree and branch, unmerged work included. Only you can discard; the parent cannot.

- The confirmation ({{ui:delegation.discard.title}}) shows every worktree nested inside the child's, which goes with it.
- When that list cannot be made, the confirmation has no discard button, and nothing is stopped or removed.

> [!tip]
> The app never removes unmerged work by itself. A discard you confirm is the only way unmerged work goes away.

## Closing panes and cleanup {#close}
- **When you close a merged child's pane**, the app removes its worktree and branch if all of its work is merged and nothing is left uncommitted (`git branch -d`, never forced). Otherwise only the pane closes and the worktree stays.
- **When you close a child's pane before merging**, the app can no longer merge it. Its card only offers discard, and its branch and worktree stay until you discard it.
- **When you close a parent that has open children**, the desktop app asks first ({{ui:delegation.parentClose.title}}). Only the parent pane closes; its children keep working.
- Children of a closed parent stay as cards under the {{ui:delegation.tree.parentClosed}} item in the sidebar. Their notices are held on their cards, which show how many, and they take no more follow-ups.
- A workspace with open children, or with child worktrees not yet cleaned up, cannot be removed.
- {{platform:windows}} If a child's worktree holds a junction or a symbolic link (a linked `node_modules`, for example), closing the merged child's pane leaves the worktree in place instead of cleaning it up, so that nothing follows the link and empties the folder it points to. Remove it with the {{ui:delegation.card.discard}} button on its card: discard takes the link itself away first and never touches the folder it points to.

![[delegation-parent-closed]]

## Limits and support {#limits}
| Item | Limit |
|---|---|
| Open children per parent | 3 made by `delegate`, plus 3 you start yourself (Mac) |
| Delegation depth | One level (children cannot delegate) |
| Follow-ups per child | 2, up to 48 KiB each |
| Task (TASK.md) | 64 KiB |
| Free disk needed | 10 GB |

- Both the parent and its children must be Claude panes. Codex and Antigravity (Gemini) panes do not delegate.
- The Mac app has it on by default, with a switch in Settings; the Windows app turns it on with a hidden switch ([Turn it on or off](#enable)). The phone has no new screen, but held rows show in its queue too.
