package jp.phonesaber.sender

import java.nio.ByteBuffer

/** Owned exclusively by the single camera/expiry executor. No buffer retained by JNI. */
class NativeCore(private val mirrors: MirrorSettings) : AutoCloseable {
    private var handle = create()
    fun process(pixels: RotationHelper.Pixels, time: Double, brightness: Int,
                dominance: Int): Array<NativeResult> = process(handle, pixels.buffer,
        pixels.width, pixels.height, pixels.rowStride, time, brightness, dominance,
        mirrors.mirrorX, mirrors.mirrorY)
    fun expire(time: Double): Array<NativeResult> = expire(handle, time)
    fun nextExpiry(): Double = nextExpiry(handle)
    override fun close() {
        if (handle != 0L) { destroy(handle); handle = 0 }
    }
    private external fun create(): Long
    private external fun destroy(handle: Long)
    private external fun process(handle: Long, buffer: ByteBuffer, width: Int, height: Int,
                                 rowStride: Int, time: Double, brightness: Int,
                                 dominance: Int, mirrorX: Boolean, mirrorY: Boolean): Array<NativeResult>
    private external fun expire(handle: Long, time: Double): Array<NativeResult>
    private external fun nextExpiry(handle: Long): Double
    companion object {
        init { System.loadLibrary("phonesaber_jni") }
    }
}
