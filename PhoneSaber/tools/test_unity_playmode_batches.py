import re
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import unity_playmode_batches as batches


class UnityPlayModeBatchesTests(unittest.TestCase):
    def test_discovers_only_classes_in_files_with_tests(self):
        with tempfile.TemporaryDirectory() as root:
            folder = Path(root)
            (folder / "A.cs").write_text("public class APlayTests {\n [UnityTest] public IEnumerator X() { yield break; } }\n")
            (folder / "Helper.cs").write_text("public static class Helper { }\n")
            (folder / "B.cs").write_text("public sealed class BTests { [TestCase(1)] public void Y(int v) {} }\n")
            (folder / "C.cs").write_text("public class CPlayTests { [UnityTest, Timeout(360000)] public IEnumerator Z() { yield break; } }\n")
            self.assertEqual(batches.discover_classes(folder), ["APlayTests", "BTests", "CPlayTests"])

    def test_chunks_cover_every_class_once(self):
        items = [f"C{i}" for i in range(11)]
        groups = batches.chunk(items, 4)
        self.assertEqual(sum(groups, []), items)
        self.assertEqual(len(groups), 4)
        self.assertEqual(batches.chunk(["A"], 6), [["A"]])

    def test_filter_is_anchored(self):
        pattern = re.compile(batches.test_filter(["PhoneSaberTests"]))
        self.assertTrue(pattern.search("PhoneSaberTests.Run"))
        self.assertFalse(pattern.search("PhoneSaberTestsExtra.Run"))
        self.assertFalse(pattern.search("OtherPhoneSaberTests.Run"))

    def test_crashed_group_is_retried_then_split_to_the_class(self):
        calls = []

        def fake(project, out, name, classes, unity):
            calls.append(tuple(classes))
            if "Bad" in classes:
                return None
            return {"total": len(classes), "passed": len(classes), "failed": 0, "skipped": 0, "failed_tests": []}

        with tempfile.TemporaryDirectory() as out, mock.patch.object(batches, "run_group", side_effect=fake):
            summary = batches.run(Path("/copy"), Path(out), ["A", "Bad", "C", "D"], groups=1)
        self.assertEqual(summary["crashed"], ["Bad"])
        self.assertEqual(summary["passed"], 3)
        self.assertEqual(calls[0], ("A", "Bad", "C", "D"))
        self.assertEqual(calls[1], ("A", "Bad", "C", "D"))  # one retry before splitting


if __name__ == "__main__":
    unittest.main()
