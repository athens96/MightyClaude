---
title: Approvals, questions and sign-in
order: 5
section: approvals
---
When an agent needs your decision, a card appears in its pane. Answer it and the work goes on. The same requests also show up in Work status, on the desktop pet and on your phone.

## Approve a permission {#permission}
When the agent wants to do something outside its permission (run a command, change a file and so on), an approval card appears.

![[permission-card]]

1. Read what it wants to do. Commands and file paths are shown in boxes; the exact input is under Original JSON.
2. Click {{ui:permission.allowOnce}} or {{ui:permission.deny}}.

An approval applies to this one request only. No rule is made that keeps allowing it.

> [!note]
> Approval cards appear in Claude panes on this Mac (Always ask and similar modes) and with Codex's {{ui:permission.label.onRequest}} mode. In other modes the CLI's own settings allow or block the action. Antigravity works differently and [asks in its own way](#antigravity-permission).

## Antigravity's permission card {#antigravity-permission}
Antigravity cannot ask the app while it works. When it meets an action it would have to ask about (running a command, writing a file and so on), it skips just that action, finishes the turn, and then asks about everything it declined in one card. The card appears in {{ui:permission.label.default}} too, and the same card appears on your phone.

The card lists every declined action. For each one you see its kind ({{ui:antigravity.permission.kind.command}}, {{ui:antigravity.permission.kind.url}}, {{ui:antigravity.permission.kind.file}} and so on), the command, address or path, and the rule that would allow it.

1. Read what was declined.
2. Pick a button.
   - {{ui:permission.allowOnce}}: adds a rule for just the declined actions to Antigravity for a moment and continues the same conversation. When that run ends (finished, failed, stopped or the app closed), the app removes only the rules it added.
   - {{ui:antigravity.permission.allowAlways}}: keeps the same rule in Antigravity's settings (`permissions.allow` in `~/.gemini/antigravity-cli/settings.json`), so it runs without asking next time. On Windows the file is `%USERPROFILE%\.gemini\antigravity-cli\settings.json`.
   - {{ui:antigravity.permission.deny}}: closes the card and continues nothing.

Allowing continues in the same conversation. If the pane is busy with something else, the continuing run waits in the queue.

Rules match from the start. A command rule allows **every command that starts with its text** (`command(git status)` also allows `git status --short`), and a file rule for a folder allows every file in it. The {{ui:permission.allowOnce}} rule also goes into Antigravity's settings for the whole computer, so while it is there it applies to **every Antigravity run on this computer** (other panes and workspaces, and `agy` in a terminal). An always-allowed command rule allows any arguments after it, so for a command whose arguments can make it do something else (`find … -exec`, `curl … -o` and the like), prefer {{ui:permission.allowOnce}}.

A command that chains (`;`, `&&`, `||`), pipes (`|`), redirects (`>`, `<`), substitutes (backticks, `$(`), runs in the background (`&`), or deletes data or raises privileges (`rm`, `sudo`, `git push --force`, `git reset --hard`, `git clean`, `kill` and so on) offers only {{ui:permission.allowOnce}}. The card's {{ui:antigravity.permission.allowAlways}} is turned off with the reason; on the phone the button is not shown.

The app never makes a rule from a command with `*` or a parenthesis, or one for the whole disk (`/`) or your home folder (`~`). Nor does it make a file rule inside Antigravity's own settings folder (`~/.gemini`) or the app's data folder, or for a folder above them (such as your home folder): a rule there would let Antigravity change its own permissions. A command that mentions `.gemini`, a shell or interpreter on its own or given code inline (`bash`, `python3 -c …`, `node -e …`, `cmd /c …`), and a wrapper that runs another command (`npx`, `xargs`, `timeout`, `env` and so on) also offer only {{ui:permission.allowOnce}}, because `command(bash)` would allow anything that starts with `bash -c`. The card says which reason applies. Such an action shows as one no rule can allow.

This differs from Claude. Claude stops before acting and waits for your answer; Antigravity finishes the turn, asks afterwards, and carries on once you allow it. So when the card appears, the rest of the work may already be done.

> [!note]
> An action that no rule can allow still shows on the card, with guidance to change the permission mode. A card with only such actions offers just {{ui:antigravity.permission.deny}}. If Antigravity's settings file cannot be read or is not JSON, the app leaves it alone and shows the reason on the card. The app only adds and removes rules under `permissions.allow` and leaves every other setting as it was. If a rule it added for a moment cannot be removed, it tries again shortly, and again when the app opens next.

An Antigravity pane on a [remote workspace](remote.md) changes the settings of the computer that runs it. Each card's allow can be used only once there, and {{ui:antigravity.permission.allowAlways}} is accepted only while {{ui:settings.remote.allowFullAccess}} is on on that computer.

## Answer questions {#questions}
When Claude asks you to choose between options, a {{ui:phone.questionnaire.title}} card appears.

![[question-card]]

1. Read the question and pick an answer. Some questions take more than one.
2. If no option fits, write your own in {{ui:phone.questionnaire.custom}}.
3. If there are several questions, go on with {{ui:phone.questionnaire.next}}.
4. When all are answered, click {{ui:phone.questionnaire.submit}}. Picking alone doesn't send anything.

## When the sign-in drops {#login-recovery}
If a request fails because the CLI sign-in expired, a sign-in recovery card appears above the composer.
1. Click {{ui:loginRecovery.loginButton}}.
2. Approve the sign-in in the browser that opens. If the browser shows a code, paste it into the card and click {{ui:loginRecovery.codeSubmit}}.
3. Once you are signed in, the failed request is sent again automatically.

If you already signed in somewhere else, click {{ui:loginRecovery.resendButton}}. If the browser sign-in doesn't work for you, use {{ui:loginRecovery.terminalButton}}.

> [!tip]
> Turn on {{ui:settings.cliAccounts.autoLoginToggle}} on the {{ui:settings.nav.cli}} tab and sign-in starts again the moment it drops.

## Background work {#background}
Claude sometimes runs long commands or sub-agents in the background. Then work is still going on after Claude's answer is done.
- A {{ui:plan.background.title}} line appears above the composer (in the default view once the turn is done, in the Mighty view from the start). It counts the tasks this request started that have ended and those still running, like "Done: 1 · Running: 2". Click the line to see each task's kind, state and time.
- When a task ends, Claude is notified and carries on with the result. An ended task shows as ended and stays on the line until you send the next request. Once they have all ended, the line stays and reads "Running: 0".
- While it runs, background work is not drawn in the request's blocks; it lives on the strip above. Click a row to open the command it ran (a sub-agent shows the first line of its prompt) and the last lines of its output (up to 6). The phone shows the same two counts on a collapsed line above the Mighty view, which opens to the rows of the tasks still running.
- When a task ends and its notice goes to Claude, nothing is added to the request: each answer of Claude's opens a new diagram. It is titled after the finished work, like "{{ui:graph.background.noticeRun}} · code review agent", and shortened to "… and N more" when several ended together. It shows the ended task's {{ui:graph.block.task}} block (command, how it ended, summary) and Claude's answer. It is not a request, so it takes no request number. In the flow it is a new card at the bottom; in {{ui:graph.view.timeline}} it is a new row, and in {{ui:graph.view.structure}} a new card (board) at the end. A notice that names nothing opens no diagram unless Claude answers it. Until every notice has been answered, the request's own result card stays an {{ui:graph.block.awaitingNotice}} block ([result card](agent-pane.md)).
- A sub-agent started in the background works the same way. When it ends, its block in the Mighty view shows {{ui:graph.state.completed}}, {{ui:graph.state.error}} or {{ui:graph.state.stopped}} (also {{ui:graph.state.stopped}} if the app restarted before it finished), and the final report it hands back becomes the block's result. A message it sends while still running is added to the block's log and does not end it; Claude's answer to that message gets its own {{ui:graph.background.messageRun}} diagram. Further messages from the same agent, with Claude's answers, go on in that same diagram. Claude's answer to the end shows in the {{ui:graph.background.noticeRun}} diagram.
- A request you send now waits in the queue and starts after the background work ends. To start it now, click stop: the queue stays and its first request starts next. In a pane with [Mighty Orchestration](delegation.md#worktree) on, in a git repository, {{kbd:⌘Enter}} can also run it right away in a new worktree.
- This line can't be hidden. It stays while there is background work, and whether you left it open is remembered.

![[background-work]]
