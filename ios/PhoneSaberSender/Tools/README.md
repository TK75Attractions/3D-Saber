# PhoneSaberSender evaluation tools

These tools read saved lossless fixtures and Debug Recording metadata. They do
not alter or re-run detection for session metadata.

The JSON contract and its legacy rules are documented in
[`PHONE_SABER_DEBUG_METADATA_SCHEMA.md`](PHONE_SABER_DEBUG_METADATA_SCHEMA.md).
Validate one or more sessions before analysis when you want a field-by-field
report:

```bash
ios/PhoneSaberSender/Tools/validate_phone_saber_metadata.py \
  /path/to/baseline_metadata.json /path/to/experimental_metadata.json
```

An absent or malformed legacy field is reported as unknown and does not reject
the file. Add `--strict` to return a nonzero status for those warnings.

`run_lossless_regression.py` compiles the checked-in Swift detector and checks
each color-specific contract in `lossless_regression_manifest.json`: expected
detection, selected candidate type, endpoint distance (allowing endpoint order
to reverse), and any candidate types that must remain rejected. The summary
includes per-class fixture, positive, negative, pass, and failure counts.
Manifest paths are relative to `PhoneSaberSenderTests/Fixtures`; SHA-256 values
pin the copied PNG bytes. The regression corpus contains 40 color-specific
cases across 35 images, including byte-preserved device captures. No external
Downloads path is required.

Failure classes:

- **A:** normal RED/BLUE and diffuser positives, including frames 0220/0600
- **B:** sparse BLUE point-LED cases
- **C:** BLUE candidate-local handling when a frame contains a rejected line
- **D:** BLUE raw-tail core-line rejection while preserving valid local output
- **E:** no-blade, background, and hard-negative scenes
- **F:** compact RED component recovery
- **G:** RED long core-line selection and bounded endpoints

`phone_saber_selection_replay.py` is the read-only evidence tool for the planned
candidate temporal-consistency fix. It reads the complete eligible candidate
geometry saved in triage frame contexts (truncated lists are skipped) and does
not change recognition:

```bash
# Score gap R - M where the recorded winner R breaks continuity while an
# eligible M continues the previous winner (switch-outs; returns listed apart)
ios/PhoneSaberSender/Tools/phone_saber_selection_replay.py /path/to/bundle
# Which frames a margin / correspondence / expiry policy would change, and how
# many >=100 px output jumps remain; comma-separated values sweep
ios/PhoneSaberSender/Tools/phone_saber_selection_replay.py /path/to/bundle \
  --mode replay --margin 0.5,1,2,5 --hold 1,3
```

Policy values are inputs to compare, never production defaults: a production
threshold must cite the measured distribution. Triage bundles hold event
windows only, so counts describe those windows.

`phone_saber_session_report.py` is the free, local, one-page summary of one
triage bundle (no Codex or other model call; read-only on the bundle). It
combines the session counts, the memory headroom verdict, the tracking event and
`bridge_priority` ledger, bridge events, CASE A/B/C hint counts, a small
selection-replay sweep (labelled as evidence), `tracking_preflight` and the
CLAUDE.md §5 checklist with the original PNGs to open first. Fields missing from
older bundles print `n/a`. The receiver writes it automatically beside each new
upload as `<inbox>/phone_saber_triage_<session>.report.md`.

```bash
ios/PhoneSaberSender/Tools/phone_saber_session_report.py /path/to/bundle
ios/PhoneSaberSender/Tools/phone_saber_session_report.py /path/to/bundle --output /tmp/report.md
ios/PhoneSaberSender/Tools/phone_saber_session_report.py /path/to/bundle --json
```

`phone_saber_hotspots.py` is the read-only static-hotspot map for background
false positives (matte red labels, carabiners). It clusters every recorded
candidate position per color by centroid distance / bbox IoU and reports, per
cluster, the representative bbox/centroid, frames present, eligible/winning
fractions, max/median `finalScore`, source types and the selected images that
show it. It uses `candidateGeometry` when present and otherwise falls back to the
winner only (`selectedCandidate`, then the detected non-predicted `endpoint`), so
on older bundles an eligible object that lost is invisible. `likelyBackground` is
a hint, never a gate input: present in ≥5 frames spanning ≥0.25 s, p90 centroid
jitter ≤2% of the image diagonal, stable bbox, eligible or winning, and present in
≥60% of that color's frames with data inside its span. A real saber held still
can match too, so confirm on the original PNG. The session report shows it as
「静的ホットスポット(背景誤検出の候補)」.

