---
title: Mightyオーケストレーション
order: 6
section: delegation
---
Mightyオーケストレーションは、エージェントのペインが作業している間に、別の作業を隣の子ペインに任せて並行して進める機能です。子は親と同じエージェント(Claude、Codex、Antigravity)で実行され、3つのうち別のエージェントでも実行できます([CodexとAntigravity](#agents))。子ペインはそれぞれ自分のgit worktreeとブランチで作業するので互いのファイルに触れず、終わったらサイドバーのカードから結果を親のブランチにマージします。以前の名前はエージェント委任です。

子ペインを開く方法は二つあります。

- **自分で起動する**: 実行中のClaudeペイン、または子がファイルを変更できるモードのCodex・Antigravityペイン([CodexとAntigravity](#agents))に次のリクエストを書き、{{kbd:⌘Enter}}を押します。リクエストが新しいworktreeの子ペインですぐに実行されます([新しいworktreeで実行する](#worktree))。
- **Claudeに分けて任せる**: Claude(またはCodex)に、作業を分けて子ペインに任せるよう頼みます。エージェントが子を作り、レポートを読み、マージまで行います([Claudeに分けて任せる](#delegate))。

> [!warning]
> 実験的な機能です。初期状態でオンで、設定でオフにできます。まずは練習用のリポジトリで試してください。

## 全体の流れ {#overview}
自分で起動するときの流れです。

1. gitリポジトリのフォルダーをワークスペースとして開き、Claudeのペインで作業を頼みます。
2. Claudeが作業している間に次のリクエストを書き、{{kbd:⌘Enter}}を押します。実行中の作業はそのまま続き、このペインの隣に子ペインが開いてリクエストをすぐに実行します。
3. 子ペインはサイドバーで親ペインの下に一段下げて表示され、横に状態が付きます。
4. 子が作業を終えてコミットし、レポートを残すと、状態が{{ui:delegation.child.state.reported}}になります。
5. 子のカードで{{ui:delegation.card.merge}}ボタンを押し、結果を親のブランチに取り込みます。
6. マージされると、その子ペインをすぐ破棄するかを尋ねます。{{ui:delegation.mergedDiscard.confirm}}を選ぶと、子ペインとworktree、ブランチが片付けられます。マージした内容は親のブランチに残ります。

![[delegation-tree]]

| したいこと | 方法 |
|---|---|
| 今の作業のあとに続けて実行 | {{kbd:Enter}}(キューに入ります) |
| 今の作業とは別に、すぐ実行 | {{kbd:⌘Enter}}または{{ui:queue.worktree}}ボタン、またはキューの行の{{ui:queue.runNow}}ボタン |
| 結果を親のブランチに取り込む | カードの{{ui:delegation.card.merge}}ボタン |
| マージを取り消す | カードの{{ui:delegation.card.undo}}ボタン |
| 結果を捨てる | カードの{{ui:delegation.card.discard}}ボタン |

## 新しいworktreeで実行する {#worktree}
Claude・Codex・Antigravityペインの実行中にリクエストを書いて{{kbd:⌘Enter}}を押すか、停止ボタンの横にある丸い{{ui:queue.worktree}}ボタン(ブランチのアイコン)を押します。実行中の作業はそのまま続き、リクエストは専用のworktreeを持つ新しい子ペインですぐに実行されます。その間に親ペインで入力を始めていなければ、新しいペインがすぐに画面に出ます。

- 実行中のペインで{{kbd:Enter}}を押すと、リクエストはキューに入り、今の作業が終わったあとに実行されます。入力欄の案内文に、二つのキーの働きが表示されます。
- 新しいworktreeは、作れる場所でだけ使います。コミットのあるブランチをチェックアウトしたgitリポジトリである必要があります。それ以外の場所(gitリポジトリでない、まだコミットがない、ブランチではなくコミットをチェックアウトしている)では、{{kbd:⌘Enter}}は{{kbd:Enter}}と同じようにリクエストをキューに入れます。
- 子は親の最後のコミットから始まります。ブランチ`mighty/<子のid>`ができ、worktreeはプロジェクトフォルダーの外(Macは`~/.mightyclaude/worktrees/<子のid>`、Windowsは`%USERPROFILE%\.mightyclaude\worktrees\<子のid>`)にできるので、親の作業には触れません。親のフォルダーでまだコミットしていない変更は、子には渡りません。
- ペインと同じエージェントの子は、親ペインの権限モードで、親ペインのモデルと{{ui:composer.effort.label}}で実行します。すべてのリクエストをプランモードで始めるスタイルでは、プランモードで始まります。親と同じ表示で開き、親がマイティ表示でスタイルを使っていたなら同じスタイルで始まります(スタイルの進み具合は子の最初のリクエストから別に数えます)。添付ファイルも一緒に渡ります。
- 最初のメッセージは入力したテキストが先に来て、その後にアプリが、子がどこで作業するかを伝え、終わったらコミットしてREPORT.mdを書くよう添えます。そうするとカードからマージできます。スラッシュコマンド(`/`で始まるテキスト)は入力したとおり単独で実行され、アプリの案内は子ペインの次のリクエストとして続くため、コマンドの引数になりません。
- {{platform:mac}} 別のエージェントでリクエストを実行するには、{{ui:queue.worktree}}ボタンを右クリック(またはControlクリック)して、Claude、Codex、Antigravityから選びます。{{kbd:⌘Enter}}とふつうのクリックは、ペインと同じエージェントのままです。グレーの項目には実行できない理由が出ます。CLIがインストールされていないかサインインしていない(設定 › CLIで確認)、ペインのモードが許す範囲でファイルを変更できるモードがない、自分で始めた子がすでに3つ開いている、のいずれかです。別のエージェントの子は、ファイルを変更でき、ペインのモードが許さないことはしない自分のモードのうち一番広いモードで始まり([CodexとAntigravity](#agents))、そのエージェントの新しいペインが受け取るモデルと{{ui:composer.effort.label}}を使い、ペインと同じ表示で開きますが、スタイルは引き継ぎません。その子を作れないか始められないときは、テキストと添付ファイルは入力欄に戻り、会話に理由が書かれます。ペイン自身には送りません。ペインのエージェントはそのモードで子を持てないが別のエージェントなら持てるときは、ボタンはそのまま表示され、クリックでメニューが開き、{{kbd:⌘Enter}}はリクエストをキューに入れます。
- {{platform:windows}} 別のエージェントでリクエストを実行するには、{{ui:queue.worktree}}ボタンを右クリック(またはボタン上でShift+F10)して、Claude、Codex、Antigravityから選びます。{{kbd:Ctrl+Enter}}とふつうのクリックは、ペインと同じエージェントのままです。ほかは上のMacと同じです。グレーの項目には実行できない理由が出ます。別のエージェントの子は、ペインのモードが許さないことをしない自分のモードのうち最も広いモードで始まり、そのエージェントの新しいペインが受け取るモデルと{{ui:composer.effort.label}}を使い、子を起動できなかったテキストは入力欄に戻ります。別のエージェントの子しか使えない場所では、クリックでメニューが開き、{{kbd:Ctrl+Enter}}は以前どおりリクエストをキューに入れます。
- すでにキューにあるリクエストも、その行の{{ui:queue.runNow}}ボタンで同じように送れます。エージェントは{{platform:mac}} 右クリック(またはControlクリック)、{{platform:windows}} 右クリック(またはShift+F10)で選びます。その子を作れない、または始められないときは、リクエストはキューの先頭に戻り、会話に理由が書かれます。
- 子ペインは同じエージェントのふつうのペインと同じように使えます。承認カードに答え、追加のリクエストを送り、権限モードを変えられます。
- 自分で起動した子について、親ペインのClaudeは通知を受け取りません。親が任せた作業ではないからです。親のツールもこの子を一覧に出さず、マージも追加の指示もできません(`human_started`)。サイドバーで状態を確かめ、カードのボタンを自分で使ってください。
- 開いておける子は親ペイン一つにつき3つまでで、ディスクの空きが10 GB必要です。終わった子ペインを閉じるか破棄すると空きができます。
- 子を作れないとき、または作った後で起動できなかったとき(開いている子がすでに3つある、ディスクが足りない、worktreeや最初の実行が失敗したなど)は、ペインの会話に理由が残ります。そのとき、テキストは{{kbd:Enter}}を押したときと同じようにそのペインの次のリクエストになります。キューがいっぱいなら入力欄に戻ります。起動できなかった子は、カードから破棄するまで一覧に残ります。
- 子ペインやMightyオーケストレーションをオフにしたペインでは、{{kbd:⌘Enter}}も{{kbd:Enter}}と同じようにキューに入れます。スマートフォンから実行中のペインに送った内容も、常にキューに入ります。人が送った内容が実行中のタスクに割り込むことはありません。

## Claudeに分けて任せる {#delegate}
ClaudeかCodexのペイン(親)が作業を分けて、ほかのペイン(子)に任せることもできます。別のエージェントを指定しなければ、子は親と同じエージェントです。子がレポートすると、親が通知を受け取ってレポートを読み、結果をマージするかどうかを決めます。ClaudeとCodexの親は、MacアプリとWindowsアプリの両方で使えます。

1. gitリポジトリのフォルダーをワークスペースとして開き、Claudeのペインを開きます。このペインが親です。
2. 「delegateツールでMightyの子ペインを二つ開いて、この作業を分けて任せて」のように、ツール名を入れて頼みます。「分けてやって」とだけ言うと、Claudeが自分の中のサブエージェントで済ませることがあります。
3. 子ペインは親の隣に開き、サイドバーでは親の下に一段下げて表示されます。

- 親が使うツールは`delegate`(任せる。`agent`で子のエージェントを`claude`、`codex`、`antigravity`から指定できます)、`list_children`(子の一覧)、`child_status`(状態とレポートを読む)、`merge`(マージ)、`follow_up`(追加の指示)です。`discard`(破棄)は親には使えず、カードからあなただけが行えます。
- 子ごとにブランチ`mighty/<子のid>`とworktree(Macは`~/.mightyclaude/worktrees/<子のid>`、Windowsは`%USERPROFILE%\.mightyclaude\worktrees\<子のid>`)ができます。子は親の最後のコミットから始まります。
- 任せた作業はTASK.mdに、結果はREPORT.mdに書かれます。どちらもworktree内のgitが無視するフォルダーにあり、コミットされません。
- 子の権限モードは親が選んだモードで、親のペインのモードより広くできません。別のエージェントの子は、親のモードがしないことはできません。親のモードが確認せずにファイルを変更するとき(編集承認モードかautoモードのClaude、{{ui:permission.label.onRequest}}か{{ui:permission.label.acceptEditsCodex}}モードのCodex、{{ui:permission.label.acceptEdits}}モードのAntigravity、{{ui:permission.label.fullAccess}}モードのどのエージェントも)だけ確認せずにファイルを変更し、親のモードがファイルを変更できるときだけ確認したうえでファイルを変更し、親のモードが確認せずにシェルコマンドを実行するとき(autoモードのClaude、{{ui:permission.label.defaultCodex}}か{{ui:permission.label.acceptEditsCodex}}モードのCodex、{{ui:permission.label.fullAccess}}モードのどのエージェントも)だけ確認せずに実行し、{{ui:permission.label.fullAccess}}の下でだけフルアクセスを持ちます。そのため、シェルコマンドの前に確認するClaudeの編集承認モードの下では、Codexの子は{{ui:permission.label.acceptEditsCodex}}ではなく{{ui:permission.label.onRequest}}になり、プランモードか手動モードのClaudeのペインはCodexやAntigravityの子をまったく持てません。広げられるのはあなただけで、実行と実行の合間に、子ペインの入力欄の権限メニューかスマートフォンの設定から行います。
- 子ペインは親ペインと同じ表示({{ui:graph.view.default}} | {{ui:graph.view.mighty}}、マイティ表示なら{{ui:graph.view.diagram}}, {{ui:graph.view.timeline}}, {{ui:graph.view.structure}}のうち同じもの)で開きます。親がマイティ表示の{{ui:graph.view.structure}}を使っていたなら、子も{{ui:graph.view.structure}}で表示されます。
- 親は子の承認カードに答えられません。子ペインにカードが出たら、そのペインであなたが答えてください。
- 一つの親が`delegate`ツールで作って開いておける子は3つまでで、自分で起動した子の3つとは別に数えます。どちらも相手を妨げません。子はさらに委任できません。レポート済みやマージ済みの子も、ペインが開いていれば3つに入るので、終わった子ペインを閉じると空きができます。ディスクの空きが10 GB未満だと委任は断られます。
- 親は通知で子のレポートを知り([通知と保留](#notices))、`follow_up`で追加の指示を送り([追加の指示](#follow-up))、`merge`でマージします([マージ](#merge))。

## CodexとAntigravity {#agents}
MightyオーケストレーションはCodexとAntigravityのペインでも使えます。既定では、子は親のエージェントで実行されます。Codexペインの子はCodexペイン、Antigravityペインの子はAntigravityペインです。

どのエージェントも3つのエージェントのどれの子でも持てます。{{ui:queue.worktree}}ボタンのメニューから選ぶか([新しいworktreeで実行](#worktree))、ClaudeかCodexの親が`delegate`の`agent`(`claude`、`codex`、`antigravity`)で指定します。子は下に書いた自分のエージェントのルールで動きます。モードも、作業を誰がコミットするかも(Claudeの子は自分でコミットし、CodexとAntigravityの子の作業はアプリがコミットします)子のエージェントに従います。通知とマージは親のエージェントに従います。`delegate`は、知らないエージェント(`agent_unknown`)、インストールされていないかサインインしていないエージェント(`agent_unavailable`)、子のエージェントのモードでないか、そのモードではファイルを変更できないか、親のモードより多くのことができるモードを拒否します(モードの比べ方は[Claudeに分けて任せる](#delegate)にあります)。

- **子が作業できるモード**: 子は親の権限モードで始まるため、CodexとAntigravityのペインは、子がファイルを変更できるモードでだけ{{kbd:⌘Enter}}で子を起動します。Codexは{{ui:permission.label.acceptEditsCodex}}・{{ui:permission.label.onRequest}}・{{ui:permission.label.fullAccess}}、Antigravityは{{ui:permission.label.acceptEdits}}・{{ui:permission.label.fullAccess}}です。それ以外のモードでは、{{kbd:⌘Enter}}はこれまでどおりリクエストをキューに入れます。
- **Codex**のペインに委任ツールが付くのは{{ui:permission.label.acceptEditsCodex}}と{{ui:permission.label.fullAccess}}のときだけです。{{ui:permission.label.defaultCodex}}ではCodexがツールを断り、{{ui:permission.label.onRequest}}ではアプリが表示できない方法で確認を求めるためです。`delegate`は、ファイルを変更できない子のモードを断ります。Codexが`merge`を呼べるのは、実行が{{ui:permission.label.fullAccess}}で始まったときだけで、それ以外は断られる(`merge_human_only`)ので、カードからマージします。実行中のCodexのターンはテキストを受け取れないため、実行中のCodexの親への通知は、その実行が正常に終わったあと次の実行として届き、停止やエラーで終わったときは保留されます。
- **Antigravity**のペインには委任ツールがありません(Antigravityはグローバル設定からしかツールを受け取らず、アプリはその設定を変えません)。子は{{kbd:⌘Enter}}か{{ui:queue.worktree}}ボタンでだけ起動します。
- CodexとAntigravityの子は、{{ui:permission.label.fullAccess}}でなければ自分のworktreeでコミットできないので、変更をそのまま残すよう案内されます。子の実行がREPORT.mdを変えて正常に終わると、アプリが変更を子のブランチにコミットし(`mighty: <子のid> report r<n>`)、そのコミットで報告を記録します。停止やエラーで終わったときは、次の正常な終了を待ちます。.gitignoreが除く新しいファイル、秘密情報に見える新しいファイル(`.env*`、`*.pem`、`*.key`、`*.pfx`、`*.p12`、`*.ppk`、`id_rsa*`、`id_ed25519*`、`id_ecdsa*`、`id_dsa*`、`credentials*`、`secrets.*`、`.npmrc`、`.pypirc`)、5 MBを超える新しいファイルはコミットせず、後の二つは子ペインに名前を表示します。コミットにはgitに設定した名前とメールアドレスを使い(ないと失敗し、ペインにそう表示します)、署名はしません。worktreeに未解決のマージ競合があるときは何もコミットせず、ペインにそう表示します。
- カードのマージが競合で断られたとき、競合の解決を頼むのはコミットできる子({{ui:permission.label.fullAccess}})だけです。それ以外は、子を{{ui:permission.label.fullAccess}}に切り替えてから子ペインで頼むか、そのworktreeのターミナルで自分で競合を解決してコミットしてください。
- リポジトリのサブフォルダーをワークスペースとして開いた場合、Codexの子はREPORT.mdがあるworktreeの最上位フォルダーにも書き込めて、Antigravityの子にはそのフォルダーが追加されます。

## 子の状態 {#states}
サイドバーに子ごとの状態が表示されます。

| 状態 | 意味 |
|---|---|
| {{ui:delegation.child.state.creating}} | worktreeとペインを作っています。 |
| {{ui:delegation.child.state.running}} | 作業しています。 |
| {{ui:delegation.child.state.waiting}} | 承認や質問へのあなたの答えを待っています。 |
| {{ui:delegation.child.state.reported}} | REPORT.mdを残して実行を終えました。マージできます。 |
| {{ui:delegation.child.state.merged}} | 親のブランチにマージされました。 |
| {{ui:delegation.child.state.ended}} | 新しいレポートなしで実行が終わりました。 |
| {{ui:delegation.child.state.interrupted}} | アプリを終了したため、実行が途切れました。 |
| {{ui:delegation.child.state.failed}} | 子ペインを開始できませんでした。 |
| {{ui:delegation.child.state.closed}} | ペインが閉じられました。破棄するまで一覧に残ります。 |

子が新しい実行を始めると{{ui:delegation.child.state.reported}}の状態は外れ、もう一度レポートすると次のレポート番号が付きます。子がレポートなしで終わったときは、子ペインで、コミットしてREPORT.mdを書くようにもう一度頼んでください。

## マージ {#merge}
子の結果を親のブランチに取り込む方法は二つあります。

1. **カードの{{ui:delegation.card.merge}}ボタン**: あなたが押します。自分で起動した子は、この方法でだけマージします。早送り(fast-forward)できればそうし、できなければマージコミットをちょうど一つ作ります。先に衝突を調べ、断ったときは理由をカードに表示します。
   マージが通ると、その子ペインをすぐ破棄するかを尋ねます。{{ui:delegation.mergedDiscard.confirm}}を選ぶと子ペインを閉じ、worktreeとブランチを削除します。マージした内容は親のブランチにそのまま残りますが、そのマージは取り消せなくなります。{{ui:delegation.mergedDiscard.later}}を選ぶとカードはそのまま残ります。
   競合で拒否されると、アプリが子ペインに、親ブランチを取り込んで競合を解決し、コミットしてもう一度報告するよう頼みます。カードにそう表示され、子は{{ui:delegation.child.state.running}}になります。子がもう一度報告したら、{{ui:delegation.card.merge}}ボタンをもう一度押してください。この依頼は報告ごとに一度だけ送られます。子ペインが今すぐ実行を始められないときは送らず、カードには拒否の理由だけが残り、次にマージするときにもう一度試します。依頼を受け取っても開始できなかったときや、子がプランモードで計画を立てただけで終わったときは、子ペインに直接、競合を解決してもう一度報告するよう頼んでください。CodexとAntigravityの子には、{{ui:permission.label.fullAccess}}のときだけ依頼します([CodexとAntigravity](#agents))。
2. **親の`merge`ツール**: Claudeに任せた子だけで使えます。早送りだけを行います。親は`child_status`で得た子のheadを一緒に渡します。子がまだレポートしていない、子か親のフォルダーにコミットしていない変更がある、子のheadが動いた、親のブランチがその間に先へ進んだ(分岐)、または親のブランチがチェックアウトされていない場合は断られ、何も変わりません。このツールで承認カードが出るかどうかは、親のペインの権限モードによります。Codexの親は実行が{{ui:permission.label.fullAccess}}で始まったときだけ使え、ほかのモードでは`merge_human_only`で断られるので、カードからマージします。

> [!tip]
> 子を一つマージすると親のブランチが先へ進むので、残りの子はふつう`merge`ツールで分岐として断られます。そのときはカードの{{ui:delegation.card.merge}}ボタンでマージしてください。

マージしたあと、子が新しい実行を始めるまでは、ペインを閉じてもカードに{{ui:delegation.card.undo}}ボタンが残ります。親のブランチがそのマージのまま、親のペインが待機中で、コミットしていない変更がないときだけ使えます。取り消すと、ファイルはマージ前の状態に戻り、ペインが開いている子はもう一度{{ui:delegation.child.state.reported}}になります。

> [!note]
> マージはできたのに委任の記録を保存できなかったときは、カードがそう知らせます。マージはそのままで、記録は次の保存で書き直されます。

## 破棄 {#discard}
カードの{{ui:delegation.card.discard}}ボタンは、子のworktreeとブランチを、まだマージしていない作業ごと削除します。破棄できるのはあなただけで、親にはできません。マージした子は、マージの直後に尋ねるウィンドウからも破棄できます([マージ](#merge))。

- 確認ウィンドウ({{ui:delegation.discard.title}})は、子のworktreeの中に入っていて一緒に削除されるほかのworktreeをすべて示します。
- その一覧を確認できないときは、確認ウィンドウに破棄ボタンは出ず、何も止めたり削除したりしません。

> [!tip]
> アプリがまだマージしていない作業を勝手に消すことはありません。マージしていない作業が消えるのは、あなたが押す破棄のときだけです。

## ペインを閉じる・後片付け {#close}
- **マージ済みの子ペインを閉じると**、作業がすべてマージ済みで未コミットの変更が残っていなければ、アプリがworktreeとブランチを片付けます(`git branch -d`、強制削除はしません)。条件に合わなければペインだけが閉じ、worktreeは残ります。
- **マージする前に子ペインを閉じると**、アプリではもうマージできません。カードには破棄だけが残り、ブランチとworktreeは破棄するまでそのまま残ります。
- **開いている子がある親のペインを閉じると**、デスクトップアプリはまず確認します({{ui:delegation.parentClose.title}})。閉じるのは親のペインだけで、子は作業を続けます。
- 親ペインが閉じられた子は、サイドバーの{{ui:delegation.tree.parentClosed}}の項目の下にカードとして残ります。その子の通知はカードに保留されて件数が表示され、追加の指示はもう受け取りません。
- 開いている子がある、または片付けていない子のworktreeが残っているワークスペースは削除できません。
- {{platform:windows}} 子のworktreeの中にジャンクションやシンボリックリンク(リンクで作った`node_modules`など)があると、マージ済みの子ペインを閉じてもアプリはworktreeを片付けずに残します。リンクをたどってリンク先のフォルダーまで消してしまわないためです。この場合はカードの{{ui:delegation.card.discard}}ボタンで削除します。破棄はリンクそのものだけを先に外し、リンク先のフォルダーには触れません。

![[delegation-parent-closed]]

## 通知と保留 {#notices}
Claudeに任せた子がレポートしたときと、開始できなかったときに、親へ通知が一度届きます。親が始めた実行(最初に任せた作業や追加の指示)がレポートなしで終わったときにも届きます。通知は`[Mighty Claude notice …]`で始まる短い案内にすぎず、レポートの中身は親が`child_status`で読みます。自分で起動した子は通知を送りません。

- 親がClaudeで実行中なら、通知はその実行にそのまま届き、親のMighty表示にオレンジ色の{{ui:graph.block.delegationNotice}}ブロックとして表示されます。親が応答を終えたあとに届いた通知も同じブロックで表示され、それに対するClaudeの応答は親のリクエストの結果には続かず、独立した{{ui:graph.block.delegationNotice}}の図として開きます。実行中のCodexの親は、その実行が正常に終わったあと、次の実行として受け取ります。
- 親がアプリを開いたあとに実行を正常に終えて待機しているなら、通知が次の実行を始めます。
- それ以外では通知は保留されます。停止のあと、エラーのあと、アプリを開き直したあと、CLIの更新中などです。すでに保留された項目があると、新しい通知もその後ろに保留され、順番が守られます。

保留された通知は、親の入力欄の上にあるキューのいちばん上に、ピンの印が付いた行として表示されます。この行には削除ボタンがなく、ふつうのキューのリクエストと違って、停止してもアプリを開き直しても残ります。

![[delegation-held]]

- 次に何かを送ると、保留された項目がすべて、古い順に、あなたの文より前に付いて一つの実行で届きます。
- いちばん上の保留行の{{ui:queue.runNow}}ボタンを押すと、保留された項目すべてを一つの実行で、すぐにこのペインへ送ります。ほかの保留行にはボタンがありません。新しいworktreeを開く{{ui:queue.runNow}}は、保留された項目をこのペイン向けに保留したままにします。項目が保留されている間は、本来なら実行中のClaudeにそのまま届けられるリクエストも、それらが送られるまでグレーのままです。
- 通知はアプリを開き直しても、ちょうど一度だけ届きます。保留された行はスマートフォンのキューにも表示され、スマートフォンから送っても一緒に届きます。

## 追加の指示 {#follow-up}
親は`follow_up`ツールで、Claudeに任せた子に追加の指示を2回まで送れます。追加の指示は通知と同じ決まりで子ペインに届き、一つあたり48 KiBまでです。あなたは子ペインの入力欄に直接書けばよく、回数の制限はありません。

## オンとオフ {#enable}
{{platform:mac}} オフにしない限りオンです。設定の{{ui:settings.nav.general}}タブで{{ui:settings.delegation.toggle}}を切り替えます。アプリを開き直さなくても、次に始まる実行から反映されます。同じスイッチがdefaultsキー`delegation.enabled`なので、ターミナルで次のコマンドを実行してもオフにでき、`defaults delete dev.mightyclaude.native delegation.enabled`でオンに戻ります:

```sh
defaults write dev.mightyclaude.native delegation.enabled -bool false
```

{{platform:mac}} 以前のバージョンはこのキーを設定したときだけオンになり、オフにするには`defaults delete …`を使っていました。今はそうするとオンのままになるので、オフのままにするには設定で{{ui:settings.delegation.toggle}}をオフにしてください。

{{platform:windows}} オフにしない限りオンです。設定の{{ui:settings.nav.general}}タブで{{ui:settings.delegation.toggle}}を切り替えます。アプリを開き直さなくても、次に始まる実行から反映されます。同じスイッチが`HKEY_CURRENT_USER\Software\MightyClaudeNative`の下の`delegation.enabled`の値(REG_DWORD)で、0ならオフ、値がないかほかの値ならオンです。そのため、コマンドプロンプトまたはPowerShellで次のコマンドを実行してもオフにでき、`reg delete "HKCU\Software\MightyClaudeNative" /v delegation.enabled /f`でオンに戻ります:

```
reg add "HKCU\Software\MightyClaudeNative" /v delegation.enabled /t REG_DWORD /d 0 /f
```

{{platform:windows}} 以前のバージョンは、その値を1にしたときだけオンでした。そのときの1が残っていれば、オンのままです。

- オンにすると、ClaudeかCodexのペインで新しい実行を始めるたびに委任ツールが付きます。すでに動いている実行には付きません。
- オフにすると外れるのはツールだけで、{{kbd:⌘Enter}}は{{kbd:Enter}}と同じようにリクエストをキューに入れます。既存の子ペイン、カード、後片付けはそのまま動きます。

## 上限と対応範囲 {#limits}
| 項目 | 上限 |
|---|---|
| 一つの親が開いておける子 | 自分で起動して3つ、`delegate`で3つ |
| 委任の深さ | 一段(子はさらに子を作れない) |
| 一つの子への追加の指示(`follow_up`) | 2回、一つあたり48 KiB |
| 任せる作業(TASK.md、自分で起動したリクエスト) | 64 KiB |
| 必要なディスクの空き | 10 GB |

- 子は親と同じエージェントで実行され、別のエージェントでも実行されます([CodexとAntigravity](#agents))。ClaudeとCodexのペインはツールと{{kbd:⌘Enter}}で、Antigravityのペインは{{kbd:⌘Enter}}でだけ子を作ります([CodexとAntigravity](#agents))。
- MacアプリもWindowsアプリも初期状態でオンで、設定にスイッチがあります([オンとオフ](#enable))。スマートフォンに新しい画面はなく、保留された行はスマートフォンのキューにも表示されます。
