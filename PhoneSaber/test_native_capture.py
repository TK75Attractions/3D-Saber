import ctypes as ct
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import numpy as np

from native_capture import NativeLatestFrame, ROOT


class FakeLibrary:
    def __init__(self):
        self.sequence = 1
        self.closed = 0

    def saber_copy(self, handle, after, destination, capacity, metadata):
        if after == self.sequence:
            return 0
        metadata[:] = [3, 2, self.sequence, 12.5, 0.02, 0.033]
        if capacity < 24:
            return -1
        raw = bytes([7, 11, 19, 255]) * 6
        ct.memmove(destination, raw, len(raw))
        return 1

    def saber_close(self, handle):
        self.closed += 1


class NativeCaptureTests(unittest.TestCase):
    def test_wrapper_resizes_converts_and_owns_frames(self):
        latest = NativeLatestFrame.__new__(NativeLatestFrame)
        latest.handle = 1
        latest.lib = FakeLibrary()
        latest.clock_offset = 10.0
        latest.buffer = np.empty(1, np.uint8)
        latest.metadata = (ct.c_double * 6)()
        latest.last_frame_shape = None
        frame, timestamp, sequence = latest.get()
        self.assertEqual(frame.shape, (2, 3, 3))
        self.assertEqual(latest.last_frame_shape, (2, 3, 3))
        np.testing.assert_array_equal(frame[0, 0], [7, 11, 19])
        self.assertEqual((timestamp, sequence, latest.pts_age_ms), (22.5, 1, 20.0))
        self.assertAlmostEqual(latest.frame_interval_s, 0.033)
        latest.buffer[:] = 0
        np.testing.assert_array_equal(frame[0, 0], [7, 11, 19])
        self.assertIsNone(latest.get(sequence)[0])
        latest.stop()
        latest.stop()
        self.assertEqual(latest.lib.closed, 1)
        self.assertIsNone(latest.get(sequence)[0])

    def test_native_format_selection_is_strict_and_reports_requested_actual(self):
        source = (ROOT / "native" / "ContinuityCapture.m").read_text()
        self.assertIn("formatHasDimensions", source)
        self.assertIn("size.width == width && size.height == height", source)
        self.assertIn("formatSupportsFPS", source)
        self.assertNotIn("CMTimeMake" + "WithSeconds", source)
        self.assertIn("frameDurationForFPS", source)
        self.assertIn("CMTimeMake(1, (int32_t)integral)", source)
        self.assertIn("device.activeVideoMinFrameDuration = requestedDuration", source)
        self.assertIn("device.activeVideoMaxFrameDuration = requestedDuration", source)
        self.assertIn("actualFormat != choice", source)
        self.assertIn('No camera format matches the requested %dx%d', source)
        self.assertIn('"selected"', source)
        self.assertIn('"actual"', source)
        self.assertIn('"fps_ranges"', source)
        self.assertIn('"min_fps"', source)
        self.assertIn('"max_fps"', source)
        self.assertIn("CVPixelBufferGetWidth", source)
        self.assertIn("_output.videoSettings = @{", source)
        self.assertIn("(id)kCVPixelBufferWidthKey: @(width)", source)
        self.assertIn("(id)kCVPixelBufferHeightKey: @(height)", source)
        self.assertIn("(id)kCVPixelBufferPixelFormatTypeKey: @(kCVPixelFormatType_32BGRA)", source)
        self.assertIn("not the wireless transport", source)
        self.assertIn('@"callback_output"', source)
        self.assertIn("_frameInterval", source)
        self.assertNotIn("formatCost(d.width, d.height, width, height)", source)

    def test_video_settings_request_exact_callback_dimensions_and_metadata_abi(self):
        source = (ROOT / "native" / "ContinuityCapture.m").read_text()
        settings = source[source.index("_output.videoSettings = @{"):]
        self.assertLess(settings.index("(id)kCVPixelBufferWidthKey: @(width)"),
                        settings.index("(id)kCVPixelBufferHeightKey: @(height)"))
        self.assertIn("metadata[5] = _frameInterval", source)
        harness = (ROOT / "native" / "test_capture.m").read_text()
        self.assertIn("double metadata[6] = {0};", harness)

    def test_integer_fps_uses_exact_period_and_non_integer_path_is_bounded(self):
        source = (ROOT / "native" / "ContinuityCapture.m").read_text()
        self.assertIn("#include <limits.h>", source)
        self.assertIn("fps > (double)INT32_MAX", source)
        self.assertIn("fps < 1.0 / (double)INT32_MAX", source)
        self.assertIn("preferredDenominator = 1000000", source)
        self.assertIn("CMTimeConvertScale(expected, duration.timescale", source)
        self.assertIn("kCMTimeRoundingMethod_RoundHalfAwayFromZero", source)
        self.assertIn("CMTime oneTick = CMTimeMake(1, duration.timescale)", source)

    @unittest.skipUnless(sys.platform == "darwin", "AVFoundation requires macOS")
    def test_native_period_helpers_keep_integer_fps_exact(self):
        with tempfile.TemporaryDirectory() as directory:
            harness = Path(directory) / "test-periods.m"
            executable = Path(directory) / "test-periods"
            harness.write_text(
                '#import "ContinuityCapture.m"\n'
                '#include <assert.h>\n'
                'int main(void) { @autoreleasepool {\n'
                '    CMTime period;\n'
                '    assert(frameDurationForFPS(30.0, &period));\n'
                '    assert(period.value == 1 && period.timescale == 30);\n'
                '    assert(durationMatchesFPS(period, 30.0));\n'
                '    assert(frameDurationForFPS(60.0, &period));\n'
                '    assert(period.value == 1 && period.timescale == 60);\n'
                '    assert(durationMatchesFPS(period, 60.0));\n'
                '    assert(frameDurationForFPS(29.97, &period));\n'
                '    assert(durationMatchesFPS(period, 29.97));\n'
                '    return 0;\n'
                '} }\n',
                encoding="utf-8",
            )
            result = subprocess.run([
                "xcrun", "clang", "-O2", "-fobjc-arc", "-Werror",
                "-Wno-deprecated-declarations", "-framework", "Foundation",
                "-framework", "AVFoundation", "-framework", "CoreMedia",
                "-framework", "CoreVideo", "-I", str(ROOT / "native"),
                str(harness), "-o", str(executable),
            ], capture_output=True, text=True, timeout=60)
            self.assertEqual(result.returncode, 0, result.stderr)
            result = subprocess.run([str(executable)], capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)

    def test_native_format_index_is_checked_before_selection(self):
        source = (ROOT / "native" / "ContinuityCapture.m").read_text()
        index_check = "formatIndex >= (NSInteger)device.formats.count"
        self.assertIn(index_check, source)
        selection = source.index("if (!formatHasDimensions(format, width, height)")
        self.assertLess(source.index(index_check), selection)

    @unittest.skipUnless(sys.platform == "darwin", "AVFoundation requires macOS")
    def test_native_mailbox(self):
        with tempfile.TemporaryDirectory() as directory:
            executable = Path(directory) / "test-capture"
            result = subprocess.run([
                "xcrun", "clang", "-O2", "-fobjc-arc", "-Werror",
                "-Wno-deprecated-declarations", "-framework", "Foundation",
                "-framework", "AVFoundation", "-framework", "CoreMedia",
                "-framework", "CoreVideo", str(ROOT / "native" / "test_capture.m"),
                "-o", str(executable),
            ], capture_output=True, text=True, timeout=60)
            self.assertEqual(result.returncode, 0, result.stderr)
            result = subprocess.run([str(executable)], capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("PASS:", result.stdout)


if __name__ == "__main__":
    unittest.main()
