# PhoneSaber STATUS

## ユーザー待ち

- 最新の main(ガイド付き録画 v3)を入れて、露出 1/100 秒で1本。v3 は最初の saberなし区間でも画像を1枚保存するので、赤の誤検出の正体を画像で確認できる。

## 現在の仮説と確度

- 高: 肌・照明・背景の誤検出と、速い振りのブレによる eligibility 消失。代表的な旧 CASE A は背景同士で、修正 A の gate は未達。
- 高: AWDL の詰まりは認識とは別の遅延原因。interactiveVoice の改善効果は実機未確認。
- 確認済み: score の決定性は `bbc1f32` で修正。赤 warmNoDeepRed は `8363024` で採用、公式 verify 全 PASS。実機再試験は未了。
- 保留: 青 deep support の採否。W は淡いピンク剣を失うため不採用。fixture 89 は窓期待値のまま。根拠・数値・却下案は [FINDINGS.md](FINDINGS.md)。

## 次にやること

Android 作業: Phase 2 の Kotlin/CameraX/JNI/UDP探索/NSD/UDP・日本語UI・JVMテストをこのworktreeに追加（ユーザー指定で未commit）。SDK API 37/build-tools 36.0.0は既存、NDK 30.0.16248370/CMake 3.22.1の導入とAndroid Studioでの初回ビルド・JVMテスト・sense9/Windows/Mac実機確認が次。sandboxでは依存取得・ビルド・テスト実行は行わず静的確認のみ。Swift/coreアルゴリズム/UDP仕様を維持し、Androidに診断/P2Pは追加しない。手順は `android/README.md`。

優先順。旧改善候補もここへ統合した。実装済みの機能は再実装せず、未確認の動作を検証する。

