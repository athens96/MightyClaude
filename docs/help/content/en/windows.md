---
title: How Windows differs
order: 11
section: windows
---
This help is written from the Mac app. The Windows app has the same features under the same names; this page collects only what differs.

> [!note]
> The Windows app is a {{ui:badge.beta}}. Its window title carries a beta label.

## Install {#install}
1. Get the zip for your PC from the [download page](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/index.html). Most PCs need x64; ARM PCs such as Snapdragon laptops need ARM64.
2. Unzip the whole folder.
3. Run `MightyClaude.exe` inside it. The other files in the folder are needed too, so don't move the exe out on its own.

It runs on Windows 10 (version 2004) or later, or Windows 11. The .NET runtime it needs is included in the zip.

> [!warning]
> The app isn't code-signed, so Windows may block it the first time. If it says the Visual C++ runtime is missing, install the Microsoft Visual C++ Redistributable (v14). Terminal and browser panes need the Microsoft Edge WebView2 runtime.

Updates come from {{ui:menu.settings}} → {{ui:settings.nav.about}}, as on the Mac.

## Keyboard shortcuts {#shortcuts}
Use {{kbd:Ctrl}} instead of {{kbd:⌘}}. Only collapsing the sidebar and stopping remote screen sessions use different keys.

| Mac | Windows | Action |
|---|---|---|
| {{kbd:⌘O}} | {{kbd:Ctrl+O}} | {{ui:menu.openProject}} |
| {{kbd:⌘N}} | {{kbd:Ctrl+N}} | {{ui:menu.newClaudePane}} |
| {{kbd:⌘T}} | {{kbd:Ctrl+T}} | {{ui:menu.addTerminalPane}} |
| {{kbd:⇧⌘E}} | {{kbd:Ctrl+Shift+E}} | {{ui:menu.showFiles}} |
| {{kbd:⌘K}} | {{kbd:Ctrl+K}} | {{ui:menu.searchWorkspaces}} |
| {{kbd:⌃⌘S}} | {{kbd:Ctrl+B}} | {{ui:sidebar.collapse}} / {{ui:sidebar.expand}} |
| {{kbd:⌘,}} | {{kbd:Ctrl+,}} | {{ui:menu.settings}} |
| {{kbd:⌘=}} / {{kbd:⌘-}} / {{kbd:⌘0}} | {{kbd:Ctrl+=}} / {{kbd:Ctrl+-}} / {{kbd:Ctrl+0}} | {{ui:menu.textBigger}} / {{ui:menu.textSmaller}} / {{ui:menu.textActualSize}} |
| {{kbd:⌘Enter}} | {{kbd:Ctrl+Enter}} | Run it now in a new worktree, or send to the running Claude now |
| {{kbd:⌃⌥⌘K}} | {{kbd:Ctrl+Alt+Esc}} | Stop every remote screen session from phones ([Screen view](phone.md#screen)) |

Windows has no menu bar. The same commands are in the sidebar and the {{ui:workspace.addPane}} menu.

## Differences {#differences}
| Feature | On Windows |
|---|---|
| Required tools | [{{ui:setup.title}}](getting-started.md#setup) installs in a separate PowerShell window, with winget and each CLI's official PowerShell installer. Homebrew, Bun and gstack are not listed. {{ui:setup.button.neverShow}} is kept as the `setup.neverShow` value (REG_DWORD) under `HKEY_CURRENT_USER\Software\MightyClaudeNative`. |
| CLI sign-in | Runs in a separate Windows terminal window, not a terminal inside the app. Once you are signed in, the failed request is sent again on its own, as on the Mac. |
| Antigravity | Installed with its PowerShell installer into `%LOCALAPPDATA%\agy\bin`; it keeps its records in `%USERPROFILE%\.gemini\antigravity-cli` ([Antigravity](agent-pane.md#antigravity)). |
| Terminal panes | Use Windows PowerShell. |
| Browser panes | Use Microsoft Edge WebView2. As on the Mac, turn on {{ui:settings.display.browserToggle}} and reopen the app. |
| Completion notification | Turn on {{ui:windows.notifications.toggleLabel}}. |
| Screen view | Screen view for the phone remote works, with fewer quality options than on the Mac. {{kbd:Ctrl+Alt+Esc}} stops every remote screen session at once. |
| Pet bubble | Approval and question bubbles have one more symbol button. On an approval it opens a window with the whole request; on a question it opens a window where you type your own answer. |
| Language | A new language applies after you reopen the app. |
| File location | {{ui:menu.showInExplorer}} instead of {{ui:menu.showInFinder}}. |
| Mighty Orchestration | On by default, with its switch in Settings, which is also a registry value ([Turn it on or off](delegation.md#enable)). Worktrees go in `%USERPROFILE%\.mightyclaude\worktrees`, and one holding a junction or symbolic link is not cleaned up automatically ([Closing panes and cleanup](delegation.md#close)). Right-click the {{ui:queue.worktree}} button to run a request in a child of another agent ([Run in a new worktree](delegation.md#worktree)). |
| Text size and font | As on the Mac, {{ui:settings.nav.general}} › {{ui:settings.text.sectionTitle}} picks the size (85–150%), the {{ui:settings.text.uiFontLabel}}, the {{ui:settings.text.transcriptFontLabel}} and the {{ui:settings.text.codeFontLabel}}, and every open window follows at once. {{kbd:Ctrl+=}} / {{kbd:Ctrl+-}} / {{kbd:Ctrl+0}} work too, except while a terminal or browser pane has the focus: then the keys stay with that pane. The setting is kept in the registry under `HKEY_CURRENT_USER\Software\MightyClaudeNative`. Text that Windows' own controls size, such as the body of a confirmation, changes when you reopen the app, and the pet bubble does not follow the setting yet. |

> [!note]
> Some Windows features haven't been checked on enough real machines yet. If something looks wrong, please tell us.
