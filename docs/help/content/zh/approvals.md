---
title: 批准、提问与登录恢复
order: 5
section: approvals
---
智能体在工作中需要你做决定时，窗格中会弹出卡片。回答卡片后，任务就会继续。在工作状态、桌面宠物和手机上也能看到同样的请求。

## 批准权限 {#permission}
当智能体想做权限范围以外的事（运行命令、修改文件等）时，会弹出批准卡片。

![[permission-card]]

1. 阅读卡片，了解它要做什么。命令和文件路径显示在框中，准确的输入可以在原始 JSON 中查看。
2. 点击{{ui:permission.allowOnce}}或{{ui:permission.deny}}。

批准仅对这一次请求有效，不会创建今后持续允许的规则。

> [!note]
> 批准卡片会在此 Mac 上的 Claude 窗格（Always ask 等）和 Codex 的{{ui:permission.label.onRequest}}权限下弹出。在其他权限下，是允许还是阻止取决于 CLI 设置。Antigravity 的工作方式不同，会[用自己的方式询问](#antigravity-permission)。

## Antigravity 的权限卡片 {#antigravity-permission}
Antigravity 在工作过程中无法向应用提问。遇到必须询问的操作（运行命令、写入文件等）时，它只跳过该操作，先结束这一轮，再把所有被拒绝的操作汇总到一张卡片里询问。{{ui:permission.label.default}}权限下也会弹出，手机上会出现同样的卡片。

卡片会列出每一项被拒绝的操作。每项都会显示类型（{{ui:antigravity.permission.kind.command}}、{{ui:antigravity.permission.kind.url}}、{{ui:antigravity.permission.kind.file}} 等）、命令、地址或路径，以及用于允许它的规则。

1. 先看被拒绝的是什么。
2. 选择一个按钮。
   - {{ui:permission.allowOnce}}：为被拒绝的操作临时给 Antigravity 添加一条规则，并在同一个对话中继续。随后的运行结束时（完成、失败、停止或关闭应用），应用只会删除自己添加的规则。
   - {{ui:antigravity.permission.allowAlways}}：把同一条规则保留在 Antigravity 的设置中（`~/.gemini/antigravity-cli/settings.json` 的 `permissions.allow`），下次无需询问即可运行。在 Windows 上该文件是 `%USERPROFILE%\.gemini\antigravity-cli\settings.json`。
   - {{ui:antigravity.permission.deny}}：关闭卡片，不继续运行任何内容。

允许后会在同一个对话中继续。如果窗格正忙于其他事情，继续运行的请求会进入队列。

规则按开头匹配。命令规则会允许**以该文本开头的所有命令**（`command(git status)` 也会允许 `git status --short`），指向文件夹的文件规则会允许其中的所有文件。{{ui:permission.allowOnce}}的规则也会写入 Antigravity 的全局设置，因此规则存在期间，它适用于**此电脑上的所有 Antigravity 运行**（其他窗格和工作区，以及终端中的 `agy`）。

串联命令（`;`、`&&`、`||`）、使用管道（`|`）、重定向（`>`、`<`）、命令替换（反引号、`$(`）、后台运行（`&`），或删除数据、提升权限的命令（`rm`、`sudo`、`git push --force`、`git reset --hard`、`git clean`、`kill` 等）只能{{ui:permission.allowOnce}}。卡片上的{{ui:antigravity.permission.allowAlways}}会关闭并显示原因；手机上不会显示该按钮。

应用不会为含有 `*` 或括号的命令、指向整个磁盘（`/`）或主文件夹（`~`）的规则创建规则。也不会为 Antigravity 自己的设置文件夹（`~/.gemini`）或应用数据文件夹之内、或其上层文件夹（如主文件夹）创建文件规则，因为那里的规则会让 Antigravity 改写自己的权限。提到 `.gemini` 的命令只能{{ui:permission.allowOnce}}。这类操作会显示为无法用规则允许的操作。

这与 Claude 不同。Claude 会在执行前停下来等待你的回答；Antigravity 则先结束这一轮，之后再询问，你允许后才继续。因此卡片出现时，其余工作可能已经完成。

> [!note]
> 没有规则可以允许的操作仍会显示在卡片上，并附有更改权限模式的提示。只含这类操作的卡片只有{{ui:antigravity.permission.deny}}一个按钮。如果 Antigravity 的设置文件无法读取或不是 JSON，应用不会改动它，并在卡片上显示原因。应用只会在 `permissions.allow` 下添加和删除规则，其他设置保持原样。如果临时添加的规则删除失败，应用会稍后再试，下次打开应用时也会删除。

[远程工作区](remote.md)中的 Antigravity 窗格会修改运行它的那台电脑的设置。每张卡片的允许只能使用一次，并且只有那台电脑开启了{{ui:settings.remote.allowFullAccess}}时，才接受{{ui:antigravity.permission.allowAlways}}。

## 回答问题 {#questions}
Claude 给出选项向你提问时，会弹出{{ui:phone.questionnaire.title}}卡片。

![[question-card]]

1. 阅读问题并选择答案。有些问题可以多选。
2. 如果没有合适的答案，请在{{ui:phone.questionnaire.custom}}中自己填写。
3. 如果有多个问题，用{{ui:phone.questionnaire.next}}按钮进入下一题。
4. 全部回答后，点击{{ui:phone.questionnaire.submit}}。只选择答案不会发送。

## 登录断开时 {#login-recovery}
CLI 登录过期导致请求失败时，输入框上方会出现登录恢复卡片。
1. 点击{{ui:loginRecovery.loginButton}}。
2. 在打开的浏览器中完成授权。如果浏览器显示了代码，请将其粘贴到卡片中并点击{{ui:loginRecovery.codeSubmit}}。
3. 登录完成后，失败的请求会自动重新发送。

如果已经在其他地方登录，请点击{{ui:loginRecovery.resendButton}}。如果浏览器登录有困难，请使用{{ui:loginRecovery.terminalButton}}。

> [!tip]
> 打开{{ui:settings.nav.cli}}标签页中的{{ui:settings.cliAccounts.autoLoginToggle}}后，登录一断开就会立即重新开始登录。

## 后台任务 {#background}
Claude 有时会在后台运行耗时较长的命令或子智能体。这时即使 Claude 的回答已经结束，任务也仍在进行。
- 输入框上方会显示一行提示，说明本轮已结束，但后台任务仍在运行。点击{{ui:plan.background.show}}可以查看每个任务的类型、状态和用时。
- 在 Mighty 视图中显示为{{ui:graph.block.task}}块。
- 此时发送的请求会在后台任务结束后开始。要立即插入，请按{{kbd:⌘Enter}}。在开启了[Mighty 编排](delegation.md#worktree)、位于 git 仓库中的 Claude 窗格里，{{kbd:⌘Enter}} 会改为立即在新的 worktree 中运行。
- 如果这一行碍事，点击它右侧的眼睛按钮（{{ui:plan.background.hideStrip}}）。所有窗格中都会隐藏这一行，后台任务照常运行。
- 要重新显示，请点击输入框右侧的{{ui:composer.backgroundWork.name}}按钮。也可以在“{{ui:menu.settings}} → {{ui:settings.nav.general}} → {{ui:settings.display.sectionTitle}}”中打开{{ui:settings.display.backgroundWorkToggle}}，或在窗格的“…”菜单中选择{{ui:pane.menu.backgroundWork}}。这一行是否展开也会被记住。

![[background-work]]
