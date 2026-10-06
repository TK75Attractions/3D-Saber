# PhoneSaber tracking diagnostics / repair evidence

単一repoの Git/Unity root は `3D-Saber/`、ツールは `PhoneSaber/` 内です。以下のコマンドは、指定がない限り Git root から `cd PhoneSaber` して実行します。

対象は `3D-Saber/PhoneSaber`（旧 school-festival）の `main`。認識閾値、HSV、候補生成・順位・eligibility、PCA/body/fallbackの採用条件、UDP payloadは変更していない。今回のproductionファイルへの編集は診断収集の追加だけで、実機の根本原因を確定した修正ではない。

## 変更ファイル一覧

`ios/PhoneSaberSender/PhoneSaberSender/`:

- `BGRADetection.swift`: recording限定traceとcompound comparator。
- `DetectionCore.swift`: 実際に採用されたbody経路とgating値。
- `DebugVideoRecorder.swift`: frame診断、bounded画像保持、実送信ログ。
- `DebugRecordingTriage.swift`: 不連続score、対応付け、compact context、選択画像mapping。
- `FrameProcessor.swift`: recording frameと送信callbackの対応付け。
- `CameraViewModel.swift`: 実send開始時のwire座標記録。

`ios/PhoneSaberSender/Tools/`:

- `phone_saber_tracking_diagnostics.py` (新規): temporal evidenceの検証・gate共通判定。
- `phone_saber_metadata_schema.py`: additive metadata fields。
- `phone_saber_triage_codex.py`: 入力mapping検証、prompt、report、再解析、second opinion。
- `phone_saber_auto_repair.py`: targeted tracking gateとreasonCodes。
- `test_phone_saber_tracking_diagnostics.py` (新規): Mac側11テスト。
- `PHONE_SABER_DEBUG_METADATA_SCHEMA.md`: この詳細schemaへの案内。
- `TRACKING_DIAGNOSTICS.md` (新規): 調査と実装の説明。

`ios/PhoneSaberSenderTests/`:

- `DebugRecordingTriageTests.swift`: temporal診断、compound traceのテスト。
- `DetectionCoreTests.swift`: recorder/PNG/mapping/欠損の統合テスト。

## 変更前のコード調査

- `FrameProcessor` は録画中だけ `collectPipelineDiagnostics` を有効にする。`DebugVideoRecorder` はwriterが受け付けたframe ID・相対presentation timestamp・fresh/predicted出力・二色の成否・候補情報をstreaming JSONに保存する。候補は上位3件と実際のselectedを保存。H.264のraw/overlay動画は補助資料で、lossless PNGだけが視覚的ground truth。
- manual、forensic、RED/BLUE dropoutのlast true / first false / recoveryが既に保存される。motion detectorにもendpoint jump・length change・prediction residual・flicker等は存在した。ただしcandidate switchは候補番号/typeの変化に依存し、並び替えと物体切替を区別できなかった。
- motion PNGは上位3eventにpre/at/postを保存。preは15frameごとの間引き、postは約0.5秒後。atはmerged eventの最大score時点。signal maximaは種類ごとに異なるframeで発生し得た。11枚の隣接時系列を保証する仕組みではなかった。
- triageはincident metadataと近傍を最大256frame保持し、通常最大12画像を選択。旧summaryには既にPNG→frame ID/timestamp/context pathの情報があった。しかし全ての対応値をcontext内の当該frameまで突き合わせる検証と、event単位の明示的な画像timelineが不足していた。
- candidateのraw PCA/comparison、robust interval、final output endpointsはfull recordingに存在した。body PCAの採用経路・実際の採用条件と未採用理由、実送信開始のUDP座標は別段階として記録していなかった。Luna向けcompact contextでは主にspan、scalar metrics、final endpoint、failed eligibility traceだけを渡していた。
- Mac側は選択PNGをCLIの`--image`で添付し、`summary.json`と選択contextだけを隔離したread-only directoryにコピーする。動画、full metadata、未選択画像は渡さない。モデルはLuna/MAX、second opinionはSol/Highに固定。
- report version 3にはsession/input/provenanceとA–G findings、limitations、repair_assessmentがある。旧gateは既にranking/endpointを許可し、`detected=false`を必須にしていなかった。実際の停止条件は、actionableでない、real saber未確認、production change unsupported、metadataとの不整合、stage不明/範囲外、具体的変更なし、画像/color参照不足、low confidence、latency-only、threshold変更の独立例不足、formal corpus coverage不足等。
- 再解析はeligibility不足の文言と既存rejection traceに偏り、最大1回。Solはvisible saber・既知recognition stage・画像参照・uncertaintyに加えてrejection value/thresholdを要求した。このためdetected=trueの端点不安定を画像で確認し数値で追えても、eligibility値が無いと進みにくかった。

