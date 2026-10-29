# 2026-10-29 赤コーン回帰

赤の刃が見えなくなった直後、床の赤いコーンへ選択が飛んだ収録の回帰。
指定の代表10 jump clipと、125036 / 135856 の2 confusion clipを保存候補で再生する。
`test_red_cone_sequence.py` は `verify_phone_saber.sh` の Tools unittest discover で常時実行する。
非公開fieldセット・カメラ・ネット接続は不要。既存のSwift再生ホスト同様、macOSのxcrun/swiftcが必要。
ビルド失敗やfixture欠落はskipせず失敗する。

```bash
python3 -B -m unittest discover -s PhoneSaber/ios/PhoneSaberSender/Tools -p test_red_cone_sequence.py -v
```

## 測定値と固定した上限

`cone` は赤出力の中点が `manifest.json` の `coneRects` 内にあるframe数。
原画像は180度反転しているので、実際の画面下のコーンは回転前PNG座標では上側にある。
480×640左上原点の4矩形は、コーンの横長の縁・台座も含めて原寸event PNGの目視から固定。ランプは矩形に含めない。
矩形外の候補を本物の刃と断定する測定ではない。

`absent` は再生した赤の選択出力がnullのframe数。画像での刃の在否とは別。
原寸event画像を各clipで1枚ずつ別途ラベルし、赤の可視発光部なしは5/12枚。
全3492frameに画像の正解があるとは主張しない（保存された原寸画像はeventのみ）。
テストは各clipの `visual_event_red_absent=0または1/1` も出す。

| clip | frames | cone上限 | absent出力 | 収録時cone | 収録時出力との差 | reset |
|---|---:|---:|---:|---:|---:|---:|
| 124359_033 | 301 | 3 | 45 | 3 | 3 | 0 |
| 124558_320 | 301 | 83 | 83 | 83 | 0 | 0 |
| 124818_863 | 301 | 149 | 152 | 152 | 32 | 0 |
| 124957_145 | 301 | 22 | 25 | 8 | 31 | 0 |
| 125054_666 | 301 | 36 | 151 | 29 | 15 | 0 |
| 132525 | 301 | 261 | 36 | 224 | 153 | 4 |
| 132602 | 301 | 140 | 161 | 132 | 102 | 0 |
| 132736 | 301 | 294 | 7 | 289 | 112 | 0 |
| 133224 | 301 | 206 | 95 | 210 | 70 | 4 |
| 135841_407 | 301 | 279 | 22 | 217 | 105 | 11 |
| 125036_741 | 301 | 19 | 20 | 19 | 10 | 0 |
| 135856_666 | 181 | 59 | 13 | 57 | 4 | 120 |

expectedFailureは使わない。上限よりコーン選択が増えればFAIL。
減った場合も「改善。原画像を再確認し上限を下げてください」とFAILにして更新を促す。
absent数・記録出力との差・reset・候補打切り数の変化もレビューして明示更新する。
自動で上限を書き換える処理はない。manifestのソースハッシュは基準値の由来の記録であり、
認識ソースの変更を禁止するassertではない。

## 元資料と抽出

元資料はローカルの `~/Library/Application Support/PhoneSaber/regression-keep/red-cone-20261029/`。
元clip名とsummaryのSHA256はmanifestに残す。画像、人物・端末・IP情報、カメラ情報、
絶対パス、画像ラベルの端点、frameContext、clip本体はrepoに含めない。
候補JSONは合計約1.9 MB（3492frame、赤のみ）。

抽出は各summaryのautoClipから以下だけをコピーした。候補やframeの間引き・座標の丸めはしない。

- candidateColumnsの先頭19列、frameSize、playArea、sources。
- 各frameのf、t、red.cの先頭19列、red.e、red.n、red.sel。
- tのみ先頭frameを0秒とした相対秒、小数9桁。blueは空候補・出力nullとする。
- 既存loaderが両色を要求するためblueの空欄を付ける。SaberTrackLockは色別に独立し、赤のみを測る。

候補列は `[rank,x1,y1,x2,y2,minX,minY,maxX,maxY,score,flags,source,
peakValue,emitterScore,highValueRatio,meanValue,nearSaturationPresent,colorPurity,componentArea]`。
rank順は既存loaderが復元する。ineligibleも保存分を残し、observed/continuityの履歴に渡す。
同じfixtureを再抽出した場合はSHA256と元候補列を照合してからmanifestを更新する。

## 再生の限界とd4での使い分け

本番 `SaberTrackLock` を `phone_saber_stationary_replay.run` / `SelectionReplayHost` で動かす。
policy上書きなし。実験用PHONESABER_REPLAY_EMITTER_PEAK_FLOORはテスト内で無効化。
最初の記録winnerをseedとするが、収録開始前の速度・復帰・選好履歴は復元できない。
eligible保存漏れ、frame ID欠落、時間の断絶は既存loaderの規則でresetする。
135856は181frameの実記録（連番でない）。補間はしない。
記録候補の点数は収録精度の0.1点、ineligibleは収録上限2件のまま。
候補が全保存されていないframe数はテストのtruncatedとして表示する。
原画素の色支持gate・無視範囲は候補だけでは再現できず、再生は上限評価。
「収録時出力との差」はこれらの制約を隠さないために固定した診断値。

このテストは**候補列からの時系列選択の回帰**。凍結候補なので、d4の画素処理だけを
改善してもこの数値が改善するとは限らない。d4の画素側合否は、同じ原画像を入れた
私設field_regressionのPASS/XFAIL/XPASSと合わせて判断する。
単フレーム12枚のラベル・XFAIL理由・配置コマンドは `/tmp/d5-conefield/report.md` に報告する。
私設画像の登録待ちでも、この時系列テストはToolsで必ず実行される。
