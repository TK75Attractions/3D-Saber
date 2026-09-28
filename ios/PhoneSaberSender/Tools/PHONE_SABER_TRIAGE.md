# PhoneSaber Debug Recording triage

## Phases

- Phase A creates a `phone_saber_triage_<session>` directory after Stop. It contains only `summary.json`, `prompt.md`, selected lossless PNG files, and at most five compact context frames per image. H.264 files and complete session metadata stay outside the bundle.
- Phase B sends the bundle to a Mac receiver over HTTP/TCP after resolving `_phonesaber-diag._tcp` with Bonjour. The receiver verifies the `PSBT` manifest, SHA-256 for every file, paths, file/image counts, bundle size, PNG signature, and summary schema before moving it atomically into the inbox.
- Phase C runs Codex CLI from the Mac inbox using the Mac user's existing Codex authentication. It attaches only the selected PNGs with the official `codex exec --image` option, uses a read-only sandbox, and writes `analysis_report.md` and `analysis_report.json` with a structured repair assessment.
- Phase D evaluates a deterministic repair gate. If the selected PNGs do not show a real saber, the diagnosis conflicts with compact context, or formal RED/BLUE corpus coverage is insufficient, it writes `NEEDS MORE EVIDENCE` and leaves production code untouched. An actionable case requires a clean, synchronized `main` before any edit.
- Phase E gives a repair Codex only seven allowlisted recognition source/test/tool files plus the selected evidence in a temporary workspace outside the repository. It applies only changed allowlisted paths, runs the pinned 40-case formal baseline comparison and `verify_phone_saber.sh`, then asks a separate read-only Codex reviewer. At most two attempts are allowed. Only a verified and approved candidate is committed on `main` and pushed normally to `origin/main`, after a second remote check.

The coordinate path remains UDP 5005/5006. Triage upload does not call or wait on `UDPSender`, camera processing, or gameplay. The phone keeps its local bundle if Bonjour discovery or upload fails; upload attempts stop after three tries. The iPhone preference `Stop後にtriage bundleをMacへ自動転送` defaults ON and can be turned OFF.

## Start the Mac receiver

Run `run_phone_saber_triage_receiver.command` on the Mac before recording. It listens on TCP 8765, publishes `Phone Saber Diagnostics` with Bonjour, and saves outside the repository at:

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

Use `--dry-run` to print the exact selected PNG and compact metadata list without making a Codex call. Use `--repair-dry-run --repair-bundle /absolute/path/to/phone_saber_triage_<session>` to evaluate an existing analysis report without editing production source or running repair/review Codex, committing, or pushing. `--no-codex` only receives and saves bundles. `--max-images` defaults to 12 and cannot exceed the hard limit of 20; at most two images are selected for each failure type.

The receiver passes `summary.json`, selected `frames/*.json`, and only the selected PNGs. The bundle's `prompt.md` is retained for inspection but is not attached to Codex; the Mac supplies its own fixed A–G analysis instructions. Analysis Codex is pinned to `gpt-6-luna` with `model_reasoning_effort="max"`. Repair Codex is pinned to `gpt-6-sol` with `model_reasoning_effort="high"`; the independent read-only reviewer uses the same Sol/High setting. Each subprocess passes both values explicitly, and a rejected model or effort stops with `MODEL_UNAVAILABLE` without retrying against CLI defaults. Video and complete recording metadata never enter these Codex calls.

The bundle retains `state.json`, `repair_status.json`, `repair_report.md/json`, `review_report.md/json`, and `final_report.md`. The inbox holds a hidden per-session candidate and backup directory for interrupted-run recovery. The receiver skips terminal repair states when restarted. A failed verification, Codex call, review, or remote check restores only the files owned by that repair. The receiver never uses force push, hard reset, or repository-wide clean. If an owned file was changed externally, rollback stops for manual inspection.

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
