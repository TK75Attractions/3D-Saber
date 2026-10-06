"""Tests for opening the most recent PhoneSaber selected PNG bundle."""

from __future__ import annotations

import contextlib
import io
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

import phone_saber_open_images as viewer


def make_session(inbox: Path, session_id: str, *, images: bool = True,
                 png_count: int = 1, report: bool = False) -> Path:
    session = inbox / f"{viewer.SESSION_PREFIX}{session_id}"
    session.mkdir(parents=True)
    if images:
        image_dir = session / "images"
        image_dir.mkdir()
        for index in range(png_count):
            (image_dir / f"image_{index + 1:02d}.png").write_bytes(b"PNG fixture")
    if report:
        (session / "analysis_report.md").write_text("# Report\n", encoding="utf-8")
    return session


class PhoneSaberOpenImagesTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="縁日 workspace ")
        self.root = Path(self.temporary.name)
        self.inbox = self.root / "Application Support" / "PhoneSaber" / "diagnostics-inbox"
        self.inbox.mkdir(parents=True)
        self.opened: list[list[str]] = []

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def fake_open(self, command: list[str], **_kwargs: object) -> subprocess.CompletedProcess[str]:
        self.opened.append(command)
        return subprocess.CompletedProcess(command, 0, "", "")

    def call_viewer(self, *args: str) -> tuple[int, str, str]:
        stdout = io.StringIO()
        stderr = io.StringIO()
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            result = viewer.main(["--inbox", str(self.inbox), *args], opener=self.fake_open)
        return result, stdout.getvalue(), stderr.getvalue()

    def test_latest_timestamp_session_opens_multiple_pngs_in_preview(self) -> None:
        older = make_session(self.inbox, "phonesaber_20260929_220028_395", png_count=1)
        latest = make_session(
            self.inbox, "phonesaber_20260929_220031_000", png_count=2, report=True,
        )
        # A later report write changes a directory mtime; it must not decide which
        # recording session is selected.
        os.utime(older, ns=(9_000_000_000, 9_000_000_000))
        os.utime(latest, ns=(1_000_000_000, 1_000_000_000))

        result, output, error = self.call_viewer()

        self.assertEqual(result, 0, error)
        self.assertIn("Session: phonesaber_20260929_220031_000", output)
        self.assertIn("Selected images: 2", output)
        self.assertIn(f"Path:    {latest}", output)
        self.assertIn("Opening in Preview...", output)
        self.assertEqual(len(self.opened), 1)
        self.assertEqual(self.opened[0][:3], ["/usr/bin/open", "-a", "Preview"])
        self.assertEqual(self.opened[0][3:], [
            str(latest / "images" / "image_01.png"),
            str(latest / "images" / "image_02.png"),
        ])

    def test_report_and_finder_options_open_latest_session(self) -> None:
        latest = make_session(
            self.inbox, "phonesaber_20260929_220031_000", png_count=2, report=True,
        )

        result, output, error = self.call_viewer("--finder", "--report")

        self.assertEqual(result, 0, error)
        self.assertIn("Opening latest session in Finder...", output)
        self.assertIn("Opening analysis report...", output)
        self.assertEqual(self.opened, [
            ["/usr/bin/open", "-R", str(latest)],
            ["/usr/bin/open", "-a", "Preview",
             str(latest / "images" / "image_01.png"),
             str(latest / "images" / "image_02.png")],
            ["/usr/bin/open", str(latest / "analysis_report.md")],
        ])

    def test_missing_report_is_reported_and_images_still_open(self) -> None:
        latest = make_session(self.inbox, "phonesaber_20260929_220031_000")

        result, output, error = self.call_viewer("--report")

        self.assertEqual(result, 0, error)
        self.assertIn(f"Analysis report not found: {latest / 'analysis_report.md'}", output)
        self.assertEqual(len(self.opened), 1)
        self.assertIn("Preview", self.opened[0])

    def test_zero_pngs_is_distinguished_from_missing_images_directory(self) -> None:
        latest = make_session(self.inbox, "phonesaber_20260929_220031_000", png_count=0)

        result, output, error = self.call_viewer()

        self.assertEqual(result, 2)
        self.assertIn("Selected images: 0", output)
        self.assertIn("No PNG images were found", error)
        self.assertEqual(self.opened, [])
        self.assertEqual(viewer.selected_pngs(latest), [])

    def test_finder_and_report_options_work_for_a_zero_image_session(self) -> None:
        latest = make_session(
            self.inbox, "phonesaber_20260929_220031_000", png_count=0, report=True,
        )

        result, _output, error = self.call_viewer("--finder", "--report")

        self.assertEqual(result, 2)
        self.assertIn("No PNG images were found", error)
        self.assertEqual(self.opened, [
            ["/usr/bin/open", "-R", str(latest)],
            ["/usr/bin/open", str(latest / "analysis_report.md")],
        ])

    def test_latest_session_without_images_directory_is_not_skipped(self) -> None:
        make_session(self.inbox, "phonesaber_20260929_220028_395", png_count=1)
        latest = make_session(self.inbox, "phonesaber_20260929_220031_000", images=False)

        result, output, error = self.call_viewer()

        self.assertEqual(result, 2)
        self.assertIn("Session: phonesaber_20260929_220031_000", output)
        self.assertIn("Latest session has no images directory", error)
        self.assertIn(str(latest / "images"), error)
        self.assertEqual(self.opened, [])

    def test_missing_inbox_and_empty_inbox_have_distinct_messages(self) -> None:
        missing = self.root / "missing inbox"
        stdout = io.StringIO()
        stderr = io.StringIO()
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            missing_result = viewer.main(["--inbox", str(missing)], opener=self.fake_open)
        self.assertEqual(missing_result, 2)
        self.assertIn("inbox does not exist", stderr.getvalue())

        result, output, error = self.call_viewer()
        self.assertEqual(result, 2)
        self.assertIn("No PhoneSaber sessions were found", error)
        self.assertIn("Session: (none)", output)


if __name__ == "__main__":
    unittest.main()
