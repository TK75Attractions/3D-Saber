# PhoneSaber Debug Recording triage

## Phases

- Phase A creates a `phone_saber_triage_<session>` directory after Stop. It contains only `summary.json`, `prompt.md`, selected lossless PNG files, and at most five compact context frames per image. H.264 files and complete session metadata stay outside the bundle.
- Phase B sends the bundle to a Mac receiver over HTTP/TCP after resolving `_phonesaber-diag._tcp` with Bonjour. The receiver verifies the `PSBT` manifest, SHA-256 for every file, paths, file/image counts, bundle size, PNG signature, and summary schema before moving it atomically into the inbox.
- Phase C runs Codex CLI from the Mac inbox using the Mac user's existing Codex authentication. It attaches only the selected PNGs with the official `codex exec --image` option, uses a read-only sandbox, and writes `analysis_report.md` and `analysis_report.json` with a structured repair assessment. Selected compact contexts include bounded candidate decision traces from Debug Recording.
- If Luna/MAX requests an internal eligibility rejection detail already present in those contexts, the receiver extracts the failed rule, measured value, and threshold and runs one enriched Luna/MAX re-analysis. If the selected images and numeric decision trace are sufficient but Luna remains ambiguous, one read-only Sol/High `SECOND_OPINION_ANALYSIS` may run. A request for genuinely new images stops at `NEEDS MORE EVIDENCE`.
- Phase D evaluates a deterministic repair gate. If the selected PNGs do not show a real saber, the diagnosis conflicts with compact context, or formal RED/BLUE corpus coverage is insufficient, it writes `NEEDS MORE EVIDENCE` and leaves production code untouched. An actionable case requires a clean, synchronized `main` before any edit.
- Phase E gives a repair Codex only four allowlisted recognition source/test files plus the selected evidence in a temporary workspace outside the repository. It applies only changed allowlisted paths, runs the pinned 40-case formal baseline comparison and `verify_phone_saber.sh`, then asks a separate read-only Codex reviewer. At most two attempts are allowed. Only a verified and approved candidate is committed on `main` and pushed normally to `origin/main`, after a second remote check.

The coordinate path remains UDP 5005/5006. Triage upload does not call or wait on `UDPSender`, camera processing, or gameplay. The phone keeps its local bundle if Bonjour discovery or upload fails; upload attempts stop after three tries. The iPhone preference `Stop後にtriage bundleをMacへ自動転送` defaults ON and can be turned OFF.

## Quick start on macOS

1. On the first use, double-click `install_phone_saber_launcher.command` in this folder. It adds Desktop links for `Start PhoneSaber.command` and `Open PhoneSaber Log.command`. Running the installer again is safe; it keeps links that already point to these launchers and does not replace other Desktop items.
2. Before recording, double-click **Start PhoneSaber** on the Desktop. It resolves this repository even when launched through the Desktop link, shows the branch and Git state, then starts the same receiver used by the direct command below. A non-main branch or dirty tree produces a warning and does not trigger cleanup or reset. The receiver can still collect and analyze bundles; the existing repair safety gate blocks source edits unless `main` is clean and synchronized with `origin/main`.
3. After recording, press Stop on the iPhone. To inspect live output or recent history, double-click **Open PhoneSaber Log** on the Desktop.

Each receiver run writes its complete stdout and stderr stream to a timestamped file in:

```text
~/Library/Logs/PhoneSaber/triage-YYYYMMDD-HHMMSS.log
```

`latest.log` points to the current run. The log viewer displays the most recent 200 lines and follows new output. Up to 50 timestamped logs are retained. Session and auto-repair phase markers are added while original receiver output is preserved.

The launcher prevents a second instance with a per-user lock and checks TCP 8765 before starting, which also detects receivers started with the direct command. Ctrl+C sends SIGINT to the receiver process group; the receiver's existing shutdown path closes HTTP, stops Bonjour, and the launcher waits for child processes to exit.

## Direct receiver command

For manual or isolated use, `run_phone_saber_triage_receiver.command` remains available. It listens on TCP 8765, publishes `Phone Saber Diagnostics` with Bonjour, and saves uploaded bundles outside the repository at:

```text
~/Library/Application Support/PhoneSaber/diagnostics-inbox/
```

For an isolated test inbox or port:

```sh
python3 phone_saber_triage_receiver.py --port 8765 --inbox /tmp/phonesaber-inbox
```

Use `--no-bonjour` to test a local receiver without publishing the service. Only local/private network peers can upload. The envelope provides integrity verification, not transport encryption; run it on the trusted Wi-Fi used by the devices.