## 新しいrecording schema

formatVersionはadditiveなversion 1のまま。既存dropout/rule/value/threshold/candidate/color/rejection metricsを維持する。

| 場所 | 追加/明確化した値 |
| --- | --- |
| `frames[].candidateDiagnostics.<color>` | 全eligible candidatesに基づく`secondBestScore`、`scoreMargin` |
| `selectedCandidate` / `topCandidates[]` | centroid、bbox、既存area/score/geometry/brightness/color metrics、`endpointPipeline`、`compoundRejections` |
| `endpointPipeline` | source画像pixelのrawPCA、robustInterval、採用したbody PCA、fallback、finalSelectedを別々に保存 |
| `endpointPipeline` | `endpointSource=bodyPCA/fallbackPCA`、bodyAdopted、robustIntervalAdopted=false、fallbackReason、gatingValues、gatingCoordinateSpace |
| `frames[].tracking.<color>` | midpoint、length、orientation、端点/中点displacement、length/orientation change、score change、margin collapse、candidateSwitch/confidence、endpointPathChanged、detectedToggle、stageDiscontinuities、scoreComponents、provisional instabilityScore |
| `udpTransmissions[]` | frame/color、実際にsend開始callbackが呼ばれたwire座標、送信に使用したsource端点、output座標系、host timestamp、state=sendStarted |
| `motionEvents[]` | 保存済みpeakのframe/score、録画全体のmax frame/score、maxが保持できたか、contextIncomplete、peak中心の連続窓(11/8/5 frame)の画像一覧 |
| `motionSummary.trackingCapture` | 録画maxと保持peakのframe/color/score、最高区間未保持、保持数、欠損状態。compact bundleにも引き継ぐ |

`robustInterval`とbody PCAは異なる推定値。productionはrobust interval端点そのものを直接採用していないため、診断も採用したと偽らない。body PCAは既存production branchで計算/採用された場合のみ保存し、不採用時に診断のため再計算しない。fallback理由はbody点数不足、retained ratio条件、body support/PCA availabilityに分ける。gatingValuesは決定時mask gridの値と実際の閾値を保存し、source画像の幾何pixelと混同しない。

送信前にはFrameProcessorの端点順序安定化と短期predictionがある。candidateのfinalSelected、fresh/predictedなemitted endpoint、UDP sendStartedを混ぜない。送信待ちで置換されたrequestや録画終了後に開始したsendには送信済み値を捏造せず、selected contextではunavailableAtRecordingStopとする。sendStartedはMac/Unityでの受信確認ではない。

## 不連続scoreとcandidate correspondence

source端点の順序は最小移動で揃える。前二frameのtimestamp間隔を使って等速予測し、現在の端点とのresidualを直前のsegment lengthで正規化する。raw PCA、robust interval、採用body、finalSelectedでそれぞれ以下を記録する。

`stage = normalized endpoint prediction residual + abs(log(currentLength / previousLength)) + undirected orientation change / pi`

最終ranking scoreは以下の合計。

- finalSelectedのstage discontinuity
- geometry対応によるcandidateSwitch: 2
- endpointSource/fallback理由の変更: 1
- detected true/false toggle: 2(metadata の scoreComponents / instabilityScore には記録するが、tracking event の ranking と保持判断では 0 として扱う。消失は bridge dropout event として別に報告する。BRIDGE_DROPOUT_DIAGNOSTICS.md 参照)
- selected scoreの相対変化: 最大0.25
- score marginの相対collapse: 最大0.5

