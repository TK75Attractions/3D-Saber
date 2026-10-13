# 実測からの遅延補正と画面経路の測定（2026-10-13、lat2）

前回の未検証wip `a976496` を引き継いだ変更案。レビュー・公式verify・commit/pushは運営が行う。
既定OFF、手動0/20/40/60 ms、予測器の60 ms上限・速度/移動量/gap制限は維持。
認識、UDP本文 `x1,y1,x2,y2`、formal corpus、iOS/Android sources、scene/prefab/.meta、ProjectSettingsは変更しない。
表示用late sampling、描画設定の強制変更、予測器の置換は提案だけ。

## 今日の実機値と補正量

| 項目 | 2026-10-13の値・条件 |
|---|---|
| iPhone 13 capture→detection-end | 60 fps、中央値34.4 ms（40窓の中央値29.7〜35.3、p95 36.2） |
| カメラ | 640×480、画角65.36度、非binned。同じサイズ・画角の120 fps formatがないため120 fpsは提示されない |
| 通信 | RTT/2: USB 0.7 ms、hotspot 3〜5 ms、74 msの単発spike |
| PC receive→first render直前 | built PlayerのGame: vsync OFF、約2900 fpsで中央値0.3 ms。メニューvsync ONでは最大約1refresh |
| 画面 | 60 Hzの半周期8.3 msは推定。compositor・GPU/present・projector処理は未分離 |

USBの合計は **43.733 → 44 ms**、hotspotは **46.033〜48.033 → 46〜48 ms**。
メニューのPC値が大きい場合やF9で長い画面残差が出た場合は合計も増える。
合計60 ms超は `MaximumHorizonMilliseconds` で制限し、
`event=prediction-lead clamped=1 maximum-ms=60 median-ms=...` を残す。
先読みは物理遅延の除去ではなく、最近の動きから遅れている端点を外挿する補償。
折り返しや誤認識では行き過ぎ得る。

## 変更と重大度順の所見

1. **高・通常入力では自動が常にOFF** — `PhoneSaberPredictionSettings.cs:22` / `InputPoint.cs:177`。
   旧コードはNTP確認済みかつts片道値がある場合だけ有効。通常の4要素座標では撮影34.4 ms等の
   計測が届いていても0 ms。色ごとの自動推定の合計を優先し、同期済みtsを第2候補にする。
   修正前 `47c9aab` の実コードで44 ms期待→0 msのFAILを再現。赤/青を独立にBridgeへ渡す。
2. **中・短い欠測による補正の点滅** — `PhoneSaberPredictionSettings.cs:97`。
   前回wipはPC統計窓のクリア直後でもOFF→再開になり、10秒ごとに外挿量が変わり得た。
   30秒窓中央値、5 ms超の変化、更新間隔10秒以上を維持し、短い欠測では保持する。
   有効期限は元のスマホ診断受信+15秒、RTT期限10秒、最後の座標+15秒の短いもの。
   キャッシュの再読で期限を延長しない。期限到達は秒1回更新を待たず次のframeで処理。旧アプリ・P2P・無入力は15秒以内にOFF。
   IP変更・判別できるplatform/app変更では履歴を捨てる。
   NTP確認解除は次の秒1回更新で旧tsを停止。保持版でこの回帰FAILも再現してから修正。
3. **中・投影機を半周期だけと見積もる** — `PhoneSaberLatencyEstimate.cs:134` / `PhoneSaberLatencyProbe.cs:185`。
   60 Hzなら常に8.3 ms扱いで、例えば投影機がさらに数frame処理しても補正に入らなかった。
   F9の有効残差を台・画面設定ごとに保存し、設定一致時だけ推定の画面項目へ使う。
   修正前コードで保存値80 ms期待→8.333 msのFAILを再現。

自動は選択した台だけで動く。秒1回、直近30秒の合計推定値の中央値を求める。
測定経路が変わったら中央値を混ぜない。送信元変更・欠測OFFは安全な履歴解除として
10秒制限の例外（別端末の履歴を引き継がない）。通常のlead更新は10秒以上あける。
F8は色別lead/source、画面の実測/半周期推定と実測の経過分を表示。
`event=perf` は色別lead/source、`event=prediction-lead` は選択と変更、clampを記録する。

## F9の意味と保存

F9を1回押し、表示される1文の指示に従ってカメラを画面に向ける。
現在の画面設定を変更せず、15応答×8ブロック（約2分）、最大180秒で自動終了。
結果は20秒間「画面の遅延: 約NN ms（この PC とこの画面）」を表示する。
開始・終了のF9切替は残るが、終了操作は必須ではない。旧F9のqueue=1/2自動交互比較は行わない。

