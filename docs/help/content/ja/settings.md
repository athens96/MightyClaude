---
title: 設定
order: 8
section: settings
---
{{ui:menu.settings}}({{kbd:⌘,}})か、サイドバー下部の設定ボタンで開きます。左側でタブを選び、{{ui:settings.closeButton}}ボタンか{{kbd:Esc}}で閉じます。最後に表示したタブが記憶されます。

## 一般 {#general}
{{ui:settings.nav.general}}タブの{{ui:settings.display.sectionTitle}}で、アプリの見た目を変更します。
- {{ui:settings.display.themeLabel}}: {{ui:settings.display.themeDarkMac}}または{{ui:settings.display.themeLightMac}}。サイドバー下部のボタンでも切り替えられます。
- {{ui:settings.display.languageLabel}}: アプリの言語。[言語を変更する](getting-started.md#language)を参照してください。
- {{ui:settings.display.statusLineToggle}}: Claudeの設定にあるステータスラインのコマンドを実行し、入力欄の下に表示します。
- {{ui:settings.display.browserToggle}}: ブラウザペインを有効にします。アプリを開き直すと適用されます。
- {{ui:settings.delegation.toggle}}: 初期状態でオンです。Claudeペインが専用のworktreeで子ペインを開けるようになり、実行中のClaudeペインで{{kbd:⌘Enter}}を押すとリクエストが新しい子ペインで実行されます。[Mightyオーケストレーション](delegation.md#enable)を参照してください。
- {{ui:agentTerminal.urlOpen.settingTitle}}: エージェントが開いたリンクをどこで開くかを決めます。

![[settings-general]]

### 文字サイズとフォント {#text}
{{platform:mac}} 同じタブの {{ui:settings.text.sectionTitle}} で、アプリ全体の文字を変更します。
- {{ui:settings.text.sizeLabel}}：85% から 150% の間で選びます。会話、入力欄、サイドバー、設定、ターミナルが一緒に大きく・小さくなります。
- {{ui:settings.text.uiFontLabel}}：サイドバー、メニュー、ボタン、設定など、アプリ画面のフォントです。この Mac にインストールされているフォントから選び、{{ui:settings.text.fontSystem}} は macOS の標準フォントです。
- {{ui:settings.text.transcriptFontLabel}}：エージェントの返答、自分が送ったメッセージ、入力欄、Mighty のカードとボードの本文のフォントです。{{ui:settings.text.sameAsInterface}} にすると画面のフォントに合わせます。
- {{ui:settings.text.codeFontLabel}}：コードブロック、インラインコード、ツールの出力とコマンド行、ターミナルペインのフォントです。等幅フォントだけが表示され、いちばん上の標準ではこれまでどおりコードに SF Mono、ターミナルに Menlo を使います。選んだフォントが削除されると、ターミナルは Menlo に戻ります。開いているターミナルにもすぐに反映されます。パス、バージョン、件数などアプリ画面の等幅の文字は、システムの等幅フォントのままです。
- それぞれのフォントの下に、そのフォントで書いた 1 行のプレビューが表示されます。
- {{ui:settings.text.reset}} ボタンで、サイズと 3 つのフォントを最初の状態に戻します。

表示メニューの {{ui:menu.textBigger}}（{{kbd:⌘=}}）、{{ui:menu.textSmaller}}（{{kbd:⌘-}}）、{{ui:menu.textActualSize}}（{{kbd:⌘0}}）でもサイズを変更できます。変更はすぐに反映され、アプリを開き直してもそのままです。

## モデル {#models}
{{ui:settings.nav.models}}タブの{{ui:settings.phaseModels.sectionTitle}}で、作業の段階ごとに使うモデルをこのMac全体で設定します。段階は{{ui:settings.phaseModels.phase.planning}}、{{ui:settings.phaseModels.phase.execution}}、{{ui:settings.phaseModels.phase.review}}、{{ui:settings.phaseModels.phase.subagents}}です。
- {{ui:settings.phaseModels.defaultOption}}のままにすると、CLIが決めます。
- {{ui:settings.phaseModels.paneModel}}を選ぶと、ペインのモデルチップで選んだモデルを使います。
- 一覧にないモデル名は、{{ui:settings.phaseModels.registeredTitle}}で{{ui:settings.phaseModels.addButton}}します。そのモデルが思考レベルに対応している場合は、{{ui:settings.phaseModels.supportsEffortLabel}}をオンにします。

![[settings-models]]

## スタイル {#styles}
{{ui:settings.nav.styles}}タブで{{ui:settings.styles.sectionTitle}}の一覧を管理します。ファイルからスタイルを登録し、内容を確認して許可したり、許可を取り消したりします。詳しくは[スタイル](styles.md#custom)にあります。

![[settings-styles]]

## コンポーネント {#components}
{{ui:settings.nav.tools}}タブでは、アプリに必要なものが準備できているかを確認できます。
- {{ui:settings.components.sectionTitle}}: Macでは[{{ui:setup.title}}](getting-started.md#setup)の一覧がここにあります。エージェントCLI、前提ツール、スタイル用ツール、Tailscale、gh・glabごとに{{ui:setup.status.installed}}、{{ui:setup.status.missing}}、{{ui:setup.status.outdated}}、{{ui:setup.status.attention}}、{{ui:setup.status.installing}}、{{ui:setup.status.failed}}のいずれかが表示され、足りない項目を選んで{{ui:setup.button.install}}をクリックすると、すべてのコマンドを確認したうえでアプリが別に開くターミナルウィンドウでインストールします。{{ui:setup.button.recheck}}と、一覧を開いている間の自動確認は毎回CLIを探し直し、古すぎるClaude Codeには{{ui:setup.button.update}}ボタンが出ます。Windowsでは、Claude、Codex、Antigravity CLIと必要なプラグインの状態が{{ui:settings.components.statusInstalled}}、{{ui:settings.components.statusMissing}}、{{ui:settings.components.statusAttention}}のいずれかで表示されます。インストールコマンドをコピーしてターミナルで実行してから、{{ui:settings.components.recheckButton}}をクリックします。
- {{ui:settings.toolkit.sectionTitle}}: よく使うツールの一覧です。インストールの前に実行するコマンドをすべて表示し、{{ui:settings.toolkit.confirmTitle}}を求めます。一覧を{{ui:settings.toolkit.exportButton}}したり{{ui:settings.toolkit.importButton}}したりできます。

![[settings-tools]]

## CLI {#cli}
{{ui:settings.nav.cli}}タブで、CLIの状態、サインイン、アップデートを管理します。

### サインイン {#cli-accounts}
{{ui:settings.cliAccounts.sectionTitle}}で、CLIごとにサインインします。
1. {{ui:settings.cliAccounts.buttonLogin}}をクリックします。Claudeでは、{{ui:settings.cliAccounts.buttonLoginClaude}}と{{ui:settings.cliAccounts.buttonLoginConsole}}から選びます。
2. アプリ内にターミナルペインが開き、CLIのサインインが始まります。ブラウザで承認します。
3. サインインが完了すると状態が変わります。変更したアカウントは次のリクエストから使われます。

アカウントを変更するには{{ui:settings.cliAccounts.buttonChange}}、サインアウトするには{{ui:settings.cliAccounts.buttonLogout}}をクリックします。サインインするには、ワークスペースが1つ以上開いている必要があります。{{ui:settings.cliAccounts.autoLoginToggle}}をオンにすると、サインインが切れたときに自動で再サインインし、失敗したリクエストを再送信します。

### CLIのアップデート {#cli-update}
{{ui:settings.cliUpdate.sectionTitle}}で、インストール済みのCLIを最新版にします。
- {{ui:settings.cliUpdate.updateButton}}: 今すぐアップデートします。
- {{ui:settings.cliUpdate.autoUpdateToggle}}: アプリの起動時と、6時間ごとに確認します。
- {{ui:settings.cliUpdate.autoUpdatePluginsToggle}}: ClaudeとCodexのプラグインも一緒にアップデートします。

CLIを最初にインストールした方法(npm、Homebrewなど)をそのまま使います。

![[settings-cli]]

## モバイルリモート {#mobile}
{{ui:settings.nav.mobile}}タブで、スマートフォンを接続し、リモート画面を許可します。[モバイルリモート](phone.md)を参照してください。

## リモート接続 {#remote}
{{ui:settings.remote.navTitle}}タブで、この Mac のワークスペースを Tailscale で共有したり、別の Mac が共有しているワークスペースに接続したりします。[リモート接続（Tailscale）](remote.md)を参照してください。

## ペットと通知 {#companion}
{{ui:settings.nav.companion}}タブで、デスクトップペットと完了通知を設定します。
- {{ui:companion.settings.enabled}}: 画面上を動き回るアライグマのペットを表示します。ペットはドラッグして移動でき、クリックすると吹き出しが開閉します。
- {{ui:companion.settings.task}}: 吹き出しに、リクエスト、現在の作業、経過時間を表示します。吹き出しから許可({{ui:permission.allowOnce}} / {{ui:permission.deny}})に答えることもできます。
- {{ui:companion.settings.pet}}: ペットを選びます。{{ui:companion.settings.import}}でほかのペットを追加できます。
- {{ui:companion.settings.motion}}: 動きを減らします。
- {{ui:companion.settings.macNotifications}}: リクエストが正常に終わると、Macの通知を送ります。通知をクリックすると、そのペインが開きます。通知にはリクエストや結果の内容は含まれません。

![[settings-companion]]

![[companion-pet]]

## アプリ情報 {#about}
{{ui:settings.nav.about}}タブでは、アプリをアップデートし、バージョンとデータの保存場所を確認します。
- {{ui:settings.appUpdate.sectionTitle}}: [アップデート](getting-started.md#update)を参照してください。
- {{ui:settings.appInfo.stateLocationLabel}}: アプリの設定と履歴があるフォルダです。{{ui:settings.appInfo.openFinderButton}}で開きます。

![[settings-about]]
