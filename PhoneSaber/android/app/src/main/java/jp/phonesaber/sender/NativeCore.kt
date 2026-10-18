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
                      dominance: Int, ignoreZones: IgnoreZones = IgnoreZones.NONE,
                      playArea: PlayArea? = null): Array<NativeResult> {
        require(pixelStride == 4)
        applyIgnoreZones(ignoreZones)
        applyPlayArea(playArea)
        return processRotated(handle, buffer, buffer.position(), buffer.remaining(), width, height,
            rowStride, degrees, time, brightness, dominance,
            mirrors.mirrorX, mirrors.mirrorY, measurementMode)
    }
    private external fun processRotated(handle: Long, buffer: ByteBuffer, position: Int, remaining: Int,
                                       width: Int, height: Int, rowStride: Int, degrees: Int,
                                       time: Double, brightness: Int, dominance: Int,
                                       mirrorX: Boolean, mirrorY: Boolean,
                                       measurementMode: Boolean): Array<NativeResult>
    // 無視範囲は変わったときだけ native へ渡す（不変オブジェクトの参照で比較）。
    private var appliedZones: IgnoreZones = IgnoreZones.NONE
    private fun applyIgnoreZones(zones: IgnoreZones) {
        if (zones === appliedZones) return
        setIgnoreZones(handle, zones.packedRed, zones.packedBlue)
        appliedZones = zones
    }
    private val winnerOut = DoubleArray(4)
    /** 直近 [maxAge] 秒以内に選ばれた [color] の候補の範囲（余白込み）。解析スレッドから呼ぶ。 */
    fun winnerZone(color: Int, now: Double, maxAge: Double): IgnoreZone? =
        if (handle != 0L && winnerZone(handle, color, now, maxAge, winnerOut))
            IgnoreZone(winnerOut[0], winnerOut[1], winnerOut[2], winnerOut[3]) else null
    // プレイ範囲も変わったときだけ渡す（新しい core は範囲なしで始まるので null と一致）。
    private var appliedArea: PlayArea? = null
    private fun applyPlayArea(area: PlayArea?) {
        if (area === appliedArea) return
        setPlayArea(handle, area?.packed())
        appliedArea = area
    }
    /** 学習の結果。samples は数えた winner（両色の合計コマ数）、area は足りたときだけ。 */
    data class AreaLearning(val samples: Int, val area: PlayArea?)
    /** プレイ範囲の学習を始める（学習中は範囲で絞らない。無視範囲は効く）。解析スレッドから呼ぶ。 */
    fun startAreaLearning() { if (handle != 0L) startAreaLearning(handle) }
    /** 学習を終える。学習していなければ null。解析スレッドから呼ぶ。 */
    fun finishAreaLearning(): AreaLearning? {
        if (handle == 0L) return null
        val out = DoubleArray(4) { Double.NaN }
        val samples = finishAreaLearning(handle, out)
        if (samples < 0) return null
        return AreaLearning(samples, PlayArea(out[0], out[1], out[2], out[3]).takeIf { it.valid })
    }
    private external fun setPlayArea(handle: Long, area: DoubleArray?)
    private external fun startAreaLearning(handle: Long)
    private external fun finishAreaLearning(handle: Long, out: DoubleArray): Int
    private external fun setIgnoreZones(handle: Long, red: DoubleArray, blue: DoubleArray)
    private external fun winnerZone(handle: Long, color: Int, now: Double, maxAge: Double, out: DoubleArray): Boolean
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
