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
