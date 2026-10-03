# 受信側(診断パイプライン)ログ監査 — 2026-10-03

対象: この Mac の実ログ(読み取りのみ。何も消していない)。

- 受信ログ: `~/Library/Logs/PhoneSaber/triage-*.log`(13 本、2026-09-29 15:23 〜 2026-10-03 21:57、計 約 260 KB)
- Codex の実行記録: `~/Library/Logs/PhoneSaber/codex/*.json`(監査時 3,291 件 / 18 MB。2026-10-04 00 時には 3,620 件)
- 診断 inbox: `~/Library/Application Support/PhoneSaber/diagnostics-inbox/`(23 bundle / 62 MB)

受信ログの行には時刻が付いていなかった。そのため時刻は、ログのファイル名(起動時刻)、Codex 記録のファイル名(UTC)、bundle 名から求めた。

## 1. エラー / 警告の分類と件数

| 種類 | 件数 | 時期 | 状態 |
|---|---:|---|---|
| HTTP 413(bundle が大きすぎる) | 80(全件が 127.0.0.1 から) | 09-29 〜 10-03 15:56 | **修正済み** f88fa50(10-03 16:18)。simulator 上の XCTest が、実際に動いている受信側へテスト bundle を自動送信していた。それ以降 413 は 0 件 |
| Codex CLI_TIMEOUT(実際の実行) | 5 | 終了時刻: 10-02 01:45 / 02:24(600 s)、10-03 15:01 / 16:11(600 s)、10-03 16:43(手動再実行、1500 s) | 一部修正済み。0aeeb8f(25 分)と ad25839(digest を渡す、timeout 後に high で 1 回だけ再試行)。**10-03 16:01 の 600 s timeout は、受信側が古いコードのまま動いていたのが原因**(下の 2.1) |
| 旧形式の timeout(`timed out after 600 seconds`) | 1 | 10-01(起動時の自動再開で 234740_348) | 修正済み。起動時の自動再開は c62378e で廃止 |
| `Codex CLI failed (1): }`(原因の分からない失敗) | 3 | 10-02 01:0x | 修正済み。03628df で schema を修正し、subprocess の記録を保存するようにした(probe で INVALID_JSON_SCHEMA を 1 回確認) |
| precheck 失敗: frame context が 32 KB を超える | 2(同じ bundle 010049_190) | 10-02 | **現在も起きる**(古い iPhone ビルドの bundle。context は 41〜42 KB > 32,768 B)。上限は CLAUDE.md の規則どおり維持。メッセージを改善した(下の 3) |
| precheck 失敗: 時間方向の証拠がない | 1(155608_448、1 frame だけの録画) | 10-03 15:56 | 修正済み。a18816a で、約 1 秒未満の録画は自動送信しないようにした |
| 起動時の自動再開(startup_resume) | 4 | 10-02 02:14 | 修正済み(c62378e) |
| Codex が成功した実際の実行 | 7 / 12 | 265〜962 s | — |
| MODEL_UNAVAILABLE / USAGE_LIMIT / 認証エラー(実際の実行) | 0 | — | — |
| 409(重複アップロード)/ 400 / 415 / 403 | 0 | — | — |
| queue full(解析待ちの溢れ) | 0 | — | 3 件以上が同時に待つことはなかった。対策は不要と判断 |
| 1 ページ要約(report)の失敗 / Traceback | 0 / 0 | — | — |
| 途中で切れた受信(`.phonesaber-receive-*` の残り) | 0 | — | — |
| 終了行(STOP)が無い受信ログ | 1(triage-20260930-234908、10-01 14:57 に途切れる) | — | 原因は不明(Terminal の強制終了か再起動と推定)。再現していない |

受信できた bundle は 19 件(LAN 192.168.1.10 から 16 件、127.0.0.1 = P2P relay から 3 件)。

**解析結果(analysis_report.json)が無い bundle: 3 件**

- 20260930_234740_348: timeout。現在のコードでは precheck を通る(手動で再解析できる)
- 20261002_010049_190: context が 32 KB を超えるので、precheck で止まる(古い録画)
- 20261003_155919_297: 600 s と 1500 s の両方で timeout。現在のコードでは precheck を通る

## 2. 今も起きていた tool 側の問題

### 2.1 受信側が古いコードのまま動き続ける(実害あり)

10-03 14:51 に起動した受信側は、16:20 まで動いていた。その間に 0aeeb8f(15:14、Codex の timeout を 600 s から 1500 s に変更)が入った。
しかし 16:01 に届いた 155919_297 の解析は、**600 s で timeout した**(Codex 記録 `20261003T071157-*.json` は `timeout_seconds=600`)。
Start PhoneSaber をもう一度ダブルクリックしても「already running」と出るだけで、古いコードだと知らせる手段がなかった。

