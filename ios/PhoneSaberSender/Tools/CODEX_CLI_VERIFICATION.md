# PhoneSaber Codex CLI 障害の検証（2026-10-02 JST）

## 原因と修正範囲

`codex exec` は起動・認証に成功した後、APIのHTTP 400で終了していた。
正確なエラーは `type=invalid_request_error`, `code=invalid_json_schema`,
`param=text.format.schema`。メッセージは次のとおり。

```text
Invalid schema for response_format 'codex_output_schema': In context=(), 'required' is required to be supplied and to be an array including every key in properties. Missing 'tracking_assessment'.
```

PhoneSaber bundleなしの `Reply with OK.` でも旧schemaでexit 1を再現した
（最初の通常出力probe: 3.02秒、JSONL再現probe: 2.829秒）。
`_output_schema()` が返すCLI専用schemaの `required` を全propertiesに一致させた。
保存済みreportの旧形式互換性、preflight、tracking diagnostics、recognition、
threshold、repair gateは変更していない。Swift、Unity、UDPの変更はない。

Structured Outputsの全フィールドrequired要件は
[公式ドキュメント](https://developers.openai.com/api/docs/guides/structured-outputs)
でも確認した。

## 実際の呼び出しと設定

- executable: `/opt/homebrew/bin/codex`
- 解決先: `/opt/homebrew/lib/node_modules/@openai/codex/bin/codex.js`
- CLI version: `codex-cli 0.157.1`（各実行時に同じexecutableへ `--version`）
- analysis: `--model gpt-6-luna -c 'model_reasoning_effort="max"'`
- sandbox: `--sandbox read-only --ephemeral --skip-git-repo-check`
- approval: 旧呼び出しも修正後も独立したapproval引数なし。`exec`の実効値は`never`
  （通常出力probeのCLIヘッダーで確認）。
- cwd: repo直下ではなく、選択されたsummary/PNG/contextだけをコピーした
  一時ディレクトリの`input/`。subprocessの`cwd`と`--cd`は同じ。
- stdin: 最後の引数`-`、Python subprocessの`input=prompt`。shell経由ではない。
- stdout / stderr: 両方を独立してcapture。旧呼び出しに`--json`はなく、
  stderrの`ERROR:`にpretty JSONが出ていた。修正後は`--json`のJSONLを取得。
- 最終response: `--output-schema <temporary schema.json>` と
  `--output-last-message <temporary response.json>`。responseは別途JSON/schema検証。
  再分析前に前のresponseを消し、古いresponseの再利用を防ぐ。
- timeout: analysis 600秒、repair/review 900秒。最小probeは120秒。
  version取得には別途10秒上限。
- environment: 親processをそのまま継承し、overrideしない。ログに変数名を保存し、
  値は出さない。今回`CODEX_HOME`未指定で`~/.codex`が使われた。
  launcherもreceiverをrepoのcwdで起動し、environmentを継承する。
- auth: `codex login status`はexit 0、`Logged in using ChatGPT`。
- `~/.codex/config.toml`: 読み取りのみ。model既定値`gpt-6.1-sol`、effort既定値
  `high`を上記CLI指定がoverrideする。`service_tier=default`。
  ユーザーのMCP/plugin/feature/environment設定は継承し、変更していない。
  configの変更、削除、CLI更新、再ログインは不要だった。

起動、認証、version、config読み込み、model、effort、sandbox/approval、
structured output optionは、修正後の実CLI成功で確認した。
廃止済み引数、model access、usage limitによる障害ではなかった。
別modelへのfallbackは追加していない。

## エラー取得

旧処理は `(stderr or stdout).strip().splitlines()[-1]` のみを300文字に制限して
表示していた。pretty JSONの最終行が`}`なので、原因のtype/code/messageを失った。

共通の`phone_saber_codex_process.py`をanalysis/reanalysis/escalation/repair/reviewで使用する。
JSON、JSONL、`ERROR:`のpretty JSON、JSONLのmessage/error文字列内に包まれたAPI JSONを
構造的に解析し、重複を除いてmessage/type/code/paramを保持する。
失敗時の表示にはexit code、redacted command、version、model、effort、cwd、
UTC timestamp、elapsed、解析されたerror payload、stdout/stderr各16 KiBの末尾、
保存先を含める。

全文のredacted stdout/stderrと実行条件は
`~/Library/Logs/PhoneSaber/codex/<timestamp>-<uuid>.json`へ保存する。
ファイル権限は0600、新規ログディレクトリは0700。
bundleやscratchの外なので、証拠ファイルを変えず一時directory削除後も読める。
environment/config/authの資格情報値、credentialラベル、Bearer、JWT、API key、
cookie/authorization header、URL内credentialsを保存・表示前に除去する。
stdin内容、environment値、config/auth全文は診断としてdumpしない。

分類は`INVALID_JSON_SCHEMA`, `MODEL_UNAVAILABLE`, `AUTHENTICATION_FAILED`,
`USAGE_LIMIT`, `PERMISSION_DENIED`, `CLI_TIMEOUT`, `EXECUTABLE_MISSING`,
`CLI_START_FAILED`, `CLI_FAILED`, `MALFORMED_OUTPUT`。
model/effort拒否は既存のMODEL_UNAVAILABLE停止に接続し、再試行でmodelを変えない。
timeoutは部分stdout/stderrも保存し、存在しないexit codeを作らずnullと記録する。

## 実CLI・保存sessionの結果

1. 通常出力の最小probe: 同じLuna/max/read-only、CLI専用schemaのみ修正し、
   exit 0、`session_summary=OK`、11.80秒。
2. 修正後のJSONL最小probe: exit 0、`session_summary=OK`、9.671秒。
   `gpt-6-luna / max`を直接使用。モデルのfallbackなし。
3. 旧schemaを意図的に渡したJSONL probe: exit 1、2.829秒。
   保存した実CLI outputを新parserで再解析し、`invalid_request_error` /
   `invalid_json_schema`とmissing tracking_assessmentのmessageを確認。
4. 最新保存session `phonesaber_20261002_010049_190` はコピー上で既存の
   contextサイズ制限によるPRECHECK_FAILEDになった。制限は変更していない。
5. 直前の保存session `phonesaber_20261002_005850_489` のコピーは
   PRECHECK PASS → Luna/max read-only ANALYSIS開始 → 完了（265.3秒）。
   12画像、`needs_capture`、reanalysis/escalationなし。repairは呼び出していない。
   両元bundleは全ファイルのSHA-256とファイル一覧が前後一致。

最小probeの再実行（保存bundle不要）:

```bash
python3 ios/PhoneSaberSender/Tools/phone_saber_codex_probe.py
```

成功時は`CLI_PROBE_OK`とcapture保存先を表示する。有料の実model呼び出しを行う。
起動済みreceiverはPython moduleを読み込み済みなので、更新を反映するには再起動が必要。

## 自動検証

Python 3.12.1で実行。

- Tools全suite: 153/153 PASS（86.824秒）。
- subprocess suite: 15/15 PASS。success、exit 1 + stderr、pretty JSON、JSONL、
  JSONL内pretty JSON、malformed output/final report、model/effort拒否、auth/usage/
  permission、timeout部分出力、executable missingとanalysisでの保存、
  secret redaction、command内credential redaction、全wire objectのrequiredを検証。
- setup/launcher suite: 6/6 PASS。
- `git diff --check`とstage後の`git diff --cached --check`: PASS。

```bash
python3.12 -B -m unittest discover -s ios/PhoneSaberSender/Tools -p 'test_*.py'
python3.12 -B -m unittest discover -s tools -p 'test_*.py'
git diff --check
```

local検証ログ・sessionコピーはignoredな`.verify-logs/codex-cli/`に置き、commitしない。
CLI captureもrepo外に置く。変更はこのTools内だけ。