1. **warmNoDeepRed の実機再試験** — 理由: offline の改善が実環境でも有効か確認する。必要: 実機録画、未使用 ORIGINAL の目視、saberなし FP と小さい/淡い剣の保持を比較。担当: user（撮影）/ Claude（解析）。
2. **ブレ・露出と候補消失の比較** — 理由: CASE B と速振りの取りこぼしが残る。必要: 実機で同じ振りを自動/1/100 秒で比較、距離 0.5–3m・端からの向き・昼夜を含む lossless。B が多ければ生成前の棄却 component を記録するコード、既定露出変更時はユーザー判断。担当: user（撮影・判断）/ Claude（解析・コード）。
3. **青 deep support の採否** — 理由: FP 29→18 の効果はあるが、落とす淡い端片の点灯真値が未確定。必要: 実機で点灯真値、遠い/淡い/ぶれた青・画面端・青光の肌や布を確認。診断コードと回帰、証拠後の採否判断。本番未適用。担当: Claude（診断・評価）/ user（撮影・判断）。
4. **残存背景・肌・布への対策** — 理由: 赤 59・青 37 の誤出力が残る。必要: 別人・別場所の点灯/OFF 対、発光 halo・点 LED 列の比較、診断コード。静的マスクは固定背景のみの案として、静止剣を消さない証拠とユーザー判断が必要。担当: Claude（解析・コード）/ user（撮影・判断）。
5. **fixture 89 の窓期待値を解消** — 理由: 公式正例が窓で、期待値だけ直すと 39/40。必要: 窓と淡い剣が共存する実機証拠、画像外 1px の端点審査、検出器コードと回帰。ユーザーの修正方針は決定済み。W は再採用しない。担当: Claude（修正・検証）/ user（撮影）。
6. **CASE A/B/C に基づく選択・端点修正** — 理由: 背景対策後も本物の不安定さが残る可能性。必要: bundle コピーで triage dry-run と selection replay、ORIGINAL と全 eligible を照合。A は別 swing で 2 event 以上の gate 後にコード、B は生成/eligibility、C は A と別変更で長い剣・分離 LED を保護。担当: Claude（判定・コード）/ user（追加撮影・再試験）。
7. **P2P/LAN の遅延・復帰検証** — 理由: AWDL に 170–300ms、ときに約 1 秒以上の空白。必要: 会場 Wi-Fi の実機比較、RTT/p95/max・到着間隔・end-to-end 計測、ネット使用中・切断・前面復帰・2 台 Mac・LAN 退避の確認。許容値のユーザー判断、不具合があればコード。担当: user（実機・目標）/ Claude（計測・修正）。
8. **Mac の計測画面・受信ツール検証** — 理由: 自動テストだけでは実ブラウザと実 UDP を確認できない。必要: 実機で開始/停止/再開・CSV 保存・結果フォルダ・失敗統計を操作、表示から受信までの同一時計計測を確認。Unity との port 競合も確認し、必要なコード修正。担当: Claude（検証・コード）/ user（カメラ操作）。
9. **Mac 診断受信・旧 bundle の整理** — 理由: 古い process・precheck・解析欠落を運用で見落とさない。必要: `/health`・安全な再起動・転送失敗後の再送・report/overview を検証。234740/155919 の手動再解析候補を確認、旧 010049 の 32KB 超過は上限を維持。必要なコード修正。担当: Claude。
10. **Unity の入力契約・短い途切れの扱い** — 理由: 正しい最新座標をゲームへ反映し、古い入力を残さない。必要: 3D-Saber の compile/EditMode/PlayMode と実機で、1920×1080 の向き・端点・最新値・未検出時を確認。≤200ms の補間/外挿は誤出力を延ばす危険も比較し、ユーザー判断後にコード。認識不安定の原因調査は iPhone で行う。担当: Claude（検証・コード）/ user（体感・判断）。
11. **30 分以上の連続運転** — 理由: 発熱・fps・電池・保存量の時間変化は未確認。必要: thermalState/fps/電池/処理時間/到着間隔/メモリの診断コードと実機試験。256MiB 上限の memoryHeadroom、低電池・復帰も確認。担当: Claude（診断・解析）/ user（長時間運転）。
12. **会場リハーサルと起動・復旧手順** — 理由: 自動起動だけでは当日の復旧まで保証しない。必要: 当日 Mac と iPhone で [runbook](EVENT_DAY_RUNBOOK.md) を通し、照明・固定配置・署名/許可・通信・予備経路・終了を確認。[2 台目 Mac](SECOND_MAC_SETUP.md) の setup も検証。receiver/bridge の監視・安全な再起動をコード化するか判断。担当: user（会場試験・判断）/ Claude（手順・コード）。
13. **Codex 解析の費用・ログ保持** — 理由: needs_capture に長時間・高 effort を費やし、inbox/実ログも増える。必要: high 既定・必要時のみ max の費用/精度比較とコード、保存期間・削除対象のユーザー判断。テストログ隔離と失敗記録を保つ。担当: Claude（比較・コード）/ user（費用・保持方針）。
14. **有線 USB 経路の試作判断** — 理由: AWDL が会場で不安定な場合の選択肢。必要: ケーブル運用のユーザー判断、usbmuxd/TCP のコード、切断復帰と end-to-end 遅延の実機比較。既存 UDP 座標互換を保ち、TCP 滞留も測る。担当: user（判断・接続）/ Claude（設計・コード）。
15. **Mac カメラ直接認識の将来比較** — 理由: ユーザーが将来の構成候補として保持。必要: Mac 内蔵/USB カメラの配置・精度・端点・遅延の実機比較、採用判断と必要なコード。Continuity Camera は別の無線比較経路で、ローカル縮小/FPS を通信遅延改善と扱わない。担当: user（構成判断・撮影）/ Claude（比較・コード）。

## 作業ログ

- 2026-10-06 / Android・Windows: C++ core(`b0efcc2`、Mac で Swift と 248 枚 bit 一致)、Android アプリ(`a61dd1a`、build と JVM テスト 16 件 PASS、emulator で起動・画面表示を確認)、Unity の Android 用 PC 探索応答 UDP 5007(3D-Saber `170fd79`)。Android の libm では選択結果 237/237 一致・内部値は 1 ulp 差(android/core/EXACTNESS.md)。実機(AQUOS sense9)と Windows での受信は未確認。

- 2026-10-06 / Android Phase 2: 本番senderのGradleプロジェクト、薄いJNI、portrait画素回転、180ms expiry、最新フレームのみのUDP、UDP/Bonjour探索、手入力IP保存、日本語UIとJVMテストを追加。未commit。Swift/core変更なし。Androidビルド・テスト実行・NDK bit parity・実機検証は未実施。

- 2026-10-06 / Android Phase 1: `android/core/` に C++17 の本番認識・状態遷移・UDP文字列、PNG CLI、Swift reference 比較、Make/CMake を追加。Swift 本番・threshold・UDP形式・fixture期待値は無変更。Python 3.12 / clang++ -O2 -Wall -Wextra -Werror: formal 40/40（35 PNG）、inbox original 213 PNG（annotated 5除外）、248画像×3入力経路の全候補/production double bit一致、状態遷移541操作・合成18ケースとも mismatch 0。core test、比較器 unittest 4件、ASan/UBSan PNG smoke、git diff --check と新規ファイル空白チェック PASS。Android libm/NDK の bit parity と実機検証は Phase 2。日本語計画は `android/README.md`、exactness リスクは `android/core/EXACTNESS.md`。

