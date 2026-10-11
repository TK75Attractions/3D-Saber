# Mac 当日用ゲーム監視 (台 A / B)

Unity の **Tools > PhoneSaber > Build > macOS Player**（または File > Build Profiles）で macOS Player をビルドし、`Start-Saber-A.command` / `Start-Saber-B.command` の `GAME_APP` をその `.app` に合わせてダブルクリックします。既定は `Builds/Mac/3D-Saber.app`（上のメニューの出力先と同じ）。Editor を開いたままでも、プロジェクトを別フォルダへ複製して `Unity -batchmode -quit -projectPath <複製> -executeMethod PhoneSaberPlayerBuild.BuildMac` でビルドできる。空白・日本語のパスにも対応します。Editor と Player、複数の launcher を同じ PC で同時に起動しないでください。

- `open -W -n ... --args -phonesaberStation A` / `B` で待機し、異常終了なら5秒後に再起動。Terminal に再起動回数を表示し、同じフォルダの `Start-Saber-A.log` / `B.log` に時刻・終了コード・回数を追記します（1 MiB を超えたら起動時に `.log.1` へ回す）。
- 二重起動防止: この PC で別の launcher（A/B どちらでも。受信 port が同じため）が動いている、または `3D-Saber.app` が既に起動している場合は、理由を表示して起動しません（ログに `not-started already-running` / `game-already-running`）。前回 Ctrl+C で監視だけ止めた場合は、先にゲームを Quit してください。
- **今回の変更を含む Unity build が必要です。** `open -W` はアプリの終了コードを返さないため、Unity の `OnApplicationQuit` が書く一時的な正常終了マーカーも確認します。正常 quit + open 終了0なら監視終了。印がない crash / 強制終了は `exit-code=1` (推定)として再起動します。`open-exit-code` もログに残します。
- ハング監視: launcher は run ごとに `-phonesaberHeartbeat <一時ファイル>` を渡し、Player の main thread が約1秒ごとに更新します。**一度も更新が無い旧 build は強制終了しません**。一度確認後、mtime が45秒以上止まると `hang detected` を記録し、今回の引数を持つ実ゲーム PID を強制終了して crash として再起動します（`open -W` の PID はゲームではありません）。数秒のシーン読込みを誤検知しない余裕として45秒にしています。45秒を超える負荷は会場用 build で確認してください。`PHONESABER_HANG_SECONDS` で秒数変更、`0` で無効化。例えば Terminal で `PHONESABER_HANG_SECONDS=0 ./Start-Saber-A.command`。Finder 起動でも使う場合は `.command` の watchdog 呼出し前に `export PHONESABER_HANG_SECONDS=0` を置きます。STOP がある間は強制終了しません。
- P2P bridge が予期せず終了したときは、両色の受信機が動作中なら自動再試行します。待機は2 / 5 / 15 / 60秒（以後60秒、1分動作した後は2秒へ戻る、監視周期による約1秒の誤差あり）。受信機停止 / Player終了で再試行も停止。既存の `PHONESABER_P2P_BRIDGE=0` を **Player 側**に設定すると自動起動を無効化します（`open` はこの環境変数を自動転送しないので、必要なら `saber-watchdog.sh` の `OPEN_ENV` 設定ブロックの後に `OPEN_ENV+=(--env PHONESABER_P2P_BRIDGE=0)` を追加）。Mac Status は Player の port 所有者も認識し、`~/Library/Logs/DefaultCompany/ServTechSlash/Player.log` を読みます。
- スタッフの通常終了はゲームの Quit。監視だけ止めるなら Terminal の **Ctrl+C**、またはこのフォルダに空の `Start-Saber-A.STOP` / `B.STOP` (両台なら `STOP`) を作成。STOP は今のゲームを強制終了せず、終了後の再起動を止めます。次回の運用前に STOP を削除してください。STOP が残ったまま起動すると、ゲームを起動せずにそのファイルの場所を表示して止まります（ログに `not-started STOP-file`）。
- ゲームの F8 (必要なら Fn+F8) に台名・受信状況・永続イベントログの場所が出ます。ログは `Application.persistentDataPath/PhoneSaber/events.log` と4世代、各1 MiBまで。OSスリープ防止・バックグラウンド受信はゲーム起動時に設定します。
- built `.app` の iPhone P2P 予備経路: launcher（`saber-watchdog.sh`）が repo の `ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py` を見つけると、`open --env PHONESABER_P2P_BRIDGE_SCRIPT=…` で自動的に渡します（ログに `p2p-bridge=yes`、無ければ `no`）。既に環境変数があればそれを優先。見つからなければ LAN（Bonjour / 手動 IP）だけで動きます。初回は bridge の Swift build に数十秒かかることがあるので、前日に一度起動しておく。Android は同じ LAN で探索 / 手動 IP。

接続・発熱・障害対応は [当日 runbook](../docs/claude/EVENT_DAY_RUNBOOK.md)。

今回の実機確認項目と検証の制約は [運用強化記録](../docs/claude/ops-hardening-20261011.md)。
