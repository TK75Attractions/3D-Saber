package jp.phonesaber.sender

import java.nio.ByteBuffer

/** カメラ/期限処理の単一executorで所有。JNIは入力planeを保持せず、回転作業領域だけを再利用する。 */
class NativeCore(private val mirrors: MirrorSettings, private val measurementMode: Boolean = false) : AutoCloseable {
    private var handle = create()
    fun process(pixels: RotationHelper.Pixels, time: Double, brightness: Int,
                dominance: Int): Array<NativeResult> = process(handle, pixels.buffer,
        pixels.width, pixels.height, pixels.rowStride, time, brightness, dominance,
        mirrors.mirrorX, mirrors.mirrorY, measurementMode)
    // plane の position/limit を渡し、slice・行ビュー・全画素の JVM 配列を作らない。
    fun processCamera(buffer: ByteBuffer, width: Int, height: Int, rowStride: Int,
                      pixelStride: Int, degrees: Int, time: Double, brightness: Int,
                      dominance: Int): Array<NativeResult> {
        require(pixelStride == 4)
        return processRotated(handle, buffer, buffer.position(), buffer.remaining(), width, height,
            rowStride, degrees, time, brightness, dominance,
            mirrors.mirrorX, mirrors.mirrorY, measurementMode)
    }
    private external fun processRotated(handle: Long, buffer: ByteBuffer, position: Int, remaining: Int,
                                       width: Int, height: Int, rowStride: Int, degrees: Int,
                                       time: Double, brightness: Int, dominance: Int,
                                       mirrorX: Boolean, mirrorY: Boolean,
                                       measurementMode: Boolean): Array<NativeResult>
    fun expire(time: Double): Array<NativeResult> = expire(handle, time)
    fun nextExpiry(): Double = nextExpiry(handle)
    override fun close() {
        if (handle != 0L) { destroy(handle); handle = 0 }
    }
    private external fun create(): Long
    private external fun destroy(handle: Long)
    private external fun process(handle: Long, buffer: ByteBuffer, width: Int, height: Int,
                                 rowStride: Int, time: Double, brightness: Int,
                                 dominance: Int, mirrorX: Boolean, mirrorY: Boolean,
                                 measurementMode: Boolean): Array<NativeResult>
    private external fun expire(handle: Long, time: Double): Array<NativeResult>
    private external fun nextExpiry(handle: Long): Double
    companion object {
        init { System.loadLibrary("phonesaber_jni") }
    }
}
