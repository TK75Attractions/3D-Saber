# PhoneSaber 縁日当日の運用手順

当日スタッフ向けのやさしい手順は [STAFF_MANUAL.md](../STAFF_MANUAL.md) を参照してください。

## 困ったら

1. **ゲームが消えた** → A/B の監視起動なら5秒待つ。繰り返すなら監視を止め、ログを保存して予備PCへ。
2. **剣が動かない** → **F8**（Macは必要ならFn+F8）。台・RED/BLUE・送信元・無受信警告を確認。スマホを前面に戻し、送信とカメラ固定を確認。
3. **違う台が動く／受信0** → PCとスマホの台を同じA/Bに。手動IPはそのPCの現在のIP。**Windows+iPhoneは手動IP必須**。受信機が停止なら二重起動を止める。
4. **通信できない** → 同じWi-Fiへ。学校Wi-Fiで端末間通信が禁止ならPCのホットスポットへ切り替え、IPを入れ直す。WindowsはPrivate設定と[ファイアウォール許可](../../windows/README.md)。Mac+iPhoneのP2P不調なら同じWi-Fi＋P2P優先OFF。
5. **スマホが熱い／fps低下** → 箱を開けて送風、排熱から離し給電を確認。危険が続くなら送信停止・冷却／予備端末。

