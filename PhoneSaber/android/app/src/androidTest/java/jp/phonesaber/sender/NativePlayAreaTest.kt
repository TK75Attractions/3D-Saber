package jp.phonesaber.sender

import java.nio.ByteBuffer
import org.junit.Assert.*
import org.junit.Test

/** 実機の JNI 経路で、プレイ範囲の受け渡し・学習・同じコマで範囲内の候補が送られることを確認する。 */
class NativePlayAreaTest {
    private val width = 200
    private val height = 120

    // core_test.cpp の play_area_tests と同じ場面（RGBA）。
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

    private fun red(core: NativeCore, area: PlayArea?, time: Double = 1.0): String? =
        core.processCamera(scene(), width, height, width * 4, 4, 0, time, 140, 20, IgnoreZones.NONE, area)
            .firstOrNull { it.color == 0 && it.fresh }?.text

    @Test fun outsideCandidateIsSkippedAndLearningWorks() {
        NativeCore(MirrorSettings()).use { core ->
            val plain = red(core, null)
            assertNotNull(plain)
            NativeCore(MirrorSettings()).use { left ->
                val l = red(left, PlayArea(0.0, 0.0, 0.5, 1.0))
                NativeCore(MirrorSettings()).use { right ->
                    val r = red(right, PlayArea(0.5, 0.0, 1.0, 1.0))
                    // どちらか一方は plain と同じ棒、もう一方は別の棒。
                    assertNotNull(l); assertNotNull(r); assertNotEquals(l, r)
                    assertTrue(l == plain || r == plain)
                }
            }
            NativeCore(MirrorSettings()).use { whole ->
                assertEquals(plain, red(whole, PlayArea(0.0, 0.0, 1.0, 1.0)))
            }
            NativeCore(MirrorSettings()).use { learning ->
                assertNull(learning.finishAreaLearning()) // 学習していない
                learning.startAreaLearning()
                for (i in 0 until 29) red(learning, PlayArea(0.9, 0.9, 1.0, 1.0), 1.0 + i / 30.0) // 学習中は範囲で絞らない
                val few = learning.finishAreaLearning()
                assertEquals(29, few?.samples); assertNull(few?.area)
                learning.startAreaLearning()
                for (i in 0 until 30) red(learning, null, 2.0 + i / 30.0)
                val learned = learning.finishAreaLearning()
                assertEquals(30, learned?.samples)
                val area = learned!!.area!!
                assertTrue(area.maxX - area.minX >= 0.3 && area.maxY - area.minY >= 0.3)
                assertTrue(area.minX >= 0 && area.maxX <= 1 && area.minY >= 0 && area.maxY <= 1)
            }
        }
    }
}