```bash
ios/PhoneSaberSender/Tools/phone_saber_hotspots.py /path/to/bundle [/path/to/bundle2 ...]
ios/PhoneSaberSender/Tools/phone_saber_hotspots.py /path/to/bundle --json
```

The session report section 「背景誤検出の証拠(emitter / shadow R7e / 露出)」
(`phone_saber_background_evidence.py`; `backgroundEvidence` in `--json`) reads the
selected-frame evidence recorded since 31ad94c: full decision-trace
`emitterDiagnostics`, the compact geometry `emitter`, and frame `camera`. For each
selected-frame winner (`selectedCandidateIndex`) and each `likelyBackground`
cluster (joined by `static_hotspots(include_members=True)` on frame and candidate
index) it shows `emitterScore` and its margin to 0.42, the dominant terms,
`hasEmitterCore` and its three inputs (full evidence only), and the shadow R7e
verdict with its four margins (recomputed from the documented thresholds for the
compact subset, marked "computed"). The key tally counts winners whose recorded
`shadowR7eEligible` is false — R7e would have changed the output — split by
whether the winner's cluster is `likelyBackground`. Exposure shows per-bundle
ISO / exposure time / bias ranges and, per flagged cluster, median ISO, exposure
and peak value for eligible vs ineligible (and R7e keep vs reject) frames; the
peak is the trace value, or inverted from `peakTerm` when that is not clamped.
All of it is evidence, not ground truth: R7e is `applied: false` and never read
by production. The subsection 「shadow PF22 tally」 does the same for the second
shadow rule PF22 (red `meanColorPurity >= 0.22 OR clippedWhiteRatio >= 0.35`,
`applied: false`): red winners and eligible red candidates it would reject, and
what it would do to the selected-frame red candidateSwitch / ≥100px jump events
(`noDetection` only when every eligible candidate of the frame is known and
rejected; `winnerChanges`, `unchanged`, `unknown`), with the R7e outcome beside
it. Where a bundle predates the recorded verdict, PF22 is recomputed from the
decision-trace / selectedCandidate `meanColorPurity` and `clippedWhiteRatio`.
Across bundles:

```bash
ios/PhoneSaberSender/Tools/phone_saber_pf22_check.py /path/to/bundle [/path/to/bundle2 ...] [--json]
```

Bundles without the fields print `n/a`; the Swift E2E harness
writes neither field (no radiance map, no Exif), so its test adds them to a copy
of a real recorder bundle and checks the strict input contract still passes.

`phone_saber_segments.py` reads the operator segment labels set on the iPhone
during Debug Recording (区間ラベル: `sabersVisible` / `noSaber` /
`noSaberCovered` / `unlabeled`) and prints frames and per-color detection rates per
label. A detection under `noSaber` or `noSaberCovered` is a false positive, so
comparing those two rows shows whether covering the red background objects
removed the RED false positives. See `PHONE_SABER_DEBUG_METADATA_SCHEMA.md`.
The session report shows the same counts in its 「区間ラベル(ground truth)」
section (near the top), a one-line evidence verdict comparing the `noSaber` and
`noSaberCovered` RED false-positive rates (`n/a` when either segment has fewer
than 30 frames), the label of every selected image (images in `noSaber` /
`noSaberCovered` are flagged 背景のみの区間: such tracking/bridge events are
background false positives, not saber instability) and CASE hints split by label
(`caseHintCountsBySegment`). `--json` carries it under `segments`.

```bash
ios/PhoneSaberSender/Tools/phone_saber_segments.py /path/to/bundle
ios/PhoneSaberSender/Tools/phone_saber_segments.py /path/to/session_metadata.json --json
```

Run the lossless fixture suite from the repository root:

```bash
ios/PhoneSaberSender/Tools/run_lossless_regression.py
```

