---
title: Agent delegation
order: 6
section: delegation
---
With agent delegation, a Claude pane (the parent) splits its work and hands parts of it to other Claude panes (its children). Each child works in its own git worktree and leaves a report when it is done. The parent gets a notice, reads the report and decides whether to merge the result. It works in the Mac app and the Windows app.

> [!warning]
> This is an experimental feature. It is off by default and has no settings screen. Try it in a practice repository first.

## Turn it on or off {#enable}
Quit the app, run this command, and open the app again.

{{platform:mac}} In Terminal:

```sh
defaults write dev.mightyclaude.native delegation.enabled -bool true
```

{{platform:windows}} In Command Prompt or PowerShell:

```
reg add "HKCU\Software\MightyClaudeNative" /v delegation.enabled /t REG_DWORD /d 1 /f
```

On Windows it is on while the `delegation.enabled` value (REG_DWORD) under `HKEY_CURRENT_USER\Software\MightyClaudeNative` is not zero. A missing value or zero means off.

- Once it is on, every new run in a Claude pane gets the delegation tools. A run that is already going does not.
- The parent's tools are `delegate` (hand over a task), `list_children` (list the children), `child_status` (state and report), `merge` and `follow_up` (send a follow-up). The parent can never use `discard`; only you can discard, from a child's card.
- To turn it off, run `defaults delete dev.mightyclaude.native delegation.enabled` on the Mac or `reg delete "HKCU\Software\MightyClaudeNative" /v delegation.enabled /f` on Windows, and open the app again. Only the tools go away; existing child panes, their cards and their cleanup keep working.

## Hand over work {#delegate}
1. Open a git repository folder as a workspace and open a Claude pane. That pane is the parent.
2. Name the tool in your request, for example "use the delegate tool to open two Mighty child panes and split this between them". If you only say "split this up", Claude may use its own built-in sub-agents instead.
3. The child panes open next to the parent, and the sidebar lists them one step in under it.

![[delegation-tree]]

- Each child gets a branch `mighty/<child id>` and a worktree in `~/.mightyclaude/worktrees/<child id>` on the Mac, `%USERPROFILE%\.mightyclaude\worktrees\<child id>` on Windows. The worktree lives outside your project folder, so it never touches the parent's work.
- A child starts from the parent's last commit. Changes you have not committed in the parent's folder do not reach it.
- The task goes into TASK.md and the result into REPORT.md. Both sit in a folder git ignores inside the worktree, so they are never committed.
- A child starts in the permission mode the parent chose, which can be no wider than the parent pane's own mode. Only you can widen it, between runs, from the permission menu in the child's composer or in the phone's settings.
- The parent cannot answer a child's approval cards. When a card appears in a child pane, answer it there yourself.
- A parent can have up to 3 open children, and a child cannot delegate further. Children that reported or were merged still count while their panes are open, so close finished child panes to make room. Delegation is refused when less than 10 GB of disk is free.

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
| Open children per parent | 3 |
| Delegation depth | One level (children cannot delegate) |
| Follow-ups per child | 2, up to 48 KiB each |
| Task (TASK.md) | 64 KiB |
| Free disk needed | 10 GB |

- Both the parent and its children must be Claude panes. Codex and Antigravity (Gemini) panes do not delegate.
- Both the Mac app and the Windows app turn it on with a hidden switch ([Turn it on or off](#enable)). The phone has no new screen, but held rows show in its queue too.
