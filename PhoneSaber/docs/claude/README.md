# PhoneSaber docs/claude の索引

AI と開発者向け。**いまの状態を知るには [STATUS.md](STATUS.md) だけ読めば足りる。** ほかは必要になったときに開く。
日付入りの文書はその時点の記録で、あとから書き換えない（現状は STATUS に書く）。

## まず読む

| 文書 | 内容 |
|---|---|
| [STATUS.md](STATUS.md) | ユーザー待ち・現在の仮説・次にやること・作業ログ（新しい順） |
| [../../CLAUDE.md](../../CLAUDE.md) / [../../AGENTS.md](../../AGENTS.md) | 運用ルール（認識の変更条件、verify、git） |
| [EVENT_DAY_RUNBOOK.md](EVENT_DAY_RUNBOOK.md) | 当日の技術担当向け手順 |
| [../STAFF_MANUAL.md](../STAFF_MANUAL.md) | 学生スタッフ向け（PDF は同じ場所） |

## 実行ファイルと道具の場所

| もの | 場所 |
|---|---|
| ゲーム本体（Mac、ビルド済み） | `Builds/Mac/3D-Saber.app`（git 管理外。`PhoneSaber/tools/unity_copy.sh build-mac` で作り直す） |
| ゲームの起動（監視付き） | `PhoneSaber/mac/Start-Saber-A.command` / `-B.command`、Windows は `PhoneSaber/windows/Start-Saber-A.bat` / `-B.bat` |
| iPhone アプリ | `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj` を Xcode で開き、実機を選んで Run |
| Android アプリ | `PhoneSaber/android/`（`./gradlew :app:assembleDebug` → `app/build/outputs/apk/debug/app-debug.apk`） |
| 録画の受け取り（Mac） | Desktop の `Start PhoneSaber.command`。届いた録画は `~/Library/Application Support/PhoneSaber/diagnostics-inbox/` |
| Unity のイベントログ | `~/Library/Application Support/DefaultCompany/ServTechSlash/PhoneSaber/events.log` |
| 公式検証 | `bash PhoneSaber/tools/verify_phone_saber.sh`（Git root から） |
| スタッフ用 PDF | `python3 PhoneSaber/tools/build_staff_manual_pdf.py`（`docs/STAFF_MANUAL.md` → `.pdf`、2ページ） |
| Android のテストとビルド | `cd PhoneSaber/android && ./gradlew --stop; ./gradlew :app:testDebugUnitTest :app:assembleDebug`（`JAVA_HOME` は Android Studio の jbr。Codex の後は先に `--stop`） |
| Unity のテスト・ビルド | `PhoneSaber/tools/unity_copy.sh editmode` / `build-mac`（複製 `~/Library/Caches/PhoneSaber/unity-copy` で実行）、PlayMode 全件は `unity_playmode_batches.py` |
| C++ / Swift の一致確認 | `make -C PhoneSaber/android/core test parity` |

## 当日運用

| 文書 | 内容 |
|---|---|
| [premortem-20261011.md](premortem-20261011.md) | 当日に起きうる故障 上位15、文書とコードの食い違い、実機で確かめる5項目 |
| [ops-hardening-20261011.md](ops-hardening-20261011.md) | 固まり検知、Windows 二重起動防止、bridge 再起動、status ツール |
| [ops-doc-audit-20261011.md](ops-doc-audit-20261011.md) | 手順書と実物の照合記録 |
| [protocol-contracts-20261011.md](protocol-contracts-20261011.md) | iPhone / Android / Unity / bridge 間の取り決め（port、座標、探索応答、待ち時間）の一覧 |
| [SECOND_MAC_SETUP.md](SECOND_MAC_SETUP.md) | 2台目の Mac の準備 |
| [unity-player-settings.md](unity-player-settings.md) | Player 設定の監査（macOS / Windows） |

## レビュー記録（見つけた不具合と、直したかどうか）

[review-ios-sender.md](review-ios-sender.md)（10-10）・[review-ios-event-day.md](review-ios-event-day.md)（8時間運用）・[review-android.md](review-android.md)・[review-unity-phonesaber.md](review-unity-phonesaber.md)・[review-mac-launchers.md](review-mac-launchers.md)・[review-windows-kit.md](review-windows-kit.md)・[review-python-tools.md](review-python-tools.md)・[review-debug-bundle-20261011.md](review-debug-bundle-20261011.md)（録画→Mac 転送）・[review-merged-20261011.md](review-merged-20261011.md)（10-10〜11 に入れた変更の再レビュー。未解決の指摘あり）

## 認識（判定）

| 文書 | 内容 |
|---|---|
| [FINDINGS.md](FINDINGS.md) | 調査結果のまとめ（根拠、残っている誤検出、却下した案） |
| [recognition-benchmark.md](recognition-benchmark.md) | 現行認識の PNG benchmark |
| [coreline-overlink-and-mural.md](coreline-overlink-and-mural.md) | core-line の過連結と壁画の誤検出（offline 分析、guard G） |
| [coreline-guard-prototype.md](coreline-guard-prototype.md) | guard G の採用ノート（証拠、切り方、残る確認） |
| [recognition-census-20261011.md](recognition-census-20261011.md) | 認識の失敗集計（見落とし・順位負け・誤検出・露出）と次の改善候補 |
| [red-fp-features.md](red-fp-features.md) | 赤・青の誤検出と winner の特徴量 |
| [debug-retention-20261011.md](debug-retention-20261011.md) | 手動録画に振りの画像が残らなかった原因 |
| [fixb-offline.md](fixb-offline.md) / [jitter-analysis.md](jitter-analysis.md) / [jitter-filter.md](jitter-filter.md) / [predict-real.md](predict-real.md) / [unity-predict.md](unity-predict.md) | 端点の修正案、揺れ、予測の検討 |
| [data/](data/) | 上の分析で使ったラベルと集計の CSV / JSON |

## 性能・遅延（2026-10-08〜11 の計測と高速化の記録）

- iPhone: [perf_ios_detection.md](perf_ios_detection.md)・[ios-detect2.md](ios-detect2.md)・[ios-detect3.md](ios-detect3.md)・[ios-pipeline.md](ios-pipeline.md)・[ios-frame-alloc.md](ios-frame-alloc.md)・[ios-sender-syscalls.md](ios-sender-syscalls.md)・[ios-ui-load.md](ios-ui-load.md)・[soak.md](soak.md)（連続運転）
- Android / C++ core: [perf_core_detection.md](perf_core_detection.md)・[core-r2.md](core-r2.md)・[core-r3.md](core-r3.md)・[core-r4.md](core-r4.md)・[android-perf.md](android-perf.md)
- Unity: [unity-frame-cost.md](unity-frame-cost.md)（受信→描画を含む）・[unity-gc.md](unity-gc.md)・[unity-ui-gc.md](unity-ui-gc.md)
- [integration.md](integration.md): 最適化7ブランチの統合検証（10-09）

## この Mac だけにあるもの（git 管理外）

| もの | 場所 |
|---|---|
| 作業用 worktree | `~/.phonesaber-worktrees/3D-Saber-<名前>/`（`~/縁日/GitHub/` には置かない） |
| Codex の無人実行 | `~/Library/Logs/PhoneSaber/codex-runner/`（`runner.log`、`prompts/`、`out/`、止めるには `STOP` を作る） |
| Unity プロジェクトの複製 | `~/Library/Caches/PhoneSaber/unity-copy/` |