### 2.2 unit test が、本番の Codex 記録ディレクトリを汚していた

`~/Library/Logs/PhoneSaber/codex` にある 3,291 件のうち、実際の Codex 実行は **12 件だけ**。残り約 3,280 件は、テスト用の偽 CLI(`fake-codex`、`codex-spy`、`codex-reject-model`、`/missing/codex`)による記録だった。
全テストを 1 回走らせるたびに、約 50 件が増える(計測: `test_phone_saber_triage_codex` 29、`tracking_e2e` 10、`tracking_diagnostics` 5、`auto_repair` 4、受信 thread 経由 4 など)。
このディレクトリには保持期間の規則が無いので、増え続ける。本物の失敗記録も、その中に埋もれる。

### 2.3 ログが読みにくい

- Codex の失敗説明(stdout / stderr の末尾、最大 16 KB ずつ)が、1 回の失敗につき **2 回** 出ていた。triage-20261003-145141.log(83 KB)の大半がこれ。
- 413 / 400 / 409 は `[http] ... 413 -` としか出ず、大きさや理由が分からなかった。
- 行に時刻が無かった。
- precheck 失敗は英語の reason code だけ。`frame context exceeds its size limit` には、どのファイルが何 byte なのかも出ていなかった。

## 3. 直したこと(この branch)

1. **受信側の `/health`**: `pid`、`startedAt`、`codeChangedSinceStart` / `changedFiles` を返す。後者は、受信側が読み込んだ Tools の `.py` の内容 hash を起動時と比べる(mtime だけの変化は無視)。作業状態として `analysis.running` / `queued`、`uploadsInProgress`、`idle` も返す。受信中の bundle は、解析の queue に入るまで作業中として数える。
2. **Start PhoneSaber**: すでに受信側が動いているときは `/health` を見て、古いコードなら知らせる。次の条件をすべて満たすときだけ、古い受信側を SIGINT で止めて(Ctrl+C と同じ)、新しいコードで起動し直す。
   - launcher の lock に記録された pid と repo が、その受信側と一致する
   - `idle`(解析中・解析待ち・受信中のいずれも無い)
   それ以外の場合は、理由と手順を表示するだけで、何も止めない。`--no-restart` または `PHONESABER_NO_AUTO_RESTART=1` で、自動再起動を無効にできる。
3. **保存するログの各行に時刻を付ける**(`[YYYY-MM-DD HH:MM:SS] `)。Terminal の表示は従来どおり。
4. **Codex の失敗説明は 1 回だけ出す**。続く `[codex] analysis failed` 行は、最初の 1 行だけにした。
5. **413 / 400 / 409 は、理由を 1 行で出す**(Content-Length と上限、送信元)。
6. **precheck 失敗に日本語の 1 行を付ける**(`[PHONE_SABER][PRECHECK] 解析は中止(Codex は呼んでいない): … 対処 …`)。context の上限超過は、ファイル名・byte 数・上限を表示する。上限そのもの(32 KB)は変えていない。
7. **テストの隔離**: `PHONESABER_CODEX_LOG_DIR`(`phone_saber_codex_process.default_log_dir()`)を追加した。Codex に届きうる全テスト module に、一時ディレクトリへ向ける module fixture(`phone_saber_test_isolation.py`)を付けた。付け忘れを検出するテストも追加した。
8. **`phone_saber_status.py`**: `/health` の `codeChangedSinceStart` を優先して使う(従来の HEAD 時刻との比較は、古い受信側のときだけ使う)。解析中・待ちの件数、precheck の日本語説明、保存量(inbox の bundle 数と MB、Codex 記録の件数と MB、解析結果の無い bundle)を表示する。**何も消さない**。

## 4. ユーザーの判断が必要なこと

- テストで生成された約 3,300 件の Codex 記録(実行ファイル名が `fake-codex` / `codex-spy` / `codex-reject-model`、または `/missing/codex`)は、残したままにしてある。消すかどうかはユーザーが決める。本物の 12 件は `executable` が `/opt/homebrew/bin/codex` のもの。
- 実行中の受信側は、このコードより前の版で動いている。次に Start PhoneSaber を使うときは、一度 Ctrl+C で止めてから起動する(古い版は `/health` に新しい項目が無いので、自動再起動の対象にならない)。
- 解析結果の無い 234740_348 と 155919_297 は、手動で再解析できる(Codex の利用枠を使う)。
