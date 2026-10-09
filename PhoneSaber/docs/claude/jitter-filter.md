# jitter-filter（2026-10-09）

任意の端点補正を **OFF / 弱 / 中** で追加。既定OFF、F8 の運営表示内のボタンで切り替え、台ごとに両色共通で保存。
運営表示の開閉は従来どおり F8（レビューで F8 の挙動変更を取り消し、スタッフマニュアルの操作を維持）。
受信したworld XYで端点対応→One Euro→既存の任意予測→既存写像/クランプ。
単点・マウスには適用しない。OFFは並べ替えもフィルタ算術も行わない。
Camera/IMUの観測履歴には補正前の端点を保存する。
認識・候補・score・eligibility・winner・iPhone diagnostics・UDP形式/5005/5006/5007は変更していない。

## 実測と採用判断

このMac（arm64、Python/numpy 2.4.6、.NET 9.0.109）で再抽出・再測定。
Downloadsのforensic/analysisに対応する連続metadataは18録画・69,748行。
有効端点63,865サンプル、559連続区間。欠測・predicted=true・50ms超の穴で切る。
未記録predictedは未知のまま主評価に含め、既知falseだけでも別評価した。
静止proxyは126窓・3,780端点観測・21録画色系列、速い区間は79,009端点観測。
32録画・874行のdiagnostics-inboxも別抽出したが、短い異常窓は静止評価に使えない。
画像・原録画・全抽出・私有manifest・全測定JSONはコミットしない。

初回grid: min_cutoff={0.5,1,2,4}, beta={0,1,5,10,20,50}, d_cutoff={0.5,1,5,10}。
境界付近の追加grid: min_cutoff={0.25,0.5,1}, beta={2,3,4,5,7}, d_cutoff={10,20,60}。
各々端点対応あり/なしを比較、重複除去で **278設定**。
静止RMS・H20 RMSE・遅れp95の3目的Pareto frontは165設定。弱・中は両方front上。

単位は仮定11×6 world写像、遅れはms。端点対応あり、予測OFF。

| 設定 (min,beta,d) | 静止jitter RMS (低減率) | 追加遅れ 平均 / p95 | fast H0 RMSE | fast H20 RMSE | fast H40 RMSE | 追加はみ出し p95 / 最大 |
|---|---:|---:|---:|---:|---:|---:|
| OFF | 0.042637 (—) | 0 / 0 | 0 | 0.830744 | 1.334757 | 0 / 0 |
| 弱 (1,10,10) | 0.037382 (12.32%) | 2.134 / 8.241 | 0.030253 | 0.830675 | 1.334530 | 0 / 0.047002 |
| 中 (0.25,7,60) | 0.035775 (16.10%) | 2.848 / 9.950 | 0.029032 | 0.830987 | 1.335134 | 0 / 0.064474 |
| 強い候補 (0.25,4,10) | 0.031700 (25.65%) | 5.129 / 18.531 | 0.062041 | 0.830732 | 1.334185 | 0 / 0.249946 |
| 強い候補 (0.5,1,10) | 0.023078 (45.87%) | 15.377 / 52.344 | 0.175489 | 0.832511 | 1.330531 | 0.011864 / 0.252393 |

先に置いた大幅改善の目安「jitter≥25%低減、平均≤5ms/p95≤10ms、H20悪化≤2%」は未達。
ただし弱・中は21系列すべてで静止残差が低減し、中のH20悪化は0.029%、
速い振りの追加遅れp95は30Hz認識1周期の約0.30倍なので、小さな任意補正として採用した。
大幅低減を得る設定は遅れが大きく、プリセットにしない。中が全指標で弱より優れるわけではない。

predicted=false確認済みのみでは静止450端点・fast 15,184端点。
OFF静止RMS 0.040630→弱0.037677（7.27%）/中0.035252（13.24%）。
遅れ平均/p95は弱2.763/8.523、中3.642/10.338。
H20はOFF 0.405557→弱0.410063（+1.11%）/中0.411474（+1.46%）。
追加はみ出しp95はいずれも0、最大は弱0.016174/中0.017313。

端点対応は前回の**生端点**へのEuclidean距離の和を比較し、同点は受信順を維持。
18,804サンプルを並べ替えた。補正なしでは弱/中の最大はみ出しが0.269798/0.212457、
ありでは0.047002/0.064474へ低下（82.58%/69.65%）。静止RMSにはほぼ影響なし。
入替だけでは無向線分の表示は変わらないが、フィルタの履歴を誤った端点へ引き継がない。
大きい回転・認識対象の切替を物理的に正しく追跡できる保証はない。

## 評価の定義と限界

