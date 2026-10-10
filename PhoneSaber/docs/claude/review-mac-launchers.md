# Mac ランチャー レビュー (2026-10-10)

対象: `PhoneSaber/mac/` (`Start-Saber-A.command` / `Start-Saber-B.command` / `saber-watchdog.sh` / `README.md`)、
`PhoneSaber/setup_mac.command`、`PhoneSaber/ios/PhoneSaberSender/Tools/Start PhoneSaber.command` / `Start PhoneSaber P2P Bridge.command`。
Unity 側は読むだけ (`PhoneSaberOperatorOverlay.OnApplicationQuit` の quit marker、`PhoneSaberP2PBridgeProcess` の環境変数と `--exit-with-parent`)。

**検証方法**: 静的レビューに加え、`/usr/bin/open` を偽物に差し替えたコピーを、空白・日本語・括弧を含む一時パス
(`…/縁日 テスト (1)/repo/PhoneSaber/mac/`) に置き、macOS 標準の `/bin/bash` 3.2.57 で実際に動かした。
本物のゲームは起動していない。`~/Library` やシステム設定には触れていない。shellcheck はこの Mac に無い。

行番号は修正前 (`d3d4b58`) の `saber-watchdog.sh` のもの。

## 指摘一覧

| # | 重大度 | 場所 | 概要 | 状態 |
|---|---|---|---|---|
| 1 | 中 | `saber-watchdog.sh:34,56` | STOP が残っていると、画面に何も出さずに終了する (終了コード 0) | 修正 |
| 2 | 中 | `saber-watchdog.sh` 全体 | 二重起動を防がない (同じ台を2回、A と B を同じ PC、Ctrl+C 後にゲームが残ったまま再実行) | 修正 |
| 3 | 低 | `saber-watchdog.sh:30-33` | `.app` が無いとき、ログに残らず、ビルド方法の案内も無い | 修正 |
| 4 | 低 | `saber-watchdog.sh:47,56,29` | 正常終了・STOP・Ctrl+C で監視が終わったとき、画面に理由が出ない | 修正 |
| 5 | 低 | `saber-watchdog.sh:36` | bridge が無いとき、ログが `p2p-bridge=` (空) になる (README は yes のみ記載) | 修正 (`no`) |
| 6 | 低 | `saber-watchdog.sh:21` | ログが無制限に伸びる | 修正 (1 MiB で `.log.1` へ) |
| 7 | 中・要実機確認 | `saber-watchdog.sh:38` | `open --env` が無い古い macOS では、ゲームが一度も起動せず5秒ごとの再起動ループになる | 未修正 (記録) |
| 8 | 低・要実機確認 | — | crash ダイアログの「再度開く」を押すと、監視の再起動と合わせて2つ起動しうる | 未修正 (記録) |
| 9 | 情報 | `saber-watchdog.sh` | `PHONESABER_P2P_BRIDGE=0` は `open` 経由のアプリに伝わらない | 記録のみ |
| 10 | 情報 | `Start-Saber-*.command:3`、`saber-watchdog.sh:4` | `CDPATH` を export した環境で相対パス起動すると `SCRIPT_DIR` が壊れる | 記録のみ |
| 11 | 情報 | — | ハング (応答なし) は検出しない (Windows と同じ) | 記録のみ |

### 1. [中] STOP が残っていると無言で終了する — 修正

