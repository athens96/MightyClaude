---
title: FAQ and troubleshooting
order: 10
section: faq
---
## I can't send a request {#cannot-send}
- The reason is shown under the composer. If a CLI isn't installed or needs a sign-in, check {{ui:menu.settings}} → {{ui:settings.nav.tools}} and {{ui:settings.nav.cli}}.
- Terminal panes can't take attachments. Use an AI pane.
- While a task runs, {{kbd:Enter}} queues instead of sending right away. See [Keep asking while it runs](agent-pane.md#queue).

## The model I want isn't in the list {#model-missing}
1. Open the model chip and click {{ui:composer.model.refresh}}.
2. If it's still missing, click {{ui:settings.cliAccounts.resetModelsButton}} on the {{ui:settings.nav.cli}} tab.
3. You can add model names of your own under {{ui:settings.phaseModels.registeredTitle}} on the {{ui:settings.nav.models}} tab.

## My sign-in keeps dropping {#login}
Turn on {{ui:settings.cliAccounts.autoLoginToggle}} on the {{ui:settings.nav.cli}} tab. Sign-in starts again the moment it drops, and the failed request is resent. See [When the sign-in drops](approvals.md#login-recovery).

## Korean input breaks into separate letters {#korean-input}
In the menu bar's {{ui:menu.workspace}} menu, click {{ui:menu.reconnectInputMethod}}. If it keeps happening, save a diagnostics file with {{ui:menu.saveInputDiagnostics}} and send it to us.

> [!warning]
> Don't overwrite the app's files while it runs, and don't open a second copy of the app. That can break the connection to the macOS input method, so Korean breaks into separate letters. Update with {{ui:settings.appUpdate.installButton}} inside the app.

## The browser pane is empty {#browser-empty}
Browser panes are off at first. Turn on {{ui:settings.display.browserToggle}} on {{ui:settings.nav.general}} and reopen the app. See [Browser pane](terminal-files-browser.md#browser).

## My phone won't connect {#phone}
- Check that {{ui:settings.mobileRemote.allowToggle}} is on under {{ui:settings.nav.mobile}} on the Mac.
- If the status is stuck on connecting to the relay, click {{ui:settings.mobileRemote.reconnectButton}}.
- If the phone's {{ui:phone.tabs.hosts}} tab shows {{ui:phone.hosts.reachability.unauthorized}}, the Mac's key changed. Pair again with the QR code.
- A sleeping Mac can't be reached.

## Does closing a pane lose the conversation? {#history}
No. The conversation stays as a CLI session. When you add a Claude or Codex pane on the same folder, choose {{ui:resume.choice.resume}} to continue it. See [Continue an earlier session](layout.md#resume).

## Where is my data kept? {#data}
The app's settings and history stay on this computer. See where under {{ui:settings.appInfo.stateLocationLabel}} on the {{ui:settings.nav.about}} tab. Requests to the AI are sent by each CLI to its own company's service. With the phone remote, content passes the relay encrypted.
