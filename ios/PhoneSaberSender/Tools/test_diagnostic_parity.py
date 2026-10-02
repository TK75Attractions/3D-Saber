#!/usr/bin/env python3
"""Debug Recording diagnostics must leave recognition bit-identical.

Compiles the production detector with DiagnosticParityHarness.swift and runs it
over every bundled fixture PNG (including the formal lossless corpus) with
diagnostics off, on, and on with profiling. SWIFT_DETERMINISTIC_HASHING=1 fixes
the Set iteration order that core-line proposal scoring depends on; without it
even two identical runs can differ in the last floating-point bits.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
SOURCES = REPO / "ios/PhoneSaberSender/PhoneSaberSender"
FIXTURES = REPO / "ios/PhoneSaberSenderTests/Fixtures"
HARNESS = REPO / "ios/PhoneSaberSenderTests/DiagnosticParityHarness.swift"


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "requires the macOS Swift toolchain")
class DiagnosticParityTests(unittest.TestCase):
    def test_diagnostics_on_and_off_are_bit_identical_on_every_fixture(self):
        with tempfile.TemporaryDirectory(prefix="phonesaber-parity-") as directory:
            binary = Path(directory) / "parity"
            compiled = subprocess.run(
                ["xcrun", "swiftc", "-O", str(SOURCES / "DetectionCore.swift"),
                 str(SOURCES / "BGRADetection.swift"), str(HARNESS), "-o", str(binary)],
                text=True, capture_output=True, timeout=300)
            self.assertEqual(compiled.returncode, 0, compiled.stderr)
            run = subprocess.run([str(binary), str(FIXTURES)], capture_output=True, timeout=600,
                                 env=dict(os.environ, SWIFT_DETERMINISTIC_HASHING="1"))
            self.assertEqual(run.returncode, 0, run.stderr.decode(errors="replace"))
            summary = json.loads(run.stdout)
        expected = sorted(str(p.relative_to(FIXTURES)) for p in FIXTURES.rglob("*") if p.suffix.lower() == ".png")
        self.assertEqual(sorted(summary["compared"]), expected)
        self.assertGreaterEqual(len(expected), 40)
        self.assertEqual(summary["mismatched"], [])
        self.assertGreater(summary["emitterTraces"], 0)
        self.assertGreater(summary["shadowVerdicts"], 0)


if __name__ == "__main__":
    unittest.main()
