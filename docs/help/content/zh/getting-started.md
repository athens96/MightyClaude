---
title: 快速入门
order: 1
section: getting-started
---
Mighty Claude 是一款桌面应用，可以在一个窗口中并排使用 Claude Code、Codex CLI 和 Google 的 Antigravity CLI。你可以在一个项目文件夹中打开多个窗格，每个窗格各自保留对话和设置。

## 准备工作 {#requirements}
- macOS 14 或更高版本。Windows 需要 Windows 10（版本 2004）或更高版本，或 Windows 11。
- 要使用的 AI CLI。应用会直接运行 CLI，因此需要安装并登录。在 Mac 上，可以在首次打开应用时出现的{{ui:setup.title}}界面中安装（[首次启动](#first-launch)）。不打算使用的 CLI 无需安装。

- Claude Code: Mac `curl -fsSL https://claude.ai/install.sh | bash`, Windows `npm install -g @anthropic-ai/claude-code`
- Codex CLI: Mac `curl -fsSL https://chatgpt.com/codex/install.sh | sh`, Windows `npm install -g @openai/codex`
- Antigravity CLI: Mac `curl -fsSL https://antigravity.google/cli/install.sh | bash`, Windows [Antigravity](agent-pane.md#antigravity)

> [!tip]
> 打开应用后，可以在{{ui:settings.nav.tools}}标签页中查看哪些 CLI 已就绪。Mac 上可以直接在那里安装，Windows 上可以复制安装命令。登录在{{ui:settings.nav.cli}}标签页中进行。请参阅[设置](settings.md#components)。

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

### 所需工具（Mac） {#setup}
如果没有任何智能体 CLI 可以运行，或缺少必需工具（Claude Code、Git），应用启动时会先打开{{ui:setup.title}}界面。界面按组列出智能体 CLI、基础工具（Git、Homebrew、Node、Bun）、风格工具（uv、Ouroboros、Superpowers、Paperthin、oh-my-claudecode、gstack）、Tailscale 以及 gh 和 glab。

1. 勾选要安装的项目。必需项目已预先勾选，所选项目需要的工具（例如 gh 需要的 Homebrew）会一并加入。
2. 点击{{ui:setup.button.install}}后，会按顺序显示将要运行的所有命令。点击{{ui:setup.confirm.run}}之后才会运行。
3. 命令会在应用内的同一个终端窗格中依次运行。需要输入密码或确认的步骤，请直接在终端中回答；应用不会使用 sudo，也不会接收你的密码。某一步失败时，仍会继续下一步。
4. 结束后，应用会重新检查并打开此界面。结果以重新检查到的状态为准，而不是命令的退出状态。新装好 CLI 时会出现{{ui:setup.button.signIn}}按钮。

Git（Xcode Command Line Tools）会打开系统安装窗口，请在该窗口中完成。Tailscale 会打开官方下载页面。版本过旧的 Claude Code 会显示为{{ui:setup.status.outdated}}，并出现{{ui:setup.button.update}}按钮。如果还没有工作区，会把主文件夹添加为工作区，并在其中打开终端。

可以用{{ui:setup.button.later}}关闭；勾选{{ui:setup.button.neverShow}}后再关闭，启动时就不会再打开。缺少必需工具期间，欢迎界面上有{{ui:setup.title}}按钮，因缺少 CLI 而受阻的窗格中有{{ui:setup.blocked.install}}按钮。设置 → {{ui:settings.nav.tools}}中也有同一个列表。Windows 暂时还没有此界面。

## 打开工作区 {#open-workspace}
一个工作区就是一个项目文件夹。窗格、标签页布局和对话记录按工作区分别保存。

1. 点击{{ui:layout.welcome.openProject}}按钮，或按{{kbd:⌘O}}。
2. 选择要处理的文件夹。
3. 侧边栏中会出现该工作区；如果该文件夹还没有窗格，会立即打开一个 Claude 窗格。要打开更多窗格，请使用{{ui:menu.newClaudePane}}（{{kbd:⌘N}}）。

要再打开其他文件夹，可以点击侧边栏{{ui:sidebar.workspacesHeader}}标题右侧的文件夹按钮，或使用菜单栏中的{{ui:menu.openProject}}或{{kbd:⌘O}}。在侧边栏中右键点击工作区，可以看到{{ui:menu.rename}}、{{ui:menu.showInFinder}}和{{ui:workspace.menu.remove}}。从列表中移除后，磁盘上的项目文件仍会原样保留。

## 更改语言 {#language}
1. 打开{{ui:menu.settings}} → {{ui:settings.nav.general}}。
2. 在{{ui:settings.display.languageLabel}}中选择语言。选择{{ui:settings.display.languageSystem}}时使用 Mac 的语言。
3. 菜单栏要在重新打开应用后才会更改。其他界面如果没有立即更改，重新打开应用后也会全部更改。

## 打开帮助 {#help}
可以直接在应用中打开本帮助，并以应用中选择的语言显示。

- Mac：菜单栏中的{{ui:menu.help}}（{{kbd:⌘?}}），或{{ui:menu.settings}} → {{ui:settings.nav.about}}中的{{ui:settings.appInfo.openHelpButton}}按钮
- Windows：侧边栏底部的帮助按钮、{{kbd:F1}}，或{{ui:menu.settings}} → {{ui:settings.nav.about}}中的{{ui:settings.appInfo.openHelpButton}}按钮
- 手机：{{ui:phone.tabs.hosts}}标签页中的{{ui:phone.hosts.help}}按钮
