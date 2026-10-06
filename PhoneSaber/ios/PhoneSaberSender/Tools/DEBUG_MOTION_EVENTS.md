# Debug Recording motion evidence

These tools live in the single 3D-Saber repository under `PhoneSaber/`. Run the commands below from `PhoneSaber/` unless specified otherwise; the Unity project is its parent directory.

This extension selects evidence automatically during Debug Recording. It does
not change production segmentation, eligibility, ranking, endpoints, UDP,
reviewer requirements or the formal corpus. It does not establish ground truth.

## Frame identity and the original capture failure

`FrameProcessor.sequence` is the camera callback frame ID. The same ID, BGRA
buffer and presentation timestamp go to the recorder and its metadata. Writer
skips leave gaps; frame IDs are not video ordinal indices. Event PNGs are encoded
from the retained original buffer after Stop, with the original ID in their name.

Session `phonesaber_20260930_005824_838` had two selected PNG references to frame
709 with identical SHA-256. Frame 708 was numeric context, not a saved PNG. There
was no demonstrated numbering offset. The previous early capture budget and
selection admitted duplicate evidence. Selection now deduplicates by source
frame ID and prefers automatically retained high-score events.

## Diagnostic constants

All switches and thresholds live in `DebugMotionThresholds`. Geometry is in
source pixels; time is the recording presentation timestamp. These are provisional
capture heuristics, not calibrated physical limits. Actual device measurements
are still required before tuning them.

| Sign | Initial trigger |
| --- | --- |
| Dropout | fresh detection true for at least 3 frames, then false |
| Flicker | at least 3 transitions in 8 accepted frames |
| Endpoint jump | 3600 px/s or vector acceleration 90000 px/s² |
| Length change | more than 35% from previous fresh detection |
| Prediction residual | more than 80 px from constant-velocity prediction |
| Candidate ambiguity | at least 2 eligible candidates, gap below 5 score units, or selected type/index switch |
| Near miss | an available numeric failed-rule relative margin at most 8% |
| Delay | interval above 55 ms, frame-ID gap, or processing above 40 ms |

Fresh success is used instead of held/predicted detection. Endpoint order is
aligned by minimum displacement. Candidate index changes are a heuristic, not
persistent object identity. Gap and failed-rule margin use the existing top-three
candidate diagnostics; unavailable metrics are omitted, never invented. Processing
time covers FrameProcessor entry through recognition/UDP emission, before recorder
append. It is not camera-to-display latency. Frame gaps can reflect writer or
callback skips; they do not identify which stage dropped a frame.

Each active sign has a bounded score (ordinary ratio clamped to 1–4; inverse
margin/gap 1–2). Sum at least 1 triggers an event. Same-color hits merge within
200 ms; RED, BLUE and global delay are independent. Per-kind event values are
maxima and may come from different frames (`event_max_per_kind`). Per-frame states
and endpoints remain attached to the actual selected frame.

## Bounds, selection and hot path

- Numeric history is a preallocated 64-slot circular buffer, owned by the recorder.
  No recorder or motion history exists when Debug Recording is off.
- Pre-roll keeps at most four original BGRA buffers, sampled every 15 accepted
  frames and expired at sampling ticks after 2 seconds. At 30 fps these represent
  about 2 seconds. Stride 15, rather than 3, limits retained camera buffers and
  initial memory pressure; this choice has not been calibrated on an iPhone.
- When any active color has a selected candidate with a centroid, the recorder
  keeps one contiguous tracking window instead (peak N±5, up to 11 originals;
  trimmed to 8 or 5 beside bridge dropout events, see
  [BRIDGE_DROPOUT_DIAGNOSTICS.md](BRIDGE_DROPOUT_DIAGNOSTICS.md) and
  [TRACKING_DIAGNOSTICS.md](TRACKING_DIAGNOSTICS.md)); pre-roll and the
  pre/peak/post scheme below then stop. They apply only while no selected
  candidate has been seen.
- Up to three strongest events retain one nearest sampled pre-frame, one peak
  event frame, and one first accepted frame at least 0.5 seconds after the last
  event hit. Continued hits renew post-roll. Early Stop can leave a role absent.
  Higher scores replace weaker events under count or memory pressure.
