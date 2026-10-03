# PhoneSaber Debug Recording metadata schema

This document defines the JSON written beside a PhoneSaber debug recording and
the compatibility rules used by the Python analysis tools. The machine-checked
field shapes live in `phone_saber_metadata_schema.py`.

## Versioning

- The current writer emits `formatVersion: 1`.
- Older recordings have no `formatVersion`; readers treat them as legacy version
  0. Some early tools also accepted a top-level array of frames, which remains
  supported as version 0.
- Version 1 adds an explicit version marker to the existing recording format.
  It does not change recognition, frame capture, or UDP behavior.
- Readers ignore unrecognized fields. A newer integer version is reported as
  unsupported but its known fields are still analyzed when possible.

The top-level JSON object has these fields:

| Field | JSON type | Version 1 writer | Meaning |
| --- | --- | --- | --- |
| `formatVersion` | integer | Required | Metadata format version; currently `1`. |
| `sessionID` | string | Required | Recording session identifier. |
| `width` | integer | Required | Recorded frame width in pixels. |
| `height` | integer | Required | Recorded frame height in pixels. |
| `frames` | array of frame objects | Required | Frames accepted by the raw video writer, in recording order. |
| `cameraSamples` | array of camera sample objects | Optional | Camera state snapshots emitted by diagnostic-capable builds. |
| `cameraExposureExperiment` | camera exposure experiment object | Optional | Opt-in shutter experiment in effect at Start (see "Camera exposure experiment"). Absent in older recordings, which always used auto exposure. |
| `shadowRuleTally` | shadow rule tally object | Optional | Whole-session counts of the shadow R7e / PF22 verdicts over every recorded frame (see "Whole-session shadow rule tally"). Absent in older recordings. Evidence only, never applied. |
| `guidedRecording` | guided recording object | Optional | Opt-in guided recording (ガイド付き録画): script, step boundaries and per-step counts (see "Guided recording"). Absent in manual and older recordings. |

`frames` is the only field required to identify an analyzable session. A missing
or non-array `frames` value is a structural error. Other missing or mistyped
fields are reported and treated as unknown at their own field or frame.

## Frame fields

| Field | JSON type | Version 1 writer | Meaning |
| --- | --- | --- | --- |
| `frameID` | integer | Required | Capture sequence identifier. |
| `presentationTimeSeconds` | number | Required | Recording-relative video presentation time in seconds. |
| `red`, `blue` | detection object | Required | Per-color endpoints and detection state. The object shapes are identical. |
| `redDetectionSucceeded`, `blueDetectionSucceeded` | boolean | Required | Whether the detector produced fresh, non-predicted endpoints for that color. |
| `candidateDiagnostics` | object | Optional | Per-color pipeline and candidate details; omitted when the recorder has no analysis payload. |
| `forensicCaptured`, `manualCaptured` | boolean | Required | Whether an anomaly or manual still image was saved for the frame. |
| `forensicFileName`, `manualFileName` | string | Optional | Saved still-image filename when present. |
| `blueDropoutRole`, `blueDropoutFileName` | string | Optional | BLUE dropout capture annotation and still-image filename. |
| `redDropoutRole`, `redDropoutFileName` | string | Optional | RED counterpart to the BLUE dropout fields. |
| `camera` | frame camera object | Optional | Exposure state of this frame (see "Per-frame camera state"). Absent in older recordings and when no source was available. |

Dropout roles `last-detected-before-dropout`, `dropout`, and `recovered` were written
by recorders before bridge dropout events existed. Current recordings no longer write
them (see "Bridge dropout fields" below); readers must keep accepting them. A role and
filename can be absent independently; their absence does not mean that the color was
detected.

### Detection object

| Field | JSON type | Version 1 writer | Meaning |
| --- | --- | --- | --- |
| `detected` | boolean | Required | Whether this record contains endpoints. |
| `predicted` | boolean | Required | Whether the endpoints are prediction-only. |
| `x1`, `y1`, `x2`, `y2` | integer | Optional | Endpoint coordinates in source-frame pixels. The four coordinates are present together when endpoints are recorded; they are absent when no endpoints are recorded. |

The two color objects use the same structure and coordinate units. Consumers must
not infer a failed detection from missing coordinates alone.

## Candidate diagnostics

When `candidateDiagnostics` is present, both `red` and `blue` objects have the
same `ColorCandidates` shape.

