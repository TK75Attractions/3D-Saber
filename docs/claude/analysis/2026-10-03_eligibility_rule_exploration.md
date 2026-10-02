# Red eligibility rules vs. matte red background objects (offline)

Everything here was run offline on copies in `agent8/`. Nothing in the repo or the inbox was changed.

## Method

- **Detector copies.** I copied `DetectionCore.swift` and `BGRADetection.swift` and added an experimental eligibility gate. The gate only rejects red candidates that would otherwise be eligible, and it runs inside `scoredSaberComponent`, so all post-rules are re-run exactly. Files: `src/`, `patch_exp.py`, `gate_parse.swift`.
- **Baseline parity.** With the gate off, the copy gives 40/40 formal cases and 7/8 background false positives. All 117 winner scores in agent5's `bundles.json` match exactly.
- **Evaluation sets:**
  - the formal `evaluate_fixture` (40 cases);
  - the 8 background-negative images;
  - an extended background set of 36 label/carabiner frames;
  - real-saber ground truth from agent5: 22 red + 25 blue bundle winners, plus the 72881 end-on red saber;
  - 15 red bundle detections that had no label.
- **Unit tests.** `src_static/` is the static BGRA test suite with failures counted instead of aborting. `src_unit/` ports the red-relevant XCTest cases: synthetic bars, `production-video-IMG_5933` axes, and the forensic lengths.
- **Best rule checked separately.** I wrote the best rule as a clean patch (`best/`, `best_rule.diff`). I then ran the real `run_lossless_regression.py` and `run_background_negative_benchmark.py` against it through `PHONESABER_REGRESSION_REPO=agent8/fakerepo`.

**Feature note.** `d` is the dominant-body axial density, in mask samples at 480x640 step 2. Where no body is established, it falls back to points / length. In the patch it is scaled to a 240-sample short side so that tiny synthetic frames keep working.

## Ranked rules (red only, applied to eligible candidates)

Column key:
- **Ext BG**: extended background frames still detected, out of 36 (34 at baseline).
- **Real lost**: real-saber detections lost from the bundle ground truth.
- **Unit**: static-test failures / ported XCTest failures.

| # | Rule | Formal | BG FP /8 | Ext BG /36 | Real lost | 72881 saber | Unit | Closest margins |
|---|---|---|---|---|---|---|---|---|
| 1 | **R7e** clip ≥ .35 OR d ≥ 4.2 OR (d ≥ 3.5 AND purity ≥ .60) | 40 | **0** | 9 | 0 | **wins** | 0 / 0 | positives: 21300 clip .45 (+.10); red_long_core_line_1000 d 4.49 (+.29); red_397 purity .79 (+.19); red_390 d 4.74 (+.54). Background: 662 d 3.91 (−.29); 256 d 3.16 (−.34); ext core-lines clip .26 (−.09) |
| 2 | R6c clip ≥ .5 OR d ≥ 4.0 | 40 | 0 | 9 | 0 | wins | 0 / 0 | red_397 d 4.05 (**+.05**); 21300 d 4.09 (+.09); 662 d 3.91 (**−.09**) |
| 3 | R8 clippedCount ≥ 5 OR d ≥ 4.0 (no fallback) | 40 | 0 | 7 | 0 | wins | 0 / **4** | same density pinch as R6c |
| 4 | R6 clip ≥ .3 OR d ≥ 4.0 | 40 | 1 (2533) | 10 | 0 | wins | 0 / 2 | clip .29 (red_1000) vs .26 (background) |
| 5 | agent5: mean ≥ 241 OR d ≥ 1.9 | 40 | 5 | 32 | 0 | no | 0 / 1 | mean ±2 units, exposure-bound |
| 6 | chroma-aware emitter ≥ .50 (lumaMean in place of max channel) | 40 | 7 | 32 | 0 | no | 0 / 0 | no separation |
| 7 | core ≥ .10 (white-core requirement) | **39** (fails red_390) | 6 | 31 | 0 | no | 16 / 7 | red_390 core .01 |
| 8 | secMean ≥ 130 OR clip ≥ .3 | 38 | 2 | 15 | 3 changed | no | 20 / 15 | daylight saber secMean 82 < label 112 |
| 9 | clip ≥ .5 only | 37 | 0 | 6 | **12** | lost | 20 / 19 | — |

