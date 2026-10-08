package jp.phonesaber.sender

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.junit.runners.Parameterized
import java.nio.ByteBuffer
import java.security.MessageDigest
import kotlin.math.hypot
import kotlin.math.min

// 本番NativeCoreの返り値にはない元画像端点・候補種別を、Debug限定JNIで読む。
private object NativeFixtureProbe {
    init { System.loadLibrary("phonesaber_jni") }
    external fun analyze(buffer: ByteBuffer, width: Int, height: Int, rowStride: Int): String
}

@RunWith(Parameterized::class)
class NativeCoreLosslessTest(private val path: String, private val expected: JSONObject) {
    @Test
    fun selectedOutputsAndFormalManifestMatch() {
        val png = assets.open(path).use { it.readBytes() }
        assertEquals("$path PNG SHA-256", expected.getString("sha256"), sha256(png))
        // 色管理をしない自前デコード(Mac の PNG CLI と同じ無変換 RGBA)。cICP/gAMA 付きの
        // 動画由来 fixture も BitmapFactory のように sRGB へ変換されない。
        val decoded = RawPng.decodeRgba(png)
        val width = decoded.width
        val height = decoded.height
        assertEquals("$path width", expected.getInt("width"), width)
        assertEquals("$path height", expected.getInt("height"), height)
        val rgba = decoded.rgba
        // PNG CLIの無変換RGBAとの全画素一致。色管理・premultiplyによる変化も検出する。
        assertEquals("$path decoded RGBA SHA-256", expected.getString("rgbaSha256"), sha256(rgba))
        for (padding in listOf(0, 13)) {
            val stride = width * 4 + padding
            val buffer = ByteBuffer.allocateDirect(stride * height)
            for (y in 0 until height) {
                buffer.put(rgba, y * width * 4, width * 4)
                repeat(padding) { buffer.put(0xa5.toByte()) }
            }
            buffer.flip()
            // 0度のカメラ経路と同じslice/stride。fixture間に予測や端点順序の履歴を持ち越さない。
            val pixels = RotationHelper().orient(buffer, width, height, stride, 4, 0)
            val legacy = NativeCore(MirrorSettings()).use { core ->
                core.process(pixels, 1.0, 145, 25)
            }
            val results = NativeCore(MirrorSettings()).use { core ->
                core.processCamera(buffer, width, height, stride, 4, 0, 1.0, 145, 25)
            }
            assertArrayEquals("$path stride=$stride camera/legacy JNI", legacy, results)
            val analysis = JSONObject(NativeFixtureProbe.analyze(
                pixels.buffer, pixels.width, pixels.height, pixels.rowStride))
            val expectedColors = expected.getJSONObject("colors")
            val context = "$path stride=$stride"
            assertEquals("$context result count", colors.count {
                !expectedColors.getJSONObject(it).isNull("selected")
            }, results.size)
            for ((index, color) in colors.withIndex()) {
                val oracle = expectedColors.getJSONObject(color)
                val actual = analysis.getJSONObject(color)
                assertEquals("$context $color selected endpoints",
                    endpoints(oracle, "selected"), endpoints(actual, "selected"))
                assertEquals("$context $color candidate type",
                    nullableString(oracle, "candidateType"), nullableString(actual, "candidateType"))
                val result = results.singleOrNull { it.color == index }
                if (oracle.isNull("selected")) {
                    assertNull("$context $color absent", result)
                } else {
                    assertNotNull("$context $color detected", result)
                    result!!
                    assertTrue("$context $color fresh", result.fresh)
                    assertFalse("$context $color predicted", result.predicted)
                    assertEquals("$context $color port", if (index == 0) Ports.RED else Ports.BLUE, result.port)
                    assertEquals("$context $color default 1920x1080 payload",
                        oracle.getString("payload"), result.text)
                }
            }
            for (fixture in fixtures.filter { it.getString("path") == path }) {
                checkFormalFixture(fixture, analysis, context)
            }
        }
    }

    private fun checkFormalFixture(fixture: JSONObject, analysis: JSONObject, context: String) {
        val label = "$context formal ${fixture.getString("name")}"
        val color = if (fixture.getString("color") == "RED") "red" else "blue"
        val actual = analysis.getJSONObject(color)
        assertEquals("$label detected", fixture.getBoolean("expectedDetected"), !actual.isNull("selected"))
        assertEquals("$label candidate type", nullableString(fixture, "expectedCandidateType"),
            nullableString(actual, "candidateType"))
        val expectedPoints = endpoints(fixture, "expectedEndpoint")
        if (expectedPoints != null) {
            val actualPoints = requireNotNull(endpoints(actual, "selected"))
            fun distance(a: Int, b: Int): Double = hypot(
                (actualPoints[a] - expectedPoints[b]).toDouble(),
                (actualPoints[a + 1] - expectedPoints[b + 1]).toDouble())
            // 既存evaluatorと同じ、順序反転を許す2端点の平均ユークリッド距離。
            val error = min((distance(0, 0) + distance(2, 2)) / 2,
                (distance(0, 2) + distance(2, 0)) / 2)
            assertTrue("$label endpoint error ${error}px", error <= fixture.getDouble("endpointTolerancePx"))
        }
        val rejected = actual.getJSONArray("rejectedCandidateTypes")
        val rejectedTypes = (0 until rejected.length()).map { rejected.getString(it) }.toSet()
        val expectedRejected = fixture.getJSONArray("expectedRejectedCandidateTypes")
        for (i in 0 until expectedRejected.length()) {
            val type = expectedRejected.getString(i)
            assertTrue("$label rejected $type", type in rejectedTypes)
        }
    }

