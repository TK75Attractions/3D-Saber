# PhoneSaber 縁日当日の運用手順(runbook)

## 困ったら (運営カード・全組み合わせ共通)

1. **ゲームが消えた** → 台 A/B の watchdog 起動なら5秒待つ。繰り返すなら STOP を作成し、隣の launcher ログと F8 のイベントログを保存して予備 PC へ。Editor は watchdog 対象外。
2. **剣が動かない** → **F8** (Mac は Fn+F8)で台・RED/BLUE・送信元・無音警告を確認。スマホのアプリを前面に戻し、送信開始・カメラ固定を確認。
3. **台が違う / 受信0** → PC とスマホを同じ A/B に。手動 IP は該当 PC の現在の IP。**Windows+iPhone は手動 IP 必須**。受信機停止なら二重起動を止める。
4. **通信できない** → 同じ Wi-Fi。学校 Wi-Fi が隔離なら PC のホットスポット + スマホをその SSID へ (IP を入れ直す)。Windows は Private network + [firewall script](../../windows/README.md#3-ファイアウォール-最初に1回管理者)。Mac+iPhone の P2P だけ不調なら同じ Wi-Fi + P2P優先 OFF。
5. **スマホが熱い / fps 低下** → 箱を開けて送風、排熱から離し給電を確認。危険が続くなら送信停止・冷却 / 予備端末。**F8 のログパスを控える** (現行 + `.1`〜`.4`も保存)。

詳細は §5。通常終了はゲームの Quit。watchdog 停止は Terminal / bat の Ctrl+C、またはスクリプト横に `Start-Saber-A.STOP` / `B.STOP` (両台なら `STOP`)。STOP は次回前に削除。

2026-10-06 のrepo統合: Git/Unity root は `3D-Saber/`、ツールは `PhoneSaber/`。Git root から `bash PhoneSaber/tools/verify_phone_saber.sh` を実行。旧 school-festival は履歴を保って統合・アーカイブ済み。

当日に「動かす」「おかしいときに直す」ための1枚です。調査の根拠は [FINDINGS.md](FINDINGS.md)、残作業は [STATUS.md](STATUS.md) にまとめています。
Mac の詳細診断はデスクトップの **`PhoneSaber Status.command`** (読み取りのみ)。

## 0. 端末の組み合わせと使い方(Mac / Windows / iPhone / Android)

PC(Mac か Windows)で Unity のゲームを動かし、スマホ(iPhone か Android)で剣を認識して座標を UDP 5005(赤)/ 5006(青)へ送る。
リポジトリは 3D-Saber の1つだけ(PhoneSaber/ はその中)。
反転はiPhoneの「詳細設定」→「検出」、Androidの閾値の下の「左右反転」「上下反転」で設定・保存する（既定OFF、Androidは停止中のみ変更可）。同じ台ではiPhoneとAndroidを同じ反転設定にする。

### 会場配置と台ごとの位置補正

- 台は **A / B の2台**。プレイヤーの立ち位置は画面から **約2.7〜3.1m**、プレイ範囲は **直径1.5m**。
- スマホのカメラは **画面下の箱に固定し、プレイヤーへ向ける**。同じ立ち位置でも、台ごとのカメラ距離・高さ・傾きで画像内の可動域が違うため、設置後に各台で位置補正する。
- **初期状態はOFF**。未設定・OFFでは従来どおりの座標を使う。認識や送信形式は変更しない。

1. 当日使うゲームを起動し、**F8**（Macは必要ならFn+F8）で台A/Bと受信を確認する。タイトルまたはメニューで、スマホの反転設定と箱の位置を確定してから行う。
2. **F7**（Macは必要ならFn+F7）または「4隅の測定を開始」を押す。既定は **RED**。青の剣を使うなら開始前に「BLUEの剣を採取」をONにする。両色の座標補正には同じ4隅を使う。
3. プレイヤーは所定の立ち位置で、点灯した剣の**中点**を、実際に振る範囲の **左上→右上→右下→左下** に順に持っていく。左右・上下はプレイヤーから見た画面方向で合わせる。各隅で静止してから **F7** / 「この隅を採取」を押し、**約1秒**そのまま待つ。次の隅へは表示が進んでから移動する。
4. 有効な受信が10件以上、約1秒にわたって続くことが必要。中点の中央値を採用する。受信不足・剣の動きはその隅を再採取する。4隅が交差・凹形・小さすぎる場合は保存せず、最初から測定し直す。
5. 成功すると **その台の4隅を保存してON** にする。剣を振り、画面全体に届くことを確認する。4隅の領域を1920×1080へ射影補正し、その後の感度・画面への写像は従来どおり。領域外は入力矩形の外側10%まで許容し、極端な座標はそこで止める。
6. **A / Bそれぞれで実施**。保存はこのPCのPlayerPrefs（`PhoneSaber.PositionCalibration.<台名>.Corners.v1` / `.Enabled`）。台なしは専用の空ラベルキー。Editorとビルドでは保存先が異なるため、当日使う方で測定する。次回起動でも同じ台の設定を読み込む。

F8内の「位置補正をON」で一時的にOFFにできる。「この台の位置補正をリセット」は4隅とON/OFFの保存を消し、従来の座標に戻す。他の台には影響しない。
測定のキャンセル・F8を閉じる・受信機が変わる・測定失敗では、前の保存済み設定を維持する。
**スマホ・箱が動いた、カメラ・画角・反転設定を変えた、立ち位置を変えた場合は必ず再測定**する。
実機なしの運営練習は [Saber Motion Replayの位置補正手順](../../tools/README_saber_motion_replay.md#位置補正の練習実機なし) を使う。

### 最初に1回だけ
- **Mac**: `git lfs install` → `git clone https://github.com/TK75Attractions/3D-Saber.git` → `3D-Saber/PhoneSaber/setup_mac.command` をダブルクリック(デスクトップに PhoneSaber のアイコン)。Unity Hub で 3D-Saber を開く(6000.3.9f1)。
- **Windows**: Git LFS を入れて同じく clone → Unity Hub で開く(6000.3.9f1)。[Windows 当日用キット](../../windows/README.md)にビルド(File > Build Profiles)、台 A / B の起動 bat、管理者 PowerShell の `Allow-PhoneSaber-Firewall.ps1`(Private profile の UDP 5005〜5007)とホットスポットの手順をまとめている。
- **iPhone**: Mac の Xcode で `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj` を開き、iPhone をつないで ▶。
- **Android(AQUOS sense9)**: 開発者向けオプションで USB デバッグを ON。Android Studio で `PhoneSaber/android` を開き、つないで ▶。

### 同じ Wi-Fi で 2 台(A / B)を並べるとき
- **各 PC の Unity Editor(Mac / Windows)**: Play 前に `Tools > PhoneSaber > Station > A` または `B` を選ぶ。PlayerPrefs の `PhoneSaber.Station` に保存され、次の Play から適用される。
- **ビルドしたゲーム**: 起動引数 `-phonesaberStation A`(もう一方は `B`)を付ける。Windows なら `Game.exe -phonesaberStation A`、Mac なら [Mac watchdog](../../mac/README.md) の `Start-Saber-A.command` / `B.command`。環境変数 `PHONESABER_STATION=A` でも指定できる。優先順は **起動引数 → 環境変数 → PlayerPrefs**。台名は16文字以内の英数字・`-`・`_`(通常は A / B)。Unity Console の探索/Bonjour/P2P 公開ログで台名を確認する。
- **各スマホ(iPhone / Android)**: アプリの接続設定の **「台」**を、その PC と同じ **A / B** にしてから送信を開始する。設定は保存される。指定した台の PC だけを自動探索する。iPhone の P2P と診断 relay も同じ台に限定される。
- **Windows Player の簡単な起動**: `PhoneSaber/windows/Start-Saber-A.bat` / `Start-Saber-B.bat` の先頭で exe の場所を合わせてダブルクリック(既定 `Builds/Windows/3D-Saber.exe`)。Editor と Player は同じ PC で同時に起動しない(UDP port が競合する)。
- **手動 IP は常に優先**。自動探索で見つからないときは、その台の PC の IP を確認して入力する(Windows + iPhone は従来どおり手動 IP が必要)。台設定は座標の受信を拒否する仕組みではなく、スマホの自動送信先を選ぶための設定。
- **1 台だけで従来どおり使うとき**: PC は `Station > None`、スマホは **指定なし**、起動引数/環境変数も未設定にする。どこにも台を設定しなければ従来と同じ動作。環境変数/起動引数を使った PC は、それを外してから None に戻す。

### 毎回
1. 本番は [Windows bat](../../windows/README.md) / [Mac command](../../mac/README.md) の台 A/B watchdog でビルド済みゲームを起動。Editor なら Unity の **Play** (watchdog 対象外)。Mac の built `.app` の P2P は bridge launcher の指定が必要。
2. スマホと PC を同じ Wi-Fi につなぐ(Mac + iPhone は P2P で直接つながるので不要)。
3. スマホのアプリで送信を開始する。

| 組み合わせ | 開始の操作 | PC の見つけ方 | 診断録画 |
| --- | --- | --- | --- |
| Mac + iPhone(推奨) | 「通常送信を開始」 | 自動(P2P、なければ Wi-Fi の Bonjour) | ○ |
| Mac + Android | 「開始」 | 自動(UDP 5007 の探索 / NSD) | × |
| Windows + Android | 「開始」 | 自動(UDP 5007 の探索) | × |
| Windows + iPhone | 「通常送信を開始」 | **Windows の IP を「手動IP」に入力**(iPhone は Bonjour で探すが Windows は出していない) | × |

- Windows の IP は、コマンドプロンプトで `ipconfig` を実行し「IPv4 アドレス」を見る。
- Android で見つからないときは、PC の IP を入れて「手入力を保存」。
- 学校などの Wi-Fi で届かないとき(端末どうしの通信が禁止されている): PC のモバイルホットスポットを ON にして、スマホをそこにつなぐ。
- 診断(Debug Recording・ガイド付き録画・自動解析)は iPhone + Mac だけ。
- 2026-10-06 時点で未確認: Android アプリの実機(AQUOS)での動作、Windows での座標受信。最初の試験では、アプリの「送信 fps」と Unity で剣が動くかを確認する。

### 当日のスマホ設定（長時間の無人送信）

- **両アプリ**: 接続設定の「起動時に送信を自動開始」を **ON** にする。既定は **OFF**（従来どおり手動開始）。同じ台 A / B と手動 IP が必要な場合は事前に保存し、カメラ・ローカルネットワーク等の許可を済ませる。
- **iPhone**: 設定 > 画面表示と明るさ > 自動ロックを「なし」にする。アクセスガイド（Guided Access）を有効にし、アプリを固定する。アクセスガイドの自動ロック設定も「なし」にする。
- **Android**: 画面固定／アプリ固定を有効にし、PhoneSaber を固定する（端末の設定名は機種による）。
- **両端末**: 通知を OFF、集中モード／おやすみモードを ON。充電しながら使い、通風を確保する。OS・端末の再起動後はスタッフがロックを解除してアプリを開く。

アプリは「送信していた」ことを保存する。通知・別アプリ・画面ロックなどの中断後、前面に戻ると自動復帰する。
クラッシュ／強制終了後も、アプリを再起動すれば保存した送信の意思に従って再開する（OSが終了したアプリを自動で起動する機能ではない）。
「起動時に送信を自動開始」が OFF でも、送信中だった場合の復帰は行う。
停止ボタンは保存した意思を消す。停止後に前面へ戻るだけでは再開しない。
自動開始が ON なら、停止済みでも次のアプリ起動時には開始する。
権限・カメラが使えるようになると「自動で送信を再開しました」と表示する。診断録画やガイド付き録画は自動再開しない。

カメラの中断・エラー・映像停止は自動で再接続する。再試行間隔は1・2・4・8・16秒、以後最大30秒。
連続15分復旧できなければ警告を残す。iPhone は「カメラを再開」、Android は停止→開始で再試行できる。
復帰が10秒安定すれば再試行回数をリセットする。Wi-Fi切断・切替時も送信の意思は消さず、
「PC を再接続中…」を表示して同じ台のPCを再探索・再解決する（探索は約30秒ごとにもやり直す）。
手動IPは自動探索より優先されるので、PCのIPが変わった場合はその設定を更新する。
LAN自動探索では選んだPC名を保存して復帰先を固定する。複数台がある会場では必ず台 A / B を指定し、
P2Pも同じ台へ復帰させる。別のPCへ移すときは停止して台・手動IPを設定する。
iPhone は既存のP2P優先／LAN fallbackを使い続ける。ネットワークの再接続には15分の打ち切りはない。
送信・復旧待ち中の前面画面は両アプリとも自動ロックを防ぐ。OSの強制ロック・電源切れを解除する機能ではない。

## 1. 全体の流れ

```text
[ゲームの座標]
iPhone PhoneSaberSender(認識 → "x1,y1,x2,y2")
  ├─ P2P 優先: peer-to-peer Wi-Fi(AWDL)UDP ─→ Mac の P2P bridge ─→ 127.0.0.1:5005(RED)/ 5006(BLUE)
  │                                          (Unity の Play で自動起動)
  └─ fallback: LAN の UDP(Bonjour `_phonesaber._udp` / 手動 IP)─→ Mac:5005 / 5006
                                                                      ↓
                                                     Unity InputPoint → SaberInputBridge → ゲーム

[診断(Debug Recording)]
iPhone で Stop ─→ triage bundle
  ├─ P2P 優先: bridge 内の「診断 relay」(`_phonesaber-dp2p._tcp`)─→ 127.0.0.1:8765
  └─ fallback: LAN(Bonjour `_phonesaber-diag._tcp`)─→ Mac:8765
        ↓
  受信側(Unity Play で自動起動 / Start PhoneSaber)→ inbox に保存 → 1ページ要約 `.report.md`(無料・すぐ)→ Codex 解析
```

- P2P は追加の経路です。bridge が無くても、iPhone と Mac が同じ LAN なら従来どおり LAN で届きます(`ios/PhoneSaberSender/P2P_BRIDGE.md`)。
- bridge は Unity が **5005 と 5006 の両方を受信できている間だけ** 動きます(`3D-Saber/.../InputPoint.cs` の `SetReceiverAlive`)。
- 受信側は診断専用です。Unity Play で自動起動し、止まっていてもゲームの座標は届きます。Unity を閉じても解析のため残ります。Start PhoneSaber からの単独起動もできます。
- inbox: `~/Library/Application Support/PhoneSaber/diagnostics-inbox/`。受信側のログ: `~/Library/Logs/PhoneSaber/latest.log`。

## 2. 前日までの準備(チェックリスト)

| | 項目 | 根拠 |
|---|---|---|
| ☐ | iPhone に main の最新の PhoneSaberSender を Xcode で入れる | `docs/claude/STATUS.md` |
| ☐ | Mac で単一の `3D-Saber` repo があり、Unity root 内の `PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py` を起動できる。違う場所なら環境変数 `PHONESABER_P2P_BRIDGE_SCRIPT` | `PhoneSaberP2PBridgeProcess.cs` |
| ☐ | bridge を一度 build しておく(Unity を開くと裏で1回 build。初回は数秒〜十数秒。`~/Library/Caches/PhoneSaber/p2p-bridge/` に保存) | `P2P_BRIDGE.md`「起動方法」 |
| ☐ | デスクトップに Start PhoneSaber と PhoneSaber Status などを置く: `Tools/install_phone_saber_launcher.command` を1回ダブルクリック(以前に入れた Mac でも、もう一度実行すれば足りない Status だけ追加。既存のリンクと動いている受信側はそのまま) | `PHONE_SABER_TRIAGE.md` |
| ☐ | Codex CLI がログイン済み(`PhoneSaber Status.command` で `[OK] Codex CLI`)。無くても受信と要約は動く | `PHONE_SABER_TRIAGE.md` |
| ☐ | **iPhone のローカルネットワーク許可**: 設定 > PhoneSaberSender > ローカルネットワーク を ON。拒否中は `P2P Failed (ローカルネットワークの許可が必要: 設定 > PhoneSaberSender)` と出る | `P2P_BRIDGE.md`「表示とログ」 |
| ☐ | **Mac のローカルネットワーク許可**: 初回に macOS が聞いてきたら、bridge を起動したアプリ(通常は Unity。単独起動なら Terminal)を許可。システム設定 > プライバシーとセキュリティ > ローカルネットワーク で確認 | `P2P_BRIDGE.md`「起動方法」 |
| ☐ | iPhone の **Wi-Fi は ON**(学校 Wi-Fi に参加しなくてよい。参加していなければ AWDL `awdl0` になる)。**インターネット共有(Personal Hotspot)は OFF**。モバイルデータは関係ない(P2P は cellular を使わない) | `P2P_BRIDGE.md`「実機での確認手順」A・F |
| ☐ | Mac は学校 Wi-Fi のままでよい(Codex などのネットは学校 Wi-Fi で続く) | `P2P_BRIDGE.md` 冒頭 |
| ☐ | iPhone の「**P2P優先**」toggle を ON(既定 ON、設定は保存される)。OFF にすると従来の LAN だけの動作 | `P2P_BRIDGE.md`「fallback の条件」 |
| ☐ | **カメラ**: 正立・固定。レンズに指をかけない。saberなしで肌・照明・画面・布の誤検出を確認し、必要なら画角・照明を調整する。赤い物の配置・隠蔽は不要 | [FINDINGS](FINDINGS.md)、[STATUS](STATUS.md) |
| ☐ | **露出**: 既定は自動。暗所の 1/50 秒では速い振りがぶれる。Debug Recording に露出上限（1/100・1/120・1/240 秒）の実験スイッチはあるが、既定を変える根拠は未確認。当日に設定を試行錯誤しない | [FINDINGS](FINDINGS.md) |

## 3. 当日の起動順

1. Mac: Unity で 3D-Saber を開き **Play**。bridge と診断受信側が自動で起動する。
2. Mac: **PhoneSaber Status.command** で受信側・bridge・UDP 5005/5006 を確認。診断だけ単独で使うときは **Start PhoneSaber** を開く。
3. iPhone: PhoneSaberSender を起動 →「P2P優先」ON →「通常送信を開始」。
4. 剣を振って、下の「正常な状態」と送信先 Mac 名を確認する。

終わるとき: Unity の Play を止める（bridge も止まる）。診断受信側は残る。
Start PhoneSaber から単独起動した受信側を止める場合は、そのウィンドウで Ctrl+C。

## 4. 正常な状態

**Windows / Mac 共通**: ゲーム画面(Editor は Game view)にフォーカスして **F8**(Mac は設定により Fn + F8)で運営 Overlay を開閉する。起動時は非表示。台名、RED / BLUE の直近1秒の packet 数、不正 payload を含む最終受信の経過・送信元 IP・解析 OK / NG、探索/Bonjour/P2P/受信機の状態を確認する。Mac の `127.0.0.1` は P2P bridge、それ以外は LAN。1秒を超えて受信がない色には日本語の警告が出る。Windows の Bonjour / P2P は未対応。

### 会場でのネットワーク確認（F8）

1. PCでゲームを開始し、スマホの送信先・台 A / B を確認する。
2. **iPhone**: 停止中に「詳細設定」→「接続・出力」の **「遅延計測モード」** をON →「遅延計測を開始」。**Android**: 停止中に反転設定の下の同名スイッチをON →「開始」。両方ともアプリ起動時はOFF、保存しない。検出・座標形式は通常と同じで、既存の `ts=<Unix epoch秒、小数6桁>;x1,y1,x2,y2` を使う。
3. ゲーム画面にフォーカスして **F8**。赤・青の点灯した剣を画角に入れ、両色が継続して認識される状態で **5秒以上**待つ。各色の「受信間隔」「片道時計差」の中央値 / p95 / 最大(ms)と日本語の判定を見る。各統計は直近5秒、最大2048サンプル。中央値は偶数個なら中央2個の平均、p95は昇順の `ceil(n×0.95)` 番目。
4. **受信間隔はスマホとPCの時計同期なしで有効**。PCのmonotonic時計で隣り合うdatagramの間隔を測る（不正payloadも含む）。完成した間隔は後のdatagramの時刻で窓に入り、表示の最大値は完成した間隔のみ。**判定には現在の無受信時間も含める**ので、次のpacketが来る前の詰まりも検知する。
5. **片道時計差 = PC受信直後の壁時計 − スマホの `ts`**。スマホ・PCの両方で自動日時/NTP同期を有効にし、同期状態・時計差を確認した場合だけ、F8の **「スマホとPCのNTP同期を確認済み（片道遅延も判定）」** にチェックする（起動時OFF）。同期の自動検証は行わない。未確認ならチェックせず **「間隔のみ」** の判定を使う。時計が合っていないと正の値でも誤差が入り、負値は時計差の警告。値は0に丸めない。NTP同期後も残る時計誤差より小さい遅延は判断できない。

| 判定 | 30fps運用の暫定目安（各色、境界値は下の悪い側へ） |
| --- | --- |
| **良好** | 間隔 p95 **< 50ms** かつ最大（現在の無受信時間を含む） **< 150ms**。間隔20サンプル以上。同期確認済みでtsがある場合は片道も同じ基準・20サンプル以上 |
| **注意** | 間隔 p95 **< 100ms** かつ最大 **< 500ms** で良好基準を外れる。片道を判定に含めるときは片道も同じ基準。20サンプル未満は「計測中」、負の片道時計差は「時計差を確認」 |
| **不良** | 間隔 p95 **≥ 100ms**、最大または現在の無受信時間 **≥ 500ms**、未受信、最新payload解析NGのいずれか。同期確認済みなら片道 p95 **≥ 100ms** または最大 **≥ 500ms** も対象 |

- `ts`がない／壊れている場合も受信間隔は表示・判定できる。片道は有効tsかつ座標解析OKのpacketのみ。片道サンプルがなければ同期チェックONでも「間隔のみ」。測定モードを切り替えた後は古いサンプルが窓から消えるまで5秒待つ。再起動・送信元IP変更で間隔/片道の窓はリセットする。
- iPhoneのtsは検出後、座標文字列を送信へ渡す直前に `Date().timeIntervalSince1970` を採る。AndroidもJNIで検出後、既存C++文字列生成の直前にepochを採る。カメラ撮影から検出までや、Unityの描画・ゲーム反映までの時間は片道差に含まない。送信キュー・P2P bridge・PCの受信スケジューリングは含む。**画面全体の遅延ではない**。
- 良好/注意/不良は現場用の目安で、通信だけを断定するものではない。剣の未検出やスマホの処理fps低下でも間隔が開く。「解析fps」「送信fps」と併せて読む。間隔のみの良好は一定の大きな片道遅延を検出できない。
- 注意/不良が続く場合は、送信先・ファイアウォール・発熱/fpsを確認する。Mac+iPhoneのP2Pなら同じWi-FiでP2P優先OFF（LAN）を試し、同じ点灯・動作条件で各経路を20〜30秒比較する。p95だけでなく最大の詰まりを見る。確認後はスマホの送信を止め、遅延計測モードをOFFにして通常運用へ戻す。

通常送信画面の端末状態行は、箱の中で数時間使うときの確認用です。
「発熱 正常 / やや高い / 高い / 危険」はOSの熱状態です（iPhoneは
nominal / fair / serious / critical、AndroidはNONE / LIGHT・MODERATE / SEVERE /
CRITICAL以上）。温度を摂氏で測った値ではありません。電池は残量%と
充電中・満充電・未充電を表示し、取得できない値は「不明」になります。

iPhoneの「カメラ / 処理 fps」、Androidの「解析 fps」は直近5秒の実測です。
処理中央値は同じ窓のiPhoneの検出処理時間（画素アクセスを含む）、AndroidのJNI呼び出し時間です。
送信fpsやUnityの受信fps、無線遅延とは別の値です。開始から3秒後以降、
要求カメラfpsの70%未満（30fpsなら21fps未満、iPhoneの60fps設定なら42fps未満）で
注意行が出ます。iPhoneはカメラ・処理のどちらが低くても対象です。
高い・危険はfpsに関係なく注意行が出ます。アプリは熱やfpsを理由にカメラfps・認識を自動変更しません。

**高い / 危険のときは箱を開け、送風・通風を増やし、プロジェクターの排熱から端末を離す。**
電池残量と充電表示を確認し、残量が減るなら給電・ケーブルを確認して充電する。
充電中も熱は増えるので通風を確保する。危険が続く場合は送信を手動停止し、
冷えるまで待つか予備端末へ交代する。fpsだけが低い場合もまず箱を開けて通風・電源を確認し、
送信fps・PCの受信状況と合わせて切り分ける。実機で熱状態が変わるとiPhoneのConsole /
AndroidのLogcat（DeviceHealth）にも記録されます。iPhoneのDebug Recordingには熱状態と電池も残ります。

| どこ | 正常 | 根拠 |
|---|---|---|
| iPhone「経路」 | `P2P Connected (awdl0 · Phone Saber Unity P2P (<Mac名>))`。同じ Wi-Fi にいると `en0` になることもある | `P2P_BRIDGE.md`、`P2PSender.swift` |
| iPhone の RTT 行 | `P2P RTT 中央値 … ms / p95 … ms / 最大 … ms / ping欠落 …%`(直近 40 回。欠落 0% が理想) | `ContentView.swift`、`P2P_BRIDGE.md` |
| Unity Console(起動時) | `[PhoneSaber][P2P] listening on UDP …`、`Bonjour registered …` | `P2P_BRIDGE.md`「実機での確認手順」B |
| Unity Console(接続時) | `peer connected …`、`RED received …` / `BLUE received …`(session ごとに1回) | 同上 D |
| Unity Console(10 秒ごと) | `[PhoneSaber][P2P] last 10s: BLUE=… RED=… maxGapMsRED=… maxGapMsBLUE=… peers=1`。座標は約 32 個/秒(10 秒で約 300)。`maxGapMs` は座標の届く間隔の最大(30 fps の本来は 33 ms) | `P2P_BRIDGE.md`「表示とログ」「遅延について」 |
| Unity Console(復帰時) | `peer alive (session …); iPhone can use P2P` | `PhoneSaberP2PBridge.swift` |
| Status ツール | 全部 `[OK]`。`maxGapMs` は 150 ms 未満で OK、150〜499 で WARN、500 以上で NG(この tool 独自の目安) | `Tools/phone_saber_status.py` |

## 5. 症状 → 原因 → 対処

「共通」は Mac+iPhone / Mac+Android / Windows+Android / Windows+iPhone のすべて。まず F8 で受信を確認します。

| 症状 | 主な原因 | 対処 |
|---|---|---|
| **入力なし・F8 が1秒超の無音** (共通) | 未送信・接続切れ・送信先違い | スマホのアプリを前面へ、送信開始。台・手動 IP・同じ Wi-Fi を確認。Windows+iPhone は `ipconfig` の IPv4 を手動 IP に入力 |
| **F8 の受信機が停止** (共通) | Editor / Player / 古い受信ツールのポート競合 | 同じ PC の重複起動を止める。受信機は自動再試行するので F8 の ON を確認。Mac は Status で UDP 5005/5006 の使用者を見る |
| **別の台が動く / 台を見つけない** (共通) | A/B 不一致・古い手動 IP | F8 の台とスマホの台を一致させ、送信先名を確認。PC は正しい台の launcher で起動し直す。手動 IP は台探索より優先なので修正 / 解除 |
| **学校 Wi-Fi だけ届かない** (LAN の全組み合わせ) | 端末間通信の隔離 | PC のホットスポット (Mac はインターネット共有) を使い、スマホをその SSID へ。PC の新しい IP を手動入力。Windows の共有接続も Private にする |
| **Windows だけ入力 / 探索なし** (Android / iPhone) | firewall / Public profile | 信頼する Wi-Fi を Private に設定し、管理者 PowerShell で [Allow-PhoneSaber-Firewall.ps1](../../windows/README.md#3-ファイアウォール-最初に1回管理者) を実行 (UDP 5005〜5007)。iPhone は探索せず手動 IP |
| **Mac+iPhone の P2P が未接続 / ラグ / LAN に戻る** | 権限・Wi-Fi OFF・bridge 未起動・P2P の詰まり | 両端のローカルネットワーク許可、iPhone の Wi-Fi ON / 個人用 Hotspot OFF、Mac Status を確認。built `.app` は bridge 指定を確認。直らなければ同じ Wi-Fi + P2P優先 OFF + LAN |
| **スマホが熱い / 処理 fps が落ちる** (共通) | 箱内の熱・排熱・給電不足 | 箱を開け送風、プロジェクター排熱から離し給電確認。高い / 危険が続くなら送信を停止して冷却 / 予備端末 (§4)。当日に認識設定を変えない |
| **F8 は受信 OK なのに剣がおかしい** (共通) | レンズが隠れた・カメラ移動・画角 / 照明 / ブレ | レンズと固定位置を確認し、剣が映る画角に戻す。照明・反転設定を確認。背景だけ / 人が入る状態の両方で試す。認識閾値は当日いじらない |
| **スマホアプリを閉じてから無音** (共通) | カメラ / 送信の停止 | ロック解除しアプリを前面へ戻して送信開始。台と送信先を再確認し、F8 の両色が復帰するか見る |
| **ゲームが crash / 消えた** (共通・built Player) | Player 異常終了 | watchdog なら5秒待つ。正常 Quit は再起動しない。繰り返すなら STOP を作り、launcher のログと F8 に出るイベントログ (全5世代) を保存、予備 PC へ。Editor は手動で Play し直す |
| **iPhone の診断 bundle / 解析が届かない** (Mac+iPhone のみ) | 診断受信側停止・録画が短い・転送 OFF | Mac Status の受信側を確認。Start PhoneSaber で診断受信側を起動し、数秒以上録画して Stop。無料の `.report.md` を先に確認。詳しくは [診断手順](../../ios/PhoneSaberSender/Tools/PHONE_SABER_TRIAGE.md) |

F8 の **イベントログ** は `Application.persistentDataPath/PhoneSaber/events.log`。受信機の開始・停止・再試行、色ごとの1秒超の途絶と復帰、送信元 IP / 経路変化、台名を UTC 時刻付きで残します。現行と `.1`〜`.4` の計5ファイル (各1 MiBまで、古い順に削除)。P2P bridge の `127.0.0.1` はスマホ本体の IP ではありません。書込失敗は F8 に表示され、受信は続きます。

## 6. 状態チェックツール(`PhoneSaber Status.command`)

`ios/PhoneSaberSender/Tools/PhoneSaber Status.command`(中身は `phone_saber_status.py`)。デスクトップのリンクは `install_phone_saber_launcher.command` が置きます。読み取りのみで、数秒で終わります。

- 受信側: `127.0.0.1:8765/health` が `ready` か。受信側が読み込んだコードが起動後に変わっていれば WARN。待機中なら Start PhoneSaber をもう一度ダブルクリックすると自動で起動し直す(解析中・受信中は止めない)。
- Unity: UDP 5005/5006 を誰が受信しているか(`lsof`)。Unity 以外なら NG(port の取り合い)。
- P2P bridge: `PhoneSaberP2PBridge` の process があるか。
- Unity Console(`~/Library/Logs/Unity/Editor.log`): 直近の Play 以降の `last 10s` 集計(直近5回の `maxGapMs`)、最後の接続イベント(`peer alive` / `no ping` など)、警告、診断 relay。Editor.log の行には時刻が無いので、ファイルの最終更新時刻を出します。
- 診断: inbox の最新 bundle、`.report.md` の有無、Codex 解析の結果(完了 / precheck 中止 / 実行中 / timeout)。
- git: 単一の 3D-Saber repo の branch、未commit、origin との差(fetch はしない)。
- Codex CLI があるか。

終了コードは NG あり 2、WARN のみ 1、すべて OK 0。Terminal から `python3 ios/PhoneSaberSender/Tools/phone_saber_status.py` でも動きます。

> 診断受信側の自動起動を無効にする場合は `PHONESABER_TRIAGE_RECEIVER=0`。Start PhoneSaber を別に開いても重複起動しません。