各ブロック:

`display-path = loop中央値 − phone c2s中央値 − RTT/2 − 500/capture-fps − Update→GUI Repaint中央値`

- loopは赤棒を切り替えたUpdateから、対応したUDPの受信まで。PC receive→renderは復路終点の
  **後**なので引かない。引くPC項目は切替Update→最初のGUI RepaintのCPU側だけ。
  Repaint以後のGPU/present、compositor、scanout、投影機処理は残差に残す。
- 半カメラframeは露光位相の**推定**（60 fpsなら8.33 ms）。同期した位相・rolling shutter・
  phone timestampの定義差では正確な分離にならない。真のglass時刻を直接測った値ではない。
- phone項目はブロック内で受信した5秒窓中央値の中央値。raw frameの全体中央値ではない。
  c2s-nは各snapshotの件数合計、rtt-nもsnapshot件数合計（RTT窓が重複し得る独立でない件数）。
  phone-windowsを別に記録する。iOS c2s-end=detectionはUDP queue/send完了を含まない。
- loop15件未満、Repaint件数不一致、phone20件未満、RTT2件未満、phone/RTT/fps欠測・期限切れ、
  LAN以外、設定/送信元/撮影設定の途中変更、残差が負/500 ms超/非有限を拒否。日本語で理由を表示。
- 各 `event=display-measured` にloop/c2s/network/露光位相/presentの値と件数、phone-windows、
  capture-fps、設定キー、display/解像度/refresh/vsync/fullscreen/queue/API/target、platform、senderを残す。
  拒否したブロックは保存しない。最新の有効値を同じ台と設定のPlayerPrefsにUnix保存時刻と保存する。
- 別の画面・解像度・Hz・vsync・fullscreen・queue・API・targetでは流用しない。
  projectorの内部game modeなどUnityから見えない変更は検出できないためF9をやり直す。
  自動の保存期限は設定していない（経過分を表示）。

`event=latency-estimate display-source=measured|configured-half-refresh display-age-min=...` で採用元を確認する。
集計は `python3 PhoneSaber/tools/latency_report.py --display-measured <events.log>`。
有効な結果だけを台・platform・画面設定でまとめ、各分解項目の中央値を表示する。

## 実機で運営が確認すること（すべて条件・結果を自動記録）

1. 更新したアプリをLANで接続し、本編で10秒以上入力する。F8の4項目と色別値を確認。
   **記録:** `latency-estimate` のc2s/network/pc/display/total・senderと `perf` のvsync/fps。
   今日の数字はGameの条件であり、メニューのPC項目を0.3 msへ固定しない。
2. F8で「自動」を選ぶ。NTP checkboxは通常入力には不要。USBなら約44 msを基準に実際の
   合計に従うこと、両色が別senderならそれぞれの値になることを確認。
   **記録:** 色別 `prediction-lead` と `perf prediction-*-lead-ms/source`。
3. 本番の出力先・画面同期・fullscreen条件でF9を1回、カメラを画面へ向け、撮影fpsの自動比較はOFF。
   自動終了の結果/拒否理由を確認。**記録:** 各 `display-measured` と `latency-loop-end`。
   撮影設定を途中で変えたブロックは拒否される。
4. 同じ設定で再起動し、F8に実測と経過分が出ることを確認。F8画面同期などを切り替えると
   設定が不一致となり半周期推定に戻ることも確認。元の設定ならその設定の保存値を使う。
   **記録:** `display-sync`、`perf` の条件、`latency-estimate display-source/display-age-min`。
5. 診断停止、旧アプリ、P2P切替、無入力を比較し、自動が15秒以内にOFFへ戻ることを確認。
   別端末への切替で前の端末の補正を使わないことを確認。
   **記録:** `prediction-lead source=fallback`、`latency-estimate status=missing-*`、既存接続ログ。
6. 同じ静止・定速振り・急な反転で手動OFFと自動を比較し、反転の行き過ぎや静止揺れを確認。
   **記録:** 切替の `prediction-lead`、`perf` の予測/抑制回数、送信側の既存Debug Recordingの数値軌跡。
   判定基準・既定値を変える判断は運営が行う。最後のOFF選択もログへ残る。

## 実行した検証 / 未実行

GUI、Unity Editor/Player、Simulator、ブラウザーは起動していない。

