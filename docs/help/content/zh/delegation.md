---
title: 智能体委派
order: 6
section: delegation
---
借助智能体委派，一个 Claude 窗格（父窗格）可以把工作拆开，交给其他 Claude 窗格（子窗格）去做。每个子窗格在自己的 git worktree 中工作，完成后留下报告。父窗格收到通知，阅读报告，再决定是否合并结果。此功能仅在 Mac 应用中提供。

> [!warning]
> 这是实验性功能，默认关闭，也没有设置界面。请先在练习用的仓库中试用。

## 开启和关闭 {#enable}
先退出应用，在终端中运行下面的命令，然后重新打开应用。

```sh
defaults write dev.mightyclaude.native delegation.enabled -bool true
```

- 开启后，Claude 窗格每次开始新的运行时都会带上委派工具。已经在进行中的运行不会带上。
- 父窗格可用的工具有 `delegate`（交出任务）、`list_children`（子窗格列表）、`child_status`（状态和报告）、`merge`（合并）和 `follow_up`（后续指示）。父窗格永远不能使用 `discard`（丢弃），只有你能在子窗格的卡片上丢弃。
- 要关闭，请运行 `defaults delete dev.mightyclaude.native delegation.enabled`。关闭后只会去掉工具，已有的子窗格、卡片和清理都照常工作。

## 交出工作 {#delegate}
1. 把一个 git 仓库文件夹作为工作区打开，再打开一个 Claude 窗格。这个窗格就是父窗格。
2. 提出类似"把这件事分给两个子窗格去做"的请求。Claude 会用 `delegate` 工具打开子窗格。
3. 子窗格在父窗格旁边打开，并在侧边栏中缩进一格列在父窗格下方。

![[delegation-tree]]

- 每个子窗格都有一个分支 `mighty/<子窗格 id>` 和一个 worktree（`~/.mightyclaude/worktrees/<子窗格 id>`）。worktree 位于项目文件夹之外，不会动到父窗格的工作。
- 交出的任务写在 TASK.md 中，结果写在 REPORT.md 中。这两个文件位于 worktree 内被 git 忽略的文件夹里，不会被提交。
- 子窗格以父窗格选择的权限模式开始，这个模式不能比父窗格自己的模式更宽。只有你能在两次运行之间，从窗格菜单或手机设置中放宽它。
- 父窗格不能回答子窗格的批准卡片。子窗格中出现卡片时，请你在那个窗格中亲自回答。
- 一个父窗格最多可以有 3 个打开的子窗格，子窗格不能再委派。磁盘剩余空间不足 10 GB 时，委派会被拒绝。

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

子窗格开始新的运行时，{{ui:delegation.child.state.reported}}状态会被清除；它下一次报告会使用下一个报告编号。

## 通知和暂留 {#notices}
子窗格报告、没有报告就结束或未能启动时，父窗格会收到一条通知。通知只是一个以 `[Mighty Claude notice …]` 开头的简短指引，报告内容由父窗格用 `child_status` 自己读取。

- 父窗格正在运行时，通知会直接送进这次运行。
- 父窗格在本次打开应用后正常结束了上一次运行、现在处于空闲状态时，通知会开始它的下一次运行。
- 其他情况下通知会被暂留：停止之后、出错之后、重新打开应用之后，以及 CLI 更新期间。

暂留的通知显示在父窗格输入框上方队列列表的最上面，并带有{{ui:queue.held}}标记。这些行没有删除按钮。

![[delegation-held]]

- 你下一次发送内容时，所有暂留的项目会按从旧到新的顺序，排在你的文字前面，作为一次运行一起送出。
- 点击{{ui:queue.runNext}}按钮，只把暂留的项目作为一次运行送出。
- 每条通知只会送达一次，重新打开应用也一样。手机的队列列表中也会显示同样的行，从手机发送也会一起送出。

## 后续指示 {#follow-up}
父窗格可以用 `follow_up` 工具给一个子窗格发送最多两条后续指示。后续指示按照与通知相同的规则送到子窗格，每条最多 48 KiB。

## 合并 {#merge}
把子窗格的结果放进父窗格的分支有两种方法。

1. **父窗格的 `merge` 工具**只做快进（fast-forward）。父窗格要一起传入 `child_status` 给出的子窗格 head。子窗格尚未报告、有未提交的更改、head 已移动、分支已分叉，或者父窗格的分支没有被检出时，合并会被拒绝，并且不做任何更改。这个工具是否会弹出批准卡片，取决于父窗格的权限模式。
2. **卡片上的{{ui:delegation.card.merge}}按钮**由你来点。能快进时就快进，否则只创建一个合并提交。它会先检查冲突，拒绝时会在卡片上显示原因。

刚合并之后，卡片上会出现{{ui:delegation.card.undo}}按钮。只有当父窗格的分支仍停在这次合并、父窗格空闲且没有未提交的更改时才能使用。撤销后，文件会回到合并之前的样子。

> [!note]
> 如果合并已完成，但委派记录没能保存，卡片会告诉你。合并保持不变，记录会在下次保存时重新写入。

## 丢弃 {#discard}
卡片上的{{ui:delegation.card.discard}}按钮会删除子窗格的 worktree 和分支，连同尚未合并的工作一起删除。只有你能丢弃，父窗格不能。

- 确认窗口（{{ui:delegation.discard.title}}）会列出子窗格 worktree 中嵌套的所有 worktree。
- 无法列出时不会提供丢弃，也不会停止或删除任何东西。

> [!tip]
> 应用绝不会自行删除尚未合并的工作。只有你确认的丢弃才会让未合并的工作消失。

## 关闭窗格和清理 {#close}
- **关闭已合并的子窗格时**，如果它的工作已全部合并且没有留下未提交的更改，应用会删除它的 worktree 和分支（`git branch -d`，从不强制删除）。否则只会关闭窗格，worktree 保留。
- **关闭还有打开子窗格的父窗格时**，Mac 应用会先询问（{{ui:delegation.parentClose.title}}）。只关闭父窗格，子窗格继续工作。
- 父窗格已关闭的子窗格会作为卡片留在侧边栏的{{ui:delegation.tree.parentClosed}}下面。它们的通知暂留在卡片上并显示数量，也不再接收后续指示。
- 还有打开的子窗格，或者还有尚未清理的子窗格 worktree 的工作区，无法删除。

![[delegation-parent-closed]]

## 限制和支持范围 {#limits}
| 项目 | 限制 |
|---|---|
| 一个父窗格的打开子窗格 | 3 个 |
| 委派深度 | 一层（子窗格不能再委派） |
| 一个子窗格的后续指示 | 2 条，每条最多 48 KiB |
| 交出的任务（TASK.md） | 64 KiB |
| 所需磁盘剩余空间 | 10 GB |

- 父窗格和子窗格都必须是 Claude 窗格。Codex 和 Gemini 窗格不能委派。
- Windows 应用暂时还没有这个功能。手机没有新的界面，只会在队列列表中显示暂留的行。
