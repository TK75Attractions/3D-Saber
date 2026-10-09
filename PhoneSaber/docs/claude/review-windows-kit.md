# Windows 当日キット レビュー (2026-10-10)

対象: `PhoneSaber/windows/` (`Start-Saber-A.bat` / `Start-Saber-B.bat` / `Allow-PhoneSaber-Firewall.ps1` / `README.md`) と、
Unity Player 側の関係箇所 (`InputPoint.cs`、`PhoneSaberStation`、`PhoneSaberOperatorOverlay` の quit marker / `OnApplicationQuit`、
`Assets/Editor/PhoneSaberPlayerBuild.cs`)。Mac 側 `PhoneSaber/mac/saber-watchdog.sh` と機能を比べた。

**検証方法の制約**: この Mac には Windows 実機も `pwsh` も無い。cmd.exe / PowerShell 5.1 の挙動は静的に読んで判断した。
「要実機確認」と書いた項目は、会場前の Windows 実機リハーサルで確かめること。

行番号は修正前 (`5680fa9`) のもの。修正済みの項目は「修正」欄に書いた。

## 指摘一覧

| # | 重大度 | 場所 | 概要 | 状態 |
|---|---|---|---|---|
| 1 | 高 | `Allow-PhoneSaber-Firewall.ps1:52-59` | ビルド済み Player の受信 Block 規則を検出できない | 修正 |
| 2 | 中 | `Start-Saber-*.bat:31-33` | `timeout` が失敗すると再起動せずに監視が終わる。Ctrl+C の判定もコメントどおりに動かない | 修正 |
| 3 | 中 | `Start-Saber-*.bat:19-20` | STOP が残っていると、何も表示せずにウィンドウが閉じる | 修正 |
| 4 | 中 | `README.md:37` | ファイアウォールスクリプトを実行する順序の説明が無い (Player を初回起動する前に実行すると効かない) | 修正 (文書) |
| 5 | 低 | `Allow-PhoneSaber-Firewall.ps1:57` | Block 規則1件の無効化に失敗すると、スクリプト全体が中断する | 修正 |
| 6 | 中 | bat 全体 | 正常終了の印 (quit marker) が無い。Mac と同等ではない | 未修正 (提案) |
| 7 | 中 | `Allow-PhoneSaber-Firewall.ps1:28` | Domain プロファイルが対象外 (学校のドメイン参加 PC) | 未修正 (提案) |
| 8 | 低 | ps1 | 「すべての着信接続をブロック」、GPO、サードパーティ製ファイアウォールを検出しない | 未修正 (提案) |
| 9 | 低 | bat / README | Ctrl+C やウィンドウを閉じた停止がログに残らない。Ctrl+C の確認が出る時機は要実機確認 | 文書化 |
| 10 | 低 | bat:23 | `start` の失敗や DLL 欠落のダイアログでモーダル停止する。ハングは検出しない | 未修正 (記録のみ) |
| 11 | 低 | ps1:57 | `Disable-NetFirewallRule -Name` がワイルドカードとして解釈される (パスに `[` `]` を含む場合) | 未修正 (記録のみ) |
| 12 | 低 | README:61 | `Screen.sleepTimeout` が Windows standalone で効くか未確認 | 文書化 (電源設定を案内) |
| 13 | 情報 | bat:3 | `chcp 65001` の副作用 (既存の cmd から起動した場合) | 記録のみ |
| 14 | 情報 | ログ | 時刻が `%date% %time%` (ロケール依存のローカル時刻)。Mac は UTC の ISO 形式 | 記録のみ |

### 1. [高] Player の受信 Block 規則を無効化できない — 修正

- 場所: `Allow-PhoneSaber-Firewall.ps1:52-59` (旧)。条件は `$_.DisplayName -match 'Unity'` だけだった。
- 原因: Windows の「アクセスを許可しますか」ダイアログが作る規則 (`TCP/UDP Query User{GUID}<path>`) の表示名は、exe のファイルの説明になる。
  Unity の Windows Player では、これが `PlayerSettings.productName` (`ProjectSettings.asset:16` の `ServTechSlash`) になる。
  説明が空の場合は exe 名 (`3D-Saber.exe`) になる。どちらも `Unity` を含まないため、この条件で拾えるのは Editor (`Unity 6000.3.9f1 Editor` など) の規則だけだった。
