---
title: 智能体委派
order: 6
section: delegation
---
借助智能体委派，一个 Claude 窗格（父窗格）可以把工作拆开，交给其他 Claude 窗格（子窗格）去做。每个子窗格在自己的 git worktree 中工作，完成后留下报告。父窗格收到通知，阅读报告，再决定是否合并结果。Mac 应用和 Windows 应用都提供此功能。在 Mac 上，你也可以在运行中的 Claude 窗格的输入框里亲自启动子窗格（[在新的 worktree 中运行](#worktree)）。

> [!warning]
> 这是实验性功能。在 Mac 上默认开启，可以在设置中关闭；在 Windows 上默认关闭，也没有设置界面。请先在练习用的仓库中试用。

## 开启和关闭 {#enable}
{{platform:mac}} 除非你关闭，否则它一直开启。在设置的{{ui:settings.nav.general}}标签页中切换{{ui:settings.delegation.toggle}}；无需重新打开应用，从下一次开始的运行起生效。同一个开关就是 defaults 键 `delegation.enabled`，因此在终端中运行下面的命令也能关闭它，运行 `defaults delete dev.mightyclaude.native delegation.enabled` 则重新开启：

```sh
defaults write dev.mightyclaude.native delegation.enabled -bool false
```

{{platform:mac}} 以前的版本只有设置了该键才会开启，关闭的方法是 `defaults delete …`。现在这样做会让它保持开启，所以要继续关闭，请在设置中关闭{{ui:settings.delegation.toggle}}。

{{platform:windows}} 默认关闭。先退出应用，在命令提示符或 PowerShell 中运行下面的命令，然后重新打开应用：

```
reg add "HKCU\Software\MightyClaudeNative" /v delegation.enabled /t REG_DWORD /d 1 /f
```

在 Windows 上，`HKEY_CURRENT_USER\Software\MightyClaudeNative` 下的 `delegation.enabled` 值（REG_DWORD）不为 0 时即为开启。没有这个值或值为 0 时为关闭。

- 开启后，Claude 窗格每次开始新的运行时都会带上委派工具。已经在进行中的运行不会带上。
- 父窗格可用的工具有 `delegate`（交出任务）、`list_children`（子窗格列表）、`child_status`（状态和报告）、`merge`（合并）和 `follow_up`（后续指示）。父窗格永远不能使用 `discard`（丢弃），只有你能在子窗格的卡片上丢弃。
- 要在 Windows 上关闭，请运行 `reg delete "HKCU\Software\MightyClaudeNative" /v delegation.enabled /f`，然后重新打开应用。关闭后只会去掉工具（在 Mac 上，{{kbd:⌘Enter}} 会重新立即发送给正在运行的 Claude），已有的子窗格、卡片和清理都照常工作。

## 交出工作 {#delegate}
1. 把一个 git 仓库文件夹作为工作区打开，再打开一个 Claude 窗格。这个窗格就是父窗格。
2. 在请求中写明工具名称，例如“用 delegate 工具打开两个 Mighty 子窗格，把这件事分给它们”。如果只说“分开做”，Claude 可能会改用它自己内置的子智能体。
3. 子窗格在父窗格旁边打开，并在侧边栏中缩进一格列在父窗格下方。

![[delegation-tree]]

- 每个子窗格都有一个分支 `mighty/<子窗格 id>` 和一个 worktree（Mac 上为 `~/.mightyclaude/worktrees/<子窗格 id>`，Windows 上为 `%USERPROFILE%\.mightyclaude\worktrees\<子窗格 id>`）。worktree 位于项目文件夹之外，不会动到父窗格的工作。
- 子窗格从父窗格的最后一次提交开始。父窗格文件夹中尚未提交的更改不会带给子窗格。
- 交出的任务写在 TASK.md 中，结果写在 REPORT.md 中。这两个文件位于 worktree 内被 git 忽略的文件夹里，不会被提交。
- 子窗格以父窗格选择的权限模式开始，这个模式不能比父窗格自己的模式更宽。只有你能在两次运行之间，从子窗格输入框的权限菜单或手机设置中放宽它。
- 父窗格不能回答子窗格的批准卡片。子窗格中出现卡片时，请你在那个窗格中亲自回答。
- 一个父窗格最多可以有 3 个由 `delegate` 工具创建的打开的子窗格（在 Mac 上，另外还可以有最多 3 个你亲自启动的子窗格，请参阅[在新的 worktree 中运行](#worktree)），子窗格不能再委派。已报告或已合并的子窗格只要窗格还开着也会计入，关闭已完成的子窗格就能腾出名额。磁盘剩余空间不足 10 GB 时，委派会被拒绝。

## 在新的 worktree 中运行 {#worktree}
{{platform:mac}} 你也可以亲自启动子窗格。在 Claude 窗格运行时写下请求，按 {{kbd:⌘Enter}}，或点击停止按钮旁的圆形{{ui:queue.worktree}}按钮（分支图标）。正在运行的任务不受打扰，你的请求会立即在拥有自己 worktree 的新子窗格中运行，新子窗格列在侧边栏中该窗格下方。除非你在此期间已经开始在父窗格中输入，新窗格会立即显示出来。

- 只有在能创建 worktree 的地方，{{kbd:⌘Enter}} 才会使用新的 worktree：必须是检出了有提交的分支的 git 仓库。在其他地方（不是 git 仓库、还没有提交、检出的是提交而不是分支），它会像以前一样立即发送给正在运行的 Claude。
- 子窗格的创建方式与父窗格的 `delegate` 相同：从父窗格的最新提交创建分支 `mighty/<子窗格 id>`，worktree 位于项目之外，并且需要 10 GB 可用磁盘空间。每个窗格最多可以有 3 个你亲自启动的打开的子窗格，与 `delegate` 可创建的 3 个分开计算，两者互不阻挡。
- 它以父窗格的权限模式运行，并使用父窗格的模型和{{ui:composer.effort.label}}。在每个请求都以计划模式开始的样式中，它以计划模式开始。附件也会一起带上。
- 它的第一条消息先是你的文字，随后是应用附加的说明：告诉子窗格在哪里工作，并要求它完成后提交并写入 REPORT.md，这样它的卡片才能合并。斜杠命令（以 `/` 开头的文字）会按你输入的样子单独运行，说明则作为子窗格的下一个请求随后发送，因此不会变成命令的参数。
- 对于你亲自启动的子窗格，父窗格不会收到通知，因为这不是父窗格要求的；父窗格的工具也不会列出它，也不能合并它或向它发送后续指示（`human_started`）。请在侧边栏中查看它的状态，并亲自使用卡片上的{{ui:delegation.card.merge}}和{{ui:delegation.card.discard}}按钮。
- 无法创建子窗格或创建后无法启动时（该窗格已有 3 个你亲自启动的打开的子窗格、磁盘空间不足、worktree 或第一次运行失败等），窗格的对话中会写明原因。随后，如果正在运行的 Claude 能接收（没有附件），你的文字会立即发给它；否则会像按 {{kbd:Enter}} 那样成为该窗格的下一个请求；队列已满时会放回输入框。启动失败的子窗格会一直列出，直到你在其卡片上丢弃它。
- 在子窗格中，或关闭委派时，{{kbd:⌘Enter}} 会像以前一样立即发送给正在运行的 Claude；Codex 和 Antigravity 窗格仍然放入队列。手机的发送方式不变：从手机立即发送仍会发给正在运行的 Claude。

## 子窗格的状态 {#states}
侧边栏会显示每个子窗格的状态。

| 状态 | 含义 |
|---|---|
| {{ui:delegation.child.state.creating}} | 正在创建它的 worktree 和窗格。 |
| {{ui:delegation.child.state.running}} | 正在工作。 |
| {{ui:delegation.child.state.waiting}} | 正在等你回答批准或提问。 |
| {{ui:delegation.child.state.reported}} | 已留下 REPORT.md 并结束运行。 |
| {{ui:delegation.child.state.merged}} | 已合并到父窗格的分支。 |
| {{ui:delegation.child.state.ended}} | 运行结束，但没有新的报告。 |
| {{ui:delegation.child.state.interrupted}} | 因为退出应用，运行被中断。 |
| {{ui:delegation.child.state.failed}} | 子窗格未能启动。 |
| {{ui:delegation.child.state.closed}} | 窗格已关闭。在丢弃之前会一直留在列表中。 |

子窗格开始新的运行时，{{ui:delegation.child.state.reported}}状态会被清除；它下一次报告会使用下一个报告编号。

## 通知和保留 {#notices}
子窗格报告或未能启动时，父窗格会收到一条通知。父窗格启动的运行（最初交出的任务或后续指示）没有报告就结束时，也会收到通知。通知只是一个以 `[Mighty Claude notice …]` 开头的简短指引，报告内容由父窗格用 `child_status` 自己读取。

- 父窗格正在运行时，通知会直接送进这次运行。
- 父窗格在打开应用后正常结束了上一次运行、现在处于空闲状态时，通知会开始它的下一次运行。
- 其他情况下通知会被保留：停止之后、出错之后、重新打开应用之后，以及 CLI 更新期间。一旦已有保留的项目，新通知也会排在其后保留，顺序不会乱。

保留的通知显示在父窗格输入框上方队列的最上面，是带图钉标记的行。这些行没有删除按钮，而且与普通的排队请求不同，停止或重新打开应用后也会留下。

![[delegation-held]]

- 你下一次发送内容时，所有保留的项目会按从旧到新的顺序，排在你的文字前面，作为一次运行一起送出。
- 点击{{ui:queue.runNext}}按钮，只把保留的项目作为一次运行送出。
- 每条通知只会送达一次，重新打开应用也一样。保留的行也会显示在手机的队列中，从手机发送也会一起送出。

## 后续指示 {#follow-up}
父窗格可以用 `follow_up` 工具给一个子窗格发送最多两条后续指示。后续指示按照与通知相同的规则送到子窗格，每条最多 48 KiB。

## 合并 {#merge}
把子窗格的结果放进父窗格的分支有两种方法。

1. **父窗格的 `merge` 工具**只做快进（fast-forward）。父窗格要一起传入 `child_status` 给出的子窗格 head。子窗格尚未报告、子窗格或父窗格文件夹中有未提交的更改、子窗格的 head 已移动、父窗格的分支在此期间已经前进（分叉），或者父窗格的分支没有被检出时，合并会被拒绝，并且不做任何更改。这个工具是否会弹出批准卡片，取决于父窗格的权限模式。
2. **卡片上的{{ui:delegation.card.merge}}按钮**由你来点。能快进时就快进，否则只创建一个合并提交。它会先检查冲突，拒绝时会在卡片上显示原因。

> [!tip]
> 合并一个子窗格后，父窗格的分支会向前移动，所以其余子窗格通常会被 `merge` 工具以分叉为由拒绝。这时请用卡片上的{{ui:delegation.card.merge}}按钮合并。

合并之后，在子窗格开始新的运行之前，即使关闭了窗格，卡片上也会保留{{ui:delegation.card.undo}}按钮。只有当父窗格的分支仍停在这次合并、父窗格空闲且没有未提交的更改时才能使用。撤销后，文件会回到合并之前的样子，窗格还开着的子窗格会重新变为{{ui:delegation.child.state.reported}}。

> [!note]
> 如果合并已完成，但委派记录没能保存，卡片会告诉你。合并保持不变，记录会在下次保存时重新写入。

## 丢弃 {#discard}
卡片上的{{ui:delegation.card.discard}}按钮会删除子窗格的 worktree 和分支，连同尚未合并的工作一起删除。只有你能丢弃，父窗格不能。

- 确认窗口（{{ui:delegation.discard.title}}）会列出嵌套在子窗格 worktree 中、将被一起删除的所有 worktree。
- 无法列出时，确认窗口不会出现丢弃按钮，也不会停止或删除任何东西。

> [!tip]
> 应用绝不会自行删除尚未合并的工作。只有你确认的丢弃才会让未合并的工作消失。

## 关闭窗格和清理 {#close}
- **关闭已合并的子窗格时**，如果它的工作已全部合并且没有留下未提交的更改，应用会删除它的 worktree 和分支（`git branch -d`，从不强制删除）。否则只会关闭窗格，worktree 保留。
- **在合并之前关闭子窗格时**，应用就不能再合并它了。卡片上只剩丢弃，分支和 worktree 会一直保留到你丢弃为止。
- **关闭还有打开子窗格的父窗格时**，桌面应用会先询问（{{ui:delegation.parentClose.title}}）。只关闭父窗格，子窗格继续工作。
- 父窗格已关闭的子窗格会作为卡片留在侧边栏的{{ui:delegation.tree.parentClosed}}项目下面。它们的通知保留在卡片上并显示数量，也不再接收后续指示。
- 还有打开的子窗格，或者还有尚未清理的子窗格 worktree 的工作区，无法删除。
- {{platform:windows}} 如果子窗格的 worktree 中有目录联接（junction）或符号链接（例如用链接创建的 `node_modules`），关闭已合并的子窗格时应用不会清理，而是保留 worktree，以免顺着链接把它指向的文件夹也删掉。这时请用卡片上的{{ui:delegation.card.discard}}按钮删除：丢弃会先只移除链接本身，不会动链接指向的文件夹。

![[delegation-parent-closed]]

## 限制和支持范围 {#limits}
| 项目 | 限制 |
|---|---|
| 一个父窗格的打开子窗格 | `delegate` 创建的 3 个，加上你亲自启动的 3 个（Mac） |
| 委派深度 | 一层（子窗格不能再委派） |
| 一个子窗格的后续指示 | 2 条，每条最多 48 KiB |
| 交出的任务（TASK.md） | 64 KiB |
| 所需磁盘剩余空间 | 10 GB |

- 父窗格和子窗格都必须是 Claude 窗格。Codex 和 Antigravity（Gemini）窗格不能委派。
- Mac 应用默认开启，并在设置中提供开关；Windows 应用通过隐藏开关开启（[开启和关闭](#enable)）。手机没有新的界面，但保留的行也会显示在手机的队列中。
