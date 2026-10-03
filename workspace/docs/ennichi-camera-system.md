# 縁日 iPhone カメラ入力システム仕様

## この文書の目的

縁日で使う iPhone カメラ入力を、低遅延かつ当日運用しやすい形で完成させるための参照仕様である。認識、iPhone と Mac の通信、Unity 入力、Mac 側デバッグ、遅延測定に関わる変更前に読むこと。優先順位は、(1) 低遅延、(2) 認識精度、(3) 当日の安定性・使いやすさ、(4) 既存コードとの互換性、(5) 実装の単純さとする。

本書は 2026-09-12 の調査結果を含む。ここで「目標」と書くものは未実装の要求であり、現状実装済みを意味しない。

## 現行実装の確認（2026-09-24）

以下は現在の作業ツリーで確認した実装状況である。コードが存在することと、iPhone実機・当日ネットワークで動作確認済みであることは区別する。

- iPhoneアプリは `BonjourDiscovery` で `_phonesaber._udp` を検索し、見つけたMacを送信先に使う。見つからない場合の手動ホスト入力もある。
- Unityの `InputPoint` は再生中にUDP 5005 / 5006を受信する。macOS EditorまたはmacOS Standaloneでは `PhoneSaberBonjourPublisher` が `/usr/bin/dns-sd` を起動し、サービス名 `Phone Saber Unity`、タイプ `_phonesaber._udp`、ドメイン `local.`、既定ポート5005でホストを公開する。青の座標は同じホストのUDP 5006で受信する。公開はホスト発見用で、座標はUDPで送る。
- Pythonデバッグ受信ツールにも `--bonjour` オプションがある。こちらは `Phone Saber Mac` をUDP 5005で公開し、同時にUDP 5005 / 5006を待ち受ける。Unityとデバッグ受信ツールはポートを共有できないため、同時起動しない。
- Macのブラウザ画面はMac名、ローカルIPv4、ポート、受信状態を表示する。遅延試験は表示状態と受信座標を照合し、Macの単調時計で計測する。回数選択、開始・停止・リセット、成功／失敗表示、成功値の統計、CSV保存、結果フォルダを開く操作が実装されている。旧来のiPhone時刻を含むpacket timestamp差分は、この測定とは別の値である。
- Swift検出コードにはHSV相当の色判定、青色ディフューザー用マスク、マスクのclose/open処理、形状候補の評価がある。2026-09-12時点の調査文にある「SwiftにはHSV・形態学処理がない」という記述は、現在のコードには当てはまらない。
- iOSテスト用の `Fixtures/` には反射を含む静止画、調査用フレーム、動画から切り出したフレームと注釈JSONがある。実機映像すべてを代表する保証や実機試験済みであることを意味しない。

この確認はコードと既存資料の読取りによるもので、今回テストや実機計測は実施していない。

## 完成時の構成

本番経路は次のとおりとする。

```text
iPhone camera → iPhone 内の棒／マーカー認識 → 最新座標のみ UDP 送信
→ Mac の UDP 受信 → Unity が最新座標を即時反映
```

- 映像は本番通信で送らない。送信するのは原則 `x`, `y`, 必要なら検出有無と識別子だけである。二端点が必要な既存ゲーム互換時は `x1,y1,x2,y2` を使う。
- FPS の数字より、カメラに写った現在位置がゲームへ届くまでの時間を優先する。処理が追いつかないときは中間フレームと中間座標を捨て、最新状態を使う。
- 古いデータを順に再生しない。iPhone、Mac、Unity の各段で保持する値は「最新の有効値」一つを基本とする。
- 詳細ログ、画像保存、計測用時刻、GUI の重い処理は本番経路から分離し、本番時に無効化できるようにする。

## 認識仕様

対象は赤・青の発光した棒である。移植先は iOS/Swift であり、Python/OpenCV をそのまま実行する前提ではない。

`GitHub/school-festival/camera.py` の比較的良好だった方式は次を再現可能な形で移植する。