## Codex analysis and dry run

The receiver starts Codex analysis automatically after a valid upload. It uses the Codex CLI already installed on the Mac and its existing login. If Codex is missing or fails, the received bundle stays in the inbox and the receiver keeps running.

Use `--dry-run` to print the exact selected PNG and compact metadata list without making a Codex call. It also prints the `[AUTO_REPAIR][BRIDGE_SUMMARY]` rows and the `[AUTO_REPAIR][CANDIDATE_AUDIT]` CASE A/B/C hints (read-only, never used by the gate), so a new session can be classified without a paid model call: `python3 ios/PhoneSaberSender/Tools/phone_saber_triage_codex.py --dry-run /path/to/phone_saber_triage_<session>`. A bundle that already holds reports is refused by the input contract; run it on a copy without them. Use `--repair-dry-run --repair-bundle /absolute/path/to/phone_saber_triage_<session>` to evaluate an existing analysis report without editing production source or running repair/review Codex, committing, or pushing. `--no-codex` only receives and saves bundles. `--max-images` defaults to 12 and cannot exceed the hard limit of 20; at most two images are selected for each failure type.

The receiver passes `summary.json`, selected `frames/*.json`, and only the selected PNGs. The bundle's `prompt.md` is retained for inspection but is not attached to Codex; the Mac supplies its own fixed A–G analysis instructions. Analysis Codex is pinned to `gpt-6-luna` with `model_reasoning_effort="max"`. Repair Codex is pinned to `gpt-6-sol` with `model_reasoning_effort="high"`; the independent read-only reviewer uses the same Sol/High setting. Each subprocess passes both values explicitly, and a rejected model or effort stops with `MODEL_UNAVAILABLE` without retrying against CLI defaults. Video and complete recording metadata never enter these Codex calls.

For `candidateCount > 0` and `eligibleCandidateCount = 0`, each compact frame carries at most three candidate traces. The color entry also marks `failureStage: eligibility`. A trace has the candidate index, source type, score, geometry and light measurements, eligibility result, rejection reasons, and failed rule entries with actual values and comparisons when available. These values are copied from production candidate measurements during Debug Recording; normal recognition does not build decision JSON. Source-only comparative rejections whose competitor measurement was not retained have a named reason and null value/threshold. The analysis prompt asks for recurrence across frames and false-positive risk, and never treats a rejection alone as a reason to relax a threshold.

Production eligibility conditions traced here are: the common emitter floor (`peakValue >= 218`, emitter core from `highValueRatio >= 0.08` or bright peak/mean or clipped-white support, and `emitterScore >= 0.42`); compact RED (`peakValue >= 230`, `highValueRatio >= 0.50`, `meanColorPurity >= 0.50`); core-line evidence (`meanColorPurity >= 0.25`, `retainedBodyRatio >= 0.35`, `longitudinalContinuity >= 0.70`, `highValueRatio >= 0.35`); and the later source-specific gates for sparse core lines, overlap, unsupported BLUE proposals, broad BLUE emitters, weak raw tails, RED bridges, short core-halo subsegments, and duplicate subsegments. The detailed comparison thresholds for those later gates come from `BGRADetection.swift` and are recorded at the rejection site during Debug Recording. Candidate-generation geometry floors precede eligibility; a rejected pre-candidate never appears in `candidateDecisionTrace`.

The maximum analysis sequence is initial Luna/MAX plus one Luna/MAX re-analysis plus one Sol/High second opinion. The Sol second opinion is read-only and cannot edit source. Its result, like an initial Luna result, must pass the same deterministic repair gate before the separate write-capable Sol/High repair role runs. `analysisReanalysisExecuted`, `analysisEscalationModel`, `analysisEscalationExecuted`, `repairModel`, and `reviewModel` are recorded separately. If either analyst still needs evidence, repair is skipped.

The bundle retains `state.json`, `repair_status.json`, `repair_report.md/json`, `review_report.md/json`, and `final_report.md`. The inbox holds a hidden per-session candidate and backup directory for interrupted-run recovery. Startup leaves every existing session untouched, regardless of state or report files. A failed verification, Codex call, review, or remote check restores only the files owned by that repair. The receiver never uses force push, hard reset, or repository-wide clean. If an owned file was changed externally, rollback stops for manual inspection.

### New uploads only; explicit manual retry