To repeat the automatic repair success-path E2E with real Codex CLI calls, run
the isolated harness from a clean, synchronized `main`:

```bash
python3 ios/PhoneSaberSender/Tools/phone_saber_auto_repair_e2e.py --allow-real-codex
```

The harness measures the 40-case corpus first, derives a blue component-area
boundary from three pinned visible positives, and requires a trial with exactly
those three failures and every negative still passing. It then creates a new
`/tmp/phonesaber-auto-repair-e2e-*` clone and local bare origin, commits the
fault there, and runs the existing Luna/max triage and Sol/high repair gates.
The real checkout is read-only; the temporary clone rejects GitHub remotes and
uses only normal local pushes. It writes `e2e-summary.json`, formal results,
measurements, and the analysis bundle under its temporary directory. Omit the
flag to inspect help without starting paid model calls. If source layout or
corpus expectations change, the harness stops before a repair attempt.

Save machine-readable results when needed:

```bash
ios/PhoneSaberSender/Tools/run_lossless_regression.py \
  --json /tmp/phonesaber-regression.json \
  --csv /tmp/phonesaber-regression.csv
```

## Background hard-negative benchmark (informational)

`run_background_negative_benchmark.py` runs the unmodified production detector
(the same Swift harness as the lossless suite) on device frames that show matte
red background objects (a red label strip, red carabiners, a red basket) and no
lit saber. These objects can pass RED emitter eligibility and win the RED
selection, which causes large output jumps. Each listed color in
`background_negative_benchmark.json` must not be detected; any detection is a
false positive. One control frame is already rejected and must stay rejected.

Why it is separate from the formal corpus: 7 of the 8 frames are detected
today, so they are known failures. Adding them to the 40/40 lossless gate would
break the gate before an eligibility fix exists. This benchmark only measures
progress. It exits 0 unless you pass `--strict`, and it is not wired into
`tools/verify_phone_saber.sh`.

Privacy rule: the source PNGs are private home recordings that show a person.
Never copy them into this repository. The manifest references them by a path
relative to the diagnostics inbox
(`~/Library/Application Support/PhoneSaber/diagnostics-inbox`) and pins the
full SHA-256. A missing image is skipped. An image with a different hash is
reported as an error and is not evaluated.

```bash
ios/PhoneSaberSender/Tools/run_background_negative_benchmark.py
ios/PhoneSaberSender/Tools/run_background_negative_benchmark.py \
  --inbox /path/to/diagnostics-inbox --json /tmp/phonesaber-bgneg.json
```

`--json -` prints JSON to stdout. You can also set the inbox with
`PHONESABER_DIAGNOSTICS_INBOX`. For each image the report shows, per color:
detected, the number of eligible candidates, and the winning candidate's source
type, score, bounding box, centroid (the bounding-box center), and features
(peak, mean, high-value ratio, purity, core support, axial density, sampled
point count). The harness does not expose component area. The summary line is
`false positives X / available N (missing M, errors E)`. Images whose measured
status differs from `baselineStatus` are listed as `baseline changed`.

Baseline (2026-10-02): **false positives 7 / available 8**. The control
`control_label_carabiners_013205_282` is rejected.

The unit tests do not need the private images. To also check the real inbox,
set `PHONESABER_BACKGROUND_BENCHMARK_INTEGRATION=1`. That test is skipped when
the images are absent.

Analyze one device recording:

```bash
ios/PhoneSaberSender/Tools/analyze_session_metadata.py \
  /path/to/phonesaber_session_metadata.json
```

Save JSON, or compare before/after recordings:

```bash
ios/PhoneSaberSender/Tools/analyze_session_metadata.py session.json --json summary.json
ios/PhoneSaberSender/Tools/analyze_session_metadata.py \
  --compare before_metadata.json after_metadata.json
```

The session analyzer uses the explicit `redDetectionSucceeded` and
`blueDetectionSucceeded` fields where present. Prediction-only coordinates
are excluded from actual detection rate. If an older metadata file lacks the
fresh-success fields, `detected=true` with `predicted=true` is excluded.

Compare two device sessions:

```bash
ios/PhoneSaberSender/Tools/compare_phone_saber_sessions.py \
  baseline_metadata.json experimental_metadata.json
```