[One Euroの著者資料](https://gery.casiez.net/1euro/)を参考に、4成分を独立に処理。
微分は前回の生座標との差/dtをd_cutoffで低域化し、cutoff=min_cutoff+beta×abs(微分)。
dtは受信monotonic時刻。100ms超の穴・無効値・設定/台/入力源/位置補正の変更で履歴を初期化。
同じpacketや逆行時刻を二度処理しない。中のd=60は30Hz入力で微分の低域化を弱くする値。

静止proxy: 重複しない連続15点（約0.47秒）を線形fitし、両端の傾き≤0.25 world/s、
両端の残差RMS≤0.15の窓をOFFから固定。jitterは設定ごとに線形傾向を除いた残差の端点距離RMS。
fast: 前後3点の約200ms chord速度≥2 world/sの端点（区間端3点は除外）。
遅れはdot(raw-filtered,chord速度)/速度²。符号付きの幾何学的推定で、実測の描画/無線遅延ではない。
参照は因果的に端点順を対応させた未フィルタ軌跡のcapture+H線形補間。
OFFも同じ未来を目標とし、誤差は線分の同値な端点順を選んだ距離RMSE。
はみ出しは各時刻の前後100msに生端点が通った成分範囲から出た非負距離（OFFは0）。

30Hzの認識出力自体が参照で、物理正解ラベルはない。誤認識も含む。
60Hzの新しいcapture正解やネットワークジッタは捏造していない。
評価はpacket cadenceで、描画間引き、台ごとの写像、フィルタ＋任意予測の実機品質は未測定。
そのため既定OFFを維持。diagnostics別評価は41区間・fast 57端点、静止0で採用判断には不十分。

## 再現と検証

Git rootから実行（numpy、.NET SDKが必要）。画像を読み込む処理はない。

```bash
python3 -B PhoneSaber/tools/real_motion_extract.py "$HOME/Downloads" --output /tmp/jitter-filter-all.csv --manifest /tmp/jitter-filter-all-manifest.json
python3 -B PhoneSaber/tools/real_motion_extract.py "$HOME/Library/Application Support/PhoneSaber/diagnostics-inbox" --image-width 480 --image-height 640 --output /tmp/jitter-filter-triage.csv --manifest /tmp/jitter-filter-triage-manifest.json
python3 -B PhoneSaber/tools/endpoint_filter_eval.py --real /tmp/jitter-filter-all.csv --json /tmp/jitter-filter-grid.json
python3 -B PhoneSaber/tools/endpoint_filter_eval.py --real /tmp/jitter-filter-all.csv --min-cutoffs 0.25,0.5,1 --betas 2,3,4,5,7 --d-cutoffs 10,20,60 --include-report /tmp/jitter-filter-grid.json --json /tmp/jitter-filter-union.json
python3 -B PhoneSaber/tools/endpoint_filter_eval.py --real /tmp/jitter-filter-all.csv --presets --require-known-prediction --json /tmp/jitter-filter-presets-known.json
python3 -B PhoneSaber/tools/endpoint_filter_eval.py --real /tmp/jitter-filter-triage.csv --presets --json /tmp/jitter-filter-presets-triage.json
python3 -B PhoneSaber/tools/endpoint_filter_eval.py --real /tmp/jitter-filter-all.csv --presets --float32-io --json /tmp/jitter-filter-presets-f32.json
python3 -B PhoneSaber/tools/check_endpoint_filter_parity.py --real /tmp/jitter-filter-all.csv
python3 -B -m unittest discover -s PhoneSaber/tools -p 'test_*.py' -v
CLANG_MODULE_CACHE_PATH=/tmp/jitter-filter-clang-cache SWIFT_MODULECACHE_PATH=/tmp/jitter-filter-swift-cache PHONESABER_VERIFY_LOG_DIR=/tmp/jitter-filter-verify-final bash PhoneSaber/tools/verify_phone_saber.sh
```

C#9の実際のfilter/settingsを.NET stubでコンパイル。共有240サンプル/960 float成分と、
全実データ両preset＋reset 128,848サンプル/515,392成分でPython期待floatビットと一致。
実装は内部double状態、入出力float。float32 IO再評価でも静止RMS差は3e-9未満、遅れp95差は0.000013ms未満。
固定共有ベクトルは合成データのみ、約33KB。OFF符号付きゼロ、定数、step範囲、swap、gap、invalid、台別設定を確認。
C#単体CPU中央値（30万回×7、warmup別）: 未補正代入2.717ns→弱50.685ns/中50.710ns、
各0 B/sample。OFFのfilter呼出自体は7.469nsだが、実際のbridge OFFではその呼出を省く。
これは純粋な数値処理の測定で、Unity描画・PlayerPrefs・bridge全体の性能ではない。

EditMode testsにはOFFビット恒等、定数、step範囲、距離和/swap、gap、重複、台別保存、
履歴reset、両色の実際のblade Updateと生観測保持、共有ベクトルを追加。
Unityは利用しないためコンパイル/EditMode/PlayMode未実行。.NET stub成功をUnity成功とは扱わない。
Python toolsは66件、65成功・既存socketテスト1 skip。
公式Detectionとformal lossless **40/40 PASS**、diff check PASS。
公式全体はsandbox内でPASSにできず、iOS XCTestはCoreSimulatorService XPC/ログ拒否で未実行、
iOS Releaseは`sandbox_apply: Operation not permitted`に伴うSwiftUI macro server失敗。
広域Toolsは384件で8 failures/33 errors/1 skip（socket bind/receiver拒否、P2P process検出拒否、
launcher終了assertion等）。tracking E2Eはdisk preflight available=0 / required=1,040,187,392で失敗。
全面PASSとは扱わない。詳細ログは`/tmp/jitter-filter-verify-final/20261009-100022/`。

自己レビュー: 指定scopeとこの依頼された文書、新規scriptのfresh GUID .metaのみ。
production recognition/UDPに差分なし。pushは行っていない。

コミットは未完了。通常のgit add、明示git-dir指定の再試行、git commitがいずれも
`3D-Saber/.git/worktrees/3D-Saber-jitter-filter/index.lock`の作成を
`Operation not permitted`で拒否した。設定上のwritable rootと異なり実行時に拒否される。
変更はopt/jitter-filterの作業ツリーに残す。目的・before/after・検証を含むコミット文面は
`/tmp/jitter-filter-commit-message.txt`に準備済み。
