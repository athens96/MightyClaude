---
title: よくある質問とトラブルシューティング
order: 10
section: faq
---
## リクエストを送信できない {#cannot-send}
- 入力欄の下に、送信できない理由が表示されます。CLIがインストールされていない場合やサインインが必要な場合は、{{ui:menu.settings}} → {{ui:settings.nav.tools}}と{{ui:settings.nav.cli}}を確認してください。
- ターミナルペインにはファイルを添付できません。AIのペインを使ってください。
- 実行中に押した{{kbd:Enter}}は、すぐには送信されずキューに入ります。[実行中に続けてリクエストする](agent-pane.md#queue)を参照してください。

## モデルの一覧に使いたいモデルがない {#model-missing}
1. モデルのチップを開き、{{ui:composer.model.refresh}}をクリックします。
2. それでも表示されない場合は、{{ui:settings.nav.cli}}タブで{{ui:settings.cliAccounts.resetModelsButton}}ボタンをクリックします。
3. 独自に使うモデル名は、{{ui:settings.nav.models}}タブの{{ui:settings.phaseModels.registeredTitle}}に追加できます。

## サインインが何度も切れる {#login}
{{ui:settings.nav.cli}}タブの{{ui:settings.cliAccounts.autoLoginToggle}}をオンにしてください。切れた時点で再サインインを始め、失敗したリクエストを再送信します。[サインインが切れたとき](approvals.md#login-recovery)を参照してください。

## ハングルの入力が字母に分解される {#korean-input}
メニューバーの{{ui:menu.workspace}}メニューで{{ui:menu.reconnectInputMethod}}を選んでください。それでも続く場合は、{{ui:menu.saveInputDiagnostics}}で診断ファイルを保存してお知らせください。

> [!warning]
> アプリの実行中に、アプリのファイルを上書きしたり、アプリを2つ起動したりしないでください。macOSの入力メソッドとの接続が切れ、ハングルが字母に分解されることがあります。アップデートには、アプリ内の{{ui:settings.appUpdate.installButton}}を使ってください。

## ブラウザペインが空になっている {#browser-empty}
ブラウザペインは最初はオフになっています。{{ui:settings.nav.general}}で{{ui:settings.display.browserToggle}}をオンにして、アプリを開き直してください。[ブラウザペイン](terminal-files-browser.md#browser)を参照してください。

## スマートフォンが接続できない {#phone}
- Macの{{ui:settings.nav.mobile}}で、{{ui:settings.mobileRemote.allowToggle}}がオンになっているかを確認します。
- 状態が「リレーに接続中」のまま止まっている場合は、{{ui:settings.mobileRemote.reconnectButton}}ボタンをクリックします。
- スマートフォンの{{ui:phone.tabs.hosts}}タブに{{ui:phone.hosts.reachability.unauthorized}}と表示される場合は、Macのキーが変更されています。QRコードで接続し直してください。
- Macがスリープ中の場合は接続できません。

## ペインを閉じると会話は消えますか？ {#history}
いいえ。会話の履歴はCLIのセッションとして残ります。同じフォルダでClaudeやCodexのペインを新しく追加するときに{{ui:resume.choice.resume}}ボタンを選ぶと、続きから再開できます。[以前のセッションを続ける](layout.md#resume)を参照してください。

## データはどこに保存されますか？ {#data}
アプリの設定と履歴は、このコンピューターにのみ保存されます。保存場所は、{{ui:settings.nav.about}}タブの{{ui:settings.appInfo.stateLocationLabel}}で確認できます。AIへのリクエストは、各CLIがそれぞれの会社のサービスに送信します。モバイルリモートを使う場合、内容は暗号化されたままリレーを通過します。
