---
title: Remote connection (Tailscale)
order: 10
section: remote
---
Link two computers' Mighty Claude (Mac or Windows) over [Tailscale](https://tailscale.com/kb/1017/install), and one computer can open the other's workspaces and run Claude, Codex, Antigravity and commands on that computer. Files and CLI sign-ins stay where the work runs; no project files are copied or synced.

## Before you start {#prepare}
- Install Mighty Claude and Tailscale on both computers and sign both into the same tailnet.
- On the computer that will do the work (the host), install and sign in to the CLIs you will use. Remote runs use the host's CLIs and accounts.
- No router port forwarding and no Tailscale Serve or Funnel are needed. Just make sure your tailnet policy lets the two devices reach each other.

## Share this computer {#share}
On the host computer:
1. Open {{ui:menu.settings}} → the {{ui:settings.remote.navTitle}} tab.
2. Under {{ui:settings.remote.shareTitle}}, pick the local workspaces to share. Leave the port as it is (43137) unless you need another.
3. Press {{ui:settings.remote.startSharing}}. This computer's Tailscale addresses and the connection key appear. Copy them with {{ui:settings.remote.copyButton}} and give them to the other computer.

While sharing, the {{ui:settings.remote.allowFullAccess}} switch appears. While it is off (the default), connected computers cannot use remote command panes or run agents with full access; such requests are refused before they run, and the pane says why. It turns off again whenever sharing starts.

{{platform:windows}} When a Windows PC shares, connected computers also cannot use Codex or Antigravity panes while this switch is off, whatever their permission mode, because Windows cannot confine Codex and Antigravity to the shared folder the way macOS does. Claude panes still run and ask for permission as usual.

Sharing accepts connections on this computer's Tailscale addresses only. It is always off when the app starts. {{ui:settings.remote.stopSharing}} stops the remote runs and revokes the key; sharing again gives a new key to pass on.

> [!warning]
> Any device with the key can run agents and commands in the shared workspaces. Depending on the permission mode, they may reach outside the folder with the host user's rights. Give the key only to devices you trust.

## Connect to another computer {#connect}
On the computer that gives the work:
1. Open {{ui:menu.settings}} → the {{ui:settings.remote.navTitle}} tab. The network button in the sidebar header and {{ui:remote.menu.connect}} in the {{ui:workspace.addPane}} menu open the same place.
2. Under {{ui:settings.remote.connectTitle}}, enter the host's address (its Tailscale IP or MagicDNS name) and the key, then press {{ui:settings.remote.connectButton}}. Unless the host changed the port, the address alone is enough.
3. The shared workspaces appear under the connected computer. Press {{ui:settings.remote.openButton}} to add one to the sidebar as a remote workspace.

A remote workspace shows a computer icon and its host's name in the sidebar, and its connection state in the workspace header and the status bar. After the app restarts, the connection stays saved and connects again on the first send from a remote pane or when you press {{ui:settings.remote.refreshButton}}.

{{platform:mac}} The key is kept in the macOS Keychain only, never in the workspace state. If macOS asks for Keychain access when it connects again, choose "Always Allow"; the app ships with the same signature, so the choice survives updates.

{{platform:windows}} On Windows the key is never kept in the workspace state either: it is saved encrypted so only this Windows user can read it, in the app's data folder. When sharing, the {{ui:remote.windows.firewall.add}} button adds one firewall rule that opens the app's ports only to Tailscale's ranges; Windows shows its administrator prompt once. If the rule already exists, the button reads {{ui:remote.windows.firewall.update}} instead, and the rule is shared with the phone's direct connection, so the {{ui:settings.nav.mobile}} tab changes the same rule. If the rule does not open every port in use now, the tab says it needs updating. Removing the rule closes both the phone's direct-connection port and the sharing port.

## Using remote panes {#use}
- Use agent panes as you would local ones. Model, permission mode, effort and Fast mode travel to the host; a setting the host's CLI does not support is reported before the run.
- Permission, question and plan cards appear in this app, and your answers reach the run on the host. A request you send while it runs waits in the queue, as in a local pane, and starts when the run ends.
- The {{ui:graph.view.default}} view and the {{ui:graph.view.mighty}} views ({{ui:graph.view.diagram}}, {{ui:graph.view.timeline}}, {{ui:graph.view.structure}}) draw exactly as for a local run.
- A {{ui:remote.menu.newCommand}} pane runs each command in a new shell in the host's shared folder. Interactive programs and password prompts are not supported.
- A remote pane shows its host's OS (macOS or Windows) in the status bar and the session info. A {{ui:remote.menu.newCommand}} pane runs commands in the login shell on a Mac host and in PowerShell on a Windows host.
- Attached files are sent as their content: up to 8 at a time, 5 MiB each, 8 MiB in total.

These are not available in a remote workspace, because they read this computer's disk or network:
- [Mighty Orchestration](delegation.md) (child worktrees)
- The files pane and the browser pane
- [Styles](styles.md) that read project files, status lines, and resuming earlier sessions

Remote workspaces do not appear on the [phone](phone.md).

## When the connection drops {#disconnect}
- If the network drops, a running remote pane ends in an error and the connection list says why. The host stops a run it no longer hears from within about 30 seconds.
- {{ui:settings.remote.disconnectButton}} stops that connection's runs and keeps the connection. Press {{ui:settings.remote.refreshButton}} or send from a remote pane to use it again.
- If the host restarted sharing, you need its new key. Connecting to the same address with the new key replaces the saved connection's key.
- While the host computer sleeps or Mighty Claude is closed there, no new work can be sent.