- 2026-10-06 / 実機 1/100 秒(005910_095, 010346_502): 露出上限が実際にかかった。赤の横切り 72%→99.6%、先端向け 96.6%→100%。saberなしの赤の誤検出は 1本目 3.5%、2本目 100%(正体の画像なし)→ ガイド v3 で saberなし区間の画像も保存、Mac の上限を 5 枚に。

- 2026-10-06 / 実機 v2(004003_239): 最初の saberなし区間の赤の誤検出 598/598→**0/598**(warmNoDeepRed が実機で効いた)。赤の静止・振りは 97–100%、横切りは 72%(画面外を含む)。最後の saberなし 149/302、青だけの区間の赤 74%/63% は棚の上のオレンジ色の物(RGB 約 189,107,102、濃い赤の画素あり)。Mac の入力チェックが新しい診断を弾いていた不具合と、露出 1/100 秒がかからない不具合を修正(`8f9a347`)。tracking event(先端を向けた静止で数 frame 見失った所)は tracking 履歴不足で Codex 解析が precheck 止まり。

- 2026-10-05 / 整理: `af20a7b` shadow R7e/PF22 記録と一度きりの調査ツールを削除（旧 bundle は引き続き読める）、`376ddcc` FINDINGS に調査を統合・残作業を一本化（公式 verify 全 PASS）。
- 2026-10-05 / 残存 FP・W: `cc47927`、`5888bb5`、`5fee717`。全件分類、青 D は保留、淡い剣を失う W は不採用（研究テスト 27 PASS、D formal 40/40）。
- 2026-10-05 / 赤の認識: `43f6b61`、`06a18c5`、`8363024`。hue/深赤単独は見送り、warmNoDeepRed 採用（公式 verify 全 PASS、formal 40/40、198 枚 mismatch 0、要実機再試験）。
- 2026-10-05 / 診断転送・起動: `9661ec2`、`fb6dc17`、3D-Saber `9613d60`。ガイドの swing 4 枚を受理、Unity Play で受信側起動（会場確認未了）。
- 2026-10-04 / 撮影・解析: `904faba`、`7823070`、`ded9bba`、`2a82db8`、`60f1933`。2 本は振りの証拠不足、ガイド v2、肌・照明 FP を確認（CASE A gate 未達、実機再試験待ち）。
- 2026-10-04 / 旧 shadow 比較: `f70dcde`、`431555c`。R7e/PF22 を当時集計したが昇格せず（formal 40/40、露出感度・実機証拠不足）。
- 2026-10-04 / Mac 運用: `6ac28ca`、`a2faab5`、`d5e547c`、`96a144f`。古い受信コード・ログ隔離、一覧、2 台目 setup、負荷依存テスト対策（6ac28ca: Tools 366 件 OK・1 skip、実機運用未確認）。
- 2026-10-03 / P2P 改善: `0aeeb8f`、`1c751df`、`36d9d93`、`63eca9d`、`5723323`、`4def143`。通信優先度・復帰・Mac 固定、露出実験、runbook、検証用 Simulator 分離（verify PASS、効果は実機待ち）。
- 2026-10-03 / 解析・診断: `59383b3`、`f88fa50`、`a18816a`、`ad25839`。不採用候補 12 件、test/短録画の転送停止、解析 25 分＋high 再試行（144936 は CASE B、155919 は背景）。
- 2026-10-03 / P2P 導入: `09ffdcd`、`152397c`、`06362f7`。LAN fallback・診断 relay・RTT（導入時 XCTest 177、Python 277、formal 40/40、Release PASS）。
- 2026-10-03 / 決定性・解析基盤: `bbc1f32`、`1ad12f9`、`31ad94c`、`7449bc7`、`67597e4`、`312f63c`、`d55294c`、`4a38f53`。score 固定・bit parity・emitter/露出・区間・解析耐性・hotspot（formal 40/40、当時 verify PASS）。
- 2026-10-02 / 診断・背景再解析: `6bf2cf3`、`a93658e`、`40986a2`、`ecbebc8`、`bdd684e`、`cf3a873`、`ebb398a`、`23b667f`、`b2d74dc`。窓・CASE・メモリ・report/benchmark（XCTest 150→155、Python 182→220、formal 40/40、背景 FP 7/8）。
