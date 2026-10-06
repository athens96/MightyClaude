---
title: ターミナル・ファイル・ブラウザのペイン
order: 6
section: terminal-files-browser
---
エージェントペインの横に、ターミナル、ファイル、ブラウザのペインを一緒に開いておけます。

## ターミナルペイン {#terminal}
ワークスペースのフォルダで開く本物のターミナルです。
1. {{ui:menu.addTerminalPane}}({{kbd:⌘T}})を選ぶか、{{ui:workspace.addPane}} → {{ui:workspace.newTerminal}}を選びます。
2. コマンドを直接入力します。

シェルが終了したら{{ui:terminal.restart}}をクリックします。以前のコマンド履歴は、ペインのメニューの{{ui:pane.menu.terminalHistory}}で確認できます。アプリを終了すると、ターミナルは再び開かれません。

![[terminal-pane]]

### エージェントのターミナル {#agent-terminal}
ClaudeやCodexがユーザーに見せる必要のあるコマンド(開発サーバー、サインインなど)を実行すると、エージェントペインの右側に専用のターミナルペインが作られます。実行中のコマンドに直接入力できます。ペインを閉じてもコマンドはアプリを終了するまで動き続け、ペインのヘッダーにある{{ui:agentTerminal.terminalPane.open}}ボタンで再び開けます。

## ファイルペイン {#files}
ワークスペースのファイルを閲覧する、読み取り専用のペインです。ファイルを編集することはありません。
1. {{ui:menu.showFiles}}({{kbd:⇧⌘E}})を選ぶか、ワークスペースのヘッダーにあるフォルダのボタンをクリックします。
2. 左の一覧からファイルを選びます。{{ui:files.tree.filter}}で名前を検索し、矢印キーと{{kbd:Return}}で移動します。
3. 右側で内容を確認します。

| ファイル | 表示のされ方 |
|---|---|
| ソース・テキスト | 行番号とシンタックスハイライト付き。大きなファイルは最初の1 MBだけが表示されます。 |
| Markdown | {{ui:files.markdown.rendered}}と{{ui:files.markdown.source}}の表示を切り替えます。 |
| 画像・PDF | {{ui:files.image.fit}}、{{ui:files.image.actualSize}}、{{ui:files.image.zoomIn}}、{{ui:files.image.zoomOut}}。PDFは最初のページだけが表示されます。 |
| その他 | {{ui:files.preview.unsupported}}。{{ui:menu.showInFinder}}で開きます。 |

ワークスペース外のファイルは表示されません。ファイルが変更された場合は{{ui:files.tree.refresh}}をクリックします。

![[files-pane]]

## ブラウザペイン {#browser}
アプリ内でWebページを開くペインです。試験運用中の機能のため、最初はオフになっています。
1. {{ui:menu.settings}} → {{ui:settings.nav.general}}で{{ui:settings.display.browserToggle}}をオンにします。
2. アプリを開き直します。
3. {{ui:workspace.addPane}} → {{ui:browser.newTab}}を選びます。
4. アドレスを入力し、{{ui:browser.back}}、{{ui:browser.forward}}、{{ui:browser.reload}}を使います。

サインインの状態はワークスペースごとに別々に保存されます。

> [!note]
> ブラウザエンジンはAppleシリコン搭載のMacでのみ動作します。エンジンが含まれていないビルドでは、ペインに案内が表示されます。

### エージェントがリンクを開くとき {#agent-links}
エージェントがWebページを開こうとすると、ペイン内に{{ui:agentTerminal.urlOpen.dialogTitle}}カードが表示されます。
- {{ui:agentTerminal.urlOpen.inAppButton}}または{{ui:agentTerminal.urlOpen.externalButton}}ボタンを選びます。
- {{ui:agentTerminal.urlOpen.rememberToggle}}をオンにすると、次回からは確認しません。
- 30秒以内に選ばなかった場合は、アプリで開きます。ブラウザペインがオフの場合は、システムのブラウザで開きます。

この選択は、{{ui:settings.nav.general}}の{{ui:agentTerminal.urlOpen.settingTitle}}で変更できます: {{ui:agentTerminal.urlOpen.settingAsk}}、{{ui:agentTerminal.urlOpen.settingInApp}}、{{ui:agentTerminal.urlOpen.settingExternal}}。
