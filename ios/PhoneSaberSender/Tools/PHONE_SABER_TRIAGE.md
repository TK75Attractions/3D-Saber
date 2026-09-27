# PhoneSaber Debug Recording triage

## Phases

- Phase A creates a `phone_saber_triage_<session>` directory after Stop. It contains only `summary.json`, `prompt.md`, selected lossless PNG files, and at most five compact context frames per image. H.264 files and complete session metadata stay outside the bundle.
- Phase B sends the bundle to a Mac receiver over HTTP/TCP after resolving `_phonesaber-diag._tcp` with Bonjour. The receiver verifies the `PSBT` manifest, SHA-256 for every file, paths, file/image counts, bundle size, PNG signature, and summary schema before moving it atomically into the inbox.
- Phase C runs Codex CLI from the Mac inbox using the Mac user's existing Codex authentication. It attaches only the selected PNGs with the official `codex exec --image` option, uses a read-only sandbox, and writes `analysis_report.md` and `analysis_report.json` only.

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

Use `--dry-run` to print the exact selected PNG and compact metadata list without making a Codex call. Use `--no-codex` to only receive and save bundles. `--max-images` defaults to 12 and cannot exceed the hard limit of 20; at most two images are selected for each failure type.

The receiver passes `summary.json`, selected `frames/*.json`, and only the selected PNGs. The bundle's `prompt.md` is retained for inspection but is not attached to Codex; the Mac supplies its own fixed A–G analysis instructions. Codex runs `codex exec --ephemeral --sandbox read-only` in a temporary directory that contains only those selected inputs. The receiver writes only `analysis_report.md` and `analysis_report.json` in the received bundle.

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
