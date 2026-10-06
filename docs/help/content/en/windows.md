---
title: How Windows differs
order: 9
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
Use {{kbd:Ctrl}} instead of {{kbd:⌘}}. Only collapsing the sidebar uses a different key.

| Mac | Windows | Action |
|---|---|---|
| {{kbd:⌘O}} | {{kbd:Ctrl+O}} | {{ui:menu.openProject}} |
| {{kbd:⌘N}} | {{kbd:Ctrl+N}} | {{ui:menu.newClaudePane}} |
| {{kbd:⌘T}} | {{kbd:Ctrl+T}} | {{ui:menu.addTerminalPane}} |
| {{kbd:⇧⌘E}} | {{kbd:Ctrl+Shift+E}} | {{ui:menu.showFiles}} |
| {{kbd:⌘K}} | {{kbd:Ctrl+K}} | {{ui:menu.searchWorkspaces}} |
| {{kbd:⌃⌘S}} | {{kbd:Ctrl+B}} | {{ui:sidebar.collapse}} / {{ui:sidebar.expand}} |
| {{kbd:⌘,}} | {{kbd:Ctrl+,}} | {{ui:menu.settings}} |
| {{kbd:⌘Enter}} | {{kbd:Ctrl+Enter}} | Send to the running Claude now |

Windows has no menu bar. The same commands are in the sidebar and the {{ui:workspace.addPane}} menu.

## Differences {#differences}
| Feature | On Windows |
|---|---|
| CLI sign-in | Runs in a separate Windows terminal window, not a terminal inside the app. |
| Terminal panes | Use Windows PowerShell. |
| Browser panes | Use Microsoft Edge WebView2. As on the Mac, turn on {{ui:settings.display.browserToggle}} and reopen the app. |
| Completion notification | Turn on {{ui:windows.notifications.toggleLabel}}. |
| Screen view | Screen view for the phone remote works, with fewer quality options than on the Mac. |
| Language | A new language applies after you reopen the app. |
| File location | {{ui:menu.showInExplorer}} instead of {{ui:menu.showInFinder}}. |

> [!note]
> Some Windows features haven't been checked on enough real machines yet. If something looks wrong, please tell us.
