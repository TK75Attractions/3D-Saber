# Unity verification

`verify_unity_tests.py` runs the Unity Test Framework in bounded groups so a
long PlayMode test cannot hide the last completed test indefinitely.

```bash
python3 Tools/verify_unity_tests.py --group all
```

The runner uses Unity `6000.3.9f1`, does not pass `-quit`, and waits for the
Test Framework to publish its result XML. The log is polled while the process
is alive. Each group reports its start time, timeout, last
`[UNITY_TEST]` callback, heartbeat, result file state, and final count.

The groups are:

- `font-regression`: the Japanese fallback suite and related visual tests,
  including the six tests that originally failed.
- `editmode-all`: the complete EditMode assembly.
- `phonesaber-editmode` and `phonesaber-playmode`: the PhoneSaber checks.
- `playmode-other`: all PlayMode classes except the two bounded special groups.
- `playmode-favorite-effects`: the real-time `FavoriteEffectsPlayTests` group,
  with a longer timeout because individual tests intentionally run for minutes.

The editor fixture uses the tracked, OFL-licensed
`Assets/Resources/Fonts/NotoSansJP-Light.otf`. It supplies Japanese glyphs to
the editor-only test override. Production prefers `Makinas-4-Square` and
automatically uses the bundled Noto font when Makinas is absent. Noto is stored
as a regular Git binary, without LFS. Makinas is also included as a regular
Git binary for this game's typography; its author's separate usage terms
are linked in `Assets/Resources/Fonts/Makinas-NOTICE.txt`.
`Tools/Fonts/download-japanese-font.ps1` restores it from the official source
if the local file is missing or damaged.
`UISkinKitTests.ProductionJapaneseFont_WorksWithoutTheTestOverride` clears the
override and checks both legacy Text and TMP through the production loader.
Run it with Makinas absent as well as present. A missing bundled Noto asset
fails explicitly; it is not reported as a skipped or passing test.

`CalibrationCountdownPlayTests` checks the selection/calibration time handoff,
counting while practice is paused or a dialog is open, and timeout navigation
without saving draft settings. Voluntary return retains the remaining time;
timeout discards the draft and opens a new 100-second selection period.

The callback in `Assets/Tests/PlayMode/PlayModeProgressCallback.cs` emits
`[UNITY_TEST][PLAYMODE] class=... test=... elapsed=...` (and the corresponding
EditMode tag) for each test. This separates a known long test from a runner
hang even when the final XML has not been written yet.
