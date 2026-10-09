# Fix B: raw PCA tail / robust body の offline 試作

基準 commit `35afe44c2d5f90cdde3ea6c62bf8c8b9dc369cd4`、source SHA-256 `6ef62bedb52c7c2cc31ce9007b48046cc7bae2810538cc9b35f56907812b9354`。
benchmark と同じ ORIGINAL **601 種類**で評価。コーパス SHA-256 `72cd6839781474ce772891a118943b3529967b752dae72af4f9f50010c094976`。
追加 observer を除いた候補・score・eligibility・winner・端点・診断の差分 **0**。

## 結論と gate

乖離時の robust 置換で、静止青9遷移の D 中央値 **138.68→12.65px**、最大 **249.36→31.62px**。ただし formal 端点条件は **5件失敗**した。
保守的な measured_body 代理 gate では、ラベル済み剣あり/なしの端点変更は **0件**、静止青の揺れも変わらない。unknown 赤のみ **4件**変更。
これは §4 B の証拠収集。production 採用・gate 通過を意味しない。順位・eligibility・候補生成・定数・UDP は変更しない。
baseline JSON の winner を固定し、端点だけを offline で置換する。検出の有無は全案で変わらないため、FP を消す修正ではない。
一律 robust 置換は効果を見る対照。divergent_body_pca は乖離条件だけで body PCA を使う対照。両案とも弱 tail・分離 LED 保護を保証しない。
measured_body は production core の一時コピーに観測処理を追加し、実際の dominant_body 選択点から PCA を再計算する。
robust interval の端点は元の raw 軸への投影であり、body 点だけの PCA とは別。両方を混同しない。

探索用 measured_body 条件（未校正、production threshold ではない）:

- raw/robust span 比 ≥1.8、body PCA 長 ≥12px、4点以上、body aspect ≥2、extent ≥0.10。
- retained ≥0.25、既存 body continuity ≥0.70、body/raw 軸の |cos| ≥0.90。
- tail は body 外の候補点。tail の core 比 ≤0.05、value≥220 比 ≤0.25。tail なしでは適用しない。
- raw 軸で3空 bin 以上離れ、各3 core 点以上の group が複数ある候補は拒否。bin は sample grid（step=2）。

core group の veto は mask 上の代理条件であり、物理的に分離 LED ではないという証明ではない。body の棒形状も実剣の意味ラベルを証明しない。

## 何枚の端点が変わるか（画像 SHA-256 で重複除外）

| 案 | 色 | 剣なし 変更 / n | 剣あり 変更 / n | unknown 変更 / n | 検出有無の変更 |
|---|---|---:|---:|---:|---:|
| production | red | 0 / 83 | 0 / 30 | 0 / 488 | 0 |
| production | blue | 0 / 32 | 0 / 31 | 0 / 538 | 0 |
| robust_all | red | 39 / 83 | 17 / 30 | 218 / 488 | 0 |
| robust_all | blue | 7 / 32 | 27 / 31 | 275 / 538 | 0 |
| divergent_robust | red | 3 / 83 | 2 / 30 | 17 / 488 | 0 |
| divergent_robust | blue | 1 / 32 | 14 / 31 | 114 / 538 | 0 |
| divergent_body_pca | red | 4 / 83 | 1 / 30 | 12 / 488 | 0 |
| divergent_body_pca | blue | 0 / 32 | 10 / 31 | 33 / 538 | 0 |
| measured_body | red | 0 / 83 | 0 / 30 | 4 / 488 | 0 |
| measured_body | blue | 0 / 32 | 0 / 31 | 0 / 538 | 0 |

## ほぼ静止の青: 現行 recognizer の再測定

