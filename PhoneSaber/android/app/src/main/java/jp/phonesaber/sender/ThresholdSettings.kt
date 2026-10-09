package jp.phonesaber.sender

/**
 * 認識の閾値（明るさ・色の優位差）。iPhone と同じく永続保存はしないが、Activity の再生成
 * （ダークモード切替などの設定変更・プロセス復元）では保持し、自動再開が既定値で走らないようにする。
 */
data class ThresholdSettings(val brightness: Int = DEFAULT_BRIGHTNESS, val dominance: Int = DEFAULT_DOMINANCE) {
    fun save(writeInt: (String, Int) -> Unit) {
        writeInt(BRIGHTNESS_KEY, brightness)
        writeInt(DOMINANCE_KEY, dominance)
    }

    companion object {
        const val DEFAULT_BRIGHTNESS = 145
        const val DEFAULT_DOMINANCE = 25
        private const val BRIGHTNESS_KEY = "thresholdBrightness"
        private const val DOMINANCE_KEY = "thresholdDominance"

        fun restore(readInt: (String, Int) -> Int) = ThresholdSettings(
            readInt(BRIGHTNESS_KEY, DEFAULT_BRIGHTNESS).coerceIn(0, 255),
            readInt(DOMINANCE_KEY, DEFAULT_DOMINANCE).coerceIn(0, 255))
    }
}
