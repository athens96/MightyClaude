---
title: 画面構成
order: 2
section: layout
---
ウインドウは左のサイドバーと右の作業エリアに分かれています。作業エリアでは、ペインをタブとして重ねたり、左右・上下に分割して並べたりできます。

![[overview]]

## サイドバー {#sidebar}
サイドバーには上から順に次のものがあります。
- {{ui:phone.dashboard.title}}: すべてのワークスペースのペインの状態を1か所で確認します。
- {{ui:sidebar.searchPlaceholder}}: {{kbd:⌘K}}を押すと検索欄に直接移動します。
- {{ui:sidebar.workspacesHeader}}の一覧: ワークスペースをクリックすると、そのワークスペースに切り替わります。横の矢印をクリックすると、ペインの一覧を展開したり折りたたんだりします。
- 下部: 画面テーマのボタン(ダーク/ライト)と設定ボタン。

ワークスペースの一覧を展開すると、最後の行に{{ui:workspace.addPane}}ボタンがあります。ペインの行を右クリックすると、{{ui:menu.rename}}、{{ui:menu.closePane}}の項目があります。

### サイドバーを折りたたむ {#sidebar-collapse}
作業エリアを広く使いたいときは、サイドバーを折りたたみます。
1. サイドバー上部の折りたたみボタンをクリックするか、{{kbd:⌃⌘S}}を押します。メニューバーの「表示」メニューにも{{ui:sidebar.collapse}}の項目があります。
2. 再び展開するときも、同じボタンか{{kbd:⌃⌘S}}を使います({{ui:sidebar.expand}})。

折りたたんだ状態は、アプリを開き直しても保たれます。折りたたんだ状態で{{kbd:⌘K}}を押すと、サイドバーが展開されて検索欄に移動します。

![[sidebar-collapsed]]

## 作業状況 {#dashboard}
サイドバーの一番上にある{{ui:phone.dashboard.title}}をクリックすると、すべてのワークスペースのペインを状態順にまとめて表示します。
- 上部の欄で、{{ui:phone.dashboard.stat.running}}、{{ui:phone.dashboard.stat.waiting}}、{{ui:phone.dashboard.stat.done}}の件数を確認します。
- {{ui:dashboard.usage.title}}カードでアカウントの使用量を確認します。同じアカウントを使うほかのアプリやセッションと共有する上限です。
- ワークスペースごとに{{ui:files.pane.title}}ボタンと{{ui:workspace.addPane}}メニューがあります。
- カードをクリックすると、そのペインに移動します。ワークスペースかペインを選ぶと、作業状況から抜けます。

![[dashboard]]

## ペインを追加する {#add-pane}
ペインは、AIエージェント(Claude、Codex、Gemini)、ターミナル、ブラウザ、ファイルのいずれかです。

1. サイドバーの{{ui:workspace.addPane}}ボタンをクリックします。
2. 作成するペインを選びます: Claude/Codex/Geminiのペイン、{{ui:workspace.newTerminal}}、{{ui:browser.newTab}}。ほかのフォルダは、同じメニューの{{ui:menu.openProject}}で開きます。
3. 新しいAIペインは、同じエージェントで最後に使ったペインのモデル、思考レベル、権限、表示方法を引き継ぎます。会話は新しく始まります。

ショートカット: {{ui:menu.newClaudePane}}は{{kbd:⌘N}}、{{ui:menu.addTerminalPane}}は{{kbd:⌘T}}、{{ui:menu.showFiles}}は{{kbd:⇧⌘E}}です。

> [!note]
> CodexとGeminiのペインには{{ui:badge.beta}}の表示が付きます。

### 以前のセッションを続ける {#resume}
ClaudeやCodexのペインを追加するとき、このフォルダに以前のセッションがあると選択画面が表示されます。
1. 新しい会話なら{{ui:resume.choice.startNew}}、以前の会話なら{{ui:resume.choice.resume}}ボタンをクリックします。
2. {{ui:resume.title}}の一覧からセッションを選びます。{{ui:resume.search}}で探すこともできます。
3. 新しいペインがそのセッションの続きから始まります。

![[resume-choice]]

![[resume-list]]

## タブと分割 {#tabs-splits}
- タブをドラッグしてほかのグループの中央にドロップすると、タブとしてまとまります({{ui:layout.drop.merge}})。
- グループの左・右・上・下の端にドロップすると、その方向に分割されます。
- 分割の境界線をドラッグするとサイズが変わり、ダブルクリックすると半分ずつに分かれます。
- ドラッグ中に{{kbd:Esc}}を押すとキャンセルされます。
- タブを右クリックすると、{{ui:menu.rename}}、{{ui:menu.focusPane}}、{{ui:menu.closeTab}}の項目があります。

配置はワークスペースごとに保存されます。ワークスペースを切り替えても、それぞれの配置がそのまま残ります。

### ペインのメニュー {#pane-menu}
ペインのヘッダーにある…ボタンには、次の項目があります。
- {{ui:menu.rename}}
- {{ui:menu.focusPane}}: このペインだけを大きく表示します。もう一度選ぶと{{ui:pane.menu.restoreLayout}}で元に戻ります。
- {{ui:pane.menu.copyLog}}
- {{ui:pane.menu.newConversation}}: 次のリクエストからは以前の会話を引き継ぎません。
- {{ui:menu.closePane}}

## ショートカット {#shortcuts}
| ショートカット | 動作 |
|---|---|
| {{kbd:⌘O}} | {{ui:menu.openProject}} |
| {{kbd:⌘N}} | {{ui:menu.newClaudePane}} |
| {{kbd:⌘T}} | {{ui:menu.addTerminalPane}} |
| {{kbd:⇧⌘E}} | {{ui:menu.showFiles}} |
| {{kbd:⌘K}} | {{ui:menu.searchWorkspaces}} |
| {{kbd:⌃⌘S}} | {{ui:sidebar.collapse}} / {{ui:sidebar.expand}} |
| {{kbd:⌘,}} | {{ui:menu.settings}} |
| {{kbd:Enter}} | リクエストを送信 |
| {{kbd:⇧Enter}} | 改行 |
| {{kbd:⌘Enter}} | 実行中のClaudeにすぐに伝える |
| {{kbd:Esc}} | シートを閉じる、スラッシュコマンドの一覧を閉じる |