- The conservative retained-BGRA accounting cap is 256 MiB (Debug Recording
  only), covering the tracking window, bridge dropout originals, pre-roll,
  motion references and two reserved legacy last-detected buffers; legacy
  buffered copies alone are further capped at 128 MiB. The minimum
  `os_proc_available_memory()` seen while recording is written to
  `motionSummary.runtime.memoryHeadroom`. A 1080p buffer is
  about 8 MiB. Shared references can be counted more than once. This cap excludes
  camera/writer pools, numeric metadata and Stop-time encoder/overlay memory.
- Observing a frame computes only scalar geometry/candidate metrics, bounded
  history, and occasional event updates. It does not copy pixel planes or encode
  PNGs. Event lookup is constant by color. Legacy metadata streaming and occasional
  legacy BGRA copies remain. Additional JSON construction and all PNG/overlay
  work run after Stop in a detached task.
- Event originals and separate overlays have a combined 192 MiB storage cap;
  legacy originals retain their 64 MiB reservation. The aggregate session cap
  remains 864 MiB. An event overlay is optional and excluded from the transport
  bundle; the lossless source PNG is never drawn on.
- Selection sorts by score descending, then event ID and roles `event_at`,
  `event_pre`, `event_post`, followed by legacy evidence. It deduplicates frame
  IDs, caps motion bundles at 12 originals (legacy-only explicit hard ceiling 20), and
  reserves 2 MiB for contexts/summary inside the unchanged 64 MiB transport cap.
  If fewer images fit, it does not enlarge the transport cap.

## Metadata and Luna / repair gate

Format version 1 remains readable by existing Codable and Python readers.
Additive root fields are `motionEvents` and `motionSummary`; additive frame
fields are `processingTimeSeconds` and `motionEventIndex`. Compact contexts
include measured timing, candidate gap/margin/index and optional `motionEvent`.
Event images have event ID, role, anomaly score, sign values/thresholds/scores,
selection reasons and both colors' actual states/endpoints. Overlay filenames
are in full metadata only.

`motionSummary` records all sign switches/thresholds, triggered-sign counts,
mean/max scores including zero-count signs, observation timing/memory scope,
and every event's score and selection code. Counts combine RED/BLUE above-threshold
signs; they are not full raw motion distributions. Bundle selection updates codes
to selected, lower_score, memory, duplicate, image_limit, byte_limit or
bridge_priority (a quiet tracking event left out whole so bridge dropout events
fit; the recorder itself writes `retained` before selection).

Luna receives only selected original PNGs, summary and matching compact contexts.
It must say what is visible, reconcile the measured signs with those pixels,
and classify delay or out-of-frame evidence as capture findings. Numeric anomaly
scores never force an actionable assessment. A relevant recognition stage,
visually confirmed real saber, reconciled metadata, supported selected images
and formal coverage remain mandatory. Latency-only event images cannot satisfy
recognition repair evidence, even if an analysis labels them actionable. The
reviewer and all verification requirements remain in force.

## Remaining real-device checks

Measure 1080p recording frame time, camera buffer-pool pressure, process memory,
Stop-time PNG duration/peak memory, and actual wireless delivery. Confirm that
selected pre/peak/post pixels contain the swinging saber. Tune the diagnostic
switches, stride and thresholds using the session distributions; do not change
production scoring to make an event actionable. Synthetic/simulator timing does
not certify 30 fps on a mounted iPhone or real end-to-end latency.

## Changed files

- `PhoneSaberSender/DebugRecordingTriage.swift`: diagnostic thresholds, numeric
  ring, signals/events, frame deduplication, selection, compact contexts/ledger.
- `PhoneSaberSender/DebugVideoRecorder.swift`: bounded pre/event/post buffer
  retention, timing/distributions, background original/overlay PNG export.
- `PhoneSaberSender/FrameProcessor.swift`: diagnostic processing duration only.
- `Tools/phone_saber_metadata_schema.py`: optional format-1 extensions.
- `Tools/phone_saber_triage_codex.py`: validated motion input and Luna instructions.
- `Tools/phone_saber_auto_repair.py`: excludes latency-only repair evidence.
- `PhoneSaberSenderTests/DebugRecordingTriageTests.swift` and
  `PhoneSaberSenderTests/DetectionCoreTests.swift`: diagnostic/lifecycle/selection,
  buffer bounds, compatibility and simulator timing/memory tests.
- `Tools/test_phone_saber_triage_codex.py` and
  `Tools/test_phone_saber_auto_repair.py`: input consistency and conservative gate.
- This document, `Tools/PHONE_SABER_DEBUG_METADATA_SCHEMA.md` and
  `Tools/PHONE_SABER_TRIAGE.md`: format/behavior documentation.
