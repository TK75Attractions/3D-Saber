# Android sender crash / hang 監査 — 2026-10-13

REPORT-ONLY。既存のソース、Unity scene / prefab / .meta、ProjectSettings、認識、UDP formatter、正式 corpus は編集していない。追加したリポジトリ内ファイルはこのノートだけ。commit / push / 公式 verify は実施していない（operator の担当）。以下は適用候補であり、実装済みの変更ではない。

## 結論と優先順位

今回の静的監査・ホスト実行では、通常入力から発生する P0 / P1 の native crash、ImageProxy 解放漏れ、デッドロックは確認できなかった。**実機未接続なので「８時間運転に合格」ではない。** アプリの条件付き起動クラッシュ１件と、監査を妨げるホストツールの欠陥２件を記録する。

| 順位 | 重大度 | 場所（監査時の行） | 具体的な失敗 |
| --- | --- | --- | --- |
| 1 | P2・条件付き起動不能 | `PhoneSaber/android/app/src/main/java/jp/phonesaber/sender/MainActivity.kt:142,218,254,280,317` / `PcDiscovery.kt:49` | `destination.xml` に旧型の値が残ると typed getter が ClassCastException。毎回同じキーで起動できなくなる。現行 writer が誤型を作る証拠はない。 |
| 2 | P2・検証不能（端末障害ではない） | `PhoneSaber/android/core/tools/android_host_check.py:69–76` | 全 JVM テストを集める一方、CameraPolicy 等と CameraX のクラスパスがなく、コンパイル失敗。JNI 回帰まで到達できない。 |
| 3 | P3・検証の再現性 | `PhoneSaber/android/core/tools/android_host_check.py:80,87` / `core/Makefile:19` | make は guard 別のディレクトリへ生成するのに、decode は別の固定 `/tmp` バイナリを使用。clean host では欠落、開発機では古いバイナリを黙って使う。 |

P2 は本番前に確認・対応候補、P3 は検証の信頼性改善。順位１の発生頻度は不明。XML が壊れた場合すべてがこの経路になるという主張ではなく、**有効な XML のキーの型が違う場合**を扱う。

## 監査・再現環境

- Git HEAD: `0fcb3e77dcbb11a7e1905946af0e1fc6d1a2952e`。監査用 Android ソースは最初にコピーし、最後の照合で Kotlin / C++ / Gradle / XML の原本との差分なし。
- Mac、Android Studio 同梱 JBR、Gradle 9.8.0、NDK 30.0.16248370、CMake 3.22.1、SDK `android-37.0`、targetSdk 35 / minSdk 29 / arm64-v8a。キャッシュ済み依存を `--offline` で使用。
- throwaway root: `/var/folders/sf/rx78c9vs3_37dby19j8b5xv00000gn/T/phonesaber-crash-audit-cah4eeze`。ソースを編集して検証したのはこの配下だけ。NDK/JNI/PNG fixture は元と同じものをコピー。ローカル SDK の指定だけをコピー内の `local.properties` に作成。
- `adb devices -l` は端末０台。AQUOS の実 OS、Windows PC、CameraX 実カメラ、実 Wi-Fi は今回確認できない。
- 共通 TSV fixture をコピーし忘れた初回は JVM 58件中２件が NoSuchElementException。fixture 追加後は同じ未修正ソースで58/58成功。これは環境準備の不足であり、製品欠陥として数えない。

## 1. 型違いの保存値で起動不能（P2・条件付き）

対象は `MainActivity.kt:142` の wasSending、`:218,317` の autoStartSending、`:254` の mirror flags、`:280` の cameraFps、`:177,186,296,306` の station / ip と、`PcDiscovery.kt:49–50` の preferredPc / preferredPcAddress。

