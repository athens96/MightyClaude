---
title: リモート接続（Tailscale）
order: 10
section: remote
---
Mac または Windows のコンピュータ 2 台の Mighty Claude を [Tailscale](https://tailscale.com/kb/1017/install) でつなぐと、一方でもう一方のコンピュータのワークスペースを開き、そのコンピュータで Claude・Codex・Antigravity とコマンドを実行できます。ファイルと CLI のログインは実行するコンピュータに置いたままで、プロジェクトのファイルをコピーや同期はしません。

## 準備 {#prepare}
- 2 台のコンピュータに Mighty Claude と Tailscale をインストールし、同じ tailnet にログインします。
- 作業を任せるコンピュータ（ホスト）に、使う CLI をインストールしてログインします。リモート実行はホストの CLI とアカウントを使います。
- ルーターのポート転送や Tailscale Serve・Funnel は不要です。tailnet のポリシーが 2 台の機器の接続を妨げていないことだけ確認してください。

## このコンピュータを共有 {#share}
ホストのコンピュータで:
1. {{ui:menu.settings}} → {{ui:settings.remote.navTitle}} タブを開きます。
2. {{ui:settings.remote.shareTitle}} で共有するローカルワークスペースを選びます。ポートは通常そのまま（43137）にします。
3. {{ui:settings.remote.startSharing}} を押します。このコンピュータの Tailscale アドレスと接続キーが表示されます。{{ui:settings.remote.copyButton}} でコピーして、接続する側に伝えます。

共有中は {{ui:settings.remote.allowFullAccess}} スイッチが表示されます。オフのあいだ（既定）は、接続したコンピュータはリモートコマンドのペインを使えず、エージェントをフルアクセスで実行することもできません。そうしたリクエストは実行前に断られ、そのペインに理由が表示されます。共有を開始するたびにオフに戻ります。

共有はこのコンピュータの Tailscale アドレスでだけ接続を受け付けます。アプリを開き直すと共有は必ずオフで始まります。{{ui:settings.remote.stopSharing}} を押すとリモート実行が止まり、接続キーは破棄されます。もう一度共有するときは新しいキーを伝えてください。

> [!warning]
> 接続キーを持つ機器は、共有したワークスペースでエージェントとコマンドを実行できます。権限モードによっては、ホストのユーザー権限でフォルダの外にもアクセスできます。キーは信頼できる機器にだけ教えてください。

## 別のコンピュータに接続 {#connect}
作業を頼むコンピュータで:
1. {{ui:menu.settings}} → {{ui:settings.remote.navTitle}} タブを開きます。サイドバー見出しのネットワークボタンや、{{ui:workspace.addPane}} メニューの {{ui:remote.menu.connect}} からも同じ画面が開きます。
2. {{ui:settings.remote.connectTitle}} にホストのアドレス（Tailscale の IP または MagicDNS 名）と接続キーを入れ、{{ui:settings.remote.connectButton}} を押します。ホストがポートを変えていなければ、アドレスだけで足ります。
3. 接続したコンピュータの下に共有中のワークスペースが表示されます。{{ui:settings.remote.openButton}} を押すと、リモートワークスペースとしてサイドバーに追加されます。

リモートワークスペースはサイドバーでコンピュータのアイコンとホスト名で見分けられ、ワークスペースの見出しと下のステータスバーに接続状態が表示されます。アプリを開き直しても接続は保存されたまま残り、リモートペインで最初に送信したときか {{ui:settings.remote.refreshButton}} を押したときに再接続します。

{{platform:mac}} 接続キーは macOS のキーチェーンにだけ保存し、ワークスペースの記録には入れません。再接続のときに macOS がキーチェーンへのアクセスを尋ねたら「常に許可」を選んでください。アプリは同じ署名で配布されるので、この選択はアップデート後も保たれます。

{{platform:windows}} Windows でも接続キーをワークスペースの記録には入れず、この Windows ユーザーだけが読めるよう暗号化してアプリのデータフォルダに保存します。共有するときは {{ui:remote.windows.firewall.add}} ボタンで、Tailscale の範囲からの接続にだけアプリのポートを開くファイアウォールの規則を 1 つ追加します。このとき Windows の管理者の確認が 1 回表示されます。規則がすでにあるときは、ボタンが {{ui:remote.windows.firewall.update}} になります。この規則はスマートフォンの直接接続と共用なので、{{ui:settings.nav.mobile}} タブからも同じ規則を変更できます。

## リモートペインを使う {#use}
- エージェントペインはローカルのペインと同じように使えます。モデル・権限モード・思考の強さ・Fast モードはそのままホストに伝わり、ホストの CLI が対応していない設定は実行前に知らせます。
- 権限・質問・計画のカードはこのアプリに表示され、答えはホストの実行に届きます。実行中の Claude のリクエストに追加することもできます（{{platform:mac}} {{kbd:⌘Enter}}、{{platform:windows}} {{kbd:Ctrl+Enter}}）。
- {{ui:graph.view.default}} 表示と {{ui:graph.view.mighty}} 表示（{{ui:graph.view.diagram}}・{{ui:graph.view.timeline}}・{{ui:graph.view.structure}}）は、ローカルの実行とまったく同じように描かれます。
- {{ui:remote.menu.newCommand}} のペインは、コマンドごとにホストの共有フォルダで新しいシェルを開きます。対話型プログラムとパスワード入力には対応していません。
- リモートペインは、ステータスバーとセッション情報に実行コンピュータの OS（macOS・Windows）を表示します。{{ui:remote.menu.newCommand}} のペインは、Mac のホストではログインシェル、Windows のホストでは PowerShell でコマンドを実行します。
- 添付したファイルは内容そのものがホストに送られます。一度に 8 個、1 ファイル 5 MiB、合計 8 MiB までです。

リモートワークスペースでは次のものを使えません。どれもこのコンピュータのディスクやネットワークを読むためです。
- [Mightyオーケストレーション](delegation.md)（子のワークツリー）
- ファイルペインとブラウザペイン
- プロジェクトのファイルを読む[スタイル](styles.md)、ステータスライン、以前のセッションの再開

リモートワークスペースは[スマートフォンのリモート](phone.md)には表示されません。

## 接続が切れたとき {#disconnect}
- ネットワークが切れると、実行中のリモートペインはエラーで終わり、接続の一覧に理由が表示されます。ホストは応答が途絶えた実行を約 30 秒以内に自分で止めます。
- {{ui:settings.remote.disconnectButton}} はその接続の実行を止め、接続情報は残します。もう一度使うには {{ui:settings.remote.refreshButton}} を押すか、リモートペインから送信します。
- ホストが共有をやり直した場合は新しいキーが必要です。同じアドレスに新しいキーで接続すると、保存された接続のキーが置き換わります。
- ホストのコンピュータがスリープ中か、そこで Mighty Claude が終了しているあいだは、新しい作業を送れません。
