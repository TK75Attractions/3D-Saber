# 当日運用の強化（2026-10-11）

中断された WIP `9033b00` / `7c045fc` を引き継いでレビューし、現コードで検証を再実行した。比較元は `0d8e411`。
実装・回帰テストを維持し、今回は追加 commit / push と公式 verify を行わず、オペレーターのレビューへ渡す。

## 修正した不具合（重大度順、行番号は修正後）

| 重大度 | 場所 | 具体的な失敗と修正 |
|---|---|---|
| 高 | `mac/saber-watchdog.sh:93`、`windows/Start-Saber-A.bat:41` / `B.bat:41`、`windows/saber-watchdog.ps1:90` | Player の main thread が固まっても process が生きているので再起動しなかった。run ごとの heartbeat を監視し、初回確認後45秒止まったら `hang detected` を記録して実 Player を終了、crash として通常の再起動へ進む。Mac は `open -W` の PID ではなく、app 内の実行パスと今回固有の引数を確認する。Windows は起動時に得た process handle を使う。 |
| 高 | `windows/Start-Saber-A.bat:33` / `B.bat:33`、`windows/saber-watchdog.ps1:21` | スタッフがもう一度ダブルクリックすると2つ目の Player が起動し、UDP 5005/5006 を bind できなかった。A/B 共通の mutex と排他 file handle を再起動待ちの間も保持し、既存 `3D-Saber.exe` も確認。拒否時は日本語と Enter 待ち。異常終了で残った lock ファイルだけでは起動を妨げない。 |
| 中 | `Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberP2PBridgeProcess.cs:174`、`PhoneSaberBridgeRetryPolicy.cs:12` | P2P bridge が非0終了すると同じ Player では永久に再起動せず、Editor 再起動を案内していた。両受信機が動作中なら2 / 5 / 15 / 60秒、以後60秒で再試行。1分動作した後の失敗は2秒へ戻す。受信機停止・Dispose・Editor reload / Play終了で取消。警告は Player でも使える案内に変更。 |
| 中 | `ios/PhoneSaberSender/Tools/phone_saber_status.py:260,354` | 正常に受信する `ServTechSlash` を他アプリと誤判定し、古い Editor.log を読み、スタッフに Play を押すよう案内していた。product name または `3D-Saber.app/Contents/MacOS/` の実行パスで Player を認識し、5005/5006/5007 を確認。Player 所有時は Player.log を読み、起動・復旧の案内も両方に対応する。既存の Unity 接頭辞判定は保持。 |

heartbeat は明示引数 `-phonesaberHeartbeat <path>` がある Player だけに runtime 初期化で生成し、Editor では生成しない。
main thread の Update から約1秒ごとに stream / buffer を再利用して書く。書込みの例外はゲームへ伝播させない。
Windows は開いた handle の mtime を `SetFileTime` でも更新する。シーン・prefab の設定は不要だが、**Player の再ビルドは必要**。

45秒は数秒のシーン読込み・一時負荷を越える余裕を取った値で、会場 build の最長停止時間を実測した値ではない。
初回 heartbeat のない旧 build・初期化中の停止は強制終了しない。STOP がある間も強制終了しない。
正常 Quit、STOP、非0終了の5秒後再起動という既存の操作は維持する。

## 無効化・変更

- ハング監視は `PHONESABER_HANG_SECONDS=0` で無効、正の整数で秒数を変更（既定45、不正値は45）。Mac は `.command` の watchdog 呼出し前に `export PHONESABER_HANG_SECONDS=0`、Windows は bat の先頭に `set "PHONESABER_HANG_SECONDS=0"`。Player へ直接引数を渡さなければ heartbeat component も生成しない。
- bridge の起動・再試行は既存の `PHONESABER_P2P_BRIDGE=0` で無効。Mac の `open` 経由では Player に変数を渡す必要があり、`saber-watchdog.sh` の `OPEN_ENV` 設定ブロック後に `OPEN_ENV+=(--env PHONESABER_P2P_BRIDGE=0)` を置く。
- 二重起動ロックの無効化スイッチは設けない。前回の lock ファイルの手動削除は不要。

## 実行した検証

