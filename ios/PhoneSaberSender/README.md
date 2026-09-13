# PhoneSaberSender 同一試行の遅延計測

## 実機手順

1. Macで `run_debug.command` をFinderからダブルクリックします。UDP 5005/5006の診断受信、Bonjour `_phonesaber._udp` 公開、ブラウザの状態画面を開始します。診断受信中はUnityを停止してください（同じUDPポートを二つのプロセスで安全に共有できません）。
2. iPhoneとMacを同じWi-Fi、またはiPhone Personal Hotspotへ接続します。初回はiPhoneのカメラと「ローカルネットワーク」アクセスを許可します。iPhoneはBonjourでMacを見つけ、画面のNetwork欄にMac名とIPを表示します。
3. iPhoneで「通常送信を開始」を押します。自動発見ができない場合だけ、手動IP欄にMacのIPv4を入力してください。送信形式は従来どおり赤=UDP 5005、青=UDP 5006、`x1,y1,x2,y2` です。

```sh
open /Users/satoshi/縁日/GitHub/school-festival/start_phone_saber_latency.command
```

ライブ画面にはMacが受信した各packetの受信Unix時刻、iPhone `ts`、`(Mac受信時刻 - iPhone ts) × 1000`、色、受信順、赤青別の件数と最新値が表示されます。差分はMacとiPhoneの時計差を含み、iPhone内部処理時間や表示時刻との照合値とは別の値です。tsなし・不正・非有限値は未計測として表示され、受信件数には含まれますが有効差分件数には含まれません。

停止はprobeのターミナルでCtrl-Cです。停止時はprobe自身のUDPソケットとHTTPサーバーを閉じます。UnityのPlay Modeや別probeを強制終了しません。UDPまたはHTTPポート競合時は起動エラーを確認し、Unity・別probe・別のライブ画面を停止してから再試行してください。二重起動で既存プロセスを自動終了させることはありません。

保存ログを使う従来の終了後照合は、`python3 -B udp_receive_probe.py --display-log /tmp/saber-display-log.json` のように従来CLIを直接起動して利用できます。無指定CLI、`run`呼び出し、`ts=`/`timestamp=`と座標だけの解析、終了後のtrial照合も保持しています。

時計はMacとiPhoneで自動設定をオンにし、計測直前に両端末の時刻を確認します。時計オフセットとドリフト、自動時刻設定の限界、ブラウザ描画開始と発光の差、液晶走査、露光・ローリングシャッター、検出処理待ち、遠近歪みが誤差要因です。`ts=`の時刻はiPhoneがpayloadを生成した時刻で、露光時刻でもMac受信成功時刻でもありません。

## 定義

座標は左上原点、右方向x・下方向yのピクセル座標です。probeは入力寸法で正規化し、端点順を逆にしても同じとする平均最近傍距離を `display_coordinate_distance` として計算します。`--reject-threshold` 以下だけを採用し、閾値超過、不正stateId/trialId、非有限値、曖昧な候補、表示試行区間外のpacketは未対応です。同じ `(trialId, color, stateId)` は最初の有効packetだけを採用し、後続の保持packetを次stateへ付け替えません。

`display_to_phone_ms = (phone_timestamp - displayEpochMs / 1000) * 1000` です。括弧内の差は秒なので、出力ミリ秒では1000倍します。統計は有効な一意標本だけを対象に、全体・赤・青の件数、平均、中央値、線形補間p50/p95を表示します。標本ゼロは `count=0` と `-` で表示します。

検出数は新鮮な赤/青検出の件数、送信試行数はその検出からUDP送信を開始した件数、ローカル完了数は送信completionが現世代で成功した件数です。これはMacの受信数・照合数ではありません。`lastLocalSendMs` はiPhone内部の処理開始からcompletionまでです。

## 静止画回帰テスト

macOSのSwiftツールチェーンだけで、本番の `detectSaber` を行パディング付きBGRA静止画へ適用する回帰テストを実行できます。カメラ、UDP、iOS Simulatorは使用しません。

```sh
cd /Users/satoshi/縁日/GitHub/school-festival
bash ios/PhoneSaberSender/run-static-tests.sh
```

このテストは赤/青の水平・垂直・正負斜め、同時表示、画面周辺、非正方形画像、反射・離れたノイズ、無検出を、端点距離・軸角度・長さの明示的な許容値で検証します。XCTestのBGRAケースはさらに `FrameProcessor` と `CameraViewModel` の端点・送信payload経路を確認します。合成静止画は実機カメラ精度の保証ではありません。

