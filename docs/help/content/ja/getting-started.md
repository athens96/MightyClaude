---
title: はじめに
order: 1
section: getting-started
---
Mighty Claudeは、Claude Code、Codex CLI、Gemini CLIを1つのウインドウで並べて使えるデスクトップアプリです。1つのプロジェクトフォルダで複数のペインを開き、ペインごとに会話と設定を別々に保ちます。

## 必要なもの {#requirements}
- macOS 14以降。WindowsではWindows 10(バージョン2004)以降またはWindows 11です。
- 使用するAI CLI。アプリはCLIを直接実行するため、先にインストールしてサインインしておく必要があります。使わないCLIはインストールしなくてもかまいません。

| CLI | インストールコマンド |
|---|---|
| Claude Code | `npm install -g @anthropic-ai/claude-code` |
| Codex CLI | `npm install -g @openai/codex` |
| Gemini CLI | `npm install -g @google/gemini-cli` |

> [!tip]
> アプリを開いたあと、{{ui:settings.nav.tools}}でどのCLIが準備できているかを確認し、インストールコマンドをコピーできます。サインインは{{ui:settings.nav.cli}}タブで行います。[設定](settings.md#components)を参照してください。

## インストール {#install}
1. [ダウンロードページ](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/index.html)から、お使いのOS用のファイルをダウンロードします。
2. macOS: `MightyClaude-macos.zip`を展開し、`MightyClaude.app`をアプリケーションフォルダに移動してから開きます。
3. Windows: zipをフォルダごと展開し、その中の`MightyClaude.exe`を実行します。詳しくは[Windowsでの違い](windows.md#install)にあります。

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

## ワークスペースを開く {#open-workspace}
ワークスペースは1つのプロジェクトフォルダです。ペイン、タブの配置、会話の履歴はワークスペースごとに保存されます。

1. {{ui:layout.welcome.openProject}}ボタンをクリックするか、{{kbd:⌘O}}を押します。
2. 作業するフォルダを選びます。
3. サイドバーにワークスペースが追加されます。続けて{{ui:menu.newClaudePane}}({{kbd:⌘N}})で最初のペインを開きます。

ほかのフォルダを開くときも、メニューバーの{{ui:menu.openProject}}か{{kbd:⌘O}}を使います。サイドバーでワークスペースを右クリックすると、{{ui:menu.rename}}、{{ui:menu.showInFinder}}、{{ui:workspace.menu.remove}}の項目があります。一覧から削除しても、ディスク上のプロジェクトファイルはそのまま残ります。

## 言語を変更する {#language}
1. {{ui:menu.settings}} → {{ui:settings.nav.general}}を開きます。
2. {{ui:settings.display.languageLabel}}で言語を選びます。{{ui:settings.display.languageSystem}}を選ぶとMacの言語に従います。
3. メニューバーはアプリを開き直すまで変わりません。ほかの画面がすぐに変わらない場合も、アプリを開き直すとすべて切り替わります。

## ヘルプを開く {#help}
このヘルプはアプリから直接開けます。アプリで選んだ言語で表示されます。

- Mac：メニューバーの{{ui:menu.help}}（{{kbd:⌘?}}）、または{{ui:menu.settings}} → {{ui:settings.nav.about}}の{{ui:settings.appInfo.openHelpButton}}ボタン
- Windows：サイドバー下部のヘルプボタン、または{{kbd:F1}}
- スマートフォン：{{ui:phone.tabs.hosts}}タブの{{ui:phone.hosts.help}}ボタン