| 匿名区間 | 案 | 隣接遷移 n | D p50 | p95 | max | ≥50px | 変更 frame |
|---|---|---:|---:|---:|---:|---:|---:|
| `0bc453ba2babbb76` | production | 9 | 138.68 | 249.36 | 249.36 | 6 | 0 |
| `ae369ef698cd7a7e` | production | 7 | 14.56 | 16.97 | 16.97 | 0 | 0 |
| `0bc453ba2babbb76` | robust_all | 9 | 12.65 | 31.62 | 31.62 | 0 | 12 |
| `ae369ef698cd7a7e` | robust_all | 7 | 14.14 | 17.89 | 17.89 | 0 | 6 |
| `0bc453ba2babbb76` | divergent_robust | 9 | 12.65 | 31.62 | 31.62 | 0 | 11 |
| `ae369ef698cd7a7e` | divergent_robust | 7 | 14.56 | 16.97 | 16.97 | 0 | 0 |
| `0bc453ba2babbb76` | divergent_body_pca | 9 | 10.77 | 29.12 | 29.12 | 0 | 9 |
| `ae369ef698cd7a7e` | divergent_body_pca | 7 | 14.56 | 16.97 | 16.97 | 0 | 0 |
| `0bc453ba2babbb76` | measured_body | 9 | 138.68 | 249.36 | 249.36 | 6 | 0 |
| `ae369ef698cd7a7e` | measured_body | 7 | 14.56 | 16.97 | 16.97 | 0 | 0 |

9遷移の区間は手ラベルの3/4/5 frame窓を合算、7遷移の区間は8 frame窓。窓間や欠測をまたがない。
D は直前との距離和が小さい端点対応で max(|ΔA|,|ΔB|) を計算。source pixel 単位、nearest-rank quantile。
小さい手ぶれを含む。旧 jitter-analysis の保存時の53.1→8.2pxとは recognizer の版・winner が違うため、その効果量を流用しない。
winner が実剣上にあることや端点の真の位置はこのラベルだけでは保証されず、揺れの低下を精度改善と同一視しない。

## 静止青9遷移区間の gate 観測

| frame | raw / robust px | body aspect | tail core / high 比 | core group | veto |
|---:|---:|---:|---:|---:|---|
| 390 | 41.23 / 42.00 | 3.97 | — / — | 1 | no_raw_body_divergence, no_tail |
| 391 | 166.01 / 30.00 | 2.01 | 0.20 / 0.43 | 2 | tail_core_supported, tail_high_supported, separated_core_groups |
| 392 | 167.23 / 34.00 | 2.48 | 0.18 / 0.48 | 2 | tail_core_supported, tail_high_supported, separated_core_groups |
| 397 | 166.00 / 34.00 | 2.36 | 0.19 / 0.42 | 2 | tail_core_supported, tail_high_supported, separated_core_groups |
| 398 | 159.49 / 34.00 | 2.25 | 0.04 / 0.34 | 2 | tail_high_supported, separated_core_groups |
| 399 | 166.01 / 32.00 | 2.32 | 0.18 / 0.42 | 2 | tail_core_supported, tail_high_supported, separated_core_groups |
| 400 | 166.01 / 30.00 | 2.02 | 0.19 / 0.44 | 2 | tail_core_supported, tail_high_supported, separated_core_groups |
| 407 | 152.21 / 30.00 | 2.16 | 0.15 / 0.31 | 2 | tail_core_supported, tail_high_supported, separated_core_groups |
| 408 | 347.12 / 58.00 | 4.14 | 0.05 / 0.33 | 4 | tail_high_supported, separated_core_groups |
| 409 | 180.59 / 40.00 | 3.08 | 0.06 / 0.48 | 2 | tail_core_supported, tail_high_supported, separated_core_groups |
| 410 | 126.12 / 20.00 | 1.43 | 0.16 / 0.36 | 2 | body_not_rod_proxy, tail_core_supported, tail_high_supported, separated_core_groups |
| 411 | 179.15 / 40.00 | 2.99 | 0.04 / 0.51 | 2 | tail_high_supported, separated_core_groups |

