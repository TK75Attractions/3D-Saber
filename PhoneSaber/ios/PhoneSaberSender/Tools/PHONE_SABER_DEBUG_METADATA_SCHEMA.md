# PhoneSaber Debug Recording metadata schema

These tools live in the single 3D-Saber repository under `PhoneSaber/`. Run the commands below from `PhoneSaber/` unless specified otherwise; the Unity project is its parent directory.

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
`nearWhiteFraction` and the applied color-specific verdict (`warmNoDeepRed` for RED,
`blueNoDeepSupport` for BLUE). The triage snapshot, and so
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

Retired `shadowR7e` / `shadowPF22` emitter fields and root / summary
`shadowRuleTally` remain accepted as legacy optional fields. Readers ignore their
payloads; reports and overview pages do not render them. New recordings omit them.

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
`nearWhiteFraction` and the applied `warmNoDeepRed` / `blueNoDeepSupport` verdict).

`blueNoDeepSupport` carries `applied`, `deepCount`, `pixelCount`, `rejected`, and
optional `rejectionReason` (`"blueNoDeepSupport"` when rejected; nil is omitted or
encoded as null). It counts unique, clipped original pixels within a one-pixel
square of the production sample points multiplied by sample step. Deep blue is
`B >= 180 && R*100 < 40*B && G*100 < 65*B`. It runs on eligible BLUE candidates
after all existing gates and ranking, rejecting `deepCount == 0`. Counts and
comparisons use integers. The decision trace records the same reason; full and
streamed emitter metadata and compact geometry include the applied verdict.
Existing compaction and the 32KB preflight limit remain in force.

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

## Guided recording (additive, version 1)

Opt-in. The Debug Recording box has a button ガイド付き録画を開始 next to the manual
Start Recording (which is unchanged and stays the default). A guided recording runs
a fixed, versioned step script (`GuidedRecordingScript.shootingPlanV3` in
`GuidedRecording.swift`; v1 `shooting_plan_2026_10_04` also had red-object steps):
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
In the triage bundle a guided recording keeps up to 5 of its lossless frames (swings and, since v3, one in the first saberなし hold)
ahead of bridge and tracking units (one per step first); manual recordings keep the
previous selection. `phone_saber_metadata_schema.py` types the field and offers the
strict `guided_recording_errors`, which the triage input contract applies to
`summary.json`; `phone_saber_session_report.py` shows the ガイド付き録画 section
(per-step table in the same shape as the per-label table, and which swing frames
are in the bundle) and `n/a` for bundles without it.

## Start/stop handling periods (additive, version 1)

Start and Stop are tapped on the iPhone, so the first and last seconds of a recording
show the operator walking away from / back to the phone. Events whose center frame
lies in the first `startSeconds` (3 s) or the last `stopSeconds` (5 s) of the
recording are **de-prioritised, never excluded**: the tracking event and the two
retained bridge events are chosen outside these periods first (then by the existing
ranking score / gap, then a `sabersVisible` segment label as a tie-break). When no
event outside them exists, one inside is still kept and marked. Segment labels never
exclude an event: events under `noSaber` / `noSaberCovered` remain background
false-positive evidence. Diagnostic image selection only (`DebugHandlingPeriod`);
recognition, scoring, eligibility and UDP never read it.

The stop period is known only at Stop, so the recorder keeps recent leaders
provisionally until they are older than `stopSeconds` (`DebugHandlingAwareRetention`;
at most `capacity + 1` provisional events). When every event lies in a handling
period (for example a recording shorter than 8 s) the choice is the plain ranking
of earlier builds.

| Field | Where | Meaning |
| --- | --- | --- |
| `handlingPeriod` | tracking entry of root `motionEvents`; each `bridgeDropoutEvents` entry | `true` when the center frame (tracking peak / bridge dropout frame) lies in a handling period. |
| `selectionNotes` | same entries | Short strings: the period of the center frame, why it was kept (for example a higher-ranked frame in a handling period that was de-prioritised, or a fallback), and a `sabersVisible` tie-break. |
| `handlingPeriod`, `highestRankedFrameDeprioritised`, `handlingPeriodSeconds` | `motionSummary.trackingCapture` | The kept window's period, whether the recording maximum was pushed below it by a handling period (then `highestRankedFrameMissing` stays `false`), and `{startSeconds, stopSeconds}`. |
| `selectionNotes` | `summary.json` root (optional) | `{handlingPeriodSeconds: {startSeconds, stopSeconds}, events: [...]}`; each event has exactly `kind` (`tracking` / `bridge`), `eventID`, `centerFrameID`, `handlingPeriod`, `selected` and `notes` (the recorder notes plus whether it reached the bundle). |

The triage bundle orders bridge events the same way (`handlingPeriod: false` first,
then the longest gap, then a `sabersVisible` dropout frame). Bundles recorded before
this feature have none of these fields and keep their old order; all readers treat
them as valid. `phone_saber_metadata_schema.selection_notes_errors` is the strict
check that `phone_saber_triage_codex.py` applies to `summary.json` (at most 8 events,
1–8 notes of at most 300 characters each, no unknown keys);
`phone_saber_session_report.py` prints the notes under "Tracking event".