1. BGR フレームを HSV へ変換する。
2. 赤は OpenCV HSV の hue 境界をまたぐ二つの範囲（既定 H=170..179 または 0..10, S=70..255, V=100..255）、青は H=99..124, S=137..255, V=168..255 でマスクする。閾値は実画像で再調整可能にする。
3. 小さい隙間は楕円カーネルの close（既定 5）でつなぎ、3x3 の open で小ノイズを除く。
4. 外側輪郭を取り、最小面積（既定 170 px）だけでなく、面積比、長短比、extent、bounding-box extent、PCA の細長さで棒らしくない物体を除く。
5. 残った候補から棒らしさのスコアが最大の一つを色ごとに選ぶ。端点は分離した二成分の重心が十分離れていればそれを使い、そうでなければ回転矩形の長軸を優先する。PCA と凸包の最遠点はフォールバックにする。
6. 端点の順序は前フレームとの距離で安定化する。未検出時の保持時間は、遅延を増やさない短い値とし、検出有無を明示してゲーム側が古い位置を生データと誤認しないようにする。

明度だけの検出は補助／比較用とする。`camera.py` の既定 bright threshold は 201 だが、保存済み `camera_thresholds.json` は 255 であり、現在の実用経路は色検出である。

Swift 実装では BGRA の各画素に単純な明度・色優勢判定を掛けるだけでなく、上の HSV 相当の閾値、形態学的ノイズ除去、棒形状の候補選択を導入する。解像度、`sampleStep`、閾値、形態カーネルは一か所の設定にまとめる。実画像と誤検出画像にオーバーレイを描く回帰テストを追加する。

## フレームと送信の低遅延規約

- `AVCaptureVideoDataOutput.alwaysDiscardsLateVideoFrames = true` を維持する。処理キューは一本とし、処理中に到着した古いフレームを蓄積しない。
- 既存の iOS `FrameProcessor` は同じ serial queue で capture delegate を実行しており、上記設定により遅延フレーム破棄を依頼している。将来、重い認識処理を分離する場合も、FIFO ワーカーキューにはせず atomic な最新フレームスロット（上書き）を使う。
- 送信は新しい検出結果だけを対象にする。検出失敗時の 0.18 秒保持を本番座標として繰り返し送らない。保持は UI 表示または短期安定化に限り、fresh/stale を区別する。
- UDP 受信は受信スレッドで値を上書きし、Unity の `Update` は一フレームに一度だけ最新値を取り出す。受信キューを `Update` で全消化する設計にしない。

## 通信・設定・発見

- 本番プロトコルの現行互換値は red = UDP 5005、blue = UDP 5006、ASCII `x1,y1,x2,y2`。計測時だけ既存互換として `ts=<epoch-seconds>;` を前置できる。
- IP アドレスとポートをソース中の複数箇所に重複させない。iOS、Mac 受信、Unity のいずれも共有設定または各アプリの一か所の設定モデルから参照する。
- 通常 Wi-Fi と iPhone Personal Hotspot の両方で、iPhone と Mac が同じローカル IP 到達可能ネットワークにいることを前提にする。セルラー単独は対象外である。macOS firewall の UDP 許可も起動時に分かるようにする。
- 通常運用は「Mac が受信サービスを起動 → iPhone が Bonjour/Network framework でサービスを発見 → 自動接続」とする。発見に失敗した場合のみ、iPhone UI の手動ホスト/IP 入力を使う。
- 発見サービス名、UDP ポート、プロトコル版は設定モデルに集約する。Personal Hotspot でも動くことは実機で確認し、SSID や固定 IP を仮定しない。
- iPhone と Mac の診断表示には少なくとも接続状態、Mac 名、IP、ポート、送受信状態／頻度、検出有無、最新座標を表示する。UDP の `ready` は到達保証ではないため、Mac 側の直近受信時刻も併記する。

## Unity 入力仕様

- Unity は 5005/5006 の既存入力を再利用し、二端点をそのままブレードへ反映できる状態を維持する。
- 最新パケットのみを使い、フレーム内で複数パケットを順にゲーム状態へ適用しない。
- 受信時刻、パケット数、周波数、最後に有効な入力を Inspector またはゲーム内デバッグ表示で確認できるようにする。
- 座標の向き、解像度基準、正規化範囲を明文化し、送信側と Unity の変換を一つの契約としてテストする。現行互換は 1920x1080 基準の二端点で、Unity はピクセル値と -1..1 正規化値の両方を受ける。

