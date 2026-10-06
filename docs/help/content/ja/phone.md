---
title: モバイルリモート
order: 8
section: phone
---
スマートフォンアプリを使うと、外出先からでもMacのMighty Claudeを確認して操作できます。作業の状態を確認し、リクエストを送信し、許可や質問に答えます。

Macとスマートフォンはリレーサーバー経由で接続されます。内容はエンドツーエンドで暗号化されるため、リレーは内容を見ることができません。ルーターのポートを開けたり、VPNを使ったりする必要はありません。

## スマートフォンアプリを入手する {#get-app}
[ダウンロードページ](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/index.html)からAndroidアプリ(APK)をダウンロードしてインストールします。

## 接続する {#pair}
Macで:
1. {{ui:menu.settings}} → {{ui:settings.nav.mobile}}タブを開きます。
2. {{ui:settings.mobileRemote.relayLabel}}のアドレスを空欄のままにすると、標準のリレーを使います。自分で運用しているリレーがある場合は、アドレスを入力して{{ui:settings.mobileRemote.applyButton}}をクリックします。
3. {{ui:settings.mobileRemote.allowToggle}}をオンにします。QRコードが表示されます。

スマートフォンで:
1. アプリを開き、{{ui:phone.tabs.hosts}}タブの{{ui:phone.hosts.addHost}}をタップします。初めて開いたときは、先に接続の案内が表示されます。
2. {{ui:phone.pair.mode.qr}}でMacのQRコードを読み取ります。カメラを使えない場合は、Macで{{ui:settings.mobileRemote.copyLinkButton}}をクリックしてリンクを送り、{{ui:phone.pair.mode.manual}}に入力します。
3. {{ui:phone.pair.connect}}をタップすると接続されます。

![[settings-mobile]]

接続したスマートフォンは、Macの{{ui:settings.mobileRemote.connectedDevicesTitle}}に表示されます。スマートフォンを使わなくなったら、{{ui:settings.mobileRemote.revokeRowButton}}をクリックします。

> [!warning]
> {{ui:settings.mobileRemote.regenerateKeyButton}}をクリックすると、接続済みのスマートフォンをすべて接続し直す必要があります。QRコードやキーが他人に見られたときに使ってください。

## スマートフォンでできること {#use}
スマートフォンアプリには4つのタブがあります。

| タブ | 内容 |
|---|---|
| {{ui:phone.tabs.dashboard}} | {{ui:phone.dashboard.title}}: 実行中、対応待ち、完了の件数とセッションのカード |
| {{ui:phone.tabs.sessions}} | すべてのワークスペースとセッション。セッションを開いて会話を確認し、リクエストを送信します。 |
| {{ui:phone.tabs.alerts}} | 回答が必要な許可・質問と、終わったタスク |
| {{ui:phone.tabs.hosts}} | 接続したMacと接続状態 |

セッション内では、リクエストの送信と停止、モデル・権限・思考レベルの変更、スラッシュコマンド、ファイルの添付、ワークスペースのファイルの閲覧(読み取り専用)ができます。

## 許可と質問 {#approvals}
エージェントが許可を求めたり質問したりすると、{{ui:phone.tabs.alerts}}タブに件数が表示されます。
- 権限のリクエスト: {{ui:permission.allow}}または{{ui:permission.deny}}をタップします。
- 質問: 答えを選んで{{ui:phone.questionnaire.submit}}をタップします。
- 計画カード: Macと同じボタン({{ui:plan.card.approveAuto}}、{{ui:plan.card.revise}}など)で答えます。

スマートフォンへのプッシュ通知は送信されません。アプリを開いて確認してください。

## リモート画面 {#screen}
スマートフォンからMacの画面を表示し、許可すれば操作することもできます。{{ui:badge.beta}}の機能で、現在はAndroidアプリでのみ利用できます。
1. Macの{{ui:settings.nav.mobile}}タブの下にある{{ui:settings.screenShare.sectionTitle}}で、スマートフォンごとに{{ui:settings.screenShare.grantLabel}}を{{ui:settings.screenShare.grantView}}または{{ui:settings.screenShare.grantControl}}に設定します。新しいスマートフォンは{{ui:settings.screenShare.grantNone}}から始まります。
2. {{ui:settings.screenShare.permissions.openButton}}で、画面収録やアクセシビリティなどのMacの権限を許可します。
3. スマートフォンで{{ui:phone.screenShare.open}}をタップし、{{ui:phone.screenShare.mode.view}}または{{ui:phone.screenShare.mode.control}}を選びます。

初めて操作を許可するときは、両方の画面に表示される指紋が一致しているかを確認します。操作を始めるたびに、スマートフォンで生体認証かPINを求められます。

> [!tip]
> Macで{{kbd:⌃⌥⌘K}}を押すと、すべてのリモート画面のセッションがすぐに止まります。操作は10分間、表示は30分間何も入力がないと自動的に終了します。

## リレーを自分で運用する {#own-relay}
標準のリレーの代わりに、自分でリレーを運用できます。リポジトリの`relay/`フォルダをサーバーにデプロイし、そのアドレス(`wss://…`)をMacの{{ui:settings.mobileRemote.relayLabel}}に入力してください。