Attach either or both iOS Freeze Diagnostics logs and save a JSON report:

```bash
ios/PhoneSaberSender/Tools/compare_phone_saber_sessions.py \
  baseline_metadata.json experimental_metadata.json \
  --baseline-freeze-log baseline_freeze.log \
  --experimental-freeze-log experimental_freeze.log \
  --json comparison.json
```

The comparison treats dropout as a metadata detection miss. Metadata cannot
establish whether the saber was in view, so it does not label a miss as a
confirmed recognition failure. Freeze timing values come from the existing
logged summary windows; the report documents how multiple windows are
combined. Run `run_lossless_regression.py` separately for the existing PNG
fixture regression gate; this session comparator does not re-run image
detection or replace that gate.

New DEBUG recordings also include `cameraSamples` in metadata, sampled about
once per second and linked to the nearest recorded `frameID` and presentation
time. Compare exposure duration, ISO, white balance gains and modes, focus and
lens position, active format, and active FPS range between school and a known
good environment. Older recordings have no camera samples.

Bridge dropout（棒が画面外に出ただけの欠落を診断対象から除く選択）、診断色（RED/BLUE/BOTH）、
compact contextは [BRIDGE_DROPOUT_DIAGNOSTICS.md](BRIDGE_DROPOUT_DIAGNOSTICS.md) を参照してください。

Tracking preflight、terminal summary、実capture + mock LLMのE2E、Recording OFF監査、
実機benchmark手順は [TRACKING_VERIFICATION.md](TRACKING_VERIFICATION.md) を参照してください。

Codex CLIのみをbundleなしで確認するには、次を実行してください。

```bash
python3 ios/PhoneSaberSender/Tools/phone_saber_codex_probe.py
```

固定のLuna/max/read-onlyで実modelを呼び、成功時は`CLI_PROBE_OK`を表示します。
失敗時はJSON/JSONLのerror payloadと実行条件を表示し、秘密情報を除いた全文を
`~/Library/Logs/PhoneSaber/codex/`へ保存します。CLI schema拒否の原因・修正・
保存session再試験は [CODEX_CLI_VERIFICATION.md](CODEX_CLI_VERIFICATION.md) を参照してください。

## Event-day status check (read-only)

`phone_saber_status.py` (or double-click `PhoneSaber Status.command`) prints one
Japanese OK / WARN / NG line per item in a few seconds: the triage receiver
(`GET 127.0.0.1:8765/health`), who binds UDP 5005/5006 (`lsof`; non-Unity owner =
port conflict), the `PhoneSaberP2PBridge` process, the latest `[PhoneSaber][P2P]
last 10s ... maxGapMs...` stats, connection events and warnings since the last
Play in `~/Library/Logs/Unity/Editor.log`, the newest inbox bundle with its
`.report.md` and Codex analysis result, git state of school-festival and
3D-Saber (no fetch, `GIT_OPTIONAL_LOCKS=0`), and the Codex CLI. It never starts,
stops, sends or writes anything. Exit status: 2 with NG, 1 with WARN, else 0.
`install_phone_saber_launcher.command` (also run by `setup_mac.command`) puts a
`PhoneSaber Status.command` link on the Desktop next to `Start PhoneSaber`;
re-running it on an older install only adds the missing link and leaves the
existing ones (and a running receiver) untouched.
Operating and troubleshooting steps: [docs/claude/EVENT_DAY_RUNBOOK.md](../../../docs/claude/EVENT_DAY_RUNBOOK.md).

## P2P bridge (optional)

`phone_saber_p2p_bridge.py` (or `Start PhoneSaber P2P Bridge.command`) builds and
runs the Mac-side bridge in `p2p_bridge/PhoneSaberP2PBridge.swift`: it receives
PhoneSaberSender coordinates over Network.framework peer-to-peer Wi-Fi
(`_phonesaber-p2p._udp`) and forwards the unchanged payload to 127.0.0.1:5005
(RED) / 5006 (BLUE) for the unchanged Unity InputPoint. Without it the iPhone
keeps using the existing LAN UDP path. See [../P2P_BRIDGE.md](../P2P_BRIDGE.md).
