# 赤の誤検出（剣なし画像）と winner 特徴量（2026-10-10、Claude）

`PhoneSaber/tools/recognition_benchmark.py --json` の出力（main 4ae0848、ORIGINAL 601 枚・重複除外、production C++ = Swift と bit 一致）から、赤の winner 候補の特徴量を比較した。認識コードは変更していない。

## ラベル付き winner の分布

| 特徴量 | 本物の赤い剣（29） | 剣なしの誤検出（56） |
|---|---|---|
| high_value_ratio | 最小 0.70、中央値 0.97 | p10 0.22、中央値 0.59 |
| mean_value | 最小 224、中央値 246 | 中央値 224、最大 240 |
| peak_value | 最小 253 | 最小 235、中央値 255 |
| point_count | 中央値 138 | 中央値 55 |
| winner source | color-mask 7, color-emitter 7, connected-core 6, core-halo 5 | color-close 19, color-mask 17, core-halo 12 |

## 仮の除外ルール（eligible 候補から除外し、残りの最高 score を採用）

| ルール | 剣 29 の残存 | 剣なし FP 56 の残り | unknown 347 で結果が変わる |
|---|---:|---:|---:|
| high_value_ratio ≥ 0.65 | 29 | 29 | 56 |
| high_value_ratio ≥ 0.70 | 29 | 28 | 78 |
| mean_value ≥ 220 | 29 | 37 | 36 |
| peak_value ≥ 250 | 29 | 41 | 28 |
| hv ≥ 0.6 かつ mean ≥ 215 かつ peak ≥ 250 | 29 | 26 | 53 |

## 判断

- 明るさ系の条件で赤の剣なし FP をほぼ半分にできる見込みがある。
- ただしラベル付きの剣は静止・近距離が中心で、**速振りのブレで high_value_ratio が下がる剣**の保持は未確認。速振りの候補消失は既存の主課題なので、そのまま production に入れない（PhoneSaber/CLAUDE.md の「証拠なしの production 修正をしない」）。
- 採用に必要な証拠: 速振り・遠距離・先端向けの赤い剣を含む新しい capture（ガイド付き録画 v3）に手ラベルを付け、このルールで剣が 1 枚も落ちないこと、formal 40/40 が保たれることを確認する。threshold はその分布から決める。
- 再現: `python3 PhoneSaber/tools/recognition_benchmark.py --json /tmp/bench.json` の後、本文の集計（winner = selected 端点の eligible 候補）を行う。

## 青（同じ benchmark、winner = eligible の最高 score）

| 特徴量 | 本物の青い剣（31） | 剣なしの誤検出（9） |
|---|---|---|
| peak_value | 全て 255 | 中央値 237 |
| core_support | 最小 0.19 | 中央値 0.016 |
| color_purity | 最小 0.27、中央値 0.58 | 中央値 0.16 |
| winner source | core-line 14, connected-core 13 | color-mask 6 |

| ルール | 剣 31 の残存 | FP 9 の残り | unknown 346 で変わる |
|---|---:|---:|---:|
| peak_value ≥ 250 | 31 | 3 | 9 |
| core_support ≥ 0.10 | 31 | 3 | 9 |
| **color_purity ≥ 0.25** | 31 | 3 | **1** |

`color_purity ≥ 0.25` は最もきれいに分かれるが、紙で拡散した青い剣は遠距離・速振りで白っぽくなり purity が下がる（そのため diffuser 青モデルがある）。ラベル付きの剣は近距離中心なので、遠い・淡い・速い青を含む手ラベル capture で保持を確認するまで採用しない。