- `python3 -B -m unittest -v PhoneSaber/tools/test_ops_watchdog.py`: 12件中7件 PASS、Windows 実行5件は skip。Mac の5件は `/bin/bash` 3.2、偽 open / ps、実子プロセスと実 kill を使用。空白・日本語・括弧・`!` のあるパスで、hang→再起動（quit marker があっても crash）、初回 heartbeat なし、0で無効、STOP、継続 heartbeat / 正常 Quit を確認。ps を偽にしたのは sandbox が実 process 一覧の取得を拒否するため。
- Tools ディレクトリで `python3 -B -m unittest -v test_phone_saber_status.py`: 46件 PASS。修正前ソースでは Player / 短縮名 / log 選択 / 案内の4件が assertion failure、修正後は PASS。
- 比較元 `0d8e411` の Mac hang 回帰は再起動せず timeout、Windows のソース契約2件も FAIL（Git 保存時の LF を checkout と同じ CRLF に戻した上で、Lock 呼出し / helper 不在を確認）。Windows の契約テストは文字コード・呼出し・排他/終了判定の静的確認であり、二重起動の実行証明ではない。
- Unity **6000.3.9f1** 同梱 Roslyn と実 Unity / InputSystem / NUnit / TestRunner DLL を参照し、PhoneSaber runtime、InputPoint、SwingEventStream と7つの関連 Editor test ファイルをコンパイル。Editor macOS / Windows、macOS Player / Windows Player は PASS。検証用に無関係な `UdpImuBridge.TryGetLatest` だけ stub を使用。Unity API の stub は使用していない。
- Unity 同梱 Mono で `PhoneSaberOpsPolicyTests` の5件を実行し PASS。1秒周期、書込みエラー、2/5/15/60秒と上限・1分境界・reset、定常1,000書込みの managed allocation **0 byte** を確認。Editor 内の Test Runner 実行ではない。
- bridge の処理コードはそのまま、Unity 外で使えない native log 出力だけ Console に置換した host harness を Mono で実行。偽 Python bridge の exit 7→自動再起動、非所有者拒否、Stop 後の再試行停止、AutoStartEnabled=false は PASS。修正前では同じ exit 7 の回帰が FAIL。sandbox で pkill の process 一覧取得は失敗するが、直接の process Kill による停止は成功。Swift compiler 子プロセスの cleanup は実証していない。
- `/bin/bash -n`（watchdog と A/B command）、`git diff --check`: PASS。bat は UTF-8 BOMなし、ps1 は BOM付き、両方 CRLF をバイト単位で確認。

compile / Mono harness は前回作業の `/private/tmp/phonesaber-ops/` を読み直し、`compile_api.py`、現ソースから再コンパイルした `Runner.cs`、`check_bridge.py` を再実行した。bridge の比較元も `0d8e411` に固定して回帰の FAIL を確認した。生成物は repo に追加しない。
認識（ranking / eligibility / scoring / PCA / threshold）、座標 UDP 本文 `x1,y1,x2,y2`、formal corpus、Unity scenes / prefabs / .meta / ProjectSettings には差分なし。

## 実機で必須の確認・残るリスク

1. 新 Player の起動後に heartbeat の mtime が毎秒進むこと。Mac は本物の `open -W -n` と実 PID 判定、Windows は **開いたファイルの mtime** を確認する。シーン遷移・非フォーカスでも継続し、通常 Quit では再起動しないこと。
2. テスト用 Player の main thread を停止して45秒後に1回だけ再起動すること。Mac は bridge が残らないこと、crash ダイアログの「再度開く」を押さないこと。旧 build・0・STOP の時は強制終了しないこと。heartbeat を初めて書く前の hang は対象外。初回確認後にディスク障害で更新できなくなった場合は hang と同じ判定になる。
3. Windows は `python -B -m unittest -v PhoneSaber.tools.test_ops_watchdog.WindowsWatchdogIntegrationTests`（repo root、Python と .NET Framework compiler が必要）を実行。その後 built Player でも A→A / A→B、再起動待ち中、監視だけ閉じてゲームが残った状態、監視を強制終了した後の古い lock を確認する。空白・日本語・括弧・`!`・`%` を含む配置、Ctrl+C / コンソールを閉じる操作、`< nul`、実際の非0終了コードも確認する。
4. Mac の実 bridge を一度終了し、両受信機が動作中なら自動復旧、受信機停止なら再起動しないこと。Editor では reload / Play終了後に bridge と timer が残らないこと。Player 所有時に Mac Status が正しい Player.log を読むこと。

Windows / PowerShell の実行、Unity Editor / Test Runner、built Player のビルド・起動、iPhone P2P 実接続、公式 `verify_phone_saber.sh` は未実行。
Windows の終了コード0の致命的ダイアログ経路、Mac の crash ダイアログによる手動再起動との競合、長いシーン読込みは従来レビューのリスクとして残す。
追加の quit marker 判定など操作結果が変わる案は実機確認後の提案に留め、今回実装しない。
