# Exactness boundary

This is a translation of the checked-in iPhone production path, not a new
recognizer. Swift production, tunable defaults, UDP format, and formal fixture
expectations are unchanged. Debug recording, emitter traces, performance clocks,
and diagnostic counters are deliberately absent from the portable core.

The Mac parity runner reads the current Swift sources on every run. It uses the
existing VideoDetectionDiagnostic runner with an extra test-only bit signature
in a temporary copy. It also extracts the actual FrameProcessor state transition
and helpers into a temporary Swift adapter. It never changes production Swift.

Exactness risks and their handling:

| Location | Risk / preserved behavior |
| --- | --- |
| HSV conversion (`pipeline.cpp`) | Binary64 doubles, nested max/min, hue remainder via `fmod` (Swift truncatingRemainder), and multiply/divide order are preserved. Gate comparisons are inclusive exactly where Swift is inclusive. Brightness/chroma derive from integer channels. |
| Mask lattice / input | Ceil-divided sample dimensions, origin (0,0), default step=2, explicit byte row stride and RGBA/BGRA channel indices. Alpha ignored. RED/BLUE support reads **original** pixels, not sampled HSV or resized pixels. Different orientation, exposure, color conversion, or gamma input changes recognition; JNI must not silently transform these. |
| Morphology / components | Diamond structuring elements, border erasure, row-major seeds, FIFO 8-neighbor BFS with y then x iteration match Swift. Points retain BFS order for every sum. Sparse fallback uses the count of all cleaned component pixels, including components that fail scoring. |
| Sets / deduplication | Line proposal samples are deduplicated and then scored in sorted row-major index order, matching Swift's explicit sort. `std::set` membership does not introduce unordered computations. RED/BLUE dilate1 and clipped-white sets are used only for uniqueness/counts. No unordered maps or sets are used. |
| PCA / robust body | Mean x/y and covariance sums, axis products, extent, histogram bin assignments, `pow(x,2)`, variance, and endpoint projections retain operation order. `std::round` means nearest with ties away from zero, as Swift `.rounded()`; `ceil` implements `.rounded(.up)`. Principal-axis endpoints use the first matching min/max **point**, while raw endpoints use rounded projections. Robust interval endpoints are allowed outside the image, as in Swift. |
| Equal elements | Stable sorting by descending score/votes preserves Swift sort stability. Group/trusted maxima keep the first equal maximum. Deduplication uses the first existing candidate. Eligibility updates are sequential and snapshots (`connected`, `complete`) are taken at the same points as Swift. |
| Score accumulation | `ScoreBreakdown::total` preserves the left-associated addends, including proposal penalty/radiance first. Penalty assignment versus subtraction, recalculation, and comparisons retain Swift order. BLUE preferences precede final subsegment suppression. RED warmNoDeepRed and BLUE blueNoDeepSupport run only on still-eligible candidates **after** ranking and never change scores/order. BLUE rejects exactly when its original dilate1 domain has no pixel with B >= 180, R*100 < 40*B and G*100 < 65*B; selection takes the first survivor. |
| Integer arithmetic | Image indices/counts fit int for accepted buffers (dimensions at most 32768); count multipliers in the warm gate/morphology fallback use int64_t to match Swift Int64 and avoid intermediate C++ overflow. Invalid pointers, undersized strides/storage, and unsupported oversized images return empty analysis. Invalid inputs are outside iPhone's valid CVPixelBuffer domain. CLI rejects unsupported PNG encodings instead of converting them. |
| Floating-point optimization | Make/CMake explicitly use `-ffp-contract=off`. Do not use fast-math, reassociation, reciprocal approximations, SIMD reductions with a different order, float32, or an implicit fused multiply-add. Optimization must preserve binary64 intermediate rounding. |
| Math libraries / CPU | `atan2`, `sin`, `cos`, `hypot`, `sqrt`, `log2`, `pow` depend on the compiler/libm. Bit parity is verified on arm64 Mac with Apple clang 21 / Swift 6.4. Android bionic/NDK libm or a different CPU/toolchain can differ by ULPs, and histogram/rounding/gate boundaries can amplify that difference. Android bit parity is **not yet established**; rerun the same corpus through a future JNI/NDK harness. Mac parity alone cannot prove cross-libm equivalence. |
| Temporal state (`frame_processor.cpp`) | Only endpoint ordering is stabilized; no positional smoothing exists. Direct/reverse costs use `hypot` and strict `<`. Prediction uses the last two real detections, lasts exactly three processed missing frames, and clamps integer extrapolated points without overwriting history. It happens before the 180ms held test, as in Swift. An independently scheduled expiry may clear history before a later frame; the caller must reproduce that scheduling. |
| Lifecycle / clocks | Core takes monotonic processing-start time and per-color send-time Unix epoch from the caller. `reset`, resolution changes, `next_expiry` and `expire` cover production state. One-slot latest-frame mailbox, camera orientation, running/generation rejection and expiry scheduling are platform responsibilities for Phase 2; counting dropped frames as processed missing frames would change prediction. |
| Coordinate mapping / UDP | Normalization uses source/output dimensions minus one, the exact multiply/divide/mirror order, and ties-away rounding. Default 1920×1080 with no mirrors. RED/BLUE ports 5005/5006. Held/expired/absent results have no text and must not be sent. Timestamp formatting uses classic locale and six fractional decimal digits, matching Swift's `String(format: "%.6f", ...)` for finite Unix epochs under the normal nearest rounding environment. Exotic locales, non-finite epochs or a changed FP rounding mode are outside the tested sender contract. |
| PNG / parity serialization | CLI-only zlib decoder preserves 8-bit RGB/RGBA bytes, including alpha; CRC, shape, encoding, filters and inflate size are checked. All tested PNG bytes agree with ffmpeg's existing lossless input path. IEEE-754 values are also serialized as uint64 decimal strings using memcpy/Double.bitPattern; JSON number text alone could obscure signed zero/rounding. Diagnostics-only timing and counters are excluded, never rounded into apparent agreement. |
| Future changes | The temporary adapter fails if its insertion marker disappears; reference fields/source changes must trigger an intentional port review. Existing fixture tolerances are used only for the formal expected-value checks. Cross-language comparisons never use epsilon or relaxed fixture expectations. |

Verification includes every candidate and stored production scalar, not just the
winner. Hardware recognition and end-to-end Wi-Fi latency remain Phase 2 work;
local resize or reported FPS is not evidence of reduced wireless latency.

## Android (bionic libm) との差(2026-10-06 に確認)
NDK 30 の arm64 build(`-ffp-contract=off`)を Android 17 / API 37 の arm64 emulator で実行し、
Mac の C++ core(= Swift と bit 一致)と 237 枚の PNG(formal fixture + inbox の元画像)で比べた。
- 最終出力(赤・青の selected 端点)は 237/237 枚で一致。
- 候補の内部値は 184 枚で最終 bit(1 ulp)だけ異なる(`atan2` / `sin` / `cos` / `hypot` / `log2` / `pow` の丸めが
  Apple の libm と bionic で違うため)。非 selected 候補の形が変わった例が 2 件。
- Apple の libm の結果を Android で bit 単位に再現する方法はないので、受け入れる。点数がほぼ同点の場面でだけ、
  まれに選択が iPhone と変わる可能性がある。
