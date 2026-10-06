---
title: 手机远程
order: 8
section: phone
---
通过手机应用，你在外面也能查看和操作 Mac 上的 Mighty Claude：查看任务状态、发送请求、回答批准和提问。

Mac 与手机通过中继服务器连接。内容全程端到端加密，中继无法查看内容。无需在路由器上开放端口，也无需使用 VPN。

## 获取手机应用 {#get-app}
从[下载页面](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/index.html)下载 Android 应用（APK）并安装。

## 连接手机 {#pair}
在 Mac 上：
1. 打开{{ui:menu.settings}} → {{ui:settings.nav.mobile}}标签页。
2. {{ui:settings.mobileRemote.relayLabel}}地址留空即使用默认中继。如果你自己运行中继，请填写其地址并点击{{ui:settings.mobileRemote.applyButton}}。
3. 打开{{ui:settings.mobileRemote.allowToggle}}。随后会显示二维码。

在手机上：
1. 打开应用，轻点{{ui:phone.tabs.hosts}}标签页中的{{ui:phone.hosts.addHost}}。首次打开时会先显示连接指南。
2. 用{{ui:phone.pair.mode.qr}}扫描 Mac 上的二维码。如果无法使用相机，请在 Mac 上点击{{ui:settings.mobileRemote.copyLinkButton}}，把链接发到手机，再粘贴到{{ui:phone.pair.mode.manual}}中。
3. 轻点{{ui:phone.pair.connect}}即可连接。

![[settings-mobile]]

已连接的手机会显示在 Mac 的{{ui:settings.mobileRemote.connectedDevicesTitle}}中。不再使用某部手机时，请点击{{ui:settings.mobileRemote.revokeRowButton}}。

> [!warning]
> 点击{{ui:settings.mobileRemote.regenerateKeyButton}}后，所有已连接的手机都需要重新连接。请在二维码或密钥被他人看到时使用。

## 在手机上可以做的事 {#use}
手机应用有四个标签页。

| 标签页 | 内容 |
|---|---|
| {{ui:phone.tabs.dashboard}} | {{ui:phone.dashboard.title}}：运行中、待你处理和已完成的数量，以及会话卡片 |
| {{ui:phone.tabs.sessions}} | 所有工作区和会话。打开会话可以查看对话并发送请求。 |
| {{ui:phone.tabs.alerts}} | 需要你回复的批准和提问，以及已完成的任务 |
| {{ui:phone.tabs.hosts}} | 已连接的 Mac 及其连接状态 |

在会话中，可以发送和停止请求，更改模型、权限和思考强度，使用斜杠命令，附加文件，以及查看工作区文件（只读）。

## 批准与提问 {#approvals}
智能体请求批准或提问时，{{ui:phone.tabs.alerts}}标签页上会显示数量。
- 权限请求：轻点{{ui:permission.allow}}或{{ui:permission.deny}}。
- 提问：选择答案后，轻点{{ui:phone.questionnaire.submit}}。
- 计划卡片：用与 Mac 上相同的按钮（{{ui:plan.card.approveAuto}}、{{ui:plan.card.revise}}等）回答。

手机不会收到推送通知。请打开应用查看。

## 远程屏幕 {#screen}
可以在手机上查看 Mac 的屏幕，获得允许后还可以操控。这是{{ui:badge.beta}}功能，目前仅在 Android 应用中提供。
1. 在 Mac 的{{ui:settings.nav.mobile}}标签页下方的{{ui:settings.screenShare.sectionTitle}}中，为每部手机将{{ui:settings.screenShare.grantLabel}}设为{{ui:settings.screenShare.grantView}}或{{ui:settings.screenShare.grantControl}}。新手机的初始设置为{{ui:settings.screenShare.grantNone}}。
2. 用{{ui:settings.screenShare.permissions.openButton}}授予屏幕录制、辅助功能等 Mac 权限。
3. 在手机上轻点{{ui:phone.screenShare.open}}，然后选择{{ui:phone.screenShare.mode.view}}或{{ui:phone.screenShare.mode.control}}。

首次允许操控时，请确认两个屏幕上显示的指纹一致。每次开始操控时，手机都会要求进行生物识别或 PIN 验证。

> [!tip]
> 在 Mac 上按{{kbd:⌃⌥⌘K}}可以立即停止所有远程屏幕会话。操控在 10 分钟内、查看在 30 分钟内没有任何输入时，会自动结束。

## 自行运行中继 {#own-relay}
可以自己运行中继，代替默认中继。将仓库中的 `relay/` 文件夹部署到服务器，然后把其地址（`wss://…`）填入 Mac 的{{ui:settings.mobileRemote.relayLabel}}中。