絶対displacementや速度だけでは異常としない。速い等速移動はresidual=0で低score。急な物理運動もrank上位になる可能性があるので、scoreは録画内の比較順位にだけ使う。認識判定、production固定threshold、real saber/異常の証明には使わない。録画中には画像保持用のprovisional scoreを使い、Stop時に保存した各frame/colorのscoreComponentsから最終score/rankingを集計する。

candidate対応はcentroid距離をspanで正規化し、bbox IoU、span比、area比、無方向axis angleを比較する。前selectedに対して現在の別候補、または現selectedに対して前の別候補が、selected同士より明確に近い場合だけswitch markerを付ける。候補番号やproposal typeだけの変化はswitch根拠にしない。候補上位3件とselectedで対応できない高速移動はambiguousMotionとし、物体切替を断定しない。これらの定数も診断の対応heuristicでありproductionの認識条件ではない。

## 連続画像とmapping

録画中にsource原画を独立bufferへcopyし、直前5frameと最上位1区間のN±5を保持する。raw画像のPNG化はStop時だけ。camera poolのbufferを長時間占有しない。memory capは11原画と5frame ringを収めるためDebug Recording限定で128→192 MiBに変更し(その後 bridge dropout 追加時に256 MiBへ変更)、legacy copy/reservationも同じcapに数える。通常認識でring/copy/history/JSON/新規PCA処理は行わない。

peak中心の連続窓を時系列順で選ぶ(単独なら11枚、bridge event 1件と共存するときは8枚、最低5枚)。score<1.0のtracking eventはbridge eventに譲って丸ごと外し、ledgerに`bridge_priority`を記録する。12枚制限の残りを既存manual等に使う。実frame IDがN±5を外れるframeで穴埋めしない。開始/終了、writer gap、copy/memory失敗、64 MiBのbundle byte制限で足りない場合は欠損を示す。最高rankのframeを保持できなかった場合、録画maxと保存済みpeakを分離し、別PNGに未保持frameのscoreを付けない。

rolesはN-5..N-2=before、N-1=onset、N=peak、N+1..N+4=after、N+5=recovery。これらは位置のラベルで、実際にfailure/recoveryが起きたという主張ではない。

各選択contextの`imageMapping`にはsessionID、frameID、timestamp、color、image path、eventID、eventRoleを明記する。summaryの同じ値と、context内の当該frame/timestampまで照合する。event role/signal/failureTypeの不一致も拒否する。旧compact contextの通常画像は互換読取りを維持し、新tracking画像のmapping欠落はfail closed。Lunaには`frame N -> image path -> Image ID -> context`と11枚のordered numeric timelineを渡す。

## compound rejectionとmodel context

`core-line-weak-bridge`は親rejectionRuleとして出し、span/retainedBody/bodySpan/bodyPurity/bodyHighValue/bodyContinuity/bodyDensityをtrigger条件の実際のcomparator、value、threshold、satisfiedで表す。production eligibilityの論理は同じ。親は拒否を表すが、satisfied=trueな個別条件をfailedRuleとは呼ばない。閾値不明の親markerにはcompact traceで明示nullを出し、Swift Codableのoptional省略とMac側のexact schemaを揃える。

Lunaはdetected=true instabilityとdropoutの両方を解析する。case A=candidate selection、B=mask/component/PCA、C=robust/body、D=selection/path/fallback、E=観測した送信端点が安定したdownstream調査対象を区別する。最初の不安定stageを探し、画像で本物のsaberと不安定症状を確認してからproduction changeを判断する。

analysis_report.json/mdの既存formatVersion 3と全既存finding section/repair_assessmentを維持し、optional `tracking_assessment`を追加する。

- symptom_confirmed_in_images
- first_unstable_stage: candidate_selection / mask_component / PCA / robust_body / endpoint_selection / fallback / downstream / unknown
- temporal_image_ids
- concrete_cause、concrete_production_change、expected_effect、regression_risk

## gate / re-analysis / second opinion