- 失敗の流れ: 当日の Player を初回起動すると、ダイアログが出る。既定ではプライベートだけにチェックが入っている。
  チェックしなかったパブリックには Block 規則ができる。モバイルホットスポットは Public になることがある (2026-10-07 確認)。
  そのため、スクリプトを実行しても Player が Public で受信できず、F8 の表示が 0 のままになる。これは README §4 の予備経路そのものが動かない状況。
- 修正: 表示名 `Unity|ServTechSlash` に加えて、`Get-NetFirewallApplicationFilter` で調べた対象プログラムのファイル名
  (`Unity.exe` / `3D-Saber.exe`、大文字小文字は区別しない) でも判定するようにした。
  `Path.GetFileName` は .NET Framework では不正な文字で例外を投げるため、使わずに `-split '\\'` の最後の要素を使った。

### 2. [中] 再起動待ちの `timeout` が失敗すると、監視自体が終わる — 修正

- 場所: `Start-Saber-*.bat:31-33` (旧) の `timeout /t 5 /nobreak >nul` → `if errorlevel 1 goto stopped`。
- 失敗の流れ: 標準入力がリダイレクトされた状態で bat を起動すると、`timeout` は "Input redirection is not supported" で即座に errorlevel 1 を返す。
  該当するのは、タスク スケジューラ、OpenSSH、別のツールからの起動、`< nul` 付きの起動など。
  この場合、**1回目のクラッシュで再起動せずに `watchdog-stopped` で終わる**。
- Ctrl+C の扱いもコメントどおりではない。Ctrl+C で中断された `timeout` の終了コードは STATUS_CONTROL_C_EXIT (負の値) で、`if errorlevel 1` (1以上) にはならない。
  実際に停止を決めているのは cmd の「バッチ ジョブを終了しますか (Y/N)?」の方。
- 修正: 待機を1秒×5回のループにし、毎回 STOP を確認するようにした。Mac の `for delay in 1 2 3 4 5` と同等。
  `timeout /t 1 /nobreak >nul 2>&1 || ping -n 2 127.0.0.1 >nul` とし、待機に失敗しても監視は止めない。
  `timeout /t 1` は次の秒の切り替わりまでしか待たないため、5回で実際には4〜5秒になる。

### 3. [中] STOP が残っていると、何も起動せずに無言で閉じる — 修正

- 場所: `Start-Saber-*.bat:19-20` (旧)。
- 失敗の流れ: 前回 STOP で止めたあと削除を忘れたまま、当日ダブルクリックする。すると `:stopped` → `exit /b 0` となり、
  コンソールが一瞬で閉じてゲームが起動しない。ログには `watchdog-stopped` が残るだけで、操作者には理由が見えない。
- 修正: 最初の起動前に STOP を確認し、見つかったらファイルのパスと「削除してから再起動」を表示して `pause` するようにした。
  ログには `not-started STOP-file` を残す。ループ中に STOP で止まった場合も、画面に一言を表示し、ログを `watchdog-stopped STOP-file` (Mac と同じ語) にした。

### 4. [中] ファイアウォールスクリプトの実行順序 — README を修正

- Block 規則はダイアログに答えた時点で作られる。そのため、Player を初回起動する**前に**スクリプトを実行すると、その後のダイアログで Public 向けの Block 規則ができて、項目1と同じ状態になる。
- README §3 に次の内容を追記した: 「Player / Editor を一度起動してダイアログに答えた後に実行」「ダイアログではプライベートとパブリックの両方にチェック」「exe を作り直して新しいダイアログに答えたら再実行」。

### 5. [低] Block 規則1件の無効化に失敗すると、全体が中断する — 修正

- `$ErrorActionPreference = 'Stop'` が設定されているため、たとえば GPO 由来の規則で `Disable-NetFirewallRule` が失敗すると、その時点で終了し、残りの規則は処理されない。
  `Get-NetFirewallRule` の既定は ActiveStore で GPO の規則も返すが、`Disable` の既定は PersistentStore なので、GPO の規則は見つからずに失敗する。