| `ColorCandidates` field | JSON type | Meaning |
| --- | --- | --- |
| `totalCandidateCount` | integer | Candidates evaluated for this color. |
| `eligibleCandidateCount` | integer | Candidates eligible for selection. |
| `maskPixelCount` | integer | Pixels in the initial color mask. |
| `morphologyPixelCount` | integer | Pixels after morphology processing. |
| `connectedComponentCount` | integer | Connected components in the processed mask. |
| `selectedCandidateIndex` | integer, optional | Index of the selected eligible candidate. |
| `selectedCandidateType` | string, optional | Selected candidate source type. |
| `selectedCandidateFinalScore` | number, optional | Final score of the selected candidate. |
| `selectedCandidateScoreBreakdown` | score object, optional | Selected candidate score components. |
| `selectedCandidate` | candidate object, optional | Full selected-candidate details. |
| `topCandidates` | array of candidate objects | Candidates retained for diagnostics, capped by the producer at three. |

Candidate object fields:

| Field | JSON type |
| --- | --- |
| `index` | integer |
| `selected` | boolean |
| `sourceType` | string |
| `eligible` | boolean |
| `finalScore` | number |
| `peakValue`, `meanValue`, `highValueRatio`, `meanColorPurity`, `clippedWhiteRatio` | measured candidate numbers |
| `isCompactRed` | boolean |
| `eligibilityRules` | array of `{name, result, value, comparison, threshold}` |
| `rejectionReasons` | array of failed production rule names |
| `scoreBreakdown` | score object |
| `rawPCAEndpoints` | endpoint object |
| `rawPCASpan` | number |
| `robustMainIntervalEndpoints` | endpoint object, optional |
| `robustMainIntervalLength` | number |
| `finalOutputEndpoints` | endpoint object |
| `continuity` | number |
| `density` | number |
| `maxGap` | integer |
| `componentArea` | integer |
| `pointCount` | integer |
| `usedPointLEDFallback` | boolean |
| `emitterDiagnostics` | emitter diagnostics object, optional (see below) |

An eligibility rule has `result: PASS` when its comparison holds and `FAIL`
otherwise. Compound rejections list the failing escape conditions that would
have kept the proposal eligible. `value` and `threshold` are null only when a
comparative witness could not be retained. Geometry comparisons recorded at
the rejection site use the detector's sampled mask units; the candidate's
saved endpoint and span fields use source-frame pixels.

An endpoint object contains `first` and `second` point objects; each point has
integer `x` and `y` coordinates. A score object contains numeric fields:
`proposalPenalty`, `radiance`, `length`, `aspect`, `extent`,
`widthConsistency`, `area`, `peakBrightness`, `meanBrightness`,
`highBrightnessRatio`, `colorPurity`, `localContrast`, `emitterTexture`,
`clippedWhite`, `longitudinalHighCoverage`, `coreSupport`,
`longitudinalCoreCoverage`, and `total`.

### Emitter diagnostics (optional, additive)

`emitterDiagnostics` is written only for frames analysed on the Debug Recording
diagnostic path (`collectPipelineDiagnostics`); older bundles and candidates
without valid evidence omit it, and readers must accept its absence. It is
built after the candidate's score, eligibility and endpoints are final and is
never read by recognition, so recording it cannot change any detection result.
Values are rounded to six decimals.

To protect the streamed file's size budget, the streamed `*_metadata.json`
carries only the compact subset that the candidate's other fields cannot
reproduce: `emitterScore`, `emitterScoreMargin`, `hasEmitterCore`,
`baseEligible`, `bladeLengthSupport`, `meanSecondChannel`, `meanMinChannel`,
`nearWhiteFraction`, `shadowR7e` with `applied`, `d240`, `ruleSatisfied`,
`shadowR7eEligible`, and `shadowPF22` with `applied`, `ruleSatisfied`,
`shadowPF22Eligible` (about 2 KiB per busy frame). The triage snapshot, and so
every compact context, carries all fields below.

