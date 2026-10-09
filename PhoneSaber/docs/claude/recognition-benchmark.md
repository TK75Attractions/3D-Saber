# 現行 production 認識 PNG benchmark

基準 commit `35afe44c2d5f90cdde3ea6c62bf8c8b9dc369cd4`、recognizer source SHA-256 `6ef62bedb52c7c2cc31ce9007b48046cc7bae2810538cc9b35f56907812b9354`。
production C++ `phonesaber-png` を変更せず、step=2、既定設定で全 ORIGINAL を再認識した。
過去の metadata 出力・予測・tracking の再集計ではない。画像・ローカルパス・詳細診断はコミットしない。

## コーパスとラベル

全ファイル **640**、同一 SHA-256 を一度だけ数えた画像 **601**。
内訳: forensic 354、inbox 234、fixture 52。
annotated 等の除外: 8。重複コピーもすべて実行し、認識一致を確認した。
率の主表は画像の SHA-256 で重複除外。色別にラベルを付け、もう一方の色へ推定しない。
手ラベルの static/moving は saber、no_saber は剣なし。formal manifest の truth positive/negative も使用し、元 PNG hash を照合する。
ファイル名の dropout/false、保存時 detected、JNI の selected=null は正解ラベルとして使用しない。
JNI expectation の端点/source 回帰照合: 70 色別件、mismatch 0、欠けた画像 0。
欠けたラベル証拠 0 件。unknown は率の分母から除外する。
コーパス SHA-256 `72cd6839781474ce772891a118943b3529967b752dae72af4f9f50010c094976`。ラベル SHA-256 `c8fcae8db9563d0e05a78e60526ada986e50fcc40a3dee32c4760d337d0c7b22`。

## 色別の baseline（重複除外）

| 色 | 剣なし FP / n（率） | 剣あり出力 / n（検出率） | unknown 出力 / n |
|---|---:|---:|---:|
| red | 56 / 83（67.47%） | 29 / 30（96.67%） | 347 / 488 |
| blue | 9 / 32（28.12%） | 31 / 31（100.00%） | 346 / 538 |

剣あり検出率は出力の有無のみ。実剣の位置を当てた率ではなく、背景を winner にした出力も含み得る。
異常を選抜した撮影窓・同じ場面の隣接画像・formal fixture の混合集団であり、実運用の FP 率・独立 swing 数へ外挿しない。

## winner source と出力端点長（px、重複除外）

| 色 / ラベル | winner source: 件数 | 長さ n | min | mean | p50 | p95 | max |
|---|---|---:|---:|---:|---:|---:|---:|
| red / no_saber | `color-close`: 19, `color-mask`: 17, `color-sparse-raw`: 2, `connected-core`: 5, `core-halo`: 12, `core-line`: 1 | 56 | 10.20 | 42.68 | 38.00 | 112.45 | 142.35 |
| red / saber | `color-emitter`: 7, `color-mask`: 7, `color-sparse-raw`: 1, `connected-core`: 6, `core-halo`: 5, `core-line`: 3 | 29 | 19.70 | 62.02 | 54.92 | 118.27 | 138.92 |
| red / unknown | `color-close`: 12, `color-emitter`: 58, `color-mask`: 172, `color-sparse-raw`: 6, `connected-core`: 48, `core-halo`: 35, `core-line`: 16 | 347 | 10.00 | 56.92 | 49.40 | 105.22 | 415.18 |
| blue / no_saber | `color-mask`: 6, `connected-core`: 2, `core-line`: 1 | 9 | 50.36 | 58.05 | 54.15 | 76.03 | 76.03 |
| blue / saber | `color-emitter`: 1, `color-mask`: 3, `connected-core`: 13, `core-line`: 14 | 31 | 16.49 | 126.81 | 140.36 | 178.28 | 344.98 |
| blue / unknown | `color-emitter`: 45, `color-mask`: 12, `connected-core`: 113, `core-halo`: 23, `core-line`: 153 | 346 | 18.00 | 85.61 | 66.27 | 198.17 | 338.85 |

## 実行時間

全体 44.65 秒（CLI build 含む）。フレーム 640 件、平均 62.89 ms、p50 39.97 ms、p95 218.64 ms、最大 622.16 ms。
フレーム時間は subprocess 起動、PNG decode、recognition、JSON 出力、終了待ちを含む wall time。Python の JSON parse は除外。
純粋な認識 kernel 時間でも iPhone の実機時間でもない。逐次実行であり、他 worker の負荷・disk cache・起動コストの影響がある。
quantile は最近傍順位。長さは CLI selected の元画像座標から計算する。

## 再実行と比較

Git worktree root から実行（Python 標準ライブラリのみ、make/clang++/zlib が必要）。

```bash
python3 -B PhoneSaber/tools/recognition_benchmark.py --json /tmp/recognition-before.json
python3 -B PhoneSaber/tools/recognition_benchmark.py --json /tmp/recognition-after.json --compare /tmp/recognition-before.json
python3 -B -m unittest discover -s PhoneSaber/tools -p test_recognition_benchmark.py -v
```

既定の Downloads forensic / diagnostics-inbox / PhoneSaber 内 fixture を自動探索。`--downloads`、`--inbox` で場所を指定できる。
CLI build は worktree と source fingerprint ごとの一時ディレクトリに cache し、再実行で再利用する。ソース変更時は新規 build。高負荷時の compile 待ちは最大600秒。
report はこの文書へ出力（`--report` で変更可能）。詳細 JSON はローカル保存のみ。画像 ID は PNG の SHA-256、コピー ID は相対パスの hash。
比較ではコーパス・truth 変更と認識変更を区別し、候補全体・score・eligibility・winner・公開診断の JSON と double bit signature を比較する。
同じ画像/ラベルでの前後比較を行う。欠けた分母、unknown、認識失敗を成功として埋めない。

今回の比較: `{"added_images": 0, "recognition_changed_images": 0, "removed_images": 0, "runtime_p50_ms_after": 39.973875, "runtime_p50_ms_before": 70.291875, "same_corpus": true, "shared_images": 601, "truth_changed_images": 0}`
