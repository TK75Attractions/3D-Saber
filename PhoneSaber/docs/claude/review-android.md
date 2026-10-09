# Android sender レビュー（2026-10-10）

対象: `PhoneSaber/android/app`（Kotlin、JNI glue `phonesaber_jni.cpp` / `rgba_rotation.hpp`、テスト）。
C++ 認識 core（`android/core`）、認識結果、UDP payload・ポート（5005/5006、探索 5007）は変更していない。
想定端末: AQUOS sense9（Android 16）。実機での確認はしていない（すべてコード読解とJVMテストに基づく）。

過去に探索・起動順の整理（`opt/android-startup` / `opt/android-energy`）と CameraSession→LatestUdpSender
のスレッド構成の変更が「実機確認なしでは危険」として不採用になっている。今回はそれらに触れず、
小さく正しさが明らかな修正だけを入れた。

## 修正したもの

| # | 重大度 | 場所 | 内容 |
| --- | --- | --- | --- |
| F1 | 中 | `PcDiscovery.kt` `onLinkPropertiesChanged`（修正前 87–89行） | **送信中に PC 候補が周期的に消える**。LinkProperties の変更通知は IPv6 の RA 更新（アドレス寿命）、一時アドレスの更新、DHCP lease 時刻の更新でも届く。修正前は毎回 `restartDiscovery(preserveSelection=false)` で `found` と選択を消し、`publish()` → `sender.configure(null)` で送信先が null になり、再探索で見つかるまで座標が送られなかった（UDP探索なら数十ms、Bonjourだけなら1秒以上）。IPv6 のある会場Wi-Fiでは数分ごとに起こりうる。表示も一瞬「PC を再接続中…」。**修正**: UDP broadcast 先（255.255.255.255＋IPv4サブネットの directed broadcast）が変わらない通知では再探索しない。サブネットが変わったときは従来どおり再探索する。30秒ごとの選択保持つき再探索はそのまま残るので、探索socketの故障からの復帰は変わらない。broadcast 計算を純粋関数 `DiscoveryBroadcasts` に切り出した（計算式は同一）。 |
| F2 | 中 | `PcDiscovery.kt` `publish()`（修正前 316–325行） | **同じPCなのに「前回のPC」と一致せず送信先を失う**。Unity の UDP 応答の name は PC 名（`Environment.MachineName`）、Mac の Bonjour サービス名は全Mac共通の `Phone Saber Unity`。前回のPCを名前だけで照合していたため、UDP名で固定した後に UDP 応答だけ止まる（5007 のポート競合など）と、同じIPのBonjour候補が受理されず、逆も同じ。**修正**: 固定時に IPv4 も保存し、名前か IPv4 のどちらかが一致すれば同じPCとして受理する（`PreferredPcMatcher`）。古い版で保存された名前だけの固定は従来どおり名前で照合する。台指定中は従来どおり固定を使わない。 |
| F3 | 低〜中（UX） | 同上 | **前回のPCの固定で他のPCが無視されていても、表示は「PCを探索中」だけ**だった。前回のPCは `停止` 操作でしか解除されず、停止せずにアプリを閉じた翌日にPCを替えると（Mac/Windows構成は未確定）、見つかっているPCを選ばない理由が分からない。**修正（表示のみ）**: その状態では「前回のPC「名前」を探索中（別のPCは自動では選びません。切り替えは「停止」で解除（停止中なら 開始→停止）、または手入力）」と表示する。選択の仕組みは変えていない。 |
| F4 | 低（潜在） | `CameraSession.kt` resolution filter（修正前 94–105行） | `StreamConfigurationMap.getOutputMinFrameDuration(YUV_420_888, size)` は、表に無いサイズでは `IllegalArgumentException` を投げる。同じ filter を Preview（PRIVATE 形式の候補）にも使っているため、PRIVATE にだけあるサイズを持つ端末では bind 全体が失敗し、復旧の再試行が15分続く。sense9 では 10-07 に30fpsで動いたので、現状は起きていないと考えられる（予備端末向けの堅牢化）。**修正**: 例外のサイズは従来の「不明（受理）」扱い。選択規則は `CameraFormatPolicy` に切り出し、規則自体は変えていない。 |
| F5 | 低〜中（当日対応） | `CameraSession.kt` 158行 | CameraX のカメラエラーが「カメラエラー (4)」のような番号だけだった。**修正**: `CameraErrorText` で対処を表示（別アプリ使用中／クイック設定のカメラアクセス OFF／おやすみモード／重大なエラー）。特に **ERROR_STREAM_CONFIG を 60fps で受けた場合は「停止して『カメラ 60fps』を OFF」と表示**する（60fps は sense9 実機未確認。README の方針どおり fps の自動切替はしない）。 |
| F6 | 低〜中 | `MainActivity.kt` `onCreate`/`onSaveInstanceState` | **Activity 再生成で閾値が既定値に戻り、そのまま自動再開する**。`configChanges` 未指定なので、ダークモードの時刻切替・フォントサイズ変更・プロセス復元などで Activity が作り直される。送信の意思は保存されているため自動再開するが、明るさ・色の優位差のスライダー（ID が無く自動復元されない）は 145/25 に戻っていた。運営が停止中に変えた閾値が黙って変わる。**修正**: `onSaveInstanceState` で保持（`ThresholdSettings`、0–255 に制限）。永続保存はしない（iPhone と同じ）ので、通常の起動では従来どおり既定値。 |

