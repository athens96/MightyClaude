---
title: はじめに
order: 1
section: getting-started
---
Mighty Claudeは、Claude Code、Codex CLI、GoogleのAntigravity CLIを1つのウインドウで並べて使えるデスクトップアプリです。1つのプロジェクトフォルダで複数のペインを開き、ペインごとに会話と設定を別々に保ちます。

## 必要なもの {#requirements}
- macOS 14以降。WindowsではWindows 10(バージョン2004)以降またはWindows 11です。
- 使用するAI CLI。アプリはCLIを直接実行するため、インストールとサインインが必要です。Macでは、アプリを初めて開いたときに表示される{{ui:setup.title}}画面からインストールできます([初回起動](#first-launch))。使わないCLIはインストールしなくてもかまいません。

- Claude Code: Mac `curl -fsSL https://claude.ai/install.sh | bash`, Windows `npm install -g @anthropic-ai/claude-code`
- Codex CLI: Mac `curl -fsSL https://chatgpt.com/codex/install.sh | sh`, Windows `npm install -g @openai/codex`
- Antigravity CLI: Mac `curl -fsSL https://antigravity.google/cli/install.sh | bash`, Windows [Antigravity](agent-pane.md#antigravity)

> [!tip]
> アプリを開いたあと、{{ui:settings.nav.tools}}でどのCLIが準備できているかを確認できます。Macではその場でインストールでき、Windowsではインストールコマンドをコピーします。サインインは{{ui:settings.nav.cli}}タブで行います。[設定](settings.md#components)を参照してください。

## インストール {#install}
1. [ダウンロードページ](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/index.html)から、お使いのOS用のファイルをダウンロードします。
2. macOS: `MightyClaude-macos.zip`を展開し、`MightyClaude.app`をアプリケーションフォルダに移動してから開きます。
3. Windows: `MightyClaude-windows-x64-setup.exe`(ARM搭載PCは`arm64`)を実行してインストールし、スタートメニューから開きます。詳しくは[Windowsでの違い](windows.md#install)にあります。

> [!warning]
> このアプリはAppleの公証を受けていません。初めて開くときにmacOSにブロックされた場合は、Finderでアプリをcontrolキーを押しながらクリックして**開く**を選ぶか、「システム設定」→「プライバシーとセキュリティ」で開くことを許可してください。

## アップデート {#update}
アプリは1日1回、起動時に新しいバージョンがあるかどうかを確認します。新しいバージョンがあると、ウインドウ下部のステータスバーに通知が表示されます。その通知をクリックすると設定が開きます。

1. {{ui:menu.settings}}({{kbd:⌘,}})を開き、{{ui:settings.nav.about}}タブに移動します。
2. {{ui:settings.appUpdate.sectionTitle}}で{{ui:settings.appUpdate.checkButton}}をクリックします。
3. 新しいバージョンがあれば、{{ui:settings.appUpdate.downloadButton}}ボタンをクリックします。
4. 準備ができたら{{ui:settings.appUpdate.installButton}}をクリックします。アプリが終了して新しいバージョンに置き換わり、再び開きます。

自動確認をオフにするには、{{ui:settings.appUpdate.autoCheckToggle}}をオフにしてください。アップデートファイルは署名を確認してからインストールされます。

> [!note]
> AI CLIのアップデートはアプリのアップデートとは別に行います。{{ui:settings.nav.cli}}タブの{{ui:settings.cliUpdate.sectionTitle}}を使ってください。

## 初回起動 {#first-launch}
初めて開いたときはまだワークスペースがないため、スタート画面が表示されます。

![[welcome]]

### 必要なツール {#setup}
動かせるエージェントCLI(Claude Code、Codex、Antigravityのいずれか)が1つもないか、Gitの準備ができていない場合は、アプリの起動時に{{ui:setup.title}}画面が先に開きます。エージェントCLI、前提ツール(Git、Homebrew、Node、Bun)、スタイル用ツール(uv、Ouroboros、oh-my-claudecode、gstack)、Tailscale、gh・glabをグループごとに表示します。

1. インストールする項目にチェックを入れます。最初に開いたときは、足りないGitと、エージェントCLIが1つもなければClaude Codeが最初からチェックされていて、選んだ項目に必要なツール(例: ghに必要なHomebrew)も一緒に入ります。
2. {{ui:setup.button.install}}をクリックすると、実行するコマンドがすべて順番に表示されます。{{ui:setup.confirm.run}}をクリックするまで何も実行されません。
3. コマンドは、アプリが別に開く1つのターミナルウィンドウで、ホームフォルダーから順に実行されます。このウィンドウはペインではなく、ワークスペースには保存されません。パスワードや確認を求められたら、ターミナルで直接答えてください。アプリはsudoを使わず、パスワードも受け取りません。ある手順が失敗しても次の手順に進みます。
4. 終わるとアプリが再確認します。アプリが前面にあればこの画面をもう一度開き、そうでなければステータスバーに{{ui:setup.finishedBadge}}ボタンを表示します。結果はコマンドの終了コードではなく、再確認した状態で決まります。CLIが新しく入ったときは{{ui:setup.button.signIn}}ボタンが表示されます。

Git(Xcode Command Line Tools)はシステムのインストール画面が開くので、そちらで完了します。スクリプトは最大30分待ち、Control-Cを押すと待つのだけをやめて次の手順に進みます。Xcodeのライセンスに同意していないためにgitが動かない場合は、{{ui:setup.status.attention}}と表示されます。Tailscaleは公式のダウンロードページを開きます。古すぎるClaude Codeは{{ui:setup.status.outdated}}と表示され、{{ui:setup.button.update}}ボタンが出ます。

{{ui:setup.button.later}}で閉じられます。{{ui:setup.button.neverShow}}にチェックしてから閉じると、起動時に開かなくなります。必須ツールが足りない間はスタート画面に{{ui:setup.title}}ボタンがあり、CLIがなくて止まっているペインには{{ui:setup.blocked.install}}ボタンがあります。同じ一覧は設定 → {{ui:settings.nav.tools}}にもあります。

{{platform:windows}} Windowsにも同じ画面があります。コマンドはホームフォルダーを作業フォルダーとして、アプリが別に開くPowerShellウィンドウ1つ(Windows Terminalがあればそのタブ、なければコンソールウィンドウ)で順に実行され、最後にEnterを押すまでウィンドウは開いたままです。Claude Code、Codex、Antigravity、uvはそれぞれの公式PowerShellインストールスクリプトで、Gitとglabはwingetでこのユーザーだけに、Node.jsとghはwingetですべてのユーザーにインストールします。すべてのユーザー向けのインストールではWindows自身が管理者の確認画面を表示することがありますが、この画面自体は管理者権限を求めません。インストールが終わると、アプリはPATHをレジストリから読み直して確認します。Homebrew、Bun、gstackはMacでのみインストールします。

## ワークスペースを開く {#open-workspace}
ワークスペースは1つのプロジェクトフォルダです。ペイン、タブの配置、会話の履歴はワークスペースごとに保存されます。

1. {{ui:layout.welcome.openProject}}ボタンをクリックするか、{{kbd:⌘O}}を押します。
2. 作業するフォルダを選びます。
3. サイドバーにワークスペースが追加され、ペインがまだないフォルダならClaudeのペインがすぐに1つ開きます。ペインをさらに開くには{{ui:menu.newClaudePane}}({{kbd:⌘N}})を使います。

ほかのフォルダを開くときは、サイドバーの{{ui:sidebar.workspacesHeader}}見出しの右にあるフォルダボタンを押すか、メニューバーの{{ui:menu.openProject}}か{{kbd:⌘O}}を使います。サイドバーでワークスペースを右クリックすると、{{ui:menu.rename}}、{{ui:menu.showInFinder}}、{{ui:workspace.menu.remove}}の項目があります。一覧から削除しても、ディスク上のプロジェクトファイルはそのまま残ります。

## 言語を変更する {#language}
1. {{ui:menu.settings}} → {{ui:settings.nav.general}}を開きます。
2. {{ui:settings.display.languageLabel}}で言語を選びます。{{ui:settings.display.languageSystem}}を選ぶとMacの言語に従います。
3. メニューバーはアプリを開き直すまで変わりません。ほかの画面がすぐに変わらない場合も、アプリを開き直すとすべて切り替わります。

## ヘルプを開く {#help}
このヘルプはアプリから直接開けます。アプリで選んだ言語で表示されます。

- Mac：メニューバーの{{ui:menu.help}}（{{kbd:⌘?}}）、または{{ui:menu.settings}} → {{ui:settings.nav.about}}の{{ui:settings.appInfo.openHelpButton}}ボタン
- Windows：サイドバー下部のヘルプボタン、{{kbd:F1}}、または{{ui:menu.settings}} → {{ui:settings.nav.about}}の{{ui:settings.appInfo.openHelpButton}}ボタン
- スマートフォン：{{ui:phone.tabs.hosts}}タブの{{ui:phone.hosts.help}}ボタン
