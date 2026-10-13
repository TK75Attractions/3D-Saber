"""Chrome を起動せず、PDF 更新中の容量不足で既存の配布物を失わないことを検証する。"""
import errno
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import build_staff_manual_pdf as builder


class StaffManualWriteTests(unittest.TestCase):
    def test_write_failure_preserves_the_previous_pdf(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = root / "manual.md"
            source.write_text("# マニュアル")
            output = root / "manual.pdf"
            previous = b"previous valid PDF"
            output.write_bytes(previous)
            pdf = b"%PDF-1.4\n/Type /Page\n/Type /Page\n%%EOF\n"

            def chrome(command, **kwargs):
                target = Path(next(arg.removeprefix("--print-to-pdf=") for arg in command if arg.startswith("--print-to-pdf=")))
                with target.open("wb") as handle:
                    handle.write(pdf)

            def disk_full(path, data):
                with path.open("wb") as handle:
                    handle.write(data[:8])
                raise OSError(errno.ENOSPC, "injected disk full")

            with mock.patch.object(builder, "CHROME", str(source)), \
                    mock.patch.object(builder.subprocess, "run", side_effect=chrome), \
                    mock.patch.object(Path, "write_bytes", disk_full):
                with self.assertRaises(OSError):
                    builder.main(["--source", str(source), "--output", str(output)])
            self.assertEqual(output.read_bytes(), previous)
            self.assertEqual(sorted(p.name for p in root.iterdir()), ["manual.md", "manual.pdf"])


if __name__ == "__main__":
    unittest.main()
