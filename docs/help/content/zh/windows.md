---
title: Windows 上的不同之处
order: 9
section: windows
---
本帮助以 Mac 界面为准编写。Windows 应用以相同的名称提供相同的功能，本页只汇总不同的部分。

> [!note]
> Windows 应用目前为{{ui:badge.beta}}版。窗口标题上带有 Beta 标记。

## 安装 {#install}
1. 从[下载页面](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/index.html)下载适合你电脑的 zip。大多数电脑选择 x64，骁龙等 ARM 电脑选择 ARM64。
2. 解压整个文件夹。
3. 运行文件夹中的 `MightyClaude.exe`。文件夹中的其他文件也是必需的，请勿把 exe 单独移到别处。

可在 Windows 10（版本 2004）或更高版本，或 Windows 11 上运行。所需的 .NET 运行时已包含在 zip 中。

> [!warning]
> 此应用没有代码签名，Windows 可能会在首次运行时阻止它。如果提示找不到 Visual C++ 运行时，请安装 Microsoft Visual C++ 可再发行程序包（v14）。终端窗格和浏览器窗格需要 Microsoft Edge WebView2 运行时。

与 Mac 相同，更新在{{ui:menu.settings}} → {{ui:settings.nav.about}}中获取。

## 快捷键 {#shortcuts}
用{{kbd:Ctrl}}代替{{kbd:⌘}}。只有收起侧边栏的按键不同。

| Mac | Windows | 操作 |
|---|---|---|
| {{kbd:⌘O}} | {{kbd:Ctrl+O}} | {{ui:menu.openProject}} |
| {{kbd:⌘N}} | {{kbd:Ctrl+N}} | {{ui:menu.newClaudePane}} |
| {{kbd:⌘T}} | {{kbd:Ctrl+T}} | {{ui:menu.addTerminalPane}} |
| {{kbd:⇧⌘E}} | {{kbd:Ctrl+Shift+E}} | {{ui:menu.showFiles}} |
| {{kbd:⌘K}} | {{kbd:Ctrl+K}} | {{ui:menu.searchWorkspaces}} |
| {{kbd:⌃⌘S}} | {{kbd:Ctrl+B}} | {{ui:sidebar.collapse}} / {{ui:sidebar.expand}} |
| {{kbd:⌘,}} | {{kbd:Ctrl+,}} | {{ui:menu.settings}} |
| {{kbd:⌘Enter}} | {{kbd:Ctrl+Enter}} | 立即发送给正在运行的 Claude |

Windows 没有菜单栏。相同的功能位于侧边栏和{{ui:workspace.addPane}}菜单中。

## 不同之处 {#differences}
| 功能 | 在 Windows 上 |
|---|---|
| CLI 登录 | 不在应用内的终端中进行，而是在单独的 Windows 终端窗口中进行。 |
| 终端窗格 | 使用 Windows PowerShell。 |
| 浏览器窗格 | 使用 Microsoft Edge WebView2 打开。与 Mac 一样，需要打开{{ui:settings.display.browserToggle}}并重新打开应用。 |
| 完成通知 | 通过{{ui:windows.notifications.toggleLabel}}开启。 |
| 远程屏幕 | 支持手机远程的屏幕查看，但画质选项比 Mac 少。 |
| 语言 | 更改的语言在重新打开应用后生效。 |
| 文件位置 | 提供{{ui:menu.showInExplorer}}，而不是{{ui:menu.showInFinder}}。 |

> [!note]
> Windows 应用的部分功能尚未在真实设备上充分验证。如发现异常，请告诉我们。
