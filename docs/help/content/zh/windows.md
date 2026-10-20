---
title: Windows 上的不同之处
order: 11
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
用{{kbd:Ctrl}}代替{{kbd:⌘}}。只有收起侧边栏和停止远程屏幕的按键不同。

| Mac | Windows | 操作 |
|---|---|---|
| {{kbd:⌘O}} | {{kbd:Ctrl+O}} | {{ui:menu.openProject}} |
| {{kbd:⌘N}} | {{kbd:Ctrl+N}} | {{ui:menu.newClaudePane}} |
| {{kbd:⌘T}} | {{kbd:Ctrl+T}} | {{ui:menu.addTerminalPane}} |
| {{kbd:⇧⌘E}} | {{kbd:Ctrl+Shift+E}} | {{ui:menu.showFiles}} |
| {{kbd:⌘K}} | {{kbd:Ctrl+K}} | {{ui:menu.searchWorkspaces}} |
| {{kbd:⌃⌘S}} | {{kbd:Ctrl+B}} | {{ui:sidebar.collapse}} / {{ui:sidebar.expand}} |
| {{kbd:⌘,}} | {{kbd:Ctrl+,}} | {{ui:menu.settings}} |
| {{kbd:⌘=}} / {{kbd:⌘-}} / {{kbd:⌘0}} | {{kbd:Ctrl+=}} / {{kbd:Ctrl+-}} / {{kbd:Ctrl+0}} | {{ui:menu.textBigger}} / {{ui:menu.textSmaller}} / {{ui:menu.textActualSize}} |
| {{kbd:⌘Enter}} | {{kbd:Ctrl+Enter}} | 立即在新的 worktree 中运行，或立即发送给正在运行的 Claude |
| {{kbd:⌃⌥⌘K}} | {{kbd:Ctrl+Alt+Esc}} | 停止来自手机的所有远程屏幕会话（[远程屏幕](phone.md#screen)） |

Windows 没有菜单栏。相同的功能位于侧边栏和{{ui:workspace.addPane}}菜单中。

## 不同之处 {#differences}
| 功能 | 在 Windows 上 |
|---|---|
| 所需工具 | [{{ui:setup.title}}](getting-started.md#setup)在单独的 PowerShell 窗口中安装，使用 winget 和各 CLI 的官方 PowerShell 安装脚本。列表中没有 Homebrew、Bun 和 gstack。{{ui:setup.button.neverShow}}保存为注册表 `HKEY_CURRENT_USER\Software\MightyClaudeNative` 下的 `setup.neverShow` 值（REG_DWORD）。 |
| CLI 登录 | 不在应用内的终端中进行，而是在单独的 Windows 终端窗口中进行。登录完成后，与 Mac 一样会自动重新发送失败的请求。 |
| Antigravity | 通过 PowerShell 安装程序安装到 `%LOCALAPPDATA%\agy\bin`，记录保存在 `%USERPROFILE%\.gemini\antigravity-cli`（[Antigravity](agent-pane.md#antigravity)）。 |
| 终端窗格 | 使用 Windows PowerShell。 |
| 浏览器窗格 | 使用 Microsoft Edge WebView2 打开。与 Mac 一样，需要打开{{ui:settings.display.browserToggle}}并重新打开应用。 |
| 完成通知 | 通过{{ui:windows.notifications.toggleLabel}}开启。 |
| 远程屏幕 | 支持手机远程的屏幕查看，但画质选项比 Mac 少。按{{kbd:Ctrl+Alt+Esc}}可立即停止所有远程屏幕会话。 |
| 宠物气泡 | 批准和提问的气泡多一个图标按钮。批准时它打开显示完整请求的窗口，提问时它打开可以自己输入答案的窗口。 |
| 语言 | 更改的语言在重新打开应用后生效。 |
| 文件位置 | 提供{{ui:menu.showInExplorer}}，而不是{{ui:menu.showInFinder}}。 |
| Mighty 编排 | 默认开启，可用设置中的开关关闭，这个开关也是一个注册表值（[开启和关闭](delegation.md#enable)）。worktree 位于 `%USERPROFILE%\.mightyclaude\worktrees`，其中含有目录联接或符号链接时不会自动清理（[关闭窗格和清理](delegation.md#close)）。右键单击 {{ui:queue.worktree}} 按钮，可在另一个代理的子窗格中运行请求（[在新 worktree 中运行](delegation.md#worktree)）。 |
| 文字大小和字体 | 与 Mac 相同，在{{ui:settings.nav.general}} › {{ui:settings.text.sectionTitle}}中选择大小（85–150%）、{{ui:settings.text.uiFontLabel}}、{{ui:settings.text.transcriptFontLabel}}和{{ui:settings.text.codeFontLabel}}，所有打开的窗口会立即跟着变化。也可以用 {{kbd:Ctrl+=}} / {{kbd:Ctrl+-}} / {{kbd:Ctrl+0}}，但终端或浏览器窗格获得焦点时，这些键留给该窗格。设置保存在注册表 `HKEY_CURRENT_USER\Software\MightyClaudeNative` 中。由 Windows 自带控件决定的文字（例如确认窗口的正文）在重新打开应用后才会变化，宠物气泡暂时不跟随此设置。 |
| 通过 Tailscale 直接连接 | 手机也能[直接连接](phone.md#direct)到 Windows 电脑。在 {{ui:settings.nav.mobile}} 标签页中用 {{ui:mobileRemote.direct.windows.firewall.addButton}} 添加 Windows 防火墙规则，Windows 会请求一次管理员确认。本应用只有在更改这条防火墙规则时才请求管理员权限，可以在该标签页或{{ui:settings.remote.navTitle}}标签页中进行。 |
| 远程连接（Tailscale） | 屏幕和协议与 Mac 相同，Mac 与 Windows 之间任一方向都能连接（[远程连接](remote.md)）。连接密钥不放在钥匙串里，而是保存在只有当前 Windows 用户能解密的文件中；要共享时，用 {{ui:remote.windows.firewall.add}} 添加一条防火墙规则（与手机直连共用同一条规则，需要一次管理员确认）。远程命令在 Windows 主机上用 PowerShell 运行。共享时如果 {{ui:settings.remote.allowFullAccess}} 开关关闭，其他电脑不能在 Windows 主机上使用 Codex 和 Antigravity 窗格，因为 Windows 无法像 macOS 那样把它们限制在共享文件夹内。Mac 与 Windows 之间的连接尚未在真实设备上验证。 |

> [!warning]
> 通过 Tailscale 直接连接默认开启，因此应用第一次等待手机连接时，Windows Defender 防火墙可能会弹出它自己的提示，说已阻止此应用的部分功能。请选择**允许访问**（Windows 随后会为此应用添加它自己的、范围更广的规则）。如果选了**取消**，Windows 可能已添加阻止 `MightyClaude.exe` 的规则，而这些规则优先于应用的规则：请打开 Windows Defender 防火墙 → 高级设置 → 入站规则，删除阻止 Mighty Claude 的规则。

> [!note]
> Windows 应用的部分功能尚未在真实设备上充分验证。如发现异常，请告诉我们。