ログの場所はF8に表示。イベントログの現行＋`.1`〜`.4`と、起動スクリプト横のログを保存する。監視停止と通常終了は[終了手順](#6-終了手順)、詳しい対処は[トラブル表](#5-トラブル表)。

## 1. 前日までの準備

Git／Unityのルートは `3D-Saber/`、スマホアプリ・ツールは `PhoneSaber/`。旧school-festivalは履歴を保って統合・アーカイブ済み。調査は[FINDINGS](FINDINGS.md)、確認待ちは[STATUS](STATUS.md)。

### PCごと

- ☐ Git LFS付きで単一repoを用意し、Unity **6000.3.9f1**で開く。事前検証はルートで `bash PhoneSaber/tools/verify_phone_saber.sh`。
- ☐ **Mac**: `PhoneSaber/setup_mac.command`を実行。[Macキット](../../mac/README.md)で最新版のゲームをビルドし、A/Bの `.command` のアプリ先を合わせる。
- ☐ **Windows**: [Windowsキット](../../windows/README.md)でビルド一式、A/Bの `.bat`、Privateネットワーク、UDP **5005（赤）／5006（青）／5007（探索）**の許可を用意する。ビルド・管理者操作・予備ホットスポットの詳細も同README。
- ☐ PC・スマホ・箱にA/Bの目印を付ける。同じPCでEditorとPlayer、複数launcher、座標受信ツールを同時起動しない。
- ☐ Editorで試す場合はPlay前に `Tools > PhoneSaber > Station > A/B`。ビルドは台別launcherを使う。台設定の優先順は **起動引数 `-phonesaberStation` → 環境変数 `PHONESABER_STATION` → PlayerPrefs `PhoneSaber.Station`**。台名は16文字以内の英数字・`-`・`_`。
- ☐ 1台で台指定なしに戻す場合は、起動引数・環境変数を外し、Editorを `Station > None`、スマホを「指定なし」にする。

### スマホごと

- ☐ **iPhone**: MacのXcodeで `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj` を開き、実機に最新版を入れる。カメラ・ローカルネットワークを許可する。[iPhoneの通信・計測README](../../ios/PhoneSaberSender/README.md)も参照（単独計測ツールはUnityと同時使用しない）。
- ☐ **Android（AQUOS sense9）**: [Android README](../../android/README.md)に従い、USBデバッグ・ビルド・実機への導入・カメラ許可を済ませる。
- ☐ 自動ロックと通知を抑える。iPhoneは自動ロック「なし」、アクセスガイドとその自動ロック「なし」。Androidは画面固定／アプリ固定。両方とも集中／おやすみモード、給電、通風を用意する。
- ☐ 正立させ、レンズを隠さず固定。剣なし・人ありで肌・照明・画面・布の誤検出を確認する。赤い物を片付けるだけでは解決しない。
- ☐ 露出は既定の自動を維持。当日に閾値や露出を試行錯誤しない。診断録画には上限1/100・1/120・1/240秒の実験設定がある。暗所の1/50秒では速振りがぶれるため、事前に比較する（照明のちらつきは東日本1/100、西日本1/120を確認）。

### 組み合わせごと

| 組み合わせ | 送信先の選び方 | 開始ボタン | 診断録画 |
| --- | --- | --- | --- |
| Mac＋iPhone（推奨） | P2P優先、使えなければ同じWi-FiのBonjour／手動IP | 通常送信を開始 | ○ |
| Mac＋Android | 同じWi-Fi、UDP 5007探索／NSD | 開始 | × |
| Windows＋Android | 同じWi-Fi、UDP 5007探索 | 開始 | × |
| Windows＋iPhone | 同じWi-Fi、WindowsのIPv4を手動IPへ | 通常送信を開始 | × |

**手動IPは自動探索より優先**。Windowsは `ipconfig` で使用中の接続のIPv4を見る。Androidは入力後「手入力を保存」、空欄保存で探索へ戻る。台指定は自動送信先を選ぶ機能で、違う台からのUDP受信を拒否する機能ではない。iPhoneのP2P・診断relayも同じ台に限定される。

- ☐ **Mac＋iPhoneのP2P**: [P2P手順](../../ios/PhoneSaberSender/P2P_BRIDGE.md)でbridgeを一度ビルドする。Editorは1セッションに1回裏でビルドし、キャッシュは `~/Library/Caches/PhoneSaber/p2p-bridge/`。標準スクリプトは `PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py`。**built .appは `PHONESABER_P2P_BRIDGE_SCRIPT` に利用可能なlauncherを指定**する。なければLANで使う。
- ☐ iPhoneのWi-FiはON、インターネット共有はOFF、P2P優先はON（既定ON・保存）。同じSSIDへの参加は不要、モバイルデータは使わない。Macは学校Wi-Fiでインターネットを使い続けられる。Macのローカルネットワーク許可はbridgeを起動するUnity／Terminalに与える。
- ☐ **診断はMac＋iPhoneのみ**。[診断手順](../../ios/PhoneSaberSender/Tools/PHONE_SABER_TRIAGE.md)でデスクトップのStart PhoneSaber／PhoneSaber Statusを用意する。既存Macもinstallerの再実行で不足リンクだけ追加できる。Codex CLIのログインを確認（なくても受信と無料要約は動く）。
- ☐ 本番の組み合わせで両色、A/Bの取り違え防止、通常終了・異常終了、Wi-Fi切替・前面復帰・アプリ再起動・停止後の非再開を試す。**AQUOS実機・Windows受信・長時間運転は未確認**。Mac＋iPhoneの台指定は10-06に確認済み。
- ☐ 剣なしの運営練習は[Saber Motion Replay](../../tools/README_saber_motion_replay.md)。実機送信を止めて使い、練習の位置補正は最後にリセットする。

## 2. 当日の起動順

**A台で1〜5を終えてから、B台でも同じ順に行う。**

1. PCとスマホを所定位置に置き、給電・通風・レンズを確認。2台は背中合わせ、個別入場・一方通行。立ち位置は画面から **約2.7〜3.1m**、プレイ範囲は **直径1.5m**。スマホは画面下の箱からプレイヤーへ向ける。背後の幕と床の範囲表示も確認する。
2. 前回のSTOPファイルを削除し、その台の[Mac launcher](../../mac/README.md)／[Windows launcher](../../windows/README.md)でゲームを起動。Editorなら台設定後にPlay（監視対象外）。ゲームは背景でも受信し、起動時にスリープ防止を設定する。
3. LANはPCとスマホを同じWi-Fiへ。Mac＋iPhoneのP2Pは上の準備条件で接続。スマホを開き、台・手動IP・送信先PC名を確認して開始する。
4. ゲーム画面（EditorはGame view）にフォーカスして **F8**。台名と両色の受信機を確認する。Macは必要なら **Fn+F8／Fn+F7**。
5. 次の本番前チェックをすべて行う。A/Bで互いの台が動かないことも確認する。

## 3. 本番前チェック

### スマホ設定

- ☐ **台**: PCと同じA/B。古い手動IPがないか、送信先名も確認。
- ☐ **左右反転・上下反転**: 実際の画面方向に合わせる。既定OFF、保存される。iPhoneは「詳細設定」→「検出」、Androidは閾値の下（停止中のみ変更可）。同じ台でiPhone／Androidを交代するなら同じ設定にする。位置補正前に確定する。
- ☐ **起動時に送信を自動開始**: 当日はON（既定OFF）。権限・保存した台・IPを確認。
- ☐ **遅延計測モード**: 下の確認時だけON、確認後は停止して **OFF** に戻し、通常送信を再開する。起動時OFF・保存なし。

### F8の値

起動時は非表示。F8で開閉し、シーンが変わっても表示を維持する。通常ビルドで使える。

| 見るもの | 合格の確認 |
| --- | --- |
| 台 | 該当するA/B |
| RED 5005／BLUE 5006 | 両色を点灯して画角に入れ、直近1秒のpacket数が増え、最終payloadが **解析OK** |
| 最終受信・送信元 | 継続受信、1秒超の無受信警告なし。送りたいPC・経路と一致 |
| 受信機・探索 | 両色の受信機ON。Android探索はUDP 5007。WindowsのBonjour／P2P「未対応」は正常 |
| 経路 | Macの `127.0.0.1` はP2P bridge、それ以外はLAN（IPによる目安。スマホ本体のIPとは限らない） |

packet数・最終受信時刻には不正payloadも含む。初回受信前は開始からの経過を表示。受信・解析OKだけでは認識品質を保証しない。剣なし・未検出時は0でもよいが、点灯した両色でゲーム内の向き・位置・四隅を確認する。

### 会場でのネットワーク確認（F8）

1. 送信を止め、iPhoneは「詳細設定」→「接続・出力」の「遅延計測モード」をON→「遅延計測を開始」。Androidは反転設定の下の同スイッチをON→「開始」。通常座標に既存の `ts=<Unix秒、小数6桁>;` が付く。
2. 両色を継続認識させ、F8で **5秒以上**待つ。色別の直近5秒の「受信間隔」「片道時計差」の中央値／p95／最大(ms)を見る。
3. 間隔は時計同期不要。片道は **PC受信の壁時計−スマホts**。両方の自動日時／NTP同期と時計差を確認した場合だけ「スマホとPCのNTP同期を確認済み」にチェック（起動時OFF・自動検証なし）。未確認なら「間隔のみ」を使う。負値は時計差の警告で、0には丸めない。

| 判定 | 30fpsの暫定目安（各色。境界値は悪い側へ） |
| --- | --- |
| **良好** | 間隔p95 **<50ms**、最大と現在の無受信時間 **<150ms**、20サンプル以上。同期確認済みでtsがある場合は片道も同基準 |
| **注意** | p95 **<100ms**、最大と現在の無受信時間 **<500ms**で良好に届かない。片道も判定するなら同基準。20サンプル未満は「計測中」、負の片道値は「時計差を確認」 |
| **不良** | p95 **≥100ms**、最大／現在の無受信時間 **≥500ms**、未受信、最新payload解析NG。同期確認済みなら片道p95 **≥100ms**／最大 **≥500ms**も対象 |

注意／不良なら送信先・許可・発熱・fpsを確認。P2Pなら同じWi-Fi＋P2P優先OFFのLANと、同じ点灯・動作で各20〜30秒比べる。p95と最大の詰まりを見る。終わったら計測OFFで通常送信へ戻す。

計測の読み方:

- 最大2048サンプル。中央値は偶数なら中央2個の平均、p95は昇順 `ceil(n×0.95)` 番目。間隔はPCのmonotonic時計で測り、不正payloadも含む。完成した間隔は後のpacket時刻で窓に入り、最大表示は完成分のみ。判定は現在の無受信時間も使う。
- 片道は有効ts＋解析OKのみ。tsなし／破損時は間隔だけ。片道サンプルがなければ同期チェックONでも「間隔のみ」。モード切替後は5秒待つ。再起動／送信元IP変更で窓をリセットする。
- tsは両アプリとも検出後・文字列生成時。送信キュー・bridge・PC受信待ちは含むが、撮影・認識・Unity描画は含まない。**画面全体の遅延ではない**。同期後も時計誤差より小さい遅延は判断できない。
- 未検出や処理fps低下でも間隔が開く。スマホの解析／送信fpsと併せて読む。「間隔のみ」の良好では一定の大きな片道遅延を検出できない。

### 台ごとの位置補正（F7）

初期OFF、未設定／OFFなら従来の座標。**当日使うEditorまたはビルドで、A/Bそれぞれ測定**する。認識・送信形式は変わらない。

1. タイトル／メニューでF8を開く。台・箱の位置・立ち位置・反転を確定する。
2. **F7**／「4隅の測定を開始」。既定はRED。青で測るなら開始前に「BLUEの剣を採取」をON。両色に同じ4隅を使う。
3. 点灯した剣の**中点**を、実際に振る範囲の **左上→右上→右下→左下** へ。方向はプレイヤーから見た画面に合わせる。各隅で静止してF7／「この隅を採取」を押し、約1秒待つ。表示が進んでから移動する。
4. 約1秒・有効受信10件以上の中点中央値で採取。受信不足／動きはその隅を再採取。交差・凹形・小さすぎる4隅は保存せず、最初からやり直す。
5. 成功後は保存してON。剣を振り画面全体に届くか確認。4隅を1920×1080へ射影補正し、その後の感度・画面写像は従来どおり。領域外は入力矩形の外側10%まで許容し、極端な値を止める。

保存先はこのPCのPlayerPrefs `PhoneSaber.PositionCalibration.<台名>.Corners.v1`／`.Enabled`（台なしは専用の空ラベルキー）。Editorとビルドは保存先が別。次回も同じ台の設定を読む。
F8の「位置補正をON」で一時OFF、「この台の位置補正をリセット」でその台の保存だけを消す。キャンセル・F8を閉じる・受信機変更・測定失敗では以前の設定を維持する。
**スマホ／箱・カメラ／画角・反転・立ち位置を変えたら再測定**。[実機なしの練習](../../tools/README_saber_motion_replay.md#位置補正の練習実機なし)も参照。

<a id="4-正常な状態"></a>

## 4. 運用中に見るもの

### スマホの状態と自動復帰

- 発熱はOSの状態（正常／やや高い／高い／危険）で、摂氏温度ではない。iPhoneはnominal／fair／serious／critical、AndroidはNONE／LIGHT・MODERATE／SEVERE／CRITICAL以上。電池は%と充電中／満充電／未充電、取得不能は「不明」。
- iPhoneのカメラ／処理fps、Androidの解析fpsは直近5秒の実測。処理中央値はiPhoneの画素アクセス込み検出時間、AndroidのJNI時間。送信fps・PC受信fps・無線遅延とは別。
- 開始3秒後から要求fpsの70%未満で注意（30fpsなら21、iPhoneの60fpsなら42未満）。iPhoneはカメラ／処理どちらも対象。高い／危険はfpsに関係なく注意。熱やfpsを理由に認識・カメラfpsを自動変更しない。
- 高い／危険、fps低下、電池減少は箱の通風・排熱・給電を確認。充電でも熱は増える。危険が続けば手動停止して冷却／交代。熱状態の変化はiPhone Console／Android Logcat（DeviceHealth）、iPhone診断録画には熱と電池も残る。
- 送信意思を保存し、中断後の前面復帰・アプリ再起動で再開する。自動開始OFFでも送信中だった場合は復帰。停止ボタンは意思を消し、前面復帰だけでは再開しない。ただし自動開始ONなら次のアプリ起動で開始する。診断／ガイド録画は自動再開しない。
- カメラ中断・エラー・映像停止は1→2→4→8→16→最大30秒で再試行。15分失敗で警告、10秒安定で回数リセット。iPhone「カメラを再開」、Android停止→開始でも再試行できる。
- Wi-Fi切替でも送信意思を保ち「PCを再接続中…」。同じ台を再探索・再解決（約30秒ごとにも更新）。LANの選択PC名を保存、手動IPは優先なのでIP変更時に更新する。iPhoneはP2P優先／LAN退避を維持し、ネット再接続には15分の打切りはない。別PCへ移すときは停止して台・IPを変える。
- 送信／復旧待ちの前面画面は自動ロックを防ぐ。OSによる終了後のアプリ起動、強制ロック解除、電源切れの復旧はスタッフが行う。

### PCの受信とログ

F8で台・両色・無受信時間・経路・判定を見る。イベントログは `Application.persistentDataPath/PhoneSaber/events.log`、現行と `.1`〜`.4` の計5世代、各1MiBまで。古い順に削除される。受信機開始／停止／再試行、色別の1秒超の途絶と復帰、送信元／経路変化、台名をUTC付きで記録。書込失敗はF8に出るが受信は続く。

Mac＋iPhoneの追加確認:

- 経路は `P2P Connected (awdl0 · Phone Saber Unity P2P (<Mac名>))`。同じWi-Fiでは `en0` の場合もある。RTTは直近40回の中央値／p95／最大／ping欠落率（0%が理想）。
- Unity Consoleは起動時 `listening on UDP`／`Bonjour registered`、接続時 `peer connected`／`RED received`／`BLUE received`（sessionごとに1回）、復帰時 `peer alive`。10秒ごとの `last 10s` のRED／BLUE・maxGapMs・peersも見る。30fpsなら間隔約33ms、10秒約300件、過去の実測は約32件/秒。bridgeは両色の受信機が生きている間だけ動く。
- **PhoneSaber Status.command**は読み取りのみ・数秒。受信側 `/health`、UDP使用者、bridge、Editor.logの直近Play以降の集計5回・接続・警告・relay、最新bundle／report／解析状態、Git branch／差分（fetchなし）、Codex CLIを確認する。Editor.logは行時刻がないため最終更新時刻を表示。通常は全項目OK、maxGapMsは同ツール独自に<150msでOK、150〜499でWARN、≥500でNG。終了コードはOK=0／WARN=1／NG=2。

### 診断を使う場合（Mac＋iPhone）

ゲーム座標と診断受信は別。録画停止後のbundleはP2P診断relay（`_phonesaber-dp2p._tcp`）経由の `127.0.0.1:8765`、またはLAN（`_phonesaber-diag._tcp`）で届く。受信→inbox保存→無料 `.report.md` →Codex解析。受信側が停止してもゲーム座標は届く。

受信側はUnity Playで自動起動し、Unity終了後も解析用に残る。Start PhoneSaberで単独起動でき、重複起動しない。Statusが古いコードを警告したら、待機中にStart PhoneSaberを再実行（受信／解析中は止めない）。無効化は `PHONESABER_TRIAGE_RECEIVER=0`。
inboxは `~/Library/Application Support/PhoneSaber/diagnostics-inbox/`、ログは `~/Library/Logs/PhoneSaber/latest.log`。詳しくは[診断手順](../../ios/PhoneSaberSender/Tools/PHONE_SABER_TRIAGE.md)。Statusの直接実行は `PhoneSaber/` から `python3 ios/PhoneSaberSender/Tools/phone_saber_status.py`。

## 5. トラブル表

全組み合わせで、まずF8を見る。

| 症状 | 主な原因 | 対処 |
| --- | --- | --- |
| 入力なし／1秒超の無受信 | 未送信・切断・送信先違い | ロック解除してアプリを前面へ。自動復帰しなければ開始。台・IP・ネットワークを確認 |
| 受信機が停止 | ポート競合 | 同じPCのEditor／Player／古い受信ツールを止める。自動再試行後のONを確認。MacはStatusで使用者を確認 |
| 別台が動く／台が見つからない | 台不一致・古い手動IP | スマホとF8の台を合わせ、正しいlauncherでPCを起動し直す。手動IPを修正／解除 |
| 学校Wi-Fiだけ届かない | 端末間通信の隔離 | PCのホットスポット（Macはインターネット共有）へ。A/BのSSIDを区別し、新しいIPを入力 |
| Windowsだけ入力／探索なし | Public設定・firewall | [Windows README](../../windows/README.md)で共有接続もPrivate、UDP 5005〜5007許可。iPhoneは手動IP |
| Mac＋iPhoneのP2P未接続／ラグ／LANへ戻る | 権限・Wi-Fi OFF・bridge・詰まり | 両端の許可、Wi-Fi ON／個人用Hotspot OFF、Status、built .appのbridge指定を確認。直らなければ同じWi-Fi＋P2P優先OFF |
| カメラ復旧待ちが続く | 中断・カメラ使用不可 | 権限確認。iPhone「カメラを再開」、Android停止→開始。台とF8の両色の復帰を確認 |
| 熱い／fps低下／電池減少 | 箱内の熱・排熱・給電不足 | 箱を開け送風、排熱から離し給電確認。危険が続けば停止して冷却／予備端末 |
| 受信OKだが剣が逆／ずれる／飛ぶ | 反転・設置・ブレ・誤検出 | レンズ・固定・画角・照明を確認。反転を直したらF7再測定。剣なし／人ありでも試す。閾値は当日変えない |
| ゲームが消えた | 異常終了 | 監視起動なら5秒待つ。正常Quitは再起動しない。繰り返すなら監視停止・ログ保存・予備PC。Editorは手動でPlay |
| iPhone診断が届かない／解析なし | 受信側・短い録画・転送OFF | Mac StatusとStart PhoneSaberを確認。数秒以上録画してStop、無料reportを先に見る。[診断手順](../../ios/PhoneSaberSender/Tools/PHONE_SABER_TRIAGE.md)へ |

## 6. 終了手順

1. スマホで送信を停止する。次回の自動開始も不要なら「起動時に送信を自動開始」をOFF。
2. ゲームの **Quit** で正常終了。監視は終了し、再起動しない。EditorはPlayを止める（P2P bridgeも止まる）。
3. 監視だけ止めるならTerminal／batの **Ctrl+C**（Windowsはバッチ終了にY）、またはスクリプト横に空の `Start-Saber-A.STOP`／`Start-Saber-B.STOP`（両台は `STOP`）を作る。STOPは今のゲームを強制終了せず、終了後の再起動を止める。**次回前に削除**する。
4. 不具合があった台のlauncherログとF8のイベントログ全5世代を保存する。
5. Macの診断受信側はゲーム終了後も残る。受信・解析が終わったことを確認し、Start PhoneSaberのウィンドウでCtrl+C。箱内の熱と給電を確認して片付ける。
