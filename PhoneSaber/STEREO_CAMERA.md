# Stereo camera tracking

`stereo_camera.py` is the recommended runtime when two cameras are available.
It keeps the existing 2D UDP protocol:

- stick A: `127.0.0.1:5005`
- stick B: `127.0.0.1:5006`

Each packet is still `x1,y1,x2,y2`, so the current Unity receiver does not
need to change. A calibrated 3D packet is also sent as compact JSON to port
5007. Its `endpoints` values are in the stereo calibration coordinate system
(millimeters when the calibration board square size is given in millimeters).

## 1. Hardware

Use two cameras with a fixed rigid mount. They should view the same play area
from slightly different horizontal positions. Lock focus, exposure, white
balance, resolution, and frame rate after setup. A larger baseline improves
depth precision, but both cameras must still see the whole area.

An actively blinking LED on each stick is still strongly recommended. Stereo
reduces false positives and gives depth, but it cannot distinguish a real red
shirt from a red LED by color alone.

## 2. Calibration

Print a chessboard with 9 by 6 inner corners. Run:

```bash
python stereo_calibrate.py --left 0 --right 1 --square 25 --output stereo_calibration.json
```

Press SPACE for 20-30 pairs while moving the board around the full shared
view. Use different distances and angles. Keep only views where the board is
clearly visible in both images. The saved file must be created at the same
resolution used by the runtime.

## 3. Runtime

```bash
python stereo_camera.py --left 0 --right 1 --calibration stereo_calibration.json
```

Add `--show` while tuning. The normal mode has no display window and uses a
latest-frame capture thread to avoid camera-buffer latency.

## IMU note

`acceration.detect` provides acceleration and gyro values over BLE through the
existing BLE-to-UDP bridge. Those values are useful for saber orientation, but
they cannot correct camera pixel coordinates until the IMU-to-camera rigid
transform is calibrated. The stereo tracker therefore does not apply an
uncalibrated IMU correction that could make the position worse. Once that
transform is measured, it can be added to the 5007 3D stream without changing
the 5005/5006 Unity protocol.
