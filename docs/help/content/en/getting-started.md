---
title: Getting started
order: 1
section: getting-started
---
Mighty Claude is a desktop app for using Claude Code, Codex CLI and Google's Antigravity CLI side by side in one window. You open several panes on one project folder, and each pane keeps its own conversation and settings.

## What you need {#requirements}
- macOS 14 or later. On Windows: Windows 10 (version 2004) or later, or Windows 11.
- The AI CLIs you want to use. The app runs the CLIs directly, so they need to be installed and signed in. On a Mac you can install them from the {{ui:setup.title}} screen the app shows the first time it opens ([First launch](#first-launch)). You can skip the ones you won't use.

- Claude Code: Mac `curl -fsSL https://claude.ai/install.sh | bash`, Windows `npm install -g @anthropic-ai/claude-code`
- Codex CLI: Mac `curl -fsSL https://chatgpt.com/codex/install.sh | sh`, Windows `npm install -g @openai/codex`
- Antigravity CLI: Mac `curl -fsSL https://antigravity.google/cli/install.sh | bash`, Windows [Antigravity](agent-pane.md#antigravity)

> [!tip]
> Once the app is open, {{ui:settings.nav.tools}} shows which CLIs are ready. On a Mac you install them right there; on Windows you copy the install commands. You sign in on the {{ui:settings.nav.cli}} tab. See [Settings](settings.md#components).

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

### Required tools {#setup}
If no agent CLI (Claude Code, Codex or Antigravity) can run, or Git is not ready, the {{ui:setup.title}} screen opens when the app starts. It groups the agent CLIs, the prerequisites (Git, Homebrew, Node, Bun), the style tools (uv, Ouroboros, Superpowers, Paperthin, oh-my-claudecode, gstack), Tailscale, and gh and glab.

1. Check what you want to install. The first time, a missing Git starts checked, and so does Claude Code when there is no agent CLI at all; and anything a picked item needs (such as Homebrew for gh) comes along.
2. Click {{ui:setup.button.install}}. Every command that will run is shown, in order. Nothing runs until you click {{ui:setup.confirm.run}}.
3. The commands run one after another, from your home folder, in a terminal window the app opens for them. It is not a pane and is not saved with your workspaces. Answer any password or confirmation prompt in the terminal yourself; the app never uses sudo and never sees your password. If a step fails, the next one still runs.
4. When it ends, the app checks again. If the app is in front, this screen opens again; otherwise the status bar shows a {{ui:setup.finishedBadge}} button. What counts is what the check finds, not how a command exited. When a CLI has just appeared, a {{ui:setup.button.signIn}} button shows.

Git (Xcode Command Line Tools) opens a system install window, where you finish it. The script waits up to 30 minutes; Control-C ends only the wait and moves on. If git does not run because the Xcode license has not been accepted, Git shows {{ui:setup.status.attention}}. Tailscale opens its official download page. A Claude Code that is too old shows {{ui:setup.status.outdated}} with an {{ui:setup.button.update}} button.

{{ui:setup.button.later}} closes the screen; check {{ui:setup.button.neverShow}} before closing and it no longer opens at launch. While a required tool is missing, the welcome screen has a {{ui:setup.title}} button, and a pane blocked by a missing CLI has an {{ui:setup.blocked.install}} button. The same list is in Settings → {{ui:settings.nav.tools}}.

{{platform:windows}} Windows has the same screen. The commands run in order, from your home folder, in one PowerShell window the app opens (a Windows Terminal tab when it is installed, otherwise a console window), which stays open until you press Enter at the end. Claude Code, Codex, Antigravity and uv install with their own official PowerShell installers, Git and glab with winget for your user only, and Node.js and gh with winget for all users. An install for all users may show Windows' own administrator prompt; the app never asks for administrator rights. When the install ends, the app reads PATH again from the registry and checks. Homebrew, Bun and gstack install on a Mac only.

## Open a workspace {#open-workspace}
A workspace is one project folder. Panes, the tab layout and conversation history are saved per workspace.

1. Click {{ui:layout.welcome.openProject}} or press {{kbd:⌘O}}.
2. Choose the folder you want to work in.
3. The workspace appears in the sidebar, and a folder with no panes gets a Claude pane right away. Open more panes with {{ui:menu.newClaudePane}} ({{kbd:⌘N}}).

To open more folders, click the folder button at the right of the {{ui:sidebar.workspacesHeader}} heading in the sidebar, or use {{ui:menu.openProject}} in the menu bar or {{kbd:⌘O}}. Right-click a workspace in the sidebar for {{ui:menu.rename}}, {{ui:menu.showInFinder}} and {{ui:workspace.menu.remove}}. Removing it from the list leaves the project files on disk untouched.

## Change the language {#language}
1. Open {{ui:menu.settings}} → {{ui:settings.nav.general}}.
2. Pick a language under {{ui:settings.display.languageLabel}}. {{ui:settings.display.languageSystem}} follows your Mac's language.
3. The menu bar changes the next time the app opens. If other screens don't change right away, they will after you reopen the app too.

## Opening this help {#help}
You can open this help straight from the app. It opens in the language you chose in the app.

- Mac: {{ui:menu.help}} ({{kbd:⌘?}}) in the menu bar, or the {{ui:settings.appInfo.openHelpButton}} button in {{ui:menu.settings}} → {{ui:settings.nav.about}}
- Windows: the help button at the bottom of the sidebar, {{kbd:F1}}, or the {{ui:settings.appInfo.openHelpButton}} button in {{ui:menu.settings}} → {{ui:settings.nav.about}}
- Phone: the {{ui:phone.hosts.help}} button on the {{ui:phone.tabs.hosts}} tab
