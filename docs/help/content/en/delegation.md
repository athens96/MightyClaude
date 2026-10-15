---
title: Mighty Orchestration
order: 6
section: delegation
---
With Mighty Orchestration, while one Claude pane works, you hand other work to child Claude panes beside it and both go on at once. Each child pane works in its own git worktree and branch, so they never touch each other's files, and when it is done you merge its result into the parent's branch from its card in the sidebar. It used to be called agent delegation.

There are two ways to open a child pane.

- **Start one yourself** ({{platform:mac}}): in a running Claude pane, type your next request and press {{kbd:⌘Enter}}. The request runs right away in a child pane with a new worktree ([Run in a new worktree](#worktree)).
- **Let Claude hand work over**: ask Claude to split the work and hand it to child panes. Claude makes the children, reads their reports and merges them too ([Let Claude hand work over](#delegate)).

> [!warning]
> This is an experimental feature. On the Mac it is on by default and can be turned off in Settings. On Windows it is off by default, and only letting Claude hand work over is available, not starting a child yourself. Try it in a practice repository first.

## At a glance {#overview}
{{platform:mac}} This is how it goes when you start a child yourself.

1. Open a git repository folder as a workspace and give a Claude pane some work.
2. While Claude works, type your next request and press {{kbd:⌘Enter}}. The running task goes on undisturbed, and a child pane opens beside this pane and runs the request right away.
3. The child pane is listed one step in under the parent pane in the sidebar, with its state beside it.
4. When the child finishes, commits and leaves a report, its state becomes {{ui:delegation.child.state.reported}}.
5. Press the {{ui:delegation.card.merge}} button on the child's card to bring the result into the parent's branch.
6. Once merged, you are asked whether to discard that child pane right away. {{ui:delegation.mergedDiscard.confirm}} cleans up the child pane, its worktree and its branch. The merged work stays in the parent's branch.

![[delegation-tree]]

| To do this | Use |
|---|---|
| Run after the current task | {{kbd:Enter}} (goes into the queue) |
| Run right away, apart from the current task | {{kbd:⌘Enter}} or the {{ui:queue.worktree}} button |
| Bring the result into the parent's branch | The card's {{ui:delegation.card.merge}} button |
| Undo a merge | The card's {{ui:delegation.card.undo}} button |
| Throw the result away | The card's {{ui:delegation.card.discard}} button |

## Run in a new worktree {#worktree}
{{platform:mac}} While a Claude pane is running, type a request and press {{kbd:⌘Enter}} or the round {{ui:queue.worktree}} button (the branch icon) beside stop. The running task goes on undisturbed, and your request runs right away in a new child pane with its own worktree. The new pane is shown at once, unless you have started typing in the parent pane meanwhile.

- In a running pane, {{kbd:Enter}} puts the request in the queue, to run after the current task ends. The hint in the composer shows what each key does.
- A new worktree is used only where one can be made: a git repository with a branch checked out that has a commit. Anywhere else (not a git repository, no commit yet, a commit checked out instead of a branch) {{kbd:⌘Enter}} sends to the running Claude as before.
- The child starts from the parent's last commit. It gets a branch `mighty/<child id>`, and its worktree is made outside your project folder (`~/.mightyclaude/worktrees/<child id>`), so it never touches the parent's work. Changes you have not committed in the parent's folder do not reach the child.
- It runs in the parent pane's permission mode, with the parent pane's model and {{ui:composer.effort.label}}. In a style that starts every request in plan mode, it starts in plan mode. Your attachments go with it.
- Its first message is your text, followed by a note from the app on where the child works, asking it to commit and write REPORT.md when it is done, so that its card can merge it. A slash command (text starting with `/`) runs alone, as you typed it, and the app's note follows as the child pane's next request, so it never becomes the command's arguments.
- Use a child pane like any Claude pane: answer its approval cards, send follow-up requests and change its permission mode.
- The parent pane's Claude gets no notices about a child you started, since it never asked for it. The parent's tools neither list it nor merge it or send it follow-ups (`human_started`). Follow its state in the sidebar and use its card's buttons yourself.
- A parent pane can have up to 3 open children, with 10 GB of free disk. Closing or discarding finished child panes makes room.
- When the child cannot be made or cannot start after all (the pane already has 3 open children, the disk is low, its worktree or first run fails, and so on), the pane's transcript says why. Your text then goes to the running Claude if it can take it (no attachments), and otherwise becomes that pane's next request, as {{kbd:Enter}} would make it. With the queue full it goes back to the composer. A child that failed to start stays listed until you discard it from its card.
- In a child pane, or in a pane with Mighty Orchestration turned off, {{kbd:⌘Enter}} sends to the running Claude as before, and Codex and Antigravity panes keep queuing. The phone's send modes are unchanged: sending now from the phone still goes to the running Claude.

## Let Claude hand work over {#delegate}
A Claude pane (the parent) can also split its work and hand parts of it to other Claude panes (its children). When a child reports, the parent gets a notice, reads the report and decides whether to merge the result. It works in the Mac app and the Windows app.

1. Open a git repository folder as a workspace and open a Claude pane. That pane is the parent.
2. Name the tool in your request, for example "use the delegate tool to open two Mighty child panes and split this between them". If you only say "split this up", Claude may use its own built-in sub-agents instead.
3. The child panes open next to the parent, and the sidebar lists them one step in under it.

- The parent's tools are `delegate` (hand over a task), `list_children` (list the children), `child_status` (read state and report), `merge` (merge a child) and `follow_up` (send a follow-up). The parent cannot call `discard`; only you can discard, from a card.
- Each child gets a branch `mighty/<child id>` and a worktree (`~/.mightyclaude/worktrees/<child id>` on the Mac, `%USERPROFILE%\.mightyclaude\worktrees\<child id>` on Windows). A child starts from the parent's last commit.
- The task goes into TASK.md and the result into REPORT.md. Both sit in a folder git ignores inside the worktree, so they are never committed.
- A child's permission mode is the one the parent chose, which can be no wider than the parent pane's own mode. Only you can widen it, between runs, from the permission menu in the child pane's composer or in the phone's settings.
- The parent cannot answer a child's approval cards. When a card appears in a child pane, answer it there yourself.
- A parent can have up to 3 open children made by its `delegate` tool, counted apart from the 3 you can start yourself on the Mac; neither kind blocks the other. A child cannot delegate further. Children that reported or were merged still count while their panes are open, so close finished child panes to make room. Delegation is refused when less than 10 GB of disk is free.
- The parent learns of a child's report from a notice ([Notices and held items](#notices)), sends follow-ups with `follow_up` ([Follow-ups](#follow-up)) and merges with `merge` ([Merging](#merge)).

## A child's state {#states}
The sidebar shows each child's state.

| State | Meaning |
|---|---|
| {{ui:delegation.child.state.creating}} | Its worktree and pane are being made. |
| {{ui:delegation.child.state.running}} | It is working. |
| {{ui:delegation.child.state.waiting}} | It waits for you to answer an approval or a question. |
| {{ui:delegation.child.state.reported}} | It left a REPORT.md and finished its run. It can be merged. |
| {{ui:delegation.child.state.merged}} | It was merged into the parent's branch. |
| {{ui:delegation.child.state.ended}} | Its run ended without a new report. |
| {{ui:delegation.child.state.interrupted}} | Its run stopped because the app quit. |
| {{ui:delegation.child.state.failed}} | The child pane could not start. |
| {{ui:delegation.child.state.closed}} | Its pane is closed. It stays listed until it is discarded. |

When a child starts a new run, its {{ui:delegation.child.state.reported}} state clears; its next report gets the next report number. If a child ended without a report, ask it in its pane again to commit and write REPORT.md.

## Merging {#merge}
There are two ways to bring a child's result into the parent's branch.

1. **The card's {{ui:delegation.card.merge}} button** is for you. It is the only way to merge a child you started yourself. It fast-forwards when it can and otherwise makes exactly one merge commit. It checks for conflicts first and shows the reason on the card when it refuses.
   Once the merge goes through, you are asked whether to discard that child pane right away. {{ui:delegation.mergedDiscard.confirm}} closes the child pane and removes its worktree and branch. The merged work stays in the parent's branch, but the merge can no longer be undone. {{ui:delegation.mergedDiscard.later}} keeps the card.
2. **The parent's `merge` tool** works only for children Claude handed work to. It fast-forwards only. The parent passes the child's head that `child_status` gave it. The merge is refused, changing nothing, when the child has not reported, when the child's or the parent's folder has uncommitted changes, when the child's head moved, when the parent's branch has moved on since (diverged), or when the parent's branch is not checked out. Whether an approval card appears for this tool follows the parent pane's permission mode.

> [!tip]
> Merging one child moves the parent's branch forward, so the `merge` tool usually refuses the other children as diverged. Merge those with the card's {{ui:delegation.card.merge}} button.

Until the child starts a new run, the card keeps an {{ui:delegation.card.undo}} button after a merge, even when the pane is closed. It works only while the parent's branch is still at that merge, the parent pane is idle and there are no uncommitted changes. Undoing puts the files back as they were before the merge, and a child whose pane is open becomes {{ui:delegation.child.state.reported}} again.

> [!note]
> If a merge goes through but the delegation record cannot be saved, the card says so. The merge stays, and the record is written again with the next save.

## Discarding {#discard}
The card's {{ui:delegation.card.discard}} button removes the child's worktree and branch, unmerged work included. Only you can discard; the parent cannot. A merged child can also be discarded from the question asked right after the merge ([Merging](#merge)).

- The confirmation ({{ui:delegation.discard.title}}) shows every other worktree nested inside the child's worktree, which goes with it.
- When that list cannot be checked, the confirmation has no discard button, and nothing is stopped or removed.

> [!tip]
> The app never removes unmerged work by itself. A discard you confirm is the only way unmerged work goes away.

## Closing panes and cleanup {#close}
- **When you close a merged child's pane**, the app removes its worktree and branch if all of its work is merged and nothing is left uncommitted (`git branch -d`, never forced). Otherwise only the pane closes and the worktree stays.
- **When you close a child's pane before merging**, the app can no longer merge it. Its card only offers discard, and its branch and worktree stay until you discard it.
- **When you close a parent pane that has open children**, the desktop app asks first ({{ui:delegation.parentClose.title}}). Only the parent pane closes; its children keep working.
- Children of a closed parent stay as cards under the {{ui:delegation.tree.parentClosed}} item in the sidebar. Their notices are held on their cards, which show how many, and they take no more follow-ups.
- A workspace with open children, or with child worktrees not yet cleaned up, cannot be removed.
- {{platform:windows}} If a child's worktree holds a junction or a symbolic link (a linked `node_modules`, for example), closing the merged child's pane leaves the worktree in place instead of cleaning it up, so that nothing follows the link and empties the folder it points to. Remove it with the {{ui:delegation.card.discard}} button on its card: discard takes the link itself away first and never touches the folder it points to.

![[delegation-parent-closed]]

## Notices and held items {#notices}
When a child Claude handed work to reports or fails to start, the parent gets one notice. It also gets one when a run the parent started (the first task or a follow-up) ends without a report. A notice is only a short pointer starting with `[Mighty Claude notice …]`; the parent reads the report itself with `child_status`. A child you started yourself sends no notices.

- If the parent is running, the notice goes straight into that run.
- If the parent finished its last run normally since the app opened and is now idle, the notice starts its next run.
- Otherwise the notice is held: after a stop, after an error, after the app reopens, or while a CLI update runs. Once something is held, new notices are held behind it, so the order is kept.

Held notices show at the top of the queue above the parent's composer, as rows with a pin. These rows have no remove button and, unlike ordinary queued requests, they survive a stop or a relaunch.

![[delegation-held]]

- The next time you send something, all held items go first, oldest first, ahead of your text, in one run.
- The {{ui:queue.runNext}} button sends only the held items, in one run.
- Each notice is delivered exactly once, even across a relaunch. Held rows also show in the phone's queue, and a send from the phone delivers them too.

## Follow-ups {#follow-up}
The parent can send a child Claude handed work to up to two follow-ups with its `follow_up` tool. A follow-up reaches the child pane by the same rules as a notice, and each can be up to 48 KiB. You can type straight into the child pane's composer, with no limit on how many times.

## Turn it on or off {#enable}
{{platform:mac}} It is on unless you turn it off. Switch {{ui:settings.delegation.toggle}} on the {{ui:settings.nav.general}} tab of Settings; it applies from the next run that starts, with no relaunch. The same switch is the defaults key `delegation.enabled`, so this command in Terminal turns it off too, and `defaults delete dev.mightyclaude.native delegation.enabled` turns it back on.

```sh
defaults write dev.mightyclaude.native delegation.enabled -bool false
```

{{platform:mac}} Earlier versions were on only when that key was set, and turning it off meant `defaults delete …`. That now leaves it on, so to keep it off, switch off {{ui:settings.delegation.toggle}} in Settings.

{{platform:windows}} It is off by default. Quit the app, run this command in Command Prompt or PowerShell, and open the app again.

```
reg add "HKCU\Software\MightyClaudeNative" /v delegation.enabled /t REG_DWORD /d 1 /f
```

On Windows it is on while the `delegation.enabled` value (REG_DWORD) under `HKEY_CURRENT_USER\Software\MightyClaudeNative` is not zero. A missing value or zero means off. To turn it off, run `reg delete "HKCU\Software\MightyClaudeNative" /v delegation.enabled /f` and open the app again.

- Once it is on, every new run in a Claude pane gets the delegation tools. A run that is already going does not.
- Turned off, only the tools go away, and on the Mac {{kbd:⌘Enter}} sends to the running Claude again. Existing child panes, their cards and their cleanup keep working.

## Limits and support {#limits}
| Item | Limit |
|---|---|
| Open children per parent | 3 you start yourself (Mac), 3 made by `delegate` |
| Delegation depth | One level (children cannot make children) |
| Follow-ups per child (`follow_up`) | 2, up to 48 KiB each |
| Task (TASK.md, a request you start yourself) | 64 KiB |
| Free disk needed | 10 GB |

- Both the parent and its children must be Claude panes. Codex and Antigravity (Gemini) panes do not make children.
- The Mac app has it on by default, with a switch in Settings. The Windows app turns it on with a hidden switch ([Turn it on or off](#enable)), and there you cannot start a child yourself. The phone has no new screen, but held rows show in its queue too.
