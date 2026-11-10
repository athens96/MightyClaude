---
title: エージェントペイン
order: 3
section: agent-pane
---
エージェントペインは、Claude、Codex、Antigravity、pi、opencode、Hermesのいずれかと会話するペインです。下部の入力欄にリクエストを書き、上部で進行状況と結果を確認します。

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
- {{kbd:⌘Enter}}または{{ui:queue.worktree}}ボタン: MightyオーケストレーションがオンのこのMacのClaudeペイン、または子がファイルを変更できるモードのCodex・Antigravityペインが、コミットのあるブランチのgitリポジトリにあるときは、このペインの隣に専用のgit worktreeを持つ新しい子ペインを開いてすぐに実行します。実行中のタスクはそのまま続き、子が終わったらサイドバーのカードで結果をマージします([新しいworktreeで実行する](delegation.md#worktree))。
- 子ペイン、そのようなリポジトリの外のペイン、Mightyオーケストレーションをオフにしたペインでは、{{kbd:⌘Enter}}も{{kbd:Enter}}と同じようにキューに入れます。子を作れないときや始められないときも、テキストはキューに入ります([新しいworktreeで実行する](delegation.md#worktree))。送ったテキストが実行中のタスクに割り込むことはもうありません。以前の記録のMighty表示には、そうしたテキストがオレンジ色の{{ui:graph.block.steer}}ブロックとして残っています。実行中のClaudeの親にそのまま届いた委任の通知は、同じ色の{{ui:graph.block.delegationNotice}}ブロックとして表示されます([通知と保留](delegation.md#notices))。
- キューの各リクエストには、✕の隣に{{ui:queue.runNow}}ボタンがあります。新しいworktreeを作れる場所(上の{{kbd:⌘Enter}}と同じ条件)では、そのリクエストを新しい子ペインですぐに実行し、実行中のタスクはそのまま続きます。右クリック(MacではControlクリックも可)で別のエージェントを選べます。ボタンのツールチップが、どちらになるかを教えてくれます。テキストがなく添付ファイルだけのリクエストは、worktreeには行きません。
- worktreeを作れない場所では、このペインで処理します。エラーで止まったペインのように待機中なら、そのリクエストをすぐに始めます。Claudeの応答が終わってバックグラウンドタスクだけを待っているペインでは、リクエストが実行中のClaudeにそのまま届き、Mighty表示に{{ui:graph.block.steer}}ブロックとして表示されます(添付のあるリクエストはタスクが終わるまで待ちます)。それ以外ではボタンがグレーになり、ツールチップが理由を教えてくれます。今のタスクが先に終わる必要がある、このペインに保留された項目が先に送られる、CLIの更新中、のいずれかです。そのリクエストは順番を待ちます。Antigravityのカードの続きのリクエストは、いつも自分のペインでだけ実行されます。

タスクを停止すると、待機中のリクエストもキャンセルされます。ただし、バックグラウンドの作業だけを待っているペインで停止すると、キューはそのまま残り、最初のリクエストがすぐに始まります。アプリを開き直すとキューは空になります。

## 入力欄のツール {#composer}
入力欄の下の行には、左から添付、モデル、思考レベル、権限、その他の実行設定(…)が並んでいます。ペインの幅が狭いと、一部が1つのメニューにまとめられます。実行中は変更できず、変更した値は次のリクエストから適用されます。

### モデル {#model}
モデルのチップをクリックすると、{{ui:composer.label.runner}}(Claude、Codex、Antigravity、pi、opencode、Hermes)と{{ui:composer.label.model}}を選べます。一覧が正しくない場合は{{ui:composer.model.refresh}}をクリックしてください。エージェントを変更すると、会話は新しく始まります。

### 思考レベル {#effort}
{{ui:composer.effort.label}}は、モデルがどれだけ深く考えるかを決めます。Auto、Low、Medium、High、XHigh、Maxから選びます。モデルが対応している段階だけが表示されます。Antigravityにはなく、piはOff、Minimal、Low、Medium、High、XHighで、opencodeはモデルが提供するバリアントのLow、Medium、High、Maxで、HermesはLow、Medium、High、XHigh、Maxです。

### 作業の権限 {#permission}
権限は、エージェントが確認なしで行える作業の範囲です。

| エージェント | 選べる権限 |
|---|---|
| Claude | Plan mode · Always ask · Accept file edits · Auto mode · Bypass |
| Codex | {{ui:permission.label.defaultCodex}} · {{ui:permission.label.acceptEditsCodex}} · {{ui:permission.label.onRequest}} · {{ui:permission.label.fullAccess}} |
| Antigravity | {{ui:permission.label.default}} · {{ui:permission.label.plan}} · {{ui:permission.label.acceptEdits}} · {{ui:permission.label.fullAccess}} |
| pi | {{ui:permission.label.defaultCodex}} · {{ui:permission.label.fullAccess}} |
| opencode | {{ui:permission.label.default}} · {{ui:permission.label.plan}} · {{ui:permission.label.acceptEdits}} · {{ui:permission.label.fullAccess}} |
| Hermes | {{ui:permission.label.default}} · {{ui:permission.label.acceptEdits}} · {{ui:permission.label.fullAccess}} |

- ClaudeのAlways ask、Codexの{{ui:permission.label.onRequest}}、{{ui:permission.label.fullAccess}}以外のopencodeのすべてのモード、Hermesのファイル変更と危険なコマンドでは、追加の権限が必要になったときにペイン内で許可を求めます。[許可と質問](approvals.md#permission)を参照してください。
- Auto modeは、インストールされているClaude CLIが対応している場合にのみ表示されます。
- Bypassと{{ui:permission.label.fullAccess}}は、プロジェクト外のファイルやコマンドにもアクセスします。本当に必要なときだけ使ってください。

### Antigravity {#antigravity}
GoogleのエージェントはAntigravity CLI(`agy`)で動きます。AntigravityはClaudeやGPT-OSSのモデルも実行できます。

- インストール: Macでは`curl -fsSL https://antigravity.google/cli/install.sh | bash`を実行すると、`~/.local/bin`に`agy`が入ります。WindowsではPowerShellで`irm https://antigravity.google/cli/install.ps1 | iex`を実行すると、`%LOCALAPPDATA%\agy\bin`に`agy.exe`が入ります。アップデートはアプリが`agy update`で行います。
- サインイン: ターミナルペインで`agy`を一度実行し、ブラウザでGoogleアカウントにサインインします。{{ui:settings.nav.cli}}タブの{{ui:settings.cliAccounts.buttonLogin}}をクリックすると、アプリが代わりに実行します。Antigravityにはサインアウトのコマンドがないため、アカウントを切り替えるには`agy`の中で`/logout`を使います。
- 権限: Antigravityはアプリの中で確認できません。{{ui:permission.label.default}}はagyに保存されたAgent Modeに従います(agyの`/config`で変えていなければレビュー)。ワークスペースのファイルは読めますが、確認が必要なファイル編集とシェルコマンドは拒否し、ターンを終えてから、拒否した操作を権限カードで尋ねます。{{ui:permission.allowOnce}}または{{ui:antigravity.permission.allowAlways}}を押すと、同じ会話で続きが進みます([Antigravityの権限カード](approvals.md#antigravity-permission))。{{ui:permission.label.plan}}はagy自身のフォルダに計画を書き、プロジェクトを変更せずに止まります。実行するには、次のリクエストを別のモードで送ってください。あらかじめ許可しておきたい操作は、`~/.gemini/antigravity-cli/settings.json`の`permissions.allow`に許可ルール(例: `command(git status)`。ルールはそのテキストで始まるすべてのコマンドを許可するので、できるだけ狭く)を自分で追加するか、{{ui:permission.label.acceptEdits}}(ファイル編集)または{{ui:permission.label.fullAccess}}(すべて許可)を選んでください。
- Antigravityのモデルは名前に思考の深さが含まれているため、{{ui:composer.effort.label}}を別に選びません。
- {{ui:graph.view.mighty}}表示はClaudeやCodexと同じです。Antigravityが起動したサブエージェントはリクエストの下にそれぞれのブロックになり、その内容(受けた指示、メッセージ、ツール呼び出し、送り返した答え)は`~/.gemini/antigravity-cli`(Windowsでは`%USERPROFILE%\.gemini\antigravity-cli`)に残るサブエージェント自身の記録から読みます。読んだファイルは名前だけが表示され、中身は写しません。会話の以前のリクエストも同じ記録から読み込み、{{ui:resume.choice.resume}}にはこのフォルダのAntigravityの会話が表示されます。
- 以前Gemini CLIで実行していたペインは、履歴はそのままですが次のリクエストから新しい会話になり、Gemini CLIのモデルを選んでいた場合は既定に戻ります。

### pi {#pi}
pi(`pi`)は、多くのモデル提供元で使えるオープンソースのコーディングエージェントCLIです。

- インストール: {{ui:setup.title}}画面からインストールします(HomebrewとNode.jsがなければ先にインストールします)。自分でインストールするなら`npm install -g @mariozechner/pi-coding-agent`を実行します。アップデートはアプリが`pi update self`で行います。
- サインイン: {{ui:settings.nav.cli}}タブの{{ui:settings.cliAccounts.buttonLogin}}を押すと、`pi`を実行するターミナルペインが開きます。そこで`/login`と入力し、サブスクリプション(Claude Pro/Max、ChatGPT、GitHub Copilot)またはAPIキーを選びます。環境変数のAPIキー(`ANTHROPIC_API_KEY`など)や`~/.pi/agent/auth.json`に保存された認証情報もサインイン済みと見なします。サインアウトはpiの中で`/logout`と入力します。
- モデル: サインインすると、`pi --list-models`の一覧が「提供元/モデル」の名前で表示され、先頭の項目はpi自身の設定に従います。思考機能のないモデルでは{{ui:composer.effort.label}}は表示されません。段階はOff、Minimal、Low、Medium、High、XHighです。
- 権限: piには承認ウィンドウがないため、モードは2つです。{{ui:permission.label.defaultCodex}}モードは読み取りツール(read、grep、find、ls)だけを有効にします。{{ui:permission.label.fullAccess}}モードはすべてのツールを確認なしで使い、プロジェクトの外にも触れられます。
- リクエストごとにペインの作業フォルダで`pi --mode rpc`を実行し、ペインのセッションを引き継ぎます。セッションは`~/.pi/agent/sessions`に保存され、{{ui:resume.choice.resume}}にもこのフォルダのpiセッションが表示されます。
- 質問: piには質問ツールがないため、アプリが実行のたびに`ask_user`ツールを拡張機能として追加します（`~/.pi`の設定は変更しません）。piがこのツールで質問すると[質問カード](approvals.md#questions)が表示され、答えると同じリクエストが続きます。ほかの拡張機能が開く選択・確認・入力のダイアログも同じカードで表示されます。
- まだできないこと: piのペインは[Mighty Orchestration](delegation.md)に参加せず、[リモートワークスペース](remote.md)では開けず、承認カードもありません。今のところMacでのみ動作します。

### opencode {#opencode}
opencode(`opencode`)は、多くのモデル提供元で使えるオープンソースのコーディングエージェントCLIです。自社サービスopencode Zenの無料モデルはサインインなしでも使えます。

- インストール: {{ui:setup.title}}画面からインストールします(HomebrewとNode.jsがなければ先にインストールします)。自分でインストールするなら`npm install -g @opencode/cli`を実行します。アプリはOpenCode 2を使います。MacにOpenCode 1があると古いものとして表示され、セットアップ画面からインストールするとnpmの`opencode-ai`を先に削除します。アップデートはアプリが`opencode upgrade`で行います。
- サインイン: {{ui:settings.nav.cli}}タブの{{ui:settings.cliAccounts.buttonLogin}}を押すと、`opencode auth login`を実行するターミナルペインが開きます。そこで提供元を選び、そのサブスクリプションでサインインするかAPIキーを入力します。opencodeが保存したサインイン(`opencode auth list`)や環境変数のAPIキー(`ANTHROPIC_API_KEY`など)もサインイン済みと見なし、どちらもなければopencode Zenの無料モデルだけが表示されます。ログアウトはターミナルで`opencode auth logout`を実行します。
- モデル: `opencode models`の一覧が「提供元/モデル」の名前で表示され、先頭の項目はopencode自身の設定に従います。
- 権限: 選んだモードがそのリクエストだけのopencode自身のルールになり、ルールが確認に残した操作はClaudeと同じくペインに承認カードとして届きます。{{ui:permission.label.default}}はファイルの編集、コマンド、Web、プロジェクト外へのアクセスの前に確認し、{{ui:permission.label.acceptEdits}}はファイルの編集を確認せず、{{ui:permission.label.plan}}はファイルを編集しないopencodeのplanエージェントで実行し、{{ui:permission.label.fullAccess}}は確認しません。
- リクエストごとにペインの作業フォルダで`opencode acp`(OpenCode 2、Agent Client Protocol)を実行し、ペインのセッションを引き継ぎます。セッションはopencodeの`opencode.db`に保存され、{{ui:resume.choice.resume}}にもこのフォルダのopencodeセッションが表示されます。使用量にはセッションの費用、使用中のコンテキスト、リクエストのトークンが表示されます。
- 質問: opencodeが`question`ツールで質問すると[質問カード](approvals.md#questions)が表示され、答えると同じリクエストが続きます。カードで{{ui:phone.questionnaire.cancel}}をクリックすると、opencodeはそのリクエストを終了します。
- まだできないこと: opencodeのペインは[Mighty Orchestration](delegation.md)に参加せず、[リモートワークスペース](remote.md)では開けません。今のところMacでのみ動作します。

### Hermes {#hermes}
Hermes(`hermes`)は、Nous ResearchのオープンソースのエージェントCLI(Hermes Agent)です。Nous Portal、OpenRouter、Anthropic、OpenAIなど多くのモデル提供元を使えます。

- インストール: {{ui:setup.title}}画面からインストールします(Gitがなければ先にインストールします)。公式のインストールスクリプトが専用のPython環境と一緒に`~/.hermes`へインストールし、ブラウザツールもダウンロードするため数分かかります。自分でインストールする場合は`curl -fsSL https://hermes-agent.nousresearch.com/install.sh | bash`を実行します。アップデートはアプリが`hermes update`で行います。
- サインイン: {{ui:settings.nav.cli}}タブの{{ui:settings.cliAccounts.buttonLogin}}ボタンをクリックすると、`hermes model`を実行するターミナルペインが開きます。そこでモデルの提供元を選び、サインインするかAPIキーを入力してから、既定のモデルを選びます。アプリが読むのは、Hermesが資格情報を見つけた提供元の名前だけです。資格情報を消すにはターミナルで`hermes auth`を使います。
- モデル: 最初の項目はHermes自体の設定(`hermes model`で決めた既定のモデル)に従います。Hermesはセッションを開いて初めてモデル一覧を示すため、最初のリクエストを送ったあとから`提供元:モデル`の一覧が表示されます。
- 権限: {{ui:permission.label.default}}はファイルを変更するたびにペインの承認カードで確認し、{{ui:permission.label.acceptEdits}}は作業フォルダ内の変更を確認せず、{{ui:permission.label.fullAccess}}はHermesの`--yolo`で危険なコマンドも確認しません。危険なコマンド(`rm -rf`など)はHermesの承認設定(`approvals.mode`)に従ってカードで確認し、それ以外のコマンドと読み取りは確認しません。計画専用のモードはありません。{{ui:permission.label.fullAccess}}はセッション開始時に決まるため、すでに始めたセッションには新しい会話から適用されます。
- リクエストごとにペインの作業フォルダで`hermes acp`(Agent Client Protocol)を実行し、ペインのセッションを引き継ぎます。実際の作業はHermesのゲートウェイ(バックグラウンドプロセス)が行い、必要なときに自動で起動し、使われなくなると終了します。セッションは`~/.hermes/state.db`に保存され、{{ui:resume.choice.resume}}にもこのフォルダのHermesセッションが表示されます。使用量にはセッションの累計トークンと使用中のコンテキストが表示され、費用はHermesが知らせません。
- 質問: Hermesがclarifyツールで質問すると[質問カード](approvals.md#questions)が表示され、答えると同じリクエストが続きます。Hermesはエディタ接続（ACP）ではこのツールを既定でオフにしているため、最初のHermesリクエストの前に、アプリがHermesの設定の`platform_toolsets.acp`という1項目を`hermes config set`でオンにし、記録に残します。自分で保存したリストがある場合は変更しません。新しい会話から適用され、元に戻すにはターミナルで`hermes config unset platform_toolsets.acp`を実行します。Hermesが回答を待つのは一定時間（既定で90秒）だけです。その後はカードが閉じ、Hermesは回答なしで先に進みます。
- まだできないこと: Hermesのペインは[Mighty Orchestration](delegation.md)に参加せず、[リモートワークスペース](remote.md)では開けません。現在はMacでのみ動作します。

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

### 送り先 {#ask-target}
oh-my-claudecodeがインストールされたClaudeペインでは、入力欄に{{ui:composer.ask.menuLabel}}スイッチが表示されます。インストール済みの他のCLI(Codex、Antigravityなど)が一覧に並び、1つ選ぶと、以降のリクエストはoh-my-claudecodeの`ask`を通ってそのCLIに送られます。答えはClaudeの返信の中に戻ってきます。{{ui:composer.ask.claude}}を選ぶと、これまでどおりClaudeに送ります。Mac、Windowsアプリのウェブウィンドウ、スマートフォンで使えます。
- 他のCLIに尋ねている間は、添付を送れません。
- oh-my-claudecodeは、選んだCLIを承認の確認とサンドボックスをオフにして起動します。そのためそのCLIは、確認なしにこのフォルダのファイルを変更したり、コマンドを実行したりできます。Mighty Codeの権限モードと承認カードが及ぶのは、それを起動するClaudeの手順だけです。CLI を選んでいる間、入力欄にこの警告が 1 行で表示されます。
- Windowsでは、古いバージョンのoh-my-claudecodeはAntigravityへの質問を断ります。そう返ってきたら、oh-my-claudecodeを更新するか、Gemini CLIを使ってください。
- `/`で始まる文章は、そのままClaudeに送られます。

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

CodexとAntigravityでは名前が少し異なります(例: Codexの`/new`、`/approvals`、`/status`、Antigravityの`/usage`、`/config`)。一覧にない`/名前`は、CLIのスキル、ユーザーコマンド、プラグインのコマンドとしてそのまま渡されます。Antigravityのスキルもこうして実行され、一覧にはagyに入っているスキル、`~/.gemini/antigravity-cli/skills`の自分のスキル、フォルダの`.agents/skills`が表示されます。ただし`/help`や`/skills`のようなAntigravity自身の読み取り専用コマンドは、ターミナルで`agy -p "/help"`のように別に実行したときだけ答えるため、アプリではagyのエラーで終わります。

### コンテキストと使用量 {#session-info}
送信ボタンの横の円形のリングは、会話がコンテキストをどれだけ使ったかを示します。クリックすると、{{ui:composer.sessionInfo.contextUsage}}、入力・出力・キャッシュのトークン、コスト、使用したモデル、セッションIDを確認できます。CLIが報告した値だけを表示し、不明な値は推定しません。

![[session-info]]

## Mighty表示 {#views}
Claude、Codex、Antigravityのペインは{{ui:graph.view.mighty}}表示で表示されます。リクエスト、サブエージェント、バックグラウンドタスク、結果をブロックのフローで表示します。切り替える操作はありません。

### Mightyフロー {#diagram}
リクエストごとにリクエストブロックができ、そのすぐ下に{{ui:graph.block.execution}}ブロックが付きます。リクエストブロックには送ったリクエストだけが入り、エージェントがそのリクエストで行ったこと（メッセージ、ツール呼び出し、進み具合）は{{ui:graph.block.execution}}ブロックに表示されます。サブエージェントとバックグラウンドタスクは{{ui:graph.block.execution}}ブロックの下に枝分かれして表示されます。終わると{{ui:graph.block.result}}カードが付きます。

{{platform:mac}} {{ui:graph.block.execution}}ブロックとサブエージェントブロックのヘッダーは、誰が作業しているかを示します。左端の色の帯とアイコンの色が役割です（メインセッション、サブエージェント）。タイトルの後ろにサブエージェントの種類（Explore、自作エージェントの名前など）と、バックグラウンド・フォーク・worktree の印が付き、{{ui:graph.block.execution}}ブロックにはペインで選んだ思考レベルが付きます。モデルはトークン表示と一緒に出て、まだ応答がないあいだは指定したモデルの後ろに「指定」と書かれます。自作エージェントの定義ファイルに `color` があればその色を使います。ブロックが狭いとこれらの印が先に隠れ、アイコンにポインタを合わせると全体を読めます。

{{platform:mac}} Claude ペインのフローでは、実行中のサブエージェントが、それを起動したブロックの下に小さなカードとして並びます。バックグラウンドで起動したサブエージェントもここに表示されます。カードには名前、種類とモデル、いま行っていることの一行、そして状態と経過時間・ツール呼び出し数・トークンが出ます。アプリが実際に見た値だけが出て、進捗率はありません。カードをクリックすると全体のブロックに開き、ブロックのヘッダーの{{ui:graph.card.fold}}ボタンで元に戻します。フォアグラウンドのサブエージェントは、終わるとこれまでどおりブロックになります。バックグラウンドのサブエージェントは終わってもカードのままで、クリックすると{{ui:graph.background.noticeRun}}の図にある全体のブロックへ移動します。

バックグラウンドで始めたサブエージェントとバックグラウンドコマンド（タスク）は、実行中はリクエストのブロックとしては描かれず、入力欄の上のバックグラウンド作業の帯にあります。MacのClaudeペインでは、サブエージェントは上のカードとしても表示されます。行には種類、名前、経過時間、出力の最後の1行が表示され、行を開くと実行したコマンド（サブエージェントはプロンプトの1行目）と出力の最後の数行（最大6行）が表示されます。タスクが終わって通知がClaudeに届くと、Claudeの応答ごとに新しい{{ui:graph.background.noticeRun}}の図ができ、終わったタスクごとに1つのブロックが、種類の行（サブエージェントかバックグラウンドコマンドか、終わり方）、コマンド、出力の最後の数行、サブエージェントの報告を示します。{{ui:graph.view.timeline}}ではこの図が新しい行として、{{ui:graph.view.structure}}では最後に新しいカード（ボード）として付きます。スマートフォンのMighty表示とパイプラインも同じです。

ClaudeやCodexがリクエストの処理中にやることリストを作ると、フローの左上に{{ui:graph.steps.title}}パネルが出て、ステップごとに完了・進行中・待機を表示します。ヘッダーのボタンで折りたためます。新しいリクエストを送ると前のリクエストのリストは消えます。Codexはステップが終わったかどうかだけを伝えるため、終わっていない最初のステップを進行中として表示します。Antigravityには表示するチェックリストがないため、パネルは出ません。

- 何もない場所をドラッグするかスクロールして、フローを動かします。拡大・縮小ボタンで50〜150%の倍率で表示できます。
- ブロックをクリックすると、そのブロックの中だけがスクロールします。何もない場所をクリックするか{{kbd:Esc}}を押すと、選択が解除されます。
- ブロックの右下の角をドラッグするとサイズが変わります。右クリックすると{{ui:graph.block.resetSize}}があります。サイズはセッションごとに保存されます。
- 一番上までスクロールすると、{{ui:graph.history.load}}で以前のリクエストを10件ずつ読み込みます。読み込んだ履歴は閲覧専用です。
- ClaudeとCodexのペインでは、ヘッダーのパズルのボタンで、インストール済みのプラグインとマーケットプレイスを開きます。

![[agent-mighty]]

![[agent-mighty-overview]]

### 記録 {#timeline}
Mighty表示の上部にある{{ui:graph.view.diagram}} | {{ui:graph.view.timeline}}の切り替えで、リクエストを時系列の一覧として表示できます。リクエストの行の次に{{ui:graph.block.execution}}の行が、その次にサブエージェントの行が続きます。

![[agent-timeline]]

### パイプライン {#structure}
同じ切り替えで{{ui:graph.view.structure}}を選ぶと、リクエストをかんばん形式のパイプラインで表示できます。
- 表示は一度に一つです。スタイルのないペインではリクエスト一つ、スタイルを使うペインではパイプライン一つです。パイプラインはフェーズが前にだけ進むリクエストのまとまりで、前のリクエストより前のフェーズに戻ったリクエストから新しいパイプラインが始まります。
- 上部の ◀ {{ui:graph.structure.board.previous}}と ▶ {{ui:graph.structure.board.next}}で前後に移動します。その間の「リクエスト 3 / 7」のようなタイトルを押すと一覧が開き、番号、タイトル、状態、ステップを見てすぐに選べます。
- その下に状態（スタイルを使うペインではパイプライン名も）が表示されます。ステップは各列の上のチップで、チップは線でつながっています。スタイルのないペインでは{{ui:graph.structure.column.request}} → {{ui:graph.structure.column.execution}} → {{ui:graph.structure.column.result}}、スタイルを使うペインではそのモードのフェーズです。
- 内容はステップごとに横に並んだ列に入ります。列の見出しにはステップのチップとカードの数があります。スタイルのないペインでは、{{ui:graph.structure.column.request}}列に送ったリクエスト、{{ui:graph.structure.column.execution}}列に進み具合（最新のメッセージとツール呼び出し）と状態のカードとサブエージェントごとに一枚のカード、{{ui:graph.structure.column.result}}列に結果が置かれます。スタイルを使うペインでは、フェーズの列ごとにそのフェーズのリクエストカード（リクエスト、実行の要約、結果の1行）と、その下にサブエージェントのカードが置かれます。入れ子のサブエージェントは字下げされ、どのブロックの下かが書かれます。
- 状態は色でもわかります。各列の背景はそのステップの状態の色になり（完了は緑、実行中は青、待機中は黄、エラーは赤、停止はグレー）、今のステップは最新のリクエストの状態に合わせ、まだ来ていないステップは背景のままです。カードは左端に同じ色のバーがあり、背景は列より淡くなります。実行中のカードは動く枠線、待機中のカードは黄色の枠線のままで、状態の文字もそのまま表示されます。
- 長い文章は折りたたまれていて、{{ui:graph.structure.more}}を押すとその場で開きます。カードの見出しにある小さなボタンを押すと、フローでそのブロックを表示します。
- 表示は最新のものから開き、そのままにしておくと新しいリクエストやパイプラインに付いていきます。前のものを見ている間に新しいものが来ても位置はそのままで、▶ の横に{{ui:graph.structure.board.newRequest}}または{{ui:graph.structure.board.newPipeline}}の印が付きます。印を押すと最新のものへ移ります。
- 列はそれぞれ別々に動きます。列の見出しは上に残り、カードだけが上下に動きます。列の上でトラックパッドやマウスホイールを上下にスクロールするか、何もない場所をドラッグすると、その列だけが動きます。列が画面に収まらないときは、横にスクロールするかドラッグしてパイプラインを横に動かします。
- 最新のものでは、終わりを見ている列が新しい内容に付いて下がります。自分で動かした列や文章を広げた列はその位置に留まり、その列の終わりに戻るとまた付いていきます。新しいリクエストが来るとすべての列が終わりを表示し、以前のものはどの列も一番上から開きます。Windowsアプリではまだパイプライン全体が一緒に動きます。

![[agent-structure]]

### 結果カード {#result}
リクエストが終わると、結果カードに結果がまとめられます。Claudeの応答が終わるとリクエストは完了になります。そのリクエストが始めたバックグラウンドタスク（終わったサブエージェントにメッセージを送って再開させたものを含む）がまだ実行中か、終わってもClaudeがその通知にまだ応答していないあいだは、結果カードが待機色の{{ui:graph.block.awaitingNotice}}ブロックになります。これまでの応答の下に、タスクごとに{{ui:plan.background.awaitingNotice}}または{{ui:plan.background.noticeDelivered}}と、「通知 1/2」のような受け取った通知の数が表示されます。このあいだペットとペインのヘッダーは{{ui:graph.state.awaitingNotice}}を表示します。すべての通知に応答すると{{ui:graph.block.result}}に変わります。バックグラウンドタスクの通知を受けてClaudeがまた応答すると、その応答はリクエストの結果には続かず、応答ごとに新しい図が開きます([バックグラウンドタスク](approvals.md#background))。同じリクエストのほかのバックグラウンド作業がまだ実行中なら、その応答のカードのタイトルは「これまでの結果 · バックグラウンド作業 N 件を待機中」となって残りの数を示します。結果にファイルが含まれていれば、{{ui:graph.resultFiles.openButton}}ボタンで一覧を開きます。ブロック内のファイルパスをクリックするとプレビューが表示されます。最新の結果カードはウインドウのサイズに合わせて表示され、サイズを手動で変更した場合は{{ui:graph.result.fitToWindow}}ボタンが表示されます。 リクエストが変更したソースやMarkdownのファイルなら、プレビューの見出しに{{ui:files.changes.show}} | {{ui:files.changes.full}}の切り替えが付き、{{ui:files.changes.show}}から表示されます。変更された行だけを前後3行とともに、古い行番号と新しい行番号を付けて表示し、追加した行は緑、削除した行は赤の背景です。上の行が何と比べたかを示します。結果のファイル一覧やブロックから開くと、そのリクエストがファイルを最初に変更する前の内容（{{ui:files.changes.baseRun}}）、その元がなければ最後のコミットです。変更がなければ切り替えはなく、ファイル全体だけが表示されます。

![[result-card]]
