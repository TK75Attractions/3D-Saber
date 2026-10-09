# iOS frame allocation measurement (worker B, 2026-10-09)

## Reproduce on Mac

From the Git root:

```sh
python3 PhoneSaber/tools/ios-hot-path/run.py --revision origin/main --frames 240
python3 PhoneSaber/tools/ios-hot-path/run.py --frames 240
```

The harness compiles with Swift `-O -whole-module-optimization`, runs 30 warmup
frames per stage, then 240 frames at 60 fps. The test-only C malloc logger counts
successful allocation/reallocation events, including Swift/Foundation/dispatch
and Network allocations on all process threads. `getrusage` measures process
user + system CPU, excluding sleep time. Neither counter is linked into the app.
Loopback receivers use ephemeral ports and assert unchanged coordinate bodies.
P2P sends use the real sender, with a loopback bridge answering real pings.

FrameProcessor submit/drain/result/expiry methods and CameraUIPendingState are
extracted unchanged into a Mac scaffold. Only the detector is replaced with
fixed red/blue endpoints. The CMSampleBuffer is constructed once and reused;
camera capture, BGRA access/detection, recordings, SwiftUI rendering, radio
latency and device energy are outside this measurement. Release diagnostics are
off. No signposts or production counters were added.

## Results

One matched 240-frame run on this Mac, with other workers building concurrently:

| Stage | malloc/frame before → after | process CPU µs/frame before → after |
| --- | ---: | ---: |
| Result generation + expiry | 14.07 → 13.04 | 72.97 → 87.70 |
| Frame mailbox + results (detector stub) | 13.03 → 12.01 | 99.95 → 87.51 |
| UI pending | 0.00 → 0.00 | 15.27 → 10.48 |
| Payload + ASCII conversion | 0.00 → 0.00 | 17.36 → 14.27 |
| P2P encoding | 7.00 → 6.00 | 36.06 → 31.88 |
| LAN probe read | 0.00 → 0.00 | 14.32 → 7.91 |
| LAN route + NW send | 32.77 → 32.63 | 243.68 → 148.91 |
| P2P route + NW send | 49.74 → 48.60 | 258.08 → 144.34 |

The repeatable result is one fewer allocation per frame in result expiry and
one fewer per P2P packet. CPU values vary substantially with concurrent builds;
these samples do **not** establish a device CPU or energy improvement. Short
ASCII coordinate strings already fit inline Data storage; their conversion and
UI pending state did not allocate, so those paths were left unchanged.

## Changes and invariants

- Expiry's compactMap uses lazy iteration, avoiding a temporary array. The
  existing iteration order, arithmetic and minimum comparison are preserved.
- P2P allocates header + body once and writes the same header/body bytes.
- The detection and tracking state transitions, predictions, hold duration,
  ranking, floating-point operations, routing, callbacks and ports are unchanged.
- XCTest LAN discovery is isolated from real Bonjour advertisements. The real
  app still discovers automatically. Unit tests use applyBonjourForTesting;
  the live-network P2P integration test remains independently opt-in.

## Verification notes

The differential encoder test checks 4,626 combinations (every body size
0...256, both colors, all three message kinds and three session/sequence pairs)
against the original encoder. Existing malformed-payload tests remain intact.

The first build caught a missing explicit `self` in the lazy closure (fixed).
A later XCTest failed `testFallbackWithoutLANCountsTheDroppedCoordinate` because
an actual `Phone Saber Unity A` advertisement changed its initially empty LAN
route. XCTest discovery isolation addresses that interference without changing
assertions or production behavior. The initial Tools run timed out compiling
`test_phone_saber_tracking_e2e` after 180 s under load; isolated reruns passed
17/17 twice (79.697 s and 61.614 s).

Swift/C++ parity: 269 PNGs, 27 synthetic cases and 541 frame transitions,
zero mismatches, formal expectations 40/40. C++ core tests and four Python tests
passed. Unity batch tests and Android emulator tests were not run (worker B
restriction); no Unity or Android production files were changed.

Final full verification: `.verify-logs/phone-saber/20261009-173156` PASS:
iOS XCTest 275 executed (274 pass, one opt-in skip), Detection PASS, lossless
40/40, Tools 402 tests (one opt-in skip), iOS Release PASS, diff check PASS.
Unity stages NOT_RUN. No gates or expectations were relaxed.