- `python3 PhoneSaber/tools/check_pc_latency.py --compensation-before-ref 47c9aab`:
  Unity 6000.3.9f1の実DLLでEditor Mac/Windows・Player Mac/Windows **4構成PASS**。
  実 `SaberInputBridge`、InputPoint、PhoneSaber runtimeを含む。ゲーム側依存だけstub。
  補正/ProbeのEditModeソースも実APIでコンパイル（native実行ではない）。
  standalone NUnitは旧コード **21 PASS / 2 FAIL（上記の回帰、期待どおり）**、現行 **34 PASS / 0 FAIL**。
  hostはUnity lifecycleとPlayerPrefsをstub、実policy/decomposition/persistence codec/perfを実行。
  短い窓欠測は修正前 **31 PASS / 1 FAIL**、NTP解除は保持版 **33 PASS / 1 FAIL** を先に確認。
- `PYTHONPATH=PhoneSaber/tools python3 -B -m unittest -v test_latency_report test_predict_latency_eval`:
  **26件PASS**。reportの拒否/設定別集計/CLI、offline参照、実C#9 parityを含む。
  内部C# hostの予測300000 frameは0 B割当（Unity全体の無割当を証明したものではない）。
- `predict_latency_eval.py` を既存実録画CSVの連続/既知false/異常窓へそれぞれ実行 **成功**。
  44/46/48/60 msの結果と限界は [offline追記](predict-20261013.md)。
- `python3 PhoneSaber/tools/check_auto_latency.py`: **4 PASS / 1 FAIL**。
  C#共有fixture/推定/保持/実EventLog formatterは成功、loopbackのUDP bindが `Access denied`。
  この失敗で停止したためSwift build/runとSwift→C# loopbackは未実行。
  contractを含めたPython実行も **28件中27 PASS / 1 FAIL**（同じsandbox理由）。skipやPASSに変えていない。
- `git diff --check` **PASS**。公式verify、Unity Test Runner/PlayMode、正式Player build、
  auto_latency_player_check、実投影F9、Windows native/API、iPhone/Android実機は未実行。

運営がrepo rootで実行（現在のPlayer/Editor Playは停止してから）:

```sh
python3 PhoneSaber/tools/check_pc_latency.py --compensation-before-ref 47c9aab
python3 PhoneSaber/tools/check_auto_latency.py
PhoneSaber/tools/unity_copy.sh sync
PhoneSaber/tools/unity_copy.sh editmode '^PhoneSaber'
PhoneSaber/tools/unity_copy.sh build-mac
python3 PhoneSaber/tools/auto_latency_player_check.py
PhoneSaber/tools/verify_phone_saber.sh
```

最後のPlayer checkは既存の窓なしbatchmode既定を使う。`--windowed`は使わない。
macOS app起動は `open -g -j -n -W <App>.app --args ...` の既存経路で、ログを判定する。
Player checkは4項目の診断疎通を検証し、実画面のF9や体感の代わりにはならない。
新規 `.meta` はこの作業では追加していないため、運営のCOPY importとEditModeで取り込みを確認する。

## その後に物理遅延を減らす候補（提案のみ、期待値順）

1. **投影機のgame/low-latency mode、補間OFFを比較。** 現在の遅延は未知。
   仮に1〜2 frameの内部bufferを減らせれば60 Hzで16.7〜33.3 ms、bufferがなければ0 ms。
   F9を各modeでやり直して残差を比較する。Unityには内部modeが見えない。
2. **異なる画角の120/240 fps camera formatを別評価。** frame位相平均だけなら
   60→120で8.33→4.17 ms（約4.17 ms削減）、60→240で2.08 ms（約6.25 ms削減）。
   capture→detection 34.4 ms全体が半分/4分の1になる保証はない。ISP/readoutの実測が必要。
   サイズ・画角・露出・校正写像・LED見え方・熱/電力・端点精度・formal検証が必要で、
   iPhone 13の現行640×480/65.36度の設定に120 fpsを無理に足さない。
3. **hotspot→現在のUSB/LAN経路。** 今日の中央値なら約2.3〜4.3 ms差。
   74 ms spikeや送信queueの非対称は定額で補償できない。RTT/2で条件をログ比較する。
4. **表示だけlate sampling / present経路の見直し。** 60 Hzメニューで新着がUpdate後に来た時は
   最大約1周期（16.7 ms）の機会。Gameはreceive→renderが既に0.3 msなのでCPU部分の余地は小さい。
   表示と判定poseの差、二重フィルタ/予測/判定の回避、実機比較が必要。今回接続していない。

F9の露光位相は推定、median同士の差はframe対応の完全な分離ではない。
60 fpsの長い実剣軌跡が不足し、44〜48 msで静止proxy揺れは約10〜11%増える。
60 ms制限を超える表示経路には残りの遅延が残る。実機の効果は上のログ付き比較で確認する。
