---
title: Remote connection (Tailscale)
order: 10
section: remote
---
Link two Macs' Mighty Claude over [Tailscale](https://tailscale.com/kb/1017/install), and one Mac can open the other's workspaces and run Claude, Codex, Antigravity and commands on that computer. Files and CLI sign-ins stay where the work runs; no project files are copied or synced. {{platform:mac}} The Windows app does not have this yet.

## Before you start {#prepare}
- Install Mighty Claude and Tailscale on both Macs and sign both into the same tailnet.
- On the Mac that will do the work (the host), install and sign in to the CLIs you will use. Remote runs use the host's CLIs and accounts.
- No router port forwarding and no Tailscale Serve or Funnel are needed. Just make sure your tailnet policy lets the two devices reach each other.

## Share this computer {#share}
On the host Mac:
1. Open {{ui:menu.settings}} → the {{ui:settings.remote.navTitle}} tab.
2. Under {{ui:settings.remote.shareTitle}}, pick the local workspaces to share. Leave the port as it is (43137) unless you need another.
3. Press {{ui:settings.remote.startSharing}}. This Mac's Tailscale addresses and the connection key appear. Copy them with {{ui:settings.remote.copyButton}} and give them to the other Mac.

Sharing accepts connections on this Mac's Tailscale addresses only. It is always off when the app starts. {{ui:settings.remote.stopSharing}} stops the remote runs and revokes the key; sharing again gives a new key to pass on.

> [!warning]
> Any device with the key can run agents and commands in the shared workspaces. Depending on the permission mode, they may reach outside the folder with the host user's rights. Give the key only to devices you trust.

## Connect to another computer {#connect}
On the Mac that gives the work:
1. Open {{ui:menu.settings}} → the {{ui:settings.remote.navTitle}} tab. The network button in the sidebar header and {{ui:remote.menu.connect}} in the {{ui:workspace.addPane}} menu open the same place.
2. Under {{ui:settings.remote.connectTitle}}, enter the host's address (its Tailscale IP or MagicDNS name) and the key, then press {{ui:settings.remote.connectButton}}. Unless the host changed the port, the address alone is enough.
3. The shared workspaces appear under the connected computer. Press {{ui:settings.remote.openButton}} to add one to the sidebar as a remote workspace.

A remote workspace shows a computer icon and its host's name in the sidebar, and its connection state in the workspace header and the status bar. The key is kept in the macOS Keychain only, never in the workspace state. After the app restarts, the connection stays saved and connects again on the first send from a remote pane or when you press {{ui:settings.remote.refreshButton}}. If macOS then asks for Keychain access, choose "Always Allow"; the app ships with the same signature, so the choice survives updates.

## Using remote panes {#use}
- Use agent panes as you would local ones. Model, permission mode, effort and Fast mode travel to the host; a setting the host's CLI does not support is reported before the run.
- Permission, question and plan cards appear in this app, and your answers reach the run on the host. You can also add to a running Claude request with {{kbd:⌘Enter}}.
- The {{ui:graph.view.default}} view and the {{ui:graph.view.mighty}} views ({{ui:graph.view.diagram}}, {{ui:graph.view.timeline}}, {{ui:graph.view.structure}}) draw exactly as for a local run.
- A {{ui:remote.menu.newCommand}} pane runs each command in a new shell in the host's shared folder. Interactive programs and password prompts are not supported.
- Attached files are sent as their content: up to 8 at a time, 5 MiB each, 8 MiB in total.

These are not available in a remote workspace, because they read this Mac's disk or network:
- [Mighty Orchestration](delegation.md) (child worktrees)
- The files pane and the browser pane
- [Styles](styles.md) that read project files, status lines, and resuming earlier sessions

Remote workspaces do not appear on the [phone](phone.md).

## When the connection drops {#disconnect}
- If the network drops, a running remote pane ends in an error and the connection list says why. The host stops a run it no longer hears from within about 30 seconds.
- {{ui:settings.remote.disconnectButton}} stops that connection's runs and keeps the connection. Press {{ui:settings.remote.refreshButton}} or send from a remote pane to use it again.
- If the host restarted sharing, you need its new key. Connecting to the same address with the new key replaces the saved connection's key.
- While the host Mac sleeps or Mighty Claude is closed there, no new work can be sent.
