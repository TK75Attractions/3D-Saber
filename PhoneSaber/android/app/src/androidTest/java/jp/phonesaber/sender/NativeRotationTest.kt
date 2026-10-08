package jp.phonesaber.sender

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.Random
import org.junit.Assert.*
import org.junit.Test

internal object NativeRotationProbe {
    init { System.loadLibrary("phonesaber_jni") }
    external fun create(): Long
    external fun destroy(handle: Long)
    external fun rotate(handle: Long, buffer: ByteBuffer, position: Int, remaining: Int,
                        width: Int, height: Int, rowStride: Int, degrees: Int): ByteBuffer
}

class NativeRotationTest {
    @Test fun randomPlanesMatchRotationHelperByteForByte() {
        val random = Random(0x51ab3)
        val reference = RotationHelper()
        val handle = NativeRotationProbe.create()
        try {
            repeat(120) { frame ->
                val width = if (frame == 0) 640 else 1 + random.nextInt(67)
                val height = if (frame == 0) 480 else 1 + random.nextInt(73)
                val stride = width * 4 + random.nextInt(17)
                val position = random.nextInt(8)
                val fullPadding = frame % 2 == 0
                val size = if (fullPadding) height * stride else (height - 1) * stride + width * 4
                val data = ByteArray(position + height * stride + 19).also { random.nextBytes(it) }
                val buffer = ByteBuffer.allocateDirect(data.size).put(data)
                buffer.position(position)
                buffer.limit(position + size)
                buffer.order(if (frame % 3 == 0) ByteOrder.LITTLE_ENDIAN else ByteOrder.BIG_ENDIAN)
                for (degrees in intArrayOf(0, 90, 180, 270)) {
                    val expected = reference.orient(buffer, width, height, stride, 4, degrees)
                    val actual = NativeRotationProbe.rotate(handle, buffer, position, size,
                        width, height, stride, degrees)
                    val actualStride = if (degrees == 0 && fullPadding) stride else expected.width * 4
                    for (y in 0 until expected.height) for (x in 0 until expected.width * 4) {
                        assertEquals("frame=$frame angle=$degrees y=$y byte=$x",
                            expected.buffer.get(y * expected.rowStride + x), actual.get(y * actualStride + x))
                    }
                    assertEquals(position, buffer.position())
                    assertEquals(position + size, buffer.limit())
                }
            }
        } finally { NativeRotationProbe.destroy(handle) }
    }

    @Test fun cameraPathMatchesLegacyResultsAndTrackState() {
        val random = Random(712)
        for (degrees in intArrayOf(0, 90, 180, 270)) {
            val width = 73; val height = 97; val stride = width * 4 + 13
            val buffer = ByteBuffer.allocateDirect(stride * height + 16)
            val rotation = RotationHelper()
            val mirrors = MirrorSettings(mirrorX = true, mirrorY = true)
            NativeCore(mirrors).use { legacy -> NativeCore(mirrors).use { camera ->
                repeat(18) { frame ->
                    val data = ByteArray(stride * height).also { random.nextBytes(it) }
                    // 赤・青棒、消失、予測、保持、復帰を同じ時刻で比較する。
                    for (y in 0 until height) for (x in 0 until width) {
                        val red = frame < 3 && x in 12..18 && y in 10..82
                        val blue = frame in 5..9 && x in 48..54 && y in 6..87
                        val offset = y * stride + x * 4
                        data[offset] = (if (red) 255 else 20).toByte()
                        data[offset + 1] = 20
                        data[offset + 2] = (if (blue) 255 else 20).toByte()
                    }
                    buffer.clear().position(5)
                    buffer.put(data)
                    buffer.limit(5 + (height - 1) * stride + width * 4).position(5)
                    val time = 1.0 + frame / 60.0
                    val expected = legacy.process(rotation.orient(buffer, width, height, stride, 4, degrees), time, 145, 25)
                    val actual = camera.processCamera(buffer, width, height, stride, 4, degrees, time, 145, 25)
                    assertArrayEquals("angle=$degrees frame=$frame", expected, actual)
                    assertEquals(legacy.nextExpiry(), camera.nextExpiry(), 0.0)
                }
                assertArrayEquals(legacy.expire(3.0), camera.expire(3.0))
            } }
        }
    }

    @Test fun rejectsInvalidPlanesAndRespectsLimit() {
        NativeCore(MirrorSettings()).use { core ->
            val buffer = ByteBuffer.allocateDirect(128)
            val cases = listOf(
                { core.processCamera(buffer.apply { clear(); limit(27) }, 3, 2, 16, 4, 90, 1.0, 145, 25) },
                { core.processCamera(buffer.apply { clear() }, 3, 2, 16, 4, 45, 1.0, 145, 25) },
                { core.processCamera(buffer, 3, 2, 16, 8, 90, 1.0, 145, 25) },
                { core.processCamera(ByteBuffer.allocate(128), 3, 2, 16, 4, 90, 1.0, 145, 25) },
                { core.processCamera(buffer, 3, 2, 11, 4, 90, 1.0, 145, 25) },
                { core.processCamera(buffer, 0, 2, 16, 4, 90, 1.0, 145, 25) })
            for (call in cases) {
                try { call(); fail("invalid plane accepted") }
                catch (_: IllegalArgumentException) { }
            }
        }
    }
}