**引き金:** アップデート等で例えば `cameraFps` が `<string>30</string>`、`wasSending` が `<string>true</string>`、`ip` が `<int value="1"/>` のまま残る → アプリ起動 → onCreate の getInt / getBoolean / getString が ClassCastException → 例外を捕捉しないためプロセス終了 → 再起動しても同じ保存値で再発。キー未存在の default は型違いには適用されない。[SharedPreferences API](https://developer.android.com/reference/android/content/SharedPreferences) に記載の例外契約とも一致する。

**証拠と限界:** Android getter と同じ cast をする SharedPreferences interface の JVM Proxy を一時テストに使用。従来 getter のままの adapter では４件中３件が ClassCastException、同じテストは下記 adapter で４件成功。Activity 本体の起動クラッシュは実機で再現していない。現行コードの保存箇所は型を揃えており、実在する移行不具合は発見していない。これは入力異常時の回復不能が確定しているという条件付き指摘。

**提案:** ClassCastException だけを既存 default へ戻す。正しい設定値・キー未存在・認識閾値の Bundle 保存復元は変えない。壊れたキーを削除せず、SecurityException 等の無関係な障害は隠さない。型違い時の手入力IPや反転、fps の既定値復帰は運営者に見えるため、operator がこの方針を選んだときだけ適用する。特に反転設定を黙って既定値へ戻してよいかは運営判断。設定異常を表示して開始を待つ案は追加の挙動変更になるので本ノートの diff には含めない。

**追加する回帰:** 下記 instrumented `PreferenceReadsTest` は隔離した preferences 名を使い、実 SharedPreferences で誤型と正常値を確認する。旧 getter の adapter で失敗、提案で成功する想定。**この実 SharedPreferences テストは APK のコンパイルだけ確認し、実行は未実施。** 実行した fail→pass は上記 JVM Proxy の４件（throwaway のみ）。operator は実機版も実行し、さらに隔離テスト端末で実際の `destination` 設定から Activity 起動まで確認する。

```diff
--- a/PhoneSaber/android/app/src/main/java/jp/phonesaber/sender/MainActivity.kt
+++ b/PhoneSaber/android/app/src/main/java/jp/phonesaber/sender/MainActivity.kt
@@ -139,7 +139,7 @@
             brightness = restored.brightness; dominance = restored.dominance
         }
         sender = LatestUdpSender()
-        resumePolicy = SendingResumePolicy(prefs.getBoolean(SendingResumePolicy.WAS_SENDING_KEY, false))
+        resumePolicy = SendingResumePolicy(prefs.booleanOrDefault(SendingResumePolicy.WAS_SENDING_KEY, false))
         power = getSystemService(PowerManager::class.java)
         battery = getSystemService(BatteryManager::class.java)
         val density = resources.displayMetrics.density
@@ -174,7 +174,7 @@
                     id = View.generateViewId()
                     tag = station
                     text = if (station.isEmpty()) "指定なし" else station
-                    isChecked = station == (prefs.getString("station", "") ?: "")
+                    isChecked = station == (prefs.stringOrDefault("station", "") ?: "")
                 })
             }
             panel.addView(this)
@@ -183,7 +183,7 @@
             hint = "PCのIPv4（例: 192.168.1.10）"
             inputType = InputType.TYPE_CLASS_PHONE
             setSingleLine(true)
-            setText(prefs.getString("ip", ""))
+            setText(prefs.stringOrDefault("ip", ""))
             panel.addView(this)
         }
         Button(this).apply {
@@ -215,7 +215,7 @@
         recoveryStatus = label("")
         Switch(this).apply {
             text = "起動時に送信を自動開始"
-            isChecked = prefs.getBoolean(SendingResumePolicy.AUTO_START_KEY, false)
+            isChecked = prefs.booleanOrDefault(SendingResumePolicy.AUTO_START_KEY, false)
             panel.addView(this)
             setOnCheckedChangeListener { _, enabled ->
                 prefs.edit().putBoolean(SendingResumePolicy.AUTO_START_KEY, enabled).apply()
@@ -251,7 +251,7 @@
         brightnessSlider = slider("認識の閾値（明るさ）", brightness) { brightness = it }
         dominanceSlider = slider("色の優位差", dominance) { dominance = it }
         label("既定値: 明るさ145 / 色の優位差25\n彩度30 / 赤・青共通。変更は停止中に行います。")
-        mirrors = MirrorSettings.load { key, default -> prefs.getBoolean(key, default) }
+        mirrors = MirrorSettings.load { key, default -> prefs.booleanOrDefault(key, default) }
         fun mirrorSwitch(title: String, initial: Boolean, change: (Boolean) -> Unit) = Switch(this).apply {
             text = title
             isChecked = initial
@@ -277,7 +277,7 @@
         // 既定 ON。iPhone の 60fps で撮影→送信が約13ms短縮（2026-10-07 実測）。非対応端末は自動で 30fps。
         fps60Switch = Switch(this).apply {
             text = "カメラ 60fps（低遅延）"
-            isChecked = prefs.getInt("cameraFps", 60) == 60
+            isChecked = prefs.intOrDefault("cameraFps", 60) == 60
             setOnCheckedChangeListener { _, on -> prefs.edit().putInt("cameraFps", if (on) 60 else 30).apply() }
             panel.addView(this)
         }
@@ -293,7 +293,7 @@
                 "PC: ${pc.name}\n${pc.address.hostAddress}（${pc.source}）\n$message"
             updateRecoveryStatus()
         }
-        discovery.setStation(prefs.getString("station", "")?.takeIf { it in stations } ?: "")
+        discovery.setStation(prefs.stringOrDefault("station", "")?.takeIf { it in stations } ?: "")
         stationGroup.setOnCheckedChangeListener { group, id ->
             if (id == View.NO_ID) return@setOnCheckedChangeListener
             val station = group.findViewById<android.widget.RadioButton>(id).tag as String
@@ -303,7 +303,7 @@
             prefs.edit().putString("station", station).apply()
             discovery.setStation(station)
         }
-        val savedAddress = ManualAddress.parse(prefs.getString("ip", "") ?: "")
+        val savedAddress = ManualAddress.parse(prefs.stringOrDefault("ip", "") ?: "")
         manualDestination = savedAddress != null
         discovery.setManual(savedAddress)
     }
@@ -314,7 +314,7 @@
         thermalStatus = power.currentThermalStatus
         power.addThermalStatusListener(ContextCompat.getMainExecutor(this), thermalListener)
         updateHealth()
-        if (resumePolicy.foreground(prefs.getBoolean(SendingResumePolicy.AUTO_START_KEY, false))) {
+        if (resumePolicy.foreground(prefs.booleanOrDefault(SendingResumePolicy.AUTO_START_KEY, false))) {
             automaticResumePending = true
             persistSendingIntent()
             cameraBackoff.reset()
--- a/PhoneSaber/android/app/src/main/java/jp/phonesaber/sender/PcDiscovery.kt
+++ b/PhoneSaber/android/app/src/main/java/jp/phonesaber/sender/PcDiscovery.kt
@@ -46,8 +46,8 @@
     private var selectedKey: String? = null
     private var warning = ""
     private val prefs = context.getSharedPreferences("destination", Context.MODE_PRIVATE)
-    private var preferredPc = prefs.getString("preferredPc", null)
-    private var preferredAddress = prefs.getString("preferredPcAddress", null)
+    private var preferredPc = prefs.stringOrDefault("preferredPc", null)
+    private var preferredAddress = prefs.stringOrDefault("preferredPcAddress", null)
     private var lastRefresh = 0L
     // 現在の探索世代が使っている UDP broadcast 先。IPv4 が変わらない LinkProperties 更新では再探索しない。
     private var broadcastTargets: Set<InetAddress>? = null
--- /dev/null
+++ b/PhoneSaber/android/app/src/main/java/jp/phonesaber/sender/PreferenceReads.kt
@@ -0,0 +1,13 @@
+package jp.phonesaber.sender
+
+import android.content.SharedPreferences
+
+// 更新前の型が残っていても、設定読み込みで起動を落とさない。
+fun SharedPreferences.booleanOrDefault(key: String, default: Boolean): Boolean =
+    try { getBoolean(key, default) } catch (_: ClassCastException) { default }
+
+fun SharedPreferences.intOrDefault(key: String, default: Int): Int =
+    try { getInt(key, default) } catch (_: ClassCastException) { default }
+
+fun SharedPreferences.stringOrDefault(key: String, default: String?): String? =
+    try { getString(key, default) } catch (_: ClassCastException) { default }
--- /dev/null
+++ b/PhoneSaber/android/app/src/androidTest/java/jp/phonesaber/sender/PreferenceReadsTest.kt
@@ -0,0 +1,41 @@
+package jp.phonesaber.sender
+
+import android.content.Context
+import android.content.SharedPreferences
+import androidx.test.platform.app.InstrumentationRegistry
+import org.junit.After
+import org.junit.Assert.*
+import org.junit.Before
+import org.junit.Test
+
+class PreferenceReadsTest {
+    private lateinit var prefs: SharedPreferences
+
+    @Before fun setUp() {
+        prefs = InstrumentationRegistry.getInstrumentation().targetContext
+            .getSharedPreferences("preference-read-regression", Context.MODE_PRIVATE)
+        prefs.edit().clear().commit()
+    }
+
+    @After fun tearDown() { prefs.edit().clear().commit() }
+
+    @Test fun wrongTypesUseExistingDefaults() {
+        prefs.edit().putString("wasSending", "true").putString("cameraFps", "30")
+            .putInt("ip", 1).putBoolean("preferredPc", false).commit()
+        assertFalse(prefs.booleanOrDefault("wasSending", false))
+        assertEquals(60, prefs.intOrDefault("cameraFps", 60))
+        assertEquals("", prefs.stringOrDefault("ip", ""))
+        assertNull(prefs.stringOrDefault("preferredPc", null))
+    }
+
+    @Test fun validAndMissingValuesKeepTheirContract() {
+        prefs.edit().putBoolean("wasSending", true).putInt("cameraFps", 30)
+            .putString("ip", "192.168.1.10").commit()
+        assertTrue(prefs.booleanOrDefault("wasSending", false))
+        assertEquals(30, prefs.intOrDefault("cameraFps", 60))
+        assertEquals("192.168.1.10", prefs.stringOrDefault("ip", ""))
+        assertFalse(prefs.booleanOrDefault("missing", false))
+        assertEquals(60, prefs.intOrDefault("missing", 60))
+        assertNull(prefs.stringOrDefault("missing", null))
+    }
+}
```

## 2. ホスト JNI チェッカーが現在のテストをコンパイルできない（P2）

**引き金:** キャッシュ・JBR・NDK のそろった現行 checkout で `python3 -B PhoneSaber/android/core/tools/android_host_check.py` → JNI library はビルドできるが、Kotlin compile に全 `*Test.kt` を渡す → sources に CameraPolicy / DiscoveryPolicy / PcReplyWatch / ThresholdSettings がない。また CameraPolicyTest の `androidx.camera.core.CameraState` が classpath にない → unresolved reference で終了。

**証拠:** 無変更コピーで実行して exit 1。下記 regression の `test_current_jvm_sources_and_jni_compile_and_run` も修正前に１件失敗。ソース４個を追加し、Gradle が使っている CameraX 1.6.2 AAR の `classes.jar` を一時抽出する提案では、元の58 JVMテスト＋既存 NativeRotationTest ３件＝61件が成功。CameraState は定数の参照に使うだけで、Android framework の動作をホスト上で模倣するものではない。

**提案・回帰:** 下記はソース／classpath の不足だけを修正。認識やテスト期待値を変更しない。新規 `android/test_host_check.py` は JBR/NDK とキャッシュがある検証環境専用。F3 の回帰も同じファイルに含むため、F2 単独では１番目の test selector を使い、全件実行は F3 も適用してから行う。

```diff
--- a/PhoneSaber/android/core/tools/android_host_check.py
+++ b/PhoneSaber/android/core/tools/android_host_check.py
@@ -10,6 +10,7 @@
 import os
 from pathlib import Path
 import subprocess
+import zipfile
 import shutil
 import sys
 import tempfile
@@ -69,8 +70,16 @@
         tests = sorted((ANDROID / "app/src/test/java/jp/phonesaber/sender").glob("*Test.kt"))
         tests.append(ANDROID / "app/src/androidTest/java/jp/phonesaber/sender/NativeRotationTest.kt")
         sources = [JAVA / (name + ".kt") for name in ("RotationHelper", "NativeCore", "Protocol", "MirrorSettings",
-                   "DeviceHealth", "EventRecovery", "SendingNetworkPolicy")]
-        cp = os.pathsep.join(map(str, [stdlib, junit, hamcrest, annotations]))
+                   "DeviceHealth", "EventRecovery", "SendingNetworkPolicy", "CameraPolicy",
+                   "DiscoveryPolicy", "PcReplyWatch", "ThresholdSettings")]
+        # CameraPolicy の定数は、Gradle と同じ CameraX AAR から読む。
+        camera_aar = list((cache / "androidx.camera/camera-core/1.6.2").glob("*/camera-core-1.6.2.aar"))
+        if len(camera_aar) != 1:
+            raise RuntimeError("Need locally cached androidx.camera:camera-core:1.6.2")
+        camera_classes = work / "camera-core.jar"
+        with zipfile.ZipFile(camera_aar[0]) as archive:
+            camera_classes.write_bytes(archive.read("classes.jar"))
+        cp = os.pathsep.join(map(str, [stdlib, junit, hamcrest, annotations, camera_classes]))
         run([jbr / "bin/java", "-cp", os.pathsep.join(map(str, compiler)), "org.jetbrains.kotlin.cli.jvm.K2JVMCompiler",
              "-no-stdlib", "-no-reflect", "-jvm-target", "17", "-classpath", cp, "-d", work / "classes",
              *sources, *tests, CORE / "tools/rotation_benchmark.kt"])
--- /dev/null
+++ b/PhoneSaber/android/test_host_check.py
@@ -0,0 +1,56 @@
+"""JBR/NDK と cached Kotlin/JUnit/CameraX が必要。Android 実機は使わない。"""
+from pathlib import Path
+import os
+import runpy
+import struct
+import subprocess
+import sys
+import tempfile
+import unittest
+from unittest.mock import patch
+import zlib
+
+ANDROID = Path(__file__).resolve().parent
+CHECK = ANDROID / "core/tools/android_host_check.py"
+ROOT = ANDROID.parents[1]
+
+
+class HostCheckTest(unittest.TestCase):
+    def test_current_jvm_sources_and_jni_compile_and_run(self):
+        result = subprocess.run([sys.executable, "-B", str(CHECK)], cwd=ROOT,
+                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
+        self.assertEqual(result.returncode, 0, result.stdout)
+        self.assertIn("OK (", result.stdout)
+
+    def test_benchmark_needs_no_legacy_tmp_decoder(self):
+        def chunk(kind, data):
+            return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)
+        with tempfile.TemporaryDirectory(prefix="host-check-regression-") as directory:
+            directory = Path(directory)
+            png = directory / "tiny.png"
+            png.write_bytes(b"\x89PNG\r\n\x1a\n" +
+                chunk(b"IHDR", struct.pack(">IIBBBBB", 8, 8, 8, 6, 0, 0, 0)) +
+                chunk(b"IDAT", zlib.compress((b"\0" + bytes([20, 20, 20, 255]) * 8) * 8)) + chunk(b"IEND", b""))
+            original = subprocess.check_output
+            decoded = []
+            def clean_host_decode(args, *extra, **kwargs):
+                # 既存の /tmp decoder がある開発機でも、clean host の欠落を再現する。
+                if str(args[0]) == "/tmp/phonesaber-cpp-core/phonesaber-png":
+                    raise FileNotFoundError(str(args[0]))
+                decoded.append(Path(args[0]))
+                return original(args, *extra, **kwargs)
+            previous = Path.cwd()
+            try:
+                os.chdir(ROOT)
+                with patch.object(sys, "argv", [str(CHECK), "--benchmark", str(png)]), \
+                     patch.dict(os.environ, {"BUILD": str(directory / "build")}), \
+                     patch.object(subprocess, "check_output", clean_host_decode):
+                    runpy.run_path(str(CHECK), run_name="__main__")
+            finally:
+                os.chdir(previous)
+            self.assertEqual(len(decoded), 1)
+            self.assertNotEqual(decoded[0], Path("/tmp/phonesaber-cpp-core/phonesaber-png"))
+
+
+if __name__ == "__main__":
+    unittest.main()
```

## 3. benchmark が生成した decoder を使っていない（P3）

**引き金:** F2 の compile 問題を解消した clean 開発機で `--benchmark PNG` → `make all` が `/tmp/phonesaber-cpp-core-guard-on/phonesaber-png` を生成 → 実行側は存在しない `/tmp/phonesaber-cpp-core/phonesaber-png` を開いて FileNotFoundError。旧 decoder が残っている機械では、別 build / guard の decoder を参照し検証が環境依存になる。今回の Mac は旧 decoder が実際に存在し、自然実行は成功したので、自然な ENOENT を再現したとは扱わない。

**証拠:** F2 に同梱する `test_benchmark_needs_no_legacy_tmp_decoder` は固定旧パスだけに FileNotFoundError を注入して clean host を再現。make の出力をその test の一時ディレクトリへ向けて実行した結果、旧実装は１件 ERROR。下記提案後は通常実行と benchmark の２件とも成功（8×8 の新規 synthetic PNG、corpus 非使用）。

**提案:** make の BUILD をセッションの一時領域へ明示し、そこに生成した decoder を使用。core の build switch、threshold、比較条件は変えない。回帰テストは F2 の新規 `test_host_check.py` の２番目。

```diff
--- a/PhoneSaber/android/core/tools/android_host_check.py
+++ b/PhoneSaber/android/core/tools/android_host_check.py
@@ -86,14 +86,15 @@
         java = [jbr / "bin/java", "-Djava.library.path=" + str(work), "-cp", str(work / "classes") + os.pathsep + cp]
         run([*java, "org.junit.runner.JUnitCore", *("jp.phonesaber.sender." + t.stem for t in tests)])
         if args.benchmark:
-            run(["make", "-C", CORE, "all"])
+            core_build = work / "core-build"
+            run(["make", "-C", CORE, "all", "BUILD=" + str(core_build)])
             for png in args.benchmark:
                 # Raw lossless fixture as a 640x480 camera plane whose 90-degree result is the original.
                 # Use the existing decoder without color management. Padding and decoding are outside timing.
                 import struct
                 png = png.resolve()
                 width, height = struct.unpack(">II", png.read_bytes()[16:24])
-                bgra = subprocess.check_output(["/tmp/phonesaber-cpp-core/phonesaber-png", "--decode-bgra", str(png)])
+                bgra = subprocess.check_output([str(core_build / "phonesaber-png"), "--decode-bgra", str(png)])
                 camera_width, camera_height = height, width
                 stride = camera_width * 4 + 16
                 raw = bytearray(stride * camera_height)
```

## 他の要求項目の監査結果と未解決リスク

ここからは確定した欠陥ではなく、修正 diff を出す根拠が不足している実機確認事項。リスクを避けるためだけの認識・閾値・自動fps変更は提案しない。

| 対象 / file:line | 確認できたこと | 未確認・実機での引き金 |
| --- | --- | --- |
| Permission / restore: `MainActivity.kt:89,135,311,333,438` | result は foreground と送信意思を確認。閾値は Bundle で保存。送信意思は preferences に保存。 | 権限 dialog 中の recreate、拒否→設定で再許可、１回限り許可の失効、OS kill 後の task restore。permissionPending 自体は保存されないので、再生成時の再 launch を実機確認する。 |
| autoStart と config: `EventRecovery.kt:11–16`, `MainActivity.kt:142,317` | evaluatedStartup は instance 内の値。 | autoStart ON→停止→ダークモード／文字サイズ変更で Activity 再生成すると startup 判定を再評価する。停止維持を config 復帰にも適用するかは仕様判断。現在の policy を新規 instance にしたホストモデルでは再開が true。実 Activity は未再現。operator が期待を決めてから instance state の追加を検討。 |
| CameraX lifecycle: `CameraSession.kt:69,121–160,193–211`, `MainActivity.kt:443–455` | main の停止が世代を進め、analyzer は JNI 前後で token 確認。core 作成・処理・破棄は単一 executor。onStop→onDestroy は stop→cleanup queue→shutdown の順で、通常の二段停止で shutdown 後 execute にはならない。 | bind pending 中の Home/recreate、camera taken/disabled、遅い分析中の終了、CameraX の analyzer dispatch と shutdown の競合。SDK 内の rejection handling も含め実機検証が必要。現呼び出し順で確定した RejectedExecutionException crash は見つからない。 |
| ImageProxy: `CameraSession.kt:121–148` | early return、stale token、Exception、正常結果は同じ finally を通る。JNI は同期で戻ってから close。 | framework が analyzer に渡す前の proxy、close 自体の OEM 例外、プロセスが native で停止するケースはこの finally だけでは証明できない。 |
| JNI lifetime / bounds: `NativeCore.kt:6–20`, `phonesaber_jni.cpp:77–91,112–126`, `rgba_rotation.hpp:26–50` | camera path は position/remaining/direct/stride/dimension を検証。最終行 padding 不足は tight copy。native は入力 pointer を保持せず、rotation scratch だけ所有。 | Android ART / bionic / OEM RGBA layout は未確認。legacy process は capacity を契約とし limit/position を使わないが、現 RotationHelper は slice/tight buffer を渡しているため、本番カメラの欠陥とは扱わない。 |
| JNI exceptions / refs: `phonesaber_jni.cpp:15–40,63–126` | std::exception を Java 例外に変換。NewStringUTF / NewObject / array store の失敗で追加の unsafe JNI 呼び出しを続けない。text/value/type local refs を解放。native thread attach はない。 | Java OOM、native bad_alloc、constructor の例外の fault injection は未実施。メモリ不足から常に復旧できるという保証はない。HotSpot の -Xcheck:jni 成功を ART CheckJNI の代用にはしない。 |
| unexpected resolution: `rgba_rotation.hpp:28–38`, `core/src/pipeline.cpp:321–329` | dimension/stride の不正入力を拒否し、計算には size_t/int64 を使用。sanitizer のランダム回転は成功。 | 有効な巨大画像なら多量の memory を使う。32768上限は運用上の memory budget ではない。AQUOS の実選択サイズ・CameraX 変換バッファを実測する。解像度を制限する変更は認識入力の挙動変更なので実装しない。 |
| Socket threading: `LatestUdpSender.kt:24–93`, `PcDiscovery.kt:182–225,342–359` | sender は configure/offer/send/close を同じ gate で直列化、nonblocking DatagramChannel。探索は１世代１ receive thread、停止は socket.close と volatile generation。 | sender gate 内の OS 呼び出しが OEM / Binder で長時間かかると main の configure/stop も待つ。現環境にネットワーク故障／遅延注入はなく、hang と断定できない。thread close 後の終了時間も実機計測。 |
| ENETUNREACH / EPERM / subnet: `LatestUdpSender.kt:79–83`, `PcDiscovery.kt:92–96,171–173,190–224` | sender exception は表示し channel を破棄、次の fresh frame で再作成。再送 FIFO はない。探索 receive error は worker 終了、最大30秒の定期 restart がある。directed broadcast の１個の送信失敗は他の宛先を妨げない。 | hotspot 再作成、DHCP/subnet/IPの変更、VPN、省電力、Doze、PC F/W。OS send 成功は Unity 到達保証ではない。手入力旧 IP は自動で新 IP に変更されない。send=0（満杯）は破棄され、例外表示は出ない。 |
| Locks / screen off: `MainActivity.kt:40–43,354–357,431–449`, `PcDiscovery.kt:142,342–349` | WAKE_LOCK manifest 宣言あり。低遅延 lock は stop で release、multicast lock は endDiscovery で release。CPU WakeLock は生成しない。keep-screen flag は stop で解除。 | screen off / Home の送信停止・前面復帰は既定設計。WifiLock はユーザーの Wi-Fi OFF、Doze 制約や thermal を無効化する保証ではない。acquire/release の OS 例外や lifecycle の異常終了は実機で確認。 |
| memory / logs: `DeviceHealth.kt:52–78`, `PcDiscovery.kt:165–169,208,230,298`, `CameraSession.kt:41,171–177` | health は５秒 / 最大1200件、found と resolve queue は16件、expiry は取消分を queue から除く。回転 scratch は高水位１領域。log は thermal 変化時のみ。８時間相当の simulated health 入力でも上限内。 | NSD present はその探索世代内の発見名を持つ（30秒毎に作り直す）。UDP/NSD の main.post backlog 自体に件数上限はない。応答 flood / メイン停止、CameraX/native allocator の RSS、高水位 scratch、繰り返す Activity の保持は JVM unit では測れない。 |
| thermal / battery saver: `CameraSession.kt:83–118`, `DeviceHealth.kt:40–45`, `MainActivity.kt:104–116,385–396` | AE固定要求と低fps/thermal の表示、stalled camera の retry（1〜30秒、15分上限）あり。自動fps/recognition変更はしない。 | HAL が bind 後にfps低下、USB充電と箱内の発熱、省電力でfps変化。15分復旧できなければ停止→開始を待つ。注意表示は時々刻々の実測値であり、安定運転保証ではない。 |
| release / R8: `app/build.gradle.kts:51–56`, `phonesaber_jni.cpp:20,22,63` | 現 release は minify=false。NDK の共有ライブラリと c++_shared が APK に入る。JNI symbol / NativeResult の rename による現行 crash の根拠なし。 | 将来 minify を ON にするなら NativeCore の native method と JNI が FindClass/GetMethodID する NativeResult のクラス名・constructor の keep、および release 実機JNIテストが必要。今は rules を増やさない。release APK は unsigned、実配布署名とインストールは未確認。 |
| targetSdk / permissions: `AndroidManifest.xml:3–13`, `app/build.gradle.kts:28–35` | アプリは Activity のみ。FGS/AlarmManager/Wi-Fi scan/P2P/hotspot制御を使わない。現経路に FGS type / exact alarm / Nearby runtime permission を追加する根拠なし。 | Android 13/14/15 と実 sense9 の API を確認。将来 background camera を追加するなら camera FGS type/while-in-use制約が別途必要。API35 edge-to-edge は insets listener が対応するが permission UI や小画面は要確認。targetSdk 上げ・ライブラリ更新はこの監査で行わない。 |
| 同期保存 / ANR: `MainActivity.kt:328–330` | 送信意思の commit は意図的な耐異常終了設計。lint ApplySharedPref warning はこれを指す。 | storage stall が main を止める可能性は実機未測定。apply へ置換すると停止意思の永続化を弱めるため、lint を消すだけの修正をしない。 |

WifiLock の有効条件と必要権限は [WifiLock API](https://developer.android.com/reference/android/net/wifi/WifiManager.WifiLock)、[WifiManager LOW_LATENCY](https://developer.android.com/reference/android/net/wifi/WifiManager#WIFI_MODE_FULL_LOW_LATENCY) に照合。[Doze](https://developer.android.com/training/monitoring-device-state/doze-standby) の制限、[Nearby Wi-Fi API の対象一覧](https://developer.android.com/develop/connectivity/wifi/wifi-permissions)、[FGS の API 別変更](https://developer.android.com/develop/background-work/services/fgs/changes)、[Android15 target35 の変更](https://developer.android.com/about/versions/15/behavior-changes-15) も参照した。これらと現在の利用APIを突き合わせた判断であり、sense9 の OEM 実装を保証するものではない。JNI の pending exception / local ref の照合は [JNI tips](https://developer.android.com/ndk/guides/jni-tips)、proxy close の責任は [CameraX image analysis](https://developer.android.com/media/camera/camerax/analyze) を参照。

## 実行した検証（成功 / 失敗を区別）

以下の `cd` はすべて throwaway。Android/JBR の値は上記環境。依存やcorpusは改変していない。

```sh
export JAVA_HOME='/Applications/Android Studio.app/Contents/jbr/Contents/Home'
export ANDROID_HOME="$HOME/Library/Android/sdk"
cd '<throwaway>/PhoneSaber/android'
./gradlew --offline --no-daemon -Pkotlin.compiler.execution.strategy=in-process --console=plain \
  :app:testDebugUnitTest :app:assembleDebug :app:assembleRelease :app:lintDebug :app:assembleDebugAndroidTest
```

- 未修正 baseline（TSV をそろえた後）: **BUILD SUCCESSFUL / JVM 58件成功**。Debug、unsigned Release、androidTest APK と lint 成功。lint **0 errors / 25 warnings**（commit、targetSdk、orientation、ABI、backup、UI文字列等）。更新・UI整理などの diff は出していない。証跡 `gradle-complete.log`。
- test APK の `assets/lossless_regression_manifest.json` を ZipFile で読み、コピーした正式 manifest と bytes 一致を確認。Debug / Release APK とも arm64 の `libphonesaber_jni.so` / `libc++_shared.so` を含む。**lossless 40件を端末で実行した結果ではない。**
- F1 の throwaway Proxy 回帰４件: 旧型 adapter **３件失敗（ClassCastException）、１件成功** → 提案 **４件成功**。`prefs-before.log` / `prefs-after.log`。正しい設定値と absent default も検証。
- F1 提案＋instrumented test の compile＋８時間相当 HealthMeter test: `:app:testDebugUnitTest :app:assembleDebug :app:assembleRelease :app:assembleDebugAndroidTest` **BUILD SUCCESSFUL / JVM 63件成功**。内訳 baseline58 + Proxy4 + simulated health1。health は60fps×８時間＝1,728,000入力を約0.12秒で与え、直近サンプル数301以下、6秒後失効を確認。実時間の８時間試験ではない。`proposal-build.log`。新規 instrumented２件は compile のみ。
- 未修正 `android_host_check.py`: native compile の後 Kotlin unresolved reference で **exit 1**。`host-audit.log`。
- F2 提案のホスト checker: **JNI build + JUnit 61件成功**。ホスト回転比較（row padding、非zero position、last row truncated、0/90/180/270、色状態の一致、不正plane）はここに含む。`host-patched.log`。
- 追加 Python 回帰: F2 test は旧版で **FAIL 1**（`host-regression-before.log`）、F3 test は旧decoder不在注入で **ERROR 1**（`benchmark-clean-before.log`）。両方の提案後は **２件成功**、synthetic tiny PNG の benchmark も実行（`host-regression-after.log`）。自然実行の旧decoderがある環境でも benchmark は一度走ったが、その成功を clean host の保証とは扱わない（`benchmark-before.log`）。その１回だけ make 既定の `/tmp/phonesaber-cpp-core-guard-on` にホスト生成物を作成した。以後の回帰は BUILD を一時領域へ指定して隔離。
- ホスト checker の JUnit 起動に `-Xcheck:jni` を加えた一時 runner: **61件成功、JNI チェックエラーなし**。JBR の native-access / Unsafe deprecation warning はある。`host-regression/check-jni.log`。Android ART の CheckJNI は未実行。

```sh
cd '<throwaway>'
make -C PhoneSaber/android/core test BUILD="$TMPDIR/phonesaber-crash-audit-core" PYTHON=python3
clang++ -std=c++17 -O1 -g -fsanitize=address,undefined -fno-omit-frame-pointer \
  -IPhoneSaber/android/core/include -IPhoneSaber/android/app/src/main/cpp \
  rotation-audit.cpp -o rotation-audit
./rotation-audit
```

- make: **core tests passed / Python parity-comparison unit 4件成功**。Mac 上の C++ public API の回帰、正式PNG corpusの40件parityを実行したことにはならない。`core-test.log`。
- 一時 C++ harness: `RgbaRotation` を2000ランダムplane×４方向＝**8000正常回転**で各画素を照合。stride は width×4 より最大20byte大きい、最終paddingなし。不足buffer / 不正dimension の **2004件拒否**。AddressSanitizer / UndefinedBehaviorSanitizer で **成功、報告なし**。`rotation-asan.log`。JNI全体やcore全体のsanitizer試験ではない。
- 上記 unified diff は適用先を新たな一時コピーに戻し、`git apply --check` と実適用で確認。原本は適用していない。

**実行していない:** `verify_phone_saber.sh`（operator担当）、connectedAndroidTest / 新規実SharedPreferences回帰 / NativeCoreLosslessTest正式40件 / NativeRotationTestのART実行、AQUOS実機のPermission/CameraX/NSD/Wi-Fi/lock/Doze/省電力/thermal、Windows受信・firewall・再接続、署名Release実インストール、実時間８時間連続運転。unit/host 成功でこれらを補完したとは扱わない。

## 追加すべき instrumented tests（優先順）

既存のinstrumented NativeRotationTest / NativeCoreLosslessTestに加え、次を隔離した実機・テストアプリデータで実施する。大半は unit test が Android lifecycle / HAL / Binder / socket の実装を持たないため見えない。

1. **Lifecycle / ImageProxy 解放:** analyzer 内を latch で止め、bind pending中／JNI処理中に onStop→onDestroy/recreate。既に配送された各proxyのcloseが１回、停止世代の送信なし、cleanup後のexecutor終了、native destroy１回を検証。analyzerの途中throw、core未作成early return、期限処理と停止の同時刻も含む。camera/proxy factory の test seam は別途設計し、この監査では追加しない。
2. **Permission / process restore:** allow / deny / don't ask again / one-time、権限結果待ちのrecreate、権限設定から復帰、taskをbackgroundにして `am kill` 後task restore。保存した閾値とmirror、送信意思を検証。force-stop は通常のprocess deathとは別テスト。autoStart ON→停止→config再生成の期待値はoperatorが決める。
3. **実 SharedPreferences:** F1 の隔離したファイルで実型違いを確認。その後、別テスト用インストールに実 destination の誤型をseedしてActivity起動を確認（運用端末の設定を壊す試験はしない）。正常値で更新・再起動して継続することも確認。
4. **Network / latest-wins:** loopback receiver＋送信endpointのfault injectionで ENETUNREACH、EPERM、send=0、bind例外、closeとconfigureの競合を再現。次のfreshで復帰、pending1個、180msより古い送信なし、payload bytes/5005/5006不変、停止世代の送信なしを確認。実Wi-Fi切替・DHCP/IP/subnet変更は別に走らせる。OS send済みのin-flight datagramを停止後受信しただけでは失敗判定しない。
5. **Camera possession / HAL:** camera privacy OFF / 他appがcameraを獲得 / process recreateの連続100回。復旧猶予後fpsが戻る、失敗継続15分の上限、停止時lock/CameraX usecase/native/executorの解放、旧handlerがUIを更新しないことを確認。
6. **JNI ART / release:** CheckJNI、direct bufferのposition/limit/rowStride・heap/短いplane・pixelStride/rotation/threshold境界・closed core・実RGBA値。実APKのNativeResult ctorとnative method linkageを確認。lossless正式40件はexpectedを変えず全件実行。OOM/constructor-failureは専用probeで注入し、pending例外下のJNI呼び出しとlocal refsを確認。
7. **８時間実測:** 実Windows受信、赤青点灯棒、同じ箱/USB電源/明るさ/温度でdebuggerなしの署名Release。15分ごとにJava/native/PSS、thread/FD、lock、CameraXフレーム間隔、解析/送信fps、JNI中央値、battery/thermalとWindows受信停止を記録。network切断/再接続を数回含める。warm-up後のmemory/thread/FDが単調増加しないか、15分のmanualだけではなく別枠で判断する。

## AQUOS sense9 + Windows: 15分の manual script

事前にWindowsの正式launcher/Unity受信を起動し、既存runbookどおり5005–5007のfirewall許可と同じWi-Fiを確認する。テスト用sense9または設定を記録した端末を使い、実OS/API・APK版・署名・台・手入力IP・mirror・fpsをメモ。USBデバッグとログ採取を準備。正常／異常のたびに時刻と画面・Windows結果を記録する。認識thresholdは変更しない。

```sh
adb shell getprop ro.build.version.release
adb shell getprop ro.build.version.sdk
adb shell getprop ro.build.fingerprint
adb logcat -v threadtime > aquos-15min-logcat.txt
# 別terminalで baseline / 中間 / 終了時に採取
adb shell dumpsys meminfo jp.phonesaber.sender
adb shell dumpsys power
adb shell dumpsys wifi
adb shell dumpsys media.camera
```

| 時刻 | 操作 | 合格目安 / 記録 |
| --- | --- | --- |
| 0:00–2:00 | 停止状態から開始。権限未付与のテストinstallで一度拒否、設定で許可して復帰→開始。赤青棒を動かす。 | crash/ANRなし。PC名/IP/台が正しい。許可後cameraが起動しWindows両色に届く。解析と送信fps、寸法、JNI、電池/thermalを記録。送信fpsだけで受信成功と判断しない。 |
| 2:00–4:00 | 送信中にHome→復帰、他のcamera app→復帰、電源ボタンで20秒screen off→unlock。 | backgroundではcamera/送信を解放し、前面復帰で送信意思どおり再開。旧Activityのwarning更新やcrashなし。自然に止まることは本設計の仕様。 |
| 4:00–6:00 | 送信中にダークモード等config変更（縦固定なので端末回転だけではrecreateにならない場合あり）。次に停止してconfig変更。 | 送信中の閾値/設定/意思の復元。停止後はautoStart OFFで停止維持。ONの場合の再生成結果も記録して仕様判断。開始/停止10回、復帰に二重camera/workerが残らないか確認。 |
| 6:00–9:00 | Wi-Fi OFF→ON。同じAPへ復帰。可能ならテスト用hotspotを再作成してsubnet/IP変更。Windows受信側も再起動。 | 落ちずに再探索。自動探索は現在IPの同じ台へ復帰。通常30秒の探索更新＋camera起動を見込んで待つ。旧手入力IPが残る場合は期待どおり手動修正。異なるPCに意図せず送らない。warningとWindows復帰時刻を記録。 |
| 9:00–11:00 | sense9 の省電力ON→赤青棒→OFF。USB充電状態と箱の通風も確認。人工的に加熱しない。 | fps / thermal / latency表示を記録。大幅低下やcamera停止から復帰するか、メッセージが出るか確認。Dozeをこの２分で検証したとは扱わない。 |
| 11:00–13:00 | アプリをHomeへ出して `adb shell am kill jp.phonesaber.sender`、履歴から復帰（foregroundのままではam killされない場合あり）。送信中の意思と、停止した後のケースを分ける。 | PIDが変わったこと、意思/設定/閾値restoreを記録。autoStart OFFで停止後は再開しない。crash/設定lostなし。force-stopは使わず、cold launchとtask restoreを混同しない。 |
| 13:00–15:00 | 両色を１分送り、最後に停止→Home。baselineと同じdumpsysを採取しlogcatを終了。 | 終了後camera usecaseとWi-Fi/multicast lockの保持なし。停止後、既送信datagramの短い飛行時間を除きWindows受信停止。ANR/FATAL/tombstoneなし。PSS/FD/thread増加は記録し、warm-up差だけでleak断定しない。 |

**15分では確認できないこと:** 本物のthermal equilibrium、電池劣化、OEM長時間省電力、Doze、local-refの長時間stress、8時間のmemory/FD/thread leak。これらは別枠の実機soakと、必要に応じ専用テスト端末の `cmd deviceidle force-idle` 等で確認する。運用設定を自動で変える提案・実装はしない。

## operator に渡す未決事項

- F1 の型異常時に既定値へ復帰する方針（特にmirror・宛先）を採用するか。現在の本番設定で誤型が存在する証拠はない。
- autoStart ONで停止後、config recreateが「起動」に含まれるか。現モデルは再開する。仕様を決めて実Activityの回帰を先に作る。
- ART / 正式40件 / CameraX lifecycle / 実network / Windows / ８時間soakは未完。sense9の実firmwareと当日構成で確認しない限り本番の安定性は保証できない。
- patchは監査時スナップショットに対するもの。他jobの変更が入った後は新しい行と呼び出しを再レビューし、公式verifyと必要なinstrumented testをoperatorが実行してから採否・commitする。
