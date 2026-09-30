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

Dropout roles currently used are `last-detected-before-dropout`, `dropout`, and
`recovered`. A role and filename can be absent independently; their absence does
not mean that the color was detected.

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

## Tracking / endpoint diagnostics

録画限定の追加schema、座標系、temporal PNG mapping、compound rejection、安全gateは [TRACKING_DIAGNOSTICS.md](TRACKING_DIAGNOSTICS.md) を参照。version 1へのadditive fieldsで、legacy sessionsのPython解析を維持する。
