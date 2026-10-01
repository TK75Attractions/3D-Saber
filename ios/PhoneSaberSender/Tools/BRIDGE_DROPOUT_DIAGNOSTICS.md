# Bridge dropout diagnostics, active color and compact contexts

Scope: Debug Recording image selection and the Mac-side evidence contract only.
Recognition thresholds, HSV, scoring, candidate ranking, eligibility, PCA, robust
endpoint, fallback, UDP, Unity and the repair safety gate's pass conditions are not
changed.

## Why

Automatic diagnosis kept receiving images from the start and end of a recording, where
the saber is not yet or no longer in view, and stopped at `NEEDS MORE EVIDENCE`.
`candidate=0` and `detected=false` only say that no saber was found. They are a
recognition failure only when the same saber was being tracked before and after.

## What is selected now

| Case | Result |
| --- | --- |
| success → short miss → success, same saber | **bridge dropout**; selected as one event |
| absence before the first success or after the last success | never a candidate |
| success → long absence → success | rejected (`gap_too_long`) |
| success → miss → success somewhere else / different length or orientation | rejected (`discontinuous`) |
| success on only one side (recording ended / started in absence) | never a candidate |
| color not selected at Start Recording | its absence is never counted |

Continuity uses the existing tracking-diagnostic geometry rather than one fixed pixel
distance: temporal interval, midpoint displacement and speed (the existing
`endpointSpeedPixelsPerSecond`), constant-velocity prediction residual, length change
and undirected orientation change. Allowances grow with the interval and scale with
the saber's length. See `DebugBridgeThresholds` (diagnostic image selection only); the
values used are stored in every event's `assessment.thresholds`.

## One event, three frames, one example

Each event stores `before_success`, `dropout` and `after_success` original lossless PNGs
with the same event ID, plus an `annotated_dropout` copy of the dropout frame with the
interpolated expected position drawn on it. The originals are always kept; the
annotated image is a viewing aid and not ground truth.

- The bundle marks the images with `bridgeEventID`, `evidenceUnit` and
  `auxiliary`. Event images come first and are included whole or not at all.
- **Image budget (12) between bridge and tracking evidence.** A tracking-instability
  event whose score is at or above the diagnostic event threshold
  (`DebugMotionThresholds.eventScore`, 1.0; a candidate switch alone scores 2.0) is never
  crowded out: it always keeps a contiguous window around its peak (at least five
  frames; eight beside one bridge event, all eleven when alone), and bridge events get
  the rest (one event beside it, two when tracking is quiet). A quiet tracking event
  (score below the threshold) yields to bridge events as a whole, and the ledger records
  `bridge_priority`, which preflight accepts. A trimmed window is reported in
  `bridgeDropoutSummary.trackingWindow` (`selected`, `available`).
- `repair_gate` counts one bridge event as ONE independent example (three PNGs and
  three frame IDs of one event do not satisfy "two distinct examples"), and the
  annotated image can never be the evidence for a color or a finding. All other gate
  conditions are unchanged. `independent_visual_examples` claimed by the model is
  checked against these event units.
- Preflight requires before_success detected, dropout missed and after_success detected
  in time order (`bridgeEvidenceIncomplete` otherwise) before any model call.
- `[AUTO_REPAIR][BRIDGE_SUMMARY]` prints one row per event.

## Active color

Start Recording has a RED / BLUE / BOTH picker (default BOTH, so existing behavior is
unchanged). Recognition and UDP still run for both colors. For a color that is not
selected, diagnosis ignores candidate=0, detected=false, dropouts, motion events,
tracking ranking and anomaly captures. Summaries mark it `excludedFromDiagnosis`, and
frame contexts carry `{}` for it. The choice is stored as `activeColors`.

## Compact contexts

The 32 KiB consumer limit is unchanged. Contexts shrank because:

- JSON is written compactly (pretty-printing was ~45% of the file size);
- only the selected frame keeps up to three candidate traces, the selected-candidate
  endpoint pipeline and tracking; neighbouring frames keep the leading candidate's
  failed rules with value and threshold;
- the color outside the diagnosis is `{}`;
- a final guard drops neighbouring frames only if a context still exceeds 24 KiB, and
  records `compaction` in the context.

Temporal mapping (`imageMapping`, `bridgeEvent`), the failed rule with actual value and
threshold, the endpoint pipeline and the tracking timeline stay on the selected frame.

## Candidate geometry (separating candidate-selection failures)

Goal: after a capture, tell apart
**A** a candidate that matches the previous winner stays eligible but another, distant
candidate narrowly wins; **B** the correct candidate was never generated or is
ineligible; **C** the winner is right but its PCA/endpoints break.

Recorded per frame and active color (in memory with the frame, written only into the
triage snapshot of retained event frames, never into the streamed full metadata, so a
long recording does not approach its size limit): every eligible candidate up to 12 and
up to 6 ineligible ones, each with `listIndex`, `eligible`, `eligibleRank` (1 = winner),
`sourceType`, `finalScore`, full `scoreBreakdown`, `centroid` (`centroidSource`:
`trace` or `bboxCenter`), `bbox`, `componentArea`, `rawPCASpan`, `rawPCAEndpoints`,
`finalOutputEndpoints`, `rejectionReasons`.

Contexts carry `candidateGeometry` on the selected frame (all recorded candidates) and,
reduced to the leading three eligible, on neighbours. Omission is always explicit:
`totalCandidateCount`, `eligibleCandidateCount` (= `savedEligibleCount` +
`eligibleOmittedCount`, checked on the Mac and against the frame's own
`eligibleCandidateCount`), `savedIneligibleCount`, `ineligibleOmittedCount`,
`candidatesTruncated`. When the 24 KiB budget forces more reduction, steps are listed in
`compaction` (neighbour geometry, then lower-ranked score breakdowns marked
`scoreBreakdownReduced`, then ineligible/eligible counts, then neighbour frames); the
selected frame's winner and leading candidates are never dropped.

Identity does not rely on list order: every candidate has `matchToPreviousWinner`
against the previous frame's winner (`previousWinner` is included): centroid distance
(raw and divided by the winner's span), bbox IoU, area ratio, span ratio and undirected
orientation difference.

`[AUTO_REPAIR][CANDIDATE_AUDIT]` and the prompt carry deterministic hints from this data
(`A`, `B` with `bCause` notGenerated / ineligible / noCandidateMatchesPreviousWinner /
unknownTruncated, `C`, `none`). They are hints with documented heuristic thresholds
(`AUDIT_*` in `phone_saber_tracking_diagnostics.py`), never conclusions and never an
input to the repair gate. Truncation turns a would-be B into `unknown`.

## Tracking-instability ranking

A bare detected true↔false toggle no longer ranks a tracking-instability event (it is
the start/end of an absence, reported through bridge events). Candidate switches, path
changes, geometry discontinuities and score collapses still rank exactly as before; the
toggle only added 2.0 and could outrank a real candidate switch (also 2.0) at the end of
a recording. The per-frame `tracking` measurements recorded in metadata are unchanged.

## Not included

- Allowing a repair from bridge evidence when visual evidence is weak.
- Sending a short clip to the Mac and replaying the detector there.
- A color that never succeeds cannot yield a bridge event; a saber that is visibly in
  view but never detected still needs a recording where it is detected at least
  before and after.

## Memory

Bridge originals share the existing retained-BGRA budget, raised from 192 MiB to
256 MiB (Debug Recording only) so they fit beside the 11-frame tracking capture. At most
two events are kept; longer losses replace shorter ones. Pending candidates keep copies
only until the next success decides them.
