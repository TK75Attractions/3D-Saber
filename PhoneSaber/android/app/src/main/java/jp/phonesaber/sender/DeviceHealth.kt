package jp.phonesaber.sender

import java.util.Locale

enum class ThermalLevel(val title: String) {
    NORMAL("正常"), ELEVATED("やや高い"), HIGH("高い"), DANGEROUS("危険"), UNKNOWN("不明");

    companion object {
        // PowerManager: NONE=0、LIGHT=1、MODERATE=2、SEVERE=3、CRITICAL以降=4..6。
        fun fromStatus(status: Int) = when (status) {
            0 -> NORMAL
            1, 2 -> ELEVATED
            3 -> HIGH
            in 4..6 -> DANGEROUS
            else -> UNKNOWN
        }
    }
}

data class HealthRates(val analysisFps: Double = 0.0, val medianJniMs: Double? = null,
                       val ready: Boolean = false, val medianCaptureToSendMs: Double? = null)

object DeviceHealthText {
    fun batteryState(status: Int): String? = when (status) {
        2 -> "充電中"
        3, 4 -> "未充電"
        5 -> "満充電"
        else -> null
    }

    fun battery(percent: Int?, state: String?) =
        "電池 ${percent?.takeIf { it in 0..100 }?.let { "$it%" } ?: "不明"} / ${state ?: "状態不明"}"

    fun timing(rates: HealthRates): String {
        val median = rates.medianJniMs?.let { String.format(Locale.JAPAN, "%.1f ms", it) } ?: "未計測"
        val age = rates.medianCaptureToSendMs?.let { String.format(Locale.JAPAN, " / 撮影→送信 %.0f ms", it) } ?: ""
        return String.format(Locale.JAPAN, "解析 %.1f fps / JNI中央値 %s%s（直近5秒）", rates.analysisFps, median, age)
    }

    fun warning(thermal: ThermalLevel, rates: HealthRates, requestedFps: Double, running: Boolean): String? {
        val reasons = mutableListOf<String>()
        if (thermal == ThermalLevel.HIGH || thermal == ThermalLevel.DANGEROUS) reasons += "発熱が${thermal.title}"
        if (running && rates.ready && requestedFps > 0 && rates.analysisFps < requestedFps * 0.7)
            reasons += "fpsが要求値の70%未満"
        return if (reasons.isEmpty()) null else "注意: ${reasons.joinToString(" / ")}。箱を開けて通風・電源を確認"
    }
}

// JNIの実測値のみを保存し、expiry/送信結果は数えない。
class DeviceHealthMeter {
    private var started = 0.0
    private val samples = ArrayDeque<Pair<Double, Double>>()
    // センサー露光時刻から JNI 処理完了（送信キュー投入）までの経過。時刻源が不明な端末では記録しない。
    private val ages = ArrayDeque<Pair<Double, Double>>()
    @Synchronized fun reset(at: Double) { started = at; samples.clear(); ages.clear() }
    @Synchronized fun processed(at: Double, milliseconds: Double, captureToSendMs: Double? = null) {
        if (!milliseconds.isFinite() || milliseconds < 0) return
        samples.addLast(at to milliseconds)
        if (captureToSendMs != null && captureToSendMs.isFinite() && captureToSendMs in 0.0..2000.0)
            ages.addLast(at to captureToSendMs)
        prune(at)
        while (samples.size > 1200) samples.removeFirst()
        while (ages.size > 1200) ages.removeFirst()
    }
    private fun prune(at: Double) {
        while (samples.isNotEmpty() && samples.first().first <= at - 5.0) samples.removeFirst()
        while (ages.isNotEmpty() && ages.first().first <= at - 5.0) ages.removeFirst()
    }
    private fun median(values: List<Double>): Double? {
        val sorted = values.sorted()
        val n = sorted.size
        return if (n == 0) null else if (n % 2 == 0) (sorted[n / 2 - 1] + sorted[n / 2]) / 2 else sorted[n / 2]
    }
    @Synchronized fun snapshot(at: Double): HealthRates {
        prune(at)
        val elapsed = at - started
        if (elapsed <= 0) return HealthRates()
        return HealthRates(samples.size / minOf(5.0, elapsed), median(samples.map { it.second }), elapsed >= 3.0,
            median(ages.map { it.second }))
    }
}
