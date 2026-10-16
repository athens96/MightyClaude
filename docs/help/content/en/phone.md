---
title: Phone remote
order: 9
section: phone
---
With the phone app you can follow and drive Mighty Claude on your Mac while you're away: check on tasks, send requests, and answer approvals and questions.

The Mac and the phone connect through a relay server. Everything is encrypted end to end, so the relay can't read it. You don't need to open router ports or use a VPN.

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

A child pane made on the Mac by [Mighty Orchestration](delegation.md) is listed one step in under its parent pane, with its state ({{ui:delegation.child.state.reported}}, {{ui:delegation.child.state.merged}} and so on) on the right. Merging or discarding a child is done on the Mac only. Gemini panes of a desktop app that runs Antigravity (Mac 0.4.23 or later, Windows 0.4.28 or later) are shown as Antigravity ({{ui:badge.beta}}); those of an older app are shown as Gemini.

## Approvals and questions {#approvals}
When an agent asks for approval or asks a question, the {{ui:phone.tabs.alerts}} tab shows a count.
- Permission requests: tap {{ui:permission.allow}} or {{ui:permission.deny}}.
- Questions: pick your answers and tap {{ui:phone.questionnaire.submit}}.
- Plan cards: answer with the same buttons as on the Mac ({{ui:plan.card.approveAuto}}, {{ui:plan.card.revise}} and so on).

The phone doesn't get push notifications. Open the app to check.

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
