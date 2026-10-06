import math
import tempfile
import unittest
from pathlib import Path
import compare_parity as parity


class ComparisonTests(unittest.TestCase):
    def test_exact_float_comparison(self):
        self.assertEqual(parity.differences({"score": 1.0}, {"score": 1}), [])
        self.assertTrue(parity.differences(1.0, math.nextafter(1.0, 2.0)))
        self.assertTrue(parity.differences(0.0, -0.0))
        self.assertTrue(parity.differences(True, 1))

    def test_candidate_and_selected_mismatches_are_reported(self):
        reference = {"selected": [{"x": 1, "y": 2}], "candidates": [{"eligible": True, "source": "core-line", "score": 80.0}]}
        self.assertEqual(parity.differences(reference, reference), [])
        changed = {"selected": [{"x": 2, "y": 2}], "candidates": [{"eligible": False, "source": "core-line", "score": 80.0}]}
        errors = parity.differences(reference, changed)
        self.assertEqual(len(errors), 2)
        self.assertIn("colors.selected[0].x", errors[1])
        self.assertTrue(parity.differences([], [reference]))
        self.assertTrue(parity.differences(reference, {"selected": None}))

    def test_only_original_images_are_discovered(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp) / "session/images"
            directory.mkdir(parents=True)
            for name in ("original.png", "dropout_annotated.png", "b.PNG", "notes.json"):
                (directory / name).touch()
            self.assertEqual(parity.original_pngs(Path(temp)), [directory / "original.png"])

    def test_rgba_conversion_and_padded_stride(self):
        bgra = bytes([1, 2, 3, 4, 5, 6, 7, 8])
        self.assertEqual(parity.rgba_padded(bgra, 1, 2), bytes([3, 2, 1, 4]) + b"\xa5" * 13 + bytes([7, 6, 5, 8]) + b"\xa5" * 13)


if __name__ == "__main__":
    unittest.main()
