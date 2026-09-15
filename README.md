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
