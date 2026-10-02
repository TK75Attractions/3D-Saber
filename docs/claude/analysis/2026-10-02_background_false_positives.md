# Background red false positives: label strip and carabiner

**Method.** I compiled the unmodified production `DetectionCore.swift` and `BGRADetection.swift` into a scratch CLI (`agent5/harness/analyze`). It runs on the bundle and fixture PNGs with the default thresholds (145/25/30) and step 2. It reproduces the device traces exactly (frame 2537: 60.815 vs 60.609) and matches all 40 manifest expectations. I checked every bundle image by eye. Samples: 35 background red winners, 29 real lit red winners (18 from bundles, 11 positive fixtures) and 37 real blue winners.

## 1. Feature comparison (winners, min–max)

| group | n | peak | mean | high | purity | clipped | core | density | area | span | score |
|---|---|---|---|---|---|---|---|---|---|---|---|
| BG label/carabiner (10-02 ×3) | 31 | 235–255 | 202–**240** | .38–.98 | .34–.79 | 0–.86 | 0–.79 | 0–3.6 (27/31 ≤1.63) | 48–716 | 12–218 | 42–68 (bridges 111) |
| BG other (label 24174, basket, shelf) | 4 | 232–252 | 200–230 | .37–.95 | .46–.67 | 0–.07 | 0–.30 | .7–4.1 | 80–720 | 26–55 | 41–62 |
| real red (bundles) | 18 | 255 | 213–253 | .48–1 | .33–.63 | 0–1 | .28–1 | 2.05–7.4 | 404–3048 | 42–166 | 49–130 |
| real red (fixtures A/F/G) | 11 | 253–255 | 224–251 | .71–1 | .14–.79 | 0–1 | .01–1 | .55–3.7 | 48–908 | 21–113 | 51–117 |
| real blue (bundles+fixtures) | 37 | 255 | 202–251 | .35–1 | .26–.92 | 0–1 | .20–.97 | — | 312–4536 | 33–232 | 44–197 |

**How well each feature separates (AUC, real red vs background):**
- mean value: 0.92. All background ≤240; 24/29 real ≥241.
- density: 0.90
- coreSupport: 0.89
- area: 0.86
- radiance: 0.85
- clipped white: 0.81
- peak: 0.65 (useless, because both saturate at 255)
- purity: 0.29 and emitterTexture: 0.28. Both are inverted: the matte objects look purer and more "textured" than the LEDs.

**No feature separates them cleanly.** Fixture `device_normal_red_390` (a distant red saber in daylight) has core 0.01, clipped 0, mean 230 and purity 0.71. That is the same profile as the label. The best two-feature rule I found was "mean ≥241 OR density ≥1.9". It rejects 30/35 background winners and keeps 29/29 real ones, but its margins are 1 value unit and 0.03 density. With n=64 from 2–3 rooms, that is overfit, and the mean value depends on auto-exposure. Not production-ready.

### Why the objects pass (`DetectionCore.swift` ~698–715)
- **value = max(R,G,B), so for a red object it is just R.** `isEmitterEligible = peak≥218 && hasEmitterCore && emitterScore≥0.42`. emitterScore is made only of R-saturation terms plus purity.
  - Label (2533): 0.32 + 0.145 + 0.143 + 0.070 = **0.678**
  - Carabiner (2537): **0.733**
  - Both are well above the 0.42 threshold.
- **No white-core evidence is required.** `hasEmitterCore` is satisfied by highRatio ≥0.08 alone, i.e. R≥220 on 8% of pixels.
- **The compact-red gate doesn't apply.** Neither object counts as compact: the label has aspect ~10 and 49–51 points, more than the 38-point minimum. The carabiner comes through `color-sparse-raw`, where the minimum area is overridden to 12. Even if the gate applied (peak≥230, high≥.5, purity≥.5), both would pass.
- **Red-specific post-rules don't touch them.** Those rules (weak-bridge, short-subsegment, subsegment) only target core-line/core-halo slices of a connected body.
- **Short candidates lose their white-core score.** The white-core terms (clippedWhite up to ~74 pts, coreSupport, longitudinal core coverage, radiance) are multiplied by `bladeLengthSupport`. That is ≈0.03 for a 40 px strip, so the score is dominated by R-saturation, where a matte red object scores about the same as an LED (~60).
- **The ping-pong is a near-tie.** Both objects are eligible in the same frame: 2537 is 60.82 vs 60.61, and 255 is 63.6 vs 61.2.
- **The label is a standing fallback.** It is eligible in 13/14 real-saber frames from 10-01 (score 42–64) and only loses because the saber scores higher. In 75607 the margin is 4.4. In 72881 an end-on real red saber (eligible, 40.2) **loses to the label (55.1)**.
- **Eligibility flips with exposure.** In 013205 frame 282 the label's peak is 232 (emitterScore ≈0.40) and it becomes ineligible.

