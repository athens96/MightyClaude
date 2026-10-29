---
title: 許可・質問・サインインの復旧
order: 5
section: approvals
---
エージェントが作業中にユーザーの判断を必要とすると、ペインにカードが表示されます。カードに答えると作業が続きます。同じリクエストは、作業状況、デスクトップペット、スマートフォンでも確認できます。

## 権限の許可 {#permission}
エージェントが権限の範囲外の作業(コマンドの実行、ファイルの変更など)をしようとすると、許可を求めるカードが表示されます。

![[permission-card]]

1. カードを読んで、何をしようとしているのかを確認します。コマンドやファイルパスは枠内に表示され、正確な入力内容は元のJSONで確認できます。
2. {{ui:permission.allowOnce}}または{{ui:permission.deny}}をクリックします。

許可はこのリクエスト1回にだけ適用されます。今後も常に許可するルールは作成しません。

> [!note]
> 許可を求めるカードは、このMacのClaudeペイン(Always askなど)と、Codexの{{ui:permission.label.onRequest}}で表示されます。ほかの権限では、CLIの設定に従って許可またはブロックされます。Antigravityは動き方が違うため、[別の方法で確認します](#antigravity-permission)。

## Antigravityの権限カード {#antigravity-permission}
Antigravityは作業の途中でアプリに確認できません。確認が必要な操作(コマンドの実行、ファイルの書き込みなど)に当たると、その操作だけを飛ばしてターンを終え、拒否した操作をまとめて1枚のカードで尋ねます。権限が{{ui:permission.label.default}}のときも表示され、スマートフォンにも同じカードが表示されます。

カードには、拒否された操作がすべて並びます。操作ごとに、種類({{ui:antigravity.permission.kind.command}}、{{ui:antigravity.permission.kind.url}}、{{ui:antigravity.permission.kind.file}}など)、コマンド・アドレス・パス、許可に使うルールが表示されます。

1. 何が拒否されたかを確認します。
2. ボタンを選びます。
   - {{ui:permission.allowOnce}}: 拒否された操作だけを許可するルールをAntigravityに一時的に追加し、同じ会話を続けます。続きの実行が終わると(完了、失敗、停止、アプリの終了のいずれでも)、アプリが追加したルールだけを削除します。
   - {{ui:antigravity.permission.allowAlways}}: 同じルールをAntigravityの設定(`~/.gemini/antigravity-cli/settings.json`の`permissions.allow`)に残します。次回からは確認なしで実行されます。Windowsでは`%USERPROFILE%\.gemini\antigravity-cli\settings.json`です。
   - {{ui:antigravity.permission.deny}}: カードを閉じ、何も続けて実行しません。

許可すると、同じ会話で続きが進みます。ペインが別の作業で忙しいときは、続きの実行はキューに入ります。

ルールは先頭から一致するものを許可します。コマンドのルールは**そのテキストで始まるすべてのコマンド**を許可し(`command(git status)`は`git status --short`も許可します)、フォルダーを指すファイルのルールはその中のすべてのファイルを許可します。{{ui:permission.allowOnce}}のルールもAntigravityの全体設定に入るため、ルールがある間は**このコンピューターのすべてのAntigravity実行**(ほかのペインやワークスペース、ターミナルの`agy`)に適用されます。常に許可したコマンドのルールはその後に続くどんな引数も許可するため、引数で別のことをさせられるコマンド(`find … -exec`、`curl … -o`など)ではできるだけ{{ui:permission.allowOnce}}を選んでください。

コマンドをつなぐ(`;`、`&&`、`||`)、パイプ(`|`)・リダイレクト(`>`、`<`)・コマンド置換(バッククォート、`$(`)・バックグラウンド(`&`)を使う、またはデータを消したり権限を上げたりするコマンド(`rm`、`sudo`、`git push --force`、`git reset --hard`、`git clean`、`kill`など)は{{ui:permission.allowOnce}}しかできません。カードの{{ui:antigravity.permission.allowAlways}}はオフになり、理由が表示されます。スマートフォンではそのボタンは表示されません。

`*`や括弧を含むコマンド、ディスク全体(`/`)やホーム(`~`)を指すルールは作りません。Antigravity自身の設定フォルダー(`~/.gemini`)やアプリのデータフォルダーの中、またはその上のフォルダー(ホームフォルダーなど)へのファイルのルールも作りません。そこにルールがあるとAntigravityが自分の権限を書き換えられるからです。`.gemini`に触れるコマンド、単独で、またはコードを直接渡して起動するシェルやインタープリター(`bash`、`python3 -c …`、`node -e …`、`cmd /c …`)、ほかのコマンドを実行するラッパー(`npx`、`xargs`、`timeout`、`env`など)も{{ui:permission.allowOnce}}しかできません。`command(bash)`は`bash -c`で始まるものを何でも許可してしまうからです。カードには理由が表示されます。そのような操作はルールで許可できない操作として表示されます。

Claudeとは違う点があります。Claudeは実行の前に止まって答えを待ちますが、Antigravityはそのターンを終えてから尋ね、許可されると続きを進めます。そのため、カードが表示された時点で残りの作業はすでに終わっていることがあります。

> [!note]
> ルールで許可できない操作もカードに表示され、権限モードを変えるよう案内が出ます。そのような操作だけのカードには{{ui:antigravity.permission.deny}}しかありません。Antigravityの設定ファイルが読めない、またはJSONでない場合、アプリはファイルを変更せず、理由をカードに表示します。アプリは`permissions.allow`のルールだけを追加・削除し、ほかの設定はそのままにします。一時的に追加したルールを削除できなかったときは、少しあとにもう一度削除し、次にアプリを開いたときにも削除します。

[リモートワークスペース](remote.md)のAntigravityペインは、そのペインを実行しているコンピュータの設定を書き換えます。カードごとの許可は一度しか使えず、{{ui:antigravity.permission.allowAlways}}はそのコンピュータで{{ui:settings.remote.allowFullAccess}}がオンのときだけ受け付けます。

## 質問に答える {#questions}
Claudeが選択肢を示して質問すると、{{ui:phone.questionnaire.title}}カードが表示されます。

![[question-card]]

1. 質問を読んで答えを選びます。複数選択できる質問もあります。
2. 当てはまる答えがない場合は、{{ui:phone.questionnaire.custom}}に直接書きます。
3. 質問が複数ある場合は、{{ui:phone.questionnaire.next}}ボタンで次に進みます。
4. すべて答えたら、{{ui:phone.questionnaire.submit}}をクリックします。選ぶだけでは送信されません。

## サインインが切れたとき {#login-recovery}
CLIのサインインの有効期限が切れてリクエストが失敗すると、入力欄の上にサインインを復旧するカードが表示されます。
1. {{ui:loginRecovery.loginButton}}をクリックします。
2. ブラウザが開いたら、サインインを承認します。ブラウザにコードが表示された場合は、カードに貼り付けて{{ui:loginRecovery.codeSubmit}}をクリックします。
3. サインインが完了すると、失敗したリクエストが自動的に再送信されます。

すでにほかの場所でサインインしている場合は、{{ui:loginRecovery.resendButton}}をクリックしてください。ブラウザでのサインインが難しい場合は、{{ui:loginRecovery.terminalButton}}を使います。

> [!tip]
> {{ui:settings.nav.cli}}タブの{{ui:settings.cliAccounts.autoLoginToggle}}をオンにしておくと、サインインが切れた時点ですぐに再サインインを始めます。

## バックグラウンドタスク {#background}
Claudeは、時間のかかるコマンドやサブエージェントをバックグラウンドで実行することがあります。この場合、Claudeの応答が終わっても作業が残っています。
- 入力欄の上に{{ui:plan.background.title}}の行が表示されます（標準表示ではターンが終わった後、Mighty表示では最初から）。このリクエストが始めたタスクのうち、終わった数とまだ実行中の数が「終了 1件 · 実行中 2件」のように表示されます。{{ui:plan.background.show}}をクリックすると、タスクごとの種類、状態、経過時間を確認できます。
- タスクが終わると Claude に通知が届き、Claude はその結果を受けて続けて答えます。終わったタスクは終了した状態に変わり、次のリクエストを送るまでこの行に残ります。すべて終わると、行は「実行中 0件」と表示されたまま残ります。
- 実行中のバックグラウンドタスクはリクエストのブロックには描かれず、上の行にだけ表示されます。行をクリックすると、実行したコマンド（サブエージェントはプロンプトの1行目）と出力の最後の数行（最大6行）が開きます。スマートフォンでも、Mighty表示の上に同じ2つの数を示す折りたたまれた行があり、開くとまだ実行中のタスクの行が表示されます。
- タスクが終わって通知がClaudeに届いても、リクエストには何も追加されません。Claudeの応答ごとに、新しい図が開きます。タイトルは終わった作業の名前から付き、「{{ui:graph.background.noticeRun}} · コードレビューエージェント」のようになります。複数が一緒に終わったときは「… ほか N 件」と短くなります。図には終わったタスクの{{ui:graph.block.task}}ブロック（コマンド、終わり方、要約）とClaudeの応答が表示されます。これはリクエストではないので、リクエスト番号は数えません。フローでは一番下の新しいカード、{{ui:graph.view.timeline}}では新しい行として、{{ui:graph.view.structure}}では最後に新しいカード（ボード）として付きます。何も名前のない通知は、Claudeが応答しない限り図を作りません。
- バックグラウンドで始めたサブエージェントも同じです。終わるとMighty表示のブロックが{{ui:graph.state.completed}}、{{ui:graph.state.error}}、{{ui:graph.state.stopped}}に変わり（終わる前にアプリが再起動した場合も{{ui:graph.state.stopped}}）、エージェントが返した最終報告がブロックの結果になります。実行中に送ってきたメッセージはブロックの記録に追加されるだけでブロックは終わりません。そのメッセージへのClaudeの応答は、別の{{ui:graph.background.messageRun}}の図に表示されます。同じエージェントが続けて送ったメッセージとその応答は、同じ図に続けて表示されます。終了の通知に対するClaudeの応答は、{{ui:graph.background.noticeRun}}の図に表示されます。
- この間に送信したリクエストはキューに入り、バックグラウンドタスクが終わってから始まります。今すぐ始めるには停止をクリックします。キューはそのまま残り、最初のリクエストが続けて始まります。[Mightyオーケストレーション](delegation.md#worktree)がオンで、gitリポジトリにあるペインでは、{{kbd:⌘Enter}}で新しいworktreeですぐ実行することもできます。
- この行は非表示にできません。バックグラウンドタスクがある間は常に表示され、行を開いたままにしたかどうかは記憶されます。

![[background-work]]
