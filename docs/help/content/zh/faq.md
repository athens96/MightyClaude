---
title: 常见问题与故障排除
order: 10
section: faq
---
## 无法发送请求 {#cannot-send}
- 输入框下方会显示无法发送的原因。如果 CLI 未安装或需要登录，请检查{{ui:menu.settings}} → {{ui:settings.nav.tools}}和{{ui:settings.nav.cli}}。
- 终端窗格不能附加文件。请使用 AI 窗格。
- 运行期间按{{kbd:Enter}}不会立即发送，而是放入队列。请参阅[运行时继续提出请求](agent-pane.md#queue)。

## 模型列表中没有想要的模型 {#model-missing}
1. 打开模型按钮，点击{{ui:composer.model.refresh}}。
2. 如果仍然没有，请在{{ui:settings.nav.cli}}标签页中点击{{ui:settings.cliAccounts.resetModelsButton}}按钮。
3. 自己使用的模型名称，可以添加到{{ui:settings.nav.models}}标签页的{{ui:settings.phaseModels.registeredTitle}}中。

## 登录总是断开 {#login}
请打开{{ui:settings.nav.cli}}标签页中的{{ui:settings.cliAccounts.autoLoginToggle}}。登录一断开就会重新开始登录，并重新发送失败的请求。请参阅[登录断开时](approvals.md#login-recovery)。

## 韩文输入被拆成单独的字母 {#korean-input}
在菜单栏的{{ui:menu.workspace}}菜单中点击{{ui:menu.reconnectInputMethod}}。如果问题仍然存在，请用{{ui:menu.saveInputDiagnostics}}保存诊断文件并发给我们。

> [!warning]
> 应用运行期间，请勿覆盖应用文件，也不要同时打开两个应用。这可能会断开与 macOS 输入法的连接，导致韩文被拆成单独的字母。更新时请使用应用内的{{ui:settings.appUpdate.installButton}}。

## 浏览器窗格是空白的 {#browser-empty}
浏览器窗格默认处于关闭状态。请在{{ui:settings.nav.general}}中打开{{ui:settings.display.browserToggle}}，然后重新打开应用。请参阅[浏览器窗格](terminal-files-browser.md#browser)。

## 手机无法连接 {#phone}
- 检查 Mac 的{{ui:settings.nav.mobile}}中是否已打开{{ui:settings.mobileRemote.allowToggle}}。
- 如果状态一直停在“正在连接中继”，请点击{{ui:settings.mobileRemote.reconnectButton}}按钮。
- 如果手机的{{ui:phone.tabs.hosts}}标签页中显示{{ui:phone.hosts.reachability.unauthorized}}，说明 Mac 的密钥已更改。请用二维码重新连接。
- Mac 处于睡眠状态时无法连接。

## 关闭窗格后，对话会消失吗？ {#history}
不会。对话记录会作为 CLI 的会话保留下来。在同一文件夹中新添加 Claude 或 Codex 窗格时，选择{{ui:resume.choice.resume}}按钮即可继续。请参阅[继续之前的会话](layout.md#resume)。

## 我的数据保存在哪里？ {#data}
应用的设置和记录只保存在这台电脑上。位置可以在{{ui:settings.nav.about}}标签页的{{ui:settings.appInfo.stateLocationLabel}}中查看。发送给 AI 的请求由各个 CLI 发送到各自公司的服务。使用手机远程时，内容会以加密状态经过中继。
