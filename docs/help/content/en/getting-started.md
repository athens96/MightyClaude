---
title: Getting started
order: 1
section: getting-started
---
Mighty Claude is a desktop app for using Claude Code, Codex CLI and Gemini CLI side by side in one window. You open several panes on one project folder, and each pane keeps its own conversation and settings.

## What you need {#requirements}
- macOS 14 or later. On Windows: Windows 10 (version 2004) or later, or Windows 11.
- The AI CLIs you want to use. The app runs the CLIs directly, so install them and sign in first. You can skip the ones you won't use.

| CLI | Install command |
|---|---|
| Claude Code | `npm install -g @anthropic-ai/claude-code` |
| Codex CLI | `npm install -g @openai/codex` |
| Gemini CLI | `npm install -g @google/gemini-cli` |

> [!tip]
> Once the app is open, {{ui:settings.nav.tools}} shows which CLIs are ready and lets you copy the install commands. You sign in on the {{ui:settings.nav.cli}} tab. See [Settings](settings.md#components).

## Install {#install}
1. Get the file for your system from the [download page](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/index.html).
2. macOS: unzip `MightyClaude-macos.zip`, move `MightyClaude.app` to your Applications folder and open it.
3. Windows: unzip the whole folder and run `MightyClaude.exe` inside it. See [How Windows differs](windows.md#install) for details.

> [!warning]
> The app is not notarized by Apple. If macOS blocks it the first time, Control-click the app in Finder and choose **Open**, or allow it in System Settings → Privacy & Security.

## Updates {#update}
Once a day, when it opens, the app checks for a new version. If there is one, a badge appears in the status bar at the bottom of the window. Clicking it opens Settings.

1. Open {{ui:menu.settings}} ({{kbd:⌘,}}) and go to the {{ui:settings.nav.about}} tab.
2. Under {{ui:settings.appUpdate.sectionTitle}}, click {{ui:settings.appUpdate.checkButton}}.
3. If a new version is available, click {{ui:settings.appUpdate.downloadButton}}.
4. When it is ready, click {{ui:settings.appUpdate.installButton}}. The app quits, is replaced by the new version and opens again.

To stop the automatic check, turn off {{ui:settings.appUpdate.autoCheckToggle}}. An update is installed only after its signature is verified.

> [!note]
> The AI CLIs are updated separately from the app. Use {{ui:settings.cliUpdate.sectionTitle}} on the {{ui:settings.nav.cli}} tab.

## First launch {#first-launch}
The first time you open the app there is no workspace yet, so you see the welcome screen.

![[welcome]]

## Open a workspace {#open-workspace}
A workspace is one project folder. Panes, the tab layout and conversation history are saved per workspace.

1. Click {{ui:layout.welcome.openProject}} or press {{kbd:⌘O}}.
2. Choose the folder you want to work in.
3. The workspace appears in the sidebar. Open your first pane with {{ui:menu.newClaudePane}} ({{kbd:⌘N}}).

To open more folders, use {{ui:menu.openProject}} in the menu bar or {{kbd:⌘O}}. Right-click a workspace in the sidebar for {{ui:menu.rename}}, {{ui:menu.showInFinder}} and {{ui:workspace.menu.remove}}. Removing it from the list leaves the project files on disk untouched.

## Change the language {#language}
1. Open {{ui:menu.settings}} → {{ui:settings.nav.general}}.
2. Pick a language under {{ui:settings.display.languageLabel}}. {{ui:settings.display.languageSystem}} follows your Mac's language.
3. The menu bar changes the next time the app opens. If other screens don't change right away, they will after you reopen the app too.

## Opening this help {#help}
You can open this help straight from the app. It opens in the language you chose in the app.

- Mac: {{ui:menu.help}} ({{kbd:⌘?}}) in the menu bar, or the {{ui:settings.appInfo.openHelpButton}} button in {{ui:menu.settings}} → {{ui:settings.nav.about}}
- Windows: the help button at the bottom of the sidebar, or {{kbd:F1}}
- Phone: the {{ui:phone.hosts.help}} button on the {{ui:phone.tabs.hosts}} tab
