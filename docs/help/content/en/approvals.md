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
> Approval cards appear in Claude panes on this Mac (Always ask and similar modes) and with Codex's {{ui:permission.label.onRequest}} mode. In other modes the CLI's own settings allow or block the action.

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
- A strip above the composer says the turn is done but background work is still running. Click {{ui:plan.background.show}} to see each task's kind, state and time.
- The Mighty view shows them as {{ui:graph.block.task}} blocks.
- A request you send now starts after the background work ends. To send it in right away, press {{kbd:⌘Enter}}.

![[background-work]]