## Mac 総合デバッグと遅延測定

Mac 側に本番受信と分離可能な総合デバッグ画面を用意する。通常操作は Finder から `run_latency_test.command` 等をダブルクリックして GUI を開き、その画面だけで接続確認と計測を完結できるようにする。

- Network: Mac のローカル IP、待受ポート、iPhone の直近受信元と接続／受信状態、受信頻度。
- Detection: 検出有無と最新 x/y（必要なら二端点）。
- Latency Test: Start Test / Stop Test / Reset、現在試行数、各結果、平均、中央値、最小、最大、成功数、失敗数、保存状態、Results Folder を開く操作。

測定は「Mac 画面上の明確な状態変化 → iPhone カメラ取得 → iPhone 認識 → UDP 送信 → Mac 受信」の end-to-end latency とする。ネットワーク単体や iPhone の時刻と Mac の時刻の単純差を、この測定の正解として扱わない。

- 既定 10 回程度を自動実行し、試行回数は変更可能にする。
- 正常値だけから平均・中央値・最小・最大を出す。認識不能・照合不能・timeout は `FAILED` とし、巨大な疑似遅延値として統計へ混ぜない。
- 終了時に日時付き CSV を `latency_results/` に保存する。ただし CSV を開かなくても GUI に結果を表示する。
- 画面状態には一意な試行／状態 ID を出し、受信座標との照合で開始時刻から受信時刻を測る。カメラで見える表示を使い、同一時計でタイムスタンプを記録する。曖昧な一致は失敗扱いにする。

## 初回調査の記録（2026-09-12時点）

以下は初回調査時点のスナップショットである。Bonjour、Mac計測画面、Swift認識処理、テスト画像については、上の「現行実装の確認」を参照すること。

### 対象リポジトリと実経路

主な経路は `GitHub/school-festival/ios/PhoneSaberSender` の iOS アプリから、`GitHub/3D-Saber` の Unity プロジェクトである。2026-09-12時点ではワークスペース直下にHello World版の別プロジェクトがあったが、2026-09-24に安全確認のうえ削除済み。現在の本番版は `GitHub/school-festival/ios/PhoneSaberSender/` である。

```text
iPhone: CameraViewModel / AVCaptureVideoDataOutput (BGRA, back camera)
  → FrameProcessor / BGRADetection
  → UDPSender (Network.framework UDP, 手入力 host, 5005/5006)
  → Mac: Unity InputPoint (UdpClient + 各ポート受信スレッド)
  → Unity Update が各棒の最新 raw 値を一回だけ反映
  → SaberInputBridge が端点を描画・判定に使用
```

Mac 単体の調査・計測系は `GitHub/school-festival/udp_receive_probe.py`、`saber_camera_test.html`、`start_phone_saber_latency.command` にある。これは UDP 受信とブラウザ画面を開く仕組みであり、Unity と同じポートを同時には使えない。

### `camera.py` が比較的良かった理由

HSV の色相・彩度・明度で対象色を切り出すため、単純な RGB の色優勢判定より照明の明暗変化や白い明るい物体を分離しやすい。赤の hue 周回を二範囲で正しく扱い、close/open で発光の欠けと点ノイズを処理する。さらに、面積だけで最大物体を選ばず、細長さ、充填率、PCA の伸びを判定し、棒候補をスコア選択するため、背景の大きい色物体を取りにくい。回転矩形の長軸から端点を作るので、斜めの棒も二つの光点に分裂しない限り扱える。

現行 Swift は RGB/BGRA の brightness + channel dominance + saturation と、縮小グリッド上の連結成分・PCA 端点である。HSV 範囲、形態学処理、輪郭の面積／充填率／形状スコアがなく、接続半径 1 の連結に依存するため、光の切れ・ノイズ・背景色への耐性が `camera.py` より低い可能性が高い。

### 現状の低遅延評価

良い点は、iOS が `alwaysDiscardsLateVideoFrames` を有効にし、Python の `camera.py` と `smartphone_camera.py` も最新フレーム一枚を上書きする設計であること、Unity `InputPoint` も受信スレッドで raw 値を上書きして `Update` が最新値だけを反映することにある。

