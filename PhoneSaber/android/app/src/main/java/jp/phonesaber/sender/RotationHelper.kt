package jp.phonesaber.sender

import java.nio.ByteBuffer

/** Clockwise pixel rotation to fixed, unmirrored portrait, BEFORE the step=2 lattice. */
class RotationHelper {
    data class Pixels(val buffer: ByteBuffer, val width: Int, val height: Int, val rowStride: Int)
    private var scratch = ByteBuffer.allocateDirect(0)
    private var rowInts = IntArray(0)

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
        // Move whole 4-byte pixels as Ints (same byte order on both sides): channels, alpha and
        // exact original RGB values are preserved. Per-byte get/put took ~80ms/frame on sense9.
        val pixelsIn = IntArray(width)
        if (rowInts.size != outWidth * outHeight) rowInts = IntArray(outWidth * outHeight)
        val out = rowInts
        val order = input.order()
        for (y in 0 until height) {
            val row = input.duplicate().order(order)
            row.position(y * rowStride)
            row.asIntBuffer().get(pixelsIn, 0, width)
            for (x in 0 until width) {
                val index = when (degrees) {
                    0 -> y * outWidth + x
                    90 -> x * outWidth + (height - 1 - y)
                    180 -> (height - 1 - y) * outWidth + (width - 1 - x)
                    else -> (width - 1 - x) * outWidth + y
                }
                out[index] = pixelsIn[x]
            }
        }
        scratch.clear()
        scratch.order(order).asIntBuffer().put(out)
        scratch.clear()
        return Pixels(scratch, outWidth, outHeight, outWidth * 4)
    }
}
