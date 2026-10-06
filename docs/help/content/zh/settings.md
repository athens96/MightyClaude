---
title: 设置
order: 7
section: settings
---
通过{{ui:menu.settings}}（{{kbd:⌘,}}）或侧边栏底部的设置按钮打开。在左侧选择标签页，用{{ui:settings.closeButton}}按钮或{{kbd:Esc}}关闭。设置会记住你上次查看的标签页。

## 通用 {#general}
在{{ui:settings.nav.general}}标签页的{{ui:settings.display.sectionTitle}}中更改应用的外观。
- {{ui:settings.display.themeLabel}}：{{ui:settings.display.themeDarkMac}}或{{ui:settings.display.themeLightMac}}。也可以用侧边栏底部的按钮切换。
- {{ui:settings.display.languageLabel}}：应用的语言。请参阅[更改语言](getting-started.md#language)。
- {{ui:settings.display.statusLineToggle}}：运行 Claude 设置中的状态栏命令，并将结果显示在输入框下方。
- {{ui:settings.display.browserToggle}}：启用浏览器窗格。重新打开应用后生效。
- {{ui:agentTerminal.urlOpen.settingTitle}}：设置智能体要打开的链接在哪里打开。

![[settings-general]]

## 模型 {#models}
在{{ui:settings.nav.models}}标签页的{{ui:settings.phaseModels.sectionTitle}}中，为整台 Mac 设置各工作阶段使用的模型。阶段包括{{ui:settings.phaseModels.phase.planning}}、{{ui:settings.phaseModels.phase.execution}}、{{ui:settings.phaseModels.phase.review}}和{{ui:settings.phaseModels.phase.subagents}}。
- 保持{{ui:settings.phaseModels.defaultOption}}时，由 CLI 决定。
- {{ui:settings.phaseModels.paneModel}}会使用窗格模型按钮中选择的模型。
- 列表中没有的模型名称，可以在{{ui:settings.phaseModels.registeredTitle}}中{{ui:settings.phaseModels.addButton}}。如果该模型支持思考强度，请打开{{ui:settings.phaseModels.supportsEffortLabel}}。

![[settings-models]]

## 风格 {#styles}
在{{ui:settings.nav.styles}}标签页中管理{{ui:settings.styles.sectionTitle}}列表：从文件注册风格、检查内容后允许，或撤销允许。详情请参阅[风格](styles.md#custom)。

![[settings-styles]]

## 组件 {#components}
{{ui:settings.nav.tools}}标签页显示应用所需的内容是否已准备就绪。
- {{ui:settings.components.sectionTitle}}：Claude、Codex、Gemini CLI 以及所需插件的状态，显示为{{ui:settings.components.statusInstalled}}、{{ui:settings.components.statusMissing}}或{{ui:settings.components.statusAttention}}。需要安装时，复制安装命令并在终端中运行，然后点击{{ui:settings.components.recheckButton}}。
- {{ui:settings.toolkit.sectionTitle}}：常用工具的列表。安装前会显示所有要运行的命令，并请你{{ui:settings.toolkit.confirmTitle}}。可以{{ui:settings.toolkit.exportButton}}或{{ui:settings.toolkit.importButton}}该列表。

![[settings-tools]]

## CLI {#cli}
在{{ui:settings.nav.cli}}标签页中管理 CLI 的状态、登录和更新。

### 登录 {#cli-accounts}
在{{ui:settings.cliAccounts.sectionTitle}}中分别登录各个 CLI。
1. 点击{{ui:settings.cliAccounts.buttonLogin}}。Claude 需要在{{ui:settings.cliAccounts.buttonLoginClaude}}和{{ui:settings.cliAccounts.buttonLoginConsole}}之间选择。
2. 应用内会打开终端窗格，并开始 CLI 登录。请在浏览器中完成授权。
3. 登录完成后，状态会随之更新。更换后的账户从下一个请求起生效。

要更换账户，请点击{{ui:settings.cliAccounts.buttonChange}}；要退出登录，请点击{{ui:settings.cliAccounts.buttonLogout}}。登录前需要至少打开一个工作区。打开{{ui:settings.cliAccounts.autoLoginToggle}}后，登录断开时会自动重新登录，并重新发送失败的请求。

### CLI 更新 {#cli-update}
在{{ui:settings.cliUpdate.sectionTitle}}中将已安装的 CLI 更新到最新版本。
- {{ui:settings.cliUpdate.updateButton}}：立即更新。
- {{ui:settings.cliUpdate.autoUpdateToggle}}：在打开应用时以及每 6 小时检查一次。
- {{ui:settings.cliUpdate.autoUpdatePluginsToggle}}：同时更新 Claude 和 Codex 插件。

更新时沿用 CLI 最初的安装方式（npm、Homebrew 等）。

![[settings-cli]]

## 手机远程 {#mobile}
在{{ui:settings.nav.mobile}}标签页中连接手机，并允许查看屏幕。请参阅[手机远程](phone.md)。

## 宠物与提醒 {#companion}
在{{ui:settings.nav.companion}}标签页中设置桌面宠物和完成通知。
- {{ui:companion.settings.enabled}}：显示一只在屏幕上走动的浣熊宠物。可以拖动宠物移动位置，点击它可以打开或关闭气泡。
- {{ui:companion.settings.task}}：在气泡中显示请求、当前任务和用时。也可以在气泡中回答批准请求（{{ui:permission.allowOnce}} / {{ui:permission.deny}}）。
- {{ui:companion.settings.pet}}：选择宠物。可以用{{ui:companion.settings.import}}添加其他宠物。
- {{ui:companion.settings.motion}}：减少动作。
- {{ui:companion.settings.macNotifications}}：请求成功完成时发送 Mac 通知。点击通知会打开对应的窗格。通知中不包含请求或结果的内容。

![[settings-companion]]

![[companion-pet]]

## 关于 {#about}
在{{ui:settings.nav.about}}标签页中可以更新应用，并查看版本和数据存储位置。
- {{ui:settings.appUpdate.sectionTitle}}：请参阅[更新](getting-started.md#update)。
- {{ui:settings.appInfo.stateLocationLabel}}：存放应用设置和记录的文件夹。可以用{{ui:settings.appInfo.openFinderButton}}打开。

![[settings-about]]