テスト追加（JVM、合計 35 → 47 件）:
- `DiscoveryPolicyTest`: broadcast 計算（/24・/8・/30、/31・/32・IPv6 の除外）、IPv6 や同一サブネット内の変更で broadcast 先が不変・サブネット変更で変化、前回PCの名前／IPv4 照合・旧形式・台指定、探索表示文。
- `CameraPolicyTest`: 60fps 選択条件、VGA 以下の最大／なければ最小、60fps で VGA が 1/30 秒しか出せない場合、表に無いサイズの受理、エラー表示文。
- `ThresholdSettingsTest`: 既定値 145/25、保存→復元、範囲外の制限。

## 修正していない所見（記録のみ）

| # | 重大度 | 場所 | 内容・失敗シナリオ | 推奨 |
| --- | --- | --- | --- | --- |
| R1 | 中（運用） | `CameraSession.kt` 85–93行 | 60fps は AE [60,60] と VGA 以下 1/60 秒の YUV サイズを確認するが、Preview＋ImageAnalysis の組合せで本当に構成できるかは bind 後に分かる。失敗するとエラー→再試行が同じ設定で繰り返される（最大15分）。露出も 1/60 秒以下に制限されるので、暗い会場では画像が暗くなる。 | sense9 実機で 60fps の開始・解析fps・暗所の検出を確認。だめなら「カメラ 60fps」OFF（F5 の表示が誘導する）。自動切替は README 方針により入れない。 |
| R2 | 低 | `CameraSession.kt` 156–159行 | `cameraState.observe` は LiveData の現在値を即座に配信する。前回の bind が `CLOSED`＋エラー（fatal／disabled）で終わっていると、再試行の bind 直後に古いエラーを受けてすぐ失敗扱いになり、バックオフが1段余計に進む（その次の再試行で回復する見込み）。 | observe 時点の値を無視する修正は可能だが、同一エラーが重複排除されると10秒の watchdog 頼みになるため見送り。 |
| R3 | 低 | `CameraSession.kt` 202行 `stop()` | `provider.unbindAll()` はプロセス共通の ProcessCameraProvider の全 use case を外す。古い Activity の `onStop/onDestroy` が新しい Activity の bind 後に来る順序（同時に2インスタンス）だと、新しい方のカメラが止まり、10秒後の watchdog で復旧する。通常の画面回転・再生成は順番どおりなので起きない。 | 自分の Preview/ImageAnalysis だけを `unbind` する。 |
| R4 | 低 | `MainActivity.kt` 90–94行 | カメラ権限を拒否しても送信の意思・「停止」ボタン・`FLAG_KEEP_SCREEN_ON` が残り、待機中も画面が消えない。前面に戻るたびに再要求する（永久拒否なら毎回トースト）。 | 前面での拒否時に `stopSending()` する案。ただし背景遷移で拒否扱いになるケースを除外する必要がある。当日は事前に許可済みなので優先度低。 |
| R5 | 低 | `LatestUdpSender.kt` 52–85行 | worker は `DatagramChannel.open()`／`Network.bindSocket()`／送信の間 `gate` を保持し、その間カメラ thread の `offer` と main の `configure` が待つ。通常は数ms以下で ANR にはならない。 | 以前のスレッド構成変更は不採用。現状維持。 |
| R6 | 低 | `PcDiscovery.kt` 86行 | Wi-Fi network が同時に2つある端末（STA+STA 等）では、後から `onAvailable` した方へ切り替わる。 | 実機で問題が出た場合のみ対応。 |
| R7 | 低 | `MainActivity.kt` 266–270行 | 「遅延計測モード」は保存しない設計のため、Activity 再生成で OFF に戻る（ts= が付かなくなるだけで送信は継続）。 | 計測中は画面設定を変えない。 |
| R8 | 情報 | `PcDiscovery.kt` / iPhone | 前回のPCの固定は、送信していない閲覧だけでも保存され、`停止` 以外では解除されない（iPhone の `PhoneSaber.bonjourService` も同様に保持する設計）。F3 で理由は表示するようにした。 | 当日は台A/Bを指定する（台指定中は固定を使わない）か手入力。 |

