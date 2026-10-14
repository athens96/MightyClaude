---
title: 智能体窗格
order: 3
section: agent-pane
---
智能体窗格是与 Claude、Codex 或 Gemini 之一对话的窗格。在下方的输入框中写请求，在上方查看进度和结果。

![[agent-basic]]

## 发送请求 {#send}
1. 在输入框中写下要做的事。
2. 按{{kbd:Enter}}或点击发送按钮。要换行，请按{{kbd:⇧Enter}}。
3. 运行期间，发送按钮的位置会变成停止按钮。要停止，请点击该按钮。

使用输入法组字时（例如输入中文或韩文）按下的{{kbd:Enter}}只会完成组字，不会发送。

![[composer]]

### 运行时继续提出请求 {#queue}
任务运行期间也可以写下一个请求。输入框中有文字时，停止按钮旁会出现与发送按钮相同的圆形按钮。
- {{kbd:Enter}}或{{ui:queue.add}}按钮：作为下一个请求放入队列。当前任务结束后按顺序执行。每个窗格最多可放 16 个。
- {{kbd:⌘Enter}}或{{ui:phone.composer.steer}}按钮：立即发送给正在运行的 Claude。仅适用于此 Mac 上的 Claude 窗格。在 Mighty 视图中显示为橙色的{{ui:graph.block.steer}}块。
- 点击队列中的{{ui:queue.runNext}}，可以立即执行排在第一位的请求。

停止任务时，排队中的请求也会被取消。重新打开应用后，队列会被清空。

## 输入框工具 {#composer}
输入框下方一行从左到右依次是附件、模型、思考强度、权限和更多运行设置（…）。窗格较窄时，部分项目会收进一个菜单。运行期间无法更改，更改后的值从下一个请求起生效。

### 模型 {#model}
点击模型按钮，可以选择{{ui:composer.label.runner}}（Claude、Codex、Gemini）和{{ui:composer.label.model}}。如果列表不对，请点击{{ui:composer.model.refresh}}。切换智能体会开始新的对话。

### 思考强度 {#effort}
{{ui:composer.effort.label}}决定模型思考的深度，可以在 Auto、Low、Medium、High、XHigh、Max 中选择。只显示模型支持的级别。Gemini 没有此项。

### 权限 {#permission}
权限决定智能体无需询问即可做哪些事。

| 智能体 | 可选权限 |
|---|---|
| Claude | Plan mode · Always ask · Accept file edits · Auto mode · Bypass |
| Codex | {{ui:permission.label.defaultCodex}} · {{ui:permission.label.acceptEditsCodex}} · {{ui:permission.label.onRequest}} · {{ui:permission.label.fullAccess}} |
| Gemini | {{ui:permission.label.default}} · {{ui:permission.label.plan}} · {{ui:permission.label.acceptEdits}} · {{ui:permission.label.fullAccess}} |

