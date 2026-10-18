package jp.phonesaber.sender

import java.nio.ByteBuffer
import org.junit.Assert.*
import org.junit.Test

/** 実機の JNI 経路で、無視範囲の受け渡し・今の winner の範囲・同じコマで次の候補が送られることを確認する。 */
class NativeIgnoreZoneTest {
    private val width = 200
    private val height = 120

    // core_test.cpp の ignore_zone_tests と同じ場面（RGBA）。
    private fun scene(): ByteBuffer {
        val buffer = ByteBuffer.allocateDirect(width * height * 4)
        fun bar(x0: Int, y0: Int, x1: Int, y1: Int, thickness: Int) {
            val steps = maxOf(Math.abs(x1 - x0), Math.abs(y1 - y0))
            for (step in 0..steps) {
                val t = if (steps == 0) 0.0 else step.toDouble() / steps
                val x = Math.round(x0 + (x1 - x0) * t).toInt(); val y = Math.round(y0 + (y1 - y0) * t).toInt()
                for (dy in -thickness..thickness) for (dx in -thickness..thickness) {
                    if (dx * dx + dy * dy > thickness * thickness || x + dx !in 0 until width || y + dy !in 0 until height) continue
                    val i = ((y + dy) * width + x + dx) * 4
                    buffer.put(i, 245.toByte()); buffer.put(i + 1, 45); buffer.put(i + 2, 30); buffer.put(i + 3, 255.toByte())
                }
            }
        }
        bar(150, 15, 150, 105, 4)
        bar(30, 35, 45, 105, 4)
        return buffer
    }

    private fun red(core: NativeCore, zones: IgnoreZones, time: Double = 1.0): String? =
        core.processCamera(scene(), width, height, width * 4, 4, 0, time, 140, 20, zones)
            .firstOrNull { it.color == 0 && it.fresh }?.text

    private fun ends(text: String?): Set<List<String>> =
        text!!.split(',').chunked(2).toSet()

    @Test fun ignoredWinnerIsSkippedInTheSameFrame() {
        NativeCore(MirrorSettings()).use { core ->
            val plain = red(core, IgnoreZones.NONE)
            assertNotNull(plain)
            val zone = core.winnerZone(0, 1.0, CameraSession.WINNER_MAX_AGE)
            assertNotNull(zone)
            assertNull(core.winnerZone(1, 1.0, CameraSession.WINNER_MAX_AGE))
            assertNull(core.winnerZone(0, 2.0, CameraSession.WINNER_MAX_AGE)) // 古い winner は使わない
            assertTrue(zone!!.maxX - zone.minX >= 0.06 && zone.maxY - zone.minY >= 0.06)

            NativeCore(MirrorSettings()).use { ignoring ->
                val skipped = red(ignoring, IgnoreZones.NONE.adding(0, zone))
                assertNotNull(skipped)
                assertNotEquals(plain, skipped)
                // 範囲を消すと次のコマから元に戻る（カメラの再起動なし）。端の順は前のコマに合わせて入れ替わり得る。
                assertEquals(ends(plain), ends(red(ignoring, IgnoreZones.NONE, 1.05)))
            }
            NativeCore(MirrorSettings()).use { blue ->
                assertEquals(plain, red(blue, IgnoreZones.NONE.adding(1, zone)))
            }
            NativeCore(MirrorSettings()).use { far ->
                assertEquals(plain, red(far, IgnoreZones.NONE.adding(0, IgnoreZone(0.9, 0.9, 1.0, 1.0))))
            }
        }
    }
}