- 修正: 規則ごとに `try/catch` で囲み、失敗したら `Write-Warning` を出して処理を続けるようにした。

### 6. [中・提案] 正常終了の印 (quit marker) が無い — 未修正

- Mac は `open -W` がアプリの終了コードを返さない。そのため `-phonesaberQuitMarker` を渡し、`PhoneSaberOperatorOverlay.OnApplicationQuit` (`Environment.ExitCode == 0` のとき) が書く印で正常終了を判定している。
- Windows の `start /wait` は子プロセスの実際の終了コードを ERRORLEVEL に返す。通常のクラッシュ (例外コード 0xC0000005 など) や、タスク マネージャーでの強制終了 (1) は非0になるので、再起動できる。
- 残るリスク (要実機確認): Unity の致命的エラーのダイアログ (D3D デバイス喪失、初期化失敗など) を閉じた後に、終了コード 0 で終わる経路があると、再起動されない。
- 未修正の理由: 印を書けなかった場合 (パス・権限など) には、逆に「正常な Quit のたびにゲームが再起動する」という当日の事故になる。Windows 実機で確認できないまま挙動を変えるのは低リスクとは言えない。
- 実機で確認してから入れる場合の案 (Unity 側は既に OS 非依存で対応済み):

  ```bat
  set "QUIT_MARKER=%~dp0Start-Saber-A.clean-quit"
  rem :launch の直後
  del /q "%QUIT_MARKER%" >nul 2>&1
  for %%I in ("%GAME_EXE%") do start "" /wait /D "%%~dpI" "%%~fI" -phonesaberStation A -phonesaberQuitMarker "%QUIT_MARKER%"
  set "GAME_EXIT=%ERRORLEVEL%"
  if "%GAME_EXIT%"=="0" if not exist "%QUIT_MARKER%" set "GAME_EXIT=1"
  ```

  `.gitignore` に `/PhoneSaber/windows/*.clean-quit` を追加する。引数の末尾が `\"` になるとマーカーの引数が壊れるので、パスはファイル名で終わらせること。

### 7. [中・提案] Domain プロファイルが対象外 — 未修正

- `Profile = 'Private,Public'` になっている。学校の管理 PC がドメインに参加していて、学校の LAN / Wi-Fi に接続している場合、ネットワークは **DomainAuthenticated** (Domain プロファイル) になり、規則が効かない。
  さらに GPO でローカル規則のマージ (`AllowLocalPolicyMerge`) が禁止されていると、ローカル規則はすべて無視される。
- 確認方法: `Get-NetConnectionProfile` の `NetworkCategory`、`Get-NetFirewallProfile -PolicyStore ActiveStore | Select Name,Enabled,AllowInboundRules,AllowLocalFirewallRules`。
- 提案: 当日用 PC は個人または非ドメインの PC にする。または、ホットスポット経路 (この場合は Public) を使う。
  `Profile` に `Domain` を加える変更は、送信元が LocalSubnet に限られるので安全性への影響は小さい。ただし README の方針 (Private/Public) を変えることになるため、判断を残した。

### 8. [低・提案] 受信を妨げる他の設定を検出しない

- 「Windows Defender ファイアウォール > すべての着信接続をブロックする」(`AllowInboundRules=False`) が有効だと、許可規則は無視される。
- サードパーティ製のセキュリティソフト (ESET、ノートン など) が独自のファイアウォールを持っていると、Windows の規則は効かない。
- 提案: スクリプトの最後に `Get-NetFirewallProfile` を読み、`AllowInboundRules -eq 'False'` のプロファイルがあれば警告を出す (読み取りだけで低リスク)。今回は範囲を絞って入れていない。

### 9. [低] Ctrl+C / ウィンドウを閉じた停止 — 文書化

