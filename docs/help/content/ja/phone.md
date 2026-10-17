---
title: モバイルリモート
order: 9
section: phone
---
スマートフォンアプリを使うと、外出先からでもMacのMighty Claudeを確認して操作できます。作業の状態を確認し、リクエストを送信し、許可や質問に答えます。

Macとスマートフォンはリレーサーバー経由で接続されます。内容はエンドツーエンドで暗号化されるため、リレーは内容を見ることができません。ルーターのポートを開けたり、VPNを使ったりする必要はありません。Tailscale を使っていれば、[リレーを通さず直接](#direct)接続することもできます。

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

## Tailscale で直接接続する {#direct}
Mac とスマートフォンが同じ [Tailscale](https://tailscale.com) ネットワークにあれば、スマートフォンはリレーを通さず Mac に直接接続します。ペアリングも暗号化もそのままで、Mac に直接届かないときは自動でリレーを使います。

1. Mac とスマートフォンで Tailscale をオンにし、同じアカウントでログインします。
2. Mac の {{ui:settings.nav.mobile}} タブで {{ui:mobileRemote.direct.toggle}} がオンになっていることを確かめます(初期設定でオンです)。その下の行に Tailscale のアドレスが出ていれば準備完了です。{{ui:mobileRemote.direct.status.notFound}} と出ていれば、Mac の Tailscale がオフです。
3. すでにペアリングしたスマートフォンはペアリングし直す必要はありません。次に接続したときに Mac がアドレスを伝えます。

スマートフォンの {{ui:phone.tabs.hosts}} タブで、Mac のカードに {{ui:phone.direct.route.direct}} または {{ui:phone.direct.route.relay}} と表示されます。

## スマートフォンでできること {#use}
スマートフォンアプリには4つのタブがあります。

| タブ | 内容 |
|---|---|
| {{ui:phone.tabs.dashboard}} | {{ui:phone.dashboard.title}}: 実行中、対応待ち、完了の件数とセッションのカード |
| {{ui:phone.tabs.sessions}} | すべてのワークスペースとセッション。セッションを開いて会話を確認し、リクエストを送信します。 |
| {{ui:phone.tabs.alerts}} | 回答が必要な許可・質問と、終わったタスク |
| {{ui:phone.tabs.hosts}} | 接続したMacと接続状態 |

セッション内では、リクエストの送信と停止、モデル・権限・思考レベルの変更、スラッシュコマンド、ファイルの添付、ワークスペースのファイルの閲覧(読み取り専用)ができます。

{{ui:phone.session.view.blocks}}表示では、終わったリクエストはリクエストと{{ui:phone.blocks.finalResult}}がすぐに表示されます。途中の経過は1行にまとめられ、タップすると開きます。実行中のリクエストはすべてのステップを表示します。

{{ui:phone.session.view.structure}}表示は、デスクトップアプリが送るパイプラインをボードとして1つずつ表示します。スタイルのないペインではリクエスト1つが、スタイルを使うペインでは段階が後戻りせずに続いたリクエストのまとまり（パイプライン）1つがボード1つです。上部の ◀ ▶ でボードを移動し、中央の位置（「リクエスト 3 / 7」など）をタップすると一覧から選べます。古いボードを見ている間に新しいボードが届くと{{ui:graph.structure.board.newRequest}}または{{ui:graph.structure.board.newPipeline}}バッジが表示され、タップすると最新のボードに移ります。その下にボードの状態と段階のチップが1行に並びます。終わった段階は緑、今の段階はアクセントカラー、残りの段階はグレーで、チップごとにカード数が付きます。段階ごとに列があり、横にスワイプするかチップをタップして移動します。最初はボードの今の段階の列が開きます。スタイルのないペインの列は{{ui:graph.structure.column.request}} → {{ui:graph.structure.column.execution}} → {{ui:graph.structure.column.result}}で、{{ui:graph.structure.column.execution}}カードの後にサブエージェントのカードが続きます。スタイルを使うペインでは、各列にその段階のリクエストとサブエージェントのカードが並び、終わったリクエストは1枚に折りたたまれ、タップすると開きます。実行中のカードは枠が点滅します。この表示には、スマートフォンアプリと同じかそれより新しいバージョンのデスクトップアプリ（Mac または Windows）が必要です。古い場合は、デスクトップアプリの更新を求める案内が表示されます。

Macで[Mightyオーケストレーション](delegation.md)により作られた子ペインは、セッション一覧で親ペインのすぐ下に1段字下げして表示され、右側に{{ui:delegation.child.state.reported}}、{{ui:delegation.child.state.merged}}などの状態が付きます。子のマージや破棄はMacでのみ行います。Antigravityを使うデスクトップアプリ（Mac 0.4.23以降、Windows 0.4.28以降）のGeminiペインはAntigravity（{{ui:badge.beta}}）として、それより古いアプリのペインはGeminiとして表示されます。

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
