import unittest

import cv2
import numpy as np

from smartphone_camera import DEFAULT_RANGES, Detection, StickTracker, detect_candidates


class TrackingTests(unittest.TestCase):
    def test_attached_skin_does_not_tilt_led_axis(self):
        frame = np.zeros((360, 640, 3), np.uint8)
        cv2.rectangle(frame, (120, 150), (160, 340), (95, 115, 185), -1)
        cv2.line(frame, (70, 150), (560, 150), (0, 0, 255), 18)
        detections, _ = detect_candidates(frame, DEFAULT_RANGES['red'], 170, 5)
        self.assertEqual(len(detections), 1)
        blade = detections[0]
        self.assertLess(abs(blade.p1[1] - blade.p2[1]), 3)
        self.assertAlmostEqual(blade.center[1], 150, delta=3)
        self.assertGreater(blade.length, 470)

    def test_skin_rejected_and_both_led_colors_retained(self):
        frame = np.zeros((360, 640, 3), np.uint8)
        cv2.rectangle(frame, (40, 40), (85, 300), (95, 115, 185), -1)
        cv2.line(frame, (230, 290), (270, 40), (0, 0, 255), 14)
        cv2.line(frame, (420, 290), (460, 40), (255, 0, 0), 14)
        for color, expected_x in (("red", 250), ("blue", 440)):
            detections, _ = detect_candidates(frame, DEFAULT_RANGES[color], 170, 5)
            self.assertEqual(len(detections), 1)
            self.assertAlmostEqual(detections[0].center[0], expected_x, delta=3)

    def test_late_candidate_requires_new_confirmation(self):
        tracker = StickTracker("red", 734, 2, 0, 18, .12, .35, 45, 2, .35)
        detection = Detection((100, 50), (100, 200), (100, 125), 150, 90, 30)
        self.assertIsNone(tracker.update([detection], 1.0))
        self.assertIsNotNone(tracker.update([detection], 1.03))
        self.assertIsNone(tracker.update([detection], 2.0))
        self.assertFalse(tracker.active)


if __name__ == "__main__":
    unittest.main()
