---
title: Settings
order: 8
section: settings
---
Open {{ui:menu.settings}} ({{kbd:⌘,}}) or the settings button at the bottom of the sidebar. Pick a tab on the left, and close with {{ui:settings.closeButton}} or {{kbd:Esc}}. The last tab you viewed is remembered.

## General {#general}
Under {{ui:settings.display.sectionTitle}} on the {{ui:settings.nav.general}} tab you set how the app looks.
- {{ui:settings.display.themeLabel}}: {{ui:settings.display.themeDarkMac}} or {{ui:settings.display.themeLightMac}}. The button at the bottom of the sidebar switches it too.
- {{ui:settings.display.languageLabel}}: the app's language. See [Change the language](getting-started.md#language).
- {{ui:settings.display.statusLineToggle}}: runs the status line command from your Claude settings and shows it under the composer.
- {{ui:settings.display.browserToggle}}: turns browser panes on. Takes effect after you reopen the app.
- {{ui:settings.delegation.toggle}}: on by default. Claude panes can open child panes in their own worktrees, and {{kbd:⌘Enter}} in a running Claude pane runs your request in a new one. See [Mighty Orchestration](delegation.md#enable).
- {{ui:agentTerminal.urlOpen.settingTitle}}: where links opened by an agent go.

![[settings-general]]

### Text size and font {#text}
{{platform:mac}} The {{ui:settings.text.sectionTitle}} group on the same tab changes the text of the whole app.
- {{ui:settings.text.sizeLabel}}: pick from 85% to 150%. Conversations, the composer, the sidebar, Settings and terminals grow and shrink together.
- {{ui:settings.text.uiFontLabel}}: the font of the app itself, such as the sidebar, menus, buttons and Settings. Pick any font installed on this Mac; {{ui:settings.text.fontSystem}} is the macOS font.
- {{ui:settings.text.transcriptFontLabel}}: the font of agent replies, your own messages, the composer, and the body text of Mighty cards and boards. {{ui:settings.text.sameAsInterface}} follows the interface font.
- {{ui:settings.text.codeFontLabel}}: the font of code blocks, inline code, tool output and terminal panes. Only monospaced fonts are listed; {{ui:settings.text.monospaceSystem}} is the monospaced font used so far. Open terminals change at once.
- Under each font, a one-line preview is drawn in that font.
- {{ui:settings.text.reset}} puts the size and all three fonts back as they were.

The View menu's {{ui:menu.textBigger}} ({{kbd:⌘=}}), {{ui:menu.textSmaller}} ({{kbd:⌘-}}) and {{ui:menu.textActualSize}} ({{kbd:⌘0}}) change the size too. A change applies at once and stays after the app is reopened.

## Models {#models}
{{ui:settings.phaseModels.sectionTitle}} on the {{ui:settings.nav.models}} tab sets which model each phase of work uses, for this whole Mac. The phases are {{ui:settings.phaseModels.phase.planning}}, {{ui:settings.phaseModels.phase.execution}}, {{ui:settings.phaseModels.phase.review}} and {{ui:settings.phaseModels.phase.subagents}}.
- Leave a phase at {{ui:settings.phaseModels.defaultOption}} and the CLI decides.
- {{ui:settings.phaseModels.paneModel}} uses the model picked on the pane's model chip.
- Model names that aren't listed can be added under {{ui:settings.phaseModels.registeredTitle}} with {{ui:settings.phaseModels.addButton}}. If the model supports thinking effort, turn on {{ui:settings.phaseModels.supportsEffortLabel}}.

![[settings-models]]

## Styles {#styles}
The {{ui:settings.nav.styles}} tab manages {{ui:settings.styles.sectionTitle}}: register styles from files, review and allow them, or take an approval back. See [Styles](styles.md#custom) for details.

![[settings-styles]]

## Components {#components}
The {{ui:settings.nav.tools}} tab shows whether everything the app needs is ready.
- {{ui:settings.components.sectionTitle}}: the state of the Claude, Codex and Antigravity CLIs and the plugins they need, shown as {{ui:settings.components.statusInstalled}}, {{ui:settings.components.statusMissing}} or {{ui:settings.components.statusAttention}}. If something needs installing, copy its install command, run it in a terminal, then click {{ui:settings.components.recheckButton}}.
- {{ui:settings.toolkit.sectionTitle}}: tools you use often. Every command is shown before anything is installed, and you get a {{ui:settings.toolkit.confirmTitle}} step. You can {{ui:settings.toolkit.exportButton}} or {{ui:settings.toolkit.importButton}} the list.

![[settings-tools]]

## CLI {#cli}
The {{ui:settings.nav.cli}} tab manages each CLI's state, sign-in and updates.

### Sign in {#cli-accounts}
Under {{ui:settings.cliAccounts.sectionTitle}} you sign in to each CLI.
1. Click {{ui:settings.cliAccounts.buttonLogin}}. For Claude, choose {{ui:settings.cliAccounts.buttonLoginClaude}} or {{ui:settings.cliAccounts.buttonLoginConsole}}.
2. A terminal pane opens in the app and the CLI's sign-in starts. Approve it in the browser.
3. When you're signed in, the status changes. A changed account is used from the next request.

To switch accounts click {{ui:settings.cliAccounts.buttonChange}}; to sign out click {{ui:settings.cliAccounts.buttonLogout}}. At least one workspace must be open to sign in. Turn on {{ui:settings.cliAccounts.autoLoginToggle}} to sign in again automatically when the sign-in drops and resend the failed request.

### CLI updates {#cli-update}
{{ui:settings.cliUpdate.sectionTitle}} brings the installed CLIs up to date.
- {{ui:settings.cliUpdate.updateButton}}: update now.
- {{ui:settings.cliUpdate.autoUpdateToggle}}: checks when the app opens, and every 6 hours.
- {{ui:settings.cliUpdate.autoUpdatePluginsToggle}}: updates Claude and Codex plugins too.

Each CLI is updated the way it was installed (npm, Homebrew and so on).

![[settings-cli]]

## Mobile remote {#mobile}
The {{ui:settings.nav.mobile}} tab pairs your phone and allows screen viewing. See [Phone remote](phone.md).

## Remote connection {#remote}
The {{ui:settings.remote.navTitle}} tab shares this Mac's workspaces over Tailscale, or connects to workspaces another Mac shares. See [Remote connection (Tailscale)](remote.md).

## Pet & alerts {#companion}
The {{ui:settings.nav.companion}} tab sets up the desktop pet and the completion notification.
- {{ui:companion.settings.enabled}}: a raccoon pet that walks on your screen. Drag it to move it; click it to open or close its bubble.
- {{ui:companion.settings.task}}: the bubble shows the request, the current step and the time taken. You can also answer approvals there ({{ui:permission.allowOnce}} / {{ui:permission.deny}}).
- {{ui:companion.settings.pet}}: choose the pet. Add more with {{ui:companion.settings.import}}.
- {{ui:companion.settings.motion}}: less movement.
- {{ui:companion.settings.macNotifications}}: a Mac notification when a request finishes successfully. Clicking it opens that pane. The notification never contains the request or its result.

![[settings-companion]]

![[companion-pet]]

## About {#about}
The {{ui:settings.nav.about}} tab updates the app and shows its version and where its data is kept.
- {{ui:settings.appUpdate.sectionTitle}}: see [Updates](getting-started.md#update).
- {{ui:settings.appInfo.stateLocationLabel}}: the folder with the app's settings and history. Open it with {{ui:settings.appInfo.openFinderButton}}.

![[settings-about]]