raw/robust が乖離する11 frameはすべて複数 core group と明るい tail を含む。弱 tail と非分離 LED の代理条件を同時に満たさない。
背景・反射にも発光支持があるため、閾値を緩めて短くしただけでは §4 B の gate 根拠にならない。

## 長剣・分離 LED と formal 期待端点

| 案 | formal 条件失敗 / 40 | 変更 fixture | 長剣/point LED の変更 |
|---|---:|---:|---:|
| production | 0 / 40 | 0 | 0 |
| robust_all | 10 / 40 | 16 | 6 |
| divergent_robust | 5 / 40 | 5 | 3 |
| divergent_body_pca | 2 / 40 | 2 | 2 |
| measured_body | 0 / 40 | 0 | 0 |

この表は既存 formal の検出/端点 tolerance 条件のみ。candidate type/rejection の正式検証は別途 unchanged production に実施する。
長剣/point LED は manifest failure class B/G と長い blue-frame-0600。未収集の長剣・分離 LED・端欠けへの安全性は未証明。

## 代理 gate の感度分析

| tail core 上限 | 軸 | 剣なし赤/青 変更 | 剣あり赤/青 変更 | 9遷移区間 p50 / max | formal 失敗 |
|---:|---:|---:|---:|---:|---:|
| 0.05 | 0.70 | 0/0 | 0/0 | 138.68 / 249.36 | 0 |
| 0.05 | 0.90 | 0/0 | 0/0 | 138.68 / 249.36 | 0 |
| 0.05 | 0.95 | 0/0 | 0/0 | 138.68 / 249.36 | 0 |
| 0.10 | 0.70 | 0/0 | 0/0 | 138.68 / 249.36 | 0 |
| 0.10 | 0.90 | 0/0 | 0/0 | 138.68 / 249.36 | 0 |
| 0.10 | 0.95 | 0/0 | 0/0 | 138.68 / 249.36 | 0 |
| 0.20 | 0.70 | 0/0 | 0/0 | 138.68 / 249.36 | 0 |
| 0.20 | 0.90 | 0/0 | 0/0 | 138.68 / 249.36 | 0 |
| 0.20 | 0.95 | 0/0 | 0/0 | 138.68 / 249.36 | 0 |

条件を少し動かしただけで変わる frame と境界の不連続を確認するための表。最良値を production へ採用する探索ではない。
全体の veto 理由（非排他）: `{"body_axis_disagrees": 18, "body_not_rod_proxy": 165, "body_pca_short_or_missing": 3, "body_support_weak": 17, "no_raw_body_divergence": 651, "no_tail": 515, "separated_core_groups": 238, "tail_core_supported": 240, "tail_high_supported": 237}`。

## 再現と制約

```bash
# recognition-benchmark branch のツール、または両 branch を統合した worktree で baseline 作成
python3 -B PhoneSaber/tools/recognition_benchmark.py --json /tmp/recognition-before.json
python3 -B PhoneSaber/tools/fixb_offline.py --benchmark /tmp/recognition-before.json --json /tmp/fixb-offline.json
python3 -B -m unittest discover -s PhoneSaber/tools -p test_fixb_offline.py -v
```

二つの task branch はそれぞれ origin/main 起点。fixb-offline branch 単独では、最初のコマンドだけ benchmark branch で実行する。
baseline の source fingerprint が現在の core と一致しない、原画像が失われた、observer が recognition を変えた場合は停止する。
observer の挿入位置は厳密に照合する。production ファイルを変更せず、一時コピーと実行ファイルは終了時に削除する。
詳細 JSON・PNG・絶対パスはコミットしない。候補 identity の時系列選好、予測、UDP、Unity はこの replay の対象外。
§4 B に必要な単独 body の妥当性、tail の物理的意味、未使用 capture での分離 LED 保護、境界の安定性、修正 A 後の順序条件は未解決。
測定時間 224.81 秒（offline CLI の一時 build と解析を含む）。
