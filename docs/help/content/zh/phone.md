---
title: 手机远程
order: 9
section: phone
---
通过手机应用，你在外面也能查看和操作 Mac 上的 Mighty Claude：查看任务状态、发送请求、回答批准和提问。

Mac 与手机通过中继服务器连接。内容全程端到端加密，中继无法查看内容。无需在路由器上开放端口，也无需使用 VPN。如果你在用 Tailscale，手机也可以[不经中继直接连接](#direct)。

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

## 通过 Tailscale 直接连接 {#direct}
Mac 和手机在同一个 [Tailscale](https://tailscale.com) 网络中时，手机会不经中继直接连接 Mac。配对方式和加密都不变；直接连不上 Mac 时，手机会自动改用中继。

1. 在 Mac 和手机上打开 Tailscale，并用同一个账号登录。
2. 在 Mac 的 {{ui:settings.nav.mobile}} 标签页中确认 {{ui:mobileRemote.direct.toggle}} 已打开（默认打开）。下面一行显示 Tailscale 地址就说明准备好了。如果显示 {{ui:mobileRemote.direct.status.notFound}}，说明 Mac 上的 Tailscale 没有打开。
3. 已经配对的手机不需要重新配对。下次连接时 Mac 会告诉它自己的地址。

在手机的 {{ui:phone.tabs.hosts}} 标签页中，Mac 卡片上会显示 {{ui:phone.direct.route.direct}} 或 {{ui:phone.direct.route.relay}}。

Windows 电脑也一样，只是需要一条 Windows 防火墙规则，以免连接被拦截。在 {{ui:settings.nav.mobile}} 标签页中点击 {{ui:mobileRemote.direct.windows.firewall.addButton}}，并允许 Windows 的管理员确认。该规则只允许来自 Tailscale 地址的连接到达本应用。规则存在后，同一行的按钮会变成 {{ui:mobileRemote.direct.windows.firewall.removeButton}}；无法读取规则时则变成 {{ui:setup.button.recheck}}，它会在不请求管理员确认的情况下重新读取。直接连接开启时若更改了端口，请先删除规则再重新添加。关闭直接连接或手机远程时会一并删除规则（如果这台电脑正在[共享工作区](remote.md#share)，还需要它的端口，规则就会保留），Windows 会再次请求管理员确认。这是同一条规则，同时打开手机直连的端口和工作区共享的端口。规则存在期间，同一行还会出现 {{ui:remote.windows.firewall.update}} 按钮，按当前使用的端口调整规则；{{ui:settings.remote.navTitle}}标签页也能添加、更新或删除同一条规则。

## 在手机上可以做的事 {#use}
手机应用有四个标签页。

| 标签页 | 内容 |
|---|---|
| {{ui:phone.tabs.dashboard}} | {{ui:phone.dashboard.title}}：运行中、待你处理和已完成的数量，以及会话卡片 |
| {{ui:phone.tabs.sessions}} | 所有工作区和会话。打开会话可以查看对话并发送请求。 |
| {{ui:phone.tabs.alerts}} | 需要你回复的批准和提问，以及已完成的任务 |
| {{ui:phone.tabs.hosts}} | 已连接的 Mac 及其连接状态 |

在会话中，可以发送和停止请求，更改模型、权限和思考强度，使用斜杠命令，附加文件，以及查看工作区文件（只读）。

会话中的任务正在运行时，发送按钮会变成{{ui:queue.add}}，你发送的内容会作为该窗格的下一个请求进入队列，不会插入正在运行的任务。

在{{ui:phone.session.view.blocks}}视图中，已完成的请求会直接显示请求和{{ui:phone.blocks.finalResult}}。过程步骤折叠为一行，点击即可展开。仍在运行的请求会显示所有步骤。

{{ui:phone.session.view.structure}}视图以看板的形式逐个显示桌面应用发送的流水线。没有风格的窗格中每个请求是一个看板；使用风格的窗格中每条流水线是一个看板，即阶段不回退的一串连续请求。用顶部的 ◀ ▶ 切换看板，点击中间的位置（如"请求 3 / 7"）可从列表中选择。查看较早的看板时如果出现新看板，会显示{{ui:graph.structure.board.newRequest}}或{{ui:graph.structure.board.newPipeline}}标记，点击即可前往最新的看板。下面是看板的状态和一排阶段标签：已完成的阶段为绿色，当前阶段为强调色，尚未开始的阶段为灰色，每个标签都带有卡片数。每个阶段各有一列，左右滑动或点击标签即可切换，打开时显示当前阶段的列。没有风格的窗格的列为{{ui:graph.structure.column.request}} → {{ui:graph.structure.column.execution}} → {{ui:graph.structure.column.result}}，子代理卡片排在{{ui:graph.structure.column.execution}}卡片之后。使用风格的窗格中，每列列出该阶段的请求及其子代理，已结束的请求折叠为一张卡片，点按即可展开。正在运行的卡片边框会闪烁。此视图需要与手机应用版本相同或更新的桌面应用（Mac 或 Windows）；桌面应用较旧时，视图会提示更新桌面应用。

在 Mac 上通过 [Mighty 编排](delegation.md)创建的子窗格，会在会话列表中缩进一级显示在父窗格下方，右侧显示其状态（{{ui:delegation.child.state.reported}}、{{ui:delegation.child.state.merged}} 等）。Windows 电脑目前还不会把子窗格的父窗格和状态发送给手机，因此 Windows 电脑上的子窗格在列表中与普通窗格一样显示。合并或丢弃子窗格无法在手机上进行，只能在桌面应用（Mac 或 Windows）中进行。使用 Antigravity 的桌面应用（Mac 0.4.23 及以上、Windows 0.4.28 及以上）中的 Gemini 窗格显示为 Antigravity（{{ui:badge.beta}}），更旧版本应用中的窗格显示为 Gemini。

## 批准与提问 {#approvals}
智能体请求批准或提问时，{{ui:phone.tabs.alerts}}标签页上会显示数量。
- 权限请求：轻点{{ui:permission.allow}}或{{ui:permission.deny}}。
- Antigravity 权限卡片：每项被拒绝的操作都会显示类型以及命令、地址或路径。可选择{{ui:permission.allowOnce}}、{{ui:antigravity.permission.allowAlways}}或{{ui:antigravity.permission.deny}}；允许后会在同一个对话中继续（[Antigravity 的权限卡片](approvals.md#antigravity-permission)）。如果卡片中只有没有规则可以允许的操作，则只有{{ui:antigravity.permission.deny}}，并提示你更改权限模式。如果命令会串联、使用管道、重定向或可能删除数据，则不显示{{ui:antigravity.permission.allowAlways}}。如果主机无法接受你的答复，卡片会保留并显示原因。不认识这种卡片的旧版手机应用会把它显示为普通的权限请求。
- 提问：选择答案后，轻点{{ui:phone.questionnaire.submit}}。
- 计划卡片：用与 Mac 上相同的按钮（{{ui:plan.card.approveAuto}}、{{ui:plan.card.revise}}等）回答。

## 通知 {#notifications}
已连接的电脑（Mac 或 Windows）上的运行完成或失败时，以及需要你处理权限请求、问题、待批准的计划或登录过期时，手机会收到通知。目前只有 Android 应用会收到。
- 第一次打开应用时（Android 13 及以上）会询问一次是否允许通知。要按类型关闭，请在 {{ui:phone.tabs.hosts}} 标签页打开 {{ui:phone.settings.title}}，修改 {{ui:phone.settings.notifications}} 下的开关。如果系统设置里该应用的通知被关闭，{{ui:phone.settings.title}} 会提示。
- 收到权限请求通知时，可以直接点 {{ui:permission.allow}} 或 {{ui:permission.deny}}。Antigravity 卡片提供 {{ui:permission.allowOnce}}、{{ui:antigravity.permission.allowAlways}} 和 {{ui:antigravity.permission.deny}}。只能允许一次的卡片（例如串联命令或可能删除数据的命令）没有 {{ui:antigravity.permission.allowAlways}}。问题和计划的通知没有按钮，点开后在应用里回答。没有任何可允许操作的 Antigravity 卡片同样不带按钮，点开后在应用里回答。
- 按钮只有在手机解锁后才起作用。锁屏时点按，通知会变为“{{ui:phone.push.outcome.locked}}”，按钮仍保留。如果已在电脑上回答，通知会变为 {{ui:phone.push.outcome.already}}；如果答复没能发出，会变为 {{ui:phone.push.outcome.failed}}。最后这种情况请点开通知，在应用里回答。
- 点按通知会打开对应的会话。手机正在显示某个会话时，不会收到关于它的通知。
- 通知在锁屏上以私密方式显示：如果你设置了在锁屏上隐藏敏感内容，标题和正文就不会显示。
- 电脑会加密每条通知，只有这部手机能读取，中继和 Google 无法读取。通知经由中继发送，因此通过 Tailscale 直接连接时，只要电脑能上网也会收到。
- 电脑应用和手机应用都必须是带有此功能的版本。没有此功能的电脑应用不会发送通知。如果你是用很旧的手机应用配对的，请重新配对。
- 手机远程访问关闭期间，电脑不会发送任何内容；重新打开后，也不会补发这段时间里发生的事。

应用崩溃时只会把出错位置发给开发者，不发送对话、命令或路径。可以在 {{ui:phone.settings.title}} 中关闭 {{ui:phone.settings.crashReports}}。

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
