---
title: Windowsでの違い
order: 11
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
| {{kbd:⌘=}} / {{kbd:⌘-}} / {{kbd:⌘0}} | {{kbd:Ctrl+=}} / {{kbd:Ctrl+-}} / {{kbd:Ctrl+0}} | {{ui:menu.textBigger}} / {{ui:menu.textSmaller}} / {{ui:menu.textActualSize}} |
| {{kbd:⌘Enter}} | {{kbd:Ctrl+Enter}} | 新しいworktreeですぐに実行、または実行中のClaudeにすぐに伝える |
| {{kbd:⌃⌥⌘K}} | {{kbd:Ctrl+Alt+Esc}} | スマートフォンからのリモート画面セッションをすべて止める([リモート画面](phone.md#screen)) |

Windowsにはメニューバーがありません。同じ機能は、サイドバーと{{ui:workspace.addPane}}メニューにあります。

## 異なる点 {#differences}
| 機能 | Windowsでは |
|---|---|
| 必要なツール | [{{ui:setup.title}}](getting-started.md#setup)のインストールは別のPowerShellウィンドウで行い、wingetと各CLIの公式PowerShellインストールスクリプトを使います。Homebrew、Bun、gstackは一覧にありません。{{ui:setup.button.neverShow}}はレジストリ `HKEY_CURRENT_USER\Software\MightyClaudeNative` の `setup.neverShow` 値(REG_DWORD)として保存されます。 |
| CLIのサインイン | アプリ内のターミナルではなく、別のWindowsターミナルのウインドウで行います。サインインが終わると、Macと同じく失敗したリクエストを自動で送り直します。 |
| Antigravity | PowerShellのインストーラーで`%LOCALAPPDATA%\agy\bin`に入り、記録は`%USERPROFILE%\.gemini\antigravity-cli`に残ります([Antigravity](agent-pane.md#antigravity))。 |
| ターミナルペイン | Windows PowerShellを使います。 |
| ブラウザペイン | Microsoft Edge WebView2で開きます。Macと同じく、{{ui:settings.display.browserToggle}}をオンにしてアプリを開き直す必要があります。 |
| 完了通知 | {{ui:windows.notifications.toggleLabel}}でオンにします。 |
| リモート画面 | モバイルリモートのリモート画面は使えます。画質のオプションはMacより少なくなります。{{kbd:Ctrl+Alt+Esc}}を押すと、すべてのリモート画面セッションがすぐに止まります。 |
| ペットの吹き出し | 承認と質問の吹き出しに記号のボタンが1つ多くあります。承認ではリクエスト全体を見るウインドウを、質問では回答を自分で書いて送るウインドウを開きます。 |
| 言語 | 変更した言語は、アプリを開き直すと適用されます。 |
| ファイルの場所 | {{ui:menu.showInFinder}}の代わりに{{ui:menu.showInExplorer}}があります。 |
| Mightyオーケストレーション | 初期状態でオンで、設定のスイッチでオフにできます。このスイッチはレジストリの値でもあります([オンとオフ](delegation.md#enable))。worktreeは`%USERPROFILE%\.mightyclaude\worktrees`にでき、中にジャンクションやシンボリックリンクがあると自動では片付けません([ペインを閉じる・後片付け](delegation.md#close))。{{ui:queue.worktree}}ボタンを右クリックすると、別のエージェントの子でリクエストを実行できます([新しいworktreeで実行](delegation.md#worktree))。 |
| 文字サイズとフォント | Macと同じく{{ui:settings.nav.general}} › {{ui:settings.text.sectionTitle}}でサイズ（85〜150%）、{{ui:settings.text.uiFontLabel}}、{{ui:settings.text.transcriptFontLabel}}、{{ui:settings.text.codeFontLabel}}を選ぶと、開いているウィンドウがすべてすぐに変わります。{{kbd:Ctrl+=}} / {{kbd:Ctrl+-}} / {{kbd:Ctrl+0}}も使えますが、ターミナルやブラウザーのペインにフォーカスがあるときは、キーはそのペインで使われます。設定はレジストリの `HKEY_CURRENT_USER\Software\MightyClaudeNative` に保存されます。確認ウィンドウの本文のようにWindows標準のコントロールが決める文字はアプリを開き直すと変わり、ペットの吹き出しはまだこの設定に従いません。 |
| Tailscale で直接接続 | スマートフォンは Windows PC にも[直接接続](phone.md#direct)します。Windows ファイアウォールの規則は {{ui:settings.nav.mobile}} タブの {{ui:mobileRemote.direct.windows.firewall.addButton}} で追加し、Windows の管理者の確認を 1 回経ます。アプリが管理者権限を求めるのはここだけです。 |

> [!warning]
> Tailscale で直接接続は初期状態でオンのため、アプリが初めてスマートフォンからの接続を待ち受けるとき、Windows Defender ファイアウォールがこのアプリの機能の一部をブロックしたという独自の通知を表示することがあります。**アクセスを許可する**を選んでください（Windows がこのアプリ用に、より広い独自の規則を追加します）。**キャンセル**を選んだ場合は、Windows が `MightyClaude.exe` をブロックする規則を追加していることがあり、その規則はアプリの規則より優先されます。Windows Defender ファイアウォール → 詳細設定 → 受信の規則で、Mighty Claude をブロックする規則を削除してください。

> [!note]
> Windowsアプリの一部の機能は、まだ実機で十分に確認されていません。気になる点があればお知らせください。