## 問題なしと確認した点

- **JNI の範囲検査**: `processRotated` は position/remaining/capacity、stride≥0、閾値0–255 を検査し、`RgbaRotation::orient` が幅・高さ（1–32768）、stride≥幅×4、最終行は width×4 バイトだけ読む条件 `(height−1) ≤ (size−width×4)/stride`、角度 0/90/180/270、32bit 上限を検査する。0度でも最終行の padding が無い plane は詰め直す。handle 0（close 後）も拒否。変更なし。
- **WifiLock（低遅延）**: 参照カウントなし。`startSending` で未保持なら取得、`stopSending`（`onStop` からも呼ばれる）で保持中なら解放。対になっている。MulticastLock も探索世代ごとに取得・`endDiscovery` で解放。
- **ライフサイクル**: `onStop` でカメラ・送信先・探索・thermal listener・定期更新を止め、`onStart` で送信の意思（同期保存）に従って再開。プロセス死後も `wasSending` で自動再開。in-flight の検出結果は世代番号で破棄。
- **鮮度 180ms**: 処理開始時刻からの経過で送信直前に判定し、古い場合は残りの色も送らない。期限処理は送信しない。
- **ANR**: main 上の処理は CameraX の bind、NSD/ConnectivityManager の binder 呼び出し、小さい SharedPreferences の commit 程度。DNS は使わない（手入力は数値IPv4のみ、broadcast も数値）。
- **端末状態表示**: 発熱の段階分け、電池の sticky broadcast（システムの保護 broadcast なので Android 14 の exported 指定は不要）、直近5秒の中央値。

## 検証

- `./gradlew :app:testDebugUnitTest :app:assembleDebug`（Android Studio JBR、`local.properties` は本体checkoutからコピー）: PASS、JVM 47 件（失敗0）。
- JNI / C++ glue は変更していないため、`make -C PhoneSaber/android/core test parity` とエミュレータの `connectedDebugAndroidTest` は対象外として未実行。
- `git diff --check`: PASS。
- 認識・UDP payload・ポート・鮮度判定・送信スレッドに差分なし（差分は探索の再起動条件・前回PC照合・表示文・解像度候補の例外処理・閾値の再生成時保持のみ）。

## 実機で確認してほしいこと

1. IPv6 のある Wi-Fi で長時間（10分以上）送信し、「PC を再接続中…」の一瞬表示や送信fpsの落ち込みが出ないこと（F1）。
2. Mac で UDP 探索と Bonjour の両方が見える状態から Unity を再起動して、同じPCへ戻ること（F2）。
3. 60fps ON で開始できるか、解析fps、暗い場所での検出（R1）。失敗時の表示が F5 の文になること。
4. 閾値を変えて開始→ダークモード切替などで画面が作り直されても、閾値が保たれて自動再開すること（F6）。
