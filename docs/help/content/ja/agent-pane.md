---
title: エージェントペイン
order: 3
section: agent-pane
---
エージェントペインは、Claude、Codex、Geminiのいずれかと会話するペインです。下部の入力欄にリクエストを書き、上部で進行状況と結果を確認します。

![[agent-basic]]

## リクエストを送信する {#send}
1. 入力欄にやってほしいことを書きます。
2. {{kbd:Enter}}を押すか、送信ボタンをクリックします。改行するには{{kbd:⇧Enter}}を押します。
3. 実行中は送信ボタンの位置に停止ボタンが表示されます。止めるにはそのボタンをクリックします。

日本語入力のように変換中の文字があるときに押した{{kbd:Enter}}は、送信せずに変換の確定だけを行います。

![[composer]]

### 実行中に続けてリクエストする {#queue}
タスクの実行中でも、次のリクエストを書けます。入力欄に文字があると、停止ボタンの横に送信ボタンと同じ丸いボタンが表示されます。
- {{kbd:Enter}}または{{ui:queue.add}}ボタン: 次のリクエストとしてキューに入れます。現在のタスクが終わると順番に実行します。1つのペインに16件まで入れられます。
- {{kbd:⌘Enter}}または{{ui:phone.composer.steer}}ボタン: 実行中のClaudeにすぐに伝えます。このMacのClaudeペインでのみ使えます。Mighty表示ではオレンジ色の{{ui:graph.block.steer}}ブロックとして表示されます。
- キューの{{ui:queue.runNext}}をクリックすると、待機中の最初のリクエストを今すぐ実行します。

タスクを停止すると、待機中のリクエストもキャンセルされます。アプリを開き直すとキューは空になります。

## 入力欄のツール {#composer}
入力欄の下の行には、左から添付、モデル、思考レベル、権限、その他の実行設定(…)が並んでいます。ペインの幅が狭いと、一部が1つのメニューにまとめられます。実行中は変更できず、変更した値は次のリクエストから適用されます。

### モデル {#model}
モデルのチップをクリックすると、{{ui:composer.label.runner}}(Claude、Codex、Gemini)と{{ui:composer.label.model}}を選べます。一覧が正しくない場合は{{ui:composer.model.refresh}}をクリックしてください。エージェントを変更すると、会話は新しく始まります。

### 思考レベル {#effort}
{{ui:composer.effort.label}}は、モデルがどれだけ深く考えるかを決めます。Auto、Low、Medium、High、XHigh、Maxから選びます。モデルが対応している段階だけが表示されます。Geminiにはありません。

### 作業の権限 {#permission}
権限は、エージェントが確認なしで行える作業の範囲です。

| エージェント | 選べる権限 |
|---|---|
| Claude | Plan mode · Always ask · Accept file edits · Auto mode · Bypass |
| Codex | {{ui:permission.label.defaultCodex}} · {{ui:permission.label.acceptEditsCodex}} · {{ui:permission.label.onRequest}} · {{ui:permission.label.fullAccess}} |
| Gemini | {{ui:permission.label.default}} · {{ui:permission.label.plan}} · {{ui:permission.label.acceptEdits}} · {{ui:permission.label.fullAccess}} |

