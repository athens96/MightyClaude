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
> 批准卡片会在此 Mac 上的 Claude 窗格（Always ask 等）和 Codex 的{{ui:permission.label.onRequest}}权限下弹出。在其他权限下，是允许还是阻止取决于 CLI 设置。

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
- 此时发送的请求会在后台任务结束后开始。要立即插入，请按{{kbd:⌘Enter}}。

![[background-work]]
