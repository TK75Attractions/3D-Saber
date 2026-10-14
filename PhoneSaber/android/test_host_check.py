"""JBR/NDK と cached Kotlin/JUnit/CameraX を使うホスト検証の回帰。"""
from pathlib import Path
import runpy
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zlib

CHECK = Path(__file__).resolve().parent / "core/tools/android_host_check.py"
ROOT = CHECK.resolve().parents[4]


class HostCheckTest(unittest.TestCase):
    def test_current_jvm_sources_and_jni_compile_and_run(self):
        result = subprocess.run([sys.executable, "-B", str(CHECK)], cwd=ROOT,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn("OK (", result.stdout)

    def test_benchmark_uses_the_decoder_it_built(self):
        def chunk(kind, data):
            return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)

        with tempfile.TemporaryDirectory(prefix="host-check-regression-") as directory:
            png = Path(directory) / "tiny.png"
            png.write_bytes(b"\x89PNG\r\n\x1a\n" +
                chunk(b"IHDR", struct.pack(">IIBBBBB", 8, 8, 8, 6, 0, 0, 0)) +
                chunk(b"IDAT", zlib.compress((b"\0" + bytes([20, 20, 20, 255]) * 8) * 8)) + chunk(b"IEND", b""))
            original_run = subprocess.run
            original_decode = subprocess.check_output
            builds = []
            decoded = []

            def record_build(args, *extra, **kwargs):
                if str(args[0]) == "make":
                    builds.extend(Path(str(arg)[6:]) for arg in args if str(arg).startswith("BUILD="))
                return original_run(args, *extra, **kwargs)

            def clean_host_decode(args, *extra, **kwargs):
                # 旧 /tmp バイナリがある開発機でも、clean host の欠落を再現する。
                if str(args[0]) == "/tmp/phonesaber-cpp-core/phonesaber-png":
                    raise FileNotFoundError(str(args[0]))
                decoded.append(Path(args[0]))
                return original_decode(args, *extra, **kwargs)

            with patch.object(sys, "argv", [str(CHECK), "--benchmark", str(png)]), \
                 patch.dict("os.environ", {"BUILD": str(Path(directory) / "build")}), \
                 patch.object(subprocess, "run", record_build), \
                 patch.object(subprocess, "check_output", clean_host_decode):
                runpy.run_path(str(CHECK), run_name="__main__")
            self.assertEqual(len(builds), 1)
            self.assertEqual(decoded, [builds[0] / "phonesaber-png"])
            self.assertFalse(builds[0].exists())


if __name__ == "__main__":
    unittest.main()
