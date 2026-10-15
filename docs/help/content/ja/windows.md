---
title: Windowsでの違い
order: 10
section: windows
---
このヘルプはMacの画面を基準に書かれています。Windowsアプリも同じ機能を同じ名前で提供しており、ここでは異なる部分だけをまとめています。

> [!note]
> Windowsアプリは{{ui:badge.beta}}版です。ウインドウのタイトルにベータの表示が付きます。

## インストール {#install}
1. [ダウンロードページ](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/index.html)から、お使いのPCに合ったzipをダウンロードします。ほとんどのPCはx64、SnapdragonなどのARM搭載PCはARM64です。
2. zipをフォルダごと展開します。
3. フォルダ内の`MightyClaude.exe`を実行します。一緒に含まれているファイルも必要なので、exeだけをほかの場所に移動しないでください。

Windows 10(バージョン2004)以降またはWindows 11で動作します。必要な.NETランタイムはzipに含まれています。

> [!warning]
> このアプリにはコード署名がないため、初回の実行時にWindowsにブロックされることがあります。Visual C++ランタイムが見つからないと表示された場合は、Microsoft Visual C++ 再頒布可能パッケージ(v14)をインストールしてください。ターミナルペインとブラウザペインには、Microsoft Edge WebView2ランタイムが必要です。

アップデートは、Macと同じく{{ui:menu.settings}} → {{ui:settings.nav.about}}で行います。

## ショートカット {#shortcuts}
{{kbd:⌘}}の代わりに{{kbd:Ctrl}}を使います。サイドバーの折りたたみとリモート画面の停止だけはキーが異なります。

| Mac | Windows | 動作 |
|---|---|---|
| {{kbd:⌘O}} | {{kbd:Ctrl+O}} | {{ui:menu.openProject}} |
| {{kbd:⌘N}} | {{kbd:Ctrl+N}} | {{ui:menu.newClaudePane}} |
| {{kbd:⌘T}} | {{kbd:Ctrl+T}} | {{ui:menu.addTerminalPane}} |
| {{kbd:⇧⌘E}} | {{kbd:Ctrl+Shift+E}} | {{ui:menu.showFiles}} |
| {{kbd:⌘K}} | {{kbd:Ctrl+K}} | {{ui:menu.searchWorkspaces}} |
| {{kbd:⌃⌘S}} | {{kbd:Ctrl+B}} | {{ui:sidebar.collapse}} / {{ui:sidebar.expand}} |
| {{kbd:⌘,}} | {{kbd:Ctrl+,}} | {{ui:menu.settings}} |
| {{kbd:⌘Enter}} | {{kbd:Ctrl+Enter}} | 実行中のClaudeにすぐに伝える |
| {{kbd:⌃⌥⌘K}} | {{kbd:Ctrl+Alt+Esc}} | スマートフォンからのリモート画面セッションをすべて止める([リモート画面](phone.md#screen)) |

Windowsにはメニューバーがありません。同じ機能は、サイドバーと{{ui:workspace.addPane}}メニューにあります。

## 異なる点 {#differences}
| 機能 | Windowsでは |
|---|---|
| CLIのサインイン | アプリ内のターミナルではなく、別のWindowsターミナルのウインドウで行います。サインインが終わると、Macと同じく失敗したリクエストを自動で送り直します。 |
| Googleのエージェント | 現在もGemini CLI(`gemini`)を使います。MacはAntigravity CLIを使います([MacのAntigravity](agent-pane.md#antigravity))。 |
| ターミナルペイン | Windows PowerShellを使います。 |
| ブラウザペイン | Microsoft Edge WebView2で開きます。Macと同じく、{{ui:settings.display.browserToggle}}をオンにしてアプリを開き直す必要があります。 |
| 完了通知 | {{ui:windows.notifications.toggleLabel}}でオンにします。 |
| リモート画面 | モバイルリモートのリモート画面は使えます。画質のオプションはMacより少なくなります。{{kbd:Ctrl+Alt+Esc}}を押すと、すべてのリモート画面セッションがすぐに止まります。 |
| ペットの吹き出し | 承認と質問の吹き出しに記号のボタンが1つ多くあります。承認ではリクエスト全体を見るウインドウを、質問では回答を自分で書いて送るウインドウを開きます。 |
| 言語 | 変更した言語は、アプリを開き直すと適用されます。 |
| ファイルの場所 | {{ui:menu.showInFinder}}の代わりに{{ui:menu.showInExplorer}}があります。 |
| Mightyオーケストレーション | レジストリの値でオンとオフを切り替えます([オンとオフ](delegation.md#enable))。{{kbd:⌘Enter}}で子ペインを自分で起動する機能はなく、Claudeに分けて任せる使い方だけができます。worktreeは`%USERPROFILE%\.mightyclaude\worktrees`にでき、中にジャンクションやシンボリックリンクがあると自動では片付けません([ペインを閉じる・後片付け](delegation.md#close))。 |

> [!note]
> Windowsアプリの一部の機能は、まだ実機で十分に確認されていません。気になる点があればお知らせください。
