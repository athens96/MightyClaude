---
title: 远程连接（Tailscale）
order: 10
section: remote
---
用 [Tailscale](https://tailscale.com/kb/1017/install) 连接两台 Mac 或 Windows 电脑上的 Mighty Claude 后，可以在一台电脑上打开另一台的工作区，并在那台电脑上运行 Claude、Codex、Antigravity 和命令。文件和 CLI 登录都留在实际运行的电脑上，不会复制或同步项目文件。

## 准备 {#prepare}
- 在两台电脑上安装 Mighty Claude 和 Tailscale，并登录同一个 tailnet。
- 在负责运行的电脑（主机）上安装并登录要用的 CLI。远程运行使用主机的 CLI 和账号。
- 不需要路由器端口转发，也不需要 Tailscale Serve 或 Funnel。只需确认 tailnet 策略允许两台设备互相连接。

## 共享这台电脑 {#share}
在主机电脑上：
1. 打开 {{ui:menu.settings}} → {{ui:settings.remote.navTitle}} 标签。
2. 在 {{ui:settings.remote.shareTitle}} 中选择要共享的本地工作区。端口一般保持不变（43137）。
3. 点按 {{ui:settings.remote.startSharing}}。会显示这台电脑的 Tailscale 地址和连接密钥。用 {{ui:settings.remote.copyButton}} 复制后告诉要连接的一方。

共享期间会显示 {{ui:settings.remote.allowFullAccess}} 开关。关闭时（默认），已连接的电脑不能使用远程命令窗格，也不能以完全权限运行代理；这类请求会在运行前被拒绝，窗格中会显示原因。每次开始共享时它都会重新关闭。

{{platform:windows}} 由 Windows 电脑共享时，只要这个开关关闭，已连接的电脑也不能使用 Codex 和 Antigravity 窗格（无论权限模式如何），因为 Windows 无法像 macOS 那样把 Codex 和 Antigravity 限制在共享文件夹内。Claude 窗格仍会运行，并照常请求权限。

共享只在这台电脑的 Tailscale 地址上接受连接。每次打开应用时共享都是关闭的。点按 {{ui:settings.remote.stopSharing}} 会停止远程运行并作废连接密钥；再次共享时需要告知新的密钥。

> [!warning]
> 持有连接密钥的设备可以在共享的工作区中运行任务和命令。根据权限模式，它们可能以主机用户的权限访问文件夹以外的内容。请只把密钥告诉你信任的设备。

## 连接其他电脑 {#connect}
在要下达任务的电脑上：
1. 打开 {{ui:menu.settings}} → {{ui:settings.remote.navTitle}} 标签。侧边栏标题中的网络按钮，以及 {{ui:workspace.addPane}} 菜单中的 {{ui:remote.menu.connect}} 也会打开同一处。
2. 在 {{ui:settings.remote.connectTitle}} 中输入主机的地址（Tailscale IP 或 MagicDNS 名称）和连接密钥，然后点按 {{ui:settings.remote.connectButton}}。如果主机没有更改端口，只输入地址即可。
3. 已连接电脑下方会列出共享的工作区。点按 {{ui:settings.remote.openButton}} 即可把它作为远程工作区加入侧边栏。

远程工作区在侧边栏中以电脑图标和主机名称区分，工作区标题和底部状态栏会显示连接状态。重新打开应用后，连接仍保持已保存状态，在远程窗格中第一次发送或点按 {{ui:settings.remote.refreshButton}} 时会重新连接。

{{platform:mac}} 连接密钥只保存在 macOS 钥匙串中，不会写入工作区记录。重新连接时如果 macOS 询问钥匙串访问权限，请选择“始终允许”；应用以相同的签名发布，因此这个选择在更新后仍然有效。

{{platform:windows}} 在 Windows 上，连接密钥同样不会写入工作区记录，而是加密保存在应用的数据文件夹中，只有当前 Windows 用户可以读取。共享时，{{ui:remote.windows.firewall.add}} 按钮会添加一条防火墙规则，只向 Tailscale 地址段打开应用的端口；Windows 会弹出一次管理员确认。如果规则已存在，该按钮会显示为 {{ui:remote.windows.firewall.update}}，并且这条规则与手机直连共用，所以在 {{ui:settings.nav.mobile}} 标签页中改的是同一条规则。 如果规则没有打开当前使用的全部端口，标签页会提示需要更新。删除规则会同时关闭手机直连端口和共享端口。

## 使用远程窗格 {#use}
- 代理窗格的用法与本地窗格相同。模型、权限模式、思考强度和 Fast 模式会原样传给主机；主机 CLI 不支持的设置会在运行前提示。
- 权限、提问和计划卡片会出现在本应用中，你的回答会传给主机上的运行。也可以向正在运行的 Claude 请求追加内容（{{platform:mac}} {{kbd:⌘Enter}}，{{platform:windows}} {{kbd:Ctrl+Enter}}）。
- {{ui:graph.view.default}} 视图和 {{ui:graph.view.mighty}} 视图（{{ui:graph.view.diagram}}、{{ui:graph.view.timeline}}、{{ui:graph.view.structure}}）的显示与本地运行完全相同。
- {{ui:remote.menu.newCommand}} 窗格的每条命令都会在主机的共享文件夹中打开新的 shell。不支持交互式程序和密码输入。
- 远程窗格会在状态栏和会话信息中显示运行电脑的系统（macOS 或 Windows）。{{ui:remote.menu.newCommand}} 窗格在 Mac 主机上用登录 shell、在 Windows 主机上用 PowerShell 运行命令。
- 附加的文件会以内容本身发送：一次最多 8 个，每个 5 MiB，合计 8 MiB。

远程工作区中不能使用以下功能，因为它们读取的是这台电脑的磁盘或网络：
- [Mighty 编排](delegation.md)（子工作树）
- 文件窗格和浏览器窗格
- 读取项目文件的[风格](styles.md)、状态行，以及继续以前的会话

远程工作区不会显示在[手机远程](phone.md)中。

## 连接断开时 {#disconnect}
- 网络断开时，正在运行的远程窗格会以错误结束，连接列表会显示原因。主机会在约 30 秒内自行停止失去联系的运行。
- {{ui:settings.remote.disconnectButton}} 会停止该连接的运行并保留连接信息。要再次使用，请点按 {{ui:settings.remote.refreshButton}} 或在远程窗格中发送。
- 如果主机重新开始共享，就需要新的密钥。用新密钥连接同一个地址，会替换已保存连接的密钥。
- 主机电脑休眠或其 Mighty Claude 退出时，无法发送新的任务。