- ClaudeのAlways askとCodexの{{ui:permission.label.onRequest}}では、追加の権限が必要になったときにペイン内で許可を求めます。[許可と質問](approvals.md#permission)を参照してください。
- Auto modeは、インストールされているClaude CLIが対応している場合にのみ表示されます。
- Bypassと{{ui:permission.label.fullAccess}}は、プロジェクト外のファイルやコマンドにもアクセスします。本当に必要なときだけ使ってください。

### その他の実行設定 {#run-settings}
…ボタンをクリックすると{{ui:settings.run.title}}が開きます。
- Claude: {{ui:settings.run.maxTurnsLabel}}と{{ui:settings.run.maxBudgetLabel}}。空欄にすると上限を設けません。
- Codex: {{ui:settings.run.webSearchLabel}}の設定と{{ui:settings.run.shellNetworkToggle}}。入力欄のFastチップは、対応しているモデルとアカウントでより速く応答しますが、使用量が多くなります。

値を変更したら{{ui:settings.run.applyButton}}をクリックします。

![[composer-settings]]

### ファイルと画像の添付 {#attachments}
クリップのボタンをクリックするか、ファイルを入力欄にドラッグ&ドロップするか、{{kbd:⌘V}}で画像を貼り付けます。
- 最大8個、1ファイルあたり5 MiB、合計8 MiBまで添付できます。
- 送信前にプレビューして、1つずつ外せます。添付だけを送信することもできます。
- ターミナルペインには添付できません。

### スラッシュコマンド {#slash}
入力欄に`/`を入力すると、コマンドの一覧が表示されます。{{kbd:↑}}{{kbd:↓}}で選び、{{kbd:Enter}}か{{kbd:Tab}}で入力し、{{kbd:Esc}}で閉じます。

| コマンド (Claudeの場合) | 動作 |
|---|---|
| `/model` | {{ui:slash.builtin.model}} |
| `/permissions` | {{ui:slash.builtin.permission}} |
| `/clear` | {{ui:slash.builtin.newConversation}} |
| `/cost`, `/usage` | {{ui:slash.builtin.usage}} |
| `/plugin` | {{ui:slash.builtin.plugin}} |
| `/config` | {{ui:slash.builtin.settings}} |
| `/rename` | {{ui:slash.builtin.rename}} |
| `/help` | {{ui:slash.builtin.help}} |

CodexとGeminiでは名前が少し異なります(例: Codexの`/new`、`/approvals`、`/status`)。一覧にない`/名前`は、CLIのスキル、ユーザーコマンド、プラグインのコマンドとしてそのまま渡されます。

### コンテキストと使用量 {#session-info}
送信ボタンの横の円形のリングは、会話がコンテキストをどれだけ使ったかを示します。クリックすると、{{ui:composer.sessionInfo.contextUsage}}、入力・出力・キャッシュのトークン、コスト、使用したモデル、セッションIDを確認できます。CLIが報告した値だけを表示し、不明な値は推定しません。

![[session-info]]

## 標準表示とMighty表示 {#views}
ClaudeとCodexのペインのヘッダーには、{{ui:graph.view.default}} | {{ui:graph.view.mighty}}の切り替えがあります。切り替えても、会話と入力中の文章はそのまま残ります。ペインごとに別々に記憶されます。Geminiのペインには標準表示しかありません。
- {{ui:graph.view.default}}: 会話を上から下へ読む記録です。
- {{ui:graph.view.mighty}}: リクエスト、サブエージェント、バックグラウンドタスク、結果をブロックのフローで表示します。

### Mightyフロー {#diagram}
リクエストごとにリクエストブロックができ、そのすぐ下に{{ui:graph.block.execution}}ブロックが付きます。リクエストブロックには送ったリクエストだけが入り、エージェントがそのリクエストで行ったこと（メッセージ、ツール呼び出し、進み具合）は{{ui:graph.block.execution}}ブロックに表示されます。サブエージェントとバックグラウンドタスクは{{ui:graph.block.execution}}ブロックの下に枝分かれして表示されます。終わると{{ui:graph.block.result}}カードが付きます。
- 何もない場所をドラッグするかスクロールして、フローを動かします。拡大・縮小ボタンで50〜150%の倍率で表示できます。
- ブロックをクリックすると、そのブロックの中だけがスクロールします。何もない場所をクリックするか{{kbd:Esc}}を押すと、選択が解除されます。
- ブロックの右下の角をドラッグするとサイズが変わります。右クリックすると{{ui:graph.block.resetSize}}があります。サイズはセッションごとに保存されます。
- 一番上までスクロールすると、{{ui:graph.history.load}}で以前のリクエストを10件ずつ読み込みます。読み込んだ履歴は閲覧専用です。
- ヘッダーのパズルのボタンで、インストール済みのプラグインとマーケットプレイスを開きます。

![[agent-mighty]]

![[agent-mighty-overview]]

### 記録 {#timeline}
Mighty表示の上部にある{{ui:graph.view.diagram}} | {{ui:graph.view.timeline}}の切り替えで、リクエストを時系列の一覧として表示できます。リクエストの行の次に{{ui:graph.block.execution}}の行が、その次にサブエージェントの行が続きます。

![[agent-timeline]]

### ボード {#structure}
同じ切り替えで{{ui:graph.view.structure}}を選ぶと、リクエストをかんばんボードで表示できます。
- 表示は一度に一つです。スタイルのないペインではリクエスト一つ、スタイルを使うペインではパイプライン一つです。パイプラインはフェーズが前にだけ進むリクエストのまとまりで、前のリクエストより前のフェーズに戻ったリクエストから新しいパイプラインが始まります。
- 上部の ◀ {{ui:graph.structure.board.previous}}と ▶ {{ui:graph.structure.board.next}}で前後に移動します。その間の「リクエスト 3 / 7」のようなタイトルを押すと一覧が開き、番号、タイトル、状態、ステップを見てすぐに選べます。
- その下に進み具合が表示されます。状態（スタイルを使うペインではパイプライン名も）と、線でつながったステップのチップです。スタイルのないペインでは{{ui:graph.structure.column.request}} → {{ui:graph.structure.column.execution}} → {{ui:graph.structure.column.result}}、スタイルを使うペインではそのモードのフェーズです。終わったステップは緑、今のステップはアクセントカラー、残りはグレーです。
- 内容はステップごとに横に並んだ列に入ります。列の見出しにはステップ名とカードの数があります。スタイルのないペインでは、{{ui:graph.structure.column.request}}列に送ったリクエスト、{{ui:graph.structure.column.execution}}列に進み具合（最新のメッセージとツール呼び出し）と状態のカードとサブエージェントごとに一枚のカード、{{ui:graph.structure.column.result}}列に結果が置かれます。スタイルを使うペインでは、フェーズの列ごとにそのフェーズのリクエストカード（リクエスト、実行の要約、結果の1行）と、その下にサブエージェントのカードが置かれます。入れ子のサブエージェントは字下げされ、どのブロックの下かが書かれます。
- 長い文章は折りたたまれていて、{{ui:graph.structure.more}}を押すとその場で開きます。カードの見出しにある小さなボタンを押すと、フローでそのブロックを表示します。
- 表示は最新のものから開き、そのままにしておくと新しいリクエストやパイプラインに付いていきます。前のものを見ている間に新しいものが来ても位置はそのままで、▶ の横に{{ui:graph.structure.board.newRequest}}または{{ui:graph.structure.board.newPipeline}}の印が付きます。印を押すと最新のものへ移ります。
- 列はそれぞれ別々に動きます。列の見出しは上に残り、カードだけが上下に動きます。列の上でトラックパッドやマウスホイールを上下にスクロールするか、何もない場所をドラッグすると、その列だけが動きます。列が画面に収まらないときは、横にスクロールするかドラッグしてボードを横に動かします。
- 最新のものでは、終わりを見ている列が新しい内容に付いて下がります。自分で動かした列や文章を広げた列はその位置に留まり、その列の終わりに戻るとまた付いていきます。新しいリクエストが来るとすべての列が終わりを表示し、以前のものはどの列も一番上から開きます。Windowsアプリではまだボード全体が一緒に動きます。

![[agent-structure]]

### 結果カード {#result}
リクエストが終わると、{{ui:graph.block.result}}カードに結果がまとめられます。結果にファイルが含まれていれば、{{ui:graph.resultFiles.openButton}}ボタンで一覧を開きます。ブロック内のファイルパスをクリックするとプレビューが表示されます。最新の結果カードはウインドウのサイズに合わせて表示され、サイズを手動で変更した場合は{{ui:graph.result.fitToWindow}}ボタンが表示されます。

![[result-card]]
