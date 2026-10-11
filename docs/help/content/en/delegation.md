---
title: Agent delegation
order: 6
section: delegation
---
With agent delegation, a Claude pane (the parent) splits its work and hands parts of it to other Claude panes (its children). Each child works in its own git worktree and leaves a report when it is done. The parent gets a notice, reads the report and decides whether to merge the result. It works in the Mac app only.

> [!warning]
> This is an experimental feature. It is off by default and has no settings screen. Try it in a practice repository first.

## Turn it on or off {#enable}
Quit the app, run this command in a terminal, and open the app again.

```sh
defaults write dev.mightyclaude.native delegation.enabled -bool true
```

- Once it is on, every new run in a Claude pane gets the delegation tools. A run that is already going does not.
- The parent's tools are `delegate` (hand over a task), `list_children` (list the children), `child_status` (state and report), `merge` and `follow_up` (send a follow-up). The parent can never use `discard`; only you can discard, from a child's card.
- To turn it off, run `defaults delete dev.mightyclaude.native delegation.enabled`. Only the tools go away; existing child panes, their cards and their cleanup keep working.

## Hand over work {#delegate}
1. Open a git repository folder as a workspace and open a Claude pane. That pane is the parent.
2. Ask something like "split this between two child panes". Claude opens the child panes with its `delegate` tool.
3. The child panes open next to the parent, and the sidebar lists them one step in under it.

![[delegation-tree]]

- Each child gets a branch `mighty/<child id>` and a worktree in `~/.mightyclaude/worktrees/<child id>`. The worktree lives outside your project folder, so it never touches the parent's work.
- The task goes into TASK.md and the result into REPORT.md. Both sit in a folder git ignores inside the worktree, so they are never committed.
- A child starts in the permission mode the parent chose, which can be no wider than the parent pane's own mode. Only you can widen it, between runs, from the pane menu or the phone's settings.
- The parent cannot answer a child's approval cards. When a card appears in a child pane, answer it there yourself.
- A parent can have up to 3 open children, and a child cannot delegate further. Delegation is refused when less than 10 GB of disk is free.

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

When a child starts a new run, its {{ui:delegation.child.state.reported}} state clears; its next report gets the next report number.

## Notices and held items {#notices}
When a child reports, ends without a report, or fails to start, the parent gets one notice. A notice is only a short pointer starting with `[Mighty Claude notice …]`; the parent reads the report itself with `child_status`.

- If the parent is running, the notice goes straight into that run.
- If the parent finished its last run normally since the app opened and is now idle, the notice starts its next run.
- Otherwise the notice is held: after a stop, after an error, after the app reopens, or while a CLI update runs.

Held notices show at the top of the queued list above the parent's composer, marked {{ui:queue.held}}. These rows have no remove button.

![[delegation-held]]

- The next time you send something, all held items go first, oldest first, ahead of your text, in one run.
- The {{ui:queue.runNext}} button sends only the held items, in one run.
- Each notice is delivered exactly once, even across a relaunch. The phone's queued list shows the same rows, and a send from the phone releases them too.

## Follow-ups {#follow-up}
The parent can send a child up to two follow-ups with its `follow_up` tool. A follow-up reaches the child pane by the same rules as a notice, and each can be up to 48 KiB.

## Merging {#merge}
There are two ways to bring a child's result into the parent's branch.

1. **The parent's `merge` tool** fast-forwards only. The parent passes the child's head that `child_status` gave it. The merge is refused, changing nothing, when the child has not reported, has uncommitted changes, has a moved head or a diverged branch, or when the parent's branch is not checked out. Whether an approval card appears for this tool follows the parent pane's permission mode.
2. **The card's {{ui:delegation.card.merge}} button** is for you. It fast-forwards when it can and otherwise makes exactly one merge commit. It checks for conflicts first and shows the reason on the card when it refuses.

Right after a merge, the card offers {{ui:delegation.card.undo}}. It works only while the parent's branch is still at that merge, the parent pane is idle and there are no uncommitted changes. Undoing puts the files back as they were before the merge.

> [!note]
> If a merge goes through but the delegation record cannot be saved, the card says so. The merge stays, and the record is written again with the next save.

## Discarding {#discard}
The card's {{ui:delegation.card.discard}} button removes the child's worktree and branch, unmerged work included. Only you can discard; the parent cannot.

- The confirmation ({{ui:delegation.discard.title}}) names every worktree nested inside the child's.
- When that list cannot be made, discarding is not offered, and nothing is stopped or removed.

> [!tip]
> The app never removes unmerged work by itself. A discard you confirm is the only way unmerged work goes away.

## Closing panes and cleanup {#close}
- **When you close a merged child's pane**, the app removes its worktree and branch if all of its work is merged and nothing is left uncommitted (`git branch -d`, never forced). Otherwise only the pane closes and the worktree stays.
- **When you close a parent that has open children**, the Mac app asks first ({{ui:delegation.parentClose.title}}). Only the parent pane closes; its children keep working.
- Children of a closed parent stay as cards under {{ui:delegation.tree.parentClosed}} in the sidebar. Their notices are held on their cards, which show how many, and they take no more follow-ups.
- A workspace with open children, or with child worktrees not yet cleaned up, cannot be removed.

![[delegation-parent-closed]]

## Limits and support {#limits}
| Item | Limit |
|---|---|
| Open children per parent | 3 |
| Delegation depth | One level (children cannot delegate) |
| Follow-ups per child | 2, up to 48 KiB each |
| Task (TASK.md) | 64 KiB |
| Free disk needed | 10 GB |

- Both the parent and its children must be Claude panes. Codex and Gemini panes do not delegate.
- The Windows app does not have it yet. The phone has no new screen; held rows simply show in its queued list.