Normal receiver startup never scans the inbox for sessions and never queues an
existing bundle. This applies to automatic, dry-run, and receive-only modes.
Unfinished, unanalyzed, timed-out, completed, and blocked sessions stay in the inbox
without changes. Restarting after interrupted analysis or repair does not resume
it; a person must explicitly select the bundle for manual processing.

Before any POST, the receiver prints START, its listen address, and WAITING:

```text
[PHONE_SABER][START]
[triage] listening on ...
[PHONE_SABER][WAITING] source=new_upload
```

Only a newly accepted `POST /v1/bundle` queues automatic processing. It prints:

```text
[PHONE_SABER][SESSION] sessionID=sample_session source=new_upload
[AUTO_REPAIR][PRECHECK] sessionID=sample_session source=new_upload elapsed=0.0s subprocess=none result=starting
```

PRECHECK, ANALYSIS, REANALYSIS, SECOND_OPINION, REPAIR, REVIEW, DONE and other repair
logs continue to carry `sessionID` and `source`. The startup scan and RESUME log
path have been removed; normal startup does not emit PRECHECK, ANALYSIS, or RESUME.
A duplicate POST for a bundle already in the inbox still returns HTTP 409 and does
not retry it. The queue limit remains four waiting jobs; a full queue preserves a
new upload for a later explicit manual invocation.

The existing manual CLIs require an explicit bundle path identifying one session.
Run from the repository root, replacing `<sessionID>` with the desired session:

```sh
bundle="$HOME/Library/Application Support/PhoneSaber/diagnostics-inbox/phone_saber_triage_<sessionID>"
# Analyze an unfinished upload that has no analysis report, including analysis timeout.
python3 ios/PhoneSaberSender/Tools/phone_saber_triage_codex.py "$bundle"
# Evaluate the existing analysis and run the existing repair/recovery policy.
python3 ios/PhoneSaberSender/Tools/phone_saber_auto_repair.py "$bundle"
```

Neither CLI starts a receiver or scans other sessions. Both retain
`source=manual_retry`; the session argument is required. The analysis CLI still
refuses to overwrite an existing analysis report. If a report already exists, use
the repair CLI directly to reuse it. For inspection without source edits, use the
existing receiver option `--repair-dry-run --repair-bundle "$bundle"`, or add
`--repair-dry-run` to the repair CLI. Manual repair keeps all existing gates,
terminal-state guards, interrupted-run recovery, and commit/push behavior; it does
not force another repair of a terminal session.

The repair terminal states remain `needs_capture`, `repair_failed`, `blocked`,
`blocked_remote_changed`, `repair_pushed`, `dry_run`, `MODEL_UNAVAILABLE`, and
`BLOCKED_BASELINE_UNSTABLE`. Removing a status file does not bypass those guards.
Recognition, diagnostics, image selection, preflight, repair assessments, models,
manual analysis contents, and in-session retry limits are unchanged.

Before repair, the pinned 40-case formal corpus always runs and its per-case results are saved as `baseline_regression.json`. A recognition assertion failure can be repair evidence; a compiler, runner, fixture, or resource failure blocks repair. Failed cases are classified against the incident PNGs that the analysis explicitly confirms as evidence, as target-related, unrelated-existing, or unknown. A baseline with no related failure or excessive unknown failures stops with `BLOCKED_BASELINE_UNSTABLE`.

After repair, every previous PASS must remain PASS with its protected detection, candidate type, and endpoints intact. At least one target-related FAIL must become PASS when the baseline has failures, and the full post-repair formal corpus must pass. The report includes the before/after counts, preserved passes, target repairs, new regressions, and a per-case diff. The repair Codex may read a pinned copy of the checked-in `camera.py` HSV reference; it cannot edit the formal runner or manifest. The independent reviewer receives three hash-checked negative PNG controls per affected color from the formal corpus and checks candidate-score changes against them and the formal rejection expectations. Required build, Detection, Tools, Release, independent review, and unchanged `origin/main` gates still apply before commit and push.

## Bundle shape

```text
phone_saber_triage_<session>/
  summary.json
  prompt.md
  images/
    image_01_manual_frame_123.png
  frames/
    frame_123_1.json
```

PNG files are copied byte-for-byte; the transport envelope adds a manifest and checksum but does not recompress images.

## Automatic motion evidence

Debug Recording now retains high-score motion events automatically and selects
original pre/event/post PNGs within the existing image and transport limits.
Luna receives measured signs and selection reasons. These heuristics do not prove
a real saber; latency-only images cannot support recognition repair. See
[Debug Recording motion evidence](DEBUG_MOTION_EVENTS.md).