注意点は、Swift の検出が delegate と同じ serial queue 上で BGRA 全体を配列コピーし、二色を走査・連結成分探索していること、毎 fresh 検出ごとに非同期 UDP send を積むこと、未検出値を 0.18 秒保持することにある。`alwaysDiscardsLateVideoFrames` はカメラ側での FIFO 蓄積を抑えるが、送信完了 callback や UI 更新の滞留までは保証しない。実機で processing time、capture output FPS、送信数、Mac 到着間隔を計測して確認する。

### 現状のネットワークと測定差分

iOS は手入力 host と固定 5005/5006 を使用し、Bonjour の公開・探索・自動接続は未実装である。Network framework と Local Network usage description はあるため、発見を追加する土台にはなる。Mac probe は `0.0.0.0` に bind でき、通常 Wi-Fi／Hotspot での到達可能性はあるが、現在の iOS は IP を手入力する必要がある。Mac 名、ローカル IP、実際の受信レートをまとめて表示する GUI は未実装である。

既存の `start_phone_saber_latency.command` はブラウザの live dashboard を開く。`udp_receive_probe.py` は受信履歴と timestamp 差、HTML は画面状態ログを扱えるが、CSV 保存、GUI 内の自動 10 試行、成功／失敗統計、GUI 操作、Finder で結果を開く操作は満たしていない。iPhone 時刻と Mac 時刻の差は時計ずれを含むため end-to-end 値ではない。既存 `camera_benchmark.py` の保存 JSON は Mac 画面→Continuity Camera→Python フレーム取得までで、認識・iPhone UDP・Mac受信を含まない。

### テスト画像・既存検証資産

`GitHub/school-festival` に実写の棒・誤検出用静止画は追跡されていない。現状の自動テストは OpenCV/Swift ともに合成画像（線、ノイズ、BGRA バッファ）を生成する。根ディレクトリの `target.png` は存在するが、棒認識の実写 fixture と確認できない。次フェーズ前に、許可済みの実写サンプルを `GitHub/school-festival/testdata/` 等へ追加するか、入手不能として合成テストとの限界を明記する。

## 初回調査時点の次フェーズ案（2026-09-12）

以下は当時の計画であり、現在の未実装一覧ではない。

Phase 2 は「iPhone 側認識精度改善・最新フレーム優先・低遅延座標送信」に限定する。主な対象は以下である。

- `GitHub/school-festival/ios/PhoneSaberSender/PhoneSaberSender/DetectionCore.swift`: HSV 相当の色判定、形状条件、端点推定の純粋ロジック。
- `GitHub/school-festival/ios/PhoneSaberSender/PhoneSaberSender/BGRADetection.swift`: BGRA から低解像度マスクを作り、上記候補選択へ接続する処理。
- `GitHub/school-festival/ios/PhoneSaberSender/PhoneSaberSender/FrameProcessor.swift`: 最新フレーム優先を明示し、fresh/stale と保持の契約を整理する処理。
- `GitHub/school-festival/ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift` と `UDPSender.swift`: 送信頻度、検出有無、診断値、設定の集約。Bonjour は Phase 2 の対象外でも設定の分散を増やさない。
- `GitHub/school-festival/ios/PhoneSaberSenderTests/StaticBGRADetectionTests.swift`、`DetectionCoreTests.swift`: 実写 fixture が得られればその回帰テストと、最新フレーム／fresh-only送信のテスト。

Unity の `GitHub/3D-Saber/Assets/Scripts/Managers/Inputsystem/Input/InputPoint.cs` と `GitHub/3D-Saber/Assets/Scripts/Saber/SaberInputBridge.cs` は、Phase 2 では既存 UDP 互換を保つ確認対象とする。Mac 発見・総合 GUI・自動 end-to-end 計測は別フェーズで `udp_receive_probe.py` を部品として再利用しつつ設計する。

## 検証の原則

実装済みと検証済みを分ける。Codex 側では単体テスト、静止画／合成画像検出、UDP payload parse、統計、CSV、GUI の基本動作、起動スクリプト、Unity コンパイルを検証する。iPhone 実機による最終認識精度、Wi-Fi／Personal Hotspot、実際の end-to-end latency は人手実機試験として記録する。
