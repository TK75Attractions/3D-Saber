# Mac 当日用ゲーム監視 (台 A / B)

Unity の **Tools > PhoneSaber > Build > macOS Player**（または File > Build Profiles）で macOS Player をビルドし、`Start-Saber-A.command` / `Start-Saber-B.command` の `GAME_APP` をその `.app` に合わせてダブルクリックします。既定は `Builds/Mac/3D-Saber.app`（上のメニューの出力先と同じ）。Editor を開いたままでも、プロジェクトを別フォルダへ複製して `Unity -batchmode -quit -projectPath <複製> -executeMethod PhoneSaberPlayerBuild.BuildMac` でビルドできる。空白・日本語のパスにも対応します。Editor と Player、複数の launcher を同じ PC で同時に起動しないでください。

- `open -W -n ... --args -phonesaberStation A` / `B` で待機し、異常終了なら5秒後に再起動。Terminal に再起動回数を表示し、同じフォルダの `Start-Saber-A.log` / `B.log` に時刻・終了コード・回数を追記します。
- **今回の変更を含む Unity build が必要です。** `open -W` はアプリの終了コードを返さないため、Unity の `OnApplicationQuit` が書く一時的な正常終了マーカーも確認します。正常 quit + open 終了0なら監視終了。印がない crash / 強制終了は `exit-code=1` (推定)として再起動します。`open-exit-code` もログに残します。
- スタッフの通常終了はゲームの Quit。監視だけ止めるなら Terminal の **Ctrl+C**、またはこのフォルダに空の `Start-Saber-A.STOP` / `B.STOP` (両台なら `STOP`) を作成。STOP は今のゲームを強制終了せず、終了後の再起動を止めます。次回の運用前に STOP を削除してください。
- ゲームの F8 (必要なら Fn+F8) に台名・受信状況・永続イベントログの場所が出ます。ログは `Application.persistentDataPath/PhoneSaber/events.log` と4世代、各1 MiBまで。OSスリープ防止・バックグラウンド受信はゲーム起動時に設定します。
- built `.app` の iPhone P2P には `PHONESABER_P2P_BRIDGE_SCRIPT` で利用可能な bridge launcher を指定する必要があります。未設定なら同じ LAN + Bonjour / 手動 IP で接続します。Android は同じ LAN で探索 / 手動 IP。

接続・発熱・障害対応は [当日 runbook](../docs/claude/EVENT_DAY_RUNBOOK.md)。
