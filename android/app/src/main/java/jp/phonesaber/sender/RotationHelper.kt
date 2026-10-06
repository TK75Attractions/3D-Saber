package jp.phonesaber.sender

import java.nio.ByteBuffer

/** Clockwise pixel rotation to fixed, unmirrored portrait, BEFORE the step=2 lattice. */
class RotationHelper {
    data class Pixels(val buffer: ByteBuffer, val width: Int, val height: Int, val rowStride: Int)
    private var scratch = ByteBuffer.allocateDirect(0)

    fun orient(source: ByteBuffer, width: Int, height: Int, rowStride: Int,
               pixelStride: Int, degrees: Int): Pixels {
        require(width in 1..32768 && height in 1..32768 && pixelStride == 4)
        require(degrees in setOf(0, 90, 180, 270))
        require(rowStride.toLong() >= width.toLong() * 4)
        val input = source.slice() // Honor plane position/limit; JNI address starts at this slice.
        require(input.isDirect)
        require(input.remaining().toLong() >= (height - 1).toLong() * rowStride + width.toLong() * 4)
        if (degrees == 0 && input.remaining().toLong() >= height.toLong() * rowStride) {
            return Pixels(input, width, height, rowStride)
        }
        val outWidth = if (degrees % 180 == 0) width else height
        val outHeight = if (degrees % 180 == 0) height else width
        val bytes = outWidth.toLong() * outHeight * 4
        require(bytes <= Int.MAX_VALUE)
        if (scratch.capacity() != bytes.toInt()) scratch = ByteBuffer.allocateDirect(bytes.toInt())
        // Also packs a 0-degree plane if its last row omits padding (the core needs stride*height).
        // Read/write bytes only: preserve channels, alpha and exact original RGB values.
        for (y in 0 until height) for (x in 0 until width) {
            val outX: Int
            val outY: Int
            when (degrees) {
                0 -> { outX = x; outY = y }
                90 -> { outX = height - 1 - y; outY = x }
                180 -> { outX = width - 1 - x; outY = height - 1 - y }
                else -> { outX = y; outY = width - 1 - x }
            }
            val src = y * rowStride + x * 4
            val dst = (outY * outWidth + outX) * 4
            for (channel in 0..3) scratch.put(dst + channel, input.get(src + channel))
        }
        scratch.clear()
        return Pixels(scratch, outWidth, outHeight, outWidth * 4)
    }
}
