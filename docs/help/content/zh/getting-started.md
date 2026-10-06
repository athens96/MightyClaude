---
title: 快速入门
order: 1
section: getting-started
---
Mighty Claude 是一款桌面应用，可以在一个窗口中并排使用 Claude Code、Codex CLI 和 Gemini CLI。你可以在一个项目文件夹中打开多个窗格，每个窗格各自保留对话和设置。

## 准备工作 {#requirements}
- macOS 14 或更高版本。Windows 需要 Windows 10（版本 2004）或更高版本，或 Windows 11。
- 要使用的 AI CLI。应用会直接运行 CLI，因此请先安装并登录。不打算使用的 CLI 无需安装。

| CLI | 安装命令 |
|---|---|
| Claude Code | `npm install -g @anthropic-ai/claude-code` |
| Codex CLI | `npm install -g @openai/codex` |
| Gemini CLI | `npm install -g @google/gemini-cli` |

> [!tip]
> 打开应用后，可以在{{ui:settings.nav.tools}}标签页中查看哪些 CLI 已就绪，并复制安装命令。登录在{{ui:settings.nav.cli}}标签页中进行。请参阅[设置](settings.md#components)。

## 安装 {#install}
1. 从[下载页面](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/index.html)下载适合你操作系统的文件。
2. macOS：解压 `MightyClaude-macos.zip`，将 `MightyClaude.app` 移到“应用程序”文件夹，然后打开。
3. Windows：解压整个文件夹，运行其中的 `MightyClaude.exe`。详情请参阅[Windows 上的不同之处](windows.md#install)。

> [!warning]
> 此应用未经过 Apple 公证。如果 macOS 在首次打开时阻止了它，请在 Finder 中按住 Control 键点按该应用并选择**打开**，或在“系统设置 → 隐私与安全性”中允许打开。

## 更新 {#update}
应用每天检查一次新版本，在打开应用时进行。如有新版本，窗口底部的状态栏会显示提醒。点击该提醒即可打开设置。

1. 打开{{ui:menu.settings}}（{{kbd:⌘,}}），进入{{ui:settings.nav.about}}标签页。
2. 在{{ui:settings.appUpdate.sectionTitle}}中点击{{ui:settings.appUpdate.checkButton}}。
3. 如有新版本，点击{{ui:settings.appUpdate.downloadButton}}按钮。
4. 准备完成后，点击{{ui:settings.appUpdate.installButton}}。应用退出后会替换为新版本并重新打开。

要停止自动检查，请关闭{{ui:settings.appUpdate.autoCheckToggle}}。更新文件只有在签名验证通过后才会安装。

> [!note]
> AI CLI 的更新与应用更新分开进行。请使用{{ui:settings.nav.cli}}标签页中的{{ui:settings.cliUpdate.sectionTitle}}。

## 首次启动 {#first-launch}
首次打开时还没有工作区，因此会显示欢迎界面。

![[welcome]]

## 打开工作区 {#open-workspace}
一个工作区就是一个项目文件夹。窗格、标签页布局和对话记录按工作区分别保存。

1. 点击{{ui:layout.welcome.openProject}}按钮，或按{{kbd:⌘O}}。
2. 选择要处理的文件夹。
3. 侧边栏中会出现该工作区。接着用{{ui:menu.newClaudePane}}（{{kbd:⌘N}}）打开第一个窗格。

要再打开其他文件夹，同样使用菜单栏中的{{ui:menu.openProject}}或{{kbd:⌘O}}。在侧边栏中右键点击工作区，可以看到{{ui:menu.rename}}、{{ui:menu.showInFinder}}和{{ui:workspace.menu.remove}}。从列表中移除后，磁盘上的项目文件仍会原样保留。

## 更改语言 {#language}
1. 打开{{ui:menu.settings}} → {{ui:settings.nav.general}}。
2. 在{{ui:settings.display.languageLabel}}中选择语言。选择{{ui:settings.display.languageSystem}}时使用 Mac 的语言。
3. 菜单栏要在重新打开应用后才会更改。其他界面如果没有立即更改，重新打开应用后也会全部更改。
