# predict-real（2026-10-09）

既定Hは **0 msを推奨し、変更しない**。任意予測の減速抑制だけを採用した。
認識、候補、score、端点出力、eligibility、winner、diagnostics、UDP形式/portは編集していない。

## 実データと方法

HOME内のPhoneSaber JSON/JSONL/CSVを検索し、Downloadsの連続metadata 18録画、
34,874フレーム・赤青69,748行を抽出。forensic/analysisフォルダ自体は画像のみ。
diagnostics-inbox 32録画の重複近傍JSONも別抽出したが、短い異常選択窓なので主評価には使わない。
最長は `phonesaber_20260922_130726_609`、7,993フレーム・266.38秒・30 Hz。
保存画像frame 183543では赤青の実セーバーを確認した。
全体に誤認識/端点入替も含まれ、全フレームの物理正解を保証しない。
実データのコミット対象は匿名sample約26 KBのみ。原録画、画像、全抽出、私有manifest、測定JSONはGit外。

30/60 Hz描画、4位相、固定受信遅延140 msの仮定、11×6 world写像、
0.35移動上限。捕捉失敗/予測済み/50 ms超の穴をまたがず、559連続区間を評価。
60 Hzの新しいcapture正解はない。受信ジッタ/台校正/実際の130–150 ms遅延は測定していない。
実端点を線形補間したcapture+Hを参照し、OFFも同じ未来目標で比較。
折り返しは座標符号反転の前後100 ms、追加はみ出しはOFFの値を引く。
古い56,384行はpredicted未記録で、その不確実性を残した。

## before / after（このMacで実行）

before=`977d5c6`の2区間最小速度。afterは、同符号かつ減速する成分のみ
`current * abs(current / previous)`を使う。定数/閾値の追加はない。
H=0、定速、反転時の0速度、gap、decay、上限は維持。
単位は仮定写像のworld units。

| 描画Hz | H ms | 同じ未来へのOFF RMSE | before RMSE | after RMSE | 追加はみ出しp95 before→after |
|---:|---:|---:|---:|---:|---:|
|30|20|0.762225|0.762432|0.760373|0.033752→0.022502|
|30|40|1.216088|1.212426|1.209390|0.075005→0.055002|
|30|60|1.359268|1.350491|1.346632|0.116571→0.082503|
|60|20|0.762228|0.762436|0.760377|0.033752→0.022502|
|60|40|1.216095|1.212433|1.209398|0.075005→0.055002|
|60|60|1.359281|1.350505|1.346646|0.116571→0.082503|

20 msでRMSE改善0.270%、追加はみ出しp95改善33.33%。
H=20/40/60、両描画Hzで36録画色系列中35系列のRMSEが改善。
系列ごとの追加はみ出しp95/最大に1e-6超の悪化なし。全体最大はbefore/afterとも約0.350000。
一例の悪化は140125録画の赤、H20: 0.089226→0.090901（+1.88%）。
改善は小さいが広く再現し、はみ出し低減があるため任意予測に採用。

predicted=falseを確認できる系列に限定しても、30 Hz/H20のRMSEは
0.384593→0.382761、H40は0.622735→0.620008。
最長録画の因果的端点対応診断でもH20は0.653146→0.650871。
端点の入替はC#に追加していない。
3点最小二乗は30 Hz/H20で0.808789、追加はみ出しp95 0.234138へ悪化。
speed適応候補は0.762317と減速抑制より劣るため不採用。

## 既定値の根拠

最長録画の赤はH20でOFF 0.277864→after 0.253308と改善する一方、
青はOFF 0.870696→after 0.876974と悪化。
全体の仮定live RMSEも30 HzではOFF 1.821587→H20 1.814764、
H40 1.811188に留まり、追加はみ出しが発生する。
最長録画では全整数H=0..60 msを両描画Hzでbefore/after掃引済み（244集計）。
両版とも仮定live RMSE最小はH=0。afterの30 HzはH0 1.494031、
H20 1.496070、H40 1.495526、60 HzはH0 1.493586、H20 1.495586。
未ラベルの認識端点・補間・仮定受信時刻から台の既定値を上げる確かな根拠はない。
実機で任意予測を試す場合は20 msから比較し、40/60 msを全台の既定にはしない。

## 検証・再現

実行コマンドと評価定義は `PhoneSaber/tools/README_real_motion.md`。
全録画before/after: `/tmp/predict-real-after.json`、
既知フラグ限定: `/tmp/predict-real-known-after.json`、
全整数掃引: `/tmp/predict-real-full-sweep.json`。
新旧の実際のC#9を.NET 9.0.109 + Vector2 stubでコンパイルし、
共有240予測・960成分のfloatビットがPython/期待値と各々一致。
OFFの符号付きゼロも確認。Unity EditMode用にも同じベクトルのテストと減速テストを追加。
tools unittestは60件、59成功・既存socketテスト1件skip。
公式検証のDetectionとformal lossless **40/40 PASS**、diff check PASS。

公式検証はsandbox内で完走PASSにはできない。CoreSimulatorServiceのXPC接続/ログアクセスが
拒否されiOS XCTestは未実行。iOS Releaseは `sandbox-exec: sandbox_apply: Operation not permitted`
でSwiftUI macro serverが失敗。広域Toolsは384件、8 failures/33 errors/1 skipで、
socket bind/receiver起動拒否、P2Pテストのprocess検出拒否等が残る。
launcher終了テストのassertion failureもあり、全面PASSとは扱わない。
tracking E2Eは容量preflightがavailable=0 / required=1,040,187,392を報告して失敗。
初回のSwift module cache拒否は `/tmp` のcacheへ移して解決し、losslessを再実行した。
Unityはユーザー指定により利用しないためEditMode/PlayMode実行は未検証。
.NET stubの成功をUnityの成功として報告していない。
最終公式ログ: `/tmp/predict-real-verify-final/20261009-093750/`。

自己レビュー: 変更は指定predictor/Editor tests/toolsとこの依頼された文書のみ。
production recognitionやUDPのコードパスに差分なし。pushしない。

コミットは未完了。通常のgit addと明示git-dirでの再試行とも、
`3D-Saber/.git/worktrees/3D-Saber-predict-real/index.lock` の作成が
`Operation not permitted` で拒否された。現在のbranchはopt/predict-real、変更は作業ツリーに残す。
コミットメッセージは `/tmp/predict-real-commit-message.txt` に準備済み。