- 失敗の流れ: 前日に STOP で止めたまま削除を忘れ、当日ダブルクリックする。`while ! stopped` が一度も回らず、
  ログに `watchdog-stopped STOP-file` を書いて終了コード 0 で終わる。Terminal には何も表示されない。
  Terminal の設定が「正常終了なら閉じる」だとウィンドウも消え、操作者には理由が分からない (Windows 版 #3 と同じ問題)。
- 修正: 最初の起動前に STOP を確認し、見つかったファイルのパスと「削除してからもう一度ダブルクリック」を表示して、終了コード 1 で終わる
  (非0なので Terminal のウィンドウは残る)。ログは `not-started STOP-file`。
  ループ中に STOP で止まったときも、画面に「STOP ファイル (…) があるため、再起動せずに監視を終了します」を出す。ログの語は従来どおり。

### 2. [中] 二重起動 — 修正

- 受信 port は台によらず 5005 / 5006 で、`InputPoint` は排他的に bind する。2つ目のゲームは受信が「停止」になる。
  修正前は次の3通りで、2つ目のゲームがそのまま起動していた (偽 `open` で再現)。
  1. 反応が遅いと思って同じ `.command` を2回ダブルクリックする。
  2. A と B を同じ PC で起動する (README は禁止しているが、止める仕組みは無かった)。
  3. Ctrl+C で監視だけ止めた (ゲームは残る) あと、もう一度ダブルクリックする。
  1 と 2 では、どちらかが crash すると両方の監視がそれぞれ再起動するため、状態がさらに分かりにくくなる。
- 修正:
  - `mkdir` による lock (`PhoneSaber/mac/.saber-watchdog.lock/pid`)。A/B 共通。持ち主の PID が生きていて、そのコマンド行が `saber-watchdog.sh` を含む場合だけ有効とみなす。
    SIGKILL などで残った古い lock や、PID が再利用された lock は自動で取り除く。終了時 (EXIT trap) に、自分が持つ lock だけを消す。
  - 起動前に `ps -axww -o command=` を読み、`/3D-Saber.app/Contents/MacOS/` を含むプロセスがあれば起動しない
    (`.app` の名前は `GAME_APP` から取る)。`grep` を使うと grep 自身のコマンド行に一致するため、bash の文字列比較で調べている
    (最初の実装でこの誤検出を踏み、テストで気づいて直した)。
  - 拒否するときは理由を表示して終了コード 1。ログは `not-started already-running pid=…` / `not-started game-already-running`。
- 範囲: 判定は起動時の1回だけ。再起動ループ中は調べないので、crash 直後のプロセス後片付けと競合して再起動が止まることはない。
  Unity Editor の Play Mode は検出しない (README の注意のまま)。`mkdir` と PID 書き込みの間 (マイクロ秒) に別の起動が来た場合の競合は残るが、実害は無視できる。

### 3〜6. [低] 表示・ログの改善 — 修正

- `.app` が無いとき: ログに `not-started app-missing` を残し、「Tools > PhoneSaber > Build > macOS Player でビルドするか、`GAME_APP` を直す」を表示する。
- 正常終了で「ゲームが正常終了しました。監視を終了します。」、Ctrl+C / Terminal を閉じたときに「監視を終了しました。起動中のゲームはそのままです」を表示する。
- `p2p-bridge=yes|no` を明示する。
- ログが 1 MiB を超えていたら、起動時に `Start-Saber-X.log.1` へ移す (1世代)。1行は約150 byte で、`open` が即失敗する最悪の再起動ループでも 1 MiB に達するまで約5時間かかる。
- `.gitignore` に `/PhoneSaber/mac/*.log.1` と `/PhoneSaber/mac/.saber-watchdog.lock/` を追加した。

### 7. [中・要実機確認] `open --env` が無い macOS — 未修正

- `--env` が無い `open` は不明なオプションとして終了コード 1 を返す。すると `exit-code=1 open-exit-code=1` が5秒ごとに記録され、ゲームは一度も起動しない。
- この Mac (macOS 26.6.2) の `man open` には `--env` がある。いつの macOS から使えるかは確認できていない。
- 当日の Mac で `man open | grep -- --env` が1行出ることを確認すること。出なければ、`OPEN_ENV=()` に固定する (P2P 予備経路は使えなくなり、LAN だけで動く)。
  自動で後退する処理は、確認できない挙動を足すことになるので入れていない。

### 8. [低・要実機確認] crash ダイアログの「再度開く」

- macOS の「予期しない理由で終了しました」ダイアログで「再度開く」を押すと、LaunchServices が引数なしでアプリを起動する。
  監視も5秒後に再起動するので、2つ起動する可能性がある (2つ目は受信が「停止」)。当日は「無視」または閉じるだけにするよう、スタッフに伝える。

### 9〜11. 記録のみ

- `PHONESABER_P2P_BRIDGE=0` (bridge を止める変数) は、Terminal の環境に設定しても `open` で起動したアプリには伝わらない (`open --env` で渡しているのは SCRIPT だけ)。
  built `.app` で bridge を止めたい場合は、bridge スクリプトが見つからない状態にするか、watchdog に `--env PHONESABER_P2P_BRIDGE=0` を足す。
- `cd "$(dirname "$0")"` は、`CDPATH` が export され、かつ相対パスで起動したときに移動先を標準出力へ出し、`SCRIPT_DIR` が2行になる。
  ダブルクリックでは `$0` が絶対パスなので起きない。`setup_mac.command` などは `CDPATH=''` 付きで対策済み。
- ハング (応答なし) は検出しない。F8 の packet 数で異常に気づき、手動で強制終了する (強制終了は quit marker が無いので再起動される)。

## 問題が無いと確認した点

- **空白・日本語・括弧を含むパス**: `GAME_APP`、`LOG_FILE`、`STOP_FILE`、`BRIDGE_SCRIPT`、quit marker は、すべて引用されている。
  偽 `open` が受け取った引数を1つずつ記録し、パスが分割されていないことを確認した。
- **bash 3.2 と `set -u`**: 空配列は `${OPEN_ENV[@]+"${OPEN_ENV[@]}"}` で展開しており、bridge が無いケースでも unbound エラーにならない (実行で確認)。
  `setup_mac.command` の `"$@"` は、引数なし + `set -u` でも bash 3.2 でエラーにならない。
- **正常終了と crash の判定**: 「crash (marker なし・open 0) → 再起動 → 正常 Quit (marker あり) → 監視終了」と、
  「open 失敗 (1) ×2 → 再起動 → 正常 Quit」を実行し、終了コード・`restart` の数・ログが期待どおりだった。
  正常 Quit は再起動しない。crash と強制終了は再起動する。
- **STOP**: 再起動待ちの間に共通の `STOP` が作られると、再起動せずに終了する (実行で確認)。
- **Ctrl+C**: Python の `pty` で実際の端末を作り、`^C` を送って確認した。trap がログ `watchdog-stopped signal` を残し、終了コード 130 で終わる。
  `open -W` (偽物) のプロセスは EXIT trap で終了し、lock と marker の一時ディレクトリも消える。
  非対話の bash で `&` 起動したプロセスは SIGINT を無視するので、`open` が先に死ぬことはない。
  `open -W` を kill してもアプリは終了しない (macOS の仕様。要実機確認の項目に含めた)。
- **監視終了後に正常 Quit した場合**: marker のディレクトリは既に無いが、Unity 側は `File.WriteAllText` を try/catch で囲んでいるので、終了を妨げない。
- **P2P bridge の path**: `$SCRIPT_DIR/../ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py` は repo の実ファイルと一致し、絶対パスに正規化して `--env` で渡している。
  既に `PHONESABER_P2P_BRIDGE_SCRIPT` があればそれを優先する。Unity 側は bridge を `--exit-with-parent <pid>` で起動するので、ゲームが crash しても bridge は残らず、再起動後に重複しない。
- **実行権限**: `Start-Saber-A/B.command`、`saber-watchdog.sh`、`setup_mac.command` は Git 上で 100755。
- **`Start PhoneSaber.command` / `Start PhoneSaber P2P Bridge.command`**: symlink を辿ってから Tools ディレクトリを決め、`CDPATH=''` 付き。問題なし。

## 検証

- 偽 `open` を使ったシナリオ (12件): crash→正常終了、起動時 STOP、待機中 STOP、open 失敗×2→正常終了、`.app` なし、
  二重起動 (A 実行中に B)、SIGTERM、bridge なし、ログ回転、ゲームが残ったまま起動、古い lock、pty での Ctrl+C。修正後はすべて期待どおり。
  修正前の版では #1・#2・#5・#6 を再現した。
- `bash -n` (3.2) は PASS。shellcheck は未導入のため未実施。`git diff --check` は PASS。
- `PhoneSaber/tools/verify_phone_saber.sh` は mac ランチャーを対象にしていない。今回の変更は launcher・README・`.gitignore`・文書だけで、
  Swift の認識・UDP 形式・Unity の C# / Scene / Prefab / `.meta` には触れていない。

## 会場前の実機確認

1. `man open | grep -- --env` が1行出ること (#7)。
2. `Start-Saber-A.command` → ログに `p2p-bridge=yes`、F8 で P2P bridge が ON。ゲームの Quit で「正常終了しました」と表示され、ログに `exit-code=0 … clean-quit=yes`。
3. アクティビティモニタで「強制終了」→ 5秒後に再起動し、ログに `exit-code=1 open-exit-code=0 clean-quit=no`。crash ダイアログが出たら「再度開く」は押さない (#8)。
4. ゲーム実行中に Terminal で Ctrl+C → ゲームは残り、「監視を終了しました」と表示される。続けて `.command` をダブルクリック → 「既に起動しています」と出て、2つ目は起動しない。
5. `Start-Saber-A.STOP` を置いてダブルクリック → パスが表示され、ウィンドウが残る。確認後に STOP を削除する。
6. 1台目の監視を動かしたまま、同じ PC で `Start-Saber-B.command` → 「既に監視が動いています」と出る。
