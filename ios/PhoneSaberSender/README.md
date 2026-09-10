# PhoneSaberSender 同一試行の遅延計測

## 実機手順

1. Mac と iPhone を同じ Wi‑Fi に接続し、Mac のプライベート IPv4（例 `192.168.1.10`）を確認します。iPhone の「設定 → プライバシーとセキュリティ」でカメラとローカルネットワークを許可し、macOS のファイアウォールも UDP 5005/5006 を受けられるようにします。セルラー/5G表示はLAN到達性を意味せず、セルラー単独では通常MacのプライベートIPに届きません。
2. Xcode で `PhoneSaberSender.xcodeproj` を iPhone 実機へ起動し、送信先にMacのIPを入力します。「遅延計測モード」をオンにしてから「開始」を押します。出力寸法は `1920 × 1080`、左右反転 `mirrorX=false`、上下反転 `mirrorY=false` が初期設定です。カメラの左右が逆に見える場合だけ `mirrorX=true`、上下が逆の場合だけ `mirrorY=true` にします。送信ポートは赤=5005、青=5006です。
3. Macのターミナル1でprobeを開始します。probeのforegroundが動作中でも、HTMLは別ターミナルまたはFinderで開けます。

```sh
cd /Users/satoshi/縁日/GitHub/school-festival
python3.12 -B udp_receive_probe.py --host 0.0.0.0 --duration 90 \
  --display-log /tmp/saber-display-log.json \
  --reject-threshold 0.08 --input-width 1920 --input-height 1080
```

4. Macのターミナル2で `open /Users/satoshi/縁日/GitHub/school-festival/saber_camera_test.html` を実行します。HTMLの「計測モード開始」を押し、モード（赤のみ・青のみ・赤+青）を選んでから、棒が全て表示されるまで待ちます。一時停止中は現在のstateとログが保持され、完了後は「新しい試行」で新しいtrialになります。色・寸法・resize・fullscreenを変えると、旧試行を保持したまま新trialが始まります。
5. 同じHTMLの「表示ログ保存」を押し、ダウンロードされた `saber-display-log.json` をターミナル3でprobeの終了前に指定場所へ移します。

```sh
mv ~/Downloads/saber-display-log.json /tmp/saber-display-log.json
```

6. 90秒経過またはCtrl-Cで同じprobeを終了します。probeは実行中に受信したpacketを順序付きで保持し、終了時に保存済みログを読み直し、trialId・stateId・色・表示時刻区間を検証してから照合します。過去ログだけを使った再実行は同一試行の照合になりません。ログなしで終了した場合、packetはプロセス終了時に失われるため「保持」は終了まで、「再実行」はできない旨を表示します。

時計はMacとiPhoneで自動設定をオンにし、計測直前に両端末の時刻を確認します。時計オフセットとドリフト、自動時刻設定の限界、ブラウザ描画開始と発光の差、液晶走査、露光・ローリングシャッター、検出処理待ち、遠近歪みが誤差要因です。`ts=`の時刻はiPhoneがpayloadを生成した時刻で、露光時刻でもMac受信成功時刻でもありません。

## 定義

座標は左上原点、右方向x・下方向yのピクセル座標です。probeは入力寸法で正規化し、端点順を逆にしても同じとする平均最近傍距離を `display_coordinate_distance` として計算します。`--reject-threshold` 以下だけを採用し、閾値超過、不正stateId/trialId、非有限値、曖昧な候補、表示試行区間外のpacketは未対応です。同じ `(trialId, color, stateId)` は最初の有効packetだけを採用し、後続の保持packetを次stateへ付け替えません。

`display_to_phone_ms = (phone_timestamp - displayEpochMs / 1000) * 1000` です。括弧内の差は秒なので、出力ミリ秒では1000倍します。統計は有効な一意標本だけを対象に、全体・赤・青の件数、平均、中央値、線形補間p50/p95を表示します。標本ゼロは `count=0` と `-` で表示します。

## 今回の検証記録（2026-09-10）

- 着手前比較コピー: `/private/tmp/task-20260910143433-before`。両リポジトリの既存変更・未追跡ファイルは保持し、対象外は編集していません。
- 実行コマンドと結果: `python3.12 -B -m unittest test_udp_receive_probe -v`（終了コード0、20件、成功15・skip5）、`python3.12 -B -m unittest discover -v`（終了コード0、67件、成功62・skip5）、`python3.12 -B -c 'import py_compile,tempfile; from pathlib import Path; d=tempfile.TemporaryDirectory(); [py_compile.compile(p,cfile=str(Path(d.name)/(Path(p).name+"c")),doraise=True) for p in ("udp_receive_probe.py","test_udp_receive_probe.py")]; print("py_compile OK"); d.cleanup()'`（終了コード0）、`python3.12 -B -c 'from html.parser import HTMLParser; from pathlib import Path; p=HTMLParser(); p.feed(Path("saber_camera_test.html").read_text(encoding="utf-8")); p.close(); print("HTML parse OK")'`（終了コード0）、`xcrun swiftc -frontend -parse ios/PhoneSaberSender/PhoneSaberSender/*.swift ios/PhoneSaberSenderTests/DetectionCoreTests.swift`（終了コード0）です。
- `xcodebuild build-for-testing -project ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -destination 'generic/platform=iOS Simulator' -derivedDataPath /private/tmp/task-20260910143433-xcode.cFkqtq/DerivedData CODE_SIGNING_ALLOWED=NO` は終了コード0（TEST BUILD SUCCEEDED）でした。`xcrun simctl list devices available` は CoreSimulatorService の `Connection refused` で実在UDIDを取得できず、`xcodebuild test -project ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -destination 'generic/platform=iOS Simulator' -destination-timeout 30 -derivedDataPath /private/tmp/task-20260910143433-xcode.cFkqtq/DerivedData -resultBundlePath /private/tmp/task-20260910143433-xcode.cFkqtq/Tests.xcresult CODE_SIGNING_ALLOWED=NO` は終了コード70（Any iOS Simulator Deviceではテスト不可）でした。Simulator Busyではありませんが、実行不能を成功・skipにはしていません。
- UDP integration testは5件skipで、いずれも `PermissionError: [Errno 1] Operation not permitted` により一時UDPソケットをbindできない具体的な環境権限制限です。`node` は利用不可、ブラウザの実表示、実機iPhone、Unity Editorは未実施です。HTML parser/static検証と実表示検証を混同しません。
- ログ保存先は実機手順では `/tmp/saber-display-log.json`。今回の自動テストは合成ログを一時ディレクトリへ保存し、実機ログや過去ログは結果に再利用していません。
