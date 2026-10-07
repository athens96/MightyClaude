---
title: 风格
order: 4
section: styles
---
风格是预先定好请求方式的工作流程。它备有阶段和按钮，能让较长的任务按既定顺序推进。不使用风格时，就像平常一样自由地发送请求（CLI）。

## 选择风格 {#pick}
风格用于此 Mac 上的 Claude 窗格，且须处于 Mighty 视图。
1. 将 Claude 窗格切换到{{ui:graph.view.mighty}}视图。
2. 打开输入框上方的风格菜单（{{ui:guidedPanel.stylesMenuAccessibility}}）。
3. 选择一种风格。要回到自由请求，请选择 CLI。

选择结果按窗格保存。运行期间无法更改。如果缺少风格所需的插件或工具，会显示{{ui:guidedPanel.installButton}}按钮。点击后，安装命令会填入新的终端窗格，{{kbd:Enter}}需要你自己按。

> [!note]
> 内置风格的阶段名称和按钮目前只有韩文。

## 内置风格 {#bundled}
| 风格 | 作用 |
|---|---|
| Ouroboros | 先通过访谈明确目标，再按种子 → 执行 → 评估 → 进化的顺序推进。第一个请求就是访谈。需要 Ouroboros 插件和 `uvx`。 |
| Paperthin | 把侧重于删减而非添加的小技能汇集成按钮。输入的文字会原样发送。 |
| Superpowers | 按头脑风暴 → 计划 → 执行 → 完成的顺序推进。阶段和进度条会根据计划文件中的复选框自动推进。 |
| Claude 计划（클러드 플랜） | 每个新请求都以 Claude 的计划模式开始。批准计划卡片后开始执行。清单进度和后台任务以小组件形式显示。 |

## 计划卡片 {#plan-card}
Claude 在计划模式（Plan mode 或 Claude 计划风格）中完成计划后，会弹出计划卡片。

![[plan-card]]

1. 阅读计划。点击{{ui:plan.card.expand}}可以像文档一样放大查看。
2. 选择其中一项：
   - {{ui:plan.card.approveAuto}}：批准后，文件修改不再询问，直接进行。
   - {{ui:plan.card.approveConfirm}}：批准后，每项操作都需要确认。
   - {{ui:plan.card.revise}}：写下需要修改的地方并点击{{ui:plan.card.reviseSend}}，Claude 会重新制定计划。
   - {{ui:plan.card.cancel}}：拒绝该计划并停止本次任务。

桌面宠物也会显示完成的计划：收到的时间、计划的前几行以及答复按钮。可以直接在宠物上点击{{ui:plan.card.approveAuto}}或{{ui:plan.card.cancel}}。要写修改请求或阅读整个计划，请点击{{ui:companion.plan.review}}（在 Mac 上也可以点击{{ui:plan.card.revise}}）。计划会在一个带有全部四种答复的小窗口中打开，答复计划后窗口会自动关闭。

已答复的计划会保留在记录中，可以再次查看。

![[plan-document]]

## 添加其他风格 {#custom}
除了内置风格，也可以使用以文件形式提供的风格。
1. 在{{ui:menu.settings}} → {{ui:settings.nav.styles}}中点击{{ui:settings.styles.registerButton}}。仓库 `.claude/mighty-styles/` 中的风格会被自动发现。如果列表不对，请点击{{ui:settings.styles.rescanButton}}。
2. 新风格需要先检查一次内容。用{{ui:settings.styles.viewButton}}按钮确认来源、自动允许的工具、安装命令和所有按钮。
3. 确认无误后，点击{{ui:settings.styles.allowButton}}。

风格文件发生变化后，需要重新确认。要撤销允许，请在同一位置点击{{ui:settings.styles.revokeButton}}。

> [!warning]
> 自动允许工具的风格会在不询问的情况下运行这些工具。请仅在信任其来源时允许。
