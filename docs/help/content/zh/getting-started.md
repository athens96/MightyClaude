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
3. Windows：运行 `MightyClaude-windows-x64-setup.exe`（ARM 电脑选择 `arm64`）进行安装，然后从开始菜单打开。详情请参阅[Windows 上的不同之处](windows.md#install)。

> [!warning]
> 此应用未经过 Apple 公证。如果 macOS 在首次打开时阻止了它，请在 Finder 中按住 Control 键点按该应用并选择**打开**，或在“系统设置 → 隐私与安全性”中允许打开。

## 更新 {#update}
{{ui:settings.appUpdate.autoUpdateToggle}}开启时（默认开启），应用会自行下载并安装新版本。

- 应用在打开时以及运行期间每 6 小时检查一次新版本。检查失败时会在下次检查时重试，失败只显示在设置中。
- 有新版本时，应用会直接下载，验证签名和文件后准备好。准备完成后，窗口底部的状态栏会显示下次启动时安装，并出现{{ui:window.status.updateRestartNow}}按钮。
- 退出应用时会安装准备好的更新。应用不会重新打开，下次打开时就是新版本。因电脑注销、重新启动或关机而关闭应用时不会安装，更新会等到下一次退出。
- 应用空闲一段时间后，窗口上方会出现一分钟的提示。倒计时结束后，应用会重新启动并安装更新，打开的工作区和窗格会原样恢复。点击{{ui:appUpdate.countdown.restartNow}}会立即重新启动；点击{{ui:appUpdate.countdown.later}}则 4 小时内不再提示。在此期间退出应用，会在退出时安装。如果电脑睡眠期间倒计时已经结束，唤醒后不会重新启动，而是收起提示。

应用空闲是指以下各项都成立。提示显示期间只要有一项不再成立，提示就会悄悄消失，等待下一次空闲。仅有键盘或鼠标输入不会让提示消失，所以可以点击提示上的按钮。
- 没有正在运行或等待开始的智能体任务，没有等待回答的卡片，队列中也没有请求。
- 输入框中没有未发送的文字或附件。
- 没有正在执行命令或占着后台进程的终端，也没有进行中的 CLI 登录、更新或安装。
- 没有手机上传、屏幕共享，也没有本机为其他电脑运行的远程任务。
- 本机没有与其他电脑共享工作区（即使没有正在运行的远程任务，只要开启了共享就不算空闲）。
- 没有打开的设置或对话框。
- 5 分钟内没有键盘或鼠标输入，3 分钟内也没有结束的运行或发送的请求。

要手动更新：

1. 打开{{ui:menu.settings}}（{{kbd:⌘,}}），进入{{ui:settings.nav.about}}标签页。
2. 在{{ui:settings.appUpdate.sectionTitle}}中点击{{ui:settings.appUpdate.checkButton}}。
3. 如有尚未下载的新版本，点击{{ui:settings.appUpdate.downloadButton}}按钮。
4. 准备完成后，点击{{ui:settings.appUpdate.restartNowButton}}。应用退出后会替换为新版本并重新打开。

关闭{{ui:settings.appUpdate.autoUpdateToggle}}后，应用不会自行检查或下载，不会显示一分钟提示，退出时也不会安装；已下载的更新只有点击{{ui:settings.appUpdate.restartNowButton}}才会安装。无论哪种方式，更新文件都只有在签名验证通过后才会安装。

Windows 应用也以同样的方式更新（[Windows 上的不同之处](windows.md#install)）。

> [!note]
> AI CLI 的更新与应用更新分开进行。请使用{{ui:settings.nav.cli}}标签页中的{{ui:settings.cliUpdate.sectionTitle}}。

## 首次启动 {#first-launch}
首次打开时还没有工作区，因此会显示欢迎界面。

![[welcome]]

### 所需工具 {#setup}
如果没有任何智能体 CLI（Claude Code、Codex 或 Antigravity）可以运行，或 Git 尚未就绪，应用启动时会先打开{{ui:setup.title}}界面。界面按组列出智能体 CLI、基础工具（Git、Homebrew、Node、Bun）、风格工具（uv、Ouroboros、oh-my-claudecode、gstack）、Tailscale 以及 gh 和 glab。

1. 勾选要安装的项目。首次打开时，缺少的 Git 会预先勾选；如果一个智能体 CLI 都没有，Claude Code 也会预先勾选；所选项目需要的工具（例如 gh 需要的 Homebrew）会一并加入。
2. 点击{{ui:setup.button.install}}后，会按顺序显示将要运行的所有命令。点击{{ui:setup.confirm.run}}之后才会运行。
3. 命令会在应用单独打开的一个终端窗口中，从主文件夹开始依次运行。这个窗口不是窗格，不会保存到工作区。需要输入密码或确认的步骤，请直接在终端中回答；应用不会使用 sudo，也不会接收你的密码。某一步失败时，仍会继续下一步。
4. 结束后，应用会重新检查。应用在前台时会重新打开此界面，否则会在状态栏显示{{ui:setup.finishedBadge}}按钮。结果以重新检查到的状态为准，而不是命令的退出状态。新装好 CLI 时会出现{{ui:setup.button.signIn}}按钮。

Git（Xcode Command Line Tools）会打开系统安装窗口，请在该窗口中完成。脚本最多等待 30 分钟；按 Control-C 只会结束等待并继续下一步。如果因为未同意 Xcode 许可而无法运行 git，Git 会显示为{{ui:setup.status.attention}}。Tailscale 会打开官方下载页面。版本过旧的 Claude Code 会显示为{{ui:setup.status.outdated}}，并出现{{ui:setup.button.update}}按钮。

可以用{{ui:setup.button.later}}关闭；勾选{{ui:setup.button.neverShow}}后再关闭，启动时就不会再打开。缺少必需工具期间，欢迎界面上有{{ui:setup.title}}按钮，因缺少 CLI 而受阻的窗格中有{{ui:setup.blocked.install}}按钮。设置 → {{ui:settings.nav.tools}}中也有同一个列表。

{{platform:windows}} Windows 上也有同样的界面。命令以主文件夹为工作文件夹，在应用单独打开的一个 PowerShell 窗口中依次运行（安装了 Windows Terminal 时为其标签页，否则为控制台窗口），最后按 Enter 之前窗口一直保持打开。Claude Code、Codex、Antigravity 和 uv 使用各自的官方 PowerShell 安装脚本安装，Git 和 glab 通过 winget 仅为当前用户安装，Node.js 和 gh 通过 winget 为所有用户安装。为所有用户安装时，Windows 可能会自己弹出管理员确认窗口；此界面本身不会请求管理员权限。安装结束后，应用会从注册表重新读取 PATH 并检查。Homebrew、Bun 和 gstack 只在 Mac 上安装。

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