| Field | JSON type | Meaning |
| --- | --- | --- |
| `emitterScore` | number | Production `emitterScore` (copied). |
| `emitterScoreThreshold`, `emitterScoreMargin` | number | `0.42` and `emitterScore - 0.42`. |
| `peakTerm`, `meanTerm`, `highValueTerm`, `purityTerm`, `clippedWhiteTerm` | number | The five addends of `emitterScore`: `clamp01((peak-200)/55)*0.32`, `clamp01((mean-160)/95)*0.23`, `highValueRatio*0.28`, `meanColorPurity*0.12`, `clippedWhiteRatio*0.05`. |
| `hasEmitterCore` | boolean | `coreByHighValueRatio OR coreByPeakAndMean OR coreByClippedWhite`. |
| `coreByHighValueRatio`, `coreByPeakAndMean`, `coreByClippedWhite` | boolean | `highValueRatio >= 0.08`; `peak >= 242 AND mean >= 190`; at least one clipped-white sample. |
| `baseEligible` | boolean | `isEmitterEligible` at the scoring site (peak, core, score and the compact-red gate). A later source-specific rule may still reject the candidate; compare with the candidate's `eligible`. |
| `compactRedGate` | boolean | The compact-red extra gate applied. |
| `majorLengthSamples`, `bladeLengthSupport` | number | PCA length in mask samples and the factor scaling the radiance / clipped-white / core-support / core-coverage score terms. |
| `localContrast`, `emitterTexture`, `brightnessVariation`, `coreSupport`, `longitudinalCoreCoverage`, `longitudinalHighCoverage` | number | Production values (copied). |
| `sampleCount`, `colorSampleCount` | integer | Component mask samples; those inside the color mask. |
| `meanMaxChannel`, `meanMinChannel` | number | Mean of the largest and smallest of R/G/B over the component. For a red component the max channel is R, so `meanMinChannel` and `meanSecondChannel` are its non-dominant channels. |
| `meanSecondChannel`, `maxSecondChannel` | number / integer, optional | Mean and maximum of the middle channel (null when the evidence had no radiance map). |
| `nearWhiteFraction` | number | Share of samples with `value >= 245 AND chroma <= 38` (the clipped-white test). |
| `brightSecondChannelFraction` | number, optional | Share of samples whose middle channel is `>= 100` (the bright-core floor). |
| `shadowR7e` | object, red only, optional | Shadow verdict of the offline "R7e" rule. **Evidence only, not applied.** |
| `shadowPF22` | object, red only, optional | Shadow verdict of the offline "PF22" purity-floor rule. **Evidence only, not applied.** Absent from bundles recorded before it existed. |

`shadowR7e` records the rule
`clippedWhiteRatio >= 0.35 OR d240 >= 4.2 OR (d240 >= 3.5 AND meanColorPurity >= 0.60)`,
where `d` is the dominant-body axial density (`points / majorLength` when no body
density was established) and `d240 = d * 240 / min(maskWidth, maskHeight)`.
Fields: `applied` (always `false`), `density`, `fallbackDensity`,
`usedFallbackDensity`, `d240`, `clippedWhiteRatio`, `meanColorPurity`, the
margins `clippedWhiteMargin`, `thickBodyMargin`, `saturatedBodyDensityMargin`,
`saturatedBodyPurityMargin` (value minus threshold), `ruleSatisfied` (the OR
expression) and `shadowR7eEligible` (`baseEligible AND ruleSatisfied`). The
thresholds come from a small offline exploration with tiny margins; production
eligibility, ranking and UDP output never read this object. It exists to
collect real distributions before any production decision.

`shadowPF22` records the red purity floor
`meanColorPurity >= 0.22 OR clippedWhiteRatio >= 0.35` (a barely-red candidate —
wall label, skin — is not an emitter unless its LED core is clipped white;
explored offline in `docs/claude/analysis/2026-10-03_motion_blur_and_blue_jumps.md`,
where it kept formal 40/40 and turned the 20261003_144936_295 f2552 label jump
into no detection). Its inputs are the candidate's own `meanColorPurity` and
`clippedWhiteRatio`. Fields: `applied` (always `false`), `ruleSatisfied` (the OR
expression), `shadowPF22Eligible` (`baseEligible AND ruleSatisfied`) and, in the
triage snapshot only, `meanColorPurity`, `clippedWhiteRatio`, `purityMargin`
(`meanColorPurity - 0.22`) and `clippedWhiteMargin` (`clippedWhiteRatio - 0.35`).
The purity margin is small (labels 0.15–0.19) and the rule rests on one event,
so production eligibility, ranking and UDP output never read this object.
Because its inputs are plain candidate fields, `phone_saber_pf22_check.py`
recomputes it for older bundles.

## Per-frame camera state

`frames[].camera` is optional and additive. Exif values come from the frame's
own sample-buffer attachment and describe exactly that frame; device values
are the newest AVCaptureDevice state pushed (about every 0.2 s, DEBUG builds)
while recording, with its age. The processing queue never calls
AVCaptureDevice.