- Mac は `trap ... INT TERM HUP` で `watchdog-stopped signal` をログに残す。cmd はシグナルを捕捉できないため、Ctrl+C → Y や、ウィンドウを閉じた停止はログに残らない。
- `start /wait` で GUI プロセスを待っている間、cmd は Ctrl+C を受け取っても、「バッチ ジョブを終了しますか」をゲームが終了した後に出す可能性がある (要実機確認)。
- Player は GUI サブシステムでコンソールに接続しないため、**bat のコンソールを閉じれば、ゲームを残したまま監視だけを止められる**。これを README に追記した。

### 10. [低] モーダルダイアログ・ハング — 記録のみ

- exe の存在確認は起動時の1回だけ。途中で exe が消えた場合 (再ビルド中など)、`start` は「Windows は ... を見つけられません」のモーダルダイアログで止まる。
- `UnityPlayer.dll` や `*_Data` が欠けている場合 (exe だけをコピーした場合など)、ローダーのダイアログ → 非0終了 → 5秒後に再起動、を繰り返す。
- 「応答なし」のハングは、Mac と同じく検出しない。当日は F8 の packet 数で異常に気づき、手動で終了する (タスク マネージャーで終了すると終了コード 1 になり再起動される)。

### 11. [低] `Disable-NetFirewallRule -Name` のワイルドカード

- Query User 規則の Name には exe のフルパスが入る。`-Name` はワイルドカードを受け付けるため、パスに `[` `]` を含むと一致しないことがある。その場合は項目5の修正で警告を出して処理を続ける。
- 通常の配置 (`Builds\Windows\3D-Saber.exe`) では問題にならない。

### 12. [低] スリープ防止 — 文書化

- `PhoneSaberOperatorOverlay.CreateAtStartup` は `Screen.sleepTimeout = NeverSleep` を設定している。しかし、この API はモバイル向けとして説明されており、Windows standalone で画面オフやスリープを抑止するかは未確認。
- README の最後に、Windows の電源設定 (画面オフ・スリープを「なし」、AC 電源) を案内する1行を追加した。

### 13. [情報] `chcp 65001`

- コードページは、`chcp` の後に読み込まれる行から UTF-8 として解釈される。1〜3行目は ASCII だけなので問題ない。bat は BOM なし・CRLF (`PhoneSaber/windows/.gitattributes` で `eol=crlf`) で、適切。
- 既存の cmd から起動すると、終了後もそのコンソールは 65001 のまま残る (実害は小さい)。Windows 10 より前のコンソールでは、日本語の echo が化けることがある (要実機確認)。

### 14. [情報] ログの時刻

- `%date% %time%` はロケール依存のローカル時刻で、`%time%` は10時前だと先頭が空白になる。Mac は `date -u` の ISO 8601 形式。
- `events.log` と突き合わせるときは、タイムゾーンに注意する。機械で解析しているツールは無い (grep で確認済み) ため、今回は変更していない。

## 問題が無いと確認した点

- **空白・日本語・`)` を含むパス**: `GAME_EXE`、`LOG_FILE`、`STOP_FILE` は、すべて `"..."` で引用している。
  括弧ブロック内の `echo ... "%GAME_EXE%"` も引用付きなので、`C:\Program Files (x86)\...` でもブロックが壊れない。
  `DisableDelayedExpansion` なので、パス中の `!` も保持される。`%~dp0` の展開結果に `%` があっても再展開されない。
- **`start "" /wait /D "%%~dpI" "%%~fI"`**: タイトルとして空文字を明示している。`/D` の値が末尾 `\"` になるが、これは `start` 自身が解釈するので問題ない (`start "" /D "%~dp0"` は定番の書き方)。
  ゲームに渡る引数は `-phonesaberStation A` だけ。`%%~fI` で `..\..` を正規化している。
