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

Windows PC でも同じです。ただし接続が遮断されないよう、Windows ファイアウォールの規則が 1 つ必要です。{{ui:settings.nav.mobile}} タブで {{ui:mobileRemote.direct.windows.firewall.addButton}} を押し、Windows の管理者の確認を許可してください。この規則は Tailscale のアドレスからこのアプリへの接続だけを許可します。規則があると同じ行のボタンは {{ui:mobileRemote.direct.windows.firewall.removeButton}} に変わり、規則を読み取れないときは管理者の確認なしで読み直す {{ui:setup.button.recheck}} になります。直接接続をオンにしたままポートを変えたら、規則を削除してからもう一度追加してください。直接接続またはモバイルリモートをオフにすると規則も一緒に削除され（このコンピュータがワークスペースを[共有](remote.md#share)している間は、そのポートに必要なので残ります）、このときも管理者の確認を求められます。これはスマートフォンの直接接続のポートとワークスペース共有のポートを一緒に開く 1 つの規則です。規則がある間は同じ行に {{ui:remote.windows.firewall.update}} ボタンも表示され、いま使っているポートに合わせて直します。{{ui:settings.remote.navTitle}} タブでも同じ規則を追加・更新・削除できます。

## スマートフォンでできること {#use}
スマートフォンアプリには4つのタブがあります。

| タブ | 内容 |
|---|---|
| {{ui:phone.tabs.dashboard}} | {{ui:phone.dashboard.title}}: 実行中、対応待ち、完了の件数とセッションのカード |
| {{ui:phone.tabs.sessions}} | すべてのワークスペースとセッション。セッションを開いて会話を確認し、リクエストを送信します。 |
| {{ui:phone.tabs.alerts}} | 回答が必要な許可・質問と、終わったタスク |
| {{ui:phone.tabs.hosts}} | 接続したMacと接続状態 |

セッション内では、リクエストの送信と停止、モデル・権限・思考レベルの変更、スラッシュコマンド、ファイルの添付、ワークスペースのファイルの閲覧(読み取り専用)ができます。

タスクが実行中のセッションでは、送信ボタンが{{ui:queue.add}}に変わり、送った内容はそのペインの次のリクエストとしてキューに入ります。実行中のタスクに割り込むことはありません。

{{ui:phone.session.view.blocks}}表示では、終わったリクエストはリクエストと{{ui:phone.blocks.finalResult}}がすぐに表示されます。途中の経過は1行にまとめられ、タップすると開きます。実行中のリクエストはすべてのステップを表示します。

{{ui:phone.session.view.structure}}表示は、デスクトップアプリが送るパイプラインをボードとして1つずつ表示します。スタイルのないペインではリクエスト1つが、スタイルを使うペインでは段階が後戻りせずに続いたリクエストのまとまり（パイプライン）1つがボード1つです。上部の ◀ ▶ でボードを移動し、中央の位置（「リクエスト 3 / 7」など）をタップすると一覧から選べます。古いボードを見ている間に新しいボードが届くと{{ui:graph.structure.board.newRequest}}または{{ui:graph.structure.board.newPipeline}}バッジが表示され、タップすると最新のボードに移ります。その下にボードの状態と段階のチップが1行に並びます。終わった段階は緑、今の段階はアクセントカラー、残りの段階はグレーで、チップごとにカード数が付きます。段階ごとに列があり、横にスワイプするかチップをタップして移動します。最初はボードの今の段階の列が開きます。スタイルのないペインの列は{{ui:graph.structure.column.request}} → {{ui:graph.structure.column.execution}} → {{ui:graph.structure.column.result}}で、{{ui:graph.structure.column.execution}}カードの後にサブエージェントのカードが続きます。スタイルを使うペインでは、各列にその段階のリクエストとサブエージェントのカードが並び、終わったリクエストは1枚に折りたたまれ、タップすると開きます。実行中のカードは枠が点滅します。この表示には、スマートフォンアプリと同じかそれより新しいバージョンのデスクトップアプリ（Mac または Windows）が必要です。古い場合は、デスクトップアプリの更新を求める案内が表示されます。

Macで[Mightyオーケストレーション](delegation.md)により作られた子ペインは、セッション一覧で親ペインのすぐ下に1段字下げして表示され、右側に{{ui:delegation.child.state.reported}}、{{ui:delegation.child.state.merged}}などの状態が付きます。Windows PCはまだ子ペインの親と状態をスマートフォンに送らないため、Windows PCの子ペインは通常のペインと同じように一覧に表示されます。子のマージや破棄はスマートフォンからは行えず、デスクトップアプリ（MacまたはWindows）で行います。Antigravityを使うデスクトップアプリ（Mac 0.4.23以降、Windows 0.4.28以降）のGeminiペインはAntigravity（{{ui:badge.beta}}）として、それより古いアプリのペインはGeminiとして表示されます。

## 許可と質問 {#approvals}
エージェントが許可を求めたり質問したりすると、{{ui:phone.tabs.alerts}}タブに件数が表示されます。
- 権限のリクエスト: {{ui:permission.allow}}または{{ui:permission.deny}}をタップします。
- Antigravityの権限カード: 拒否された操作ごとに種類とコマンド・アドレス・パスが表示されます。{{ui:permission.allowOnce}}、{{ui:antigravity.permission.allowAlways}}、{{ui:antigravity.permission.deny}}から選びます。許可すると同じ会話が続きます([Antigravityの権限カード](approvals.md#antigravity-permission))。ルールで許可できない操作だけのカードでは{{ui:antigravity.permission.deny}}だけが表示され、権限モードを変えるよう案内が出ます。コマンドをつなぐ、パイプやリダイレクトを使う、またはデータを消す可能性があるコマンドがあると、{{ui:antigravity.permission.allowAlways}}は表示されません。ホストが答えを受け付けられないときは、理由とともにカードが残ります。このカードを知らない古いスマートフォンアプリでは、通常の権限リクエストとして表示されます。
- 質問: 答えを選んで{{ui:phone.questionnaire.submit}}をタップします。
- 計画カード: Macと同じボタン({{ui:plan.card.approveAuto}}、{{ui:plan.card.revise}}など)で答えます。

## 通知 {#notifications}
接続したコンピューター（Mac または Windows）で実行が終わったとき、失敗したとき、そして許可の依頼・質問・計画の承認・ログイン切れで対応が必要なときに、スマートフォンへ通知が届きます。今は Android アプリだけが受け取ります。
- 初めてアプリを開いたとき（Android 13 以降）、通知を許可するか一度だけ聞きます。種類ごとにオフにするには、{{ui:phone.tabs.hosts}} タブの {{ui:phone.settings.title}} を開き、{{ui:phone.settings.notifications}} のスイッチを変えます。システム設定でこのアプリの通知がオフのときは、{{ui:phone.settings.title}} でお知らせします。
- 許可の依頼の通知では、その場で {{ui:permission.allow}} か {{ui:permission.deny}} をタップできます。Antigravity のカードは {{ui:permission.allowOnce}}・{{ui:antigravity.permission.allowAlways}}・{{ui:antigravity.permission.deny}} です。コマンドがつながっている、データを消せるなど、一度だけ許可できるカードには {{ui:antigravity.permission.allowAlways}} はありません。質問と計画の通知にはボタンがなく、タップしてアプリで答えます。許可できる操作がひとつもない Antigravity のカードもボタンなしで届くので、タップしてアプリで答えてください。
- ボタンはスマートフォンのロックを解除してからでないと動きません。ロック中に押すと通知が「{{ui:phone.push.outcome.locked}}」に変わり、ボタンは残ります。コンピューターで先に答えていた場合は {{ui:phone.push.outcome.already}} に、答えを送れなかった場合は {{ui:phone.push.outcome.failed}} に変わります。最後の場合は通知をタップして、アプリで答えてください。
- 通知をタップすると、そのセッションが開きます。スマートフォンでそのセッションを表示している間は、そのセッションの通知は届きません。
- 通知はロック画面では非公開で表示されます。ロック画面で機密性の高い内容を隠す設定にしていると、タイトルと本文は表示されません。
- 通知の内容は、このスマートフォンだけが読めるようにコンピューターが暗号化します。リレーと Google は読めません。通知はリレーを通るので、Tailscale で直接接続していても、コンピューターがインターネットにつながっていれば届きます。
- コンピューターのアプリとスマートフォンのアプリの両方が、この機能のあるバージョンである必要があります。この機能のないコンピューターのアプリは通知を送りません。とても古いスマートフォンアプリでペアリングした場合は、もう一度ペアリングしてください。
- スマートフォンからの接続（モバイルリモート）をオフにしている間、コンピューターは何も送りません。オンに戻したときも、その間にあったことは知らせません。

アプリが止まったときは、止まった場所だけを開発者に送ります。会話・コマンド・パスは送りません。{{ui:phone.settings.title}} で {{ui:phone.settings.crashReports}} をオフにできます。

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