## 2. Corpus gap and recommended negative fixtures

The corpus has no matte red hard negative. Its E-class red negatives are:
- 0922_134817 frames 103/131/136/139: red candidates have peak ≤229 and highRatio ≤0.03, so they never get near emitterScore.
- red_hard_negative 83/90/348: white-clipped lamp lines with purity ~0.15.

None of them exercises the "saturated R, no white core" path.

Recommended new fixtures (all are under `~/Library/Application Support/PhoneSaber/diagnostics-inbox/phone_saber_triage_phonesaber_<session>/images/`). I checked each one by eye: no lit red saber is visible. Expected result is "not detected" for every row.

| session | file (frame) | content | currently | expected |
|---|---|---|---|---|
| 20261002_005850_489 | image_05_…_onset_2537.png (sha a3835b47…) | carabiner beats label 60.82/60.61 | RED detected | RED+BLUE not detected |
| 20261002_005850_489 | image_01_…_before_2533.png (1192f7ba…) | label | RED 60.9 | RED not detected |
| 20261002_013205_087 | image_06_…_peak_256.png (9712bb0a…) | label with white text, core .57 (hardest) | RED 66.5 | RED not detected |
| 20261002_013205_087 | image_05_…_onset_255.png (5c31a5a8…) | carabiner vs label | RED 63.6 | RED not detected |
| 20261002_010049_190 | image_07_…_after_662.png (fea4f357…) | blurred carabiner | RED 62.8 | RED not detected |
| 20261001_003921_526 | image_02_…_post_24174.png (f3cbf25e…) | label, unlit sabers, other day | RED 61.8 | RED+BLUE not detected |
| 20260929_152331_131 | image_02_blue_dropout_false_96.png (a9c811bd…) | blurred red basket, other room | RED 50.5 | RED not detected |
| 20261002_013205_087 | image_12_red_dropout_false_282.png (37bdd001…) | control: label + 2 carabiners, already rejected | passes | RED not detected |

- **Optional, very hard:** 010049 `image_10_…_after_665.png` (sha 35807bac…), a core-line bridge from the label with clipped .85 and score 111. It probably needs separate bridge logic.
- **Don't use 002509 `72881` as a negative:** a real end-on red saber is in that frame.
- **Adding these would break the current 40/40 gate,** because 7 of the 8 are detected today. A "pending/known-failing" class is the lead's call.

## 3. Cheapest diagnostics for the next capture

1. **Remove or cover the objects (most decisive).** Record ~10 s with no saber in view and the camera in its play position, once with the label and carabiners present and once with them covered or removed. Then repeat a still-held saber near them. Every RED output in the no-saber segment is a false positive. If the ping-pong disappears when they're covered, background eligibility is confirmed.
2. **Build a static-hotspot map from the per-frame eligible-candidate geometry already recorded.** Grid the centroids per session. A cell that is eligible in a large share of frames regardless of motion is background: the label sat at the identical bbox for 10 frames.
3. **Add three things to the candidate trace:**
   - per-candidate emitterScore and each of its terms, with the margin to 0.42;
   - `bladeLengthSupport`;
   - G/B channel stats (brightCore fraction, mean second channel).

   Today the per-candidate trace leaves out localContrast, emitterTexture, coreSupport and emitterScore, and it is truncated.
4. **Log exposure (ISO, duration, bias) per frame.** Label eligibility tracks peak 232 vs 255.
5. **Replay offline with the harness above.** Running unmodified production code on the original PNGs gave exact parity.

**Uncertainty.** n is small and comes from 2–3 rooms under warm indoor light. The mean-value separation is exposure-dependent. Some bundle ground truth (blurred close-ups 69683/69684) is ambiguous and was excluded.
