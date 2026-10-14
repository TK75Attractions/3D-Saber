# Player監査・soak追補（2026-10-14、未commit）

- 対象: main...HEAD（54a869a/d24648b）、前回2メモを照合。新機能なし、commit/pushなし。
- 修正・高: unity_copy.sh:77/125。重複完了行に成功が混じるとsmoke/shotsが誤合格。完了行を1件に限定。
- 修正・中: unity_soak_summary.py:28。完了後の周回行、11周未満のstable、11周以上のinsufficientを誤認証。順序・標本数を照合。
- 修正・中: unity_soak_stop.py:47。TERM無視でも停止成功を返していた。5秒待機、対象再照合、KILL、停止確認。PID再利用・他runは対象外。
- 修正・中: unity_copy.sh:122。Result/調整/戻りの4画像欠落でもshots成功。全7画像を必須化。
- 修正・中: check_pc_latency.py:40、test_predict_latency_eval.py:12。soak API / Debug.Logのhost代替不足で既存C#検証がcompile失敗。
- 修正・低: test_unity_shots.pyの成功fixtureが旧完了形式のまま。新形式へ更新し、旧形式の拒否も確認。
- 補完: check_player_smoke.pyでsoak Editor testも実DLL compile。実soak coroutineのhost試験5件（周回完了、自然Result、3ストア復元、入力/刃/初期scene異常）。Unity nativeと送信は代替。
- 回帰: 重複完了、順序/標本矛盾、画像欠落、TERM無視、上記compile失敗を修正前に再現、修正後PASS。既存smokeもmainで6 PASS/8 FAIL、現行14 PASS。
- 通常起動/Editor/認識/採点/UDP本文/corpusは不変。明示smokeの待機5秒・拡張遷移は前回player-vs-editorメモどおり。soak既定Game40秒。
- Unity新規C#4件のmetaあり。tools内Host.csはUnity import対象外。Swift/Kotlin追加なし。scene/prefab/meta/ProjectSettingsへの今回の編集なし。

- PASS: python3.12 -B -m unittest discover -s PhoneSaber -v — 85件、14 skip（socket禁止）。python3(3.13)ではcv2不在で4 import ERROR。
- 実行: 同discoverの -s PhoneSaber/tools — 247件、1 FAIL（実UDP Access denied）、7 skip、1 expected failure。変更対象のgate 34件は全PASS。
- 実行: 同discoverの -s PhoneSaber/ios/PhoneSaberSender/Tools — 431件、7 FAIL/50 ERROR/1 skip。socket/Network禁止、録画空き容量APIがavailable=0。Swift cacheは/tmpへ指定して再実行。
- PASS: python3 -B PhoneSaber/tools/check_pc_latency.py — Unity実DLL4構成、Editor検証、Windows PInvoke compile、NUnit34/34。
- PASS: python3 -B PhoneSaber/tools/check_player_smoke.py --before-ref main — runtime209件×4構成、Editor scripts/tests compile、smoke14/14、soak flow5/5。
- 実行: python3 -B PhoneSaber/tools/check_player_soak.py — 7 PASS/実UDP1 FAIL。--stub-udpは8/8 PASS（transport代替）。
- 実行: python3 -B PhoneSaber/tools/check_auto_latency.py — C#4 PASS/実UDP1 FAIL。別途同scriptの非network部分のみ実行、C#4/4・Swift Metrics3/3 PASS。loopback未検証。
- PASS: make -C PhoneSaber/android/core test parity PYTHON=python3 — core、Python4件、325画像/541遷移/27 synthetic差分0、formal40/40。
- PASS: bash PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh — static BGRA、DeviceHealth13/13。
- PASS: python3 -B PhoneSaber/ios/PhoneSaberSenderTests/run_latency_host_tests.py — host27/27、routing6/6。
- 実行: python3 -B PhoneSaber/ios/PhoneSaberSenderTests/run_send_gap_host_tests.py — 14 PASS/3 FAIL（Network禁止）。
- PASS: bash -n PhoneSaber/tools/unity_copy.sh、git diff --check、formal専用run_lossless_regression.py 40/40。
- 実行: unity_copy.sh build-mac / editmode 'PhoneSaberSoakTests|PhoneSaberBuildShaderTests'（copy=/private/tmp/phonesaber-finish-*）。open -g -j -n -WがkLSNoExecutableErr、成果物なし。
- 実行: xcodebuild -project PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -configuration Release -destination 'generic/platform=iOS' -derivedDataPath /private/tmp/phonesaber-finish-xcode CODE_SIGNING_ALLOWED=NO build — exit65、sandbox_apply拒否でSwift macro起動失敗。
- 未実行: 公式verify、実Player/実UDP、Windows build（support moduleなし）、Gradle（Android変更なし）、device、8時間。全ログ=/private/tmp/finish-*。

- operator残検証1: PhoneSaber/tools/verify_phone_saber.sh（公式XCTest/xcodebuild/既存Unity試験、operator担当）。
- operator残検証2: python3.12 -B -m unittest discover -s PhoneSaber/tools -v（check_auto_latencyの実loopbackも含む）。
- operator残検証3: PhoneSaber/tools/unity_copy.sh editmode 'PhoneSaberSoakTests|PhoneSaberBuildShaderTests'（公式Unity filterにはこの2クラスがない）。
- operator残検証4: PhoneSaber/tools/unity_copy.sh build-mac（隠しPlayer smokeを内包、別smoke不要）。
- operator残検証5: PhoneSaber/tools/unity_copy.sh soak 0.1（既定40秒の途中退出）→ PHONESABER_SOAK_GAME_SECONDS=600 PhoneSaber/tools/unity_copy.sh soak 480（自然Result/8時間）。前後の成績3ストアを比較。
- operator残検証6: iPhone両色LAN入力のcut/途絶/復帰を実Playerで確認。実timeout停止、shader variant/音声/表示fpsはhostで保証しない。
- 残リスク: 実Esc経路は通らない、完走後crash/強制killの成績復元は保証しない、cache増加と漏れの区別は実測未評価。通常動作を変える修正は未実装。