- **Blue** is untouched by every rule: no blue selection changed.
- **R6c/R7e on fixtures:** no fixture selection changed, including the non-manifest fixtures.
- **Unlabeled detections removed by rules 1–4:** 0930/4095 (a foot on the floor), 1001/76899 (an arm) and 1001/18215 (an orange blurred streak). I checked these by eye; they appear to be false positives too.

## Best candidate: R7e

```text
if color == red && isEmitterEligible:          # after compact-red gate
    d = body.density > 0 ? body.density : points / majorLength
    d240 = d * 240 / min(maskWidth, maskHeight)
    isEmitterEligible = clippedWhiteRatio >= 0.35            # clipped white LED core
                     || d240 >= 4.2                           # thick body
                     || (d240 >= 3.5 && meanPurity >= 0.60)   # saturated daylight body
```

The full patch is `agent8/best_rule.diff`. `git apply --check` passes against the production files. On that copy:
- the real formal runner gives 40/40 (23/23 positives, 0/17 false positives);
- the background benchmark gives 0/8 false positives;
- static tests pass, and the ported unit tests pass.

**Follow-up if the patch is ever applied.** `DebugVideoRecorder.swift` reconstructs the eligibility rules for Debug Recording. It would need a matching `redMatteGuard` check, or traces will show false-positive candidates as PASS.

## Evidence and risks

1. **Margins are tiny.** The deciding gap is density 3.91 (blurred label, 662) against 4.05–4.09 (red_397, 21300), which is 0.14 samples ≈ 0.3 px. R7e rescues red_397 with a purity branch and 21300 with clipped .45. That adds two thresholds fitted to about 5 sessions in 3 rooms. This is classic overfitting: R9 (thick-body threshold 4.5) already breaks `red_long_core_line_1000`.
2. **The rule depends on exposure.** With a +8% global gain, the background false positives come back (R7e 7/8, baseline 8/8). White label text, walls and lamps reach value ≥ 245 with low chroma, so `clippedWhiteRatio` and new core-line bridges satisfy the clipped branch. With −8% gain, R7e keeps 5/11 red formal positives against 10/11 at baseline, because LED clipping disappears. The down-scaling test is pessimistic, since real LEDs stay clipped. Either way, the clipped branch is not exposure-invariant.
3. **Density depends on distance.** A saber 1.5–2× farther than in red_390/397 thins below 3.5 and becomes undetectable unless it clips.
4. **Not fixed:**
   - 010049 frames 664–666: label-to-lamp core-line bridges with clip .85–.91.
   - 005850 frames 2541/2543.
   - Thick dim blobs: 010049 658/659 and 005134/22875, at d 6–8.
   The extended background set only drops from 34/36 to 9/36.
5. **Unit-test coupling.** The synthetic tests model a saber as a pure saturated red bar, which is the label profile. Without the points/length fallback and the frame-relative scaling, `testDetectionFindsPrincipalAxisEndpoints` and the 96x64 view-model test fail.

**Recommendation.** Do not ship. At most, run R7e as a shadow-logged flag (compute it and log it, without gating) to collect distributions.

## Real captures needed before any production threshold

1. **Distance sweep of the lit red saber.** Cover 0.5–3 m in at least 3 rooms, including daylight (non-clipping) and night, each with fixed and auto exposure. This gives the density / purity / clipped distributions of true positives, especially the low tail below 4.5.
2. **The same rooms with sabers off.** Include the label, carabiner, basket and other matte red or pink objects, both static and motion-blurred, at the play distance. This gives the background distributions and the static-hotspot rate.
3. **Exposure bracketing.** Log ISO, duration and bias per frame, and step EV ±1 in both of the sets above. This shows whether `clippedWhiteRatio` and density stay separated under auto-exposure.
4. **Skin and orange negatives.** The foot and arm detections above show this class exists.
5. **Per-candidate debug fields:** clippedWhiteRatio, body density, fallback density, meanPurity and the shadow R7e verdict. Without these, real-device traces cannot confirm the offline margins.

**Acceptance bar.** Margins should be at least 25% of each feature's within-class spread across at least 5 rooms and both exposure modes, with 0 lost positives on the distance sweep.
