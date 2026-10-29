---
title: Mighty 编排
order: 6
section: delegation
---
借助 Mighty 编排，一个代理窗格在工作的同时，可以把别的工作交给旁边的子窗格一起推进。子窗格运行与父窗格相同的代理（Claude、Codex 或 Antigravity）；也可以运行三者中的另一个（[Codex 和 Antigravity](#agents)）。每个子窗格都在自己的 git worktree 和分支中工作，因此不会动到彼此的文件；完成后，在侧边栏的卡片上把结果合并到父窗格的分支。此功能以前叫作智能体委派。

打开子窗格有两种方法。

- **亲自启动**：在运行中的 Claude 窗格，或处于子窗格能更改文件的模式的 Codex、Antigravity 窗格（[Codex 和 Antigravity](#agents)）里写下下一个请求，按 {{kbd:⌘Enter}}。请求会立即在新 worktree 的子窗格中运行（[在新的 worktree 中运行](#worktree)）。
- **让 Claude 分工**：请 Claude（或 Codex）把工作拆开交给子窗格。代理会创建子窗格、阅读报告，并一直做到合并（[让 Claude 分工](#delegate)）。

> [!warning]
> 这是实验性功能。默认开启，可以在设置中关闭。请先在练习用的仓库中试用。

## 概览 {#overview}
亲自启动时的流程如下。

1. 把一个 git 仓库文件夹作为工作区打开，在 Claude 窗格中交给它一项工作。
2. Claude 工作期间，写下下一个请求并按 {{kbd:⌘Enter}}。正在运行的任务继续进行，这个窗格旁边会打开一个子窗格，立即运行你的请求。
3. 子窗格在侧边栏中缩进一格列在父窗格下方，旁边显示它的状态。
4. 子窗格完成工作、提交并留下报告后，状态变为{{ui:delegation.child.state.reported}}。
5. 点击子窗格卡片上的{{ui:delegation.card.merge}}按钮，把结果放进父窗格的分支。
6. 合并后，会询问是否立即丢弃该子窗格。选择{{ui:delegation.mergedDiscard.confirm}}会清理子窗格及其 worktree 和分支。已合并的内容保留在父窗格的分支中。

![[delegation-tree]]

| 想做的事 | 方法 |
|---|---|
| 在当前任务之后接着运行 | {{kbd:Enter}}（放入队列） |
| 与当前任务分开，立即运行 | {{kbd:⌘Enter}} 或{{ui:queue.worktree}}按钮 |
| 把结果放进父窗格的分支 | 卡片上的{{ui:delegation.card.merge}}按钮 |
| 撤销合并 | 卡片上的{{ui:delegation.card.undo}}按钮 |
| 丢弃结果 | 卡片上的{{ui:delegation.card.discard}}按钮 |

## 在新的 worktree 中运行 {#worktree}
在 Claude、Codex 或 Antigravity 窗格运行时写下请求，按 {{kbd:⌘Enter}}，或点击停止按钮旁的圆形{{ui:queue.worktree}}按钮（分支图标）。正在运行的任务不受打扰，你的请求会立即在拥有自己 worktree 的新子窗格中运行。除非你在此期间已经开始在父窗格中输入，新窗格会立即显示出来。

- 在运行中的窗格里，{{kbd:Enter}} 会把请求放入队列，等当前任务结束后再运行。输入框的提示文字会说明这两个键的作用。
- 只有在能创建 worktree 的地方才会使用新的 worktree：必须是检出了有提交的分支的 git 仓库。在其他地方（不是 git 仓库、还没有提交、检出的是提交而不是分支），{{kbd:⌘Enter}} 会像 {{kbd:Enter}} 一样把请求放入队列。
- 子窗格从父窗格的最后一次提交开始。会创建分支 `mighty/<子窗格 id>`，worktree 位于项目文件夹之外（Mac 上为 `~/.mightyclaude/worktrees/<子窗格 id>`，Windows 上为 `%USERPROFILE%\.mightyclaude\worktrees\<子窗格 id>`），不会动到父窗格的工作。父窗格文件夹中尚未提交的更改不会带给子窗格。
- 与窗格相同代理的子窗格以父窗格的权限模式运行，并使用父窗格的模型和{{ui:composer.effort.label}}。在每个请求都以计划模式开始的样式中，它以计划模式开始。它以与父窗格相同的视图打开；如果父窗格在 Mighty 视图中使用某个样式，它也以同一样式开始（样式的进度从子窗格的第一个请求起单独计算）。附件也会一起带上。
- 第一条消息先是你输入的文字，随后应用会告诉子窗格在哪里工作，并要求它完成后提交并写入 REPORT.md，这样才能在卡片上合并。斜杠命令（以 `/` 开头的文字）会按你输入的样子单独运行，应用的说明则作为子窗格的下一个请求随后发送，因此不会变成命令的参数。
- {{platform:mac}} 若要用另一个代理运行请求，请右键点按 {{ui:queue.worktree}} 按钮（或按住 Control 点按），然后选择 Claude、Codex 或 Antigravity。{{kbd:⌘Enter}} 和普通点按仍使用窗格自己的代理。灰色的项目会说明无法运行的原因：其 CLI 未安装或未登录（请查看“设置 › CLI”）、在窗格模式允许的范围内没有能更改文件的模式，或你启动的 3 个子窗格已经打开。另一个代理的子窗格以它自己的模式中能更改文件、且不做窗格模式不允许之事的最宽模式开始（[Codex 和 Antigravity](#agents)），使用该代理的新窗格会得到的模型和{{ui:composer.effort.label}}，并以窗格的视图打开，但不带样式。如果无法创建或启动该子窗格，你的文字和附件会放回输入框，对话中会说明原因；它们绝不会发给窗格本身。如果窗格自己的代理在当前模式下不能有子窗格，而另一个代理可以，按钮仍会显示，点按会打开菜单，{{kbd:⌘Enter}} 会把请求放入队列。
- {{platform:windows}} 若要用另一个代理运行请求，请右键单击 {{ui:queue.worktree}} 按钮（或在按钮上按 Shift+F10），然后选择 Claude、Codex 或 Antigravity。{{kbd:Ctrl+Enter}} 和普通单击仍使用窗格自己的代理。其余与上面的 Mac 相同：灰色的项目会说明无法运行的原因；另一个代理的子窗格以它自己的模式中不做窗格模式不允许之事的最宽模式启动，并使用该代理的新窗格会得到的模型和{{ui:composer.effort.label}}；无法启动子窗格的文本会回到输入框。只有另一个代理的子窗格可用时，单击会打开菜单，{{kbd:Ctrl+Enter}} 则像以前一样把请求放入队列。
- 子窗格的用法和同一代理的普通窗格一样：可以回答批准卡片、发送后续请求、更改权限模式。
- 对于你亲自启动的子窗格，父窗格中的 Claude 不会收到通知，因为这不是父窗格交出的工作。父窗格的工具也不会列出它，也不能合并它或向它发送后续指示（`human_started`）。请在侧边栏中查看它的状态，并亲自使用卡片上的按钮。
- 一个父窗格最多可以有 3 个打开的子窗格，并且需要 10 GB 可用磁盘空间。关闭或丢弃已完成的子窗格就能腾出名额。
- 无法创建子窗格或创建后无法启动时（已有 3 个打开的子窗格、磁盘空间不足、worktree 或第一次运行失败等），窗格的对话中会写明原因。随后，你的文字会像按 {{kbd:Enter}} 那样成为该窗格的下一个请求。队列已满时会放回输入框。启动失败的子窗格会一直列出，直到你在卡片上丢弃它。
- 在子窗格中，或关闭 Mighty 编排的窗格中，{{kbd:⌘Enter}} 也会像 {{kbd:Enter}} 一样放入队列。从手机发给运行中窗格的内容同样总是进入队列。人发送的内容不会插入正在运行的任务。

## 让 Claude 分工 {#delegate}
Claude 或 Codex 窗格（父窗格）也可以把工作拆开，交给其他窗格（子窗格）；除非它指定别的代理，子窗格运行它自己的代理。子窗格报告后，父窗格收到通知，阅读报告，再决定是否合并结果。Claude 和 Codex 父窗格在 Mac 应用和 Windows 应用中都可使用。

1. 把一个 git 仓库文件夹作为工作区打开，再打开一个 Claude 窗格。这个窗格就是父窗格。
2. 在请求中写明工具名称，例如“用 delegate 工具打开两个 Mighty 子窗格，把这件事分给它们”。如果只说“分开做”，Claude 可能会改用它自己内置的子智能体。
3. 子窗格在父窗格旁边打开，并在侧边栏中缩进一格列在父窗格下方。

- 父窗格使用的工具有 `delegate`（交出任务；可以用 `agent` 指定子窗格的代理：`claude`、`codex` 或 `antigravity`）、`list_children`（子窗格列表）、`child_status`（读取状态和报告）、`merge`（合并）和 `follow_up`（后续指示）。父窗格不能调用 `discard`（丢弃），只有你能在卡片上丢弃。
- 每个子窗格都有一个分支 `mighty/<子窗格 id>` 和一个 worktree（Mac 上为 `~/.mightyclaude/worktrees/<子窗格 id>`，Windows 上为 `%USERPROFILE%\.mightyclaude\worktrees\<子窗格 id>`）。子窗格从父窗格的最后一次提交开始。
- 交出的任务写在 TASK.md 中，结果写在 REPORT.md 中。这两个文件位于 worktree 内被 git 忽略的文件夹里，不会被提交。
- 子窗格的权限模式是父窗格选择的模式，不能比父窗格自己的模式更宽。另一个代理的子窗格不能做父窗格模式不做的事：只有父窗格的模式不经询问就更改文件时（接受编辑或 auto 模式的 Claude、{{ui:permission.label.onRequest}} 或 {{ui:permission.label.acceptEditsCodex}} 模式的 Codex、{{ui:permission.label.acceptEdits}} 模式的 Antigravity、{{ui:permission.label.fullAccess}} 模式的任何代理）才不经询问更改文件，只有父窗格的模式能更改文件时才在询问后更改文件，只有父窗格的模式不经询问就运行 shell 命令时（auto 模式的 Claude、{{ui:permission.label.defaultCodex}} 或 {{ui:permission.label.acceptEditsCodex}} 模式的 Codex、{{ui:permission.label.fullAccess}} 模式的任何代理）才不经询问运行，只有在 {{ui:permission.label.fullAccess}} 下才有完全访问权限。因此在运行 shell 命令前会询问的 Claude 接受编辑模式下，Codex 子窗格得到的是 {{ui:permission.label.onRequest}}，而不是 {{ui:permission.label.acceptEditsCodex}}；计划模式或手动模式的 Claude 窗格完全不能有 Codex 或 Antigravity 子窗格。只有你能在两次运行之间，从子窗格输入框的权限菜单或手机设置中放宽它。
- 子窗格以与父窗格相同的视图打开（{{ui:graph.view.default}} | {{ui:graph.view.mighty}}，在 Mighty 视图中则是 {{ui:graph.view.diagram}}, {{ui:graph.view.timeline}}, {{ui:graph.view.structure}} 中相同的那一个）。如果父窗格在 Mighty 视图中使用{{ui:graph.view.structure}}，子窗格也显示为{{ui:graph.view.structure}}。
- 父窗格不能回答子窗格的批准卡片。子窗格中出现卡片时，请你在那个窗格中亲自回答。
- 一个父窗格最多可以有 3 个由 `delegate` 工具创建的打开的子窗格，与亲自启动的 3 个分开计算，两者互不阻挡。子窗格不能再委派。已报告或已合并的子窗格只要窗格还开着也会计入这 3 个，关闭已完成的子窗格就能腾出名额。磁盘剩余空间不足 10 GB 时，交出任务会被拒绝。
- 父窗格通过通知得知子窗格的报告（[通知和保留](#notices)），用 `follow_up` 发送后续指示（[后续指示](#follow-up)），用 `merge` 合并（[合并](#merge)）。

## Codex 和 Antigravity {#agents}
Mighty 编排也适用于 Codex 和 Antigravity 窗格。默认情况下，子窗格运行父窗格的代理：Codex 窗格的子窗格是 Codex 窗格，Antigravity 窗格的子窗格是 Antigravity 窗格。

任何代理都可以有三者中任意一个代理的子窗格。可以从 {{ui:queue.worktree}} 按钮的菜单中选择（[在新 worktree 中运行](#worktree)），也可以让 Claude 或 Codex 父窗格用 `delegate` 的 `agent`（`claude`、`codex` 或 `antigravity`）指定。子窗格按下面所写的它自己代理的规则工作：它的模式，以及由谁提交它的工作（Claude 子窗格自己提交；Codex 或 Antigravity 子窗格的工作由应用提交）。通知和合并跟随父窗格的代理。`delegate` 会拒绝它不认识的代理（`agent_unknown`）、未安装或未登录的代理（`agent_unavailable`），以及不是子代理的模式、在其中无法更改文件的模式，或能比父窗格的模式做更多事的模式（模式的比较方法见[让 Claude 分工](#delegate)）。

- **子窗格能工作的模式**：子窗格以父窗格的权限模式启动，因此 Codex 和 Antigravity 窗格只有在子窗格能更改文件的模式下才会用 {{kbd:⌘Enter}} 启动子窗格：Codex 为{{ui:permission.label.acceptEditsCodex}}、{{ui:permission.label.onRequest}}或{{ui:permission.label.fullAccess}}，Antigravity 为{{ui:permission.label.acceptEdits}}或{{ui:permission.label.fullAccess}}。在其他模式下，{{kbd:⌘Enter}} 会像以前一样把请求放入队列。
- **Codex** 窗格只有在{{ui:permission.label.acceptEditsCodex}}或{{ui:permission.label.fullAccess}}模式下才会获得委派工具：在{{ui:permission.label.defaultCodex}}模式下 Codex 会拒绝这些工具，在{{ui:permission.label.onRequest}}模式下它会以应用无法显示的方式请求批准。`delegate` 会拒绝无法更改文件的子窗格模式。只有当运行以{{ui:permission.label.fullAccess}}模式开始时，Codex 才能调用 `merge`，否则会被拒绝（`merge_human_only`），请在卡片上合并。运行中的 Codex 回合无法接收文字，因此发给运行中 Codex 父窗格的通知会在这次运行正常结束后作为下一次运行送达；如果因停止或错误而结束，则会被保留。
- **Antigravity** 窗格没有委派工具（Antigravity 只从其全局设置获取工具，而应用从不更改该设置），只能用 {{kbd:⌘Enter}} 或 {{ui:queue.worktree}} 按钮启动子窗格。
- 除非处于{{ui:permission.label.fullAccess}}模式，Codex 和 Antigravity 子窗格无法在自己的 worktree 中提交，因此会被告知保留更改。子窗格的运行在 REPORT.md 有变化时正常结束后，应用会把更改提交到子窗格的分支（`mighty: <子窗格 id> report r<n>`），并以该提交记录报告；因停止或错误结束时，会等到下一次正常结束。被 .gitignore 排除的新文件、看起来像机密信息的新文件（`.env*`、`*.pem`、`*.key`、`*.pfx`、`*.p12`、`*.ppk`、`id_rsa*`、`id_ed25519*`、`id_ecdsa*`、`id_dsa*`、`credentials*`、`secrets.*`、`.npmrc`、`.pypirc`）以及超过 5 MB 的新文件不会提交，后两类会在子窗格中列出名称。提交使用你在 git 中设置的姓名和邮箱（没有时提交失败，并在窗格中说明），并且不签名。worktree 中有未解决的合并冲突时，应用不会提交任何内容，并在窗格中说明。
- 卡片的合并因冲突被拒绝时，只会请能够提交的子窗格（{{ui:permission.label.fullAccess}}）解决冲突。否则请把子窗格切换到{{ui:permission.label.fullAccess}}后在子窗格中请它解决，或在该 worktree 的终端中亲自解决冲突并提交。
- 把仓库的子文件夹作为工作区打开时，Codex 子窗格也可以写入 REPORT.md 所在的 worktree 顶层文件夹，Antigravity 子窗格会添加该文件夹。

## 子窗格的状态 {#states}
侧边栏会显示每个子窗格的状态。

| 状态 | 含义 |
|---|---|
| {{ui:delegation.child.state.creating}} | 正在创建 worktree 和窗格。 |
| {{ui:delegation.child.state.running}} | 正在工作。 |
| {{ui:delegation.child.state.waiting}} | 正在等你回答批准或提问。 |
| {{ui:delegation.child.state.reported}} | 已留下 REPORT.md 并结束运行，可以合并。 |
| {{ui:delegation.child.state.merged}} | 已合并到父窗格的分支。 |
| {{ui:delegation.child.state.ended}} | 运行结束，但没有新的报告。 |
| {{ui:delegation.child.state.interrupted}} | 因为退出应用，运行被中断。 |
| {{ui:delegation.child.state.failed}} | 子窗格未能启动。 |
| {{ui:delegation.child.state.closed}} | 窗格已关闭。在丢弃之前会一直留在列表中。 |

子窗格开始新的运行时，{{ui:delegation.child.state.reported}}状态会被清除；再次报告时会使用下一个报告编号。如果子窗格没有报告就结束了，请在子窗格中再次要求它提交并写入 REPORT.md。

## 合并 {#merge}
把子窗格的结果放进父窗格的分支有两种方法。

1. **卡片上的{{ui:delegation.card.merge}}按钮**：由你来点。亲自启动的子窗格只能用这种方法合并。能快进（fast-forward）时就快进，否则只创建一个合并提交。它会先检查冲突，拒绝时会在卡片上显示原因。
   合并成功后，会询问是否立即丢弃该子窗格。选择{{ui:delegation.mergedDiscard.confirm}}会关闭子窗格并删除其 worktree 和分支；已合并的内容仍保留在父窗格的分支中，但这次合并将无法撤销。选择{{ui:delegation.mergedDiscard.later}}则保留卡片。
   因冲突被拒绝时，应用会请子窗格并入父分支、解决冲突、提交并重新报告。卡片上会这样提示，子窗格变为{{ui:delegation.child.state.running}}。子窗格重新报告后，请再次按{{ui:delegation.card.merge}}按钮。这个请求每次报告只发送一次。子窗格现在无法开始运行时不会发送，卡片上只留下拒绝原因，下次合并时会再试一次。如果子窗格收到请求却没能开始，或者子窗格处于计划模式、只做了计划就结束了，请直接让子窗格解决冲突并重新报告。对于 Codex 和 Antigravity 子窗格，只有在{{ui:permission.label.fullAccess}}模式下才会发送此请求（[Codex 和 Antigravity](#agents)）。
2. **父窗格的 `merge` 工具**：只适用于交给 Claude 的子窗格，只做快进。父窗格要一起传入 `child_status` 给出的子窗格 head。子窗格尚未报告、子窗格或父窗格文件夹中有未提交的更改、子窗格的 head 已移动、父窗格的分支在此期间已经前进（分叉），或者父窗格的分支没有被检出时，合并会被拒绝，并且不做任何更改。这个工具是否会弹出批准卡片，取决于父窗格的权限模式。只有当运行以{{ui:permission.label.fullAccess}}模式开始时，Codex 父窗格才能使用它，在其他模式下会以 `merge_human_only` 被拒绝，请在卡片上合并。

> [!tip]
> 合并一个子窗格后，父窗格的分支会向前移动，所以其余子窗格通常会被 `merge` 工具以分叉为由拒绝。这时请用卡片上的{{ui:delegation.card.merge}}按钮合并。

合并之后，在子窗格开始新的运行之前，即使关闭了窗格，卡片上也会保留{{ui:delegation.card.undo}}按钮。只有当父窗格的分支仍停在这次合并、父窗格空闲且没有未提交的更改时才能使用。撤销后，文件会回到合并之前的样子，窗格还开着的子窗格会重新变为{{ui:delegation.child.state.reported}}。

> [!note]
> 如果合并已完成，但委派记录没能保存，卡片会告诉你。合并保持不变，记录会在下次保存时重新写入。

## 丢弃 {#discard}
卡片上的{{ui:delegation.card.discard}}按钮会删除子窗格的 worktree 和分支，连同尚未合并的工作一起删除。只有你能丢弃，父窗格不能。已合并的子窗格也可以在合并后立即弹出的询问窗口中丢弃（[合并](#merge)）。

- 确认窗口（{{ui:delegation.discard.title}}）会列出嵌套在子窗格 worktree 中、将被一起删除的所有其他 worktree。
- 无法确认这份列表时，确认窗口不会出现丢弃按钮，也不会停止或删除任何东西。

> [!tip]
> 应用绝不会自行删除尚未合并的工作。只有你点击的丢弃才会让未合并的工作消失。

## 关闭窗格和清理 {#close}
- **关闭已合并的子窗格时**，如果它的工作已全部合并且没有留下更改，应用会清理它的 worktree 和分支（`git branch -d`，从不强制删除）。否则只会关闭窗格，worktree 保留。
- **在合并之前关闭子窗格时**，应用就不能再合并它了。卡片上只剩丢弃，分支和 worktree 会一直保留到你丢弃为止。
- **关闭还有打开子窗格的父窗格时**，桌面应用会先询问（{{ui:delegation.parentClose.title}}）。只关闭父窗格，子窗格继续工作。
- 父窗格已关闭的子窗格会作为卡片留在侧边栏的{{ui:delegation.tree.parentClosed}}项目下面。它们的通知保留在卡片上并显示数量，也不再接收后续指示。
- 还有打开的子窗格，或者还有尚未清理的子窗格 worktree 的工作区，无法删除。
- {{platform:windows}} 如果子窗格的 worktree 中有目录联接（junction）或符号链接（例如用链接创建的 `node_modules`），关闭已合并的子窗格时应用不会清理，而是保留 worktree，以免顺着链接把它指向的文件夹也删掉。这时请用卡片上的{{ui:delegation.card.discard}}按钮删除：丢弃会先只移除链接本身，不会动链接指向的文件夹。

![[delegation-parent-closed]]

## 通知和保留 {#notices}
交给 Claude 的子窗格报告或未能启动时，父窗格会收到一条通知。父窗格启动的运行（最初交出的任务或后续指示）没有报告就结束时，也会收到通知。通知只是一个以 `[Mighty Claude notice …]` 开头的简短指引，报告内容由父窗格用 `child_status` 读取。亲自启动的子窗格不会发送通知。

- 父窗格正在运行 Claude 时，通知会直接送进这次运行，并在父窗格的 Mighty 视图中显示为橙色的{{ui:graph.block.delegationNotice}}块。在父窗格答完之后才到达的通知同样显示为这个块，Claude 对它的回答不会接在父窗格请求的结果后面，而是作为单独的{{ui:graph.block.delegationNotice}}图表打开。正在运行的 Codex 父窗格会在这次运行正常结束后，把通知作为下一次运行接收。
- 父窗格在打开应用后正常结束了上一次运行、现在处于空闲状态时，通知会开始它的下一次运行。
- 其他情况下通知会被保留：停止之后、出错之后、重新打开应用之后，以及 CLI 更新期间。一旦已有保留的项目，新通知也会排在其后保留，顺序不会乱。

保留的通知显示在父窗格输入框上方队列的最上面，是带图钉标记的行。这些行没有删除按钮，而且与普通的排队请求不同，停止或重新打开应用后也会留下。

![[delegation-held]]

- 你下一次发送内容时，所有保留的项目会按从旧到新的顺序，排在你的文字前面，作为一次运行一起送出。
- 点击{{ui:queue.runNext}}按钮，只把保留的项目作为一次运行送出。
- 每条通知只会送达一次，重新打开应用也一样。保留的行也会显示在手机的队列中，从手机发送也会一起送出。

## 后续指示 {#follow-up}
父窗格可以用 `follow_up` 工具给交给 Claude 的子窗格发送最多两条后续指示。后续指示按照与通知相同的规则送到子窗格，每条最多 48 KiB。你可以直接在子窗格的输入框中输入，没有次数限制。

## 开启和关闭 {#enable}
{{platform:mac}} 除非你关闭，否则它一直开启。在设置的{{ui:settings.nav.general}}标签页中切换{{ui:settings.delegation.toggle}}；无需重新打开应用，从下一次开始的运行起生效。同一个开关就是 defaults 键 `delegation.enabled`，因此在终端中运行下面的命令也能关闭它，运行 `defaults delete dev.mightyclaude.native delegation.enabled` 则重新开启。

```sh
defaults write dev.mightyclaude.native delegation.enabled -bool false
```

{{platform:mac}} 以前的版本只有设置了该键才会开启，关闭的方法是 `defaults delete …`。现在这样做会让它保持开启，所以要继续关闭，请在设置中关闭{{ui:settings.delegation.toggle}}。

{{platform:windows}} 除非你关闭，否则它一直开启。在设置的{{ui:settings.nav.general}}标签页中切换{{ui:settings.delegation.toggle}}；无需重新打开应用，从下一次开始的运行起生效。同一个开关就是 `HKEY_CURRENT_USER\Software\MightyClaudeNative` 下的 `delegation.enabled` 值（REG_DWORD）：为 0 时关闭，没有这个值或为其他值时开启。因此在命令提示符或 PowerShell 中运行下面的命令也能关闭它，运行 `reg delete "HKCU\Software\MightyClaudeNative" /v delegation.enabled /f` 则重新开启。

```
reg add "HKCU\Software\MightyClaudeNative" /v delegation.enabled /t REG_DWORD /d 0 /f
```

{{platform:windows}} 早期版本只有把这个值设为 1 时才开启。那时留下的 1 会让它保持开启。

- 开启后，Claude 或 Codex 窗格每次开始新的运行时都会带上委派工具。已经在进行中的运行不会带上。
- 关闭后只会去掉工具，{{kbd:⌘Enter}} 会像 {{kbd:Enter}} 一样把请求放入队列。已有的子窗格、卡片和清理都照常工作。

## 限制和支持范围 {#limits}
| 项目 | 限制 |
|---|---|
| 一个父窗格的打开子窗格 | 亲自启动的 3 个，`delegate` 创建的 3 个 |
| 委派深度 | 一层（子窗格不能再创建子窗格） |
| 一个子窗格的后续指示（`follow_up`） | 2 条，每条最多 48 KiB |
| 交出的任务（TASK.md、亲自启动的请求） | 64 KiB |
| 所需磁盘剩余空间 | 10 GB |

- 子窗格运行与父窗格相同的代理，也可以运行另一个代理（[Codex 和 Antigravity](#agents)）。Claude 和 Codex 窗格可以通过工具和 {{kbd:⌘Enter}} 创建子窗格，Antigravity 窗格只能通过 {{kbd:⌘Enter}}（[Codex 和 Antigravity](#agents)）。
- Mac 应用和 Windows 应用都默认开启，并在设置中提供开关（[开启和关闭](#enable)）。手机没有新的界面，保留的行也会显示在手机的队列中。
