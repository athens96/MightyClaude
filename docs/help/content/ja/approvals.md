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
> 許可を求めるカードは、このMacのClaudeペイン(Always askなど)と、Codexの{{ui:permission.label.onRequest}}で表示されます。ほかの権限では、CLIの設定に従って許可またはブロックされます。

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
- 入力欄の上に、ターンは終わったもののバックグラウンドタスクが実行中であることを示す行が表示されます。{{ui:plan.background.show}}をクリックすると、タスクごとの種類、状態、経過時間を確認できます。
- Mighty表示では{{ui:graph.block.task}}ブロックとして表示されます。
- この間に送信したリクエストは、バックグラウンドタスクが終わってから始まります。すぐに割り込ませるには{{kbd:⌘Enter}}を押します。

![[background-work]]