    companion object {
        private val colors = listOf("red", "blue")
        private val assets get() = InstrumentationRegistry.getInstrumentation().context.assets
        private fun document(name: String) = assets.open(name).bufferedReader().use {
            JSONObject(it.readText())
        }
        private val manifest = document("lossless_regression_manifest.json")
        private val fixtures = manifest.getJSONArray("fixtures").let { array ->
            (0 until array.length()).map { array.getJSONObject(it) }
        }

        @JvmStatic
        @Parameterized.Parameters(name = "{0}")
        fun parameters(): Collection<Array<Any>> {
            val oracle = document("native_fixture_expectations.json")
            assertEquals(1, manifest.getInt("schemaVersion"))
            assertEquals(1, oracle.getInt("schemaVersion"))
            assertEquals(1920, oracle.getInt("outputWidth"))
            assertEquals(1080, oracle.getInt("outputHeight"))
            assertEquals(40, fixtures.size)
            assertEquals(fixtures.size, oracle.getInt("formalFixtureCount"))
            val images = oracle.getJSONArray("images")
            val records = (0 until images.length()).map { images.getJSONObject(it) }
            val paths = records.map { it.getString("path") }
            assertEquals("duplicate oracle paths", paths.size, paths.toSet().size)
            assertEquals("all formal PNGs covered", fixtures.map { it.getString("path") }.toSet(), paths.toSet())
            for (fixture in fixtures) {
                val record = records.single { it.getString("path") == fixture.getString("path") }
                assertEquals(fixture.getString("name"), fixture.getString("sha256"), record.getString("sha256"))
            }
            return records.map { arrayOf<Any>(it.getString("path"), it) }
        }

        private fun nullableString(value: JSONObject, key: String): String? =
            if (value.isNull(key)) null else value.getString(key)

        private fun endpoints(value: JSONObject, key: String): List<Int>? {
            if (value.isNull(key)) return null
            val points: JSONArray = value.getJSONArray(key)
            assertEquals("two endpoints", 2, points.length())
            return (0..1).flatMap { i ->
                val point = points.getJSONObject(i)
                listOf(point.getInt("x"), point.getInt("y"))
            }
        }

        private fun sha256(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256")
            .digest(bytes).joinToString("") { "%02x".format(it.toInt() and 255) }
    }
}

/** 8-bit RGB/RGBA、非インターレースの PNG を無変換の RGBA に展開する(テスト専用)。 */
private object RawPng {
    class Image(val width: Int, val height: Int, val rgba: ByteArray)

    fun decodeRgba(png: ByteArray): Image {
        val input = java.io.DataInputStream(png.inputStream())
        input.skipBytes(8)
        var width = 0; var height = 0; var colorType = -1
        val idat = java.io.ByteArrayOutputStream()
        while (true) {
            val length = input.readInt()
            val type = ByteArray(4).also { input.readFully(it) }.toString(Charsets.US_ASCII)
            val data = ByteArray(length).also { input.readFully(it) }
            input.readInt() // CRC
            when (type) {
                "IHDR" -> {
                    val header = java.nio.ByteBuffer.wrap(data)
                    width = header.int; height = header.int
                    val bitDepth = header.get().toInt(); colorType = header.get().toInt()
                    header.get(); header.get()
                    val interlace = header.get().toInt()
                    check(bitDepth == 8 && (colorType == 2 || colorType == 6) && interlace == 0) {
                        "unsupported PNG: depth=$bitDepth colour=$colorType interlace=$interlace"
                    }
                }
                "IDAT" -> idat.write(data)
                "IEND" -> break
            }
        }
        val channels = if (colorType == 6) 4 else 3
        val stride = width * channels
        val raw = java.util.zip.InflaterInputStream(idat.toByteArray().inputStream()).readBytes()
        val out = ByteArray(width * height * 4)
        var previous = ByteArray(stride)
        for (y in 0 until height) {
            val base = y * (stride + 1)
            val filter = raw[base].toInt()
            val line = raw.copyOfRange(base + 1, base + 1 + stride)
            for (x in 0 until stride) {
                val a = if (x >= channels) line[x - channels].toInt() and 255 else 0
                val b = previous[x].toInt() and 255
                val c = if (x >= channels) previous[x - channels].toInt() and 255 else 0
                val predictor = when (filter) {
                    0 -> 0
                    1 -> a
                    2 -> b
                    3 -> (a + b) / 2
                    4 -> { val p = a + b - c; val pa = kotlin.math.abs(p - a); val pb = kotlin.math.abs(p - b)
                           val pc = kotlin.math.abs(p - c)
                           if (pa <= pb && pa <= pc) a else if (pb <= pc) b else c }
                    else -> error("bad PNG filter $filter")
                }
                line[x] = ((line[x].toInt() + predictor) and 255).toByte()
            }
            for (x in 0 until width) {
                val o = (y * width + x) * 4
                out[o] = line[x * channels]; out[o + 1] = line[x * channels + 1]; out[o + 2] = line[x * channels + 2]
                out[o + 3] = if (channels == 4) line[x * channels + 3] else 255.toByte()
            }
            previous = line
        }
        return Image(width, height, out)
    }
}