既存のvisual/provenance/corpus/threshold independent-example/verification/reviewer/main/push gateは維持する。tracking修正は既存の安全条件に加え、同じeventの前・peak・後を含む少なくとも3画像、実frame IDの連続性、candidate/pipeline履歴、画像上の症状確認、具体的stage/cause/change/effect/riskを必要とする。downstreamはrecognition repairの対象外。

追加tracking gateは、repair evidenceがtracking finding、numeric geometry/candidate/path peak、またはendpoint repairを対象にする場合に適用する。安定tracking画像が同梱されるだけのeligibility/dropout-only repairを止めない。

`repair_gate.reasonCodes`とterminal `repair_status.json` / `final_report.json`は、visualEvidenceMissing、temporalEvidenceMissing、frameMappingMissing、rootCauseStageUnknown、insufficientCandidateHistory、insufficientEndpointHistory、noConcreteRepairProposed、productionChangeUnsupported等を出す。既存の説明文reasonsも維持し、provenance/corpus/threshold独立例不足にもcodeを付ける。

Lunaが不足を訴えた種類の証拠がselected bundle内に既にある場合だけ、その種類のstructured enrichmentでLunaを最大1回再実行する。実際に欠けるtemporal historyをeligibility traceで代用して再解析しない。tracking判断欄だけの読み落としも既存の十分なnumeric eventを使って補える。

Sol/Highはread-only。trackingではreal saber・画像上の不安定・前/peak/後の参照・numeric timeline・限定されたstage・uncertaintyが必要。画像がない場合は進めない。eligibilityでは従来のmeasured rule evidenceを維持し、compound条件のvalue/thresholdも数える。既存flowはLuna→必要な再解析→必要なSol second opinion→actionable→Sol repair→verification→independent reviewer→commit→normal push。

## Testsと検証範囲

Swift XCTestにはstable/速い等速移動、candidate switchと番号だけの入替え、raw/robust安定・後段geometry jump、endpoint source switch、dropout/recovery toggle、compound comparator/satisfied、11frame selection/PNG画素とmetadata対応を追加。録画統合テストは最高score frameのcopy失敗も注入し、保持peakとのscore取り違えとN+6による穴埋めがないことを検証する。

Pythonには11枚のtransport/input/prompt、mapping不一致/欠落、十分なdetected=true evidenceからのgate、欠損時のreasonCodes、Luna再解析1回、証拠欠落時の再解析禁止、visual confirmationなしSol禁止、incidental tracking付きdropout gate、compoundのみのSol evidence、role/signal/type偽装拒否、compound satisfaction整合を追加。

全正式verificationは既存 `tools/verify_phone_saber.sh` を使用する。正式lossless manifest/corpusは変更せず40/40を要求。実機の新録画やUDP受信/Unityでの再現確認を実施したとは扱わない。synthetic timelineとmodel stubのtestは、実機原因や実際のLuna/Sol判断の成功を証明するものではない。

最終検証 (2026-10-01、`.verify-logs/phone-saber/20261001-023032`):

- iOS XCTest: PASS、122/122、failure/skip/worker killなし。xcresultによる正式分類もPASS。
- Detection / Static BGRA Detection: PASS。
- formal lossless corpus: PASS、40/40、optional manifest fixture欠落0。
- Tools: PASS、129/129 (新trackingテスト11件を含む)。
- iOS Release: PASS。
- `git diff --check`: PASS。
- 独立したread-only reviewer: 指摘3件を修正し、最終null表現修正まで再レビュー済み。阻害事項なし。
- 追加で検出したUnity capabilityはBLOCKED (ユーザーのUnity Editorがprojectを使用中)。EditMode/PlayMode/CompileはNOT_RUN。このため検証コマンドの総合exitは2だが、今回要求された上記必須検証は全てPASS。Unity repositoryの変更はない。

通常30fpsでの実機benchmarkは未実施。追加の画像copy/history/score/JSON処理はRecording内に限定し、通常時の追加状態はnilの診断参照とflag確認に留める。送信ログは非同期であり、その時間を認識処理時間の数値に混入させない。
