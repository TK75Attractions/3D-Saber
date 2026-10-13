package jp.phonesaber.sender

import java.util.Locale
import kotlin.math.ceil

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
                       val ready: Boolean = false, val medianCaptureToSendMs: Double? = null,
                       val p95CaptureToSendMs: Double? = null, val sentSamples: Int = 0,
                       val captureFps: Double? = null, val redSendFps: Double = 0.0,
                       val blueSendFps: Double = 0.0, val unanalysedEstimate: Long = 0,
                       val udpDroppedFrames: Long = 0)

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
        val age = if (rates.medianCaptureToSendMs != null && rates.p95CaptureToSendMs != null)
            String.format(Locale.JAPAN, " / 撮影→送信 中央値 %.0f ms / p95 %.0f ms（%d件）",
                rates.medianCaptureToSendMs, rates.p95CaptureToSendMs, rates.sentSamples)
            else " / 撮影→送信 未計測"
        val capture = rates.captureFps?.let { String.format(Locale.JAPAN, "%.1f", it) } ?: "未計測"
        return String.format(Locale.JAPAN, "撮影fps %s / 解析 %.1f fps / JNI中央値 %s%s（直近5秒） / 未解析推定 %d / 送信破棄 %d（開始から）",
            capture, rates.analysisFps, median, age, rates.unanalysedEstimate, rates.udpDroppedFrames)
    }

    fun warning(thermal: ThermalLevel, rates: HealthRates, requestedFps: Double, running: Boolean): String? {
        val reasons = mutableListOf<String>()
        if (thermal == ThermalLevel.HIGH || thermal == ThermalLevel.DANGEROUS) reasons += "発熱が${thermal.title}"
        if (running && rates.ready && requestedFps > 0 && rates.analysisFps < requestedFps * 0.7)
            reasons += "fpsが要求値の70%未満"
        return if (reasons.isEmpty()) null else "注意: ${reasons.joinToString(" / ")}。箱を開けて通風・電源を確認"
    }
}

object CaptureClock {
    // UNKNOWN は nanoTime に近くても正確な比較はできない。REALTIME だけを使う。
    fun timestamp(sensorNs: Long, realtime: Boolean): Long? = sensorNs.takeIf { realtime && it > 0 }
}

// 解析fpsとJNI時間は解析フレーム、撮影→送信は成功したフレームだけを数える。
class DeviceHealthMeter {
    private var started = 0.0
    private val samples = ArrayDeque<Pair<Double, Double>>()
    // センサー先頭行の露光開始から、そのフレームで最後に成功したOS送信まで。
    private val ages = ArrayDeque<Pair<Double, Double>>()
    private val captures = ArrayDeque<Double>()
    private val redSends = ArrayDeque<Double>()
    private val blueSends = ArrayDeque<Double>()
    private val eventQueues = arrayOf(captures, redSends, blueSends)
    private var lastCaptureFrame = -1L
    private var capturedTotal = 0L
    private var analysedTotal = 0L
    private var udpDropped = 0L
    @Synchronized fun reset(at: Double) {
        started = at; samples.clear(); ages.clear()
        captures.clear(); redSends.clear(); blueSends.clear()
        lastCaptureFrame = -1; capturedTotal = 0; analysedTotal = 0; udpDropped = 0
    }
    @Synchronized fun captured(at: Double, frameNumber: Long) {
        if (frameNumber <= lastCaptureFrame) return
        lastCaptureFrame = frameNumber
        capturedTotal++
        event(captures, at)
    }
    @Synchronized fun datagramSent(at: Double, red: Boolean) {
        event(if (red) redSends else blueSends, at)
    }
    // 空/held は送信対象が無いだけ。freshを含むフレームが全く送れなかったときだけ破棄。
    @Synchronized fun droppedFrame(hasPackets: Boolean) { if (hasPackets) udpDropped++ }
    private fun event(queue: ArrayDeque<Double>, at: Double) {
        queue.addLast(at)
        while (queue.isNotEmpty() && queue.first() <= at - 5.0) queue.removeFirst()
        while (queue.size > 1200) queue.removeFirst()
    }
    @Synchronized fun processed(at: Double, milliseconds: Double) {
        if (!milliseconds.isFinite() || milliseconds < 0) return
        analysedTotal++
        samples.addLast(at to milliseconds)
        prune(at)
        while (samples.size > 1200) samples.removeFirst()
    }
    @Synchronized fun sent(at: Double, capturedNs: Long?, completedNs: Long) {
        if (capturedNs == null || capturedNs <= 0 || completedNs < capturedNs) return
        val milliseconds = (completedNs - capturedNs) / 1e6
        if (milliseconds !in 0.0..2000.0) return
        ages.addLast(at to milliseconds)
        prune(at)
        while (ages.size > 1200) ages.removeFirst()
    }
    private fun prune(at: Double) {
        while (samples.isNotEmpty() && samples.first().first <= at - 5.0) samples.removeFirst()
        while (ages.isNotEmpty() && ages.first().first <= at - 5.0) ages.removeFirst()
        for (queue in eventQueues)
            while (queue.isNotEmpty() && queue.first() <= at - 5.0) queue.removeFirst()
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
        val sortedAges = ages.map { it.second }.sorted()
        val p95 = if (sortedAges.isEmpty()) null else sortedAges[ceil(sortedAges.size * 0.95).toInt() - 1]
        val window = minOf(5.0, elapsed)
        // callbackとanalyzerは非同期。差には配送中のフレームも含むので「推定」と明記する。
        return HealthRates(samples.size / window, median(samples.map { it.second }), elapsed >= 3.0,
            median(sortedAges), p95, sortedAges.size,
            if (lastCaptureFrame < 0) null else captures.size / window,
            redSends.size / window, blueSends.size / window,
            maxOf(0L, capturedTotal - analysedTotal), udpDropped)
    }
}