## レビュー修正後の検証記録（2026-09-11）

- 静止画テストは bash ios/PhoneSaberSender/run-static-tests.sh で14検査を実行し、終了コード0・全件成功を確認しました。スクリプトは自身の場所を基準に解決するため、別の作業ディレクトリから絶対パスで実行でき、空白を含むパスも引用して扱います。専用 mktemp ディレクトリとmodule-cacheを終了時（コンパイル失敗時を含む）に全体削除し、リポジトリ内に生成物を残しません。
- bash -n ios/PhoneSaberSender/run-static-tests.sh と xcrun swiftc -frontend -parse ios/PhoneSaberSender/PhoneSaberSender/*.swift ios/PhoneSaberSenderTests/*.swift は終了コード0です。
- xcodebuild build-for-testing は終了コード0（TEST BUILD SUCCEEDED）です。xcrun simctl list devices available は CoreSimulatorService の Connection refused で終了コード1、実在UDIDを取得できないため XCTest の xcodebuild test は実行不能でした。build成功をXCTest成功とは扱っていません。実機iPhone、実カメラ映像、実Mac受信も未検証です。

`lastLocalSendMs` は同じ単調時計で `(completedAt - processingStart) * 1000` とした、iPhone内部の最新の送信成功標本です。送信completion直後に公開値が更新され、次フレームやMac受信を待つ値ではありません。

## 再レビュー修正の検証（2026-09-11）

- 正常実行: `cd /Users/satoshi/縁日/GitHub/school-festival && bash ios/PhoneSaberSender/run-static-tests.sh` は終了コード0、14検査すべて成功しました。
- 別作業ディレクトリ: `cd /private/tmp && bash '/Users/satoshi/縁日/GitHub/school-festival/ios/PhoneSaberSender/run-static-tests.sh'` は終了コード0、14検査成功。空白を含むパス: `space_root=$(mktemp -d); mkdir -p "$space_root/tree with spaces/ios"; cp -R ios/PhoneSaberSender "$space_root/tree with spaces/ios/PhoneSaberSender"; cp -R ios/PhoneSaberSenderTests "$space_root/tree with spaces/ios/PhoneSaberSenderTests"; (cd "$space_root/tree with spaces" && bash 'ios/PhoneSaberSender/run-static-tests.sh')` は終了コード0、14検査成功でした。スクリプトは自身のディレクトリを基準に解決し、cwdと空白を含むパスに依存しません。
- 一時ディレクトリ: `trap` で正常終了・コンパイル失敗の双方に `mktemp` のビルドディレクトリを削除する実装を確認しました。正常終了後は専用一時ディレクトリが削除され、リポジトリ内に生成物はありません。コンパイル失敗時のcleanupはコード上の対応であり、この環境では故意のコンパイル失敗を起こす実行までは未実施です。
- `bash -n ios/PhoneSaberSender/run-static-tests.sh` と `xcrun swiftc -frontend -parse ios/PhoneSaberSender/PhoneSaberSender/*.swift ios/PhoneSaberSenderTests/*.swift` は終了コード0です。XCTest実行と実機カメラ確認は、既存記録どおりSimulatorサービス不通・実機未接続のため未実施です。

## 今回の検証記録（2026-09-10、再レビュー対応）

- 着手前比較コピー: `/private/tmp/task-20260910143433-rerun/before/`。これは今回の着手直前に取得した記録で、過去に存在した比較コピーを後付けしたものではありません。確認できたstatus/diffと対象ファイルのみを比較根拠にします。
- 実行コマンドと結果: `python3 -B -m unittest test_udp_receive_probe -v`（終了コード0、22件、成功16・skip6）、`python3 -B -m unittest discover -v`（終了コード1、probe 22件は成功16・skip6、対象外camera/native系4モジュールはcv2/numpy不足でimport error）、py_compile（終了コード0）、HTML parser/script block静的確認（終了コード0）、`xcrun swiftc -frontend -parse ios/PhoneSaberSender/PhoneSaberSender/*.swift ios/PhoneSaberSenderTests/DetectionCoreTests.swift`（終了コード0）です。
- `xcodebuild build-for-testing -project ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -destination 'generic/platform=iOS Simulator' -derivedDataPath /private/tmp/task-20260910143433-rerun/xcode/DerivedData CODE_SIGNING_ALLOWED=NO` は終了コード0（TEST BUILD SUCCEEDED、警告あり）でした。`xcrun simctl list devices available` は終了コード1、CoreSimulatorService `Connection refused` で実在UDIDを取得できませんでした。実在UDIDがないため今回の `xcodebuild test` は未実施です。Simulator Busyの成功扱いではありません。
- UDP integration testのskip 6件はすべて `PermissionError: [Errno 1] Operation not permitted` による一時UDP bind権限制限です。コード不具合や一般OSErrorをskip理由にはしていません。ブラウザ実表示、実機iPhone、Unity Editorは未実施です。
- 今回の再実行ログは `/private/tmp/task-20260910143433-rerun/logs/`（probe単体、discover、py_compile）に保存しました。`node` は利用できないためJavaScript実行検査は未実施です。
- ログ保存先は実機手順では `/tmp/saber-display-log.json`。今回の自動テストは合成ログを一時ディレクトリへ保存し、実機ログや過去ログは結果に再利用していません。

## 再レビュー追補（2026-09-10 15:16 JST）

今回の修正後に実行したコマンドと結果は次の通りです。コマンド出力は `/private/tmp/task-20260910143433-rerun2/logs/` に保存しました。

```sh
cd /Users/satoshi/縁日/GitHub/school-festival
python3 -B -c 'import py_compile,tempfile; from pathlib import Path; d=tempfile.TemporaryDirectory(); [py_compile.compile(p,cfile=str(Path(d.name)/(Path(p).name+"c")),doraise=True) for p in ("udp_receive_probe.py","test_udp_receive_probe.py")]; d.cleanup()'
python3 -B -m unittest test_udp_receive_probe -v
python3 -B -m unittest discover -v
xcrun swiftc -frontend -parse ios/PhoneSaberSender/PhoneSaberSender/*.swift ios/PhoneSaberSenderTests/DetectionCoreTests.swift
python3 -B -c 'from html.parser import HTMLParser; from pathlib import Path; p=HTMLParser(); p.feed(Path("saber_camera_test.html").read_text(encoding="utf-8")); p.close(); print("HTML parse OK")'
xcodebuild build-for-testing -project ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -destination 'generic/platform=iOS Simulator' -derivedDataPath /private/tmp/task-20260910143433-rerun2/DerivedData CODE_SIGNING_ALLOWED=NO
xcrun simctl list devices available
```

py_compile、probe単体テスト、Swift parse、HTML parserは終了コード0です。probe単体テストは23件中16成功・7 skipで、skipは全て `[Errno 1] Operation not permitted` のUDP bind権限制限（コード失敗や一般OSErrorではありません）です。全体 `discover -v` はprobeの同結果に加え、対象外のcamera/nativeモジュールでcv2/numpy不足のimport errorとなり終了コード1です。build-for-testingは終了コード0（`TEST BUILD SUCCEEDED`）です。`simctl list devices available` はCoreSimulatorServiceのConnection refusedで終了コード1となり、実在UDIDが得られないためxcodebuild testは未実施です。Simulator Busyを成功・skipとは扱っていません。

今回参照した着手前status/diff記録と対象ファイルの比較コピーは `/private/tmp/task-20260910143433-rerun/before/` です。比較対象の既存記録に `CameraViewModel.swift` の着手前コピーが含まれていないため、同ファイルについては今回の着手直前 `git status --short` と `git diff --stat`、および作業対象の現在差分を確認可能な根拠とし、過去結果を着手前内容として再利用していません。HTML/Swift/buildの今回ログはそれぞれ `/private/tmp/task-20260910143433-rerun2/logs/html-parse.log`、`/private/tmp/task-20260910143433-rerun2/logs/swift-parse.log`、`/private/tmp/task-20260910143433-rerun2/logs/xcodebuild-build-for-testing.log` です。

実runの赤青既知標本、非ゼロ閾値境界、拒否後の有効packet、異なる時刻の重複で最初のpacketを採用する検査、Ctrl-C時の標本集計・再bindをテストコードへ追加しましたが、今回の環境ではUDP bind権限制限によりskipです。ブラウザのfile://表示、実機iPhone、Unity Editor、実Mac受信は未検証です。

## 再レビュー指摘への追補（2026-09-11）

- `test_udp_receive_probe.py` のライブ回帰テストは、`webbrowser.open` のモック実行中にライブHTTPへ接続してHTTP 200を確認します。probe終了後のHTTPへ接続する検査は残していません。終了後はUDP/HTTPポートを再bindし、`webbrowser.open` が `False` を返す起動失敗でもcleanup後に再bindできることまで検証します。
- 同テストは既知の受信時刻より10秒未来の `ts` を103件送信し、保持された負の `arrivalMinusPhoneMs`、累積の赤103件・青2件、履歴上限100件、履歴の受信順（order 6から105）、色別最新orderを確認します。別の青packetではtsなしを送り、無効tsが未計測として保持されることも確認します。
- 実行コマンド: `python3 -B -m unittest test_udp_receive_probe -v`（2026-09-11実行、30件中18成功・12 skip、終了コード0）。この環境ではUDP bind権限制限によりライブソケット系テストはskipされるため、上記のモックHTTP確認部分を含む実ソケット経路は実行されません。これは外部のソケット権限制限であり、テストコードの不具合によるskipではありません。HTMLParserによるHTML確認はJavaScript実行やブラウザ表示の成功を意味しません。

## 再レビュー対応追補（2026-09-10）

- 変更一覧: `test_udp_receive_probe.py` はCtrl-Cテストの一時ログ寿命をrun・統計assert・再bindまで延長し、実run既知標本を赤 `1/10 ms`・青 `5.5 ms` に固定しました。さらに全体・色別の件数、平均、中央値、p50、p95を明示検証し、入力幅1000で距離 `.02` の採用、`.021` の拒否、拒否後の有効packet採用を検証する送信列にしました。閾値比較は `1e-12` の表現誤差だけを許容します。`saber_camera_test.html` は保存時に試行を停止・固定し、固定済みtrialのstate集合と終了時刻を再保存で書き換えないようにしました。保存前の一時停止→継続は従来どおり可能で、保存後は「新しい試行」からのみ続行します。
- 正確な再検証コマンドと結果: `cd /Users/satoshi/縁日/GitHub/school-festival && python3 -B -m unittest test_udp_receive_probe -v`（終了コード0、23件、成功16・skip7）。7件のskipはすべて `PermissionError: [Errno 1] Operation not permitted` によるUDP bind権限制限で、コード不具合や一般OSErrorによるskipではありません。`python3 -B -c 'import py_compile,tempfile; from pathlib import Path; d=tempfile.TemporaryDirectory(); [py_compile.compile(p,cfile=str(Path(d.name)/(Path(p).name+"c")),doraise=True) for p in ("udp_receive_probe.py","test_udp_receive_probe.py")]; print("py_compile OK"); d.cleanup()'`（終了コード0）、`python3 -B -c 'from html.parser import HTMLParser; from pathlib import Path; p=HTMLParser(); p.feed(Path("saber_camera_test.html").read_text(encoding="utf-8")); p.close(); print("HTML parse OK")'`（終了コード0）、`git diff --check`（終了コード0）も成功しました。
- 実run統合テスト（既知統計、閾値境界、Ctrl-C再bind）は上記bind権限制限で実行不能です。これは環境による未実施であり、テストコードの構文・単体ロジック検証成功とは区別します。file://ブラウザ表示、保存→継続→再保存のDOM実操作、実機iPhone、Unity Editor、実Mac受信も未検証です。`node` が利用できないためJavaScriptエンジン実行検査も未実施です。
- 今回のログ保存先: `/private/tmp/task-20260910143433-rerun3/logs/`（単体テスト、py_compile、HTML parser、diff check）。実機手順の表示ログ保存先は引き続き `/tmp/saber-display-log.json` です。今回の結果に過去ログ・実機ログは再利用していません。

## task-20260911151641 再レビュー修正の検証記録（2026-09-11）

着手前記録は存在します。今回の着手直前に取得した `git status --short` は `/private/tmp/task-20260911151641-before/git-status.txt`、`git diff --stat` は `/private/tmp/task-20260911151641-before/git-diff-stat.txt`、対象ファイルの比較コピーと差分は `/private/tmp/task-20260911151641-before/`（`git-diff.patch` を含む）です。後から取得した過去コピーやDerivedDataは着手前記録として使用していません。着手前status/diffを再照合し、対象外の既存変更を保存したまま、今回の追加変更は `udp_receive_probe.py`、`test_udp_receive_probe.py`、このREADMEの3ファイルだけに限定しました。

今回の追加変更一覧は、保存ログなしのライブpacket履歴を最大100件に制限して受信順・色別件数を独立カウンター化したこと、HTML存在/読取とHTTP 200確認後のブラウザ起動・`webbrowser.open`失敗時の明示的エラー・UDP/HTTP cleanup・IPv4 loopback限定、live=Trueの合成赤青UDP・負値/tsなし/非有限ts・API反映・履歴上限・ブラウザ起動順・HTML欠落・HTTP競合cleanupの回帰テスト追加です。

- `python3.12 --version` は Python 3.12環境を確認しました。
- `python3.12 -B -c 'import py_compile,tempfile; from pathlib import Path; d=tempfile.TemporaryDirectory(); [py_compile.compile(p,cfile=str(Path(d.name)/(Path(p).name+"c")),doraise=True) for p in ("udp_receive_probe.py","test_udp_receive_probe.py")]; print("py_compile OK"); d.cleanup()'` は終了コード0です。
- `python3.12 -B -m unittest test_udp_receive_probe -v` は終了コード0、30件中成功18・失敗0・skip12です。skipは全て `PermissionError: [Errno 1] Operation not permitted` によるUDP/HTTPソケットbind禁止で、実live=True経路、停止後再bind、HTTP競合解放を実ソケットでは実行できませんでした。
- `python3.12 -B -m unittest discover -v` は終了コード0、77件中成功65・失敗0・skip12です。skip理由は上記と同じソケットbind禁止です。
- `python3.12 -B -c 'from html.parser import HTMLParser; from pathlib import Path; p=HTMLParser(); p.feed(Path("saber_camera_test.html").read_text(encoding="utf-8")); p.close(); print("HTML parse OK")'` は終了コード0です。`node --version` は終了コード127で利用不能のためscript抽出後の `node --check` は未実施です。
- `xcrun swiftc -frontend -parse ios/PhoneSaberSender/PhoneSaberSender/*.swift ios/PhoneSaberSenderTests/*.swift` は終了コード0です。`bash ios/PhoneSaberSender/run-static-tests.sh` は終了コード0、14件成功・失敗0・skip0です。
- `bash -n start_phone_saber_latency.command` は終了コード0、実行属性も確認しました。別cwd・空白を含むパスでの実行は終了コード2（ソケットbind禁止）でした。失敗時のUDP/HTTP cleanupは実装経路で確認しました。
- `xcodebuild build-for-testing -project ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -destination 'generic/platform=iOS Simulator' -derivedDataPath /private/tmp/task-20260911151641/DerivedData CODE_SIGNING_ALLOWED=NO` は終了コード0（`TEST BUILD SUCCEEDED`）です。これはXCTest実行成功や実機検証を意味しません。

ブラウザ自動開始と受信後1秒以内更新は、実live=True経路がソケットbind禁止でskipのため未実施です。`webbrowser.open`の準備順とFalseはモック回帰テストで検証対象にしました。file://通常表示・停止/再開・試行保存固定の実ブラウザ操作は、in-app browserでfile:// URLがセキュリティポリシーにより拒否されたため未実施です。過去の検証記録やDerivedDataは今回の成功証拠に使用していません。実機iPhone、実カメラ、実Mac受信、Unity Editorも未実施です。

## 再レビュー修正の検証（2026-09-10）

`test_real_run_reports_red_blue_statistics_threshold_boundary_and_first_timestamp` の `run` 呼び出しを `input_width=1000, input_height=1000` に修正しました。送信列が1000×1000座標を前提としているのに、修正前のrun設定だけ100×100だったテストコードの不具合です。これにより、距離 `.02` の境界値採用、`.021` の拒否、拒否後の有効packet採用、最初の時刻採用、`matched=3 unmatched=1 duplicate=1`、全体・赤・青の統計assertが同じ寸法系で検証されます。

実行した検証は、`python3 -B -m unittest test_udp_receive_probe -v`（終了コード0、23件中16成功・7 skip）、py_compile（終了コード0、`py_compile OK`）、`git diff --check`（終了コード0）です。7件のskipには今回の実run統合テストが含まれ、すべて `PermissionError: [Errno 1] Operation not permitted` によるUDP bind権限制限でした。これは上記のテストコード不具合とは別の環境制限であり、実runの赤青統計・閾値境界・重複抑制・再bind自体はこの環境では未実施です。ブラウザのfile://実表示、実機iPhone、実Mac受信、Unity Editorも未実施です。