| Field | JSON type | Meaning |
| --- | --- | --- |
| `source` | string | `exif`, `device` or `exif+device`. |
| `iso`, `exposureDurationSeconds` | number, optional | Exif ISO / exposure time; device values only when Exif lacks them. |
| `exposureBiasEV`, `brightnessValue`, `fNumber` | number, optional | Exif ExposureBiasValue, BrightnessValue (APEX) and FNumber. |
| `exposureTargetBias`, `exposureTargetOffset` | number, optional | Device exposure target bias and metering offset (EV). |
| `whiteBalanceGains` | array of 3 numbers, optional | Device white-balance gains `[red, green, blue]`. |
| `deviceSampleAgeSeconds` | number, optional | Age of the device values when the frame was recorded. |

## Camera samples

`cameraSamples` is an optional diagnostic extension. Each sample has these
fields:

| Fields | JSON type | Required in a sample |
| --- | --- | --- |
| `frameID` | integer | Optional |
| `presentationTimeSeconds` | number | Optional |
| `exposureDurationMs`, `iso`, `whiteBalanceRedGain`, `whiteBalanceGreenGain`, `whiteBalanceBlueGain`, `lensPosition` | number | Required |
| `exposureMode`, `whiteBalanceMode`, `focusMode`, `activeFormat`, `activeFormatFPSRanges` | string | Required |
| `activeMinFPS`, `activeMaxFPS` | number | Optional |

## Camera exposure experiment

`cameraExposureExperiment` is optional and additive. The app writes it at the
root of the metadata (and copies it to the triage `summary.json`) when a Debug
Recording starts; recorders created without a setting (tests, older callers)
omit it. The experiment is chosen in the app's Debug Recording box
("露出実験") and cannot change while a recording is active. It only caps the
auto exposure algorithm's maximum shutter time via
`AVCaptureDevice.activeMaxExposureDuration` (ISO stays auto); `auto`, the
default, leaves the device exposure untouched. It never changes recognition,
scoring or UDP.

