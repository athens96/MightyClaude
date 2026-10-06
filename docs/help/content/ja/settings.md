---
title: 設定
order: 7
section: settings
---
{{ui:menu.settings}}({{kbd:⌘,}})か、サイドバー下部の設定ボタンで開きます。左側でタブを選び、{{ui:settings.closeButton}}ボタンか{{kbd:Esc}}で閉じます。最後に表示したタブが記憶されます。

## 一般 {#general}
{{ui:settings.nav.general}}タブの{{ui:settings.display.sectionTitle}}で、アプリの見た目を変更します。
- {{ui:settings.display.themeLabel}}: {{ui:settings.display.themeDarkMac}}または{{ui:settings.display.themeLightMac}}。サイドバー下部のボタンでも切り替えられます。
- {{ui:settings.display.languageLabel}}: アプリの言語。[言語を変更する](getting-started.md#language)を参照してください。
- {{ui:settings.display.statusLineToggle}}: Claudeの設定にあるステータスラインのコマンドを実行し、入力欄の下に表示します。
- {{ui:settings.display.browserToggle}}: ブラウザペインを有効にします。アプリを開き直すと適用されます。
- {{ui:agentTerminal.urlOpen.settingTitle}}: エージェントが開いたリンクをどこで開くかを決めます。

![[settings-general]]

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
- {{ui:settings.components.sectionTitle}}: Claude、Codex、Gemini CLIと、必要なプラグインの状態です。{{ui:settings.components.statusInstalled}}、{{ui:settings.components.statusMissing}}、{{ui:settings.components.statusAttention}}のいずれかで表示されます。インストールが必要な場合は、インストールコマンドをコピーしてターミナルで実行してから、{{ui:settings.components.recheckButton}}をクリックします。
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
