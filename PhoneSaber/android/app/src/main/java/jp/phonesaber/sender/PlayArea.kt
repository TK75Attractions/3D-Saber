package jp.phonesaber.sender

import java.util.Locale
import kotlin.math.roundToInt

/**
 * プレイ範囲（剣を振る場所。色に依らず1つ）。C++ の phonesaber::PlayArea、iPhone の SaberPlayArea と同じ意味。
 * 2026-10-18: 赤い服の人など動く赤・青の物は無視範囲では外せない。範囲を設定すると、両端の中点が範囲の外の
 * 候補は選ばず、同じコマの次の eligible 候補を選ぶ（範囲が無ければ選び方は従来と同じ）。
 * 座標は無視範囲と同じ解析フレーム（縦向きに回転した後、送信用の拡大・反転より前）の 0..1。
 * 範囲は「プレイ範囲を覚える」の間に選ばれた winner の両端の min/max に余白 0.08・最小 0.3 を付けて C++ が作る。
 */
data class PlayArea(val minX: Double, val minY: Double, val maxX: Double, val maxY: Double) {
    val valid: Boolean get() = listOf(minX, minY, maxX, maxY).all { it.isFinite() } && minX < maxX && minY < maxY

    /** JNI へ渡す [min_x, min_y, max_x, max_y]。 */
    fun packed() = doubleArrayOf(minX, minY, maxX, maxY)

    /** SharedPreferences 用（Locale に依存しない）。 */
    fun encode(): String = listOf(minX, minY, maxX, maxY).joinToString(",") { String.format(Locale.US, "%.6f", it) }

    /** 画面・ログ用。「x 12〜68% / y 12〜88%」。 */
    val summary: String get() = "x ${pct(minX)}〜${pct(maxX)}% / y ${pct(minY)}〜${pct(maxY)}%"

    private fun pct(value: Double) = (value * 100).roundToInt()

    companion object {
        const val PREFERENCE_KEY = "playArea.v1"
        /** C++ PlayArea::minimum_samples と同じ。これより少ない学習では前の範囲を残す。 */
        const val MINIMUM_SAMPLES = 30
        const val LEARN_SECONDS = 10

        /** 壊れた値は捨てる（起動を妨げない）。 */
        fun decode(text: String?): PlayArea? {
            if (text.isNullOrEmpty()) return null
            val v = text.split(',').map { it.toDoubleOrNull() ?: return null }
            if (v.size != 4) return null
            return PlayArea(v[0], v[1], v[2], v[3]).takeIf { it.valid }
        }
    }
}

/**
 * 露出補正（光らない物を暗くする試験スイッチ）。既定 0 = OFF で、0 の間はカメラの露出に一切触れない。
 * シャッターだけ短くしても自動露出が ISO を上げて戻すので、露出補正を負にして服などを暗くし、
 * 自発光の LED 剣だけを明るく残す狙い。0 以外では認識への入力（画素）が変わる。閾値は変えない。
 */
object ExposureBias {
    val CHOICES = listOf(0, -1, -2)
    const val PREFERENCE_KEY = "exposureBias.v1"

    fun valid(ev: Int) = ev in CHOICES

    /**
     * EV → CameraX の露出補正 index。step は 1 index あたりの EV（分数）。範囲内に丸める。
     * 対応していない端末（範囲が [0,0] か step が不正）は null。
     */
    fun index(ev: Int, stepNumerator: Int, stepDenominator: Int, lower: Int, upper: Int): Int? {
        if (stepNumerator <= 0 || stepDenominator <= 0 || lower > upper || (lower == 0 && upper == 0)) return null
        return (ev.toDouble() * stepDenominator / stepNumerator).roundToInt().coerceIn(lower, upper)
    }

    fun label(ev: Int) = if (ev == 0) "0（OFF）" else "−${-ev} EV"
}
