package jp.phonesaber.sender

import java.util.Locale

/**
 * 会場に置かれたままの赤・青の物（コーンなど）を選ばないための範囲。iPhone の SaberIgnoreZone（7c5a1c1）と同じ意味。
 * 座標は解析フレーム（縦向きに回転した後、送信用の拡大・反転より前）を 0..1 にしたもの。
 * 範囲は C++ の ignore_zone_around が、選ばれた候補の範囲（成分の箱と両端）に余白 0.04・最小 0.06 を付けて作る。
 */
data class IgnoreZone(val minX: Double, val minY: Double, val maxX: Double, val maxY: Double) {
    val valid: Boolean get() = listOf(minX, minY, maxX, maxY).all { it.isFinite() } && minX <= maxX && minY <= maxY
}

/** 色ごとの範囲（red=0, blue=1）。不変。押すたびに増え、色ごとに古いものから捨てて最大 [MAXIMUM] つ。 */
class IgnoreZones(val red: List<IgnoreZone> = emptyList(), val blue: List<IgnoreZone> = emptyList()) {
    /** JNI へ渡す [min_x, min_y, max_x, max_y] × n。解析スレッドで毎フレーム作らないよう一度だけ作る。 */
    val packedRed: DoubleArray = pack(red)
    val packedBlue: DoubleArray = pack(blue)

    fun of(color: Int): List<IgnoreZone> = if (color == 0) red else blue

    fun adding(color: Int, zone: IgnoreZone): IgnoreZones {
        val list = (of(color) + zone).takeLast(MAXIMUM)
        return if (color == 0) IgnoreZones(list, blue) else IgnoreZones(red, list)
    }

    val isEmpty: Boolean get() = red.isEmpty() && blue.isEmpty()
    val summary: String get() = "赤 ${red.size}・青 ${blue.size}"

    /** SharedPreferences 用。"red:minX,minY,maxX,maxY;...|blue:..."（Locale に依存しない）。 */
    fun encode(): String {
        fun side(name: String, zones: List<IgnoreZone>) = name + ":" + zones.joinToString(";") { z ->
            listOf(z.minX, z.minY, z.maxX, z.maxY).joinToString(",") { String.format(Locale.US, "%.6f", it) }
        }
        return side("red", red) + "|" + side("blue", blue)
    }

    override fun equals(other: Any?) = other is IgnoreZones && other.red == red && other.blue == blue
    override fun hashCode() = red.hashCode() * 31 + blue.hashCode()
    override fun toString() = "IgnoreZones(red=$red, blue=$blue)"

    companion object {
        const val MAXIMUM = 4
        const val PREFERENCE_KEY = "ignoreZones.v1"
        val NONE = IgnoreZones()

        private fun pack(zones: List<IgnoreZone>) = DoubleArray(zones.size * 4).also { out ->
            zones.forEachIndexed { i, z -> out[i * 4] = z.minX; out[i * 4 + 1] = z.minY; out[i * 4 + 2] = z.maxX; out[i * 4 + 3] = z.maxY }
        }

        /** 壊れた値は捨てる（起動を妨げない）。 */
        fun decode(text: String?): IgnoreZones {
            if (text.isNullOrEmpty()) return NONE
            var red = emptyList<IgnoreZone>()
            var blue = emptyList<IgnoreZone>()
            for (side in text.split('|')) {
                val parts = side.split(':', limit = 2)
                if (parts.size != 2) continue
                val zones = parts[1].split(';').filter { it.isNotEmpty() }.mapNotNull { item ->
                    val v = item.split(',').map { it.toDoubleOrNull() ?: return@mapNotNull null }
                    if (v.size != 4) null else IgnoreZone(v[0], v[1], v[2], v[3]).takeIf { it.valid }
                }.takeLast(MAXIMUM)
                when (parts[0]) { "red" -> red = zones; "blue" -> blue = zones }
            }
            return IgnoreZones(red, blue)
        }
    }
}
