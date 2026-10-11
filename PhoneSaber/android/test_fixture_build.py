"""Android SDK/JDK が必要。lint と正式fixture APKを同じタスクグラフで検証する。"""

from pathlib import Path
import subprocess
import unittest
import zipfile


class FixtureBuildTest(unittest.TestCase):
    def test_lint_and_fixture_apk_build_together(self):
        android = Path(__file__).resolve().parent
        # Kotlin daemon の外部キャッシュを使わず、サンドボックス内でも同じ検証を行う。
        result = subprocess.run(
            [str(android / "gradlew"), "-Pkotlin.compiler.execution.strategy=in-process",
             "--console=plain", ":app:lintDebug", ":app:assembleDebugAndroidTest"],
            cwd=android, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
            check=False,
        )
        self.assertEqual(result.returncode, 0, result.stdout)
        manifest = android.parent / "ios/PhoneSaberSender/Tools/lossless_regression_manifest.json"
        apk = android / "app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk"
        with zipfile.ZipFile(apk) as archive:
            self.assertEqual(archive.read("assets/lossless_regression_manifest.json"), manifest.read_bytes())


if __name__ == "__main__":
    unittest.main()
