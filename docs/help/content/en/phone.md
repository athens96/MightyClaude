---
title: Phone remote
order: 9
section: phone
---
With the phone app you can follow and drive Mighty Claude on your Mac while you're away: check on tasks, send requests, and answer approvals and questions.

The Mac and the phone connect through a relay server. Everything is encrypted end to end, so the relay can't read it. You don't need to open router ports or use a VPN. If you use Tailscale, the phone can also [connect directly](#direct) without the relay.

## Get the phone app {#get-app}
Download the Android app (APK) from the [download page](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/index.html) and install it.

## Pair your phone {#pair}
On the Mac:
1. Open {{ui:menu.settings}} → {{ui:settings.nav.mobile}}.
2. Leave the {{ui:settings.mobileRemote.relayLabel}} address empty to use the default relay. If you run your own relay, enter its address and click {{ui:settings.mobileRemote.applyButton}}.
3. Turn on {{ui:settings.mobileRemote.allowToggle}}. A QR code appears.

On the phone:
1. Open the app and tap {{ui:phone.hosts.addHost}} on the {{ui:phone.tabs.hosts}} tab. The first time, a connection guide shows first.
2. Scan the Mac's QR code with {{ui:phone.pair.mode.qr}}. If you can't use the camera, click {{ui:settings.mobileRemote.copyLinkButton}} on the Mac, send yourself the link and paste it in {{ui:phone.pair.mode.manual}}.
3. Tap {{ui:phone.pair.connect}}.

![[settings-mobile]]

Paired phones are listed under {{ui:settings.mobileRemote.connectedDevicesTitle}} on the Mac. When you stop using a phone, click {{ui:settings.mobileRemote.revokeRowButton}}.

> [!warning]
> {{ui:settings.mobileRemote.regenerateKeyButton}} means every paired phone has to pair again. Use it if someone else saw the QR code or the key.

## Connect directly over Tailscale {#direct}
When the Mac and the phone are on the same [Tailscale](https://tailscale.com) network, the phone connects straight to the Mac without the relay. Pairing and encryption stay the same, and when the Mac can't be reached directly the phone uses the relay on its own.

1. Turn on Tailscale on the Mac and the phone and sign in with the same account.
2. In the {{ui:settings.nav.mobile}} tab on the Mac, check that {{ui:mobileRemote.direct.toggle}} is on (it is by default). When the line below it shows Tailscale addresses, you're set. If it says {{ui:mobileRemote.direct.status.notFound}}, Tailscale is off on the Mac.
3. Phones you already paired don't need pairing again. The Mac tells them its addresses the next time they connect.

On the phone, the Mac's card in the {{ui:phone.tabs.hosts}} tab shows {{ui:phone.direct.route.direct}} or {{ui:phone.direct.route.relay}}.

It works the same with a Windows PC, which needs one Windows Firewall rule so the connection isn't blocked. In the {{ui:settings.nav.mobile}} tab, click {{ui:mobileRemote.direct.windows.firewall.addButton}} and allow Windows' administrator prompt. The rule lets only connections from Tailscale addresses reach this app. Once the rule exists, the button on the same row becomes {{ui:mobileRemote.direct.windows.firewall.removeButton}}; when the rule can't be read, it becomes {{ui:setup.button.recheck}}, which reads again without an administrator prompt. If you change the port while the direct connection is on, remove the rule and add it again. Turning the direct connection or the mobile remote off removes the rule too (it stays while this computer is [sharing workspaces](remote.md#share), which still need its port), and Windows asks for administrator approval again. It is one rule that opens both the phone's direct port and the workspace-sharing port. While it exists, the same row also offers {{ui:remote.windows.firewall.update}}, which fits it to the ports in use now, and the {{ui:settings.remote.navTitle}} tab adds, updates or removes the same rule.

## What you can do on the phone {#use}
The phone app has four tabs.

| Tab | What it shows |
|---|---|
| {{ui:phone.tabs.dashboard}} | {{ui:phone.dashboard.title}}: counts of running, waiting and finished work, and session cards |
| {{ui:phone.tabs.sessions}} | Every workspace and session. Open one to read the conversation and send requests. |
| {{ui:phone.tabs.alerts}} | Approvals and questions waiting for you, and finished work |
| {{ui:phone.tabs.hosts}} | Your paired Macs and whether they are connected |

Inside a session you can send and stop requests, change the model, permission and thinking effort, run slash commands, attach files and browse the workspace's files (read-only).

In the {{ui:phone.session.view.blocks}} view a finished request shows the request and its {{ui:phone.blocks.finalResult}} at once. Its steps are folded into one row that opens when you tap it. A request still running shows every step.

The {{ui:phone.session.view.structure}} view shows the pipeline the desktop app sends as boards, one at a time. In a pane without a style each request is a board; in a pane with a style each pipeline is a board: a run of requests whose step never goes back. ◀ and ▶ at the top move between boards, and tapping the place in the middle ("Request 3 / 7") opens the list to pick one. When a new board arrives while you read an older one, a {{ui:graph.structure.board.newRequest}} or {{ui:graph.structure.board.newPipeline}} badge appears; tap it to go to the newest board. Below them are the board's status and its steps as a row of chips: finished steps are green, the current step is in the accent colour and the steps still to come are grey, each with its card count. Each step has its own column; swipe sideways or tap a chip to move between them, and the board opens on its current step. A pane without a style has the columns {{ui:graph.structure.column.request}} → {{ui:graph.structure.column.execution}} → {{ui:graph.structure.column.result}}, with the sub-agents' cards after the {{ui:graph.structure.column.execution}} card. In a pane with a style each column holds its step's requests and their sub-agents, and a finished request folds to one card that opens when you tap it. A running card's border pulses. This view needs the desktop app (Mac or Windows) on the same version as the phone app or newer; with an older one the view asks you to update the desktop app.

A child pane made on the Mac by [Mighty Orchestration](delegation.md) is listed one step in under its parent pane, with its state ({{ui:delegation.child.state.reported}}, {{ui:delegation.child.state.merged}} and so on) on the right. A Windows PC doesn't send a child's parent and state to the phone yet, so a child pane on a Windows PC is listed like any other pane. Merging or discarding a child can't be done from the phone; it is done in the desktop app (Mac or Windows). Gemini panes of a desktop app that runs Antigravity (Mac 0.4.23 or later, Windows 0.4.28 or later) are shown as Antigravity ({{ui:badge.beta}}); those of an older app are shown as Gemini.

## Approvals and questions {#approvals}
When an agent asks for approval or asks a question, the {{ui:phone.tabs.alerts}} tab shows a count.
- Permission requests: tap {{ui:permission.allow}} or {{ui:permission.deny}}.
- Antigravity permission card: each declined action shows its kind and its command, address or path. Choose {{ui:permission.allowOnce}}, {{ui:antigravity.permission.allowAlways}} or {{ui:antigravity.permission.deny}}; allowing continues the same conversation ([Antigravity's permission card](approvals.md#antigravity-permission)). A card with only actions no rule can allow offers just {{ui:antigravity.permission.deny}} and tells you to change the permission mode. When a command chains, pipes, redirects or may delete data, {{ui:antigravity.permission.allowAlways}} is not shown. If the host cannot take your answer, the card stays with the reason. An older phone app that does not know the card shows it as an ordinary permission request.
- Questions: pick your answers and tap {{ui:phone.questionnaire.submit}}.
- Plan cards: answer with the same buttons as on the Mac ({{ui:plan.card.approveAuto}}, {{ui:plan.card.revise}} and so on).

## Notifications {#notifications}
Your phone gets a notification when a run on a connected computer (Mac or Windows) finishes or fails, and when an agent needs you: a permission request, a question, a plan to approve or an expired sign-in. Only the Android app gets them for now.
- The first time you open the app (Android 13 or later) it asks once whether to allow notifications. To turn kinds off, open {{ui:phone.settings.title}} on the {{ui:phone.tabs.hosts}} tab and change the {{ui:phone.settings.notifications}} switches. If notifications for the app are off in the system settings, {{ui:phone.settings.title}} tells you so.
- On a permission request you can tap {{ui:permission.allow}} or {{ui:permission.deny}} right in the notification. An Antigravity card offers {{ui:permission.allowOnce}}, {{ui:antigravity.permission.allowAlways}} and {{ui:antigravity.permission.deny}}. A card that can only be allowed once, such as a chained command or one that can delete data, has no {{ui:antigravity.permission.allowAlways}}. Question and plan notifications have no buttons; tap them and answer in the app. An Antigravity card with nothing that can be allowed also comes without buttons; tap it and answer in the app.
- The buttons work only after you unlock the phone. If you tap one while it is locked, the notification changes to "{{ui:phone.push.outcome.locked}}" and keeps its buttons. If you already answered on the computer it changes to {{ui:phone.push.outcome.already}}, and if the answer could not be sent it changes to {{ui:phone.push.outcome.failed}}. In that last case, tap the notification and answer in the app.
- Tapping a notification opens that session. You get no notifications about a session while it is on your phone's screen.
- Notifications are private on the lock screen: if you chose to hide sensitive content there, the title and text are hidden.
- The computer encrypts each notification so only this phone can read it; the relay and Google can't. Notifications go through the relay, so over a direct Tailscale connection they still arrive, as long as the computer can reach the internet.
- Both the desktop app and the phone app need a version with this feature. A desktop app without it sends no notifications. If you paired with a very old phone app, pair again.
- While phone remote access is off the computer sends nothing, and when you turn it back on it does not report what happened in the meantime.

When the app crashes it sends the developers only where it stopped, never conversations, commands or paths. You can turn {{ui:phone.settings.crashReports}} off in {{ui:phone.settings.title}}.

## Screen view {#screen}
You can watch your Mac's screen from the phone and, if you allow it, control it. This is a {{ui:badge.beta}} feature, in the Android app only for now.
1. On the Mac, under {{ui:settings.screenShare.sectionTitle}} on the {{ui:settings.nav.mobile}} tab, set {{ui:settings.screenShare.grantLabel}} for each phone to {{ui:settings.screenShare.grantView}} or {{ui:settings.screenShare.grantControl}}. A new phone starts at {{ui:settings.screenShare.grantNone}}.
2. Use {{ui:settings.screenShare.permissions.openButton}} to grant the Mac permissions it needs, such as Screen Recording and Accessibility.
3. On the phone, tap {{ui:phone.screenShare.open}} and choose {{ui:phone.screenShare.mode.view}} or {{ui:phone.screenShare.mode.control}}.

The first time you allow control, check that the fingerprint matches on both screens. Each time control starts, the phone asks for your biometrics or PIN.

> [!tip]
> Press {{kbd:⌃⌥⌘K}} on the Mac to stop every remote screen session at once. Control ends after 10 minutes without input, viewing after 30.

## Run your own relay {#own-relay}
Instead of the default relay you can run your own. Deploy the `relay/` folder from the repository to a server, then enter its address (`wss://…`) under {{ui:settings.mobileRemote.relayLabel}} on the Mac.
