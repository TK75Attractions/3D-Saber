package jp.phonesaber.sender

import java.nio.ByteBuffer
import org.junit.Assert.*
import org.junit.Test

class RotationHelperTest {
    private fun image(lastPadding: Boolean = true): ByteBuffer {
        // 3x2 with 4 padding bytes per row; four distinct channels per labeled pixel.
        val bytes = if (lastPadding) 32 else 28
        val buffer = ByteBuffer.allocateDirect(bytes + 5)
        for (i in 0 until buffer.capacity()) buffer.put(i, 99.toByte())
        for (y in 0..1) for (x in 0..2) for (c in 0..3) {
            buffer.put(5 + y * 16 + x * 4 + c, ((y * 3 + x + 1) * 10 + c).toByte())
        }
        buffer.position(5) // Nonzero plane position must be honored.
        buffer.limit(5 + bytes)
        return buffer
    }
    private fun assertPixels(expected: List<Int>, pixels: RotationHelper.Pixels) {
        val labels = mutableListOf<Int>()
        for (y in 0 until pixels.height) for (x in 0 until pixels.width) {
            val index = y * pixels.rowStride + x * 4
            val label = pixels.buffer.get(index).toInt() / 10
            labels += label
            for (c in 0..3) assertEquals(label * 10 + c, pixels.buffer.get(index + c).toInt())
        }
        assertEquals(expected, labels)
    }

    @Test fun clockwiseQuarterTurnMatchesPortraitPixelConvention() {
        // Input: 1 2 3 / 4 5 6 -> portrait: 4 1 / 5 2 / 6 3. x right, y down.
        val result = RotationHelper().orient(image(), 3, 2, 16, 4, 90)
        assertEquals(2, result.width); assertEquals(3, result.height)
        assertEquals(8, result.rowStride)
        assertPixels(listOf(4, 1, 5, 2, 6, 3), result)
    }

    @Test fun halfTurnAndOtherQuarterTurnAreUnmirrored() {
        val helper = RotationHelper()
        val half = helper.orient(image(), 3, 2, 16, 4, 180)
        assertEquals(3, half.width); assertEquals(2, half.height)
        assertPixels(listOf(6, 5, 4, 3, 2, 1), half)
        assertPixels(listOf(3, 6, 2, 5, 1, 4), helper.orient(image(), 3, 2, 16, 4, 270))
    }

    @Test fun zeroRotationSharesPlaneStorageAndPreservesStride() {
        val input = image()
        val result = RotationHelper().orient(input, 3, 2, 16, 4, 0)
        assertEquals(16, result.rowStride)
        assertPixels(listOf(1, 2, 3, 4, 5, 6), result)
        input.put(5, 70)
        assertEquals(70, result.buffer.get(0).toInt())
        assertEquals(5, input.position())
    }

    @Test fun missingFinalPaddingIsPackedForCoreFullRowStorageContract() {
        val result = RotationHelper().orient(image(false), 3, 2, 16, 4, 0)
        assertEquals(12, result.rowStride)
        assertEquals(24, result.buffer.remaining())
        assertPixels(listOf(1, 2, 3, 4, 5, 6), result)
    }

    @Test fun reusesScratchStorageForSameDimensions() {
        val helper = RotationHelper()
        val first = helper.orient(image(), 3, 2, 16, 4, 90)
        val second = helper.orient(image(), 3, 2, 16, 4, 90)
        assertSame(first.buffer, second.buffer)
    }

    @Test(expected = IllegalArgumentException::class)
    fun rejectsShortPlane() { RotationHelper().orient(ByteBuffer.allocateDirect(23), 3, 2, 16, 4, 90) }

    @Test(expected = IllegalArgumentException::class)
    fun rejectsUnsupportedPixelStride() { RotationHelper().orient(image(), 3, 2, 16, 8, 90) }

    @Test(expected = IllegalArgumentException::class)
    fun rejectsNonQuarterTurn() { RotationHelper().orient(image(), 3, 2, 16, 4, 45) }
}