| Field | JSON type | Meaning |
| --- | --- | --- |
| `formatVersion` | integer, optional | Object format version; currently `1`. |
| `setting` | string, required | `auto`, `maxShutter1_100`, `maxShutter1_120` or `maxShutter1_240`. |
| `status` | string, required | `auto` (untouched), `autoRestored` (cap reset to the device default), `applied`, `clamped` (cap clamped to the active format's exposure range), `notNeeded` (device default already at or below the request; untouched), `unsupported` / `failed` (stayed auto; see `detail`), `pending` (camera not configured yet). |
| `capActive` | boolean, optional | Whether a cap set by the app is in effect (`applied` or `clamped`). |
| `requestedMaxExposureSeconds`, `appliedMaxExposureSeconds` | number, optional | Requested and applied maximum shutter time. |
| `defaultMaxExposureSeconds` | number, optional | Device default `activeMaxExposureDuration` for the active format, read before capping. |
| `formatMinExposureSeconds`, `formatMaxExposureSeconds` | number, optional | Active format exposure range used for clamping. |
| `observedMaxExposureSeconds` | number, optional | `activeMaxExposureDuration` read back from the device at Start. |
| `detail` | string, optional | Reason for `unsupported` / `failed`. |

The per-frame effect is visible in `frames[].camera.exposureDurationSeconds`.
`phone_saber_session_report.py` shows the object in its 露出実験 section and
reports `n/a` for bundles without it.

## FrameProcessor diagnostics and regression tools

`candidateDiagnostics` is assembled from `SaberFrameAnalysis` and attached by
`FrameProcessor` to the accepted recorded frame. `FrameTrace`, performance
samples, and `[FREEZE_DIAG]` console lines are separate diagnostic outputs; they
are not fields in the session JSON and do not use `formatVersion`. The freeze
timeline tool reads those logs separately from metadata.

`run_lossless_regression.py` reads the PNG fixture manifest and emits detection
results; it does not read or validate session metadata. Session compatibility
is covered by the metadata analyzer, freeze timeline, and session comparison
tests.

## Backward compatibility and unknown values

- A missing `formatVersion` means legacy version 0; it never invalidates an
  otherwise readable session.
- `sessionID`, dimensions, frame IDs, timestamps, detection flags, coordinates,
  capture flags, candidate details, and camera samples may be absent in older
  sessions. Missing fields remain unknown; consumers do not invent zero counts,
  `false` detection results, or empty candidate lists from absence.
- Legacy detection-rate analysis uses `redDetectionSucceeded` /
  `blueDetectionSucceeded` when present. Otherwise it falls back to that color's
  boolean `detected` field and excludes records whose `predicted` value is true.
  If neither source is usable, detection status is unknown.
- Missing endpoint coordinates remove only endpoint-based metrics for that
  frame. Missing candidate diagnostics remove only candidate-based metrics.
  Missing or non-increasing timestamps remove only time-based events involving
  those frames and break adjacent-frame runs.
- Missing optional image names and camera samples make only those details
  unknown. Missing top-level dimensions does not prevent pixel-coordinate
  analysis.
- The Python validator normalizes a mistyped field to unknown and continues. It
  returns a hard error only when JSON cannot be read, the root is not a supported
  object/legacy array, or there is no `frames` array. `--strict` can make field
  warnings nonzero for CI checks.

Validate one or more sessions with:

```bash
python3 ios/PhoneSaberSender/Tools/validate_phone_saber_metadata.py path/to/session_metadata.json
```

## Optional automatic motion evidence

Format 1 adds optional root `motionEvents` and `motionSummary`, and optional
frame `processingTimeSeconds` / `motionEventIndex`. Existing frame fields retain
their meaning. Compact event contexts add role, event ID, score and measured
signals; lossless originals and overlays are separate. See
[Debug Recording motion evidence](DEBUG_MOTION_EVENTS.md) for limits, metric
scope, selection ledger and conservative repair acceptance.

`motionSummary.runtime.memoryHeadroom` (additive) records the minimum
`os_proc_available_memory()` seen while recording: `source`, `samples`,
`available` (false on the Simulator or macOS, where no value is provided), and when
available `minimumAvailableBytes`, `minimumFrameID` and
`retainedBGRABytesAtMinimum`. It is used to confirm device headroom for the
256 MiB retained-BGRA budget; it does not change any selection.

## Tracking / endpoint diagnostics

録画限定の追加schema、座標系、temporal PNG mapping、compound rejection、安全gateは [TRACKING_DIAGNOSTICS.md](TRACKING_DIAGNOSTICS.md) を参照。version 1へのadditive fieldsで、legacy sessionsのPython解析を維持する。

## Bridge dropout fields (additive, version 1)

These root fields are written by recorders that support diagnostic color selection.
Metadata without `activeColors` predates them and diagnoses both colors with the
older dropout rules. All fields are additive; readers that ignore them keep working.

| Root field | JSON type | Meaning |
| --- | --- | --- |
| `activeColors` | array of `"red"`/`"blue"` | Colors chosen at Debug Recording start (RED, BLUE or BOTH). Recognition and UDP always run for both colors; only these colors' absences and anomalies count as diagnostic failures. |
| `diagnosticWindows` | object, per active color | `firstSuccessFrameID`, `lastSuccessFrameID`, `firstSuccessTime`, `lastSuccessTime`. A color that never succeeded has no entry. Frames before the first or after the last success cannot be attributed to recognition. |
| `bridgeDropoutEvents` | array | Accepted bridge dropouts (see below). |
| `bridgeDropoutSummary` | object | `observedDropouts`, `accepted`, `rejected` (counts by cause: `gap_too_long`, `discontinuous`, `memory_unavailable`, `lower_rank`, ...), `evictedForLongerEvent`, `unclosedAtStop`, `retained`, `scope`. |

A **bridge dropout** is `detected success → short missing interval → detected success`
for one active color. The missing interval must be closed by a later success, so an
absence at the start or end of a recording is never a candidate. Whether the two
successes are the same saber is judged from the existing tracking-diagnostic geometry:
temporal interval, midpoint displacement and speed, constant-velocity prediction
residual (when two earlier successes give a velocity), length change and undirected
orientation change. The limits grow with the temporal interval and are scaled by the
saber's length (`assessment.thresholds` records them for every event). They exist only
to choose diagnostic images and never reach recognition, scoring, eligibility or UDP.

Each event has `eventID`, `color`, `beforeFrameID`/`dropoutFrameID`/`afterFrameID` with
timestamps, `missingFrameCount`, `gapSeconds`, `assessment` (`accepted`, `measurements`,
`thresholds`, `scope`), `images` and `annotation`.
`images` lists `before_success`, `dropout` and `after_success` original lossless PNGs
(`fileName`); the `dropout` entry also names `annotatedFileName`. The originals are
never modified. The annotated PNG draws the position interpolated between the two
successful detections (yellow dashed), the before detection (green) and the after
detection (magenta); `annotation.groundTruth` is `false`, it only helps to locate the
saber and never proves one is present.

The three frames of an event are ONE temporal evidence event. The triage bundle labels
them with `bridgeEventID` and `evidenceUnit`, marks the annotated image
`"auxiliary": true`, and the Mac gate counts the event once.

### Candidate geometry (triage snapshot only)

`candidateGeometry` (array of `{frameID, red?, blue?}`) exists only in the triage
snapshot handed to the bundle builder for retained event frames. It is not written to
the streamed `*_metadata.json`. See `BRIDGE_DROPOUT_DIAGNOSTICS.md`.

Each geometry entry may carry an optional compact `emitter` object (a subset of
`emitterDiagnostics`: `emitterScore`, `emitterScoreMargin`, the five `*Term`
fields, `hasEmitterCore`, `bladeLengthSupport`, `localContrast`,
`emitterTexture`, `coreSupport`, `meanSecondChannel`, `meanMinChannel`,
`nearWhiteFraction` and, for red, `shadowR7e` with `applied: false`, `d240`,
`clippedWhiteRatio`, `meanColorPurity`, `shadowR7eEligible`, and `shadowPF22`
with `applied: false`, `shadowPF22Eligible` — its inputs are the `shadowR7e`
purity and clipped-white values).

### Compact triage contexts

In a frame context only the selected frame carries `camera`, and only its
`candidateDecisionTrace` entries carry `emitterDiagnostics`; neighbour frames
carry neither, and neighbour geometry has no `emitter`. When a context exceeds
its 24 KiB budget the builder reduces detail step by step and lists each step in
`compaction`, in this order: `neighbourCandidateGeometryWinnerOnly`,
`neighbourCandidateGeometryDropped`, `selectedScoreBreakdownLimitedToLeadingEligible`,
`selectedEmitterDiagnosticsLimitedToLeadingFour` (later entries get
`emitterDiagnosticsReduced: true` instead of `emitter`),
`selectedIneligibleLimitedToTwo`, `selectedEligibleLimitedToEight`,
`selectedDecisionTraceEmitterDiagnosticsDropped`, `neighbourFramesWithin1`,
`neighbourFramesWithin0`. Contexts stay below the 32 KiB preflight limit.

## Segment markers (additive, version 1)

Operator-provided ground truth for intervals of a recording. While Debug Recording
is active the iPhone shows a segmented control 区間ラベル with
`未設定` (`unlabeled`, the default at every Start), `saberあり` (`sabersVisible`:
lit sabers in view), `saberなし` (`noSaber`: no saber or sabers off, background
only) and `赤い物隠し` (`noSaberCovered`: background only with the red background
objects covered). The control is disabled when not recording. Setting a label only
writes one value under the frame mailbox lock; the processing queue reads it with
the per-frame camera state and passes it to the recorder. The label never reaches
recognition, tracking or UDP, and nothing is read when recording is off. The keys
below are stable English identifiers; the Japanese text is UI only.

| Root field | JSON type | Meaning |
| --- | --- | --- |
| `segmentMarkers` | array of `{frameID, timestamp, label}` | One entry per label change: `frameID` is the first recorded frame carrying the new label, `timestamp` its `presentationTimeSeconds`. Frames before the first marker are `unlabeled`; an empty array means the recording was never labeled. At most 256 markers are kept (`droppedMarkerCount` counts the rest). |
| `segmentSummary` | object | Whole-session counts over every recorded frame (not only retained frames): `formatVersion` (1), `totalFrames`, `byLabel` (all four labels, each `{frames, red: {detectedFrames, measuredFrames}, blue: {...}}`), `falsePositiveFrames` (`noSaber` and `noSaberCovered`, each `{red, blue}`), `markerCount`, `droppedMarkerCount`, `definition`. |

`detectedFrames` counts frames whose fresh output for the color is `detected: true`
(prediction included); `measuredFrames` counts `<color>DetectionSucceeded: true`
(prediction excluded). `falsePositiveFrames[label][color]` equals
`byLabel[label][color].detectedFrames` for the two no-saber labels: any detection
while no saber is in view is a false positive. Frame entries in `*_metadata.json`
do not change; the label of a frame is derived from the markers.

The triage bundle carries the same data compactly:

- `summary.json` → `segmentSummary`: the root object plus `markers` (a copy of
  `segmentMarkers`).
- Each frame context → `segmentLabel`: the label of the selected frame (the newest
  marker at or before it). A few bytes; it is included before the 24 KiB compaction
  check.

Recordings made before this feature have neither root field, no `segmentSummary` in
`summary.json` and no `segmentLabel` in contexts; all readers treat them as valid.
`phone_saber_metadata_schema.py` types the new fields (an unknown marker label is a
warning) and offers strict `segment_marker_errors` / `segment_summary_errors`, which
`phone_saber_triage_codex.py` applies to `summary.json` (counts consistent, false
positives equal to the no-saber detections, no unknown keys) alongside the
`segmentLabel` check in contexts.

`phone_saber_segments.py <bundle_or_metadata> [--json]` prints frames, detections
and detection rates per label and color, and the false-positive rates under
`noSaber` / `noSaberCovered`. With a bundle (or `summary.json`) it reads
`segmentSummary`; with a full `*_metadata.json` it recomputes the counts from
`frames` and `segmentMarkers` and warns when they differ from the recorded
`segmentSummary`. Older metadata counts every frame as `unlabeled`; an older
`summary.json` without `segmentSummary` exits with status 2.

## Whole-session shadow rule tally (additive, version 1)

The shadow R7e and PF22 verdicts (`emitterDiagnostics.shadowR7e` /
`shadowPF22`, red only, `applied: false`) are attached to every candidate while
Debug Recording collects pipeline diagnostics, but the bundle keeps them only for
the retained context frames. `DebugShadowRuleTally` therefore counts them on the
iPhone over EVERY recorded frame (counters only, O(1) memory, updated in
`DebugVideoRecorder.append` after the frame is accepted; it reads the verdicts that
are already there and does no detector work). Recognition, ranking, eligibility,
tracking and UDP never read it. Root `shadowRuleTally` in `*_metadata.json`, copied
into `summary.json`:

| Field | JSON type | Meaning |
| --- | --- | --- |
| `formatVersion` | integer | `1`. |
| `applied` | boolean | Always `false`. |
| `rules` | array | `["r7e", "pf22"]`. |
| `colors` | array | Diagnostic (active) colors, in `red`, `blue` order. Only red carries verdicts; blue counts winners only. |
| `totalFrames` | integer | Every recorded frame. |
| `total` | bucket | Counts over the whole session. |
| `byLabel` | object | All four segment labels (`unlabeled`, `sabersVisible`, `noSaber`, `noSaberCovered`), each a bucket; they add up to `total`. |
| `byExposure` | object | Non-empty per-frame exposure buckets from `frames[].camera.exposureDurationSeconds`: `le1_240` (≤ 1.02/240 s), `le1_120` (≤ 1.02/120 s), `le1_60` (≤ 1.02/60 s), `gt1_60`, `unknown` (no camera state); they add up to `total`. |
| `exposureExperimentSetting` | string | Optional. The session-wide `cameraExposureExperiment.setting` (one per recording). |
| `winnerRejectionSamples` | object | Per rule, at most `sampleLimit` (6) frames whose WINNER the rule would reject, outside `noSaber` / `noSaberCovered` (a rejection there is expected; one under `sabersVisible` or `unlabeled` needs an ORIGINAL PNG check). Kept by stride decimation, so they are evenly spread over all such rejections and deterministic. Each `{frameID, timestamp, label, color, exposureBucket, meanColorPurity, clippedWhiteRatio, d240, shadowR7eEligible, shadowPF22Eligible}`. |
| `winnerRejectionsOffered` | object | Per rule, how many rejections the samples were drawn from. |
| `sampleLimit`, `definition` | integer, string | Bound and plain-language definition. |

A bucket is `{frames, red: counts, blue: counts}` (active colors only). `counts`
is `{winners, eligibleCandidates, r7e: rule, pf22: rule, both: {winnersJudged,
winnersRejected}}`: `winners` are frames with at least one eligible candidate of
the color; the winner is the first eligible candidate in production order (the
one `selectedCandidateIndex` names). `rule` is `{winnersJudged, winnersRejected,
eligibleJudged, eligibleRejected, noEligibleLeft}`: a winner or eligible candidate
is judged when it carries the verdict and rejected when the verdict's
`shadow…Eligible` is false; `noEligibleLeft` counts rejected winners whose frame
had no eligible candidate the rule would keep (the color would have no detection).

In `summary.json` each sample additionally states `retainedContext` (the frame is
in the bundle's retained context frames) and, when the frame already is a selected
image, `image` and `frameContextPath` (never the annotated copy). No image is added
or displaced for the samples: the image budget (12 by default) and the 32 KiB
context limit are unchanged, and bridge / tracking images keep their priority.

`phone_saber_metadata_schema.shadow_rule_tally_errors` checks the object strictly
(nesting rejected ≤ judged ≤ winners, label / exposure sums equal `total`, samples
bounded and outside the no-saber labels, no unknown keys). `validate_document`
warns about and drops an inconsistent root tally; `phone_saber_triage_codex.py`
rejects a bundle whose `summary.json` tally is inconsistent. Readers:
`phone_saber_shadow_tally.py` (per-bundle view and the two promotion checks of the
2026-10-03 rule study: zero rejections under `sabersVisible`, ≥ 80 % under the
no-saber labels; `unlabeled` is shown separately and counted toward neither),
`phone_saber_session_report.py`, `phone_saber_sessions_overview.py` (the R7e / PF22
columns prefer the recorded whole-session tally, marked 全) and
`phone_saber_pf22_check.py` (whole-session table first). Older bundles show n/a and
keep their selected-frame tallies.

## Guided recording (additive, version 1)

Opt-in. The Debug Recording box has a button ガイド付き録画を開始 next to the manual
Start Recording (which is unchanged and stays the default). A guided recording runs
a fixed, versioned step script (`GuidedRecordingScript.shootingPlanV1` in
`GuidedRecording.swift`, the 2026-10-04 shooting plan in `docs/claude/STATUS.md`):
each step has an unlabeled lead-in (spoken Japanese cue, then a 3-2-1 countdown) and
a hold that carries the step's segment label. The labels go through the same path as
the manual 区間ラベル picker, so `segmentMarkers` / `segmentSummary` are written as
usual. During swing steps the existing one-shot lossless capture is requested every
1.5 s (at most 6 per guided recording instead of 3; the 128 MiB buffered, 256 MiB
retained and 64 MiB lossless-disk caps are unchanged). The script stops the
recording at the end (auto-transfer as usual); Cancel stops it at once. Like the
segment label, the current step reaches the recorder through the frame mailbox and
never reaches recognition, tracking or UDP.

Root `guidedRecording` (also copied to triage `summary.json`):

| Field | JSON type | Meaning |
| --- | --- | --- |
| `formatVersion` | integer | Object format version; currently `1`. |
| `scriptID`, `scriptVersion` | string, integer | Script that ran (`shooting_plan_2026_10_04`, version 1). Any change to steps or timings gets a new version. |
| `outcome` | string | `completed` (the script stopped the recording), `cancelled` (Cancel), `incomplete` (the recording ended first: limit, background, interruption). |
| `plannedSeconds` | number | Planned duration of the script. |
| `steps` | array | One object per script step, in order: `index`, `id`, `title`, `label` (segment label of the hold), `plannedLeadInSeconds`, `plannedHoldSeconds`, `plannedLosslessCaptures`, `leadInStartFrameID` / `leadInStartTimestamp`, `holdStartFrameID` / `holdStartTimestamp`, `holdEndFrameID` / `holdEndTimestamp` (first lead-in frame, first and last hold frame actually recorded; absent when the step was not reached), `frames` (hold frames), `red` / `blue` (`{detectedFrames, measuredFrames}` over hold frames, same definitions as `segmentSummary`). |
| `losslessCaptures` | array of `{stepIndex, frameID}` | Lossless frames captured while the guide ran (`manual_frame_<frameID>.png`). At most 32 entries. |
| `definition` | string | Human-readable definition of the counts. |

Under a `noSaber` / `noSaberCovered` step every detected frame is a false positive.
In the triage bundle a guided recording keeps up to 4 of its swing lossless frames
ahead of bridge and tracking units (one per step first); manual recordings keep the
previous selection. `phone_saber_metadata_schema.py` types the field and offers the
strict `guided_recording_errors`, which the triage input contract applies to
`summary.json`; `phone_saber_session_report.py` shows the ガイド付き録画 section
(per-step table in the same shape as the per-label table, and which swing frames
are in the bundle) and `n/a` for bundles without it.
