# school-festival
rosin

## Camera tracking

- Basic red/blue tracking: `camera.py`
- Smartphone-only tracking experiment: `smartphone_camera.py`
- Marker-assisted tracking without stereo calibration: `aruco_camera.py`
- Calibrated 3D stereo tracking: `stereo_camera.py`

See `SMARTPHONE_CAMERA.md` for the current single-camera experiment.

## Phone Saber latency test

Run `run_debug.command`, then use **Latency Test** in the browser dashboard.
Point the iPhone camera at the whole test display and start normal sending. The
test waits for two packets from the currently displayed side before each A/B
switch, measures the following matching UDP arrival using the Mac monotonic
clock, and saves completed/partial results in `latency_results/` as CSV.
Timeouts are recorded as failures and excluded from the summary statistics.

Normal operation is **Enter Test View** (or **Prepare Test**) → fullscreen
**Ready** → **Start Test** inside Test View. Point the iPhone at the display
before starting. Controls hide, then a 500 ms settling interval precedes trials.
Completion returns to the dashboard; Escape or the small Stop button cancels.
The target is 3% of the viewport width with a narrow bright core and no glow.
Network packet and
coordinate details, legacy display controls, and the result-folder operation
are under the collapsed Details sections.

The value begins when the browser has drawn a switch and the local dashboard
receives its acknowledgement. It therefore measures the normal camera,
recognition, fresh-only UDP, and Mac receive path without iPhone/Mac clock
synchronization; display refresh/compositor timing remains an external
measurement uncertainty.

## PhoneSaber verification

Run the complete automated verification from the `school-festival` repository:

```bash
./tools/verify_phone_saber.sh
```

The command runs the full iOS XCTest suite (including `DetectionCoreTests`)
serially on one Simulator, with parallel test execution disabled and the worker
count capped at one. It also runs the dedicated static BGRA Detection tests,
the lossless fixture regression, PhoneSaber Tools unittests, an iOS Release
build, the PhoneSaber-related Unity EditMode and PlayMode tests, Unity script
compilation, and `git diff --check` for both `school-festival` and `3D-Saber`.
Every command's
stdout and stderr, Unity result XML, and Xcode result bundles are saved under
`.verify-logs/phone-saber/<run timestamp>/`.
The iOS logs also include the xcresult summary and
`ios-xctest-classification.stdout.log`, which separates assertion failures,
worker kills, other execution failures, and passing runs.

By default the Unity project is expected at the sibling path `../3D-Saber`.
Set `UNITY_PROJECT_PATH` if it is elsewhere. Set `UNITY_EDITOR` to select a
Unity Editor executable, or `PHONESABER_IOS_SIMULATOR_ID` to choose an installed
iPhone Simulator explicitly. If the Unity Editor already has the target
project open and prevents batch-mode execution, Unity stages report `BLOCKED`;
close that Editor and rerun. The command exits `0` when all stages pass, `1` on
a failure, and `2` when a stage is blocked.