- Claude 的 Always ask 和 Codex 的{{ui:permission.label.onRequest}}权限在需要额外权限时，会在窗格中请求批准。请参阅[批准与提问](approvals.md#permission)。
- 只有已安装的 Claude CLI 支持时，才会显示 Auto mode。
- Bypass 和{{ui:permission.label.fullAccess}}权限也能访问项目以外的文件和命令。请仅在确有必要时使用。

### 更多运行设置 {#run-settings}
点击 … 按钮会打开{{ui:settings.run.title}}。
- Claude：{{ui:settings.run.maxTurnsLabel}}和{{ui:settings.run.maxBudgetLabel}}。留空则不设限制。
- Codex：{{ui:settings.run.webSearchLabel}}设置和{{ui:settings.run.shellNetworkToggle}}。输入框中的 Fast 按钮在支持的模型和账户上回答更快，但会消耗更多用量。

更改值后，点击{{ui:settings.run.applyButton}}。

![[composer-settings]]

### 附加文件和图片 {#attachments}
点击回形针按钮、将文件拖到输入框中，或用{{kbd:⌘V}}粘贴图片。
- 最多 8 个文件，每个文件 5 MiB，总计 8 MiB。
- 发送前可以预览，并逐个移除。也可以只发送附件。
- 终端窗格不能添加附件。

### 斜杠命令 {#slash}
在输入框中输入 `/` 会显示命令列表。用{{kbd:↑}}{{kbd:↓}}选择，按{{kbd:Enter}}或{{kbd:Tab}}插入，按{{kbd:Esc}}关闭。

| 命令（以 Claude 为准） | 作用 |
|---|---|
| `/model` | {{ui:slash.builtin.model}} |
| `/permissions` | {{ui:slash.builtin.permission}} |
| `/clear` | {{ui:slash.builtin.newConversation}} |
| `/cost`、`/usage` | {{ui:slash.builtin.usage}} |
| `/plugin` | {{ui:slash.builtin.plugin}} |
| `/config` | {{ui:slash.builtin.settings}} |
| `/rename` | {{ui:slash.builtin.rename}} |
| `/help` | {{ui:slash.builtin.help}} |

Codex 和 Gemini 的命令名称略有不同（例如 Codex 的 `/new`、`/approvals`、`/status`）。列表中没有的 `/名称` 会作为 CLI 的技能、用户命令或插件命令原样传递。

### 上下文与用量 {#session-info}
发送按钮旁的圆环显示对话已使用了多少上下文。点击它可以查看{{ui:composer.sessionInfo.contextUsage}}、输入/输出/缓存 token、费用、所用模型和会话 ID。只显示 CLI 报告的值，不会估算未知的值。

![[session-info]]

## 默认视图与 Mighty 视图 {#views}
Claude 和 Codex 窗格的标题栏中有{{ui:graph.view.default}} | {{ui:graph.view.mighty}}切换开关。切换后，对话和正在输入的内容保持不变。每个窗格会各自记住选择。Gemini 窗格只有默认视图。
- {{ui:graph.view.default}}：从上到下阅读的对话记录。
- {{ui:graph.view.mighty}}：以块状流程显示请求、子智能体、后台任务和结果。

### Mighty 流程 {#diagram}
每个请求生成一个请求块，其正下方是{{ui:graph.block.execution}}块。请求块只包含发出的请求；智能体因该请求所做的事（消息、工具调用、进度）显示在{{ui:graph.block.execution}}块中。子智能体和后台任务从{{ui:graph.block.execution}}块下方分支出来。请求结束后，会附上{{ui:graph.block.result}}卡片。
- 拖动空白处或滚动即可移动流程。用缩放按钮可以在 50% 到 150% 之间调整。
- 点击某个块后，只在该块内部滚动。点击空白处或按{{kbd:Esc}}即可取消选择。
- 拖动块的右下角可以调整大小。右键点击可以选择{{ui:graph.block.resetSize}}。大小按会话保存。
- 滚动到最顶部后，可以用{{ui:graph.history.load}}每次加载 10 个之前的请求。加载的记录仅供查看。
- 用标题栏中的拼图按钮可以打开已安装的插件和插件市场。

![[agent-mighty]]

![[agent-mighty-overview]]

### 记录 {#timeline}
用 Mighty 视图顶部的{{ui:graph.view.diagram}} | {{ui:graph.view.timeline}}切换开关，可以按时间顺序以列表形式查看请求。每个请求行之后是它的{{ui:graph.block.execution}}行，然后是子智能体行。

![[agent-timeline]]

### 看板 {#structure}
在同一个切换开关中选择{{ui:graph.view.structure}}，可以把请求看成看板。
- 视图一次只显示一项：没有风格的窗格显示一个请求，使用风格的窗格显示一条流水线。流水线是阶段只向前推进的一串请求；某个请求的阶段比前一个请求退回到更早的阶段时，就开始一条新流水线。
- 用顶部的 ◀ {{ui:graph.structure.board.previous}}和 ▶ {{ui:graph.structure.board.next}}前后切换。点击两者之间的标题（如"请求 3 / 7"）会打开列表，显示每一项的编号、标题、状态和阶段，可直接选择。
- 下方显示进度：状态（使用风格的窗格还有流水线名称），以及用线连起来的阶段标签。没有风格的窗格是{{ui:graph.structure.column.request}} → {{ui:graph.structure.column.execution}} → {{ui:graph.structure.column.result}}，使用风格的窗格是该模式的阶段。已完成的阶段为绿色，当前阶段为强调色，其余为灰色。
- 内容放在并排的列中，每个阶段一列，列头显示阶段名称和卡片数。没有风格时，{{ui:graph.structure.column.request}}列放请求，{{ui:graph.structure.column.execution}}列放显示进度（最新消息和工具调用）与状态的卡片，后面每个子代理一张卡片，{{ui:graph.structure.column.result}}列放结果。使用风格时，每个阶段列放该阶段的请求卡片（请求、执行摘要和一行结果），其子代理卡片放在下方。嵌套的子代理会缩进，并注明它在哪个块之下。
- 长文本默认折叠，点击{{ui:graph.structure.more}}即可就地展开。点击卡片标题栏中的小按钮，会在流程中显示该块。
- 视图从最新的一项打开，停留在那里时会随新的请求或流水线前进。查看较早的一项时出现新项，位置保持不变，▶ 旁会出现{{ui:graph.structure.board.newRequest}}或{{ui:graph.structure.board.newPipeline}}标记，点击即可转到最新的一项。
- 每一列各自移动：列头留在顶部，只有卡片上下移动。在某一列上用触控板或鼠标滚轮上下滚动，或拖动其中的空白处，只会移动这一列；列放不下时，横向滚动或拖动即可左右移动看板。
- 在最新一项上，停在末尾的列会跟随新内容向下。自己移动过的列，或展开了文字的列，会停在原处，回到该列末尾后再继续跟随。来了新请求时，每一列都会显示末尾；之前的项打开时，每一列都从顶部开始。Windows 应用中整个看板仍然一起移动。

![[agent-structure]]

### 结果卡片 {#result}
请求结束后，{{ui:graph.block.result}}卡片会整理出结果。如果结果中提到了文件，可以用{{ui:graph.resultFiles.openButton}}按钮打开文件列表。点击块中的文件路径会弹出预览。最新的结果卡片会适应窗格大小；如果你手动调整过大小，会出现{{ui:graph.result.fitToWindow}}按钮。

![[result-card]]