- **終了コード**: `start /wait` は GUI プロセスの終了コードを ERRORLEVEL に返す。`set "GAME_EXIT=%ERRORLEVEL%"` は `for` の次の行なので、正しい値を取得している。負の値 (例外コード) も `"%GAME_EXIT%"=="0"` で非0と判定される。
- **台の指定**: `PhoneSaberStation.Resolve` は `-phonesaberStation` の次の引数を読む。`A` / `B` は `Normalize` を通る。優先順は README のとおり (起動引数 → 環境変数 → PlayerPrefs)。
- **ps1 の文字コード**: UTF-8 **BOM 付き**・CRLF。PowerShell 5.1 は BOM が無いと ANSI (日本語 Windows では CP932) として読み、日本語の表示や規則の Description が化けるが、BOM があるので問題ない。
- **PowerShell 5.1 / NetSecurity**: `New-NetFirewallRule` の各引数 (PolicyStore、Name、DisplayName、Description、Direction、Action、Enabled、Profile `'Private,Public'` (Flags enum への文字列変換)、Protocol、LocalPort、RemotePort、LocalAddress、RemoteAddress `LocalSubnet`、Program、Service、InterfaceType) は、すべて有効。
  `Set-NetFirewallRule` で `-Name` と `-DisplayName` を同時に指定するとパラメータセットが衝突するが、`NewDisplayName` に置き換えて回避している。正しい処理。
- **冪等性**: 固定の `Name` を `Get-NetFirewallRule -PolicyStore PersistentStore -Name ... -ErrorAction SilentlyContinue` で調べてから、New か Set のどちらかを実行するので、再実行しても規則は増えない。
- **管理者の確認**: `WindowsPrincipal.IsInRole(Administrator)` は PS 5.1 で動き、UAC で昇格していない場合は false になる。
- **LocalSubnet とホットスポット**: モバイルホットスポットの 192.168.137.0/24 は、PC 側のインターフェイスのサブネットなので LocalSubnet に含まれる。
- **Unity 側の受信**: `InputPoint` は `new UdpClient(port)` で排他的に bind する (ReuseAddress なし)。そのため、Editor と Player を二重に起動すると、後から起動した方の受信機が「停止」になり、F8 で気づける。
  受信ソケットからは送信しないので、Windows 特有の WSAECONNRESET (10054) は起きない。
  探索 (5007) は `ReuseAddress=true` のため、Windows では二重に起動しても両方が bind できる。両方が応答するが実害は無い。10054 は汎用の `catch` で処理し、ループを続ける。

## Mac ランチャーとの比較

| 機能 | Mac `saber-watchdog.sh` | Windows bat (修正後) |
|---|---|---|
| 終了判定 | `open -W` の終了コード + quit marker | `start /wait` の実際の終了コード (marker なし、項目6) |
| 再起動待ち | 1秒×5、毎秒 STOP を確認 | 同じ (今回修正) |
| STOP / 共通 STOP | `Start-Saber-X.STOP` / `STOP` | 同じ |
| 起動時に STOP が残っている | ログだけ残して終了 | 表示 + pause (今回修正。Mac より親切) |
| Ctrl+C | trap でログを残してから終了 (ゲームは残る) | cmd の Y/N で終了。ログは残らない (項目9) |
| ログ | UTC ISO、`watchdog-stopped STOP-file/signal` | ローカル時刻、`watchdog-stopped STOP-file` / `not-started STOP-file` |
| P2P bridge | 環境変数でスクリプトを渡す | Windows は対象外 (README のとおり) |

## 検証

- 静的レビューのみ (Windows 実機と `pwsh` が無い)。bat は BOM なし・CRLF、ps1 は BOM 付き・CRLF を維持していることをバイト単位で確認した。
- `git diff --check` は PASS。
- `PhoneSaber/tools/verify_phone_saber.sh` は iOS / Python / Unity diff-check が対象で、`windows/` は対象外。今回の変更は Windows キットと文書だけで、認識・UDP・Unity の Scene / Prefab / `.meta` には触れていない。
- 会場前の実機確認項目:
  1. Player の初回ダイアログでパブリックを未チェックにする → スクリプトを実行 → 「無効化: ... ServTechSlash」が表示され、ホットスポットで受信できること。
  2. `Start-Saber-A.STOP` を置いて bat を起動 → メッセージと pause が出ること。
  3. タスク マネージャーで Player を終了 → 5秒後に再起動され、ログに `exit-code=1` が記録されること。
  4. `cmd /c Start-Saber-A.bat < nul` で同じ操作をしても、再起動が止まらないこと。
  5. ゲームの Quit で `exit-code=0` になり、監視が終わること。
