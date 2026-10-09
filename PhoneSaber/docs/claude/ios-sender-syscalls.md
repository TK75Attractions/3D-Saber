# iOS sender dispatch/syscall measurement (worker B, 2026-10-09)

## Reproduce

After merging `opt/ios-frame-alloc` (which adds the shared Mac harness):

```sh
python3 PhoneSaber/tools/ios_sender_syscalls.py --revision origin/main --frames 240
python3 PhoneSaber/tools/ios_sender_syscalls.py --frames 240
```

For independent worktrees, pass `--harness-root` pointing to that branch's
`PhoneSaber/tools/ios-hot-path` directory. Only temporary copies of the sender
source are instrumented. All production counters/diagnostics are unchanged.

The test reads macOS TASK_EVENTS_INFO process-wide Unix and Mach syscall
counters. A calibration performs 100 getpgid calls and requires at least 100
observed Unix syscalls. Sender queue.async calls are counted in the temporary
source, without signposts. Network.framework's internal dispatches and kernel
calls are included in process syscall totals but cannot be individually
attributed to coordinates. Receivers, semaphore waits, pacing sleep, pings and
counter reads also contribute. Idle control uses the same active receivers and
P2P liveness traffic. These are process totals, not a count of just sendmsg.

## 240-frame measurements at 60 fps

| Stage | explicit sender async/frame | Unix syscalls/frame | Mach syscalls/frame | malloc/frame | process CPU us/frame |
| --- | ---: | ---: | ---: | ---: | ---: |
| Idle control before | 0.06 | 2.23 | 0.37 | 2.59 | 30.47 |
| Idle control after | 0.07 | 2.28 | 0.38 | 2.75 | 36.61 |
| LAN before | 2.07 | 14.81 | 3.21 | 32.75 | 206.77 |
| LAN after | 1.07 | 14.20 | 3.21 | 30.75 | 205.28 |
| P2P before | 2.07 | 13.00 | 3.25 | 49.74 | 215.85 |
| P2P after | 1.11 | 11.75 | 3.36 | 48.63 | 189.27 |

One required asynchronous hop remains **before NWConnection.send** on each
coordinate: detection/CoordinateDelivery to the sender's serialized queue.
That hop cannot safely be removed without changing synchronization and the
latest-pending policy. On completion, Network.framework already invokes the
callback on its start(queue:) queue. Removing the extra queue.async saves one
explicit dispatch per coordinate, and two measured malloc events for LAN /
about one for P2P. Liveness callbacks account for the fractional background
hops. Kernel syscall counts and CPU vary with scheduling/load; only the removal
of one explicit sender dispatch is a structural guarantee.

## Safety and coverage

- Completion state/generation/send-ID checks, latest-pending selection,
  payload generation/bytes, routing, watchdog durations and diagnostics remain
  unchanged. Recognition and all floating-point operations are untouched.
- Sender finish methods assert their queue via dispatchPrecondition. The real
  Mac LAN/P2P harness exercised these assertions through Network callbacks.
- Injected send hooks can complete on any thread; they retain the original
  asynchronous return and strong sender lifetime after accepting a callback.
  XCTest additionally checks immediate and background-queue hook completions.
- State/path/receive callback dispatches are retained; the measured coordinate
  change is confined to Network send completions.
- This independent branch includes the same XCTest-only Bonjour isolation as
  opt/ios-frame-alloc, so the live Unity advertiser cannot alter synthetic routes.
  The identical CameraViewModel patch merges without changing app discovery.

Actual iPhone radio latency and energy are not measured. Unity batch and Android
emulator tests are not run (worker B restriction). No Unity or Android source
changes are included.

The counter tool also counts delayed DispatchWorkItem enqueues. A 60-frame
smoke run confirmed 1.00 delayed work/frame on both coordinate paths, with
1.07 explicit async/frame after this change (including P2P ping callbacks).
This watchdog scheduling is retained here for the separate energy task.

Swift/C++ parity: 269 PNGs + 27 synthetic cases + 541 frame transitions,
0 mismatches; formal expectations 40/40. C++ core + four Python tests PASS.

Final full verify `.verify-logs/phone-saber/20261009-174858`: PASS. XCTest
276 executed (275 pass, one opt-in skip), Detection PASS, lossless 40/40,
Tools 401 (one opt-in skip), iOS Release PASS, diff check PASS. Unity NOT_RUN.
